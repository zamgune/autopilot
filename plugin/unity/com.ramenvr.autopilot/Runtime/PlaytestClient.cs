// The bridge to the brain (docs/protocol.md): one WebSocket out to the brain, JSON text frames. The socket lives on
// background threads; everything that touches the game runs on the main thread, in Tick() (every frame), where all
// requests that arrived since the last frame are handled, many per frame. Deferred answers (move, watch, key taps,
// codex, session changes) are Pending entries ticked each frame until they answer.
//
// Or a link in place of the WebSocket (ConnectLink): another editor plugin carries the same frames to the brain over a
// connection of its own (Aura's, Editor/HostLink.cs). Frames out go to its send on the main thread, in Tick; frames in
// come through LinkReceive from any thread, and are handled in Tick as the socket's are.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Autopilot.Playtest.Tests")]

namespace Autopilot.Playtest
{
    /// <summary>A request answered later: Tick(pending, now) returns true once it has answered.</summary>
    public sealed class Pending
    {
        public long Id;
        public string Op;
        public double Start;
        public double Seconds;
        public Func<Pending, double, bool> Tick;
    }

    /// <summary>What differs between the editor and a player build (the editor assembly provides its own).</summary>
    public interface IPlaytestHost
    {
        /// <summary>"editor" or "game".</summary>
        string HostName { get; }

        /// <summary>The project's folder (where Assets/ is), or "" when it isn't known (a player away from its project).</summary>
        string ProjectDir { get; }

        /// <summary>The project's productGUID (Player Settings), or "".</summary>
        string ProjectId { get; }

        bool SessionRunning { get; }

        bool WantsInspection { get; }

        /// <summary>session start / stop / restart for the editor (play mode); a player handles them itself (return false).</summary>
        bool Session(PlaytestClient client, long id, string action, Dictionary<string, object> args);

        /// <summary>Run the codex export (scenes, prefabs, data, input as text) into dir; done(exitCode, error) on the
        /// main thread. Returns false when this host can't export.</summary>
        bool Export(string dir, Action<int?, string> done);

        /// <summary>The run on the command line is over: leave with this code.</summary>
        void Exit(int code);

        /// <summary>The report folder for a run (when the command line gave none).</summary>
        string ReportDir();
    }

    public sealed class PlaytestClient
    {
        public const int Protocol = 1;
        public const string PluginVersion = "1.0.0";
        public const string DefaultUrl = "ws://127.0.0.1:8765/v1/bridge";
        const double MaxBackoff = 30.0;
        const int MaxLogEventsPerFrame = 50;

        public static PlaytestClient Current { get; private set; }

        public IPlaytestHost Host { get; }

        // What the UI shows
        public event Action<string> OnStatus;
        public event Action<Dictionary<string, object>> OnFinding;
        public event Action<Dictionary<string, object>, string> OnResult;
        public event Action<bool> OnConnectionChanged;
        public event Action<Dictionary<string, object>> OnPolicyStep;
        public event Action OnMemoryState;
        /// <summary>The answer to Reset memory (its `memory_cleared` message): whether the memory was reset (after the
        /// status line says so).</summary>
        public event Action<bool> OnMemoryCleared;
        /// <summary>The brain's token counts for the run going now (its `usage` message: each replaces the one before).</summary>
        public event Action<Dictionary<string, object>> OnUsage;
        /// <summary>What Autopilot billed for the run through Aura that just finished (its `bill` message, before the
        /// result).</summary>
        public event Action<Dictionary<string, object>> OnBill;
        /// <summary>A run the window didn't start began (its `run_started` message: the user's own Claude Code or Codex
        /// started it, docs/protocol.md, Runs): its run ({"kind", "claim"|"goal"|"brief", ..., "by"}; a kind "auto"
        /// has its words as "prompt", its kind said by a run_kind later). Not raised while a run is going.</summary>
        public event Action<Dictionary<string, object>> OnRunStarted;
        /// <summary>The kind of run the agent service read from the run's words (its `run_kind` message, before the run
        /// plays: "verify", "goal" or "explore"), and whether an exploration was asked as a test (GAME TESTED).</summary>
        public event Action<string, bool> OnRunKind;
        /// <summary>One event of the run's activity feed (its `activity` message: what the agent did or said, {id, t,
        /// stage, kind, text, detail, ends}; docs/protocol.md, The activity feed). The window lists them under their stages.</summary>
        public event Action<Dictionary<string, object>> OnActivity;
        /// <summary>Set by the editor: the activity id the window's current run holds (0: none yet), or -1 to ask for no
        /// replay. A hello that isn't a resume asks the brain for the feed's events after it (`activity_since`). Null (a
        /// player build): never asked.</summary>
        public Func<int> ActivitySince;

