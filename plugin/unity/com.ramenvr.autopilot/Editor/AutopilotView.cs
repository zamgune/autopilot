// The Autopilot window's content (UI Toolkit): connecting, the composer, a run live (stage track, NOW, attempts, feed), its
// result (verdict, recording, evidence, findings), the feed and the policy log; the run history as a rail when the
// window is wide, a dropdown when it is narrow; the Settings page (AutopilotView.Settings.cs), the first-run setup
// (AutopilotView.Setup.cs) and, once Autopilot is removed from this computer, how that went (AutopilotView.Remove.cs).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using static Autopilot.Playtest.Editor.AutopilotUI;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Autopilot.Playtest.Tests")]

namespace Autopilot.Playtest.Editor
{
    public sealed partial class AutopilotView : VisualElement
    {
        public const float WideFrom = 700;
        /// <summary>How wide a page's content gets (the USS's max-width of each page): centred beyond it. The composer's
        /// column, the Settings page, the first-run setup, the agent service card (and the removal's page); a run's
        /// Live and Result pages take the window's width.</summary>
        public const float ComposerWidth = 620;
        public const float SettingsWidth = 640;
        public const float SetupWidth = 600;
        public const float ConnectWidth = 540;
        /// <summary>The history rail's width (wide windows).</summary>
        public const float RailWidth = 280;

        // What the window keeps across script reloads
        public string SelectedId;
        public string Tab = "result";  // result | feed | policy
        public bool Composing;
        public string Page = "runs";   // runs | settings
        public bool Wide;
        public event Action StateChanged;

        readonly VisualElement rail, railList, railFoot, main, topbar, notice, tabs, content, pop;
        VisualElement historyButton;
        readonly VisualElement railSettings;
        bool setupShown;
        bool removedShown;
        double nextSetupCheck;
        bool connectShown;
        string connectServiceKey = "";
        string setupButtonKey = "";
        string resetKeyShown = "";
        // Steer: the user's words to the run while it plays. Kept out of the content, which a live run builds again
        // on each new line, so what is being typed (and the focus) stays.
        readonly VisualElement steer, steerButton;
        readonly TextField steerField;
        readonly Label statusLine;
        AutopilotPolicyView policy;
        ListView feedList;
        Action liveTick;
        Action composerTick;
        bool dirty = true;
        string builtKey = "";
        int builtVersion = -1;
        int historyVersion = -1;
        string historyKey = "";
        string lastAutoRun;
        string liveSig = "";
        bool layoutKnown;

        public AutopilotView()
        {
            AddToClassList("autopilot");
            AddToClassList("narrow");
            if (Sheet != null) styleSheets.Add(Sheet);
            var body = Box("body");
            Add(body);

            rail = Box("rail col");
            // (the logo, History, then Settings (the gear) and New run)
            var head = Box("rail-head");
            head.Add(new Logo(14));
            head.Add(L(HistoryName, "rail-title grow nowrap"));
            railSettings = SettingsButton("small");
            head.Add(railSettings);
            head.Add(Btn("New run", NewRun, "primary small"));
            rail.Add(head);
            var railScroll = new ScrollView(ScrollViewMode.Vertical) { horizontalScrollerVisibility = ScrollerVisibility.Hidden };
            railScroll.AddToClassList("grow");
            railList = railScroll.contentContainer;
            rail.Add(railScroll);
            railFoot = Box();
            rail.Add(railFoot);
            body.Add(rail);

            main = Box("main");
            topbar = Row("topbar");
            main.Add(topbar);
            notice = Box();
            main.Add(notice);
            tabs = Box("tabs");
            main.Add(tabs);
            content = Box("content");
            main.Add(content);
            steer = Row("steer");
            steer.style.display = DisplayStyle.None;
            var steerBox = Box("steer-box");
            steer.Add(steerBox);
            // (it wraps, so its hints fit a narrow window, but it is one line of words: Enter sends, never a new line)
            steerField = Field("", true, v =>
            {
                if (v != null && (v.IndexOf('\n') >= 0 || v.IndexOf('\r') >= 0))
                    steerField.SetValueWithoutNotify(v.Replace("\r", "").Replace('\n', ' ').Trim());
                UpdateSteer();
            }, SteerHint);
            steerField.AddToClassList("grow");
            steerField.AddToClassList("steer-input");
            steerField.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            steerField.RegisterCallback<KeyDownEvent>(e =>
            {
                // Enter sends
                if (e.keyCode != KeyCode.Return && e.keyCode != KeyCode.KeypadEnter && e.character != '\n') return;
                e.StopPropagation();
                if (e.keyCode != KeyCode.None) SendSteer();
            }, TrickleDown.TrickleDown);
            // As tall as the longer of its two hints needs at its width: both fit, and the row never jumps between stages
            steerField.Q<TextElement>()?.RegisterCallback<GeometryChangedEvent>(_ => FitSteerHints());
            steerBox.Add(steerField);
            steerButton = Btn("Steer", SendSteer, "primary");
            steerButton.SetEnabled(false);
            steerBox.Add(steerButton);
            main.Add(steer);
            // The status line: the main column's last row (AutopilotStatus: a message for 8 s)
            statusLine = L("", "status-bar nowrap");
            statusLine.style.display = DisplayStyle.None;
            main.Add(statusLine);
            body.Add(main);

            pop = Box("history-pop");
            pop.style.display = DisplayStyle.None;
            Add(pop);

            RegisterCallback<GeometryChangedEvent>(e => SetWide(e.newRect.width > WideFrom));
            RegisterCallback<AttachToPanelEvent>(_ => AutopilotSession.Changed += MarkDirty);
            RegisterCallback<DetachFromPanelEvent>(_ => AutopilotSession.Changed -= MarkDirty);
            RegisterCallback<PointerDownEvent>(e =>
            {
                if (pop.style.display == DisplayStyle.Flex && e.target is VisualElement t && !pop.Contains(t) && (historyButton == null || !historyButton.Contains(t)))
                    pop.style.display = DisplayStyle.None;
            }, TrickleDown.TrickleDown);
            schedule.Execute(Tick).Every(250);
        }

        public void MarkDirty()
        {
            dirty = true;
        }

        void Changed()
        {
            StateChanged?.Invoke();
        }

        public void SetWide(bool wide)
        {
            if (layoutKnown && wide == Wide) return;
            layoutKnown = true;
            Wide = wide;
            EnableInClassList("wide", wide);
            EnableInClassList("narrow", !wide);
            Chrome(!setupShown && !removedShown);
            pop.style.display = DisplayStyle.None;
            Refresh(true);
        }

