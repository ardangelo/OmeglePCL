using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace Omegle {

	public enum EventType {
		Waiting, Connected, RecaptchaRequired, RecaptchaRejected, CommonLikes, Typing, Question,
		StoppedTyping, GotMessage, StrangerDisconnected, StatusInfo, IdentDigests, SpyTyping, 
		SpyMessage, SpyDisconnected, Error, Unhandled
	};

	public class Event {
		public EventType type;
		public List<String> parameters;

		public Event(EventType type, List<String> parameters) {
			this.type = type;
			this.parameters = parameters;
		}
	}

	public class ErrorEvent : Event { //because client doesn't catch the exception for some reason
		public Exception exception;

		public ErrorEvent(Exception exception) : base(EventType.Error, new List<String>()) {
			this.exception = exception;
		}
	}

	internal class EventHandlerThread {
		private Client instance;
		private string startUrl;
		private ManualResetEvent disconnectEvent, runningEvent;
		public Boolean isAlive;
		
		public EventHandlerThread(Client instance, string startUrl) {
			this.isAlive = false;
			this.instance = instance;
			this.startUrl = startUrl;
			disconnectEvent = new ManualResetEvent(false);
			runningEvent = new ManualResetEvent(false);
		}

		public void Pause() {
			runningEvent.Reset();
		}

		public void Unpause() {
			runningEvent.Set();
		}

		public async void Run() {
			isAlive = true;
			runningEvent.Set();

			//get client id and handle events from this.starturl response
			//string resp = await instance.makeRequest(instance.browser, startUrl);
			HttpContent content = new StringContent("");
			content.Headers.ContentLength = 0;

			HttpResponseMessage result;
			dynamic jsonResponse;
			string id;
			try {
				Debug.WriteLine("Making start request to " + startUrl);
				result = await instance.getBrowser().PostAsync(startUrl, content);
				Debug.WriteLine("Initial response: {0}", result.Content.ReadAsStringAsync().Result);
				jsonResponse = JObject.Parse(result.Content.ReadAsStringAsync().Result);
				id = jsonResponse.clientID.Value;
				instance.setClientId(id);
				Debug.WriteLine(String.Format("Client ID: {0}", id));
				instance.handleEvents(jsonResponse.events);
			}
			catch (Exception ex) {
				lock (instance.eventQueue) {
					instance.eventQueue.Enqueue(new ErrorEvent(ex));
					instance.eventInQueueSignal.Set();
				}
			}

			instance.connected = true;

			while (true) {
				runningEvent.WaitOne();
				try {
					Debug.WriteLine("Requesting events");
					instance.requestEvents();
				} catch (Exception ex) {
					lock (instance.eventQueue) {
						instance.eventQueue.Enqueue(new ErrorEvent(ex));
						instance.eventInQueueSignal.Set();
					}
				}
				if (disconnectEvent.WaitOne(0)) { //disconnectEvent is set
					isAlive = false;
					return;
				}
				//sleep
				await Task.Delay(TimeSpan.FromSeconds(instance.getEventDelay()));
			}
		}

		public void Stop() {
			disconnectEvent.Set();
		}
	}

	public class Client {
		#region class fields
		internal Boolean connected;
		private int eventDelay, rcs, firstevents;
		private string spid, randomId, lang, server, clientId;
		private Dictionary<string, EventType> eventTypesByName;
		private HttpClient browser;

		public static string recaptchaChallenge = "http://www.google.com/recaptcha/api/challenge?k={0}";
		public static string recaptchaImage = "http://www.google.com/recaptcha/api/image?c={0}";
		public static string challengeRegex = "challenge\\s*:\\s*'(.+)'";

		private static string baseUrl = "http://{0}";
		private static Dictionary<string, string> requestUrls = new Dictionary<string, string>() {
			{"status", "/status?nocache={0}&randid={1}"},
			{"start", "/start?rcs={0}&firstevents={1}&spid={2}&randid={3}&lang={4}"},
			{"recaptcha", "/recaptcha"},
			{"events", "/events"},
			{"typing", "/typing"},
			{"stoppedtyping", "/stoppedtyping"},
			{"disconnect", "/disconnect"},
			{"send", "/send"}
		};
		public const string defaultHost = "omegle.com";

		private static string[] serverList = new string[9];

		//normal chat
		private List<String> topics;

		//answer question
		private Boolean wantsSpy;
		private int m; //m for MYSTERY

		//ask question
		private string ask;
		private Boolean canSaveQuestion;
		
		public Queue<Event> eventQueue; //in implementation, waitOne unhandledEvent and then process states from queue
		public AutoResetEvent eventInQueueSignal;

		private EventHandlerThread eventThread;
		#endregion

		//constructor with values that seem to be constant from sniffed official requests
		public Client(List<String> topics, string lang, string host = defaultHost) : this(host, 1, 1, "", generateId(), topics, false, 0, null, false, lang, 3) { }

		public Client(Boolean wantsSpy, string lang, string host = defaultHost) : this(host, 1, 1, "", generateId(), new List<String>(), wantsSpy, 1, null, false, lang, 3) { }

		public Client(string ask, Boolean canSaveQuestion, string lang, string host = defaultHost) : this(host, 1, 1, "", generateId(), new List<String>(), false, 0, ask, canSaveQuestion, lang, 3) { }
		
		//full constructor
		private Client(string host, int rcs, int firstevents, string spid, string randomId, List<String> topics, 
			Boolean wantsSpy, int m, string ask, Boolean canSaveQuestion, string lang, int eventDelay) {
			
			Random rnd = new Random();
			
			eventTypesByName = new Dictionary<string, EventType>() {
				{"waiting", EventType.Waiting},
				{"connected", EventType.Connected},
				{"recaptchaRequired", EventType.RecaptchaRequired},
				{"recaptchaRejected", EventType.RecaptchaRejected},
				{"commonLikes", EventType.CommonLikes},
				{"typing", EventType.Typing},
				{"stoppedTyping", EventType.StoppedTyping},
				{"gotMessage", EventType.GotMessage},
				{"strangerDisconnected", EventType.StrangerDisconnected},
				{"statusInfo", EventType.StatusInfo},
				{"identDigests", EventType.IdentDigests},
				{"question", EventType.Question},
				{"spyTyping", EventType.SpyTyping},
				{"spyMessage", EventType.SpyMessage},
				{"spyDisconnected", EventType.SpyDisconnected}
			};

			//populate serverList
			for (int i = 0; i < 9; i++) {
				serverList[i] = String.Format("front{0}.{1}", (i + 1).ToString(), host);
			}

				this.rcs = rcs;
			this.firstevents = firstevents;
			this.spid = spid;
			this.eventDelay = eventDelay;

			this.randomId = randomId;
			this.lang = lang;

			//normal chat
			this.topics = topics;
			
			//answer question
			this.m = m;
			this.wantsSpy = wantsSpy;
			
			//ask question
			this.ask = ask;
			this.canSaveQuestion = canSaveQuestion;

			this.server = Client.serverList[rnd.Next(Client.serverList.Length)];
			this.clientId = null;
			this.connected = false;

			this.browser = new HttpClient();
			//set request headers
			this.browser.DefaultRequestHeaders.Add("Origin", "http://www.omegle.com");
			//this.browser.DefaultRequestHeaders.Add("Connection", "keep-alive");
			this.browser.DefaultRequestHeaders.Add("Referer", "http://omegle.com/");
			this.browser.DefaultRequestHeaders.Add("Accept-Charset", "utf-8, iso-8859-1, utf-16, *;q=0.7");
			this.browser.DefaultRequestHeaders.Add("Accept", "application/json");
			this.browser.DefaultRequestHeaders.Add("Accept-Language", "en-US");
			this.browser.DefaultRequestHeaders.Add("Accept-Encoding", "gzip,deflate");
			//randomize user agent eventually
			this.browser.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Linux; U; Android 4.1.2; en-us; SCH-I535 Build/JZO54K) AppleWebKit/534.30 (KHTML, like Gecko) Version/4.0 Mobile Safari/534.30");

			this.eventQueue = new Queue<Event>();
			this.eventInQueueSignal = new AutoResetEvent(false);
		}

		#region accessors and mutators as required by protection
		public Boolean isConnected() { return connected; }
		internal void setClientId(string newId) { this.clientId = newId; }
		internal int getEventDelay() { return this.eventDelay; }
		internal HttpClient getBrowser() { return this.browser; }
		#endregion

		#region utilities
		internal static string generateId() {
			string randId = "";
			string selection = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
			Random rnd = new Random();

			for (int i = 0; i < 8; i++) {
				int randPos = rnd.Next(32);
				randId += selection.Substring(randPos,1);
			}

			return randId;
		}

		internal static string urlEncode(Dictionary<string, string> content) {
			Boolean isFirst = true;
			string construct = "";
			foreach (KeyValuePair<string, string> prop in content) {
				if (!isFirst) { construct += "&"; }
				if (prop.Value == null || prop.Value.Length == 0) {
					//construct += prop.Key + "=";
				} else {
					construct += prop.Key + "=" + Uri.EscapeDataString(prop.Value);
				}
				isFirst = false;
			}

			return construct;
		}

		internal static string createTopicString(List<string> topics) {
			string ts = "[";
			foreach (string topic in topics) {
				if (!(topic == null)) {
					ts += "\"" + Uri.EscapeUriString(topic) + "\"";
				}
			}
			ts += "]";

			return ts;
		}
		#endregion

		#region server -> client tasks
		internal void handleEvents(JArray jsonResponse) {
			JArray container;
			if (jsonResponse.Count > 1) {
				container = jsonResponse;
			} else {
				container = new JArray{ jsonResponse }; 
			}
			foreach (dynamic eventArray in container) {
				List<String> eventParams = new List<String>();
				
				for (int i = 1; i < eventArray.Count; i++) {
					if (eventArray[i] is JArray) { //only ever seen when list of common topics is passed as a parameter to commonlikes
						for (int j = 0; j < eventArray[i].Count; j++) {
							eventParams.Add(eventArray[i][j].Value);
						}
					} else {
						eventParams.Add(eventArray[i].Value);
					}
				}
				Event theEvent;
				string eventName = eventArray[0].Value;
				if (this.eventTypesByName.ContainsKey(eventName)) {
					theEvent = new Event(this.eventTypesByName[eventName], eventParams);
				} else {
					eventParams.Add(eventName);
					theEvent = new Event(EventType.Unhandled, eventParams);
				}
				//add event to queue
				Debug.WriteLine(eventName);
				lock (this.eventQueue) {
					Debug.WriteLine("Lock has been acquired");
					this.eventQueue.Enqueue(theEvent);
				}
				this.eventInQueueSignal.Set();
			}
		}
		#endregion

		#region client -> server tasks

		public static async Task<string> getNumberOfUsers(string host = defaultHost) {
			HttpClient browser = new HttpClient();
			HttpResponseMessage result;
			Random rnd = new Random();
			dynamic jsonResponse;
			string nu = "";

			HttpContent content = new StringContent("");
			content.Headers.ContentLength = 0;

			try {
				string url = String.Format(Client.baseUrl, String.Format("front{0}.{1}", new Random().Next(serverList.Length), host)) + 
					String.Format(Client.requestUrls["status"], "1", Client.generateId());
				Debug.WriteLine("Making user number request to " + url);
				result = await browser.PostAsync(url, content);
				jsonResponse = JObject.Parse(result.Content.ReadAsStringAsync().Result); 

				nu = (int.Parse(jsonResponse.count.Value.ToString()) / 1000).ToString() + ",000+";
			}
			catch (Exception ex) {
				throw ex;
			}

			return nu;
		}

		internal async Task<string> makeRequest(string requestUrl, Dictionary<string, string> passedContent) {
			if (passedContent == null) {
				return await makeRequest(requestUrl);
			}

			HttpResponseMessage result = new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError);

			Debug.WriteLine("Attempting request to {0}", String.Format(Client.baseUrl, this.server) + requestUrl);
			try {
				//temporary headers
				HttpContent content = new StringContent(urlEncode(passedContent), Encoding.UTF8, "application/x-www-form-urlencoded");
				//content-length
				content.Headers.TryAddWithoutValidation("Content-Length", urlEncode(passedContent).Length.ToString());
				content.Headers.TryAddWithoutValidation("Host", Client.defaultHost);
				//make request
				Task<HttpResponseMessage> resultTask = browser.PostAsync(
					String.Format(Client.baseUrl, this.server) + requestUrl, content);
				result = await resultTask;
			} catch (Exception ex) {
				lock (this.eventQueue) {
					this.eventQueue.Enqueue(new ErrorEvent(ex));
					this.eventInQueueSignal.Set();
					Debug.WriteLine("Put error event in queue");

					return "null";
				}
			}
			Debug.WriteLine("Response: {0}", result.Content.ReadAsStringAsync().Result);
				
			return result.Content.ReadAsStringAsync().Result;
		}
		internal async Task<string> makeRequest(string requestUrl) {
			return await makeRequest(requestUrl, new Dictionary<string, string>());
		}

		internal async void requestEvents() {
			//JObject content = new JObject(new JProperty("id", this.clientId));
			Dictionary<string, string> content = new Dictionary<string,string>{{"id", this.clientId}};
						
			//parse request
			string resp = "null";
			try {
				resp = await makeRequest(Client.requestUrls["events"], content);
				if (resp.Equals("null") || resp.Equals("")) { //omegle responded iwth "null", b/c httpclient has no keep-alive
					return;
				}
				dynamic jsonResponse = Newtonsoft.Json.Linq.JArray.Parse(resp);
				//hande events
				if (jsonResponse.HasValues) {
					handleEvents(jsonResponse);
				}
			} catch (Exception ex) {
				lock (this.eventQueue) {
					this.eventQueue.Enqueue(new ErrorEvent(ex));
					this.eventInQueueSignal.Set();
				}
			}
		}

		//can your programming language do this?
		public async void disconnect() { //the nice way
			this.Stop();
			
			Dictionary<string, string> content = new Dictionary<string, string> { { "id", this.clientId } };

			await makeRequest(Client.requestUrls["disconnect"], content);
		}
		
		public async void recapcha(string challenge, string response) {
			Dictionary<string, string> content = new Dictionary<string, string> { 
				{ "id", this.clientId },
				{ "challenge", challenge }, 
				{ "response", response } };

			string resp = await makeRequest(String.Format(Client.requestUrls["recaptcha"], this.server), content);
			//there was orignally a try-catch
		}

		public async void typing() {
			Dictionary<string, string> content = new Dictionary<string, string> { { "id", this.clientId } };

			await makeRequest(Client.requestUrls["typing"], content);
		}

		public async void stoppedTyping() {
			Dictionary<string, string> content = new Dictionary<string, string> { { "id", this.clientId } };

			await makeRequest(Client.requestUrls["stoppedtyping"], content);
		}

		public async void send(string message) {
			Dictionary<string, string> content = new Dictionary<string, string> { { "msg", message }, { "id", this.clientId } };

			if ((await makeRequest(Client.requestUrls["send"], content)).Equals("fail")) {
				throw new InvalidOperationException("Server rejected message");
			}
		}

		public async Task<JObject> status() {
			return JObject.Parse(await makeRequest(String.Format(Client.requestUrls["status"], this.server, 
				new Random().ToString(), this.randomId))); //nocache() has been experimentallty replaced with new Random()
		}

		public void Start() { //was of type EventHandlerThread, but that's not necessary anymore with the 
			string url = String.Format(Client.baseUrl, this.server) + //eventThread field
				String.Format(Client.requestUrls["start"], this.rcs, this.firstevents, this.spid, this.randomId, this.lang);
			if (this.topics.Count > 0) { //normal chat
				url += "&topics=" + Uri.EscapeUriString(createTopicString(topics));
			} else if (this.wantsSpy) { //answer question
				url += "&m=" + this.m + "&wantsspy=1";
			} else if (this.ask != null) { //ask question
				url += "&ask=" + Uri.EscapeUriString(this.ask) + "&cansavequestion=" ;
				if (this.canSaveQuestion) {
					url += "1";
				} else { url += "0"; }
			}

			EventHandlerThread thread = new EventHandlerThread(this, url);
			var runTask = Task.Factory.StartNew(() => {
				thread.Run();
			});

			runTask.Wait();

			this.eventThread = thread;

			//return thread;
		}

		public void Stop() { //Stop terminates the thread, disconnect sends disconnect event and then calls Stop
			connected = false;
			this.eventThread.Stop();
		}

		public void Pause() { //call pause before something that causes a navigation away event but not destruction
			this.eventThread.Pause();
		}

		public void Unpause() {
			this.eventThread.Unpause();
		}
		#endregion
	}
}
