// The Autopilot window's model: the run history (the last 50 runs), each with its feed of status lines, engine
// findings, policy steps and result. It listens to the client whether or not the window is open, is kept across script
// reloads (entering play mode reloads them) in SessionState, and across editor restarts in UserSettings/AutopilotRuns/
// (one file per run, without its policy steps).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Autopilot.Playtest.Editor
{
    /// <summary>A line of a run's feed: a status line as translated, a finding, the user's words, the result, or one event
    /// of the agent service's activity feed (Kind "activity": ActivityId, ActivityKind, Detail, and, for a call that came
    /// back, its result folded in: Result, ResultDetail, ResultId). Every line knows the stage it belongs to (the Feed tab
    /// groups by it).</summary>
    public sealed class AutopilotFeedLine
    {
        public double At;     // seconds since the run started
        public string Text;
        public string Kind;   // status | attempt | now | engine | agent | stage | you (the user's words to the run) | memory | activity
        public string Severity = "";
        /// <summary>The stage the line belongs to (AutopilotText.Stages' index; -1: before any).</summary>
        public int Stage = -1;
        // An activity event (docs/protocol.md, The activity feed)
        public int ActivityId;              // the event's id in the run (0: not an activity event)
        public string ActivityKind = "";    // text, read, search, write, probe, episode, look, restart, steer, report, alert, refine
        public string Detail = "";          // what opens under the line ("" for none): the code tried, the path, the whole message
        public string Result = "";          // the words of the event that ended this one (a call's result), "" until it came
        public string ResultDetail = "";    // its detail (what came back)
        public int ResultId;                // that event's id (0: none yet)
        /// <summary>The event's own clock: the frame's `t`, seconds since the run started as the agent service counts (-1: not
        /// an activity event, or a run kept before this was). At is the window's clock when the event arrived, the one every
        /// other line shows, so the feed's times never run backwards; this is kept for ordering and replay.</summary>
        public double AgentT = -1;
        /// <summary>A stage heading's count of lines (Kind "heading": the Feed tab's own item, never a run's line, never kept).</summary>
        [NonSerialized] public int HeadingCount;

        public bool IsActivity => Kind == "activity";
        public bool IsHeading => Kind == "heading";

        /// <summary>The icon before an activity line's words (AutopilotText.ActivityIcon: robot, magnifier, binoculars...).</summary>
        public string Icon => AutopilotText.ActivityIcon(ActivityKind);

        /// <summary>What the icon stands for: the line's tooltip.</summary>
        public string Title => AutopilotText.ActivityTitle(ActivityKind);

        /// <summary>The letter shown in the icon's place when none can be drawn.</summary>
        public string Glyph => AutopilotText.ActivityGlyph(ActivityKind);

        /// <summary>The fold's word for the detail ("the code", "what was written"...; "" when it has none to fold).</summary>
        public string DetailLabel => Folds(Detail) ? AutopilotText.DetailLabel(ActivityKind) : "";

        /// <summary>The fold's word for the result's detail ("the output", "the summary"...; "" when none).</summary>
        public string ResultLabel => Folds(ResultDetail) ? AutopilotText.ResultLabel(ActivityKind) : "";

        /// <summary>Whether a detail is better folded than shown: more than one line, or long.</summary>
        public static bool Folds(string detail) => !string.IsNullOrEmpty(detail) && (detail.Contains("\n") || detail.Length > 80);

        public Dictionary<string, object> Save()
        {
            var d = new Dictionary<string, object> { ["at"] = At, ["text"] = Text, ["kind"] = Kind, ["severity"] = Severity, ["stage"] = Stage };
            if (ActivityId > 0)
            {
                // (an activity event: its fields, so a reopened run shows the feed as it was, folds included)
                d["aid"] = ActivityId;
                d["akind"] = ActivityKind;
                d["agent_t"] = AgentT;
                if (Detail.Length > 0) d["detail"] = Detail;
                if (ResultId > 0)
                {
                    d["rid"] = ResultId;
                    d["result"] = Result;
                    if (ResultDetail.Length > 0) d["rdetail"] = ResultDetail;
                }
            }
            return d;
        }

        public static AutopilotFeedLine Load(Dictionary<string, object> l)
        {
            return new AutopilotFeedLine
            {
                At = Json.Num(l, "at", 0), Text = Json.Str(l, "text"), Kind = Json.Str(l, "kind"), Severity = Json.Str(l, "severity"),
                Stage = (int)Json.Num(l, "stage", -1),  // (absent in runs kept before the stage groups)
                ActivityId = (int)Json.Num(l, "aid", 0), ActivityKind = Json.Str(l, "akind"), Detail = Json.Str(l, "detail"),
                AgentT = Json.Num(l, "agent_t", -1),
                ResultId = (int)Json.Num(l, "rid", 0), Result = Json.Str(l, "result"), ResultDetail = Json.Str(l, "rdetail"),
            };
        }
    }

    public sealed class AutopilotStep
    {
        public Dictionary<string, object> Frame;
        public int Attempt;   // 1-based: a new attempt starts when the call number goes down
        public int Call;
        public double T;
        public double Wall;
        public double Ms;
        public string Tag;    // START | KEEP | SWITCH | HELD | DROPPED | ""
        public string Chosen;
        public string Current;
        public List<KeyValuePair<string, double>> Pick;  // most likely first
        /// <summary>The call failed (the model API's error, a timeout): its error, "" otherwise (the frame's `error`).</summary>
        public string Error = "";

        public string Label => $"H{Attempt} #{Call}";

        public static AutopilotStep From(Dictionary<string, object> frame, AutopilotStep previous)
        {
            var s = new AutopilotStep { Frame = frame };
            s.Error = Json.Str(frame, "error");
            s.Call = (int)Json.Num(frame, "call", Json.Num(frame, "step", 0));
            s.Attempt = previous == null ? 1 : s.Call <= previous.Call ? previous.Attempt + 1 : previous.Attempt;
            s.T = Json.Num(frame, "t", 0);
            s.Wall = Json.Num(frame, "wall", 0);
            s.Ms = Math.Round(Json.Num(Json.Obj(frame, "seconds"), "jev", 0) * 1000.0);
            var decided = Json.Obj(frame, "decided");
            var summary = Json.Str(decided, "summary");
            var colon = summary.IndexOf(':');
            var action = colon >= 0 ? summary.Substring(0, colon).Trim() : summary.Trim();
            s.Tag = TagOf(action);
            var chosen = Json.List(decided, "chosen");
            s.Chosen = chosen != null && chosen.Count > 0 ? chosen[0] as string ?? "" : colon >= 0 ? summary.Substring(colon + 1).Trim() : "";
            var state = State(frame);
            var now = Json.Obj(state, "now_doing");
            s.Current = now != null ? Json.Str(now, "option") : "";
            var pick = Json.Obj(Json.Obj(Json.Obj(frame, "output"), "answers"), "pick");
            s.Pick = new List<KeyValuePair<string, double>>();
            if (pick != null)
            {
                foreach (var pair in pick)
                    if (pair.Value is double p) s.Pick.Add(new KeyValuePair<string, double>(pair.Key, p));
                s.Pick.Sort((a, b) => b.Value.CompareTo(a.Value));
            }
            if (s.Error.Length > 0) s.Tag = "FAILED";
            return s;
        }

        public static string TagOf(string action)
        {
            switch (action)
            {
                case "started": return "START";
                case "kept": return "KEEP";
                case "switched": return "SWITCH";
                case "kept_running": return "HELD";
                case "dropped": return "DROPPED";
                default: return string.IsNullOrEmpty(action) ? "" : action.ToUpperInvariant();
            }
        }

        public static Dictionary<string, object> State(Dictionary<string, object> frame)
        {
            return Json.Obj(Json.Obj(frame, "input"), "state");
        }

        public double P(string option)
        {
            foreach (var pair in Pick)
                if (pair.Key == option) return pair.Value;
            return 0;
        }

        /// <summary>What the step's current action is shown as: the option running, else the plan step's text.</summary>
        public string CurrentShown => !string.IsNullOrEmpty(Current) ? Current : Json.Str(Frame, "plan_step");

        /// <summary>The frame without our instructions (input.questions.*.instructions), for Raw JSON and Copy.</summary>
        public Dictionary<string, object> Redacted()
        {
            var copy = Json.Parse(Json.Write(Frame)) as Dictionary<string, object> ?? new Dictionary<string, object>();
            var questions = Json.Obj(Json.Obj(copy, "input"), "questions");
            if (questions != null)
                foreach (var q in questions.Values)
                    if (q is Dictionary<string, object> d)
                        d.Remove("instructions");
            return copy;
        }
    }

    public sealed class AutopilotRun
    {
        public string Id;
        public string Kind = "run";   // verify | goal | explore | run (started elsewhere, or not read yet: run_kind says it)
        public string Prompt = "";
        public Dictionary<string, object> RunFrame;
        public double Started;
        public double Ended;
        public Dictionary<string, object> Result;
        public string ReportDir = "";
        public int Stage = -1;
        public double[] StageSeconds = new double[AutopilotText.Stages.Length];
        public double StageSince;
        public int Attempt;
        public int AttemptLines;
        public string Now = "";
        public int NowAttempt;
        public readonly List<AutopilotFeedLine> Feed = new List<AutopilotFeedLine>();
        public readonly List<Dictionary<string, object>> Findings = new List<Dictionary<string, object>>();
        public readonly HashSet<string> NotABug = new HashSet<string>();
        public readonly List<AutopilotStep> Steps = new List<AutopilotStep>();
        public readonly Dictionary<string, int> Colors = new Dictionary<string, int>();
        /// <summary>Every policy step this run sent (the oldest are let go past MaxStepsKept).</summary>
        [NonSerialized] public int StepsSeen;
        public int Version;
        /// <summary>The run's token counts, as the last `usage` message said ({"final", "models"}); null when none came.</summary>
        public Dictionary<string, object> Usage;
        /// <summary>{"usd", "billed", "reason"?} as the `bill` message said (runs through Aura; null otherwise).</summary>
        public Dictionary<string, object> Bill;
        /// <summary>Stop was pressed: stopping until it ends (not kept across reloads: a stop asked survives as the
        /// agent service's own).</summary>
        [NonSerialized] public bool Stopping;
        public bool LostConnection;  // ended (NO RESULT) only because the connection closed: the run may still go on
        public bool EditorClosed;    // going when the editor closed (NO RESULT): its result never came here
        /// <summary>The run's recording (an MP4): the copy in its run folder once made, else where the agent service
        /// wrote it; "" when there is none.</summary>
        public string Recording = "";
        /// <summary>How long the recording is, as the result said (0: not said).</summary>
        public double RecordingSeconds;
        /// <summary>The recording's frames (the folder of JPEGs the agent service wrote, or their copy in the run's
        /// folder) and how many a second: what a project without the Video module plays, as a flipbook.</summary>
        public string RecordingFrames = "";
        public double RecordingFps;
        /// <summary>The report's line saying why the run has no recording, decided when its result came (see
        /// NoRecordingLine); "" when it has one, when the result didn't say, and in runs kept before this line.</summary>
        public string NoRecording = "";
        /// <summary>Who started the run when the window didn't (its `run_started` message's `by`: claude-code, codex,
        /// ...); "" for a run the window started.</summary>
        public string By = "";

        /// <summary>The run's origin line, under its request ("Started from Claude Code"); "" for a run the window
        /// started.</summary>
        public string Origin => AutopilotText.StartedFrom(By);

        /// <summary>The line a result with no recording shows where the recording would be: "No recording: " and the
        /// result's `no_recording` (why, in plain words); the older-service line when the result has neither `recording`
        /// nor `memory` (every agent service that records every run sends `memory`); else "" (nothing shown).</summary>
        public static string NoRecordingLine(Dictionary<string, object> result)
        {
            if (result == null || Json.Has(result, "recording")) return "";
            var why = Json.Str(result, "no_recording").Trim();
            if (why.Length > 0) return AutopilotText.NoRecording + why;
            return Json.Has(result, "memory") ? "" : AutopilotText.NoRecordingOlderService;
        }

        /// <summary>What the user must know to trust the outcome or to get a better one, as the result's `alerts` said
        /// (the game's picture couldn't be looked at: docs/protocol.md, Runs); none in runs whose result had none. Kept
        /// with the result.</summary>
        public List<string> ResultAlerts => AlertsOf(Result);

        /// <summary>A result's `alerts`, each as the agent service words it: none when it has none.</summary>
        public static List<string> AlertsOf(Dictionary<string, object> result)
        {
            var list = result == null ? null : Json.List(result, "alerts");
            if (list == null) return new List<string>();
            return list.OfType<string>().Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
        }

        public bool Active => Ended <= 0;

        /// <summary>Whether the run can be steered now as far as its stage goes: only while it is going and at the
        /// Rollout (an episode is playing). The steer box shows, disabled, at every other stage.</summary>
        public static bool SteerableAt(AutopilotRun run) => run != null && run.Active && run.Stage == AutopilotText.StageRollout;

        public double Elapsed => (Active ? AutopilotText.UnixNow : Ended) - Started;

        /// <summary>What the run took: the result's own seconds when it has them, else the plugin's timing.</summary>
        public double Took => Result != null && Json.Has(Result, "seconds") ? Json.Num(Result, "seconds", Elapsed) : Elapsed;

        public int Attempts => Result != null && Json.Has(Result, "attempts") ? (int)Json.Num(Result, "attempts", AttemptLines) : AttemptLines;

        public int Alerts => Findings.Count(IsEngine);

        public static bool IsEngine(Dictionary<string, object> f)
        {
            var source = Json.Str(f, "source");
            return source == "engine" || (string.IsNullOrEmpty(source) && Json.Has(f, "detector"));
        }

        /// <summary>CONFIRMED / DENIED / INCONCLUSIVE / GOAL HIT / GOAL MISSED / EXPLORED / GAME TESTED / LIVE / STOPPED /
        /// NO RESULT.</summary>
        public string Verdict
        {
            get
            {
                if (Active) return "LIVE";
                if (Result == null) return LostConnection || EditorClosed ? "NO RESULT" : "STOPPED";
                var v = Json.Str(Result, "verdict");
                if (!string.IsNullOrEmpty(v)) return v.ToUpperInvariant();
                // (an exploration asked as a test in the user's own words: its result says "tested")
                if (Kind == "explore" || Json.Has(Result, "bugs")) return Json.Bool(Result, "tested", false) ? "GAME TESTED" : "EXPLORED";
                if (Json.Has(Result, "reached")) return Json.Bool(Result, "reached", false) ? "GOAL HIT" : "GOAL MISSED";
                return "DONE";
            }
        }

        /// <summary>lime | red | amber | info | muted | accent (a run going): the verdict's colour.</summary>
        public static string Tone(string verdict)
        {
            switch (verdict)
            {
                case "CONFIRMED":
                case "GOAL HIT":
                    return "lime";
                case "LIVE":
                    return "accent";
                case "DENIED":
                case "GOAL MISSED":
                    return "red";
                case "INCONCLUSIVE":
                    return "amber";
                case "EXPLORED":
                case "GAME TESTED":
                    return "info";
                default:
                    return "muted";
            }
        }

        public int ColorOf(string option)
        {
            if (string.IsNullOrEmpty(option)) return -1;
            if (!Colors.TryGetValue(option, out var i))
            {
                i = Colors.Count;
                Colors[option] = i;
            }
            return i;
        }

        public void AddStep(Dictionary<string, object> frame)
        {
            var step = AutopilotStep.From(frame, Steps.Count > 0 ? Steps[Steps.Count - 1] : null);
            // (colours by first appearance: the current action, the chosen one, then the picks, most likely first)
            ColorOf(step.Current);
            ColorOf(step.Chosen);
            foreach (var pair in step.Pick) ColorOf(pair.Key);
            Steps.Add(step);
        }

        public void EnterStage(int stage, double now)
        {
            if (Stage >= 0 && Stage < StageSeconds.Length) StageSeconds[Stage] += Math.Max(0, now - StageSince);
            Stage = stage;
            StageSince = now;
        }

        public double StageTime(int stage)
        {
            if (stage < 0 || stage >= StageSeconds.Length) return 0;
            var t = StageSeconds[stage];
            if (stage == Stage && Active) t += Math.Max(0, AutopilotText.UnixNow - StageSince);
            return t;
        }

        /// <summary>The highest activity event id received (0: none): what the next hello asks the feed from
        /// (`activity_since`). Kept with the run.</summary>
        public int LastActivityId;

        /// <summary>Events that came again (ids shown already: the feed replayed after a reconnect) since the last new one:
        /// counted, said in the log when the replay moves past them. Not kept.</summary>
        public int ReplaySkipped;

        /// <summary>A line added to the feed, under the run's stage unless it names its own; the oldest let go past
        /// AutopilotSession.MaxFeed.</summary>
        public void AddLine(AutopilotFeedLine line)
        {
            if (line.Stage < 0) line.Stage = Stage;
            Feed.Add(line);
            if (Feed.Count > AutopilotSession.MaxFeed) Feed.RemoveRange(0, Feed.Count - AutopilotSession.MaxFeed);
        }

        /// <summary>The line that is activity event id, or the call it ended (ResultId); null when the run has none.</summary>
        public AutopilotFeedLine FindActivity(int id)
        {
            if (id <= 0) return null;
            for (var i = Feed.Count - 1; i >= 0; --i)
                if (Feed[i].ActivityId == id || Feed[i].ResultId == id)
                    return Feed[i];
            return null;
        }

        /// <summary>One event of the activity feed (an `activity` message): a line of the feed, or, when it ends a call
        /// (`ends`), that call's result folded into its line; its stage moves the run's. An id seen already (the feed
        /// sent again after a reconnect) changes nothing. Whether anything changed.</summary>
        public bool OnActivity(Dictionary<string, object> m, double now)
        {
            var id = (int)Json.Num(m, "id", 0);
            if (id <= 0) return false;
            if (FindActivity(id) != null)
            {
                // The event came already: the feed replayed after a reconnect. Counted; the log says so once at the start and
                // again, with the count, when the replay moves past what is here (a reconnect can be checked from the log)
                if (ReplaySkipped++ == 0) Debug.Log($"[Autopilot] A replay of the activity feed is arriving: event {id} is shown already");
                return false;
            }
            if (ReplaySkipped > 0)
            {
                Debug.Log($"[Autopilot] The replay skipped {ReplaySkipped} events shown already (ids up to {LastActivityId}); new events from {id}");
                ReplaySkipped = 0;
            }
            var text = AutopilotText.Scrub(Json.Str(m, "text")).Trim();
            var kind = Json.Str(m, "kind");
            var detail = Json.Str(m, "detail");  // (null: "")
            // The line's time is the window's clock now (what every other line shows: one clock per feed); the frame's `t`,
            // the agent service's own count, is kept apart (AgentT). A replayed event gets the time it arrives too
            var agentT = Json.Has(m, "t") && m["t"] != null ? Json.Num(m, "t", 0) : -1.0;
            var ends = (int)Json.Num(m, "ends", 0);  // (null: 0, a call or a line of its own)
            LastActivityId = Math.Max(LastActivityId, id);
            var stage = AutopilotText.StageOf(Json.Str(m, "stage"));
            if (Active && stage >= 0 && stage != Stage) EnterStage(stage, now);  // (the frame's stage drives the track; status lines still do for an older agent service)
            if (ends > 0)
            {
                var call = FindActivity(ends);
                if (call != null)
                {
                    if (call.ResultId == 0)
                    {
                        // The call and what came back: one line (its words, then the result's, quieter; two folds)
                        call.Result = text;
                        call.ResultDetail = detail;
                        call.ResultId = id;
                    }
                    return true;
                }
            }
            AddLine(new AutopilotFeedLine
            {
                At = now - Started, AgentT = agentT, Text = text, Kind = "activity", Stage = stage >= 0 ? stage : Stage,
                ActivityId = id, ActivityKind = kind, Detail = detail,
            });
            return true;
        }

        // -- kept across script reloads -----------------------------------------------------------------------

        public Dictionary<string, object> Save(int maxSteps)
        {
            var steps = Steps.Count > maxSteps ? Steps.Skip(Steps.Count - maxSteps) : Steps;
            return new Dictionary<string, object>
            {
                ["id"] = Id, ["kind"] = Kind, ["prompt"] = Prompt, ["run"] = RunFrame, ["started"] = Started, ["ended"] = Ended,
                ["result"] = Result, ["report_dir"] = ReportDir, ["stage"] = Stage, ["lost_connection"] = LostConnection,
                ["editor_closed"] = EditorClosed,
                ["stage_seconds"] = StageSeconds.Select(s => (object)s).ToList(), ["stage_since"] = StageSince,
                ["attempt"] = Attempt, ["attempt_lines"] = AttemptLines, ["now"] = Now, ["now_attempt"] = NowAttempt,
                ["feed"] = Feed.Select(l => (object)l.Save()).ToList(),
                ["last_activity"] = LastActivityId,
                ["findings"] = Findings.Select(f => (object)f).ToList(),
                ["not_a_bug"] = NotABug.Select(s => (object)s).ToList(),
                ["steps"] = steps.Select(s => (object)s.Frame).ToList(),
                ["usage"] = Usage,
                ["bill"] = Bill,
                ["recording"] = Recording,
                ["recording_seconds"] = RecordingSeconds,
                ["recording_frames"] = RecordingFrames,
                ["recording_fps"] = RecordingFps,
                ["no_recording"] = NoRecording,
                ["by"] = By,
            };
        }

        public static AutopilotRun Load(Dictionary<string, object> o)
        {
            var r = new AutopilotRun
            {
                Id = Json.Str(o, "id"), Kind = Json.Str(o, "kind", "run"), Prompt = Json.Str(o, "prompt"), RunFrame = Json.Obj(o, "run"),
                Started = Json.Num(o, "started", 0), Ended = Json.Num(o, "ended", 0), Result = Json.Obj(o, "result"),
                ReportDir = Json.Str(o, "report_dir"), Stage = (int)Json.Num(o, "stage", -1), StageSince = Json.Num(o, "stage_since", 0),
                Attempt = (int)Json.Num(o, "attempt", 0), AttemptLines = (int)Json.Num(o, "attempt_lines", 0),
                Now = Json.Str(o, "now"), NowAttempt = (int)Json.Num(o, "now_attempt", 0),
                LostConnection = Json.Bool(o, "lost_connection", false),
                EditorClosed = Json.Bool(o, "editor_closed", false),
                Usage = Json.Obj(o, "usage"),  // (absent in runs kept before there was usage)
                Bill = Json.Obj(o, "bill"),  // (absent in runs kept before bills, and in runs not through Aura)
                Recording = Json.Str(o, "recording"), RecordingSeconds = Json.Num(o, "recording_seconds", 0),  // (absent before recordings)
                RecordingFrames = Json.Str(o, "recording_frames"), RecordingFps = Json.Num(o, "recording_fps", 0),
                NoRecording = Json.Str(o, "no_recording"),  // (absent in runs kept before the line: they show nothing)
                By = Json.Str(o, "by"),  // (absent in runs the window started)
            };
            var secs = Json.List(o, "stage_seconds");
            if (secs != null)
            {
                // (a run kept before the Process result stage: its Report was where that stage is now)
                var old = secs.Count == AutopilotText.Stages.Length - 1;
                if (old && r.Stage >= AutopilotText.StageResults) ++r.Stage;
                for (var i = 0; i < secs.Count && i < r.StageSeconds.Length; ++i)
                    r.StageSeconds[old && i >= AutopilotText.StageResults ? i + 1 : i] = secs[i] is double d ? d : 0;
            }
            foreach (var x in Json.List(o, "feed") ?? new List<object>())
                if (x is Dictionary<string, object> l)
                    r.Feed.Add(AutopilotFeedLine.Load(l));
            r.LastActivityId = (int)Json.Num(o, "last_activity", 0);  // (absent in runs kept before the activity feed)
            foreach (var x in Json.List(o, "findings") ?? new List<object>())
                if (x is Dictionary<string, object> f) r.Findings.Add(f);
            foreach (var x in Json.List(o, "not_a_bug") ?? new List<object>())
                if (x is string s) r.NotABug.Add(s);
            foreach (var x in Json.List(o, "steps") ?? new List<object>())
                if (x is Dictionary<string, object> f) r.AddStep(f);
            // Its recording's copy in the run's folder, when there is one: a reload may have come before the copy was kept
            try
            {
                var copied = string.IsNullOrEmpty(r.ReportDir) || string.IsNullOrEmpty(r.Recording) ? null : Path.Combine(r.ReportDir, "recording.mp4");
                if (copied != null && File.Exists(copied)) r.Recording = copied;
                var frames = string.IsNullOrEmpty(r.ReportDir) || string.IsNullOrEmpty(r.RecordingFrames) ? null : Path.Combine(r.ReportDir, "recording");
                if (frames != null && Directory.Exists(frames) && Directory.GetFiles(frames, "*.jpg").Length > 0) r.RecordingFrames = frames;
            }
            catch (Exception) { }  // (a path that can't be: as it was kept)
            return r;
        }
    }

    [InitializeOnLoad]
    public static class AutopilotSession
    {
        const string StateKey = "Autopilot.Session";
        /// <summary>The runs kept (in the window and on disk): the newest.</summary>
        public const int MaxRuns = 50;
        const double SaveEverySeconds = 5;
        const int MaxStepsKept = 4000;
        public const int MaxFeed = 2000;

        public static readonly List<AutopilotRun> Runs = new List<AutopilotRun>();  // oldest first
        public static int Version { get; private set; }
        public static event Action Changed;

        static PlaytestClient hooked;
        static double lastEvent;

        static AutopilotSession()
        {
            Restore();
            EditorHost.ClientCreated += Hook;
            if (EditorHost.Client != null) Hook(EditorHost.Client);
            if (!SessionState.GetBool(LoadedKey, false))
            {
                SessionState.SetBool(LoadedKey, true);
                LoadHistory();
            }
            AssemblyReloadEvents.beforeAssemblyReload += SaveHistory;
            AssemblyReloadEvents.beforeAssemblyReload += Persist;
            AssemblyReloadEvents.beforeAssemblyReload += () => AutopilotUsage.Flush();
            EditorApplication.quitting += SaveHistory;
            EditorApplication.quitting += () => AutopilotUsage.Flush();
            EditorApplication.update += Update;
        }

        public static AutopilotRun Current => Runs.Count > 0 && Runs[Runs.Count - 1].Active ? Runs[Runs.Count - 1] : null;

        public static AutopilotRun Latest => Runs.Count > 0 ? Runs[Runs.Count - 1] : null;

        public static AutopilotRun Find(string id)
        {
            return string.IsNullOrEmpty(id) ? null : Runs.FirstOrDefault(r => r.Id == id);
        }

        /// <summary>Autopilot removed from this computer without keeping its memories: the runs forgotten (also the
        /// copy kept across script reloads), and the Settings page's token counts with them.</summary>
        public static void Forget()
        {
            Runs.Clear();
            SessionState.EraseString(StateKey);
            saved.Clear();
            try
            {
                if (Directory.Exists(HistoryDir)) Directory.Delete(HistoryDir, true);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Autopilot] Couldn't delete the run history in {HistoryDir}: {e.Message}");
            }
            AutopilotUsage.Forget();
            Touch(null);
        }

        static void Hook(PlaytestClient client)
        {
            if (hooked == client) return;
            if (hooked != null)
            {
                hooked.OnStatus -= OnStatus;
                hooked.OnFinding -= OnFinding;
                hooked.OnResult -= OnResult;
                hooked.OnConnectionChanged -= OnConnection;
                hooked.OnPolicyStep -= OnPolicyStep;
                hooked.OnMemoryState -= OnMemoryState;
                hooked.OnMemoryCleared -= OnMemoryCleared;
                hooked.OnUsage -= OnUsage;
                hooked.OnBill -= OnBill;
                hooked.OnRunStarted -= OnRunStarted;
                hooked.OnRunKind -= OnRunKind;
                hooked.OnActivity -= OnActivity;
                hooked.ActivitySince = null;
            }
            hooked = client;
            if (client == null) return;  // (tests: let go of a test's client)
            client.OnActivity += OnActivity;
            client.ActivitySince = ActivitySince;  // (each hello asks for the feed from where the run's is)
            client.OnStatus += OnStatus;
            client.OnFinding += OnFinding;
            client.OnResult += OnResult;
            client.OnConnectionChanged += OnConnection;
            client.OnPolicyStep += OnPolicyStep;
            client.OnMemoryState += OnMemoryState;
            client.OnMemoryCleared += OnMemoryCleared;
            client.OnUsage += OnUsage;
            client.OnBill += OnBill;
            client.OnRunStarted += OnRunStarted;
            client.OnRunKind += OnRunKind;
            Touch(null);
        }

        static void Touch(AutopilotRun run)
        {
            if (run != null) ++run.Version;
            ++Version;
            lastEvent = AutopilotText.UnixNow;
            try
            {
                Changed?.Invoke();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Autopilot] The window failed to update: " + e.Message);
            }
        }

        // -- runs --------------------------------------------------------------------------------------------

        /// <summary>Send a run (the composer's: kind "auto" and its words; or one of a kind of its own, the first run's
        /// Explore, Run again: kind, claim/goal, minutes, brief; runs are always real time).</summary>
        public static bool Start(Dictionary<string, object> frame, string prompt)
        {
            var client = EditorHost.Client;
            var problem = StartProblem(client, Json.Str(frame, "cli_client"));
            if (problem != null)
            {
                AutopilotStatus.Say(problem);
                return false;
            }
            Begin(KindOf(frame), prompt, frame);
            client.StartRun(frame);
            return true;
        }

        internal static string StartProblem(PlaytestClient client, string cliClient)
        {
            if (client == null || !client.Connected) return AutopilotStatus.RunOffline;
            if (client.RunActive) return AutopilotStatus.RunBusy;
            if (string.IsNullOrEmpty(cliClient) || client.SupportsCliLaunch == true) return null;
            return client.SupportsCliLaunch == false ? AutopilotStatus.CliUpdate : AutopilotStatus.CliChecking;
        }

        /// <summary>Stop the run going: asked of the agent service, and the run shown as stopping (Stop reads
        /// "Stopping…", the steer box locked) until it ends.</summary>
        public static void Stop()
        {
            var client = EditorHost.Client;
            if (client == null) return;
            client.StopRun();
            // (else the project index goes on writing every scene until it ends, and the run's stop waits for it)
            CodexExport.Stop();
            var live = Current;
            if (live == null || live.Stopping) return;
            live.Stopping = true;
            live.AddLine(new AutopilotFeedLine { At = AutopilotText.UnixNow - live.Started, Text = StoppingLine, Kind = "status" });
            Touch(live);
        }

        public const string StoppingLine = "Stopping…";

        /// <summary>Whether the user can steer the run going now (the service said it takes steering).</summary>
        public static bool CanSteer
        {
            get
            {
                var client = EditorHost.Client;
                return client != null && client.CanSteer && Current != null;
            }
        }

        /// <summary>The user's words to the live run, sent and shown in its feed; false when it can't be steered now
        /// (not going, the agent service doesn't take steering, or the run isn't at the Rollout).</summary>
        public static bool SteerRun(string text)
        {
            var said = (text ?? "").Trim();
            var live = Current;
            var client = EditorHost.Client;
            if (said.Length == 0 || live == null || client == null || !client.CanSteer || !AutopilotRun.SteerableAt(live)) return false;
            client.SteerRun(said);
            live.AddLine(new AutopilotFeedLine { At = AutopilotText.UnixNow - live.Started, Text = "You: " + said, Kind = "you" });
            if (live.Feed.Count > MaxFeed) live.Feed.RemoveRange(0, live.Feed.Count - MaxFeed);
            Touch(live);
            return true;
        }

        static AutopilotRun Begin(string kind, string prompt, Dictionary<string, object> frame)
        {
            var cur = Current;
            if (cur != null) cur.Ended = AutopilotText.UnixNow;
            var now = AutopilotText.UnixNow;
            var run = new AutopilotRun
            {
                Id = DateTime.Now.ToString("yyyyMMdd-HHmmss"),
                Kind = kind,
                Prompt = prompt ?? "",
                RunFrame = frame,
                Started = now,
                StageSince = now,
            };
            while (Runs.Any(r => r.Id == run.Id)) run.Id += "b";
            Runs.Add(run);
            while (Runs.Count > MaxRuns) Runs.RemoveAt(0);
            return run;
        }

        /// <summary>A run the user's own Claude Code or Codex started (the client's `run_started`): shown as a run the
        /// window started (the window shows it live, Stop and steering reach it, its result goes in the run history),
        /// with where it came from under its request. Ignored while a run is going.</summary>
        static void OnRunStarted(Dictionary<string, object> frame)
        {
            try
            {
                if (Current != null || frame == null) return;
                var kind = KindOf(frame);
                var prompt = Json.Str(frame, kind == "verify" ? "claim" : kind == "goal" ? "goal" : kind == "explore" ? "brief" : "prompt");
                if (string.IsNullOrEmpty(prompt)) prompt = Json.Str(frame, "claim", Json.Str(frame, "goal", Json.Str(frame, "brief", Json.Str(frame, "prompt"))));
                // (an explore reads as the window's own do, its words: "Explore the market"; no brief: "Explore")
                else if (kind == "explore") prompt = "Explore " + prompt;
                var run = Begin(kind, prompt, frame);
                run.By = Json.Str(frame, "by");
                Touch(run);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Autopilot] Couldn't show the run started from your coding agent: " + e.Message);
            }
        }

        /// <summary>A run frame's kind as the window shows it: verify, goal, explore; "run" for one whose kind the agent
        /// service reads from its words (kind "auto", or none) until its run_kind says it.</summary>
        internal static string KindOf(Dictionary<string, object> frame)
        {
            var kind = Json.Str(frame, "kind", "run");
            return kind == "verify" || kind == "goal" || kind == "explore" ? kind : "run";
        }

        /// <summary>The kind the agent service read from the run's words (its run_kind, before the run plays): the run
        /// going is shown as that kind from now on. (Whether it was asked as a test comes again with its result.)</summary>
        static void OnRunKind(string kind, bool tested)
        {
            try
            {
                if (kind != "verify" && kind != "goal" && kind != "explore") return;
                var run = Target();
                if (run == null || run.Kind == kind) return;
                run.Kind = kind;
                Touch(run);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Autopilot] Couldn't show the run's kind: " + e.Message);
            }
        }

        /// <summary>The run events belong to: the current one, or one started elsewhere (the command line).</summary>
        static AutopilotRun Target()
        {
            var cur = Current;
            if (cur != null) return cur;
            var lost = ReopenLost();
            if (lost != null) return lost;
            var client = EditorHost.Client;
            if (client == null || !client.RunActive) return null;
            var run = client.CommandLineRun;
            var kind = run != null ? KindOf(run) : "run";
            var prompt = run != null ? Json.Str(run, "claim", Json.Str(run, "goal", Json.Str(run, "prompt"))) : "";
            return Begin(kind, prompt, run);
        }

        /// <summary>The latest run, open again when it ended only because the connection closed and the agent service,
        /// connected again, still sends its events (its status lines, its result): null otherwise.</summary>
        static AutopilotRun ReopenLost()
        {
            var last = Latest;
            if (last == null || last.Active || !last.LostConnection) return null;
            last.Ended = 0;
            last.LostConnection = false;
            last.AddLine(new AutopilotFeedLine { At = AutopilotText.UnixNow - last.Started, Text = "Connected again: the run goes on", Kind = "stage" });
            Touch(last);
            return last;
        }

        /// <summary>A verdict in sentence case for the feed's last line ("GOAL HIT": "Goal hit").</summary>
        internal static string VerdictWords(string verdict)
        {
            if (string.IsNullOrEmpty(verdict)) return "";
            return verdict.Substring(0, 1).ToUpperInvariant() + verdict.Substring(1).ToLowerInvariant();
        }

        /// <summary>A line the plugin itself says (about connecting, starting, stopping), not one from the agent
        /// service's run: it never opens a run.</summary>
        static bool IsPluginNotice(string line)
        {
            var problem = EditorHost.Client?.Problem;
            return line.StartsWith("Started ", StringComparison.Ordinal) || line == "Stopping…" || line == "Disconnected"
                   || line.StartsWith("Connection closed", StringComparison.Ordinal) || line.StartsWith("Connected to ", StringComparison.Ordinal)
                   || line.StartsWith("Not connected", StringComparison.Ordinal) || line.StartsWith("Can’t reach ", StringComparison.Ordinal)
                   || line.StartsWith("A run is going", StringComparison.Ordinal)
                   || line.StartsWith("Resetting memory", StringComparison.Ordinal)
                   || line.StartsWith("Memory reset", StringComparison.Ordinal)
                   || line.StartsWith("Memory not reset", StringComparison.Ordinal)
                   || (!string.IsNullOrEmpty(problem) && line == problem);
        }

        static void OnStatus(string raw)
        {
            try
            {
                if (raw == null || raw.StartsWith("Finding: ", StringComparison.Ordinal) || raw.StartsWith("Run finished", StringComparison.Ordinal))
                {
                    Touch(null);
                    return;
                }
                if (IsPluginNotice(raw.Trim()))
                {
                    var live = Current;  // (shown in the run going now; never one that ended or a new one)
                    if (live != null)
                    {
                        live.AddLine(new AutopilotFeedLine { At = AutopilotText.UnixNow - live.Started, Text = AutopilotText.Status(raw, out _, out _), Kind = "status" });
                        if (live.Feed.Count > MaxFeed) live.Feed.RemoveRange(0, live.Feed.Count - MaxFeed);
                    }
                    Touch(live);
                    return;
                }
                var run = Target();
                if (run == null)
                {
                    Touch(null);
                    return;
                }
                var text = AutopilotText.Status(raw, out var stage, out var attempt);
                var now = AutopilotText.UnixNow;
                var kind = "status";
                if (stage >= 0) run.EnterStage(stage, now);
                // (the rollout's last action is over; NOW keeps its harness through Process result)
                if (stage >= AutopilotText.StageResults) run.Now = "";
                if (attempt > 0)
                {
                    run.Attempt = attempt;
                    ++run.AttemptLines;
                    kind = "attempt";
                }
                else if (stage >= 0) kind = "stage";
                if (text.StartsWith("Now: ", StringComparison.Ordinal))
                {
                    run.Now = text.Substring(5);
                    run.NowAttempt = run.Attempt;
                    kind = "now";
                }
                run.AddLine(new AutopilotFeedLine { At = now - run.Started, Text = text, Kind = kind });
                if (run.Feed.Count > MaxFeed) run.Feed.RemoveRange(0, run.Feed.Count - MaxFeed);
                Touch(run);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Autopilot] Couldn't show a status line: " + e.Message);
            }
        }

        /// <summary>What the hello asks the activity feed from: the latest run's last event id while it goes on (or lost
        /// its connection under), else -1 (nothing asked: a finished run's feed is here already).</summary>
        internal static int ActivitySince()
        {
            var last = Latest;
            if (last != null && (last.Active || last.LostConnection)) return last.LastActivityId;
            return -1;
        }

        /// <summary>One event of the run's activity feed (an `activity` message): a line under its stage, a call's
        /// result folded into the call's line; nothing for an id seen already.</summary>
        static void OnActivity(Dictionary<string, object> m)
        {
            try
            {
                var run = Target();
                if (run == null || m == null) return;
                if (run.OnActivity(m, AutopilotText.UnixNow)) Touch(run);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Autopilot] Couldn't show an activity line: " + e.Message);
            }
        }

        static void OnFinding(Dictionary<string, object> f)
        {
            try
            {
                var run = Target() ?? Latest;
                if (run == null || f == null) return;
                var sig = Json.Str(f, "signature");
                var same = string.IsNullOrEmpty(sig) ? -1 : run.Findings.FindIndex(x => Json.Str(x, "signature") == sig);
                if (same >= 0) run.Findings[same] = f;  // the same finding again: its count went up
                else run.Findings.Add(f);
                if (same < 0 || run.Active)
                {
                    var count = (int)Json.Num(f, "count", 1);
                    run.AddLine(new AutopilotFeedLine
                    {
                        At = AutopilotText.UnixNow - run.Started,
                        Text = AutopilotText.Scrub(Json.Str(f, "summary")) + (count > 1 ? $" (×{count})" : ""),
                        Kind = AutopilotRun.IsEngine(f) ? "engine" : "agent",
                        Severity = Json.Str(f, "severity"),
                    });
                }
                Touch(run);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Autopilot] Couldn't show a finding: " + e.Message);
            }
        }

        static void OnResult(Dictionary<string, object> result, string dir)
        {
            try
            {
                var run = Current ?? ReopenLost();  // (the run whose connection dropped: its result came once connected again)
                if (run == null)
                {
                    var client = EditorHost.Client;
                    var cl = client?.CommandLineRun;
                    run = Begin(cl != null ? KindOf(cl) : Json.Has(result, "verdict") ? "verify" : "goal",
                        Json.Str(result, "claim", Json.Str(result, "goal")), cl);
                }
                var now = AutopilotText.UnixNow;
                run.EnterStage(AutopilotText.StageReport, now);
                run.Result = result ?? new Dictionary<string, object>();
                run.ReportDir = dir ?? "";
                run.Ended = now;
                if (string.IsNullOrEmpty(run.Prompt)) run.Prompt = Json.Str(result, "claim", Json.Str(result, "goal"));
                run.AddLine(new AutopilotFeedLine { At = now - run.Started, Text = "Result: " + VerdictWords(run.Verdict), Kind = "stage" });
                // The run changed what is remembered about the project: said last, as its own kind of line
                if (Json.Bool(Json.Obj(result, "memory"), "updated", false))
                    run.AddLine(new AutopilotFeedLine { At = now - run.Started, Text = AutopilotText.MemoryUpdated, Kind = "memory" });
                KeepRecording(run, Json.Obj(result, "recording"));
                run.NoRecording = AutopilotRun.NoRecordingLine(result);
                AutopilotUsage.Record(run);
                Debug.Log($"[Autopilot] Result: {run.Verdict} in {AutopilotText.Clock(run.Took)}, {run.Attempts} {(run.Attempts == 1 ? "harness" : "harnesses")}");
                Touch(run);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Autopilot] Couldn't show the result: " + e.Message);
            }
        }

        // -- tests: the client's events as it sends them --------------------------------------------------------

        internal static void TestStatus(string raw) => OnStatus(raw);

        /// <summary>The session listening to a test's client (null: to none; the client before goes back by passing it).</summary>
        internal static void TestHook(PlaytestClient client) => Hook(client);

        internal static void TestResult(Dictionary<string, object> result, string dir) => OnResult(result, dir);

        /// <summary>Apply what work off the main thread finished (as Update does).</summary>
        internal static void TestMainThread()
        {
            while (mainThread.TryDequeue(out var done)) done();
        }

        // -- the recording ---------------------------------------------------------------------------------------

        /// <summary>Work done off the main thread whose end is applied on it (in Update).</summary>
        static readonly ConcurrentQueue<Action> mainThread = new ConcurrentQueue<Action>();

        /// <summary>The result's recording ({"video", "seconds"}): shown from where the agent service wrote it, and copied
        /// into the run's folder (recording.mp4) off the main thread, so it outlives a Reset memory; once copied the run
        /// shows the copy. A copy that fails leaves the agent service's path.</summary>
        static void KeepRecording(AutopilotRun run, Dictionary<string, object> recording)
        {
            var video = Json.Str(recording, "video");
            run.RecordingFrames = Json.Str(recording, "frames");
            run.RecordingFps = Json.Num(recording, "frames_fps", 2);
            run.RecordingSeconds = Json.Num(recording, "seconds", 0);
#if !AUTOPILOT_VIDEO
            KeepFrames(run);  // (played here as a flipbook: kept with the run)
#endif
            if (string.IsNullOrEmpty(video)) return;
            run.Recording = video;
            var dir = run.ReportDir;
            if (string.IsNullOrEmpty(dir)) return;
            string target;
            try
            {
                target = Path.Combine(dir, "recording.mp4");
                if (string.Equals(Path.GetFullPath(video), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) return;
            }
            catch (Exception)
            {
                return;  // (a path that can't be: shown from where it is)
            }
            Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    // (to a .part first: a reload or close mid-copy never leaves a cut recording.mp4 to be found)
                    var part = target + ".part";
                    File.Copy(video, part, true);
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(part, target);
                    mainThread.Enqueue(() =>
                    {
                        if (run.Recording != video) return;
                        run.Recording = target;
                        Touch(run);
                    });
                }
                catch (Exception e)
                {
                    mainThread.Enqueue(() => Debug.LogWarning($"[Autopilot] Couldn't copy the run's recording to {target} (shown from {video}): {e.Message}"));
                }
            });
        }

        /// <summary>The recording's frames copied into the run's folder (recording/, off the main thread), so the
        /// flipbook outlives a Reset memory, as Godot's dock keeps them; once copied the run plays the copy.</summary>
        internal static void KeepFrames(AutopilotRun run)
        {
            var src = run.RecordingFrames;
            var dir = run.ReportDir;
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(dir)) return;
            string dst;
            try
            {
                if (!Directory.Exists(src)) return;
                dst = Path.Combine(dir, "recording");
                if (string.Equals(Path.GetFullPath(src).TrimEnd('\\', '/'), Path.GetFullPath(dst).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return;
            }
            catch (Exception)
            {
                return;
            }
            Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(dst);
                    var n = 0;
                    foreach (var file in Directory.GetFiles(src, "*.jpg"))
                    {
                        File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), true);
                        ++n;
                    }
                    if (n == 0) return;
                    mainThread.Enqueue(() =>
                    {
                        if (run.RecordingFrames != src) return;
                        run.RecordingFrames = dst;
                        Touch(run);
                    });
                }
                catch (Exception e)
                {
                    mainThread.Enqueue(() => Debug.LogWarning($"[Autopilot] Couldn't copy the run's recording frames to {dst} (played from {src}): {e.Message}"));
                }
            });
        }

        /// <summary>The connection closed under a live run (not the user's Disconnect): the run ends with no result,
        /// marked so that the agent service, once connected again, can still go on with it (see ReopenLost).</summary>
        static void OnConnection(bool connected)
        {
            var cur = Current;
            var client = EditorHost.Client;
            if (!connected && cur != null && client != null && client.WantConnected)
            {
                var now = AutopilotText.UnixNow;
                cur.EnterStage(cur.Stage, now);
                cur.Ended = now;
                cur.LostConnection = true;
                cur.AddLine(new AutopilotFeedLine { At = now - cur.Started, Text = "Connection closed: no result", Kind = "stage" });
                Touch(cur);
                return;
            }
            Touch(null);
        }

        static void OnMemoryState()
        {
            Touch(null);  // (the save data line shows it)
        }

        /// <summary>Reset memory done: the runs before leave the history (SetRunsAside).</summary>
        static void OnMemoryCleared(bool ok)
        {
            if (ok) SetRunsAside();
        }

        /// <summary>The run's token counts so far (each usage message replaces the one before): kept with the run and in
        /// the project's usage ledger, for the Settings page.</summary>
        static void OnUsage(Dictionary<string, object> message)
        {
            try
            {
                var run = Target();
                if (run == null || message == null) return;
                run.Usage = AutopilotUsage.Kept(message);
                AutopilotUsage.Record(run);
                Touch(run);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Autopilot] Couldn't keep the run's usage: " + e.Message);
            }
        }

        /// <summary>What Autopilot billed for the run through Aura that just finished: kept with the run and in the
        /// project's usage ledger, for the Settings page.</summary>
        static void OnBill(Dictionary<string, object> message)
        {
            try
            {
                if (message == null) return;
                // (before the run's result: the run going now; a bill that came after the result: the run just ended)
                var latest = Latest;
                var run = Current ?? (latest != null && latest.Bill == null && latest.Ended > 0 && AutopilotText.UnixNow - latest.Ended < 120 ? latest : Target());
                if (run == null) return;
                run.Bill = Billing.Kept(message);
                AutopilotUsage.Record(run);
                Touch(run);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Autopilot] Couldn't keep the run's bill: " + e.Message);
            }
        }

        static void OnPolicyStep(Dictionary<string, object> frame)
        {
            try
            {
                var run = Target();
                if (run == null || frame == null) return;
                run.AddStep(frame);
                ++run.StepsSeen;
                if (run.Steps.Count > MaxStepsKept) run.Steps.RemoveRange(0, run.Steps.Count - MaxStepsKept);
                Touch(run);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Autopilot] Couldn't show a policy step: " + e.Message);
            }
        }

        public static void MarkNotABug(AutopilotRun run, Dictionary<string, object> finding)
        {
            var sig = Json.Str(finding, "signature");
            if (string.IsNullOrEmpty(sig)) return;
            EditorHost.Client?.SendFeedback(sig, "not_a_bug");
            run.NotABug.Add(sig);
            Touch(run);
        }

        /// <summary>A run with no result, once the client says no run is going (stopped, disconnected).</summary>
        static void Update()
        {
            while (mainThread.TryDequeue(out var done)) done();
            AutopilotUsage.Flush(true);
            if (AutopilotText.UnixNow - lastHistorySave > SaveEverySeconds) SaveHistory();
            var cur = Current;
            if (cur == null) return;
            var client = EditorHost.Client;
            if (client == null) return;
            var over = !client.WantConnected || (client.Connected && !client.RunActive && AutopilotText.UnixNow - lastEvent > 3);
            if (!over) return;
            cur.EnterStage(cur.Stage, AutopilotText.UnixNow);
            cur.Ended = AutopilotText.UnixNow;
            cur.AddLine(new AutopilotFeedLine { At = cur.Ended - cur.Started, Text = "Stopped", Kind = "stage" });
            Touch(cur);
        }

        // -- kept across script reloads -----------------------------------------------------------------------

        static void Persist()
        {
            try
            {
                var budget = MaxStepsKept;
                var runs = new List<object>();
                for (var i = Runs.Count - 1; i >= 0; --i)
                {
                    runs.Insert(0, Runs[i].Save(Math.Max(0, budget)));
                    budget -= Runs[i].Steps.Count;
                }
                SessionState.SetString(StateKey, Json.Write(new Dictionary<string, object> { ["runs"] = runs }));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Autopilot] Couldn't keep the run history across the script reload: " + e.Message);
            }
        }

        static void Restore()
        {
            var text = SessionState.GetString(StateKey, "");
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                var o = Json.ParseObject(text);
                foreach (var x in Json.List(o, "runs") ?? new List<object>())
                    if (x is Dictionary<string, object> r) Runs.Add(AutopilotRun.Load(r));
                // (saved just before the reload: on disk as they are)
                foreach (var r in Runs)
                    if (!string.IsNullOrEmpty(r.Id)) saved[r.Id] = r.Version;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Autopilot] Couldn't restore the run history: " + e.Message);
            }
        }

        // -- kept across editor restarts ------------------------------------------------------------------------

        const string LoadedKey = "Autopilot.HistoryLoaded";

        /// <summary>Each run's Version when its file was last written or read (the runs whose file this editor knows).</summary>
        static readonly Dictionary<string, int> saved = new Dictionary<string, int>();
        static double lastHistorySave;

        /// <summary>Tests: the run history's folder instead of the project's (null: the project's).</summary>
        internal static string HistoryDirOverride;

        /// <summary>The run history's folder: UserSettings/AutopilotRuns/ (Unity's per-user folder, never committed).</summary>
        public static string HistoryDir => HistoryDirOverride ?? Path.Combine(Directory.GetParent(Application.dataPath).FullName, "UserSettings", "AutopilotRuns");

        static string FileOf(string id) => Path.Combine(HistoryDir, id + ".json");

        public const string EditorClosedLine = "Editor closed: no result";

        /// <summary>The runs kept on disk, read when the editor starts: the newest MaxRuns (older files deleted), oldest
        /// first, before the runs already here. A run going when the editor closed ends with no result.</summary>
        internal static void LoadHistory()
        {
            var dir = HistoryDir;
            var loaded = new List<AutopilotRun>();
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var path in Directory.GetFiles(dir, "*.json"))
                {
                    try
                    {
                        var o = Json.ParseObject(File.ReadAllText(path));
                        var run = o == null ? null : AutopilotRun.Load(o);
                        if (run == null || run.Started <= 0 || run.Id + ".json" != Path.GetFileName(path) || Find(run.Id) != null) continue;
                        if (run.Active || (run.Result == null && run.LostConnection))
                        {
                            // Going when the editor closed: its result never came here (and no run now reopens it)
                            if (run.Active) run.Ended = Math.Max(run.Started, Json.Num(o, "saved_at", run.Started));
                            run.LostConnection = false;
                            run.EditorClosed = true;
                            run.AddLine(new AutopilotFeedLine { At = run.Ended - run.Started, Text = EditorClosedLine, Kind = "stage" });
                            ++run.Version;  // (saved again with its NO RESULT)
                        }
                        loaded.Add(run);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[Autopilot] Couldn't read the run {path}: {e.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Autopilot] Couldn't read the run history in {dir}: {e.Message}");
            }
            loaded.Sort((a, b) => a.Started.CompareTo(b.Started));
            // Only the newest are kept
            while (loaded.Count > 0 && loaded.Count + Runs.Count > MaxRuns)
            {
                TryDelete(FileOf(loaded[0].Id));
                loaded.RemoveAt(0);
            }
            foreach (var r in loaded) saved[r.Id] = r.EditorClosed ? r.Version - 1 : r.Version;
            if (loaded.Count == 0) return;
            Runs.InsertRange(0, loaded);  // (a run that began before the history loaded stays newest)
            Touch(null);
        }

        /// <summary>Write the runs that changed since their file was (each written whole, then put in place: a close
        /// mid-write never leaves half a run), and delete the files of the runs no longer kept.</summary>
        internal static void SaveHistory()
        {
            lastHistorySave = AutopilotText.UnixNow;
            try
            {
                foreach (var gone in saved.Keys.Where(id => Find(id) == null).ToList())
                {
                    TryDelete(FileOf(gone));
                    saved.Remove(gone);
                }
                foreach (var run in Runs)
                {
                    if (string.IsNullOrEmpty(run.Id) || (saved.TryGetValue(run.Id, out var v) && v == run.Version)) continue;
                    var o = run.Save(0);  // (no policy steps: they would make each file many megabytes)
                    o["saved_at"] = AutopilotText.UnixNow;
                    Directory.CreateDirectory(HistoryDir);
                    var path = FileOf(run.Id);
                    var temp = path + ".tmp";
                    File.WriteAllText(temp, Json.Write(o));
                    if (File.Exists(path)) File.Replace(temp, path, null);
                    else File.Move(temp, path);
                    saved[run.Id] = run.Version;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Autopilot] Couldn't save the run history to {HistoryDir}: {e.Message}");
            }
        }

        /// <summary>Where Reset memory puts the files of the runs it takes off the history: HistoryDir/.reset-backup/&lt;time&gt;/
        /// (as the agent service keeps the memory it resets: autopilot/memory.py), never read again; Forget and the
        /// uninstall delete it with the rest of HistoryDir.</summary>
        public const string ResetBackup = ".reset-backup";

        /// <summary>The runs ended taken off the history (a run going stays), their files moved to a new folder in
        /// HistoryDir/ResetBackup named for now (-1, -2… after another this second). The folder (null when no file
        /// moved).</summary>
        internal static string SetRunsAside()
        {
            SaveHistory();  // (each as it is now)
            var ended = Runs.Where(r => !r.Active).ToList();
            if (ended.Count == 0) return null;
            string backup = null;
            try
            {
                foreach (var run in ended)
                {
                    if (string.IsNullOrEmpty(run.Id) || !File.Exists(FileOf(run.Id))) continue;
                    if (backup == null)
                    {
                        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                        backup = Path.Combine(HistoryDir, ResetBackup, stamp);
                        for (var n = 1; Directory.Exists(backup); ++n)  // (two resets in one second)
                            backup = Path.Combine(HistoryDir, ResetBackup, $"{stamp}-{n}");
                        Directory.CreateDirectory(backup);
                    }
                    File.Move(FileOf(run.Id), Path.Combine(backup, run.Id + ".json"));
                }
            }
            catch (Exception e)
            {
                // (a file not moved is listed again at the next start)
                Debug.LogWarning($"[Autopilot] Couldn't move the run history aside in {HistoryDir}: {e.Message}");
            }
            foreach (var run in ended)
            {
                Runs.Remove(run);
                if (!string.IsNullOrEmpty(run.Id)) saved.Remove(run.Id);  // (its file is not this editor's to delete now)
            }
            Touch(null);
            return backup;
        }

        static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception) { }  // (left: read again at the next start, and kept or deleted then)
        }

        // -- tests ----------------------------------------------------------------------------------------------

        /// <summary>Tests: forget which files this editor wrote (as a new editor would).</summary>
        internal static void TestForgetSaved() => saved.Clear();
    }
}