        /// <summary>The run history (rail or top bar) shown, or none of the window's frame (the first-run setup).</summary>
        void Chrome(bool on)
        {
            rail.style.display = on && Wide ? DisplayStyle.Flex : DisplayStyle.None;
            topbar.style.display = on && !Wide ? DisplayStyle.Flex : DisplayStyle.None;
            if (on) return;
            tabs.style.display = DisplayStyle.None;
            notice.style.display = DisplayStyle.None;
            pop.style.display = DisplayStyle.None;
        }

        /// <summary>The runs (the composer, a run's tabs) or the Settings page.</summary>
        public void ShowPage(string page)
        {
            Page = page == "settings" ? "settings" : "runs";
            pop.style.display = DisplayStyle.None;
            // (the account corner shows the agent service's account: asked again, as it may be stale)
            if (Page == "settings") EditorHost.Client?.AskAccount();
            Changed();
            Refresh(true);
        }

        void Tick()
        {
            DriveSignIn();
            // (removed from this computer: how it went, built again when it changes)
            if (Uninstall.Holds || removedShown) dirty |= Uninstall.Holds != removedShown || RemovedKey() != builtKey;
            else if (ShowsSetup != setupShown) dirty = true;
            else if (setupShown && EditorApplication.timeSinceStartup >= nextSetupCheck)
            {
                nextSetupCheck = EditorApplication.timeSinceStartup + 0.5;
                dirty = true;  // (the sign-in, the agent service, Aura installed meanwhile: built again when they changed)
            }
            else if (!setupShown && Page == "settings" && SettingsKey() != builtKey) dirty = true;
            else if (!setupShown && connectShown && ConnectServiceKey() != connectServiceKey) dirty = true;
            else if (!setupShown && SetupButtonKey(EditorHost.Client) != setupButtonKey) dirty = true;  // (Reset memory, greyed mid-run)
            FollowReset();
            if (!setupShown && !removedShown && ResetKey() != resetKeyShown) dirty = true;
            if (dirty) Refresh(false);
            liveTick?.Invoke();
            composerTick?.Invoke();
            UpdateSteer();
            UpdateStatus();
        }

        /// <summary>The steer row: shown while the run selected is the one going and it can be steered. The box can be
        /// typed in at every stage (what was typed stays); Steer sends only while the game plays (the Rollout), its
        /// tooltip saying so before.</summary>
        void UpdateSteer()
        {
            var run = SelectedRun();
            var cur = AutopilotSession.Current;
            var show = !setupShown && !removedShown && Page != "settings" && Tab != "policy" && !Composing && run != null && cur != null && run.Id == cur.Id && AutopilotSession.CanSteer;
            steer.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            var stopping = show && cur.Stopping;
            var now = show && !stopping && AutopilotRun.SteerableAt(cur);
            // (stopping: the box and Steer locked until the run ends)
            steer.Q(className: "steer-box")?.EnableInClassList("locked", stopping);
            if (steerField.enabledSelf == stopping) steerField.SetEnabled(!stopping);
            if (steerField.textEdition.placeholder != SteerHint) steerField.textEdition.placeholder = SteerHint;
            var tip = now || !show || stopping ? "" : SteerWaitHint;
            if (steerButton.tooltip != tip) steerButton.tooltip = tip;
            steerButton.SetEnabled(now && !string.IsNullOrWhiteSpace(steerField.value));
        }

        /// <summary>The status line: the last message (AutopilotStatus) while it is fresh, on every page but the setup
        /// and the removal's; hidden (no room taken) otherwise.</summary>
        internal void UpdateStatus()
        {
            var text = setupShown || removedShown ? "" : AutopilotStatus.Shown(EditorApplication.timeSinceStartup);
            var show = text.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            if (statusLine.style.display != show) statusLine.style.display = show;
            if (statusLine.text != text)
            {
                statusLine.text = text;
                statusLine.tooltip = text;
            }
        }

        void FitSteerHints()
        {
            var text = steerField.Q<TextElement>();
            var w = text?.contentRect.width ?? float.NaN;
            if (float.IsNaN(w) || w < 1) return;
            float Lines(string hint) => text.MeasureTextSize(hint, w, MeasureMode.AtMost, 0, MeasureMode.Undefined).y;
            var h = Mathf.Ceil(Lines(SteerHint));
            if (float.IsNaN(h) || Mathf.Abs(text.style.minHeight.value.value - h) < 0.5f) return;
            text.style.minHeight = h;
        }

        void SendSteer()
        {
            if (AutopilotSession.SteerRun(steerField.value)) steerField.value = "";
            UpdateSteer();
        }

        AutopilotRun SelectedRun()
        {
            return AutopilotSession.Find(SelectedId) ?? AutopilotSession.Latest;
        }

        static string ConnectionKey(PlaytestClient c)
        {
            if (c == null) return "none";
            return $"{c.Connected}|{c.WantConnected}|{c.ProblemCode}|{(string.IsNullOrEmpty(c.Problem) ? 0 : 1)}|{c.RunActive}|{c.LinkedBy}";
        }

