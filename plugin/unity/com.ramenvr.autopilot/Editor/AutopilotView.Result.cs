// The Autopilot window's Result tab and the copied report (AutopilotView.cs has the rest of the window).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using static Autopilot.Playtest.Editor.AutopilotUI;

namespace Autopilot.Playtest.Editor
{
    public sealed partial class AutopilotView
    {
        // -- result ------------------------------------------------------------------------------------------

        void BuildResult(AutopilotRun run, PlaytestClient client)
        {
            var box = PageBox("full result-body " + (Wide ? "stack-14" : "stack-12"));
            var result = run.Result ?? new Dictionary<string, object>();

            // The run's id line, and what to do with the run at its right (icons, named by their tooltips): one row at
            // any width (the line wraps when narrow, the icons stay)
            var top = Row("run-head");
            top.Add(L($"RUN {run.Id} // {KindWord(run)} // {AutopilotText.When(run.Started).ToUpperInvariant()}", "idline grow run-id"));
            AddActions(top, run, client);
            box.Add(top);
            box.Add(L(Shown(run), "big"));
            if (run.Origin.Length > 0) box.Add(Para(run.Origin, "small t-sec2 run-origin"));
            // What the user must know to trust the outcome (the game's picture couldn't be looked at), before its reason
            foreach (var alert in run.ResultAlerts) box.Add(ResultAlert(alert));

            // The verdict over its reason (one left edge: a long reason runs the page's width)
            var verdict = run.Verdict;
            var color = ToneColor(AutopilotRun.Tone(verdict));
            var reason = AutopilotText.Scrub(Reason(run));
            var vr = Box("col verdict-block");
            vr.Add(Verdict(verdict, color));
            if (!string.IsNullOrEmpty(reason)) vr.Add(L(reason, "reason"));
            box.Add(vr);

            // The run's recording, under the verdict; with none, why, as its result said (nothing in runs kept before that)
            // (a recording gone from its place: the player kept, nothing to press, saying so)
            if (!string.IsNullOrEmpty(run.Recording) || RecordingPlayer.PlaysFrames && !string.IsNullOrEmpty(run.RecordingFrames))
                box.Add(new RecordingPlayer(run.Recording, run.RecordingSeconds, run.RecordingFrames, run.RecordingFps));
            else if (!run.Active && !string.IsNullOrEmpty(run.NoRecording))
                box.Add(Para(AutopilotText.Breakable(AutopilotText.Scrub(run.NoRecording)), "small t-muted no-recording"));

            var evidence = Evidence(run);
            if (evidence.Count > 0)
            {
                var ev = Box("col");
                ev.Add(Lbl("Evidence", "section-head"));
                var mb = Box("monobox");
                foreach (var line in evidence) mb.Add(EvidenceLine(line));
                ev.Add(mb);
                box.Add(ev);
            }

            // What the agent changed in the game directly (a verification's setup): the result was played on a changed game
            var changes = Changes(run);
            if (changes.Count > 0)
            {
                var ch = Box("col");
                ch.Add(Lbl(Wide ? $"Changed directly · not through play · {changes.Count}" : $"Changed directly · {changes.Count}", "t-amber section-head"));
                var mb = Box("monobox");
                foreach (var line in changes) mb.Add(new Label { enableRichText = true, text = $"<color=#e8a94e>!</color>  <noparse>{line}</noparse>" });
                ch.Add(mb);
                box.Add(ch);
            }

            var notes = AgentNotes(run);
            var engine = run.Findings.Where(AutopilotRun.IsEngine).ToList();
            if (notes.Count > 0)
            {
                var agent = Box("col stack-8");
                foreach (var note in notes) agent.Add(AgentCard(note));
                agent.style.marginBottom = Wide ? 6 : 4;  // (8 under its last card; the page's gap under the section)
                box.Add(agent);
            }
            if (engine.Count > 0)
            {
                var section = Box("col stack-8");
                section.Add(Lbl($"Engine findings · {engine.Count}"));
                foreach (var f in engine) section.Add(EngineCard(run, f, client));
                section.style.marginBottom = Wide ? 6 : 4;
                box.Add(section);
            }
        }

        public const string RunAgainName = "Run again";
        public const string CopyReportName = "Copy report";
        public const string RunFolderName = "Run folder";

