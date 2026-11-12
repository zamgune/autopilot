// The composer's one box: its start frame (kind "auto": the agent service reads the kind from the words) and how the
// run is shown, the kind the agent service read (run_kind), a test's GAME TESTED; and what a run keeps at its end: the
// memory line in its feed, the status line that saves to memory (part of the report, never a step back), and its
// recording copied into its run folder; and steering, only at the Rollout (the rule, and the sending path refusing at
// every other stage).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Autopilot.Playtest.Editor;
using NUnit.Framework;
using UnityEngine;

namespace Autopilot.Playtest.Tests
{
    public class ComposerTests
    {
        [Test]
        public void TheStartFrameLeavesTheKindToTheAgentService()
        {
            // Every kind of words: one frame, kind "auto", the words as typed (trimmed), the Time limit
            foreach (var text in new[] { "  Verify that doors open ", "Reach the castle gate", "explore the game for bugs: farm area", "Can the player swim?",
                         "Lvl_HordeArea test this game and find all the bugs with animation" })
            {
                var frame = AutopilotView.ComposerFrame(text, 20, out var shown);
                CollectionAssert.AreEquivalent(new Dictionary<string, object> { ["kind"] = "auto", ["prompt"] = text.Trim(), ["time_limit_minutes"] = 20 }, frame, text);
                Assert.AreEqual(text.Trim(), shown, "the typed words, never rewritten");
                Assert.IsFalse(frame.ContainsKey("turn_based"), "runs are always real time");
            }
            // (kept to 1-600; none: the agent service's own)
            Assert.AreEqual(600, AutopilotView.ComposerFrame("Reach the gate", 9999, out _)["time_limit_minutes"]);
            Assert.IsFalse(AutopilotView.ComposerFrame("Reach the gate", 0, out _).ContainsKey("time_limit_minutes"));
        }

        [Test]
        public void CliRunsRequireThisConnectionsServiceSupport()
        {
            var previous = EditorHost.Client;
            var sent = new List<string>();
            var client = new PlaytestClient(EditorHost.Instance) { TestWire = sent.Add };
            var frame = AutopilotView.ExploreFrame(4, "Open the gate", "claude");
            const string welcome = "{\"type\":\"welcome\",\"features\":[\"account\"]}";
            try
            {
                EditorHost.TestClient(client);
                client.TestConnected(true);
                client.TestReceive(welcome);
                Assert.AreEqual(AutopilotStatus.CliChecking, AutopilotSession.StartProblem(client, "claude"));
                Assert.IsFalse(AutopilotSession.Start(frame, "Explore"));
                client.TestReceive("{\"type\":\"account\",\"supports_cli_launch\":true}");
                Assert.IsNull(AutopilotSession.StartProblem(client, "claude"));
                client.TestConnected(false);
                Assert.AreEqual(AutopilotStatus.RunOffline, AutopilotSession.StartProblem(client, "claude"));
                client.TestConnected(true);
                client.TestReceive(welcome);
                Assert.IsFalse(AutopilotSession.Start(frame, "Explore"));
                client.TestReceive("{\"type\":\"account\"}");
                Assert.AreEqual(AutopilotStatus.CliUpdate, AutopilotSession.StartProblem(client, "claude"));
                Assert.IsFalse(AutopilotSession.Start(frame, "Explore"));
                Assert.IsNull(AutopilotSession.StartProblem(client, ""));
                Assert.IsFalse(sent.Select(Json.ParseObject).Any(m => Json.Str(m, "type") == "start"));
                Assert.AreEqual("claude", Json.Str(frame, "cli_client"));
                Assert.IsFalse(AutopilotView.ExploreFrame(4, "").ContainsKey("cli_client"));
            }
            finally
            {
                client.TestWire = null;
                EditorHost.TestClient(previous);
            }
        }

        [Test]
        public void RunAgainKeepsTheRunsOwnTimeLimit()
        {
            // The run as it was (a copy), not the composer's Time limit now; an exploration plays for its minutes
            foreach (var kind in new[] { "verify", "goal", "auto" })
            {
                var frame = new Dictionary<string, object> { ["kind"] = kind, ["prompt"] = "x", ["time_limit_minutes"] = 45 };
                var again = AutopilotView.AgainFrame(frame);
                Assert.AreNotSame(frame, again, kind);
                Assert.AreEqual(45, (int)Json.Num(again, "time_limit_minutes", 0), kind);
                Assert.AreEqual(kind, Json.Str(again, "kind"));
            }
            var verify = AutopilotView.AgainFrame(new Dictionary<string, object> { ["kind"] = "verify", ["claim"] = "x" });
            Assert.IsFalse(verify.ContainsKey("time_limit_minutes"), "none: the agent service's own, as before");
            var exploreAgain = AutopilotView.AgainFrame(new Dictionary<string, object> { ["kind"] = "explore", ["minutes"] = 7 });
            Assert.IsFalse(exploreAgain.ContainsKey("time_limit_minutes"));
            Assert.AreEqual(7, (int)Json.Num(exploreAgain, "minutes", 0));
            Assert.IsNull(AutopilotView.AgainFrame(null));
        }