        /// <summary>Bring the view up to date with the session (force: build everything again).</summary>
        public void Refresh(bool force)
        {
            dirty = false;
            if (Uninstall.Holds)
            {
                // Autopilot removed from this computer (or being removed): how it went, in place of everything else
                if (!removedShown)
                {
                    removedShown = true;
                    setupShown = false;
                    Chrome(false);
                    steer.style.display = DisplayStyle.None;
                }
                var rk = RemovedKey();
                if (force || rk != builtKey)
                {
                    builtKey = rk;
                    liveTick = null;
                    composerTick = null;
                    feedList = null;
                    policy = null;
                    BuildRemoved();
                }
                return;
            }
            if (removedShown)
            {
                removedShown = false;
                Chrome(true);
                force = true;
            }
            // The first run's offer is for a game the agent service remembers nothing about: told it remembers
            // something, the setup closes by itself
            if (SetupOpen && SetupStep == 4 && MemoryHasSomething(EditorHost.Client)) EndSetup();
            if (ShowsSetup)
            {
                // The first-run setup, in place of the window's content (built again only when it changes: what is
                // being typed stays)
                if (!setupShown)
                {
                    setupShown = true;
                    Chrome(false);
                    steer.style.display = DisplayStyle.None;
                }
                var sk = SetupKey();
                if (force || sk != builtKey)
                {
                    builtKey = sk;
                    liveTick = null;
                    composerTick = null;
                    feedList = null;
                    policy = null;
                    BuildSetup();
                }
                return;
            }
            if (setupShown)
            {
                setupShown = false;
                Chrome(true);
                force = true;
            }
            railSettings.EnableInClassList("on", Page == "settings");
            var cur = AutopilotSession.Current;
            if (cur != null && cur.Id != lastAutoRun)
            {
                // A new run: show it, live
                lastAutoRun = cur.Id;
                SelectedId = cur.Id;
                Composing = false;
                Page = "runs";
                if (Tab != "policy") Tab = "result";
                steerField.value = "";
                force = true;
                Changed();
            }
            var client = EditorHost.Client;
            var connected = client != null && client.Connected;
            var run = SelectedRun();
            var composer = Composing || run == null;
            if (!composer && Tab == "policy" && run.Steps.Count == 0) Tab = "result";

            var settings = Page == "settings";
            var hk = $"{run?.Id}|{composer}|{Wide}|{ConnectionKey(client)}|{settings}|{SetupButtonKey(client)}|{ResetKey()}";
            if (force || historyVersion != AutopilotSession.Version || hk != historyKey)
            {
                historyVersion = AutopilotSession.Version;
                historyKey = hk;
                BuildHistory(run, composer);
                BuildTabs(run, composer || settings);
                BuildNotice(connected, composer || settings, client);  // (none on the Settings page)
            }

            if (settings)
            {
                var uk = SettingsKey();
                if (force || uk != builtKey)
                {
                    var same = builtKey.StartsWith("settings|", StringComparison.Ordinal);
                    builtKey = uk;
                    builtVersion = -1;
                    liveSig = "";
                    liveTick = null;
                    composerTick = null;
                    feedList = null;
                    policy = null;
                    if (!same) content.Clear();
                    BuildSettings();
                }
                return;
            }

            var key = composer ? $"composer|{connected}|{ConnectionKey(client)}|{Wide}|{(connected ? "" : ConnectServiceKey())}" : $"run|{run.Id}|{Tab}|{Wide}|{run.Active}";
            connectShown = composer && !connected;
            if (force || key != builtKey)
            {
                builtKey = key;
                builtVersion = -1;
                liveSig = "";
                content.Clear();
                liveTick = null;
                composerTick = null;
                feedList = null;
                policy = null;
                if (composer)
                {
                    if (connected) BuildComposer(client.Url);
                    else BuildConnect(client);
                }
                else if (Tab == "feed") BuildFeed(run);
                else if (Tab == "policy")
                {
                    policy = new AutopilotPolicyView();
                    content.Add(policy);
                }
            }
            if (composer) return;
            if (Tab == "policy")
            {
                policy?.Refresh(run, Wide);
                return;
            }
            if (run.Version == builtVersion) return;
            builtVersion = run.Version;
            if (Tab == "result" && run.Active)
            {
                // Live: built again for what it shows (policy steps alone only change a count)
                // (LastActivityId: a call's result folds into a line that is there already, the count the same)
                var sig = $"{run.Feed.Count}|{run.LastActivityId}|{run.Stage}|{run.AttemptLines}|{run.Now}|{run.Steps.Count > 0}";
                if (sig == liveSig && content.childCount > 0) return;
                liveSig = sig;
            }
            if (Tab == "feed")
            {
                RefreshFeed(run);
                return;
            }
            var old = content.Q<ScrollView>();
            var offset = old != null && !run.Active ? old.scrollOffset : Vector2.zero;
            content.Clear();
            liveTick = null;
            if (run.Active) BuildLive(run, client);
            else BuildResult(run, client);
            var scroll = content.Q<ScrollView>();
            if (scroll != null && offset != Vector2.zero) scroll.schedule.Execute(() => scroll.scrollOffset = offset);
        }

        // -- run history ---------------------------------------------------------------------------------

        void BuildHistory(AutopilotRun selected, bool composer)
        {
            railList.Clear();
            pop.Clear();
            var popList = new ScrollView(ScrollViewMode.Vertical) { horizontalScrollerVisibility = ScrollerVisibility.Hidden };
            popList.style.maxHeight = 360;
            pop.Add(popList);
            var shownRows = 0;
            for (var i = AutopilotSession.Runs.Count - 1; i >= 0; --i)
            {
                var r = AutopilotSession.Runs[i];
                var on = !composer && Page != "settings" && selected != null && r.Id == selected.Id;
                // (the rail's rows alternate #0f1013 / #121316; the dropdown's lie on the menu's ground)
                railList.Add(HistoryRow(r, on, shownRows++ % 2 == 1));
                popList.Add(HistoryRow(r, on, false));
            }
            if (AutopilotSession.Runs.Count == 0) railList.Add(L("No runs yet.", "no-runs"));
            railFoot.Clear();
            railFoot.Add(SaveData());
            pop.Add(SaveData());

            topbar.Clear();
            historyButton = Row("history-button");
            historyButton.tooltip = HistoryName;
            historyButton.name = HistoryName;
            historyButton.AddManipulator(new Clickable(() =>
            {
                // (under the whole top bar: its buttons may have gone on a second line)
                var under = topbar.worldBound.yMax - worldBound.yMin;
                if (!float.IsNaN(under) && under > 0) pop.style.top = under;
                pop.style.display = pop.style.display == DisplayStyle.Flex ? DisplayStyle.None : DisplayStyle.Flex;
            }));
            if (composer || selected == null)
            {
                historyButton.Add(Tag("New", "muted"));
                historyButton.Add(L(AutopilotSession.Runs.Count == 0 ? "No runs yet" : $"{AutopilotSession.Runs.Count} run{(AutopilotSession.Runs.Count == 1 ? "" : "s")}", "mono hist-meta grow nowrap"));
            }
            else
            {
                var v = selected.Verdict;
                historyButton.Add(Tag(v, AutopilotRun.Tone(v)));
                historyButton.Add(L(Meta(selected), "mono hist-meta grow nowrap"));
            }
            historyButton.Add(new Glyph(Glyph.Shape.Chevron, 10));
            // (no runs yet: nothing to list, the button disabled at 60 %)
            historyButton.SetEnabled(AutopilotSession.Runs.Count > 0);
            topbar.Add(historyButton);
            // (Settings and New run: on a line of their own when they don't fit beside the history)
            var right = Row("head-right");
            var gear = SettingsButton("");
            gear.EnableInClassList("on", Page == "settings");
            right.Add(gear);
            right.Add(Btn("New run", NewRun, "primary"));
            topbar.Add(right);
            setupButtonKey = SetupButtonKey(EditorHost.Client);
            resetKeyShown = ResetKey();
        }

        public const string SettingsName = "Settings";
        /// <summary>The history's name: the rail's heading, the narrow dock's history button.</summary>
        public const string HistoryName = "History";

        /// <summary>The gear: the Settings page, or back to the runs from it ("on" while it is open: the accent on the
        /// hover ground).</summary>
        VisualElement SettingsButton(string size)
        {
            return IconBtn(Glyph.Shape.Gear, SettingsName, () => ShowPage(Page == "settings" ? "runs" : "settings"), "quiet settings-button " + size);
        }

        // -- Rerun setup: the first-run setup again, from its first step (Account: the login menu) ---------------------

        static string SetupButtonKey(PlaytestClient client) => $"{client != null && client.RunActive}";