        public string Url { get; private set; } = DefaultUrl;
        /// <summary>Use with Aura: each attempt goes to the agent service the Aura app runs (Aura.ServiceUrl).</summary>
        public bool WithAura { get; private set; }
        // No address was set (the default): each attempt goes to the agent service that says where it runs, else the default
        bool autoUrl = true;
        /// <summary>Who carries this client's connection in place of its WebSocket (ConnectLink: "aura"), or null.
        /// Set too while the client waits for that plugin to link it again after a script reload.</summary>
        public string LinkedBy { get; private set; }
        public bool Linked => LinkedBy != null;
        public bool Connected { get; private set; }
        public bool WantConnected { get; private set; }
        public string BrainRole { get; private set; }
        public string SessionId { get; private set; }
        public bool RunActive { get; private set; }
        // The welcome's features had "steer": the brain takes the user's words to the run going now
        public bool BrainSteers { get; private set; }
        // Whether the user can steer the run going now
        public bool CanSteer => Connected && RunActive && BrainSteers;
        /// <summary>Whether a run is using the game now: one is going, or the brain asked the game something in the
        /// last <see cref="RunUseSeconds"/> (a run this side didn't hear start). Only then do the detectors and the log
        /// events work: when nothing would read what they find, the user's own play sessions don't pay for them.</summary>
        public bool RunUsingGame => Connected && (RunActive || (lastRequestAt.HasValue && Now - lastRequestAt.Value < RunUseSeconds));
        const double RunUseSeconds = 30.0;
        double? lastRequestAt;  // when the brain's last request came (none on this connection: null)
        bool detectorsOn;  // they worked on the last frame: they start afresh when on again
        // The welcome's features had "account": the brain says how it runs and signs in to Aura when asked
        // (docs/protocol.md, Account)
        public bool BrainHasAccount { get; private set; }
        /// <summary>Whether this connection's service can launch a signed-in CLI. Null until it has answered.</summary>
        public bool? SupportsCliLaunch { get; private set; }
        /// <summary>The brain's last `account` frame (null until one came on this client).</summary>
        public AccountInfo Account { get; private set; }
        /// <summary>The Aura sign-in this client asked the brain for.</summary>
        public readonly SignInState SignIn = new SignInState();
        /// <summary>Changes whenever Account or SignIn does (for the window to know when to build again).</summary>
        public int AccountVersion { get; private set; }
        public event Action OnAccount;
        // Counts the connections made (one more each time this client connects), and the one the sign-in going on was
        // asked on
        int connectionSerial, signInSerial;
        // What the brain remembers about this project (its memory_state on this connection; false until one came): the
        // earlier runs it keeps, whether it has notes and code of its own for the game. Nothing at all: the next run is
        // cold.
        public bool MemoryKnown { get; private set; }
        public int MemoryRuns { get; private set; }
        public bool MemoryNotes { get; private set; }
        public bool MemoryLib { get; private set; }
        public string Problem { get; private set; } = "";
        public int ProblemCode { get; private set; }
        public string LastStatus { get; private set; } = "";
        /// <summary>The last `usage` message from the brain (null until one came).</summary>
        public Dictionary<string, object> LastUsage { get; private set; }
        /// <summary>The last `bill` message from the brain (null until one came).</summary>
        public Dictionary<string, object> LastBill { get; private set; }

        // The run asked for on the command line (-Playtest=...), sent with the hello
        public Dictionary<string, object> CommandLineRun;
        public string CommandLineReportDir = "";
        public bool ExitWhenDone;
        // A command-line start (-PlaytestConnectTimeout, default 120 s; 0 for none): with no welcome from the brain in
        // this long, write result.json with the error and exit 1 instead of retrying for ever
        public double ConnectTimeout;
        public const double DefaultConnectTimeout = 120.0;
        // When this client first tried to connect (wall clock, kept across the editor's script reloads), and whether
        // the brain has welcomed it since
        double connectSince;
        bool welcomed;
        bool gaveUp;
        string lastFailure = "";
        bool sentRun;

        string explicitToken = "";
        double backoff = 1.0;
        double nextReconnect = double.MaxValue;
        int generation;
        ClientWebSocket socket;
        CancellationTokenSource cancel;
        // The link's two halves (null while there is none, or until it is given again after a script reload), whether
        // it is open, and the connection number it was opened as (frames that come in on it are that connection's)
        Action<string> linkSend;
        Action<int, string> linkClose;
        bool linkOpen;
        int linkGeneration;
        // Whether frames can go out now: a socket, or an open link
        bool HasWire => socket != null || linkOpen;
        readonly ConcurrentQueue<(int gen, string kind, string text)> incoming = new ConcurrentQueue<(int, string, string)>();
        readonly BlockingCollection<string> outgoing = new BlockingCollection<string>(new ConcurrentQueue<string>());
        readonly List<object> events = new List<object>();
        readonly List<Pending> pending = new List<Pending>();
        readonly List<string> heldResponses = new List<string>();
        double lastHeartbeat;
        string resumeSession;  // a session to resume after the editor reloaded its scripts
        string askedResume;    // the session the last hello asked to resume

        public readonly Detectors Detectors;

        public PlaytestClient(IPlaytestHost host)
        {
            Host = host;
            Detectors = new Detectors(this);
            Current = this;
            LogCapture.Install();
        }

        /// <summary>Seconds on a clock that only goes forward. Not one of Unity's: the editor starts those over with every
        /// Play, and a restart is a Stop and a Play, so a time kept in a static would stay in the future (with no script
        /// reload to clear it, a recording then sent nothing until the new clock caught up).</summary>
        public static double Now => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

        /// <summary>The game time of the played world, or null when no game is running.</summary>
        public object GameTime => Application.isPlaying ? (object)Json.Round(Time.timeAsDouble, 3) : null;

        // -- connecting ------------------------------------------------------------------------------------

        public void Connect(string url = null, string explicitTok = null, bool withAura = false)
        {
            // With Aura: the agent service the Aura app runs. Else the address given, and left at the default address
            // the agent service running on this machine, wherever it says it listens. Either is looked up again at
            // every attempt: one that restarts may have another port
            DropLink();  // (a connection of its own: no other plugin carries it any more)
            WithAura = withAura;
            autoUrl = !withAura && (string.IsNullOrEmpty(url) || url == DefaultUrl);
            Url = withAura ? Aura.ServiceUrl() ?? "" : autoUrl ? AutoUrl() : url;
            explicitToken = explicitTok ?? "";
            WantConnected = true;
            backoff = 1.0;
            Problem = "";
            ProblemCode = 0;
            if (connectSince <= 0) connectSince = WallNow;
            OpenSocket();
        }

        /// <summary>Connect again after the editor reloaded its scripts: the same brain session goes on. With
        /// <paramref name="linkedBy"/> (the connection was a link) the client waits for that plugin to link it again
        /// (ConnectLink), and resumes the session over the new link.</summary>
        public void Resume(string url, string explicitTok, string session, bool runActive, bool sentRunAlready,
            double connectTimeout = 0, double since = 0, bool welcomedAlready = false, bool withAura = false,
            string linkedBy = null)
        {
            resumeSession = session;
            RunActive = runActive;
            sentRun = sentRunAlready;
            ConnectTimeout = connectTimeout;
            connectSince = since;
            welcomed = welcomedAlready;
            if (string.IsNullOrEmpty(linkedBy))
            {
                Connect(url, explicitTok, withAura);
                return;
            }
            Url = url;
            explicitToken = explicitTok ?? "";
            LinkedBy = linkedBy;
            WantConnected = true;
            nextReconnect = double.MaxValue;  // (until the plugin links it again)
        }

