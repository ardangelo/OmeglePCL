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
		Waiting, Connected, RecaptchaRequired, RecaptchaRejected, CommonLikes, Typing, 
		StoppedTyping, GotMessage, StrangerDisconnected, StatusInfo, IdentDigests, Unhandled
	};

	public class Event {
		public EventType type;
		public List<String> parameters;

		public Event(EventType type, List<String> parameters) {
			this.type = type;
			this.parameters = parameters;
		}
	}

	public class EventHandlerThread {
		private Client instance;
		private string startUrl;
		private ManualResetEvent disconnectEvent;
		public Boolean isAlive;
		
		public EventHandlerThread(Client instance, string startUrl) {
			this.isAlive = false;
			this.instance = instance;
			this.startUrl = startUrl;
			disconnectEvent = new ManualResetEvent(false);
		}

		public async void Run() {
			isAlive = true;

			//get client id and handle events from this.starturl response
			//string resp = await instance.makeRequest(instance.browser, startUrl);
			HttpContent content = new StringContent("");
			content.Headers.ContentLength = 0;

			HttpResponseMessage result;
			dynamic jsonResponse;
			string id;
			try {
				result = await instance.getBrowser().PostAsync(startUrl, content);
				jsonResponse = JObject.Parse(result.Content.ReadAsStringAsync().Result);
				id = jsonResponse.clientID.Value;
				instance.setClientId(id);
				Debug.WriteLine(String.Format("Client ID: {0}", id));
				instance.handleEvents(jsonResponse.events);
			} catch {
				Debug.WriteLine("Error in inital request");
				throw new HttpRequestException(String.Format("Error making request to {0}", startUrl));
			}

			while (true) {
				instance.requestEvents();
				if (disconnectEvent.WaitOne(0)) { //disconnectEvent is set
					instance.disconnect();
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
		private Boolean connected;
		private int eventDelay, rcs, firstevents;
		private string spid, randomId, lang, server, clientId;
		private String baseUrl;
		private string[] serverList;
		private List<String> topics;
		private Dictionary<string, string> requestUrls;
		private Dictionary<string, EventType> eventTypesByName;
		private HttpClient browser;
		
		public Queue<Event> eventQueue; //in implementation, waitOne unhandledEvent and then process states from queue
		public AutoResetEvent eventInQueueSignal;
		#endregion

		//constructor with values that seem to be constant from sniffed official requests
		public Client(List<String> topics, string lang) : this(1, 1, "", generateId(), topics, lang, 3) { }

		//full constructor
		public Client(int rcs, int firstevents, string spid, string randomId, List<String> topics, string lang, int eventDelay) {
			Random rnd = new Random();

			baseUrl = "http://{0}";
			requestUrls = new Dictionary<string, string>() {
				{"status", "/status?nocache={0}&randid={1}"},
				{"start", "/start?rcs={0}&firstevents={1}&spid={2}&randid={3}&lang={4}"},
				{"recaptcha", "/recaptcha"},
				{"events", "/events"},
				{"typing", "/typing"},
				{"stoppedtyping", "/stoppedtyping"},
				{"disconnect", "/disconnect"},
				{"send", "/send"}
			};

			serverList = new String[]{
				"front1.omegle.com", "front2.omegle.com", "front3.omegle.com",
				"front4.omegle.com", "front5.omegle.com", "front6.omegle.com",
				"front7.omegle.com", "front8.omegle.com", "front9.omegle.com"
			};

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
				{"identDigests", EventType.IdentDigests}
			};

			this.rcs = rcs;
			this.firstevents = firstevents;
			this.spid = spid;
			this.eventDelay = eventDelay;

			this.randomId = randomId;
			this.lang = lang;
			this.topics = topics;

			this.server = serverList[rnd.Next(serverList.Length)];
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
		public void setClientId(string newId) { this.clientId = newId; }
		public int getEventDelay() { return this.eventDelay; }
		public HttpClient getBrowser() { return this.browser;  }
		#endregion

		#region utilities
		public static string generateId() {
			string randId = "";
			string selection = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
			Random rnd = new Random();

			for (int i = 0; i < 8; i++) {
				int randPos = rnd.Next(32);
				randId += selection.Substring(randPos,1);
			}

			return randId;
		}

		public static string urlEncode(Dictionary<string,string> content) {
			Boolean isFirst = true;
			string construct = "";
			foreach (KeyValuePair<string, string> prop in content) {
				if (!isFirst) { construct += "&"; }
				construct += prop.Key + "=" + Uri.EscapeDataString(prop.Value);
				isFirst = false;
			}
			return construct;
		}
		#endregion

		#region server -> client tasks
		public void handleEvents(JArray jsonResponse) {

			JArray container;
			if (jsonResponse.Count > 1) {
				container = jsonResponse;
			} else {
				container = new JArray{ jsonResponse }; 
			}
			foreach (dynamic jsonEvent in container) {
				List<String> eventParams = new List<String>();
				for (int i = 1; i < jsonEvent.Count; i++) {
					eventParams.Add(jsonEvent[i].Value);
				}
				Event theEvent;
				string eventName = jsonEvent[0].Value;
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
		public async Task<string> makeRequest(string requestUrl, Dictionary<string, string> passedContent) {
			HttpResponseMessage result;

			Debug.WriteLine("Attempting request to {0}", String.Format(this.baseUrl, this.server) + requestUrl);
			try {
				//temporary headers
			HttpContent content = new StringContent(urlEncode(passedContent), Encoding.UTF8, "application/x-www-form-urlencoded");
				//content-length
				content.Headers.TryAddWithoutValidation("Content-Length", urlEncode(passedContent).Length.ToString());
				content.Headers.TryAddWithoutValidation("Host", this.server);
				//make request
				Task<HttpResponseMessage> resultTask = browser.PostAsync(
					String.Format(this.baseUrl, this.server) + requestUrl, content);
				result = await resultTask;
			} catch {
				throw new HttpRequestException(requestUrl);
			}
			Debug.WriteLine("Response: {0}", result.Content.ReadAsStringAsync().Result);
				
			return result.Content.ReadAsStringAsync().Result;
		}
		public async Task<string> makeRequest(string requestUrl) {
			return await makeRequest(requestUrl, new Dictionary<string, string>());
		}

		public async void requestEvents() {
			//JObject content = new JObject(new JProperty("id", this.clientId));
			Dictionary<string, string> content = new Dictionary<string,string>{{"id", this.clientId}};
			
			//parse request
			string resp = await makeRequest(this.requestUrls["events"], content);
			if (resp.Equals("null")) {
				return;
			}
			dynamic jsonResponse = Newtonsoft.Json.Linq.JArray.Parse(resp);

			//hande events
			if (jsonResponse.HasValues) {
				handleEvents(jsonResponse);
			}
		}

		//can your programming language do this?
		public async void disconnect() {
			this.connected = false;
			Dictionary<string, string> content = new Dictionary<string, string> { { "id", this.clientId } };

			await makeRequest(this.requestUrls["disconnect"], content);
		}
		
		public async void recapcha(string challenge, string response) {
			Dictionary<string, string> content = new Dictionary<string, string> { 
				{ "id", this.clientId },
				{ "challenge", challenge }, 
				{ "response", response } };

			string resp = await makeRequest(String.Format(this.requestUrls["recaptcha"], this.server), content);
			//there was orignally a try-catch
		}

		public async void typing() {
			//JObject content = new JObject(new JProperty("id", this.clientId));
			Dictionary<string, string> content = new Dictionary<string, string> { { "id", this.clientId } };

			await makeRequest(this.requestUrls["typing"], content);
		}

		public async void stoppedTyping() {
			//JObject content = new JObject(new JProperty("id", this.clientId));
			Dictionary<string, string> content = new Dictionary<string, string> { { "id", this.clientId } };

			await makeRequest(this.requestUrls["stoppedTyping"], content);
		}

		public async void send(string message) {
			Dictionary<string, string> content = new Dictionary<string, string> { { "id", this.clientId } };

			await makeRequest(this.requestUrls["send"], content);
		}

		public async Task<JObject> status() {
			return JObject.Parse(await makeRequest(String.Format(this.requestUrls["status"], this.server, 
				new Random().ToString(), this.randomId))); //nocache() has been experimentallty replaced with new Random()
		}

		public EventHandlerThread Start() {
			string url = String.Format(this.baseUrl, this.server) + 
				String.Format(this.requestUrls["start"], this.rcs, this.firstevents, this.spid, this.randomId, this.lang);
			if (this.topics.Count > 0) {
				JObject json = new JObject(new JProperty("topics", new JArray(from c in this.topics.ToArray<String>() select new JValue(c))));
				url += "&" + Uri.EscapeUriString(json.ToString());
			}

			EventHandlerThread thread = new EventHandlerThread(this, url);
			Task.Factory.StartNew(() => {
				thread.Run();
			});

			return thread;
		}
		#endregion
	}
}