        [Test]
        public void ATestedExplorationIsGameTested()
        {
            AutopilotRun Ended(string kind, string result) => new AutopilotRun { Id = "tested", Kind = kind, Started = 1, Ended = 2, Result = Json.ParseObject(result) };
            Assert.AreEqual("GAME TESTED", Ended("explore", "{\"bugs\": [], \"tested\": true}").Verdict);
            Assert.AreEqual("EXPLORED", Ended("explore", "{\"bugs\": [], \"tested\": false}").Verdict);
            Assert.AreEqual("EXPLORED", Ended("explore", "{\"bugs\": []}").Verdict, "a result from before tests");
            Assert.AreEqual("GAME TESTED", Ended("run", "{\"bugs\": [], \"tested\": true}").Verdict, "its kind never said: the result alone");
            // (the same tone as Explored)
            Assert.AreEqual(AutopilotRun.Tone("EXPLORED"), AutopilotRun.Tone("GAME TESTED"));
            // Kept with the result, through a reload
            var back = AutopilotRun.Load(Json.ParseObject(Json.Write(Ended("explore", "{\"bugs\": [], \"tested\": true}").Save(100))));
            Assert.AreEqual("GAME TESTED", back.Verdict);
        }
    }

    public class RunEndTests
    {
        string folder;
        readonly List<AutopilotRun> added = new List<AutopilotRun>();

        [SetUp]
        public void SetUp()
        {
            folder = Path.Combine(Path.GetTempPath(), "autopilot-runend-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            AutopilotUsage.Flush();
            AutopilotUsage.LedgerOverride = Path.Combine(folder, "AutopilotUsage.json");
            AutopilotUsage.TestReload();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var r in added) AutopilotSession.Runs.Remove(r);
            added.Clear();
            AutopilotUsage.LedgerOverride = null;
            AutopilotUsage.TestReload();
            try
            {
                Directory.Delete(folder, true);
            }
            catch (Exception)
            {
                // (left in the temp folder)
            }
        }

        AutopilotRun Live(string kind = "goal")
        {
            var now = AutopilotText.UnixNow;
            var run = new AutopilotRun { Id = "runend-" + Guid.NewGuid().ToString("N"), Kind = kind, Prompt = "Reach the gate", Started = now - 30, StageSince = now - 30 };
            AutopilotSession.Runs.Add(run);
            added.Add(run);
            Assert.AreSame(run, AutopilotSession.Current);
            return run;
        }

        [Test]
        public void ItCanBeSteeredOnlyAtTheRollout()
        {
            Assert.IsFalse(AutopilotRun.SteerableAt(null));
            var run = Live();
            Assert.IsFalse(AutopilotRun.SteerableAt(run), "no stage yet");
            foreach (var (line, stage) in new[]
                     {
                         ("Starting the game", 0), ("Reading the game's code", 1), ("Trying things in the game", 2), ("Writing the harness", 3),
                     })
            {
                AutopilotSession.TestStatus(line);
                Assert.AreEqual(stage, run.Stage);
                Assert.IsFalse(AutopilotRun.SteerableAt(run), AutopilotText.Stages[stage]);
            }
            AutopilotSession.TestStatus("Trying the harness (run 1)");
            Assert.IsTrue(AutopilotRun.SteerableAt(run), "Rollout");
            AutopilotSession.TestStatus("Now: walking to the gate");
            Assert.IsTrue(AutopilotRun.SteerableAt(run), "Rollout, its Now lines");
            AutopilotSession.TestStatus("Looking at the results");
            Assert.IsFalse(AutopilotRun.SteerableAt(run), "Process result");
            AutopilotSession.TestStatus("Trying the harness (run 2)");
            Assert.IsTrue(AutopilotRun.SteerableAt(run), "the next Rollout");
            AutopilotSession.TestStatus(AutopilotText.SavingMemory);
            Assert.IsFalse(AutopilotRun.SteerableAt(run), "Report");
            run.Stage = AutopilotText.StageRollout;
            run.Ended = AutopilotText.UnixNow;
            Assert.IsFalse(AutopilotRun.SteerableAt(run), "a run that ended, whatever its stage");
        }

        [Test]
        public void SteeringIsSentOnlyAtTheRollout()
        {
            var clientWas = EditorHost.Client;
            var sent = new List<string>();
            var client = new PlaytestClient(EditorHost.Instance) { TestWire = s => sent.Add(s) };
            try
            {
                client.TestConnected(true);
                client.TestReceive("{\"type\": \"welcome\", \"role\": \"editor\", \"session\": \"steer\", \"features\": [\"steer\"]}");
                EditorHost.TestClient(client);
                Assert.IsTrue(AutopilotSession.Start(new Dictionary<string, object> { ["kind"] = "goal", ["goal"] = "Reach the gate" }, "Reach the gate"));
                var run = AutopilotSession.Current;
                added.Add(run);
                Assert.IsTrue(AutopilotSession.CanSteer);
                int Steers() => sent.Count(s => Json.Str(Json.ParseObject(s), "type") == "steer");

                AutopilotSession.TestStatus("Writing the harness");
                Assert.IsFalse(AutopilotSession.SteerRun("go left"), "Build harness: refused");
                AutopilotSession.TestStatus("Trying the harness (run 1)");
                Assert.IsTrue(AutopilotSession.SteerRun("go left"), "Rollout: sent");
                Assert.AreEqual(1, Steers());
                Assert.AreEqual("You: go left", run.Feed.Last().Text);
                AutopilotSession.TestStatus("Looking at the results");
                var feed = run.Feed.Count;
                Assert.IsFalse(AutopilotSession.SteerRun("go right"), "Process result: refused");
                AutopilotSession.TestStatus(AutopilotText.SavingMemory);
                Assert.IsFalse(AutopilotSession.SteerRun("go right"), "Report: refused");
                Assert.AreEqual(1, Steers(), "nothing sent outside the Rollout");
                Assert.AreEqual(feed + 1, run.Feed.Count, "(only the status line: no You line)");
            }
            finally
            {
                client.Disconnect();
                client.TestWire = null;
                EditorHost.TestClient(clientWas);
                var cur = AutopilotSession.Current;
                if (cur != null && added.Contains(cur)) cur.Ended = AutopilotText.UnixNow;
            }
        }