        // -- a link: another plugin carries the connection -----------------------------------------------------

        /// <summary>
        /// Connect over a link in place of the WebSocket: <paramref name="by"/> ("aura") carries the frames to the brain
        /// over a connection of its own. <paramref name="send"/> gets every frame this client sends (JSON text, on the
        /// main thread), starting with its hello: each hello begins a new connection to the brain. <paramref
        /// name="close"/> (code, reason) says the connection is to end: 1012 for a script reload (the brain keeps the
        /// session, and the hello on the next link resumes it), 1000 otherwise. Frames from the brain come in through
        /// LinkReceive, the end of that plugin's connection through LinkClosed. A link given while the client waits for
        /// one after a script reload resumes the session it had.
        /// </summary>
        public void ConnectLink(string by, Action<string> send, Action<int, string> close)
        {
            if (string.IsNullOrEmpty(by)) throw new ArgumentException("a link needs the name of the plugin that carries it", nameof(by));
            var resuming = LinkedBy == by && linkSend == null && WantConnected;
            if (!resuming)
            {
                // (whatever connection there was ends: this plugin carries the next)
                CloseSocket(WebSocketCloseStatus.NormalClosure, "linked");
                SetConnected(false);
                WithAura = false;
                autoUrl = false;
                resumeSession = null;
            }
            LinkedBy = by;
            linkSend = send ?? throw new ArgumentNullException(nameof(send));
            linkClose = close;
            Url = "link:" + by;
            WantConnected = true;
            backoff = 1.0;
            Problem = "";
            ProblemCode = 0;
            if (connectSince <= 0) connectSince = WallNow;
            OpenSocket();
        }

        /// <summary>A frame from the brain, on the link (any thread). Dropped when the link isn't open.</summary>
        public void LinkReceive(string json)
        {
            if (Volatile.Read(ref linkOpen)) incoming.Enqueue((Volatile.Read(ref linkGeneration), "text", json));
        }

        /// <summary>The plugin's connection to the brain ended (the brain closed it, or it couldn't reach it), any thread.
        /// The client links again after its backoff, with a new hello, as it connects again after a closed socket.</summary>
        public void LinkClosed(string why)
        {
            if (Volatile.Read(ref linkOpen)) incoming.Enqueue((Volatile.Read(ref linkGeneration), "closed", why ?? "closed"));
        }

        void DropLink()
        {
            if (!Linked) return;
            CloseSocket(WebSocketCloseStatus.NormalClosure, "unlinked");
            LinkedBy = null;
            linkSend = null;
            linkClose = null;
        }

        void OpenLink()
        {
            CloseSocket(WebSocketCloseStatus.NormalClosure, "reconnect");
            var gen = ++generation;
            Volatile.Write(ref linkGeneration, gen);
            Volatile.Write(ref linkOpen, true);
            nextReconnect = double.MaxValue;  // until the link closes
            incoming.Enqueue((gen, "open", null));
        }

        // Frames out on the link: what was sent since the last tick (from the main thread, or a worker's
        // SendIfConnection), in order
        void PumpLink()
        {
            if (!linkOpen || linkSend == null) return;
            while (outgoing.TryTake(out var text))
            {
                try
                {
                    linkSend(text);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Autopilot] the link's send failed: {e.Message}");
                }
            }
        }

        static double WallNow => DateTime.UtcNow.Subtract(DateTime.UnixEpoch).TotalSeconds;