        /// <summary>Run again (filled, the accent), Copy report and Run folder (outlined), as icons.</summary>
        void AddActions(VisualElement row, AutopilotRun run, PlaytestClient client)
        {
            var again = IconBtn(Glyph.Shape.Retry, RunAgainName, () =>
            {
                if (run.RunFrame == null) return;
                // (the run as it was, its own time limit included)
                var frame = AgainFrame(run.RunFrame);
                if (frame != null && AutopilotSession.Start(frame, run.Prompt))
                {
                    Composing = false;
                    Changed();
                }
                Refresh(true);
            }, "primary");
            // (not connected, or a run going: greyed but pressable, the status line saying why)
            again.SetEnabled(run.RunFrame != null);
            var problem = AutopilotSession.StartProblem(client, Json.Str(run.RunFrame, "cli_client"));
            again.EnableInClassList("off", problem != null);
            row.Add(again);
            row.Add(IconBtn(Glyph.Shape.Clipboard, CopyReportName, () =>
            {
                EditorGUIUtility.systemCopyBuffer = Report(run);
                AutopilotStatus.Say(AutopilotStatus.CopiedReport);
            }));
            var folder = IconBtn(Glyph.Shape.Folder, RunFolderName, () =>
            {
                if (Directory.Exists(run.ReportDir)) EditorUtility.OpenWithDefaultApp(run.ReportDir);
            });
            folder.SetEnabled(!string.IsNullOrEmpty(run.ReportDir) && Directory.Exists(run.ReportDir));
            row.Add(folder);
        }

        static bool HasRecording(AutopilotRun run)
        {
            try
            {
                return !string.IsNullOrEmpty(run.Recording) && File.Exists(run.Recording);
            }
            catch (Exception)
            {
                return false;
            }
        }

        static Color ToneColor(string tone)
        {
            switch (tone)
            {
                case "lime": return Success;
                case "red": return Red;
                case "amber": return Amber;
                case "info": return Info;
                case "accent": return Accent;
                default: return Muted;
            }
        }

        static VisualElement ResultAlert(string alert)
        {
            var p = Box("problem amber result-alert");
            p.Add(Para(AutopilotText.Breakable(AutopilotText.Scrub(alert)), "body-500"));
            return p;
        }

        static string Reason(AutopilotRun run)
        {
            var r = run.Result;
            if (r == null)
                return run.LostConnection ? "The connection to the agent service closed before the result came."
                    : run.EditorClosed ? "The editor closed before the result came." : "Stopped before a result.";
            if (Json.Has(r, "reason")) return Json.Str(r, "reason");
            var notes = Json.List(r, "notes");
            if (notes != null && notes.Count > 0) return NoteText(notes[0]);
            return Json.Str(r, "summary");
        }

        static string NoteText(object o)
        {
            if (o is string s) return s;
            if (o is Dictionary<string, object> d)
            {
                foreach (var k in new[] { "summary", "title", "text", "description", "message" })
                    if (Json.Has(d, k))
                        return Json.Str(d, k);
            }
            return o == null ? "" : Json.Write(o);
        }

        static List<string> Evidence(AutopilotRun run)
        {
            var r = run.Result;
            var lines = new List<string>();
            if (r == null) return lines;
            var ev = Json.List(r, "evidence");
            if (ev != null) lines.AddRange(ev.Select(NoteText));
            else if (run.Kind != "explore")
            {
                var notes = Json.List(r, "notes");
                if (notes != null) lines.AddRange(notes.Skip(1).Select(NoteText));
            }
            return lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(AutopilotText.Scrub).ToList();
        }

        /// <summary>The result's direct changes to the game, each as the brain words it (docs/protocol.md, Runs: `changes`).</summary>
        static List<string> Changes(AutopilotRun run)
        {
            var list = run.Result == null ? null : Json.List(run.Result, "changes");
            if (list == null) return new List<string>();
            return list.OfType<Dictionary<string, object>>().Select(c => Json.Str(c, "text"))
                .Where(t => !string.IsNullOrWhiteSpace(t)).Select(AutopilotText.Scrub).ToList();
        }