        [Test]
        public void ARunStartedFromYourCodingAgentIsShownAsTheWindowsOwn()
        {
            var clientWas = EditorHost.Client;
            var sent = new List<string>();
            var client = new PlaytestClient(EditorHost.Instance) { TestWire = s => sent.Add(s) };
            try
            {
                client.TestConnected(true);
                client.TestReceive("{\"type\": \"welcome\", \"role\": \"editor\", \"session\": \"agent\", \"features\": [\"steer\"]}");
                EditorHost.TestClient(client);
                AutopilotSession.TestHook(client);
                Assert.IsNull(AutopilotSession.Current, "no run going before");

                // Claude Code's verify run: going, as one the window started, with where it came from
                client.TestReceive("{\"type\": \"run_started\", \"run\": {\"kind\": \"verify\", \"claim\": \"Verify that the crate turns green\", \"time_limit_minutes\": 15, \"by\": \"claude-code\"}}");
                var run = AutopilotSession.Current;
                Assert.IsNotNull(run, "run_started: a run going");
                added.Add(run);
                Assert.IsTrue(client.RunActive);
                Assert.AreEqual("verify", run.Kind);
                Assert.AreEqual("Verify that the crate turns green", run.Prompt);
                Assert.AreEqual("claude-code", run.By);
                Assert.AreEqual("Started from Claude Code", run.Origin);
                Assert.IsTrue(AutopilotSession.CanSteer, "steering reaches it");
                AutopilotSession.TestStatus("Trying the harness (run 1)");
                Assert.IsTrue(AutopilotSession.SteerRun("open the other crate"));
                Assert.AreEqual(1, sent.Count(s => Json.Str(Json.ParseObject(s), "type") == "steer"));
                Assert.AreEqual(0, sent.Count(s => Json.Str(Json.ParseObject(s), "type") == "start"), "the window sent no start of its own");

                // Another while it goes: ignored (never a second run over the one going)
                client.TestReceive("{\"type\": \"run_started\", \"run\": {\"kind\": \"goal\", \"goal\": \"Light a lantern\", \"by\": \"codex\"}}");
                Assert.AreSame(run, AutopilotSession.Current);
                Assert.AreEqual(1, AutopilotSession.Runs.Count(r => added.Contains(r)));

                // Its result ends it and is kept with it, origin and all (across a reload too)
                client.TestReceive("{\"type\": \"result\", \"result\": {\"verdict\": \"CONFIRMED\", \"reason\": \"It turns green.\", \"claim\": \"Verify that the crate turns green\"}}");
                Assert.IsFalse(run.Active);
                Assert.IsFalse(client.RunActive);
                Assert.AreEqual("CONFIRMED", run.Verdict);
                Assert.AreEqual("Started from Claude Code", AutopilotRun.Load(run.Save(100)).Origin);

                // Codex's, and one from a coding agent the window doesn't know; an explore's brief is its request
                client.TestReceive("{\"type\": \"run_started\", \"run\": {\"kind\": \"goal\", \"goal\": \"Light a lantern\", \"by\": \"codex\"}}");
                var codex = AutopilotSession.Current;
                added.Add(codex);
                Assert.AreEqual("Light a lantern", codex.Prompt);
                Assert.AreEqual("Started from Codex", codex.Origin);
                client.TestReceive("{\"type\": \"result\", \"result\": {\"reached\": true}}");
                client.TestReceive("{\"type\": \"run_started\", \"run\": {\"kind\": \"explore\", \"brief\": \"the market\", \"minutes\": 5, \"by\": \"mcp\"}}");
                var other = AutopilotSession.Current;
                added.Add(other);
                Assert.AreEqual("explore", other.Kind);
                Assert.AreEqual("Explore the market", other.Prompt, "as the window shows its own explores");
                Assert.AreEqual("Started from your coding agent", other.Origin);
                client.TestReceive("{\"type\": \"result\", \"result\": {\"bugs\": []}}");

                // One whose kind the agent service reads: its words as its request, its kind blank until run_kind says it
                client.TestReceive("{\"type\": \"run_started\", \"run\": {\"kind\": \"auto\", \"prompt\": \"Is the shop open at night?\", \"time_limit_minutes\": 20, \"by\": \"claude-code\"}}");
                var auto = AutopilotSession.Current;
                added.Add(auto);
                Assert.AreEqual("run", auto.Kind);
                Assert.AreEqual("Is the shop open at night?", auto.Prompt);
                client.TestReceive("{\"type\": \"run_kind\", \"kind\": \"verify\", \"tested\": false}");
                Assert.AreEqual("verify", auto.Kind);
                client.TestReceive("{\"type\": \"result\", \"result\": {\"verdict\": \"DENIED\"}}");

                // A run the window started says nothing of where it came from
                Assert.AreEqual("", AutopilotText.StartedFrom(""));
            }
            finally
            {
                client.Disconnect();
                client.TestWire = null;
                EditorHost.TestClient(clientWas);
                AutopilotSession.TestHook(clientWas);
                foreach (var r in added) if (r != null && r.Active) r.Ended = AutopilotText.UnixNow;
            }
        }