        /// <summary>-PlaytestConnectTimeout=seconds (default 120; 0 waits for ever).</summary>
        public static double CommandLineConnectTimeout()
        {
            var v = Arg("PlaytestConnectTimeout");
            return !string.IsNullOrEmpty(v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s >= 0
                ? s : DefaultConnectTimeout;
        }

        /// <summary>No welcome within ConnectTimeout: the run can't happen. Write result.json with the error, exit 1.</summary>
        void CheckConnectTimeout()
        {
            if (gaveUp || welcomed || ConnectTimeout <= 0 || connectSince <= 0 || WallNow - connectSince < ConnectTimeout) return;
            gaveUp = true;
            var why = ProblemCode != 0 && !string.IsNullOrEmpty(Problem) ? Problem
                : !string.IsNullOrEmpty(lastFailure) ? lastFailure : "no welcome from it";
            var error = $"Couldn’t connect to the playtest service at {Url} within {ConnectTimeout:0.#} s ({why})";
            WantConnected = false;
            CloseSocket(WebSocketCloseStatus.NormalClosure, "connect timeout");
            SetConnected(false);
            Status(error);
            WriteReport(new Dictionary<string, object> { ["error"] = error, ["exit_code"] = 1 }, null);
            Debug.Log("[Autopilot] Exiting with code 1");
            Host.Exit(1);
        }

        public void Disconnect()
        {
            WantConnected = false;
            RunActive = false;
            Problem = "";
            ProblemCode = 0;
            CloseSocket(WebSocketCloseStatus.NormalClosure, "disconnect");
            // (a link ends with it: the next connection is the editor's own, or a new link)
            LinkedBy = null;
            linkSend = null;
            linkClose = null;
            SetConnected(false);
            Hands.ReleaseAll();
            Media.Stop();
            Status("Disconnected");
        }

        /// <summary>The optional bearer token: the one given (the window's setting, -PlaytestToken), else the
        /// AUTOPILOT_PLAYTEST_TOKEN environment variable, else none. Read again at every connect.</summary>
        string PickToken()
        {
            if (!string.IsNullOrEmpty(explicitToken)) return explicitToken;
            var env = Environment.GetEnvironmentVariable("AUTOPILOT_PLAYTEST_TOKEN");
            return string.IsNullOrEmpty(env) ? "" : env.Trim();
        }

        static string AutoUrl() => Aura.AnnouncedUrl(Aura.OwnBrainFile) ?? DefaultUrl;

        void OpenSocket()
        {
            if (TestWire != null) return;  // (tests: no socket)
            if (Linked)
            {
                // (no socket of its own while another plugin carries the connection; none at all until it links again)
                if (linkSend != null) OpenLink();
                else nextReconnect = double.MaxValue;
                return;
            }
            if (WithAura)
            {
                Url = Aura.ServiceUrl() ?? "";
                if (Url == "")  // (Aura's agent service isn't running: try again soon, it may be starting)
                {
                    CloseSocket(WebSocketCloseStatus.NormalClosure, "reconnect");
                    Problem = Aura.IsInstalled()
                        ? "Aura isn’t running on this machine. Start Aura, and Autopilot connects to it by itself."
                        : "Aura isn’t installed on this machine. Get Aura, or turn off Use with Aura.";
                    ProblemCode = 0;
                    nextReconnect = Now + 2.0;
                    return;
                }
            }
            else if (autoUrl)
            {
                Url = AutoUrl();
            }
            CloseSocket(WebSocketCloseStatus.NormalClosure, "reconnect");
            var gen = ++generation;
            var ws = new ClientWebSocket();
            var token = PickToken();
            if (!string.IsNullOrEmpty(token))
                ws.Options.SetRequestHeader("Authorization", "Bearer " + token);
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            socket = ws;
            cancel = new CancellationTokenSource();
            var ct = cancel.Token;
            nextReconnect = double.MaxValue;  // until this attempt fails
            var url = Url;
            Task.Run(() => RunSocket(ws, url, gen, ct));
        }

        async Task RunSocket(ClientWebSocket ws, string url, int gen, CancellationToken ct)
        {
            try
            {
                await ws.ConnectAsync(new Uri(url), ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                incoming.Enqueue((gen, "failed", e.InnerException?.Message ?? e.Message));
                return;
            }
            incoming.Enqueue((gen, "open", null));
            var sender = Task.Run(() => SendLoop(ws, gen, ct));
            var buffer = new byte[1 << 16];
            var message = new MemoryStream();
            string why = "closed";
            try
            {
                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        why = $"{(int?)result.CloseStatus} {result.CloseStatusDescription}";
                        break;
                    }
                    message.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage) continue;
                    var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                    message.SetLength(0);
                    if (result.MessageType == WebSocketMessageType.Text)
                        incoming.Enqueue((gen, "text", text));
                }
            }
            catch (Exception e)
            {
                why = e.InnerException?.Message ?? e.Message;
            }
            incoming.Enqueue((gen, "closed", why));
            try
            {
                await sender.ConfigureAwait(false);
            }
            catch
            {
                // (closing)
            }
        }

        async Task SendLoop(ClientWebSocket ws, int gen, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    if (!outgoing.TryTake(out var text, 200, ct)) continue;
                    if (gen != Volatile.Read(ref generation)) return;
                    var bytes = Encoding.UTF8.GetBytes(text);
                    await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                }
            }
            catch
            {
                // (the receive loop tells)
            }
        }

        void CloseSocket(WebSocketCloseStatus status, string why)
        {
            var ws = socket;
            var c = cancel;
            socket = null;
            cancel = null;
            if (linkOpen)
            {
                // (what is still to go goes first: the response to a session start before its reload's close, say)
                PumpLink();
                Volatile.Write(ref linkOpen, false);
                try
                {
                    linkClose?.Invoke((int)status, why);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Autopilot] the link's close failed: {e.Message}");
                }
            }
            ++generation;
            while (outgoing.TryTake(out _))
            {
            }
            if (ws == null) return;
            try
            {
                if (ws.State == WebSocketState.Open)
                    ws.CloseOutputAsync(status, why, CancellationToken.None).Wait(300);
            }
            catch
            {
                // (already gone)
            }
            try
            {
                c?.Cancel();
                ws.Dispose();
            }
            catch
            {
                // (already gone)
            }
        }

        /// <summary>Close for a script reload in the editor (code 1012, "service restart"): the brain keeps the session
        /// for the plugin to resume.</summary>
        public void CloseForReload()
        {
            CloseSocket((WebSocketCloseStatus)1012, "domain reload");
            Connected = false;
        }

        void ScheduleReconnect()
        {
            if (!WantConnected) return;
            nextReconnect = Now + backoff;
            backoff = Math.Min(backoff * 2, MaxBackoff);
        }

        void SetConnected(bool connected)
        {
            if (Connected == connected) return;
            Connected = connected;
            SupportsCliLaunch = null;
            if (connected) ++connectionSerial;
            else ForgetMemoryState();
            OnConnectionChanged?.Invoke(connected);
        }

        // (a memory_state is what the agent service on this connection said: every connection gets its own, and one
        // from before isn't true any more once the agent service is gone, removed from this computer say)
        void ForgetMemoryState()
        {
            MemoryKnown = false;
            MemoryRuns = 0;
            MemoryNotes = false;
            MemoryLib = false;
        }

        // -- sending ---------------------------------------------------------------------------------------

        public void Send(Dictionary<string, object> frame)
        {
            if (!Connected) return;
            if (TestWire != null)
            {
                TestWire(Json.Write(frame));
                return;
            }
            if (!HasWire) return;
            outgoing.Add(Json.Write(frame));
        }

        // -- tests: frames sent go to TestWire instead of a socket, and frames come in as from the brain -----------

        internal Action<string> TestWire;

        internal void TestConnected(bool connected) => SetConnected(connected);

        internal void TestReceive(string json) => HandleMessage(json);

        internal void TestRequestAge(double seconds) => lastRequestAt = Now - seconds;

        /// <summary>The connection's number now: a message made for one (on a worker thread) isn't sent on another.</summary>
        public int ConnectionGeneration => Volatile.Read(ref generation);

        /// <summary>Send a message already written as JSON, from any thread, only on the connection it was made for.</summary>
        public bool SendIfConnection(string text, int gen)
        {
            if (!Connected || !HasWire || gen != Volatile.Read(ref generation)) return false;
            outgoing.Add(text);
            return true;
        }

        /// <summary>A response already written as JSON, from any thread: sent now, or held until connected again (as
        /// Respond does).</summary>
        public void SendResponseText(string text)
        {
            if (Connected && HasWire)
            {
                outgoing.Add(text);
                return;
            }
            MainThread.Post(() => SendOrHoldText(text));
        }

        void SendHello()
        {
            var hello = new Dictionary<string, object>
            {
                ["type"] = "hello",
                ["protocol"] = Protocol,
                ["plugin_version"] = PluginVersion,
                ["engine"] = "unity",
                ["engine_version"] = Application.unityVersion,
                ["project"] = Application.productName,
                ["host"] = Host.HostName,
                ["input"] = Hands.Capabilities(),
                // what this plugin does beyond the protocol's first version: the brain falls back for one without them
                ["features"] = Media.Compiled ? new List<object> { "timed_hold", "pointer", "levels", "media" } : new List<object> { "timed_hold", "pointer", "levels" },
                // whether the user shares runs' data (preferences.json's share_data; -PlaytestShare overrides it)
                ["share"] = Preferences.Share(),
            };
            // whether runs use the research agent, only once chosen (preferences.json's research_agent; -PlaytestResearch
            // overrides it): left out, the agent service decides (off for the Aura app's, on otherwise)
            var research = Preferences.Research();
            if (research.HasValue) hello["research"] = research.Value;
            var id = Host.ProjectId;
            if (!string.IsNullOrEmpty(id)) hello["project_id"] = id;
            if (Host.WantsInspection) hello["inspect"] = true;
            if (!string.IsNullOrEmpty(resumeSession)) hello["resume"] = resumeSession;
            else
            {
                Media.Stop();  // (a new connection: no recording goes on from the one before)
                // The window holds a run going (or one whose connection dropped): ask for the activity events it hasn't
                // got (docs/protocol.md, The activity feed; a resume gets the whole feed again without asking)
                var since = ActivitySince != null ? ActivitySince() : -1;
                if (since >= 0)
                {
                    hello["activity_since"] = since;
                    Debug.Log($"[Autopilot] Asking for activity since {since}");
                }
            }
            askedResume = resumeSession;
            if (CommandLineRun != null && !sentRun)
            {
                hello["run"] = CommandLineRun;
                sentRun = true;
                RunActive = true;
            }
            Send(hello);
        }

        public void Respond(long id, object result)
        {
            var frame = new Dictionary<string, object>
            {
                ["type"] = "response",
                ["id"] = id,
                ["ok"] = true,
                ["result"] = result ?? new Dictionary<string, object>(),
                ["t"] = GameTime,
            };
            SendOrHold(frame);
        }

        public void RespondError(long id, string error, int status = 400)
        {
            var frame = new Dictionary<string, object>
            {
                ["type"] = "response",
                ["id"] = id,
                ["ok"] = false,
                ["status"] = status,
                ["error"] = error,
                ["t"] = GameTime,
            };
            SendOrHold(frame);
        }

        void SendOrHold(Dictionary<string, object> frame)
        {
            SendOrHoldText(Json.Write(frame));
        }

        void SendOrHoldText(string text)
        {
            if (Connected && HasWire)
            {
                outgoing.Add(text);
                return;
            }
            // (between a script reload and the resumed connection: answered once connected again)
            if (WantConnected && heldResponses.Count < 200) heldResponses.Add(text);
        }

        public void QueueEvent(Dictionary<string, object> e)
        {
            if (Connected) events.Add(e);
        }

        public void AddPending(Pending p)
        {
            p.Start = Now;
            // Its first tick right away: movement starts in the frame the request came
            if (p.Tick != null && p.Tick(p, p.Start)) return;
            pending.Add(p);
        }

        public void Status(string text)
        {
            LastStatus = text;
            Debug.Log("[Autopilot] " + text);
            OnStatus?.Invoke(text);
        }

        // -- runs (the Playtest window, the command line) ---------------------------------------------------

        public void StartRun(Dictionary<string, object> run)
        {
            if (!Connected)
            {
                Status("Not connected: connect first");
                return;
            }
            Send(new Dictionary<string, object> { ["type"] = "start", ["run"] = run });
            RunActive = true;
            Status($"Started {Json.Str(run, "kind")} run");
        }

        public void StopRun()
        {
            Send(new Dictionary<string, object> { ["type"] = "stop" });
            Status("Stopping…");
        }

        // What the user types to steer the run going now: it goes on from where the game is (see docs/protocol.md)
        public void SteerRun(string text)
        {
            var said = (text ?? "").Trim();
            if (!CanSteer || said.Length == 0) return;
            Send(new Dictionary<string, object> { ["type"] = "steer", ["text"] = said });
        }

        public void SendFeedback(string signature, string verdict, string note = "")
        {
            var m = new Dictionary<string, object> { ["type"] = "feedback", ["signature"] = signature, ["verdict"] = verdict };
            if (!string.IsNullOrEmpty(note)) m["note"] = note;
            Send(m);
        }

        public void ClearMemory()
        {
            if (!Connected)
            {
                Status("Not connected: connect first");
                return;
            }
            if (RunActive)
            {
                Status("A run is going: reset the memory once it is done");
                return;
            }
            Send(new Dictionary<string, object> { ["type"] = "clear_memory" });
            Status("Resetting memory…");
        }

        // -- the account (docs/protocol.md, Account) ------------------------------------------------------------

        void AccountChanged()
        {
            ++AccountVersion;
            OnAccount?.Invoke();
        }

        /// <summary>Whether the brain this client is connected to can sign in to Aura (its welcome said "account").</summary>
        public bool CanSignIn => Connected && BrainRole != null && BrainHasAccount;

        /// <summary>Ask the brain to sign in to Aura (it opens the browser on Aura's sign-in). Returns false when it
        /// can't be asked (not connected, or a brain without the account feature).</summary>
        public bool AskAuraSignIn()
        {
            if (!CanSignIn) return false;
            SignIn.Ask();
            signInSerial = connectionSerial;
            AccountChanged();
            Send(new Dictionary<string, object> { ["type"] = "aura_sign_in" });
            return true;
        }

        /// <summary>A sign-in asked on a connection that closed since: the agent service kept it going, but what it said
        /// may have gone with that connection. The account frame after the welcome says whether it finished while away
        /// (signed in, with the session's email: signed_in alone can mean only that the Aura app runs); else it's asked
        /// again here (the agent service says where it is, or starts one when it was started again).</summary>
        void ResumeSignIn()
        {
            if (!SignIn.Going || signInSerial == connectionSerial) return;
            signInSerial = connectionSerial;
            if (Account != null && Account.SignedIn && !string.IsNullOrEmpty(Account.Email))
                SignIn.SignedInAs(Account.Email);
            else if (CanSignIn)
                Send(new Dictionary<string, object> { ["type"] = "aura_sign_in" });
        }

        /// <summary>Stop waiting for the browser (the brain answers with a cancelled error, which shows nothing).</summary>
        public void CancelAuraSignIn()
        {
            var going = SignIn.Going;
            SignIn.Reset();
            AccountChanged();
            if (going && CanSignIn) Send(new Dictionary<string, object> { ["type"] = "aura_sign_in_cancel" });
        }

        /// <summary>Forget the Aura sign-in on this computer (every editor here); the brain answers with an account
        /// frame.</summary>
        public bool AuraSignOut()
        {
            if (!CanSignIn) return false;
            SignIn.Reset();
            AccountChanged();
            Send(new Dictionary<string, object> { ["type"] = "aura_sign_out" });
            return true;
        }

        /// <summary>Ask for the account frame again.</summary>
        public void AskAccount()
        {
            if (CanSignIn) Send(new Dictionary<string, object> { ["type"] = "account" });
        }

        /// <summary>Forget the sign-in's outcome shown (after the window moved on from it).</summary>
        public void ClearSignIn()
        {
            if (SignIn.State == SignInState.Idle) return;
            SignIn.Reset();
            AccountChanged();
        }

        // -- every frame -----------------------------------------------------------------------------------

        public void Tick()
        {
            MainThread.Run();
            CheckConnectTimeout();
            var now = Now;
            if (WantConnected && !Connected && !HasWire && now >= nextReconnect)
                OpenSocket();
            HandleReceived();
            Hands.Tick(Connected, RunActive);
            if (!Connected)
            {
                lastRequestAt = null;
                detectorsOn = false;
                LogCapture.Drain();
                events.Clear();
                PumpLink();
                return;
            }
            CheckSessionChange();
            for (var i = pending.Count - 1; i >= 0; --i)
            {
                bool done;
                try
                {
                    done = pending[i].Tick(pending[i], now);
                }
                catch (Exception e)
                {
                    RespondError(pending[i].Id, $"{pending[i].Op} failed: {e.Message}", 500);
                    done = true;
                }
                if (done) pending.RemoveAt(i);
            }
            Media.Tick(this);
            var lines = LogCapture.Drain();
            var detect = RunUsingGame;
            if (detect && !detectorsOn) Detectors.Reset();  // (afresh: what was seen before this run isn't this run's)
            detectorsOn = detect;
            var sent = 0;
            foreach (var line in lines)
            {
                if (!Application.isPlaying || !detect) break;
                if (++sent <= MaxLogEventsPerFrame)
                {
                    QueueEvent(new Dictionary<string, object>
                    {
                        ["kind"] = "log",
                        ["t"] = GameTime,
                        ["verbosity"] = line.Verbosity,
                        ["category"] = line.Category,
                        ["message"] = line.Message.Length > 2000 ? line.Message.Substring(0, 2000) : line.Message,
                    });
                }
            }
            if (lines.Count > 0 && Application.isPlaying && detect) Detectors.OnLogLines(lines);
            if (Application.isPlaying && detect) Detectors.Tick();
            if (now - lastHeartbeat >= 1.0)
            {
                lastHeartbeat = now;
                QueueEvent(new Dictionary<string, object>
                {
                    ["kind"] = "heartbeat",
                    ["t"] = GameTime,
                    ["fps"] = Math.Round(Time.unscaledDeltaTime > 0 ? 1.0 / Time.unscaledDeltaTime : 0.0),
                    ["paused"] = Ops.IsPaused,
                });
            }
            Hands.Flush();
            FlushEvents();
            PumpLink();
        }

        /// <summary>Handle what came from the wire since the last tick, in order (Tick; and HostLink.Close, so that
        /// frames the other plugin passed on before closing the link aren't dropped with it: a run's result, say).
        /// Main thread.</summary>
        public void HandleReceived()
        {
            while (incoming.TryDequeue(out var item))
            {
                if (item.gen != generation) continue;
                switch (item.kind)
                {
                    case "open":
                        SetConnected(true);
                        backoff = 1.0;
                        lastHeartbeat = 0;
                        SendHello();
                        if (heldResponses.Count > 0)
                        {
                            foreach (var r in heldResponses) outgoing.Add(r);
                            heldResponses.Clear();
                        }
                        break;
                    case "failed":
                        socket = null;
                        // (Windows' socket errors come padded with NULs and a line break)
                        lastFailure = (item.text ?? "").Replace("\0", "").Trim();
                        if (ProblemCode == 0)
                            Problem = $"Can’t reach the playtest service at {Url} ({lastFailure}). Trying again…";
                        if (LastStatus != Problem) Status(Problem);
                        ScheduleReconnect();
                        break;
                    case "closed":
                    {
                        var was = Connected;
                        socket = null;
                        Volatile.Write(ref linkOpen, false);  // (the plugin's connection ended: nothing to tell it)
                        SetConnected(false);
                        BrainRole = null;
                        BrainSteers = false;
                        BrainHasAccount = false;
                        // (a sign-in going on stays: the agent service keeps it, and it's asked again on the next
                        // connection, ResumeSignIn)
                        if (SignIn.Going) AccountChanged();
                        if (was) Status($"Connection closed ({item.text})");
                        Hands.ReleaseAll();
                        Media.Stop();  // (a recording doesn't outlive its connection)
                        ScheduleReconnect();
                        break;
                    }
                    case "text":
                        HandleMessage(item.text);
                        break;
                }
            }
        }

        void FlushEvents()
        {
            if (events.Count == 0) return;
            Send(new Dictionary<string, object> { ["type"] = "events", ["events"] = new List<object>(events), ["t"] = GameTime });
            events.Clear();
        }

        // Sessions: play mode starting or stopping, a scene loaded in its place
        bool lastRunning;
        int lastSceneHandle = -1;

        void CheckSessionChange()
        {
            var running = Host.SessionRunning;
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var handle = running && scene.IsValid() ? scene.GetHashCode() : -1;
            if (running == lastRunning && handle == lastSceneHandle) return;
            var first = lastSceneHandle == -1 && !lastRunning;
            lastRunning = running;
            lastSceneHandle = handle;
            Detectors.Reset();
            Paths.Prune();
            if (first && !running) return;
            QueueEvent(new Dictionary<string, object> { ["kind"] = "session", ["t"] = GameTime, ["running"] = running, ["scene"] = running ? scene.path : "" });
        }

        // -- frames from the brain -------------------------------------------------------------------------

        void HandleMessage(string text)
        {
            Dictionary<string, object> m;
            try
            {
                m = Json.ParseObject(text);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Autopilot] Unreadable message from the brain ({e.Message}): {text.Substring(0, Math.Min(200, text.Length))}");
                return;
            }
            if (m == null) return;
            var type = Json.Str(m, "type");
            switch (type)
            {
                case "request":
                {
                    lastRequestAt = Now;  // (a run is using the game: RunUsingGame)
                    var id = (long)Json.Num(m, "id", 0);
                    var op = Json.Str(m, "op");
                    var args = Json.Obj(m, "args") ?? new Dictionary<string, object>();
                    try
                    {
                        Ops.Handle(this, id, op, args);
                    }
                    catch (RcError e)
                    {
                        RespondError(id, e.Message, e.Status);
                    }
                    catch (Exception e)
                    {
                        RespondError(id, $"{op} failed: {e.GetType().Name}: {e.Message}", 500);
                    }
                    break;
                }
                case "welcome":
                    BrainRole = Json.Str(m, "role");
                    BrainSteers = false;
                    BrainHasAccount = false;
                    var features = Json.List(m, "features");
                    if (features != null)
                        foreach (var feature in features)
                        {
                            BrainSteers |= feature is string name && name == "steer";
                            BrainHasAccount |= feature is string acc && acc == "account";
                        }
                    SupportsCliLaunch = BrainHasAccount ? (bool?)null : false;
                    var session = Json.Str(m, "session");
                    // (a resume the brain didn't take is a new connection: no recording goes on)
                    if (!string.IsNullOrEmpty(askedResume) && session != askedResume) Media.Stop();
                    askedResume = null;
                    if (!string.IsNullOrEmpty(session)) SessionId = session;
                    resumeSession = null;
                    welcomed = true;
                    Problem = "";
                    ProblemCode = 0;
                    Status("Connected to the playtest service");
                    if (BrainRole == "cli") RunActive = true;
                    // (every connection: what this client knew of the account may be stale)
                    AskAccount();
                    break;
                case "status":
                    Status(Json.Str(m, "text"));
                    break;
                case "activity":
                    OnActivity?.Invoke(m);  // (one event of the run's activity feed: the window lists it under its stage)
                    break;
                case "finding":
                {
                    var f = Json.Obj(m, "finding");
                    if (f != null)
                    {
                        OnFinding?.Invoke(f);
                        Status("Finding: " + Json.Str(f, "summary"));
                    }
                    break;
                }
                case "result":
                    HandleResult(m);
                    break;
                case "run_started":
                {
                    // A run started from the user's own Claude Code or Codex: going, as one the window started (Stop
                    // and steering reach it), unless one is going already
                    if (RunActive) break;
                    RunActive = true;
                    OnRunStarted?.Invoke(Json.Obj(m, "run") ?? new Dictionary<string, object>());
                    break;
                }
                case "run_kind":
                    // What the decision model read the run's words as: the window shows the run as that kind from now on
                    OnRunKind?.Invoke(Json.Str(m, "kind"), Json.Bool(m, "tested", false));
                    break;
                case "memory_state":
                    // What the brain remembers about this project (docs/protocol.md): the save data the window shows
                    MemoryRuns = Math.Max(0, (int)Json.Num(m, "runs", 0));
                    MemoryNotes = Json.Bool(m, "notes", false);
                    MemoryLib = Json.Bool(m, "lib", false);
                    MemoryKnown = true;
                    OnMemoryState?.Invoke();
                    break;
                case "memory_cleared":
                    Status(Json.Bool(m, "ok", false)
                        ? "Memory reset successful"
                        : "Memory not reset: " + Json.Str(m, "message"));
                    OnMemoryCleared?.Invoke(Json.Bool(m, "ok", false));
                    break;
                case "error":
                {
                    // The brain refused (answering the hello) or gave up: show its message as it is
                    Problem = Json.Str(m, "message");
                    ProblemCode = (int)Json.Num(m, "code", 0);
                    Status(Problem);
                    WantConnected = false;  // a refusal: connecting again by itself wouldn't help
                    if (ExitWhenDone && CommandLineRun != null) Host.Exit(1);
                    break;
                }
                case "jev":
                    OnPolicyStep?.Invoke(m);
                    break;
                case "usage":
                    // The tokens the run's models used so far (docs/protocol.md, Usage): the window's Settings page
                    LastUsage = m;
                    OnUsage?.Invoke(m);
                    break;
                case "bill":
                    // What Autopilot billed for the run through Aura that just finished, before its result: the
                    // window's Settings page
                    LastBill = m;
                    OnBill?.Invoke(m);
                    break;
                case "account":
                    // How the brain runs: Aura (signed in or not), the user's keys, or nothing set up
                    Account = AccountInfo.From(m);
                    SupportsCliLaunch = m.TryGetValue("supports_cli_launch", out var launch) && launch is true;
                    ResumeSignIn();
                    AccountChanged();
                    break;
                case "aura_sign_in":
                    SignIn.Apply(m);
                    AccountChanged();
                    break;
                case "memory":
                case "memories":
                    break;  // (the inspector: not in this plugin yet)
            }
        }

        void HandleResult(Dictionary<string, object> m)
        {
            RunActive = false;
            var result = Json.Obj(m, "result") ?? new Dictionary<string, object>();
            var dir = WriteReport(result, Json.Obj(m, "files"));
            Status($"Run finished. Report: {dir}");
            OnResult?.Invoke(result, dir);
            if (!ExitWhenDone || CommandLineRun == null) return;
            int code;
            if (Json.Has(result, "exit_code")) code = (int)Json.Num(result, "exit_code", 1);
            else if (Json.Has(result, "verdict"))
            {
                var v = Json.Str(result, "verdict");
                code = v == "CONFIRMED" ? 0 : v == "DENIED" ? 2 : 3;
            }
            else code = Json.List(result, "bugs")?.Count > 0 ? 2 : 0;
            Debug.Log($"[Autopilot] Exiting with code {code}");
            Host.Exit(code);
        }

        /// <summary>Write result.json (and the brain's report files, each by its bare name) to the run's report folder;
        /// returns the folder.</summary>
        string WriteReport(Dictionary<string, object> result, Dictionary<string, object> files)
        {
            var dir = !string.IsNullOrEmpty(CommandLineReportDir) ? CommandLineReportDir : Host.ReportDir();
            try
            {
                dir = Path.GetFullPath(dir);
                Directory.CreateDirectory(dir);
                if (files != null)
                {
                    foreach (var pair in files)
                    {
                        var name = Path.GetFileName(pair.Key);  // never outside the report folder
                        if (string.IsNullOrEmpty(name) || !(pair.Value is string content)) continue;
                        File.WriteAllText(Path.Combine(dir, name), content, new UTF8Encoding(false));
                    }
                }
                File.WriteAllText(Path.Combine(dir, "result.json"), Json.Pretty(result), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Autopilot] Couldn't write the report to {dir}: {e.Message}");
            }
            return dir;
        }

        // -- the command line --------------------------------------------------------------------------------

        /// <summary>-Playtest=verify|goal|explore and its options (as the Unreal and Godot plugins take them). Returns whether
        /// a run (or a connection, -PlaytestUrl alone) was asked for.</summary>
        public static bool ReadCommandLine(out Dictionary<string, object> run, out string url, out string tok, out string reportDir, out bool stayOpen)
        {
            run = null;
            url = Arg("PlaytestUrl");
            tok = Arg("PlaytestToken");
            reportDir = Arg("PlaytestReportDir");
            stayOpen = Flag("PlaytestStayOpen");
            var kind = Arg("Playtest");
            if (string.IsNullOrEmpty(kind)) return !string.IsNullOrEmpty(url) && Flag("PlaytestConnect");
            run = new Dictionary<string, object> { ["kind"] = kind.ToLowerInvariant() };
            var claim = Arg("PlaytestClaim");
            if (!string.IsNullOrEmpty(claim)) run["claim"] = claim;
            var goal = Arg("PlaytestGoal");
            if (!string.IsNullOrEmpty(goal)) run["goal"] = goal;
            var brief = "";
            var briefFile = Arg("PlaytestBriefFile");
            if (!string.IsNullOrEmpty(briefFile))
            {
                try
                {
                    brief = File.ReadAllText(briefFile);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Autopilot] Couldn't read {briefFile}: {e.Message}");
                }
            }
            else brief = Arg("PlaytestBrief");
            if (!string.IsNullOrWhiteSpace(brief)) run["brief"] = brief.Trim();
            foreach (var (arg, key) in new[] { ("PlaytestMinutes", "minutes"), ("PlaytestTimeLimit", "time_limit_minutes"), ("PlaytestSteps", "steps"), ("PlaytestAttempts", "attempts") })
            {
                var v = Arg(arg);
                if (!string.IsNullOrEmpty(v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) run[key] = n;
            }
            // (-PlaytestTurnBased is still accepted and ignored: runs are always real time)
            return true;
        }

        /// <summary>-Name=value (or -Name value), quotes taken off.</summary>
        public static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            var prefix = "-" + name + "=";
            for (var i = 0; i < args.Length; ++i)
            {
                var a = args[i];
                if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return a.Substring(prefix.Length).Trim('"');
                if (string.Equals(a, "-" + name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                    return args[i + 1].Trim('"');
            }
            return "";
        }

        public static bool Flag(string name)
        {
            foreach (var a in Environment.GetCommandLineArgs())
                if (string.Equals(a, "-" + name, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>What the editor keeps across a script reload.</summary>
        public Dictionary<string, object> SaveState()
        {
            return new Dictionary<string, object>
            {
                ["url"] = Url,
                ["token"] = explicitToken,
                ["want"] = WantConnected,
                ["session"] = SessionId ?? "",
                ["run_active"] = RunActive,
                ["sent_run"] = sentRun,
                ["exit_when_done"] = ExitWhenDone,
                ["report_dir"] = CommandLineReportDir,
                ["run"] = CommandLineRun,
                ["connect_timeout"] = ConnectTimeout,
                ["connect_since"] = connectSince,
                ["welcomed"] = welcomed,
                ["with_aura"] = WithAura,
                ["linked_by"] = LinkedBy ?? "",
                ["record"] = Media.SaveState(),
            };
        }

        public void Shutdown()
        {
            CloseSocket(WebSocketCloseStatus.NormalClosure, "shutdown");
            Connected = false;
            WantConnected = false;
            if (Current == this) Current = null;
        }
    }
}