        /// <summary>Whether the setup can be opened again now: not while a run goes (the setup would take the window's
        /// place, and signing in again can reconnect under it).</summary>
        static bool CanChangeAccount(PlaytestClient client) => client == null || !client.RunActive;

        public const string AccountBusyTip = "Finish or stop the run first";
        public const string RerunSetupLabel = "Rerun setup";
        public const string RerunSetupTip = "The first-run setup again: your account (Aura or BYOK), the agent service, your privacy settings";

        /// <summary>Rerun setup (the Settings page's Account card): the first-run setup from
        /// its first step. While a run goes it is greyed and opens nothing, its tooltip saying why (greyed by a class
        /// rather than disabled, so the tooltip still shows).</summary>
        VisualElement RerunSetupButton(string classes)
        {
            var b = Btn(RerunSetupLabel, RerunSetup, classes + " rerun-setup");
            var free = CanChangeAccount(EditorHost.Client);
            b.EnableInClassList("off", !free);
            b.tooltip = free ? RerunSetupTip : AccountBusyTip;
            return b;
        }

        /// <summary>The first-run setup again, from its first step; nothing while a run goes.</summary>
        public void RerunSetup()
        {
            if (Uninstall.Holds)
            {
                SetUpAgain();
                return;
            }
            if (!CanChangeAccount(EditorHost.Client)) return;
            OpenSetup();  // (it opens on step 1, Account)
        }

        static string Meta(AutopilotRun r)
        {
            return $"{AutopilotText.When(r.Started)} · {AutopilotText.Clock(r.Took)} · {Attempts(r.Attempts)}";
        }

        /// <summary>"1 attempt", "3 attempts" (a run's harness attempts).</summary>
        public static string Attempts(int n) => $"{n} attempt{(n == 1 ? "" : "s")}";

        VisualElement HistoryRow(AutopilotRun r, bool on, bool alt)
        {
            var row = Box("hist" + (on ? " on" : "") + (alt ? " alt" : ""));
            var top = Row("wrap");  // (the meta under the tag when the rail is too narrow for both)
            var v = r.Verdict;
            // (selected: #2a1b10 and a 2 px accent rule on its left, the USS's .hist.on; the verdict's tag as in any row)
            top.Add(Tag(v, AutopilotRun.Tone(v)));
            top.Add(L(Meta(r), "mono hist-meta"));
            row.Add(top);
            row.Add(L(Shown(r), "hist-title"));
            row.AddManipulator(new Clickable(() =>
            {
                SelectedId = r.Id;
                Composing = false;
                Page = "runs";
                pop.style.display = DisplayStyle.None;
                Changed();
                Refresh(true);
            }));
            return row;
        }

        static string Shown(AutopilotRun r)
        {
            if (!string.IsNullOrWhiteSpace(r.Prompt)) return AutopilotText.Short(r.Prompt.Trim(), 140);
            return FallbackPrompt(r.Kind);
        }

        /// <summary>A run with no typed words: Explore, Play a goal, Verify, Run (its kind).</summary>
        internal static string FallbackPrompt(string kind) =>
            kind == "explore" ? "Explore" : kind == "goal" ? "Play a goal" : kind == "verify" ? "Verify" : "Run";

        // Reset memory, confirmed in place in the Memory footer (the rail's, the history dropdown's): asked, going,
        // then how it went (for 8 s)
        bool resetAsk, resetGoing;
        string resetBefore = "", resetOutcome = "";
        double resetOutcomeAt = double.NegativeInfinity;

        public const string ResetQuestion = "Reset memory?";
        public const string ResetLine = "The agent forgets what it has learned about this game.";
        public const string ResettingLine = "Resetting memory…";
        public const string ResetBusyTip = "A run is going: reset memory once it is done";
        const string ResetTip = "Reset memory: the agent forgets what it has learned about this game";

        string ResetKey()
        {
            var fresh = EditorApplication.timeSinceStartup - resetOutcomeAt < AutopilotStatus.Seconds;
            return $"{resetAsk}|{resetGoing}|{(fresh ? resetOutcome : "")}";
        }

        /// <summary>Each tick: a reset asked for ends when the agent service says how it went (its memory_cleared).</summary>
        void FollowReset()
        {
            if (!resetGoing) return;
            var client = EditorHost.Client;
            var said = client?.LastStatus ?? "";
            if (said != resetBefore && (said.StartsWith("Memory reset", StringComparison.Ordinal) || said.StartsWith("Memory not reset", StringComparison.Ordinal)))
            {
                resetGoing = false;
                resetOutcome = said;
                resetOutcomeAt = EditorApplication.timeSinceStartup;
            }
            else if (client == null || !client.Connected)
            {
                resetGoing = false;
                resetOutcome = "Memory not reset: the connection to the agent service closed";
                resetOutcomeAt = EditorApplication.timeSinceStartup;
            }
        }

        /// <summary>Reset memory… (the footer's button, the window tab's menu): the confirm in place; when it can't be
        /// now, the status line says why.</summary>
        public void AskResetMemory()
        {
            var client = EditorHost.Client;
            if (client == null || !client.Connected)
            {
                AutopilotStatus.Say(AutopilotStatus.ResetOffline);
                return;
            }
            if (client.RunActive)
            {
                AutopilotStatus.Say(AutopilotStatus.ResetBusy);
                return;
            }
            resetAsk = true;
            if (!Wide) pop.style.display = DisplayStyle.Flex;  // (narrow: the confirm is in the history dropdown)
            Refresh(true);
        }

        void ConfirmResetMemory()
        {
            resetAsk = false;
            var client = EditorHost.Client;
            if (client == null || !client.Connected || client.RunActive)
            {
                AutopilotStatus.Say(client != null && client.RunActive ? AutopilotStatus.ResetBusy : AutopilotStatus.ResetOffline);
                Refresh(true);
                return;
            }
            resetGoing = true;
            resetOutcome = "";
            client.ClearMemory();
            // (its own "Resetting the memory..." said: the reply is the next "Memory (not) reset" after it, even when it
            // is worded as the last one was)
            resetBefore = client.LastStatus ?? "";
            Refresh(true);
        }