        [Test]
        public void TheAgentServiceSaysTheKindItRead()
        {
            var clientWas = EditorHost.Client;
            var sent = new List<string>();
            var client = new PlaytestClient(EditorHost.Instance) { TestWire = s => sent.Add(s) };
            try
            {
                client.TestConnected(true);
                client.TestReceive("{\"type\": \"welcome\", \"role\": \"editor\", \"session\": \"kind\", \"features\": [\"steer\"]}");
                EditorHost.TestClient(client);
                AutopilotSession.TestHook(client);
                const string words = "Lvl_HordeArea test this game and find all the bugs with animation";
                Assert.IsTrue(AutopilotSession.Start(AutopilotView.ComposerFrame(words, 25, out var shown), shown));
                var run = AutopilotSession.Current;
                added.Add(run);
                // Sent as typed, its kind the agent service's to read; blank until it says
                var start = Json.Obj(sent.Select(Json.ParseObject).Last(m => Json.Str(m, "type") == "start"), "run");
                Assert.AreEqual("auto", Json.Str(start, "kind"));
                Assert.AreEqual(words, Json.Str(start, "prompt"));
                Assert.AreEqual(25, (int)Json.Num(start, "time_limit_minutes", 0));
                Assert.AreEqual("run", run.Kind);
                Assert.AreEqual(words, run.Prompt);
                StringAssert.Contains("// RUN //", AutopilotView.IdLine(run));

                // What Jev read: the run is that kind from now on (a kind it doesn't know changes nothing)
                client.TestReceive("{\"type\": \"run_kind\", \"kind\": \"explore\", \"tested\": true}");
                Assert.AreEqual("explore", run.Kind);
                StringAssert.Contains("// EXPLORE //", AutopilotView.IdLine(run));
                client.TestReceive("{\"type\": \"run_kind\", \"kind\": \"dream\", \"tested\": false}");
                Assert.AreEqual("explore", run.Kind);

                // A test's result: GAME TESTED, kept across a reload
                client.TestReceive("{\"type\": \"result\", \"result\": {\"bugs\": [], \"tested\": true}}");
                Assert.IsFalse(run.Active);
                Assert.AreEqual("GAME TESTED", run.Verdict);
                Assert.AreEqual("Result: Game tested", run.Feed.Last(l => l.Kind == "stage").Text);
                var back = AutopilotRun.Load(Json.ParseObject(Json.Write(run.Save(100))));
                Assert.AreEqual("explore", back.Kind);
                Assert.AreEqual("GAME TESTED", back.Verdict);
            }
            finally
            {
                client.Disconnect();
                client.TestWire = null;
                EditorHost.TestClient(clientWas);
                AutopilotSession.TestHook(clientWas);
                foreach (var r in added) if (r != null && r.Active) r.Ended = AutopilotText.UnixNow;
            }
        }

        [Test]
        public void SavingToMemoryIsPartOfTheReport()
        {
            var run = Live();
            AutopilotSession.TestStatus("Trying the harness (run 2)");
            AutopilotSession.TestStatus("Now: walking to the gate");
            AutopilotSession.TestStatus("Looking at the results");
            Assert.AreEqual(AutopilotText.StageResults, run.Stage);
            AutopilotSession.TestStatus(AutopilotText.SavingMemory);
            Assert.AreEqual(AutopilotText.StageReport, run.Stage, "saving to memory: the Report stage");
            Assert.AreEqual("", run.Now);
            Assert.AreEqual(AutopilotText.SavingMemory, run.Feed.Last().Text);
            Assert.AreEqual("stage", run.Feed.Last().Kind);
            AutopilotSession.TestResult(new Dictionary<string, object> { ["reached"] = true }, folder);
            Assert.AreEqual(AutopilotText.StageReport, run.Stage, "the result: still the report, never a step back");
            Assert.IsFalse(run.Active);

            // (said straight after the rollout too: the report, never before it)
            var other = Live();
            AutopilotSession.TestStatus("Trying the harness (run 1)");
            AutopilotSession.TestStatus(AutopilotText.SavingMemory + "…");
            Assert.AreEqual(AutopilotText.StageReport, other.Stage);
            other.Ended = AutopilotText.UnixNow;
        }