        /// <summary>An evidence line, its leading harness and time in the accent: "H1 12.4 s", "H2 3:20" (the
        /// design's), or the older "A3 12.4 s". The agent writes evidence as free text, so a line without one is plain.</summary>
        internal static readonly System.Text.RegularExpressions.Regex EvidencePrefix =
            new System.Text.RegularExpressions.Regex(@"^([AH]\d+(\s+(\d+:\d{2}(\.\d+)?|[\d.]+\s?s))?)\s+");

        static VisualElement EvidenceLine(string line)
        {
            var l = new Label { enableRichText = true };
            var m = EvidencePrefix.Match(line);
            l.text = m.Success
                ? $"<color=#f0873a>{m.Groups[1].Value}</color>  <noparse>{line.Substring(m.Length)}</noparse>"
                : $"<noparse>{line}</noparse>";
            return l;
        }

        static List<string> AgentNotes(AutopilotRun run)
        {
            var notes = new List<string>();
            var r = run.Result;
            if (r != null)
            {
                if (run.Kind == "explore")
                {
                    var n = Json.List(r, "notes");
                    if (n != null) notes.AddRange(n.Skip(1).Select(NoteText));
                }
                foreach (var key in new[] { "findings", "bugs" })
                {
                    var list = Json.List(r, key);
                    if (list != null) notes.AddRange(list.Select(NoteText));
                }
            }
            notes.AddRange(run.Findings.Where(f => !AutopilotRun.IsEngine(f)).Select(f => Json.Str(f, "summary")));
            return notes.Where(n => !string.IsNullOrWhiteSpace(n)).Select(AutopilotText.Scrub).Distinct().ToList();
        }

        VisualElement AgentCard(string note)
        {
            // (its whole text, the period kept: no title and body split)
            var card = Card("plate tight stack-6");
            var head = Row("wrap");
            head.Add(Tag("Found", "red"));
            var l = Lbl("Agent finding");
            l.style.marginLeft = 8;
            head.Add(l);
            card.Add(head);
            card.Add(L(note, "finding-title"));
            var actions = Box("buttons");
            actions.Add(Btn("Verify a fix", () =>
            {
                var s = PlaytestSettings.Get();
                s.Prompt = "Verify that this is fixed: " + note;
                s.Save();
                Composing = true;
                Changed();
                Refresh(true);
            }, "small"));
            actions.Add(Btn("Copy", () =>
            {
                EditorGUIUtility.systemCopyBuffer = note;
                AutopilotStatus.Say(AutopilotStatus.Copied);
            }, "quiet small"));
            card.Add(actions);
            return card;
        }

        VisualElement EngineCard(AutopilotRun run, Dictionary<string, object> f, PlaytestClient client)
        {
            var card = Card("plate tight stack-6");
            var head = Row();
            var severity = Json.Str(f, "severity");
            if (string.IsNullOrWhiteSpace(severity)) severity = "info";  // (none said: INFO, blue)
            head.Add(Tag(severity, SeverityTone(severity)));
            var det = L(Json.Str(f, "detector"), "mono tiny t-muted grow nowrap");
            det.style.marginLeft = 8;
            head.Add(det);
            var count = (int)Json.Num(f, "count", 1);
            if (count > 1) head.Add(L("×" + count, "mono-700 small"));
            card.Add(head);
            card.Add(L(AutopilotText.Scrub(Json.Str(f, "summary")), "finding-title"));
            var where = Json.Obj(f, "where");
            var actor = Json.Str(f, "actor");
            var loc = new List<string>();
            if (where != null)
                loc.Add($"at {Json.Num(where, "X", 0):0}, {Json.Num(where, "Y", 0):0}, {Json.Num(where, "Z", 0):0} cm");
            if (!string.IsNullOrEmpty(actor)) loc.Add(actor);
            if (Json.Has(f, "t") && f["t"] is double t) loc.Add($"t={AutopilotText.Num(t, "0.0")}s");
            if (loc.Count > 0) card.Add(L(string.Join(" · ", loc), "mono tiny t-muted"));
            var actions = Box("buttons");
            if (where != null) actions.Add(Btn("Show in level", () => ShowInLevel(where, actor), "small"));
            var sig = Json.Str(f, "signature");
            if (run.NotABug.Contains(sig)) actions.Add(L("Marked not a bug", "small t-muted"));
            else if (!string.IsNullOrEmpty(sig))
            {
                // (not connected: greyed but pressable, the status line saying why)
                var name = Json.Str(f, "detector", Json.Str(f, "summary"));
                var nb = Btn("Not a bug", () =>
                {
                    var c = EditorHost.Client;
                    if (c == null || !c.Connected)
                    {
                        AutopilotStatus.Say(AutopilotStatus.FeedbackOffline);
                        return;
                    }
                    AutopilotSession.MarkNotABug(run, f);
                    AutopilotStatus.Say(AutopilotStatus.NotABug(name));
                }, "quiet small");
                nb.EnableInClassList("off", client == null || !client.Connected);
                if (client == null || !client.Connected) nb.tooltip = AutopilotStatus.FeedbackOffline;
                actions.Add(nb);
            }
            if (actions.childCount > 0) card.Add(actions);
            return card;
        }