        /// <summary>The Memory footer: MEMORY, what it remembers and Reset memory…, a line under it while a reset goes
        /// and how it went; or, asked, the confirm in its place.</summary>
        VisualElement SaveData()
        {
            var box = Box("savedata-box");
            var client = EditorHost.Client;
            if (resetAsk)
            {
                box.AddToClassList("asking");
                var ask = Card("plate confirm stack-6 reset-confirm");
                ask.Add(L(ResetQuestion, "body-500"));
                ask.Add(Para(ResetLine, "small t-sec2"));
                var buttons = Box("buttons");
                buttons.style.paddingTop = 2;
                buttons.Add(Btn("Reset memory", ConfirmResetMemory, "small danger-fill reset-yes"));
                buttons.Add(Btn("Cancel", () =>
                {
                    resetAsk = false;
                    Refresh(true);
                }, "quiet small"));
                ask.Add(buttons);
                box.Add(ask);
                return box;
            }
            // (one line: what it remembers shortens, with an ellipsis, so it never runs under the button)
            var row = Row("savedata");
            row.tooltip = MemoryTip;
            row.Add(Lbl("Memory", "savedata-name"));
            var shown = SaveDataText(client);
            if (shown.Length > 0) row.Add(L("· " + shown, "savedata-line nowrap"));
            row.Add(Spacer());
            var reset = Btn("Reset memory…", AskResetMemory, "small danger savedata-reset");
            // (not connected, a run going, a reset going: greyed; pressed, the status line says why)
            var busy = client != null && client.RunActive;
            reset.EnableInClassList("off", client == null || !client.Connected || busy || resetGoing);
            reset.SetEnabled(!resetGoing);
            reset.tooltip = busy ? ResetBusyTip : ResetTip;
            row.Add(reset);
            box.Add(row);
            if (resetGoing) box.Add(Progress(ResettingLine, "small t-sec2 reset-going"));
            else if (EditorApplication.timeSinceStartup - resetOutcomeAt < AutopilotStatus.Seconds && resetOutcome.Length > 0)
                box.Add(Para(resetOutcome, "small reset-outcome " + (resetOutcome.StartsWith("Memory reset", StringComparison.Ordinal) ? "t-lime" : "t-red")));
            return box;
        }

        const string MemoryTip = "What the agent remembers about this game, kept between runs: its earlier runs, its notes on how the game works, what it built to play it. It is what makes later runs better: they are faster and play better with it. “Empty”: the next run starts as on a new game.";

        /// <summary>What the agent service remembers about this game (its memory_state), or "" when it never said.</summary>
        static string SaveDataText(PlaytestClient client)
        {
            if (client == null || !client.MemoryKnown) return "";  // (an agent service that doesn't say)
            var n = client.MemoryRuns;
            if (n == 0 && !client.MemoryNotes && !client.MemoryLib) return "empty";
            return $"{n} run{(n == 1 ? "" : "s")}";
        }

        void NewRun()
        {
            Composing = true;
            Page = "runs";
            pop.style.display = DisplayStyle.None;
            Changed();
            Refresh(true);
        }

        // -- tabs ----------------------------------------------------------------------------------------------

        void BuildTabs(AutopilotRun run, bool composer)
        {
            tabs.Clear();
            tabs.style.display = composer ? DisplayStyle.None : DisplayStyle.Flex;
            if (composer || run == null) return;
            tabs.Add(TabButton("result", run.Active ? "Live" : "Result"));
            tabs.Add(TabButton("feed", "Feed"));
            if (run.Steps.Count > 0) tabs.Add(TabButton("policy", $"Policy log · {run.Steps.Count}"));
            if (!Wide) return;
            tabs.Add(Spacer());
            tabs.Add(L(IdLine(run), "idline nowrap"));
            // (while the run plays, with the agent service on this computer)
            if (run.Active && IsLocal(EditorHost.Client?.Url)) tabs.Add(DotLabel("LOCAL AGENT", Success, "close local-agent"));
        }

        /// <summary>Whether the agent service's address is this computer (127.0.0.1, localhost, ::1).</summary>
        internal static bool IsLocal(string url)
        {
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
            var host = u.Host.Trim('[', ']');
            return host == "127.0.0.1" || host == "localhost" || host == "::1";
        }

        /// <summary>"RUN 20261020-132501 // GOAL // DURATION 01:23 // 2 ATTEMPTS" (the tabs' row, wide windows).</summary>
        public static string IdLine(AutopilotRun run) =>
            $"RUN {run.Id} // {KindWord(run)} // DURATION {AutopilotText.Clock(run.Took)} // {Attempts(run.Attempts).ToUpperInvariant()}";

        Label TabButton(string id, string text)
        {
            var t = L(text, "ptab" + (Tab == id ? " on" : ""));
            t.AddManipulator(new Clickable(() =>
            {
                Tab = id;
                Changed();
                Refresh(true);
            }));
            return t;
        }

        static string KindWord(AutopilotRun r)
        {
            switch (r.Kind)
            {
                case "verify": return "VERIFY";
                case "goal": return "GOAL";
                case "explore": return "EXPLORE";
                default: return "RUN";
            }
        }

        void BuildNotice(bool connected, bool composer, PlaytestClient client)
        {
            notice.Clear();
            if (connected || composer)
            {
                notice.style.display = DisplayStyle.None;
                return;
            }
            notice.style.display = DisplayStyle.Flex;
            var row = Row("notice");
            var trying = client != null && client.WantConnected;
            row.Add(L(trying ? "Reconnecting to the agent service…" : "Not connected to the agent service", "notice-text grow"));
            // (trying: what is going on, on the connect card; disconnected by hand: connect again)
            if (trying)
                row.Add(Btn("Details", () =>
                {
                    Composing = true;
                    Changed();
                    Refresh(true);
                }, "small"));
            else row.Add(Btn("Reconnect", EditorHost.Connect, "small"));
            notice.Add(row);
        }

        // -- connecting ----------------------------------------------------------------------------------------