        [Test]
        public void AResultThatUpdatedMemoryEndsTheFeedWithIt()
        {
            var run = Live();
            AutopilotSession.TestResult(Json.ParseObject("{\"reached\": true, \"memory\": {\"updated\": true, \"notes\": true, \"lib\": false}}"), folder);
            var last = run.Feed.Last();
            Assert.AreEqual("memory", last.Kind);
            Assert.AreEqual("Memory updated", last.Text);
            // (kept with the run across a script reload)
            var back = AutopilotRun.Load(Json.ParseObject(Json.Write(run.Save(100))));
            Assert.AreEqual("memory", back.Feed.Last().Kind);
            Assert.AreEqual(last.Text, back.Feed.Last().Text);

            foreach (var json in new[] { "{\"reached\": true, \"memory\": {\"updated\": false, \"notes\": true, \"lib\": true}}", "{\"reached\": true}" })
            {
                var plain = Live();
                AutopilotSession.TestResult(Json.ParseObject(json), folder);
                Assert.IsFalse(plain.Feed.Any(l => l.Kind == "memory"), json);
            }
        }

        [Test]
        public void TheRecordingIsCopiedIntoTheRunFolder()
        {
            var source = Path.Combine(folder, "memory", "recording.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(source));
            var bytes = Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray();
            File.WriteAllBytes(source, bytes);
            var dir = Path.Combine(folder, "Logs", "Playtest", "run1");

            var run = Live();
            AutopilotSession.TestResult(Json.ParseObject($"{{\"reached\": true, \"recording\": {{\"video\": {Json.Write(source)}, \"seconds\": 123.4}}}}"), dir);
            // At once: shown from where the agent service wrote it; then from the copy in the run's folder
            Assert.AreEqual(123.4, run.RecordingSeconds, 1e-9);
            Assert.IsTrue(run.Recording == source || run.Recording == Path.Combine(dir, "recording.mp4"));
            var until = DateTime.UtcNow.AddSeconds(10);
            while (run.Recording != Path.Combine(dir, "recording.mp4") && DateTime.UtcNow < until)
            {
                Thread.Sleep(20);
                AutopilotSession.TestMainThread();
            }
            Assert.AreEqual(Path.Combine(dir, "recording.mp4"), run.Recording);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(run.Recording));

            // Kept with the run (a run reopened from the history shows it); a run from before recordings has none
            var back = AutopilotRun.Load(Json.ParseObject(Json.Write(run.Save(100))));
            Assert.AreEqual(run.Recording, back.Recording);
            Assert.AreEqual(123.4, back.RecordingSeconds, 1e-9);
            Assert.AreEqual("", AutopilotRun.Load(Json.ParseObject("{\"id\": \"old\", \"kind\": \"goal\"}")).Recording);

            // A copy that can't be made: the agent service's path stays
            var gone = Live();
            var missing = Path.Combine(folder, "nowhere", "recording.mp4");
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex("Couldn't copy the run's recording"));
            var warned = false;
            void Heard(string message, string stack, UnityEngine.LogType type) => warned |= message.Contains("Couldn't copy the run's recording");
            UnityEngine.Application.logMessageReceived += Heard;
            try
            {
                AutopilotSession.TestResult(Json.ParseObject($"{{\"reached\": true, \"recording\": {{\"video\": {Json.Write(missing)}, \"seconds\": 5}}}}"), Path.Combine(folder, "Logs", "Playtest", "run2"));
                until = DateTime.UtcNow.AddSeconds(10);
                while (!warned && DateTime.UtcNow < until)
                {
                    Thread.Sleep(20);
                    AutopilotSession.TestMainThread();
                }
            }
            finally
            {
                UnityEngine.Application.logMessageReceived -= Heard;
            }
            Assert.IsTrue(warned, "the failed copy is said");
            Assert.AreEqual(missing, gone.Recording);

            // No recording in the result: none
            var none = Live();
            AutopilotSession.TestResult(Json.ParseObject("{\"reached\": true}"), dir);
            Assert.AreEqual("", none.Recording);
        }