        /// <summary>Frame the scene view on a finding's place (the kit frame: Z up, centimetres), and select the object
        /// it names; the status line says what it showed (or that the scene isn't open, or that nothing is there).</summary>
        public static void ShowInLevel(Dictionary<string, object> where, string actor)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded || scene.rootCount == 0)
            {
                AutopilotStatus.Say(AutopilotStatus.NoScene);
                return;
            }
            var point = Frame.ToUnity(where);
            var view = SceneView.lastActiveSceneView;
            if (view == null && SceneView.sceneViews.Count > 0) view = SceneView.sceneViews[0] as SceneView;
            if (view == null) view = EditorWindow.GetWindow<SceneView>();
            if (view == null) return;
            view.Frame(new Bounds(point, Vector3.one * 2f), false);
            view.Focus();
            GameObject go = null;
            if (!string.IsNullOrEmpty(actor))
            {
                var name = actor.Split('/').LastOrDefault();
                go = string.IsNullOrEmpty(name) ? null : GameObject.Find(actor.TrimStart('/')) ?? GameObject.Find(name);
                if (go != null) Selection.activeGameObject = go;
            }
            AutopilotStatus.Say(go != null ? AutopilotStatus.Showing(go.name) : AutopilotStatus.ShowingPlace);
        }

        static string Report(AutopilotRun run)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Autopilot run {run.Id}");
            sb.AppendLine();
            sb.AppendLine($"**Prompt:** {Shown(run)}");
            sb.AppendLine();
            sb.AppendLine($"**Verdict:** {run.Verdict}  ");
            sb.AppendLine($"**Duration:** {AutopilotText.Clock(run.Took)} · **Harnesses:** {run.Attempts} · **Policy steps:** {run.Steps.Count} · **Alerts:** {run.Alerts}");
            foreach (var alert in run.ResultAlerts)
            {
                sb.AppendLine();
                sb.AppendLine("> " + AutopilotText.Scrub(alert));
            }
            var reason = AutopilotText.Scrub(Reason(run));
            if (!string.IsNullOrEmpty(reason))
            {
                sb.AppendLine();
                sb.AppendLine("## Reason");
                sb.AppendLine();
                sb.AppendLine(reason);
            }
            var evidence = Evidence(run);
            if (evidence.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Evidence");
                sb.AppendLine();
                foreach (var e in evidence) sb.AppendLine("- " + e);
            }
            var changes = Changes(run);
            if (changes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Changed directly (not through play)");
                sb.AppendLine();
                foreach (var c in changes) sb.AppendLine("- " + c);
            }
            var notes = AgentNotes(run);
            var engine = run.Findings.Where(AutopilotRun.IsEngine).ToList();
            if (notes.Count + engine.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Findings");
                sb.AppendLine();
                foreach (var n in notes) sb.AppendLine("- [agent] " + n);
                foreach (var f in engine)
                {
                    var where = Json.Obj(f, "where");
                    var at = where != null ? $" at {Json.Num(where, "X", 0):0}, {Json.Num(where, "Y", 0):0}, {Json.Num(where, "Z", 0):0} cm" : "";
                    var count = (int)Json.Num(f, "count", 1);
                    sb.AppendLine($"- [engine · {Json.Str(f, "severity")} · {Json.Str(f, "detector")}] {AutopilotText.Scrub(Json.Str(f, "summary"))}{(count > 1 ? $" (×{count})" : "")}{at}{(run.NotABug.Contains(Json.Str(f, "signature")) ? " (not a bug)" : "")}");
                }
            }
            return sb.ToString();
        }
    }
}