        void BuildConnect(PlaytestClient client)
        {
            var s = PlaytestSettings.Get();
            var page = PageBox("connect-page");
            var plate = Card("plate connect");
            page.Add(plate);
            var card = Box("connect-body stack-10");
            plate.Add(card);

            // What this is and where it runs: Autopilot's own agent service (autopilot serve), or the Aura app's
            var head = Row("wrap");
            head.Add(L("Agent service", "big"));
            var tag = Tag("Not connected", "muted");
            tag.style.marginLeft = 8;
            head.Add(tag);
            card.Add(head);
            card.Add(s.UseWithAura ? Para(AuraServiceLine, "small t-sec2 connect-where") : ServeLine());

            var batch = Application.isBatchMode || !string.IsNullOrEmpty(PlaytestClient.Arg("Playtest"));
            // (about to connect by itself: as good as trying)
            var want = (client != null && client.WantConnected)
                       || EditorHost.ConnectsByItself(Preferences.Current.Onboarded, EditorHost.DisconnectedByHand, Uninstall.Holds, batch, client);
            var problem = client?.Problem ?? "";
            var buttons = Box("buttons");
            connectServiceKey = ConnectServiceKey();
            var disconnected = false;  // (Back to the last run: on this state only)
            var starts = AgentService.StartsWithConnect(s);
            var phase = AgentService.Current;
            if (client != null && client.Linked)
            {
                // Another plugin carries the connection (HostLink): it links again once its code is back from a
                // script reload. Stop: the editor's own connection again
                card.Add(Working($"Waiting for {HostLink.DisplayName(client.LinkedBy)}…"));
                buttons.Add(Btn("Stop", EditorHost.Disconnect, "quiet"));
            }
            else if (starts && want && phase != AgentService.Phase.Running && phase != AgentService.Phase.Idle)
            {
                // The agent service is being set up or started for this connection, or can't be
                switch (phase)
                {
                    case AgentService.Phase.Installing:
                        card.Add(Working("Installing the agent service…"));
                        break;
                    case AgentService.Phase.Starting:
                        card.Add(Working("Starting the agent service…"));
                        break;
                    case AgentService.Phase.NotInstalled:
                        card.Add(Problem("amber", "The agent service isn’t installed on this computer."));
                        buttons.Add(Btn("Install", AgentService.Install, "primary btn-space"));
                        break;
                    case AgentService.Phase.NoInstaller:
                        card.Add(Problem("amber", "No agent service found.", "Set Agent service command under Advanced, or run autopilot serve."));
                        buttons.Add(Btn("Try again", () =>
                        {
                            AgentService.TryAgain();
                            EditorHost.Connect();
                        }, "primary btn-space"));
                        break;
                    default:
                        // (the board's headline; what the agent service said under it, in mono)
                        card.Add(Problem("red", ServiceDidntStart, string.IsNullOrEmpty(AgentService.Problem) ? null : AutopilotText.Breakable(AgentService.Problem)));
                        buttons.Add(Btn("Try again", () =>
                        {
                            AgentService.TryAgain();
                            EditorHost.Connect();
                        }, "primary btn-space"));
                        buttons.Add(Btn(ViewLogs, AgentService.OpenLog, "btn-space"));
                        break;
                }
                buttons.Add(Btn("Stop", EditorHost.Disconnect, "quiet"));
            }
            else if (!want && !string.IsNullOrEmpty(problem))
            {
                // The agent service refused the connection (an error frame): its words, as it sent them
                card.Add(Problem("red", client != null && (client.ProblemCode == 401 || client.ProblemCode == 403) ? RefusedLine : AutopilotText.Scrub(problem)));
                buttons.Add(Btn("Try again", EditorHost.Connect, "primary btn-space"));
            }
            else if (want && !string.IsNullOrEmpty(problem) && client != null && client.WithAura)
            {
                // With Aura: it isn't running (or isn't installed) on this machine
                var installed = Aura.IsInstalled();
                card.Add(installed
                    ? Problem("amber", "Aura isn’t running on this machine.", "Autopilot connects once it starts.")
                    : Problem("amber", "Aura isn’t installed on this machine."));
                if (installed)
                    buttons.Add(Btn("Start Aura", () =>
                    {
                        if (!Aura.Launch()) AutopilotStatus.Say(AutopilotStatus.AuraDidntStart);
                    }, "btn-space"));
                else buttons.Add(Btn("Get Aura", () => Application.OpenURL(Aura.DownloadUrl), "btn-space"));
                buttons.Add(Btn("Try now", EditorHost.Connect, "primary btn-space"));
                buttons.Add(Btn("Stop", EditorHost.Disconnect, "quiet"));
            }
            else if (want && !string.IsNullOrEmpty(problem))
            {
                // (the address it is trying, under the headline)
                var detail = client?.Url ?? s.Url;
                card.Add(Problem("amber", "Can’t reach the agent service, retrying", detail));
                buttons.Add(Btn("Try now", EditorHost.Connect, "primary btn-space"));
                buttons.Add(Btn("Stop", EditorHost.Disconnect, "quiet"));
            }
            else if (want)
            {
                card.Add(Working("Connecting to the agent service…"));
                buttons.Add(Btn("Stop", EditorHost.Disconnect, "quiet"));
            }
            else
            {
                // Disconnected by hand (it never connects by itself again in this editor session)
                buttons.Add(Btn("Reconnect", EditorHost.Connect, "primary btn-space"));
                disconnected = true;
            }
            card.Add(buttons);

            // Advanced: another address, an access token, the command that starts it (folded, under a rule)
            var advanced = new Foldout { text = "ADVANCED", value = false };
            advanced.AddToClassList("advanced");
            VisualElement Labelled(string label, VisualElement field)
            {
                var box = Box("keyfield");
                box.Add(Lbl(label, "field-label"));
                box.Add(field);
                advanced.Add(box);
                return box;
            }
            var url = Field(s.Url, false, v =>
            {
                s.Url = v.Trim();
                s.Save();
            });
            url.AddToClassList("mono-field");
            url.tooltip = AgentUrlTip;
            Labelled("Agent service URL", url);
            var token = Field(s.Token, false, v =>
            {
                s.Token = v.Trim();
                s.Save();
            });
            token.isPasswordField = true;
            Labelled("Access token · optional", token);
            var command = Field(s.ServiceCommand, false, v =>
            {
                s.ServiceCommand = v.Trim();
                s.Save();
            }, "Detected automatically");
            command.tooltip = AgentCommandTip;
            Labelled("Agent service command · optional", command).style.marginBottom = 0;
            plate.Add(advanced);

            if (disconnected && AutopilotSession.Find(SelectedId) != null)  // (a run was shown before)
            {
                var back = Row("back-row");
                back.Add(Btn("Back to the last run", () =>
                {
                    Composing = false;
                    Changed();
                    Refresh(true);
                }, "quiet small"));
                page.Add(back);
            }
        }

        public const string ViewLogs = "View logs";
        public const string ServiceDidntStart = "The agent service didn’t start.";
        public const string RefusedLine = "The agent service refused the connection: the access token is wrong.";
        public const string AgentUrlTip = "Empty: ws://127.0.0.1:8765/v1/bridge, the agent service on this computer. You may use your own endpoint.";
        public const string AgentCommandTip = @"The command that runs the agent service, without “serve”: e.g. C:\autopilot\.venv\Scripts\python.exe -m autopilot";
        public const string AuraServiceLine = "Start the Aura app: it runs the agent service.";

        /// <summary>"Start the agent service with `autopilot serve`", the command in the code style.</summary>
        static VisualElement ServeLine()
        {
            var row = Row("wrap connect-where");
            row.Add(L("Start the agent service with", "small t-sec2"));
            row.Add(L("autopilot serve", "code"));
            return row;
        }