        [Test]
        public void ARunWithNoRecordingSaysWhy()
        {
            const string why = "the game can't be recorded here: it runs without rendering";
            // The decision, from the result alone
            Assert.AreEqual("No recording: " + why, AutopilotRun.NoRecordingLine(Json.ParseObject($"{{\"reached\": true, \"memory\": {{\"updated\": false}}, \"no_recording\": {Json.Write(why)}}}")));
            Assert.AreEqual("No recording: " + why, AutopilotRun.NoRecordingLine(Json.ParseObject($"{{\"reached\": true, \"no_recording\": {Json.Write(why)}}}")), "said, even with no memory");
            Assert.AreEqual(AutopilotText.NoRecordingOlderService, AutopilotRun.NoRecordingLine(Json.ParseObject("{\"reached\": true}")), "neither recording nor memory: an older agent service");
            Assert.AreEqual("No recording.", AutopilotText.NoRecordingOlderService);
            Assert.AreEqual("", AutopilotRun.NoRecordingLine(Json.ParseObject("{\"reached\": true, \"memory\": {\"updated\": true}}")), "memory without either: nothing");
            Assert.AreEqual("", AutopilotRun.NoRecordingLine(Json.ParseObject("{\"reached\": true, \"recording\": {\"video\": \"C:/r.mp4\", \"seconds\": 3}}")), "a recording: nothing");
            Assert.AreEqual("", AutopilotRun.NoRecordingLine(null));

            // Decided when the result comes, and kept with the run
            var run = Live();
            Assert.AreEqual("", run.NoRecording, "nothing while it goes");
            AutopilotSession.TestResult(Json.ParseObject($"{{\"reached\": true, \"memory\": {{\"updated\": false}}, \"no_recording\": {Json.Write(why)}}}"), folder);
            Assert.AreEqual("No recording: " + why, run.NoRecording);
            var back = AutopilotRun.Load(Json.ParseObject(Json.Write(run.Save(100))));
            Assert.AreEqual("No recording: " + why, back.NoRecording, "after a reload");

            var older = Live();
            AutopilotSession.TestResult(Json.ParseObject("{\"reached\": true}"), folder);
            Assert.AreEqual(AutopilotText.NoRecordingOlderService, AutopilotRun.Load(Json.ParseObject(Json.Write(older.Save(100)))).NoRecording);

            var plain = Live();
            AutopilotSession.TestResult(Json.ParseObject("{\"reached\": true, \"memory\": {\"updated\": true}}"), folder);
            Assert.AreEqual("", plain.NoRecording);
            Assert.AreEqual("", AutopilotRun.Load(Json.ParseObject(Json.Write(plain.Save(100)))).NoRecording);

            // A run kept before the line (no such field, its result without recording or memory): nothing
            var old = AutopilotRun.Load(Json.ParseObject("{\"id\": \"old\", \"kind\": \"goal\", \"ended\": 5, \"result\": {\"reached\": true}}"));
            Assert.AreEqual("", old.NoRecording);
        }