        /// <summary>A page's content in the content area: scrolls vertically when short, never sideways, and is at
        /// at most its max width (ComposerWidth, SettingsWidth, SetupWidth, ConnectWidth; a run's pages none), centred (the USS's .page). steady: the scroll bar's room is kept whether the page scrolls
        /// or not (the bar shows only when it does), so pages shown one after another (the setup's steps) have one
        /// column, whatever each one holds.</summary>
        VisualElement PageBox(string classes = "", bool steady = false)
        {
            var scroll = new ScrollView(ScrollViewMode.Vertical) { horizontalScrollerVisibility = ScrollerVisibility.Hidden };
            scroll.AddToClassList("scroll");
            if (steady)
            {
                scroll.verticalScrollerVisibility = ScrollerVisibility.AlwaysVisible;
                void ShowBar(GeometryChangedEvent e)
                {
                    var scrolls = scroll.contentContainer.layout.height > scroll.contentViewport.layout.height + 0.5f;
                    scroll.verticalScroller.style.visibility = scrolls ? Visibility.Visible : Visibility.Hidden;
                }
                scroll.contentContainer.RegisterCallback<GeometryChangedEvent>(ShowBar);
                scroll.contentViewport.RegisterCallback<GeometryChangedEvent>(ShowBar);
            }
            content.Add(scroll);
            var page = Box("page " + classes);
            scroll.Add(page);
            return page;
        }

        static string ConnectServiceKey() => $"{AgentService.Current}|{AgentService.Version}";

        /// <summary>A tinted box with the problem (and, under it, a mono detail: the address, what to do).</summary>
        static VisualElement Problem(string tone, string text, string detail = null)
        {
            var p = Box("problem " + tone);
            p.Add(L(text, "body-500"));
            if (!string.IsNullOrEmpty(detail)) p.Add(L(detail, "mono problem-detail"));
            return p;
        }

        /// <summary>Something going (installing, starting, connecting): a spinner and the words, in the info tint.</summary>
        static VisualElement Working(string text)
        {
            var p = Box("problem info");
            p.Add(Progress(text, ""));
            return p;
        }

        // -- the composer ------------------------------------------------------------------------------------

        /// <summary>A header bar over the page (New run, Settings): its title at 15 / 500, a rule under it, and what
        /// goes at its right.</summary>
        VisualElement PageHead(string title, VisualElement right = null)
        {
            var head = Box("page-head");
            head.Add(L(title, "page-title nowrap"));
            if (right != null) head.Add(right);
            content.Add(head);
            return head;
        }

        static readonly string[] RunClientIds = { "", CodingAgents.Claude, CodingAgents.Codex };
        static readonly List<string> RunClientNames = new List<string> { "Configured agent", "Claude Code CLI", "Codex CLI" };

        static VisualElement RunClientField(Action changed = null)
        {
            var settings = PlaytestSettings.Get();
            var box = Box("keyfield");
            box.Add(Lbl("Run with", "field-label"));
            var pick = new DropdownField(RunClientNames, Math.Max(0, Array.IndexOf(RunClientIds, settings.CliClient)))
            {
                tooltip = "CLI options use your local login. Jev chooses game actions."
            };
            pick.AddToClassList("key-pick");
            pick.AddToClassList("run-client");
            pick.RegisterValueChangedCallback(e =>
            {
                settings.CliClient = RunClientIds[pick.index];
                settings.Save();
                changed?.Invoke();
            });
            box.Add(pick);
            return box;
        }

        void BuildComposer(string clientUrl)
        {
            var s = PlaytestSettings.Get();
            PageHead("New run");
            var page = PageBox("composer-page stack-10");
            page.Add(RunClientField());

            // One box (a well): the prompt; under it the Time limit and Run at the row's right (the kind of run is the
            // agent service's to read from the words: its run_kind says it once the run starts)
            var composer = Box("composer");
            var runRow = Row("run-row");
            runRow.Add(Spacer());
            var minutesRow = Row("minutes-row");
            minutesRow.tooltip = TimeLimitTip;
            minutesRow.Add(Lbl(TimeLimitTitle, "minutes-key"));
            // (kept per project, PlaytestSettings.TimeLimitMinutes: the composer starts with the one last set)
            var minutes = NumField(PlaytestSettings.ClampTimeLimit(s.TimeLimitMinutes), "minutes-field");
            minutes.RegisterValueChangedCallback(e =>
            {
                // (1 to 600 minutes)
                var kept = PlaytestSettings.ClampTimeLimit(e.newValue);
                if (kept != e.newValue) minutes.SetValueWithoutNotify(kept);
                if (s.TimeLimitMinutes == kept) return;
                s.TimeLimitMinutes = kept;
                s.Save();
            });
            minutesRow.Add(minutes);
            runRow.Add(minutesRow);
            var runButton = Btn("Run", () => StartFromComposer(), "primary run-button");
            runRow.Add(runButton);

            var prompt = Field(s.Prompt, true, v =>
            {
                s.Prompt = v;
                s.Save();
            }, PromptPlaceholder);
            prompt.AddToClassList("prompt-input");
            composer.Add(prompt);
            composer.Add(runRow);
            // Over the box: Aura's words while the team has no credits left for a run, with Add credits when Aura names
            // where (Run stays: Aura checks again when a run starts); gone once an account frame says none
            var credits = Box("credits-slot");
            string creditsShown = null;
            page.Add(credits);
            page.Add(composer);

            var hint = Para(BusyLine, "busy-line");
            page.Add(hint);
            composerTick = () =>
            {
                var c = EditorHost.Client;
                var notice = CreditsNotice(c?.Account);
                var said = notice == null ? "" : notice.Message + "\n" + notice.Url;
                if (said != creditsShown)
                {
                    creditsShown = said;
                    credits.Clear();
                    if (notice != null) credits.Add(OutOfCredits(notice));
                    credits.style.display = notice != null ? DisplayStyle.Flex : DisplayStyle.None;
                }
                var busy = c != null && c.RunActive;
                var cliProblem = !string.IsNullOrEmpty(s.CliClient) ? AutopilotSession.StartProblem(c, s.CliClient) : null;
                runButton.SetEnabled(!string.IsNullOrWhiteSpace(s.Prompt) && cliProblem == null);
                runButton.EnableInClassList("off", c == null || !c.Connected || busy);
                hint.text = busy ? BusyLine : cliProblem ?? "";
                var show = hint.text.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
                if (hint.style.display != show) hint.style.display = show;
            };
            composerTick();

            // The link and Disconnect: one row at the right under a rule (the address shortens when narrow, so the two
            // never split)
            var foot = Row("link-row");
            foot.Add(Spacer());
            foot.Add(DotLabel("CONNECTED", Success, "link"));
            foot.Add(L(AutopilotText.Short(clientUrl, 60), "mono tiny t-muted nowrap link-url"));
            foot.Add(Btn("Disconnect", EditorHost.Disconnect, "quiet small"));
            page.Add(foot);
        }

        /// <summary>The red callout over the composer: Aura's words, and Add credits under them when Aura names where.</summary>
        static VisualElement OutOfCredits(AccountCredits credits)
        {
            var p = Box("problem red credits");
            p.Add(Para(AutopilotText.Breakable(credits.Message), "credits-words"));
            if (credits.Url.Length > 0) p.Add(AddCreditsButton(credits.Url, "small"));
            return p;
        }

        /// <summary>The first run's Minutes: at most 600 minutes.</summary>
        public const int MaxMinutes = 600;

        public const string BusyLine = "A run is going: stop it or wait for its result.";

        // -- tests: a page built as the window builds it, without a connection ---------------------------------------

        internal void TestShowComposer(string url)
        {
            ClearContent();
            BuildComposer(url);
        }

        internal void TestShowConnect()
        {
            ClearContent();
            BuildConnect(null);
        }

        internal void TestShowRun(AutopilotRun run)
        {
            ClearContent();
            if (run.Active) BuildLive(run, null);
            else BuildResult(run, null);
        }

        /// <summary>The steer row brought up to date now (the window does it on its tick).</summary>
        internal void TestUpdateSteer() => UpdateSteer();

        /// <summary>The composer brought up to date now (the window does it on its tick).</summary>
        internal void TestTickComposer() => composerTick?.Invoke();

        void ClearContent()
        {
            content.Clear();
            liveTick = null;
            composerTick = null;
            feedList = null;
            policy = null;
            builtKey = "test";
        }

        public void StartFromComposer()
        {
            var frame = ComposerFrame(PlaytestSettings.Get(), out var shown);
            if (AutopilotSession.Start(frame, shown))
            {
                Composing = false;
                Tab = "result";
                Changed();
            }
            Refresh(true);
        }

        public const string PromptPlaceholder = "Describe what to test: a claim to verify, a goal to reach, or what to explore";
        /// <summary>The composer's Time limit: its name and tooltip.</summary>
        public const string TimeLimitTitle = "Time limit";
        public const string TimeLimitTip = "The whole time the run may take, in minutes; an exploration plays for it";
        public const string SteerHint = "Send a steering prompt to Autopilot";
        /// <summary>Steer's tooltip before the Rollout.</summary>
        public const string SteerWaitHint = "You can steer once the game is playing";

        /// <summary>The run the composer's text asks for (always real time: no turn_based), and how it is shown; with
        /// the Design Brief setting when it says anything.</summary>
        public static Dictionary<string, object> ComposerFrame(PlaytestSettings s, out string shown)
        {
            var frame = ComposerFrame(s.Prompt, s.TimeLimitMinutes, s.DesignBrief, out shown);
            if (!string.IsNullOrEmpty(s.CliClient)) frame["cli_client"] = s.CliClient;
            return frame;
        }

        /// <summary>The run the text asks for, its kind the agent service's to read (kind "auto": its run_kind says
        /// what it read), with the Time limit (<paramref name="timeLimitMinutes"/>, as time_limit_minutes; 0: none,
        /// the agent service's own). Shown as typed.</summary>
        public static Dictionary<string, object> ComposerFrame(string text, int timeLimitMinutes, out string shown)
        {
            return ComposerFrame(text, timeLimitMinutes, "", out shown);
        }

        /// <summary>As above, with the Design Brief (<paramref name="designBrief"/>: how the game is meant to work) as
        /// design_brief when it says anything, as Unreal's composer sends it.</summary>
        public static Dictionary<string, object> ComposerFrame(string text, int timeLimitMinutes, string designBrief, out string shown)
        {
            var t = (text ?? "").Trim();
            shown = t;  // (the typed words, never rewritten)
            var frame = new Dictionary<string, object> { ["kind"] = "auto", ["prompt"] = t };
            if (timeLimitMinutes > 0) frame["time_limit_minutes"] = PlaytestSettings.ClampTimeLimit(timeLimitMinutes);
            var brief = (designBrief ?? "").Trim();
            if (brief.Length > 0) frame["design_brief"] = brief;
            return frame;
        }

        /// <summary>An Explore run of <paramref name="minutes"/> minutes of play (its kind said in the frame: the agent
        /// service doesn't read it from words), with the Design Brief as its brief when it says anything, as Unreal's.</summary>
        public static Dictionary<string, object> ExploreFrame(int minutes, string designBrief, string cliClient = "")
        {
            var frame = new Dictionary<string, object> { ["kind"] = "explore", ["minutes"] = Math.Max(1, minutes) };
            var brief = (designBrief ?? "").Trim();
            if (brief.Length > 0) frame["brief"] = brief;
            if (!string.IsNullOrEmpty(cliClient)) frame["cli_client"] = cliClient;
            return frame;
        }

        /// <summary>Run again's frame: a copy of the run's own, as it was (its time limit too, not the composer's
        /// now; an exploration plays for its minutes); null without one.</summary>
        public static Dictionary<string, object> AgainFrame(Dictionary<string, object> runFrame)
        {
            return runFrame == null ? null : Json.Parse(Json.Write(runFrame)) as Dictionary<string, object>;
        }
    }

    /// <summary>The window's status line: one short message (what a click did, or why it couldn't), shown on every
    /// page's last row until the next one or 8 s (AutopilotView.UpdateStatus).</summary>
    public static class AutopilotStatus
    {
        public const double Seconds = 8;
        static string text = "";
        static double at = double.NegativeInfinity;

        public static void Say(string message)
        {
            text = message ?? "";
            at = EditorApplication.timeSinceStartup;
        }

        /// <summary>The message while it is fresh at <paramref name="now"/>, else "".</summary>
        public static string Shown(double now) => now - at < Seconds ? text : "";

        /// <summary>Tests: the message as if said <paramref name="seconds"/> ago.</summary>
        internal static void TestAge(double seconds) => at = EditorApplication.timeSinceStartup - seconds;

        // The words (the same in every engine's plugin)
        public const string CopiedReport = "Copied the report";
        public const string Copied = "Copied";
        public const string FeedbackOffline = "Not connected: connect to send feedback";
        public const string AuraDidntStart = "Couldn’t start Aura";
        public const string RunOffline = "Not connected: connect first";
        public const string RunBusy = "A run is going: wait for its result, or stop it";
        public const string CliUpdate = "Install the agent service supplied with this Unity package to run a signed-in CLI.";
        public const string CliChecking = "Waiting for the agent service to confirm CLI support.";
        public const string ResetOffline = "Not connected: connect first to reset the memory";
        public const string ResetBusy = "A run is going: reset the memory once it is done";
        public const string NoScene = "Open the scene to show this finding in it";
        /// <summary>A bundled font couldn't be read (AutopilotUI.FontsFound): the editor's font is drawn instead.</summary>
        public const string FontMissing = "A bundled font couldn’t be read: using the editor’s font";
        public const string NothingNear = "Nothing in the open scene near that place";
        public const string ShowingPlace = "Showing the place in the open scene";
        public static string NotABug(string name) => $"Told the agent: {name} is not a bug";
        public static string Showing(string name) => $"Showing {name}";
        public static string UsageNotSaved(string path, int error) => $"Couldn’t save the usage to {path} (error {error})";
        public static string SetupNotSaved(string path, int error) => $"Couldn’t save the setup to {path} (error {error})";
    }
}