        [Test]
        public void ARunKeepsItsResultsAlerts()
        {
            const string alert = "Autopilot couldn't look at the game's picture: the game runs without a picture (headless: nothing is rendered). A claim about how something looks (an animation, an effect, the UI) can't be settled without it: run the game with its window shown (not headless, not minimized).";
            // From the result alone: each as said, blanks and non-texts left out; none when the result has none
            CollectionAssert.AreEqual(new[] { alert, "second" }, AutopilotRun.AlertsOf(Json.ParseObject($"{{\"verdict\": \"INCONCLUSIVE\", \"alerts\": [{Json.Write(alert)}, \"  \", 3, \" second \"]}}")));
            CollectionAssert.IsEmpty(AutopilotRun.AlertsOf(Json.ParseObject("{\"verdict\": \"CONFIRMED\"}")), "no alerts: none");
            CollectionAssert.IsEmpty(AutopilotRun.AlertsOf(null));

            // Kept with the result, through a reload
            var run = Live("verify");
            CollectionAssert.IsEmpty(run.ResultAlerts, "none while it goes");
            AutopilotSession.TestResult(Json.ParseObject($"{{\"verdict\": \"INCONCLUSIVE\", \"reason\": \"It couldn't look.\", \"alerts\": [{Json.Write(alert)}]}}"), folder);
            CollectionAssert.AreEqual(new[] { alert }, run.ResultAlerts);
            var back = AutopilotRun.Load(Json.ParseObject(Json.Write(run.Save(100))));
            CollectionAssert.AreEqual(new[] { alert }, back.ResultAlerts, "after a reload");

            // A run kept before alerts: none
            var old = AutopilotRun.Load(Json.ParseObject("{\"id\": \"old\", \"kind\": \"verify\", \"ended\": 5, \"result\": {\"verdict\": \"CONFIRMED\"}}"));
            CollectionAssert.IsEmpty(old.ResultAlerts);
        }
    }

    // The activity feed (docs/protocol.md, `activity`): each frame a line of the run's feed under its stage (which drives
    // the stage track), a call and its result one line (the result's words after the call's; two folds), a frame seen
    // already (the feed sent again after a reconnect) changing nothing, a result whose call isn't here a line of its own;
    // the Feed tab's items: a heading per stretch of one stage with its count; the folds' words; what the hello asks for
    // (activity_since); a run saved and read back with its events.
    public class ActivityTests
    {
        readonly List<AutopilotRun> added = new List<AutopilotRun>();

        [TearDown]
        public void TearDown()
        {
            foreach (var r in added) AutopilotSession.Runs.Remove(r);
            added.Clear();
        }

        AutopilotRun Live()
        {
            var now = AutopilotText.UnixNow;
            var run = new AutopilotRun { Id = "activity-" + Guid.NewGuid().ToString("N"), Kind = "goal", Prompt = "Reach the gate", Started = now - 30, StageSince = now - 30 };
            AutopilotSession.Runs.Add(run);
            added.Add(run);
            Assert.AreSame(run, AutopilotSession.Current);
            return run;
        }

        static Dictionary<string, object> Frame(int id, double t, string stage, string kind, string text, string detail, int ends)
        {
            return new Dictionary<string, object>
            {
                ["type"] = "activity", ["id"] = (double)id, ["t"] = t, ["stage"] = stage, ["kind"] = kind, ["text"] = text,
                ["detail"] = detail, ["ends"] = ends > 0 ? (object)(double)ends : null,
            };
        }

        [Test]
        public void EveryKindHasAnIconTheWindowCanDraw()
        {
            // An icon per kind, the same meanings as the other Autopilot UIs
            var kinds = new[] { "text", "read", "search", "write", "probe", "episode", "look", "restart", "steer", "report", "alert", "refine" };
            var icons = new[] { "robot", "magnifier", "binoculars", "pencil", "flask", "gamepad", "eye", "circular-arrow", "speech-bubble", "flag", "megaphone", "notebook" };
            var glyphs = new[] { "?", "R", "S", "W", "P", "E", "L", "*", ">", "!", "!", "M" };
            for (var i = 0; i < kinds.Length; ++i)
            {
                Assert.AreEqual(icons[i], AutopilotText.ActivityIcon(kinds[i]), kinds[i]);
                Assert.AreEqual(glyphs[i], AutopilotText.ActivityGlyph(kinds[i]), kinds[i]);
                Assert.IsNotEmpty(AutopilotText.ActivityTitle(kinds[i]), kinds[i]);
            }
            Assert.AreEqual("Refined the game's memory", AutopilotText.ActivityTitle("refine"));
            Assert.AreEqual("the edit and its evidence", AutopilotText.DetailLabel("refine"));
            Assert.AreEqual("", AutopilotText.ActivityIcon("dream"));
            // The editor's own icons the feed uses exist in this Unity (a missing name gives an empty image)
            foreach (var pair in AutopilotView.EditorIcons)
                Assert.IsNotNull(UnityEditor.EditorGUIUtility.IconContent(pair.Value)?.image, pair.Key + ": " + pair.Value);
            // Every icon is drawable as Lucide's paths too (the fallback, and the seven the editor has no icon for)
            foreach (var icon in icons)
            {
                Assert.IsTrue(AutopilotUI.Glyph.Paths.ContainsKey(icon), icon);
                var lines = AutopilotUI.Glyph.Paths[icon].SelectMany(AutopilotUI.PathPolylines).ToList();
                Assert.IsTrue(lines.Count > 0 && lines.All(l => l.Points.Count >= 2), icon);
                Assert.IsTrue(lines.All(l => l.Points.All(q => q.x >= -0.5f && q.x <= 24.5f && q.y >= -0.5f && q.y <= 24.5f)), icon + " stays in its box");
            }
            // The path reader: a flag (smooth cubics, a close) and a circle (two arcs)
            var flag = AutopilotUI.PathPolylines("M4 15s1-1 4-1 5 2 8 2 4-1 4-1V3s-1 1-4 1-5-2-8-2-4 1-4 1z");
            Assert.AreEqual(1, flag.Count);
            Assert.IsTrue(flag[0].Closed && flag[0].Points.Count > 20);
            var circle = AutopilotUI.PathPolylines("M19 11a8 8 0 1 1-16 0a8 8 0 1 1 16 0z")[0];
            Assert.IsTrue(circle.Closed && circle.Points.All(q => Math.Abs(Vector2.Distance(q, new Vector2(11, 11)) - 8) < 0.01f));
        }

        [Test]
        public void EventsBecomeLinesUnderTheirStage()
        {
            var run = Live();
            var now = AutopilotText.UnixNow;
            Assert.IsTrue(run.OnActivity(Frame(1, 2.0, "analyze", "read", "Read codex/README.md", "codex/README.md", 0), now));
            Assert.AreEqual(1, run.Feed.Count);
            Assert.AreEqual(1, run.Stage, "the frame's stage drives the track");
            var read = run.Feed[0];
            Assert.IsTrue(read.IsActivity && read.ActivityId == 1 && read.Stage == 1);
            // One clock per feed: the line's time is the window's (the run just started here: about 0), the frame's t kept apart
            Assert.IsTrue(read.At >= 30 && read.At < 60, "the window's clock (the run began 30 s ago here), not the frame's t: " + read.At);
            Assert.AreEqual(2.0, read.AgentT, 1e-9);
            Assert.AreEqual("magnifier", read.Icon);
            Assert.AreEqual("Read a file", read.Title);
            Assert.AreEqual("R", read.Glyph);
            Assert.AreEqual("", read.DetailLabel, "a short path folds nothing");

            Assert.IsTrue(run.OnActivity(Frame(2, 83.4, "probe", "probe", "Tried 12 lines in the game", "print(1)\nprint(2)", 0), now));
            Assert.IsTrue(run.OnActivity(Frame(3, 84.6, "probe", "probe", "Got 3 lines back in 1.2 s", "1\n2\n3", 2), now));
            Assert.AreEqual(2, run.Feed.Count, "a probe and its result: one line");
            Assert.AreEqual(2, run.Stage);
            var probe = run.Feed[1];
            Assert.AreEqual("Tried 12 lines in the game", probe.Text);
            Assert.AreEqual("Got 3 lines back in 1.2 s", probe.Result);
            Assert.AreEqual(3, probe.ResultId);
            Assert.AreEqual("1\n2\n3", probe.ResultDetail);
            Assert.AreEqual("flask", probe.Icon);
            Assert.AreEqual("Tried something in the game", probe.Title);
            Assert.AreEqual("the code", probe.DetailLabel);
            Assert.AreEqual("the output", probe.ResultLabel);
            Assert.AreEqual("+ inspect the code", AutopilotText.FoldLabel(probe.DetailLabel, false));
            Assert.AreEqual("– hide the code", AutopilotText.FoldLabel(probe.DetailLabel, true));
            Assert.AreEqual("+ inspect the output", AutopilotText.FoldLabel(probe.ResultLabel, false));
            Assert.IsTrue(probe.At >= 30 && probe.At < 60 && Math.Abs(probe.AgentT - 83.4) < 1e-9, "the probe's time: the window's clock");
            Assert.AreEqual(3, run.LastActivityId);
            Assert.AreSame(probe, run.FindActivity(3), "found by the result's id too");

            // The feed sent again (a reconnect): nothing changes
            Assert.IsFalse(run.OnActivity(Frame(2, 83.4, "probe", "probe", "Tried 12 lines in the game", "print(1)\nprint(2)", 0), now));
            Assert.IsFalse(run.OnActivity(Frame(3, 84.6, "probe", "probe", "Got 3 lines back in 1.2 s", "1\n2\n3", 2), now));
            Assert.AreEqual(2, run.Feed.Count);
            Assert.AreEqual(2, run.ReplaySkipped, "the 2 skipped counted for the log");

            // A result whose call never came here: a line of its own, at the stage it says
            Assert.IsTrue(run.OnActivity(Frame(5, 90.0, "play", "episode", "Episode 1: 60 s of play (status done)", "The agent walked to the gate.\nIt opened.", 4), now));
            Assert.AreEqual(3, run.Feed.Count);
            Assert.AreEqual(AutopilotText.StageRollout, run.Stage);
            Assert.IsTrue(AutopilotRun.SteerableAt(run), "play: steerable");
            Assert.AreEqual("gamepad", run.Feed[2].Icon);
            Assert.AreEqual("what was asked", run.Feed[2].DetailLabel);
            Assert.AreEqual(0, run.ReplaySkipped, "a new event after a replay: the skipped count said and cleared");
            Assert.AreEqual(5, AutopilotSession.ActivitySince(), "the hello asks from the live run's last id");

            // A status line still drives the stage (an older agent service sends no activity frames), its line under it
            run.EnterStage(AutopilotText.StageResults, now);
            run.AddLine(new AutopilotFeedLine { At = 95, Text = "Processing results", Kind = "stage" });
            Assert.AreEqual(AutopilotText.StageResults, run.Feed[3].Stage);

            // The Feed tab's items: a heading before each stage's lines, with how many
            var items = AutopilotView.GroupFeed(run.Feed);
            Assert.AreEqual(8, items.Count, "4 headings and 4 lines");
            var headings = new[] { "Analyze", "Probe", "Play", "Results" };
            for (var i = 0; i < 4; ++i)
            {
                Assert.IsTrue(items[i * 2].IsHeading && items[i * 2].Text == headings[i] && items[i * 2].HeadingCount == 1, headings[i]);
                Assert.AreSame(run.Feed[i], items[i * 2 + 1]);
            }
            Assert.IsTrue(run.OnActivity(Frame(6, 96.0, "results", "text", "The gate opened on the second try", "The gate opened on the second try.\nThe first press came too early.", 0), now));
            items = AutopilotView.GroupFeed(run.Feed);
            Assert.AreEqual(9, items.Count);
            Assert.AreEqual(2, items[6].HeadingCount, "Results: 2 lines");
            Assert.AreEqual("robot", items[8].Icon);
            Assert.AreEqual("What the agent said", items[8].Title);
            Assert.AreEqual("the whole message", items[8].DetailLabel);

            // The refiner's edit to the game's memory: a notebook, its fold the edit and its evidence
            Assert.IsTrue(run.OnActivity(Frame(7, 97.0, "results", "refine", "Added to advice.md after episode 2: Open the crate straight away",
                "+ Open the crate straight away\nEvidence: episode 2 reached the gate at 41 s", 0), now));
            var refine = run.Feed[run.Feed.Count - 1];
            Assert.AreEqual("notebook", refine.Icon);
            Assert.AreEqual("Refined the game's memory", refine.Title);
            Assert.AreEqual("M", refine.Glyph);
            Assert.AreEqual("the edit and its evidence", refine.DetailLabel);

            // Done: saved and read back with its events; nothing asked any more
            run.Ended = now + 1;
            Assert.AreEqual(-1, AutopilotSession.ActivitySince());
            var back = AutopilotRun.Load(Json.ParseObject(Json.Write(run.Save(100))));
            Assert.AreEqual(run.Feed.Count, back.Feed.Count);
            Assert.AreEqual(7, back.LastActivityId);
            var p = back.Feed[1];
            Assert.IsTrue(p.IsActivity && p.ActivityId == 2 && p.ResultId == 3 && p.Result == "Got 3 lines back in 1.2 s"
                && p.Detail == "print(1)\nprint(2)" && p.ResultDetail == "1\n2\n3" && p.ActivityKind == "probe" && p.Stage == 2);
            Assert.AreEqual(AutopilotText.StageResults, back.Feed[3].Stage);
            Assert.AreSame(back.FindActivity(2), back.FindActivity(3));
        }
    }
}
