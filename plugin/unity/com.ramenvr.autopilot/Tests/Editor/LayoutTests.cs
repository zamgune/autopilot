// The Autopilot window's pages laid out at several window sizes (narrow docks to wide windows, tall and short), in an
// editor panel of their own (UI Toolkit lays out in batch mode, -nographics too): nothing runs out of the window or out
// of the box it is in, no text runs out of its label, a page's content is at most 560 px wide and centred beyond it,
// and only its ScrollView is taller than the window. Each page's words are checked against the design's screen copy
// on the way (BYOK, never "own keys"; Autopilot's bills on the Settings page, long ones too; out of Aura credits, Aura's
// words over the composer and on the account card, Add credits when Aura names where). Also: the BYOK Coding Agent's
// dropdown and the LLM provider's (on one row with its key, locked for Claude Code and Codex, preselected, saved; both
// keys' placeholders fitting their inputs, checked on every page), the account asked for again
// after the setup (the stale "not signed in" Settings page), runs always real time, and Reset memory. And the dogfooding
// round's: Settings as a gear in the run history's head, Rerun setup on the Settings page (the setup from its first step,
// greyed mid-run), the run history's selected row (a faint tint, its words readable), the composer's one box (its
// placeholder fitting, its kind's tag following the words, Explore's duration at the row's right),
// the Live page's stage names never cut at any width, the memory line in the feed, the run's recording on its result
// (or, with none, the line saying why, wrapping in its place), no findings section without findings, and the steer box (shown at every stage of a run going, usable only at the Rollout, its waiting hint fitting).
// And Remove Autopilot: the Settings page's last box, confirmed in place, and the pages saying how the removal went. And
// the cleanup round's: no explainer paragraphs, the connect card's states (no plain Connect: it connects by itself; a
// Disconnect, Reconnect), its Advanced fields, "View logs", "N attempts", the data-sharing disclosure. And the setup's
// four steps in one column, the run's id line with Run again, Copy report and Run folder as icons on its row (the
// prompt close under it), and the composer's link on Disconnect's row.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autopilot.Playtest.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Autopilot.Playtest.Tests
{
    public class LayoutTests
    {
        static readonly Vector2Int[] Sizes =
        {
            new Vector2Int(320, 900), new Vector2Int(360, 640), new Vector2Int(560, 480), new Vector2Int(900, 400), new Vector2Int(1400, 900),
        };

        const string Email = "you@studio.test";
        const string ServiceUrl = "ws://127.0.0.1:51734/v1/bridge";
        const string InstallProblem = @"Couldn't install the agent service (see C:\Users\someone\AppData\Local\Autopilot\logs\install-20261013-141502.log)";

        ScriptableObject owner;
        IDisposable panelHandle;
        IPanel panel;
        MethodInfo validate;
        VisualElement host;
        string folderWas, folder;
        PlaytestClient clientWas;
        PlaytestClient client;
        readonly List<string> sent = new List<string>();
        bool useWithAuraWas;
        string promptWas, cliClientWas;
        int minutesWas;
        int timeLimitWas;

        [SetUp]
        public void SetUp()
        {
            folderWas = Aura.FolderOverride;
            folder = Path.Combine(Path.GetTempPath(), "autopilot-layout-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            Aura.FolderOverride = folder;  // (preferences.json, brain.json: never the user's own)
            AutopilotUsage.Flush();
            AutopilotUsage.LedgerOverride = Path.Combine(folder, "AutopilotUsage.json");  // (the project's usage ledger: the test's own)
            AutopilotUsage.TestReload();
            notSaid.Clear();
            Preferences.Update(new Dictionary<string, object> { ["onboarded"] = true });
            clientWas = EditorHost.Client;
            useWithAuraWas = PlaytestSettings.Get().UseWithAura;
            PlaytestSettings.Get().UseWithAura = false;
            promptWas = PlaytestSettings.Get().Prompt;
            cliClientWas = PlaytestSettings.Get().CliClient;
            PlaytestSettings.Get().CliClient = "";
            minutesWas = PlaytestSettings.Get().ExploreMinutes;
            timeLimitWas = PlaytestSettings.Get().TimeLimitMinutes;

            owner = ScriptableObject.CreateInstance<StyleSheet>();
            owner.hideFlags = HideFlags.HideAndDontSave;
            var create = typeof(VisualElement).Assembly.GetType("UnityEngine.UIElements.Panel")?.GetMethod("CreateEditorPanel", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (create == null) Assert.Inconclusive("This Unity has no Panel.CreateEditorPanel: the layout can't be computed here");
            var p = create.Invoke(null, new object[] { owner });
            panel = (IPanel)p;
            panelHandle = p as IDisposable;
            validate = FindMethod(p.GetType(), "ValidateLayout");
            Assert.IsNotNull(validate, "ValidateLayout");
            host = new VisualElement();
            host.style.position = Position.Absolute;
            host.style.left = 0;
            host.style.top = 0;
            panel.visualTree.Add(host);
            Uninstall.RealRunsBlocked = true;  // (never the real uninstall: only a stand-in in the temporary folder)
            // The editor's default theme, as a window has it (Toggle, TextField and ScrollView take their sizes from it)
            var util = typeof(UnityEditor.UIElements.PropertyField).Assembly.GetType("UnityEditor.UIElements.UIElementsEditorUtility");
            var addDefault = util?.GetMethod("AddDefaultEditorStyleSheets", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(VisualElement) }, null);
            addDefault?.Invoke(null, new object[] { host });
        }

        [TearDown]
        public void TearDown()
        {
            AutopilotSession.Runs.RemoveAll(r => r.Id != null && r.Id.StartsWith("layout-", StringComparison.Ordinal));
            if (client != null)
            {
                client.Disconnect();
                client.TestWire = null;
            }
            client = null;
            EditorHost.TestClient(clientWas);
            AgentService.TestState(AgentService.Phase.Idle);
            Uninstall.TestState(Uninstall.Phase.None);
            PlaytestSettings.Get().UseWithAura = useWithAuraWas;
            Uninstall.RealRunsBlocked = false;
            var settings = PlaytestSettings.Get();
            if (settings.Prompt != promptWas || settings.ExploreMinutes != minutesWas || settings.TimeLimitMinutes != timeLimitWas || settings.CliClient != cliClientWas)
            {
                settings.Prompt = promptWas;
                settings.CliClient = cliClientWas;
                settings.ExploreMinutes = minutesWas;
                settings.TimeLimitMinutes = timeLimitWas;
                settings.Save();
            }
            Aura.InstalledOverride = null;
            host?.RemoveFromHierarchy();
            panelHandle?.Dispose();
            if (owner != null) UnityEngine.Object.DestroyImmediate(owner);
            Aura.FolderOverride = folderWas;
            AutopilotUsage.LedgerOverride = null;
            AutopilotUsage.TestReload();
            Preferences.Update(new Dictionary<string, object>(), Path.Combine(folder, "unused.json"));  // (forgets the cached preferences)
            try
            {
                Directory.Delete(folder, true);
            }
            catch (Exception)
            {
                // (left in the temp folder)
            }
        }

        static MethodInfo FindMethod(Type t, string name)
        {
            for (; t != null; t = t.BaseType)
            {
                var m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                if (m != null) return m;
            }
            return null;
        }

        // -- a client with no socket ----------------------------------------------------------------------------

        PlaytestClient Client()
        {
            if (client != null) return client;
            client = new PlaytestClient(EditorHost.Instance) { TestWire = s => sent.Add(s) };
            client.TestConnected(true);
            client.TestReceive("{\"type\": \"welcome\", \"role\": \"editor\", \"session\": \"layout\", \"features\": [\"account\", \"steer\"]}");
            EditorHost.TestClient(client);
            return client;
        }

        bool SentAccount() => sent.Any(s => Json.Str(Json.ParseObject(s), "type") == "account");

        // -- laying a page out ---------------------------------------------------------------------------------------

        void Layout()
        {
            validate.Invoke(panel, null);
            validate.Invoke(panel, null);
        }

        AutopilotView Show(Vector2Int size, Action<AutopilotView> before, Action<AutopilotView> after = null)
        {
            host.Clear();
            host.style.width = size.x;
            host.style.height = size.y;
            var view = new AutopilotView();
            before?.Invoke(view);
            host.Add(view);
            view.SetWide(size.x > AutopilotView.WideFrom);
            view.Refresh(true);
            after?.Invoke(view);
            Layout();
            return view;
        }

        static bool Shown(VisualElement e, VisualElement top)
        {
            for (var x = e; x != null && x != top; x = x.parent)
                if (x.resolvedStyle.display == DisplayStyle.None || x.resolvedStyle.visibility == Visibility.Hidden)
                    return false;
            return true;
        }

        static bool InsideAny(VisualElement e, VisualElement top, Func<VisualElement, bool> what)
        {
            for (var x = e.parent; x != null && x != top; x = x.parent)
                if (what(x))
                    return true;
            return false;
        }

        /// <summary>Whether the element hides what overflows it: the window's USS does for .nowrap labels and the
        /// usage tables' cells (IResolvedStyle has no overflow to ask), Unity for a ScrollView's viewport.</summary>
        static bool Clips(VisualElement e)
        {
            return e.ClassListContains("nowrap") || e.ClassListContains("unity-scroll-view__content-viewport")
                   || (e is Label && e.parent != null && e.parent.ClassListContains("urow"))
                   || e.style.overflow.value == Overflow.Hidden;
        }

        static string Name(VisualElement e)
        {
            var text = e is TextElement t ? $" \"{AutopilotText.Short(t.text, 40)}\"" : "";
            return $"{e.GetType().Name}.{string.Join(".", e.GetClasses())}{text}";
        }

        /// <summary>Every problem with the layout now, in words (none: it fits).</summary>
        List<string> Problems(string what, Vector2Int size, out int measured)
        {
            var problems = new List<string>();
            measured = 0;
            var window = host.worldBound;
            var view = host[0];
            if (Mathf.Abs(view.layout.width - size.x) > 0.5f || Mathf.Abs(view.layout.height - size.y) > 0.5f)
                problems.Add($"the view is {view.layout.width}x{view.layout.height}");
            foreach (var e in host.Query<VisualElement>().ToList())
            {
                if (e == host || !Shown(e, host)) continue;
                if (InsideAny(e, host, x => x is TextInputBaseField<string> || x is TextInputBaseField<int> || x.ClassListContains("unity-scroller") || x is Toggle))
                    continue;  // (a field's own insides: its text scrolls in it)
                if (e.ClassListContains("unity-scroller")) continue;
                if (e.resolvedStyle.position == Position.Absolute && e != view) continue;
                var r = e.worldBound;
                if (float.IsNaN(r.x) || float.IsNaN(r.width)) continue;
                if (r.width <= 0.01f && r.height <= 0.01f) continue;
                if (r.xMin < window.xMin - 0.5f || r.xMax > window.xMax + 0.5f)
                    problems.Add($"{Name(e)} spans x {r.xMin:0.#}..{r.xMax:0.#}, outside the window's 0..{size.x}");
                var inScroll = InsideAny(e, host, x => x is ScrollView);
                if (!inScroll && r.yMax > window.yMax + 0.5f)
                    problems.Add($"{Name(e)} ends at y {r.yMax:0.#}, below the window's {size.y} (and doesn't scroll)");
                var parent = e.parent;
                if (parent != null && parent != host && !(parent.ClassListContains("unity-scroll-view__content-viewport")) && !Clips(parent))
                {
                    var pr = parent.worldBound;
                    if (r.xMin < pr.xMin - 0.5f || r.xMax > pr.xMax + 0.5f)
                        problems.Add($"{Name(e)} spans x {r.xMin:0.#}..{r.xMax:0.#}, out of its {Name(parent)} ({pr.xMin:0.#}..{pr.xMax:0.#})");
                }
                if (e is TextElement label && !(e is Button) && !string.IsNullOrEmpty(label.text))
                {
                    if (r.height > 0.5f && r.width > 0.5f) ++measured;
                    var clipped = Clips(label) && label.resolvedStyle.textOverflow == TextOverflow.Ellipsis;
                    if (clipped) continue;
                    var nowrap = label.resolvedStyle.whiteSpace == WhiteSpace.NoWrap;
                    var room = label.contentRect.width;
                    var need = label.MeasureTextSize(label.text, nowrap ? 0 : room, nowrap ? VisualElement.MeasureMode.Undefined : VisualElement.MeasureMode.AtMost,
                        0, VisualElement.MeasureMode.Undefined);
                    if (need.x > room + 1f)
                        problems.Add($"{Name(e)}: its text needs {need.x:0.#} px, it has {room:0.#}");
                }
            }
            // The key inputs' placeholders fit their boxes (a field's insides aren't checked above: its text scrolls)
            foreach (var key in host.Query<TextField>(className: "key-input").ToList())
            {
                if (!Shown(key, host)) continue;
                var placeholder = key.textEdition.placeholder;
                var room = PlaceholderRoom(key);
                var need = PlaceholderNeeds(key);
                if (string.IsNullOrEmpty(placeholder) || float.IsNaN(room)) continue;
                if (need > room + 0.5f)
                    problems.Add($"the placeholder \"{placeholder}\" needs {need:0.#} px, its input has {room:0.#}");
            }
            // The composer's box: its placeholder fits it (it wraps in the box: every word fits a line, the lines its height)
            foreach (var box in host.Query<TextField>(className: "prompt-input").ToList())
            {
                if (!Shown(box, host)) continue;
                var placeholder = box.textEdition.placeholder;
                var input = box.Q(className: "unity-base-text-field__input");
                var text = box.Q<TextElement>();
                if (string.IsNullOrEmpty(placeholder) || input == null || text == null) continue;
                var room = input.contentRect;
                foreach (var word in placeholder.Split(' '))
                {
                    var w = text.MeasureTextSize(word, 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined).x;
                    if (w > room.width + 0.5f) problems.Add($"the placeholder's word \"{word}\" needs {w:0.#} px, the box has {room.width:0.#}");
                }
                var need = text.MeasureTextSize(placeholder, room.width, VisualElement.MeasureMode.AtMost, 0, VisualElement.MeasureMode.Undefined);
                if (need.y > room.height + 0.5f) problems.Add($"the placeholder \"{placeholder}\" needs {need.y:0.#} px of height, the box has {room.height:0.#}");
            }
            foreach (var page in host.Query(className: "page").ToList())
            {
                if (!Shown(page, host)) continue;
                var r = page.worldBound;
                // (each page's own max width: the composer 620, Settings 640, the setup 600, the agent service card 540; a
                // run's pages none)
                var max = page.resolvedStyle.maxWidth;
                var limit = max.keyword == StyleKeyword.Undefined && max.value > 0 ? max.value : float.PositiveInfinity;
                if (r.width > limit + 0.5f) problems.Add($"the page is {r.width:0.#} px wide, more than its {limit:0.#}");
                // (centred in what shows of its ScrollView: the viewport, beside the scroll bar when there is one)
                var sv = page.GetFirstAncestorOfType<ScrollView>();
                var pr = sv != null ? sv.contentViewport.worldBound : page.parent.worldBound;
                if (pr.width > limit + 1 && Mathf.Abs((r.xMin - pr.xMin) - (pr.xMax - r.xMax)) > 1f)
                    problems.Add($"the page isn't centred ({r.xMin - pr.xMin:0.#} px left, {pr.xMax - r.xMax:0.#} px right)");
            }
            return problems.Select(p => $"{what} at {size.x}x{size.y}: {p}").ToList();
        }

        /// <summary>The width a text field's text has: its input box's, inside its padding and border.</summary>
        static float PlaceholderRoom(TextField f)
        {
            var input = f.Q(className: "unity-base-text-field__input");
            return input == null ? float.NaN : input.contentRect.width;
        }

        /// <summary>The width a text field's placeholder takes in the field's own font.</summary>
        static float PlaceholderNeeds(TextField f)
        {
            var text = f.Q<TextElement>();
            return text == null ? 0 : text.MeasureTextSize(f.textEdition.placeholder ?? "", 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined).x;
        }

        /// <summary>The words shown (labels, the fields' placeholders), as one text.</summary>
        string Words()
        {
            var words = new List<string>();
            foreach (var e in host.Query<VisualElement>().ToList())
            {
                if (!Shown(e, host)) continue;
                if (e is TextElement t && !string.IsNullOrEmpty(t.text)) words.Add(t.text);
                if (e is TextField f && !string.IsNullOrEmpty(f.textEdition.placeholder)) words.Add(f.textEdition.placeholder);
            }
            return string.Join("\n", words).Replace(((char)0x200B).ToString(), "");
        }

        static readonly string[] NeverShown =
        {
            "Ramen VR", "train", "Turn based", "Turn-based", "Real time", "Timing", "Delete save",
            "Jev", "brain", "instructions", "you@studio.com", "[N]", "DENIED IN THE BROWSER", "VARIANT", "THEN, IN PLACE",
            "Use your own API keys", "Your own API keys", "your own API keys", "Own keys", "USE YOUR OWN API KEYS", "choose your own keys", "· KEYS",
            "Keys are read where the agent service runs", "a copy is kept",
            // (one Rerun setup in place of the account chip, Setup... and Change sign-in)
            "Setup…", "SETUP…", "CHANGE SIGN-IN", "Change sign-in", "RUN SETUP AGAIN", "Run setup again",
        };

        /// <summary>Words the next checks must not show (besides NeverShown).</summary>
        readonly List<string> notSaid = new List<string>();

        /// <summary>Lay a page out at every size: it fits, and says what the design says.</summary>
        void Check(string what, Action<AutopilotView> before, Action<AutopilotView> after = null, params string[] says)
        {
            var problems = new List<string>();
            var measuredAll = 0;
            foreach (var size in Sizes)
            {
                Show(size, before, after);
                problems.AddRange(Problems(what, size, out var measured));
                measuredAll += measured;
                var words = Words();
                foreach (var w in says)
                    if (words.IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0)
                        problems.Add($"{what} at {size.x}x{size.y}: doesn't say \"{w}\"");
                foreach (var w in NeverShown.Concat(notSaid))
                    if (words.IndexOf(w, StringComparison.Ordinal) >= 0)
                        problems.Add($"{what} at {size.x}x{size.y}: says \"{w}\"");
                // (BYOK is the mode's label everywhere; "own keys" only in running text that explains it, and in the BYOK
                // account card's title, "Your own keys", as the design has it)
                if (System.Text.RegularExpressions.Regex.IsMatch(words, @"(?i)(?<!your )own keys(?! \(BYOK\))|(?<!^your )own keys(?=\s*$)", System.Text.RegularExpressions.RegexOptions.Multiline))
                    problems.Add($"{what} at {size.x}x{size.y}: names the BYOK mode \"own keys\"");
            }
            if (measuredAll == 0) Assert.Inconclusive("No label was given a size: UI Toolkit's layout didn't run here");
            Assert.IsEmpty(problems, string.Join("\n", problems.Distinct().Take(40)));
        }

        // keys: step 1's BYOK card opened (Show), so its fields are there to measure; it opens folded
        static void Setup(AutopilotView v, int step, string choice = "", bool keys = true)
        {
            v.SetupOpen = true;
            v.SetupStep = step;
            v.SetupChoice = choice;
            v.SetupAccess = choice == AutopilotView.ChoiceKeys ? Preferences.AccessOwnKeys : choice == "" ? "" : Preferences.AccessAura;
            v.SetupEmail = choice == AutopilotView.ChoiceAura ? Email : "";
            KeysShown.SetValue(v, keys);
        }

        static readonly FieldInfo KeysShown = typeof(AutopilotView).GetField("keysShown", BindingFlags.Instance | BindingFlags.NonPublic);

        // -- the first-run setup ------------------------------------------------------------------------------------

        [Test]
        public void SetupStep1Fits()
        {
            notSaid.AddRange(new[] { "Optional, e.g. Anthropic API key", "Optional, Anthropic API key", "Optional, OpenAI API key",
                "Choose how to run it", "Autopilot learns your game", "Leave empty", "Not sure yet", "Use Ramen's models", "e.g. TypeSafe",
                "Decide later", "Billed to your Aura plan." });
            // (as it is here, and with the Aura app found: the BYOK card is the same in both)
            foreach (var auraApp in new[] { false, true })
            {
                Aura.InstalledOverride = auraApp;
                Check(auraApp ? "step 1, the Aura app found" : "step 1", v => Setup(v, 1), null,
                    "1 ACCOUNT", "2 AGENT SERVICE", "3 PRIVACY SETTINGS", "4 FIRST RUN", "Welcome to Autopilot",
                    "Sign in with Aura", AutopilotView.AuraLine, "Create Account", "Sign in",
                    "Bring your own keys (BYOK)", "Decision provider API key", "Optional", "Coding Agent", "OpenCode",
                    "LLM provider API key", "Anthropic", "Use my keys", "Hide");
            }

            // The BYOK card as step 1 opens: folded, Show opens it
            notSaid.AddRange(new[] { "Decision provider API key", "Use my keys", "Hide" });
            Check("step 1, BYOK folded", v => Setup(v, 1, keys: false), null, "Sign in with Aura", AutopilotView.AuraLine, "Bring your own keys (BYOK)", "Show");
            notSaid.RemoveRange(notSaid.Count - 3, 3);

            // The Aura app found or not: Create Account is the main choice, Sign in the other, after it
            foreach (var auraApp in new[] { false, true })
            foreach (var size in Sizes)
            {
                Aura.InstalledOverride = auraApp;
                Show(size, v => Setup(v, 1));
                var buttons = host.Query(className: "btn").ToList().Where(b => Shown(b, host)).ToList();
                Assert.IsFalse(buttons.Any(b => b.Q<Label>()?.text == "Use Aura app"), $"no Use Aura app at {size}");
                var create = buttons.Single(b => b.Q<Label>()?.text == "Create Account");
                var signIn = buttons.Single(b => b.Q<Label>()?.text == "Sign in");
                Assert.IsTrue(create.ClassListContains("primary"), $"Create Account the main button at {size}");
                Assert.IsFalse(signIn.ClassListContains("primary"), $"Sign in not the main button at {size}");
                Assert.IsTrue(signIn.ClassListContains("quiet"), $"Sign in quiet at {size}");
                Assert.Less(buttons.IndexOf(create), buttons.IndexOf(signIn), $"Create Account first at {size}");
            }
            Aura.InstalledOverride = null;

            // Create Account's next page (only the flag set: no browser opened from a test): finish in the browser, then
            // Sign in, the main button
            Check("step 1, creating an account", v => { Setup(v, 1); CreatingAccount.SetValue(v, true); }, null,
                "Sign in with Aura", "Finish creating your Aura account",
                "Once you have created your account, sign in and start using Autopilot", "Sign in");
            foreach (var size in Sizes)
            {
                Show(size, v => { Setup(v, 1); CreatingAccount.SetValue(v, true); });
                var buttons = host.Query(className: "btn").ToList().Where(b => Shown(b, host)).ToList();
                Assert.IsFalse(buttons.Any(b => b.Q<Label>()?.text == "Create Account"), $"no Create Account at {size}");
                Assert.IsTrue(buttons.Single(b => b.Q<Label>()?.text == "Sign in").ClassListContains("primary"), $"Sign in the main button at {size}");
            }
        }

        static readonly FieldInfo CreatingAccount = typeof(AutopilotView).GetField("creatingAccount", BindingFlags.Instance | BindingFlags.NonPublic);

        [Test]
        public void TheAccountCreationPagesServer()
        {
            // autopilot.env's AUTOPILOT_AURA_SERVER, else the package's AgentService~/settings.env, else Aura's
            var dir = Path.Combine(Path.GetTempPath(), "autopilot-signup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var env = Path.Combine(dir, "autopilot.env");
                Assert.AreEqual("https://app.tryaura.dev/auth/signup?src=autopilot", AutopilotView.SignUpUrl(env, dir));
                File.WriteAllText(Path.Combine(dir, "settings.env"), "AUTOPILOT_AURA_SERVER=https://beta.example/\n");
                Assert.AreEqual("https://beta.example/auth/signup?src=autopilot", AutopilotView.SignUpUrl(env, dir));
                File.WriteAllText(env, "AUTOPILOT_AURA_SERVER=https://own.example\n");
                Assert.AreEqual("https://own.example/auth/signup?src=autopilot", AutopilotView.SignUpUrl(env, dir));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        static readonly MethodInfo UseMyKeys = typeof(AutopilotView).GetMethod("UseMyKeys", BindingFlags.Instance | BindingFlags.NonPublic);

        [Test]
        public void TheCodingAgentAndLlmProviderPickers()
        {
            // The account frame's Coding Agent and provider are picked to begin with (else autopilot.env's: the test's own
            // folder has none)
            var c = Client();
            const string openRouterFrame = "{\"type\": \"account\", \"mode\": \"own_keys\", \"signed_in\": false, \"keys\": {\"decision\": null, \"llm\": \"••••3c2e\"}, " +
                                           "\"coding_agent\": \"opencode\", \"llm_provider\": \"openrouter\"}";
            c.TestReceive(openRouterFrame);
            notSaid.AddRange(new[] { "Optional, Anthropic API key", "Optional, OpenAI API key", "Optional, e.g. Anthropic API key" });
            Check("step 1, OpenCode with OpenRouter", v => Setup(v, 1), null, "Coding Agent", "OpenCode", "LLM provider API key", "OpenRouter", "Use my keys");

            // At every size, as here and with the Aura app found: the decision key, the Coding Agent's dropdown and the
            // LLM key's row stacked, each full width; the LLM key's row the provider's dropdown (140 px; every provider's
            // name fitting it), then the key filling the rest; both placeholders fitting their inputs
            var agentNames = new[] { "Claude Code", "Codex", "OpenCode" };
            var providerNames = new[] { "Anthropic", "OpenAI", "OpenCode Zen", "OpenRouter", "DeepSeek", "Gemini" };
            foreach (var auraApp in new[] { false, true })
            {
                Aura.InstalledOverride = auraApp;
                foreach (var size in Sizes)
                {
                    var at = $"at {size.x}x{size.y}{(auraApp ? ", the Aura app found" : "")}";
                    Show(size, v => Setup(v, 1));
                    Assert.AreEqual("Configured agent", host.Q<DropdownField>(className: "run-client").value);
                    var agent = host.Q<DropdownField>(className: "coding-agent");
                    var pick = host.Q<DropdownField>(className: "llm-provider");
                    Assert.AreEqual("Coding Agent", agent.tooltip);
                    Assert.AreEqual("LLM provider", pick.tooltip);
                    CollectionAssert.AreEqual(agentNames, agent.choices);
                    Assert.AreEqual("OpenCode", agent.value);
                    CollectionAssert.AreEqual(providerNames, pick.choices);
                    Assert.AreEqual("OpenRouter", pick.value);
                    Assert.IsTrue(pick.enabledSelf, $"OpenCode's provider can be picked {at}");
                    var keys = host.Query<TextField>(className: "key-input").ToList();
                    Assert.AreEqual(2, keys.Count, $"two key inputs {at}");
                    TextField decision = keys[0], key = keys[1];
                    Assert.AreSame(key, pick.parent.Q<TextField>(), $"the LLM key beside the dropdown {at}");
                    Assert.IsTrue(decision.isPasswordField && key.isPasswordField);
                    Assert.AreEqual("Optional", decision.textEdition.placeholder);
                    Assert.AreEqual("Optional", key.textEdition.placeholder);

                    Rect d = decision.worldBound, a = agent.worldBound, p = pick.worldBound, k = key.worldBound, row = pick.parent.worldBound;
                    var box = decision.parent;
                    while (box != null && !box.ClassListContains("keys")) box = box.parent;
                    Assert.IsNotNull(box, "the key fields' box");
                    var fields = box.worldBound;
                    Assert.AreEqual(fields.width, d.width, 0.5f, $"the decision key full width {at}");
                    Assert.AreEqual(fields.width, a.width, 0.5f, $"the Coding Agent's dropdown full width {at}");
                    Assert.AreEqual(fields.width, row.width, 0.5f, $"the LLM key's row full width {at}");
                    Assert.Greater(a.yMin, d.yMax, $"the Coding Agent under the decision key {at}");
                    Assert.Greater(p.yMin, a.yMax, $"the LLM key under the Coding Agent {at}");
                    Assert.AreEqual(d.height, a.height, 1f, $"the Coding Agent's dropdown as tall as a key {at}");
                    Assert.AreEqual(140f, p.width, 0.5f, $"the dropdown's width {at}");
                    Assert.AreEqual(row.xMin, p.xMin, 0.5f, $"the dropdown first {at}");
                    Assert.AreEqual(p.yMin, k.yMin, 0.5f, $"one row {at}");
                    Assert.AreEqual(p.height, k.height, 1f, $"one height {at}");
                    Assert.GreaterOrEqual(k.xMin, p.xMax - 0.5f, $"the key after the dropdown {at}");
                    Assert.AreEqual(row.xMax, k.xMax, 0.5f, $"the key fills the rest {at}");
                    foreach (var f in keys)
                    {
                        var room = PlaceholderRoom(f);
                        var need = PlaceholderNeeds(f);
                        Assert.Greater(need, 0f, $"the placeholder measured {at}");
                        Assert.LessOrEqual(need, room + 0.5f, $"\"{f.textEdition.placeholder}\" fits its input {at}");
                    }
                    foreach (var (dropdown, names) in new[] { (agent, agentNames), (pick, providerNames) })
                    {
                        var shown = dropdown.Q<TextElement>(className: "unity-base-popup-field__text");
                        Assert.IsNotNull(shown, "the dropdown's text");
                        foreach (var name in names)
                        {
                            var need = shown.MeasureTextSize(name, 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined).x;
                            Assert.LessOrEqual(need, shown.contentRect.width + 0.5f, $"\"{name}\" fits its dropdown ({need:0.#} px in {shown.contentRect.width:0.#}) {at}");
                        }
                    }
                    TestContext.WriteLine($"key inputs {at}: decision {d.width:0.#} px (text room {PlaceholderRoom(decision):0.#}, placeholder {PlaceholderNeeds(decision):0.#}); " +
                                          $"LLM {k.width:0.#} px (text room {PlaceholderRoom(key):0.#}, placeholder {PlaceholderNeeds(key):0.#}); " +
                                          $"provider's text room {pick.Q<TextElement>(className: "unity-base-popup-field__text").contentRect.width:0.#}");
                }
            }
            Aura.InstalledOverride = null;

            // Each Coding Agent: Claude Code and Codex show their one provider, locked (and fitting, not faded);
            // OpenCode its six, on the one last picked for it
            var view = Show(new Vector2Int(360, 640), v => Setup(v, 1));
            DropdownField Agent() => host.Q<DropdownField>(className: "coding-agent");
            DropdownField Provider() => host.Q<DropdownField>(className: "llm-provider");
            Agent().value = "Claude Code";
            Layout();
            CollectionAssert.AreEqual(new[] { "Anthropic" }, Provider().choices);
            Assert.AreEqual("Anthropic", Provider().value);
            Assert.IsFalse(Provider().enabledInHierarchy, "Claude Code's provider locked");
            Assert.AreEqual(1f, Provider().resolvedStyle.opacity, 0.01f, "a locked provider isn't faded");
            Assert.AreEqual("Optional", Provider().parent.Q<TextField>().textEdition.placeholder);
            Assert.IsEmpty(Problems("step 1, Claude Code", new Vector2Int(360, 640), out _));
            Agent().value = "Codex";
            Layout();
            CollectionAssert.AreEqual(new[] { "OpenAI" }, Provider().choices);
            Assert.AreEqual("OpenAI", Provider().value);
            Assert.IsFalse(Provider().enabledInHierarchy, "Codex's provider locked");
            Agent().value = "OpenCode";
            Layout();
            CollectionAssert.AreEqual(providerNames, Provider().choices);
            Assert.AreEqual("OpenRouter", Provider().value, "OpenCode's provider back");
            Assert.IsTrue(Provider().enabledInHierarchy);
            Provider().value = "DeepSeek";
            Agent().value = "Codex";
            Agent().value = "OpenCode";
            Assert.AreEqual("DeepSeek", Provider().value, "the one last picked for OpenCode");
            Provider().value = "Gemini";

            // A key typed for one LLM provider is never saved as another's: another provider empties it, a Coding Agent
            // with the same provider keeps it
            TextField LlmKey() => Provider().parent.Q<TextField>();
            Provider().value = "Anthropic";
            LlmKey().value = "ant-typed";
            Agent().value = "Claude Code";
            Assert.AreEqual("ant-typed", LlmKey().value, "OpenCode's Anthropic to Claude Code's: kept");
            Agent().value = "Codex";
            Assert.AreEqual("", LlmKey().value, "to Codex's OpenAI: emptied");
            view.Refresh(true);
            Layout();
            Assert.AreEqual("", LlmKey().value, "built again: still empty");
            LlmKey().value = "oai-typed";
            Agent().value = "OpenCode";
            Assert.AreEqual("", LlmKey().value, "to OpenCode's Anthropic: emptied");
            LlmKey().value = "ant-typed";
            Provider().value = "Gemini";
            Assert.AreEqual("", LlmKey().value, "another provider picked: emptied");

            // Built again (another account frame): the user's picks stay
            c.TestReceive(openRouterFrame);
            view.Refresh(true);
            Layout();
            Assert.AreEqual("OpenCode", Agent().value);
            Assert.AreEqual("Gemini", Provider().value);

            // Use my keys: the agent, OpenCode's provider, and the key under its provider's name, in the test's own
            // autopilot.env (other providers' keys kept)
            var env = Path.Combine(folder, AutopilotEnv.FileName);
            File.WriteAllText(env, "OPENAI_API_KEY=oai-old\n");
            Provider().parent.Q<TextField>().value = "gem-typed";
            AgentService.TestState(AgentService.Phase.Running, ServiceUrl);  // (step 2 starts none)
            UseMyKeys.Invoke(view, null);
            Assert.AreEqual("OPENAI_API_KEY=oai-old\nAUTOPILOT_AGENT=opencode\nAUTOPILOT_OPENCODE_PROVIDER=google\nGEMINI_API_KEY=gem-typed\n", File.ReadAllText(env));
            Assert.AreEqual(2, view.SetupStep);
            Assert.AreEqual(AutopilotView.ChoiceKeys, view.SetupChoice);

            // Back on step 1: what was saved. Claude Code with its key: OpenCode's provider line left as it is
            view.SetupStep = 1;
            view.Refresh(true);
            Layout();
            Assert.AreEqual("OpenCode", Agent().value);
            Assert.AreEqual("Gemini", Provider().value);
            Assert.AreEqual("", Provider().parent.Q<TextField>().value, "the typed key isn't kept");
            Agent().value = "Claude Code";
            Provider().parent.Q<TextField>().value = "ant-typed";
            UseMyKeys.Invoke(view, null);
            Assert.AreEqual("OPENAI_API_KEY=oai-old\nAUTOPILOT_AGENT=claude\nAUTOPILOT_OPENCODE_PROVIDER=google\nGEMINI_API_KEY=gem-typed\nANTHROPIC_API_KEY=ant-typed\n", File.ReadAllText(env));
            // Codex with its key left empty changes only the agent
            view.SetupStep = 1;
            view.Refresh(true);
            Layout();
            Assert.AreEqual("Claude Code", Agent().value);
            Assert.AreEqual("Anthropic", Provider().value);
            Agent().value = "Codex";
            UseMyKeys.Invoke(view, null);
            Assert.AreEqual("OPENAI_API_KEY=oai-old\nAUTOPILOT_AGENT=codex\nAUTOPILOT_OPENCODE_PROVIDER=google\nGEMINI_API_KEY=gem-typed\nANTHROPIC_API_KEY=ant-typed\n", File.ReadAllText(env));

            // Setup… again, no account frame from the agent service: autopilot.env's agent, and OpenCode's provider when
            // OpenCode is picked
            EditorHost.TestClient(null);
            view.OpenSetup();
            Layout();
            Assert.AreEqual("Codex", Agent().value);
            Assert.AreEqual("OpenAI", Provider().value);
            Agent().value = "OpenCode";
            Assert.AreEqual("Gemini", Provider().value);
            notSaid.Clear();
        }

        [Test]
        public void SetupSignInFits()
        {
            AgentService.TestState(AgentService.Phase.Installing);
            notSaid.AddRange(new[] { "The first time, Autopilot", "This tab carries on by itself", "Runs use Ramen's models",
                "Bring your own keys (BYOK)", "Available after you cancel", "Decision provider API key" });
            // (no keys card meanwhile: Cancel brings the choices back)
            Check("getting ready", v =>
            {
                Setup(v, 1);
                v.SigningIn = true;
            }, null, "Getting ready", "Setting up the agent service…", "Starting the agent service…", "Finish signing in in your browser…",
                "Cancel");

            var c = Client();
            c.TestReceive("{\"type\": \"aura_sign_in\", \"state\": \"waiting\", \"url\": \"https://app.tryaura.dev/auth/cli?code=AB12CD34EF56GH78IJ90KL12MN34OP56\"}");
            Check("waiting for the browser", v => Setup(v, 1), null,
                "In your browser", "Finish signing in in your browser…",
                "app.tryaura.dev", "Open again", "Copy link", "Cancel");

            // The connection to the agent service gone meanwhile: being prepared again (asked again once connected), or
            // the agent service's problem with Try again
            c.TestConnected(false);
            AgentService.TestState(AgentService.Phase.Running, ServiceUrl);
            notSaid.AddRange(new[] { "Open again", "Copy link", "In your browser" });
            Check("waiting, the connection gone", v => Setup(v, 1), null, "Getting ready", "Starting the agent service…", "Cancel");
            AgentService.TestState(AgentService.Phase.Failed, problem: "The agent service stopped (exit code 1).");
            Check("waiting, the agent service failed", v => Setup(v, 1), null,
                "Getting ready", "The agent service stopped (exit code 1).", AutopilotView.ViewLogs, "Try again", "Cancel");
            notSaid.Clear();
            notSaid.AddRange(new[] { "Runs use Ramen's models" });
            AgentService.TestState(AgentService.Phase.Running, ServiceUrl);
            c.TestConnected(true);

            c.TestReceive($"{{\"type\": \"aura_sign_in\", \"state\": \"signed_in\", \"email\": \"{Email}\"}}");
            Check("signed in", v => Setup(v, 1, keys: false), null,
                "Signed in", "Signed in as", Email, "Continue", "Use another account",
                "Bring your own keys (BYOK)", "Show");
            notSaid.Clear();

            c.TestReceive("{\"type\": \"aura_sign_in\", \"state\": \"error\", \"reason\": \"denied\"}");
            if (!Aura.IsInstalled())
                Check("sign-in denied", v => Setup(v, 1), null, "Not signed in", "Sign-in was cancelled in the browser.", "Try again");
        }

        [Test]
        public void SetupStep2Fits()
        {
            notSaid.AddRange(new[] { "The agent service runs the agent", "Nothing to run in a terminal", "your Aura sign-in", "the keys you saved",
                "Open settings", "Start it yourself", "automatically is off" });
            AgentService.TestState(AgentService.Phase.NotInstalled);
            Check("not installed", v => Setup(v, 2, AutopilotView.ChoiceAura), null,
                "1 ACCOUNT · AURA", "Set up the agent service", "Not installed on this computer", AutopilotView.NotInstalledLine,
                "Install", "I’ll do it later", "Back");
            Check("not installed, own keys", v => Setup(v, 2, AutopilotView.ChoiceKeys), null,
                "1 ACCOUNT · BYOK", "Not installed on this computer", "Install");

            AgentService.TestState(AgentService.Phase.Installing);
            Check("installing", v => Setup(v, 2, AutopilotView.ChoiceAura), null, "Set up the agent service", "Installing the agent service…", "Continue", "Back");

            AgentService.TestState(AgentService.Phase.Running, ServiceUrl);
            Check("ready", v => Setup(v, 2, AutopilotView.ChoiceAura), null,
                "Set up the agent service", "Agent service ready", "Starts with your editor", ServiceUrl, "Continue", "Back");

            AgentService.TestState(AgentService.Phase.InstallFailed, problem: InstallProblem);
            Check("install failed", v => Setup(v, 2, AutopilotView.ChoiceAura), null,
                "Couldn't install the agent service (see", "install-20261013-141502.log", AutopilotView.ViewLogs, "Try again", "I’ll do it later", "Back");

            AgentService.TestState(AgentService.Phase.NoInstaller);
            Check("no installer", v => Setup(v, 2, AutopilotView.ChoiceAura), null,
                "No agent service found", "Agent service command", "Start", "Or run", "autopilot serve", "Copy", "I’ll do it later");
            notSaid.Clear();
        }

        [Test]
        public void SetupStep3Fits()
        {
            // (the code export is never sent: only the parts of the code the agent read, in its traces)
            const string share = "Enabling this sends recordings, screenshots, and agent traces and memories to the developers of Autopilot." +
                                 " For runs from your own Claude Code or Codex, the agent traces are its conversation during the run.";
            Assert.AreEqual(share, AutopilotView.TrainLine);
            // Not known yet what the agent service remembers about this game: the first run may follow (Next, 4 steps)
            AgentService.TestState(AgentService.Phase.NotInstalled);
            notSaid.AddRange(new[] { "Finish", "code export", "text export", "Autopilot offers to install it", "You can change this any time" });
            Check("privacy, own keys", v => Setup(v, 3, AutopilotView.ChoiceKeys), null,
                "Privacy settings", "Share my runs to improve Autopilot", share, "Account", "BYOK", "Agent service",
                "Not installed yet", "4 FIRST RUN", "Next", "Back");
            AgentService.TestState(AgentService.Phase.Running, ServiceUrl);
            Check("privacy, Aura", v => Setup(v, 3, AutopilotView.ChoiceAura), null,
                "Privacy settings", "Aura · " + Email, "Ready", "4 FIRST RUN", "Next", "Back");
            var c = Client();
            c.TestReceive("{\"type\": \"memory_state\", \"runs\": 0, \"notes\": false, \"lib\": false}");
            Check("privacy, nothing remembered", v => Setup(v, 3, AutopilotView.ChoiceAura), null, "4 FIRST RUN", "Next", "Back");
            notSaid.Clear();

            // Something remembered (runs, notes or code of its own): no first run, Finish ends the setup
            notSaid.AddRange(new[] { "4 FIRST RUN", "Next" });
            foreach (var memory in new[] { "{\"runs\": 2, \"notes\": false, \"lib\": false}", "{\"runs\": 0, \"notes\": true, \"lib\": false}", "{\"runs\": 0, \"notes\": false, \"lib\": true}" })
            {
                c.TestReceive("{\"type\": \"memory_state\", " + memory.Substring(1));
                Check("privacy, remembered " + memory, v => Setup(v, 3, AutopilotView.ChoiceAura), null,
                    "1 ACCOUNT", "3 PRIVACY SETTINGS", "Finish", "Back");
            }
            notSaid.Clear();
        }

        const string MemoryEmptyFrame = "{\"type\": \"memory_state\", \"runs\": 0, \"notes\": false, \"lib\": false}";

        [Test]
        public void NextGoesOnToTheFirstRunWhenNothingIsRemembered()
        {
            foreach (var memory in new[] { MemoryEmptyFrame, null })
            {
                Preferences.Update(new Dictionary<string, object> { ["onboarded"] = false });
                var c = Client();
                if (memory != null) c.TestReceive(memory);
                var view = Show(new Vector2Int(560, 900), v => Setup(v, 3, AutopilotView.ChoiceAura));
                typeof(AutopilotView).GetMethod("FinishSetup", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(view, null);
                var what = memory == null ? "not known yet" : "nothing remembered";
                Assert.IsTrue(Preferences.Current.Onboarded, $"the choices saved at Next ({what})");
                Assert.IsTrue(view.SetupOpen, $"the setup stays open ({what})");
                Assert.AreEqual(4, view.SetupStep, what);
                client.Disconnect();
                client = null;
            }
        }

        [Test]
        public void SetupStep4OffersAFirstExploreRun()
        {
            // Connected, not told yet what is remembered: waiting, with Skip
            var c = Client();
            notSaid.AddRange(new[] { "Explore and finish", "Back" });
            Check("first run, waiting", v => Setup(v, 4, AutopilotView.ChoiceAuraApp), null,
                "1 ACCOUNT · AURA APP", "3 PRIVACY SETTINGS", "4 FIRST RUN", AutopilotView.FirstRunTitle,
                "Checking what Autopilot remembers about this game…", "Skip");
            notSaid.Clear();

            // Nothing remembered: the Explore run offered, recommended and skippable
            c.TestReceive(MemoryEmptyFrame);
            notSaid.AddRange(new[] { "Back", "Plays for the minutes below", "Recommended, not required", "its memory is empty" });
            Assert.AreEqual("You’ve never used Autopilot with this game before. To help Autopilot understand your game, use this first exploration run and watch it improve. Autopilot still works if you skip this step.",
                AutopilotView.FirstRunLine);
            Check("first run, nothing remembered", v => Setup(v, 4, AutopilotView.ChoiceAuraApp), null,
                "4 FIRST RUN", AutopilotView.FirstRunTitle, AutopilotView.FirstRunLine, "Explore your game", "Recommended",
                "Minutes", "Explore and finish", "Skip and finish");
            notSaid.Clear();

            // The minutes are Explore's own (10 unless changed); Explore and finish starts that run, shows it and closes the setup
            var settings = PlaytestSettings.Get();
            settings.ExploreMinutes = 10;
            var view = Show(new Vector2Int(560, 900), v => Setup(v, 4, AutopilotView.ChoiceAuraApp));
            Assert.AreEqual(10, host.Q<IntegerField>().value);
            var explore = host.Query(className: "btn").ToList().Single(b => b.Q<Label>()?.text == "Explore and finish");
            Assert.IsTrue(explore.ClassListContains("primary"));
            Assert.IsTrue(explore.enabledInHierarchy);
            sent.Clear();
            view.ExploreAndFinish();
            var start = sent.Select(Json.ParseObject).Last(m => Json.Str(m, "type") == "start");
            var run = Json.Obj(start, "run");
            Assert.AreEqual("explore", Json.Str(run, "kind"));
            Assert.AreEqual(10, (int)Json.Num(run, "minutes", 0));
            Assert.IsFalse(view.SetupOpen, "the setup closed");
            var started = AutopilotSession.Current;
            Assert.IsNotNull(started);
            Assert.AreEqual("Explore", started.Prompt);
            Assert.AreEqual("result", view.Tab);
            AutopilotSession.Runs.Remove(started);

            // A run going: Explore and finish can't be pressed
            Assert.IsTrue(c.RunActive);
            Show(new Vector2Int(560, 900), v => Setup(v, 4, AutopilotView.ChoiceAuraApp));
            Assert.IsTrue(host.Query(className: "btn").ToList().Single(b => b.Q<Label>()?.text == "Explore and finish").ClassListContains("off"), "greyed mid-run");
            client.Disconnect();
            client = null;

            // Skip and finish: the setup closed, nothing started
            c = Client();
            c.TestReceive(MemoryEmptyFrame);
            view = Show(new Vector2Int(560, 900), v => Setup(v, 4, AutopilotView.ChoiceAuraApp));
            sent.Clear();
            view.SkipFirstRun();
            Assert.IsFalse(view.SetupOpen);
            Assert.IsFalse(sent.Any(s => Json.Str(Json.ParseObject(s), "type") == "start"), "Skip starts nothing");

            // Something remembered: step 4 isn't needed, the setup closes by itself
            view = Show(new Vector2Int(560, 900), v => Setup(v, 4, AutopilotView.ChoiceAuraApp));
            Assert.IsTrue(view.SetupOpen);
            c.TestReceive("{\"type\": \"memory_state\", \"runs\": 3, \"notes\": true, \"lib\": false}");
            view.Refresh(false);
            Assert.IsFalse(view.SetupOpen, "closed by itself");
            Assert.AreEqual(1, view.SetupStep);
            StringAssert.DoesNotContain(AutopilotView.FirstRunTitle, Words());
        }

        [Test]
        public void EveryStepOfTheSetupIsOneColumn()
        {
            // Steps 1 to 4 in one column, whatever each one's content: the window's width up to 600 px, centred beyond
            // it (a short step and a long one alike, with the scroll bar or without)
            AgentService.TestState(AgentService.Phase.NotInstalled);
            Client().TestReceive(MemoryEmptyFrame);
            var sizes = new[] { 300, 340, 420, 600, 700 }.Select(w => new Vector2Int(w, 700)).Concat(Sizes).ToArray();
            foreach (var size in sizes)
            {
                var columns = new List<Rect>();
                for (var step = 1; step <= 4; ++step)
                {
                    var at = step;
                    Show(size, v => Setup(v, at, at == 1 ? "" : AutopilotView.ChoiceAura));
                    var page = host.Q(className: "page");
                    Assert.IsTrue(page.ClassListContains("setup"), $"step {step}'s column at {size}");
                    columns.Add(page.worldBound);
                    // (the scroll bar's room kept on every step, the bar itself shown only on a step that scrolls)
                    var scroll = page.GetFirstAncestorOfType<ScrollView>();
                    var scrolls = scroll.contentContainer.layout.height > scroll.contentViewport.layout.height + 0.5f;
                    Assert.AreEqual(scrolls ? Visibility.Visible : Visibility.Hidden, scroll.verticalScroller.style.visibility.value,
                        $"step {step}'s scroll bar shown only when it scrolls at {size}");
                    Assert.IsEmpty(Problems($"step {step}", size, out _));
                }
                var widths = string.Join(", ", columns.Select((r, i) => $"step {i + 1}: {r.xMin:0.#}..{r.xMax:0.#}"));
                var window = host.worldBound;
                var expected = Mathf.Min(AutopilotView.SetupWidth, window.width);
                foreach (var r in columns)
                {
                    Assert.AreEqual(columns[0].xMin, r.xMin, 0.5f, $"one left edge at {size} ({widths})");
                    Assert.AreEqual(columns[0].width, r.width, 0.5f, $"one width at {size} ({widths})");
                    // (as wide as the window up to 600 px; the scroll bar, when a step has one, takes from the room beside it)
                    Assert.AreEqual(expected, r.width, Scrollbar + 0.5f, $"the window's width up to 600 px at {size} ({widths})");
                    Assert.AreEqual(window.center.x, r.center.x, Scrollbar / 2 + 0.5f, $"centred at {size} ({widths})");
                }
            }
        }

        /// <summary>A vertical scroll bar's width (the most a ScrollView's viewport loses beside it).</summary>
        const float Scrollbar = 14;

        // -- the Settings page -----------------------------------------------------------------------------------------

        // (the same words as the setup's step 3)
        const string ShareLine = "Enabling this sends recordings, screenshots, and agent traces and memories to the developers of Autopilot." +
                                 " For runs from your own Claude Code or Codex, the agent traces are its conversation during the run.";
        const string ResearchLine = "Enabling the research agent allows Autopilot to achieve more difficult goals with fewer attempts, but at a higher cost.";

        static void Settings(AutopilotView v) => v.Page = "settings";

        [Test]
        public void SettingsPageFits()
        {
            Assert.AreEqual(ShareLine, AutopilotView.ShareLine);
            Assert.AreEqual(ResearchLine, AutopilotView.ResearchLine);
            var sharing = new List<string>
            {
                "Share runs to improve Autopilot", ShareLine,
                "RESEARCH AGENT", ResearchLine,
                "Uninstall Autopilot", "Uninstall",
            };
            Assert.AreEqual(0, AutopilotUsage.Entries.Count, "the test's own ledger starts empty");
            sharing.Add("No usage yet.");
            // (no Time limit here: the composer's own, kept per project)
            notSaid.AddRange(new[] { "Time limit", "TIME LIMIT", "Set the time limit for every run", "Ramen's models, billed to your Aura plan", "Autopilot this period", "Billed", "How long a verify or goal run",
                "A change applies from the next connection", "Once a run has used the models", "before your first run", "DATA SHARING",
                "Shared runs not sent yet", "code export" });
            Check("Settings, not set up", Settings, null, sharing.Concat(new[]
            {
                "Account", "Not set up", "Choose how to run Autopilot", "Rerun setup",
                "Agent service: starts when Autopilot connects",
            }).ToArray());

            Preferences.Update(new Dictionary<string, object> { ["access"] = Preferences.AccessAura });
            PlaytestSettings.Get().UseWithAura = true;
            notSaid.Add("Sign out in the Aura app");
            Check("Settings, the Aura app", Settings, null, "Aura app", "The Aura app’s agent service", "Rerun setup");
            PlaytestSettings.Get().UseWithAura = false;

            var c = Client();
            AgentService.TestState(AgentService.Phase.Running, ServiceUrl);
            // (Aura couldn't say what Autopilot billed this period: no line for it)
            c.TestReceive($"{{\"type\": \"account\", \"mode\": \"aura\", \"email\": \"{Email}\", \"signed_in\": true, \"keys\": {{\"decision\": null, \"llm\": null}}, \"bill\": null}}");
            Check("Settings, Aura", Settings, null, "Aura", Email, "Agent service: running",
                ServiceUrl, "Rerun setup", "Sign out");
            notSaid.Clear();
            notSaid.Add("Ramen's models, billed to your Aura plan");

            // What Autopilot billed this period, a long one too
            c.TestReceive($"{{\"type\": \"account\", \"mode\": \"aura\", \"email\": \"{Email}\", \"signed_in\": true, \"keys\": {{}}, " +
                          "\"bill\": {\"usd\": 12.34, \"runs\": 38, \"from\": \"2026-10-01\", \"to\": \"2026-10-31\"}}");
            Check("Settings, Aura, this period", Settings, null, "Billing period (Oct 1 – Oct 31):", "$12.34 USD", Email, "Sign out");
            c.TestReceive($"{{\"type\": \"account\", \"mode\": \"aura\", \"email\": \"{Email}\", \"signed_in\": true, \"keys\": {{}}, " +
                          "\"bill\": {\"usd\": 1234567.891, \"runs\": 1234567, \"from\": \"2026-12-01\", \"to\": \"2027-01-31\"}}");
            Check("Settings, Aura, a long period", Settings, null, "Billing period (Dec 1, 2026 – Jan 31, 2027):", "$1,234,567.89 USD", Email);

            // Out of credits: Aura's words in red under the bill, Add credits first in the card's buttons
            c.TestReceive(AuraAccount(Credits(CreditsUrl)));
            Check("Settings, Aura, out of credits", Settings, null, "Billing period (Oct 1 – Oct 31):", NoCredits, AutopilotView.AddCredits, "Rerun setup", "Sign out");
            CreditsOnTheCard();
            // (the Aura app's agent service says it the same way)
            PlaytestSettings.Get().UseWithAura = true;
            Check("Settings, the Aura app, out of credits", Settings, null, "The Aura app’s agent service", "Billing period (Oct 1 – Oct 31):", NoCredits, AutopilotView.AddCredits, "Rerun setup");
            CreditsOnTheCard();
            PlaytestSettings.Get().UseWithAura = false;
            // Aura named no page: its words, no button
            c.TestReceive(AuraAccount(Credits(null)));
            Check("Settings, Aura, out of credits, no page", Settings, null, NoCredits, "Rerun setup", "Sign out");
            Assert.IsFalse(host.Q(className: "account").Query(className: "btn").ToList().Any(b => b.Q<Label>()?.text == AutopilotView.AddCredits), "no Add credits");
            // Credits again: neither
            c.TestReceive(AuraAccount(""));
            notSaid.AddRange(new[] { NoCredits, AutopilotView.AddCredits });
            Check("Settings, Aura, credits again", Settings, null, "Billing period (Oct 1 – Oct 31):", "Sign out");
            notSaid.Remove(NoCredits);
            notSaid.Remove(AutopilotView.AddCredits);
            c.TestReceive(AuraAccount(Credits(CreditsUrl)));

            var confirm = typeof(AutopilotView).GetField("signOutConfirm", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(confirm);
            Check("Settings, signing out", v =>
            {
                Settings(v);
                confirm.SetValue(v, true);
            }, null, $"Sign out {Email} on this computer?",
                "Every editor on this computer stops using Aura until you sign in again.", "Sign out", "Cancel");

            // (signed out: no bill, no credits' words)
            notSaid.AddRange(new[] { "Billing period", "Sign in again to keep running with Aura", NoCredits, AutopilotView.AddCredits });
            c.TestReceive($"{{\"type\": \"account\", \"mode\": \"aura\", \"email\": \"{Email}\", \"signed_in\": false, " +
                          "\"bill\": {\"usd\": 12.34, \"runs\": 38, \"from\": \"2026-10-01\", \"to\": \"2026-10-31\"}" + Credits(CreditsUrl) + "}");
            Check("Settings, signed out", Settings, null, "Signed out", Email, "Your Aura sign-in on this computer ended.", "Sign in again", "Rerun setup");

            AgentService.TestState(AgentService.Phase.Failed, problem: @"The agent service stopped (see C:\Users\someone\AppData\Local\Autopilot\logs\agent-service-20261013-141630.log)");
            c.TestReceive("{\"type\": \"account\", \"mode\": \"own_keys\", \"signed_in\": false, \"keys\": {\"decision\": \"••••a91f\", \"llm\": \"••••3c2e\"}, " +
                          "\"coding_agent\": \"opencode\", \"llm_provider\": \"openrouter\"}");
            Check("Settings, BYOK", Settings, null, "BYOK", "Saved on this computer: Decision provider ••••a91f · Coding Agent: OpenCode (OpenRouter) ••••3c2e",
                AutopilotView.ServiceDidntStart, AutopilotView.ViewLogs, "Rerun setup");
            c.TestReceive("{\"type\": \"account\", \"mode\": \"own_keys\", \"signed_in\": false, \"keys\": {\"decision\": \"••••a91f\", \"llm\": \"••••3c2e\"}, " +
                          "\"coding_agent\": \"claude\", \"llm_provider\": \"anthropic\"}");
            Check("Settings, BYOK, Claude Code", Settings, null, "Saved on this computer: Decision provider ••••a91f · Coding Agent: Claude Code (Anthropic) ••••3c2e");
            // (an agent service from before coding_agent and llm_provider: OpenCode and Anthropic, its defaults)
            c.TestReceive("{\"type\": \"account\", \"mode\": \"own_keys\", \"signed_in\": false, \"keys\": {\"decision\": \"••••a91f\", \"llm\": \"••••3c2e\"}}");
            Check("Settings, BYOK, Anthropic", Settings, null, "Saved on this computer: Decision provider ••••a91f · Coding Agent: OpenCode (Anthropic) ••••3c2e");
            // (no keys saved: just the tag, the agent service's line and the button)
            c.TestReceive("{\"type\": \"account\", \"mode\": \"own_keys\", \"signed_in\": false, \"keys\": {\"decision\": null, \"llm\": null}}");
            notSaid.Add("Saved on this computer");
            Check("Settings, BYOK, no keys saved", Settings, null, "BYOK", AutopilotView.ViewLogs, "Rerun setup");

            AgentService.TestState(AgentService.Phase.NotInstalled);
            Check("Settings, not installed", Settings, null, "Agent service: not installed", "Install");
        }

        // -- out of Aura credits -------------------------------------------------------------------------------------

        const string NoCredits = "Your team is out of Aura credits. Add credits at https://aura.example/account/pricing to keep playtesting.";
        const string CreditsUrl = "https://aura.example/account/pricing";

        /// <summary>The account frame's out_of_credits (Aura's words, the page it names or null), after a comma.</summary>
        static string Credits(string url) => $", \"out_of_credits\": {{\"message\": \"{NoCredits}\", \"url\": {(url == null ? "null" : $"\"{url}\"")}}}";

        /// <summary>A signed-in Aura account frame with this period's bill, and <paramref name="more"/>.</summary>
        static string AuraAccount(string more) =>
            $"{{\"type\": \"account\", \"mode\": \"aura\", \"email\": \"{Email}\", \"signed_in\": true, \"keys\": {{}}, " +
            "\"bill\": {\"usd\": 12.34, \"runs\": 38, \"from\": \"2026-10-01\", \"to\": \"2026-10-31\"}" + more + "}";

        /// <summary>The account card as laid out last: Aura's words in red right under the bill, Add credits first in
        /// its buttons.</summary>
        void CreditsOnTheCard()
        {
            var card = host.Q(className: "account");
            var line = card.Q<Label>(className: "credits-line");
            Assert.IsNotNull(line, "the credits' line");
            Assert.AreEqual(AutopilotUI.Hex("#e26a62"), line.resolvedStyle.color, "in red");
            var bill = card.Q(className: "bill-line");
            Assert.AreEqual(card.IndexOf(bill) + 1, card.IndexOf(line), "under the bill");
            var first = card.Q(className: "buttons").Children().First();
            Assert.AreEqual(AutopilotView.AddCredits, first.Q<Label>().text, "Add credits first in the buttons");
            Assert.IsTrue(first.ClassListContains("primary"));
        }

        /// <summary>Out of Aura credits: Aura's words in a red callout right over the composer's box, with Add credits
        /// when Aura names where; Run stays as it is (Aura checks again when a run starts); gone once an account frame
        /// says none.</summary>
        [Test]
        public void TheComposerSaysWhenTheTeamIsOutOfCredits()
        {
            PlaytestSettings.Get().Prompt = "Verify that doors open";
            var c = Client();
            c.TestReceive(AuraAccount(Credits(CreditsUrl)));
            Check("composer, out of credits", null, v => v.TestShowComposer(ServiceUrl), NoCredits, AutopilotView.AddCredits, "Run", "TIME LIMIT");
            VisualElement Callout() => host.Q(className: "credits");
            foreach (var size in Sizes)
            {
                Show(size, null, v => v.TestShowComposer(ServiceUrl));
                var callout = Callout();
                var box = host.Q(className: "composer");
                Assert.IsTrue(Shown(callout, host), $"shown at {size}");
                Assert.IsTrue(callout.ClassListContains("problem") && callout.ClassListContains("red"), "the red alert's tint");
                Assert.AreEqual(AutopilotUI.Hex("#2c1715"), callout.resolvedStyle.backgroundColor);
                Assert.AreEqual(AutopilotUI.Hex("#7a3b36"), callout.resolvedStyle.borderTopColor);
                Assert.AreEqual(AutopilotUI.Hex("#f0b2ac"), callout.Q<Label>(className: "credits-words").resolvedStyle.color);
                // Right over the box, as wide as it
                var slot = callout.parent;
                Assert.AreEqual(slot.parent.IndexOf(slot) + 1, slot.parent.IndexOf(box), "the box next");
                Assert.LessOrEqual(callout.worldBound.yMax, box.worldBound.yMin + 0.5f, $"over the box at {size}");
                Assert.AreEqual(box.worldBound.width, callout.worldBound.width, 0.5f, $"as wide as the box at {size}");
                var add = callout.Query(className: "btn").ToList();
                Assert.AreEqual(1, add.Count);
                Assert.AreEqual(AutopilotView.AddCredits, add[0].Q<Label>().text);
                // Run as without it
                var run = host.Q(className: "run-button");
                Assert.IsTrue(run.enabledSelf, "Run enabled");
                Assert.IsFalse(run.ClassListContains("off"), "Run not greyed");
            }

            // Aura named no page: its words, no button (brought up to date in place: the box isn't built again)
            var view = Show(new Vector2Int(360, 640), null, v => v.TestShowComposer(ServiceUrl));
            var prompt = host.Q<TextField>(className: "prompt-input");
            c.TestReceive(AuraAccount(Credits(null)));
            view.TestTickComposer();
            Layout();
            Assert.AreSame(prompt, host.Q<TextField>(className: "prompt-input"), "the same box");
            Assert.IsTrue(Shown(Callout(), host));
            StringAssert.Contains(NoCredits, Words());
            Assert.AreEqual(0, Callout().Query(className: "btn").ToList().Count, "no Add credits");
            Assert.IsEmpty(Problems("composer, out of credits, no page", new Vector2Int(360, 640), out _));
            Check("composer, out of credits, no page", null, v => v.TestShowComposer(ServiceUrl), NoCredits, "Run");
            Assert.AreEqual(0, Callout().Query(className: "btn").ToList().Count, "no Add credits");

            // Credits again: gone, no room taken
            view = Show(new Vector2Int(360, 640), null, v => v.TestShowComposer(ServiceUrl));
            c.TestReceive(AuraAccount(""));
            view.TestTickComposer();
            Layout();
            Assert.IsNull(Callout(), "gone");
            Assert.AreEqual(DisplayStyle.None, host.Q(className: "credits-slot").resolvedStyle.display, "no room taken");
            StringAssert.DoesNotContain(NoCredits, Words());
            // (signed out, or not through Aura: never)
            c.TestReceive($"{{\"type\": \"account\", \"mode\": \"aura\", \"email\": \"{Email}\", \"signed_in\": false{Credits(CreditsUrl)}}}");
            view.TestTickComposer();
            Assert.IsNull(Callout(), "signed out");
            c.TestReceive($"{{\"type\": \"account\", \"mode\": \"own_keys\", \"signed_in\": false, \"keys\": {{}}{Credits(CreditsUrl)}}}");
            view.TestTickComposer();
            Assert.IsNull(Callout(), "BYOK");
        }

        /// <summary>Share runs, Research agent and Autopilot in Claude Code &amp; Codex look alike: each card is its
        /// title (the mono micro-label), the line under it in the sharing text's style, then its control (a switch
        /// with no words of its own, the commands with Copy), with the same gaps; no Time limit card (the composer has
        /// it).</summary>
        [Test]
        public void TheSettingsCardsAreTitleLineThenControl()
        {
            Assert.AreEqual("Share runs to improve Autopilot", AutopilotView.ShareTitle);
            Assert.AreEqual("Research agent", AutopilotView.ResearchTitle);
            Show(new Vector2Int(560, 900), Settings);
            Assert.IsNull(host.Q(className: "time-limit"), "no Time limit card");
            var cards = new[]
            {
                ("sharing", "SHARE RUNS TO IMPROVE AUTOPILOT", ShareLine), ("research", "RESEARCH AGENT", ResearchLine),
                ("assistants", "AUTOPILOT IN CLAUDE CODE & CODEX", AutopilotView.AssistantsLine),
            };
            var shareLine = host.Q(className: "sharing").Children().ElementAt(1);
            var titles = new List<VisualElement>();
            foreach (var (cls, title, line) in cards)
            {
                var card = host.Q(className: cls);
                Assert.IsNotNull(card, cls);
                Assert.That(card.ClassListContains("plate") && card.ClassListContains("setting"), $"{cls}: a setting's plate");
                var parts = card.Children().ToList();
                Assert.AreEqual(3, parts.Count, $"{cls}: title, line, control");
                var t = parts[0] as Label;
                var l = parts[1] as Label;
                Assert.IsNotNull(t, $"{cls}: the title is a label");
                Assert.IsNotNull(l, $"{cls}: the line is a label");
                Assert.AreEqual(title, t.text, $"{cls}: the title");
                Assert.That(t.ClassListContains("lbl"), $"{cls}: the title is the mono micro-label");
                Assert.AreEqual(line, l.text, $"{cls}: the line");
                titles.Add(t);
                // (the line in the sharing text's font, size and colour)
                Assert.AreEqual(shareLine.resolvedStyle.fontSize, l.resolvedStyle.fontSize, $"{cls}: the line's size");
                Assert.AreEqual(shareLine.resolvedStyle.unityFontDefinition, l.resolvedStyle.unityFontDefinition, $"{cls}: the line's font");
                Assert.AreEqual(shareLine.resolvedStyle.color, l.resolvedStyle.color, $"{cls}: the line's colour");
                // (title, line and control one under another, 6 px then 10 px apart)
                var control = parts[2];
                Assert.That(control.ClassListContains("setting-control"), $"{cls}: the control last");
                Assert.AreEqual(6, l.worldBound.yMin - t.worldBound.yMax, 0.5f, $"{cls}: title to line");
                Assert.AreEqual(10, control.worldBound.yMin - l.worldBound.yMax, 0.5f, $"{cls}: line to control");
                Assert.AreEqual(t.worldBound.xMin, l.worldBound.xMin, 0.5f, $"{cls}: one left edge");
                Assert.AreEqual(t.worldBound.xMin, control.worldBound.xMin, 0.5f, $"{cls}: one left edge");
            }
            foreach (var t in titles)
            {
                Assert.AreEqual(titles[0].resolvedStyle.unityFontDefinition, t.resolvedStyle.unityFontDefinition, "one title font");
                Assert.AreEqual(titles[0].resolvedStyle.fontSize, t.resolvedStyle.fontSize, "one title size");
            }

            // Share runs: the switch, then 8 px, "Data sharing on/off" (as it is now), centred with the pill
            var toggle = host.Q(className: "sharing").Q(className: "setting-control").Q<Toggle>(className: "share-toggle");
            Assert.IsNotNull(toggle);
            Assert.AreEqual("Share runs to improve Autopilot", toggle.tooltip);
            Assert.AreEqual(Preferences.Current.ShareData, toggle.value);
            Assert.AreEqual(AutopilotView.ShareState(toggle.value), toggle.text);
            Assert.AreEqual("Data sharing on", AutopilotView.ShareState(true));
            Assert.AreEqual("Data sharing off", AutopilotView.ShareState(false));
            var pill = toggle.Q(className: "unity-toggle__checkmark");
            var words = toggle.Q<Label>(className: "unity-toggle__text");
            Assert.IsNotNull(pill);
            Assert.IsNotNull(words);
            Assert.AreEqual(8, words.worldBound.xMin - pill.worldBound.xMax, 0.5f, "8 px from the pill to its words");
            Assert.AreEqual(pill.worldBound.center.y, words.worldBound.center.y, 2f, "its words centred with the pill");
            // (toggled: the words follow at once, and the change is passed on; a switch of its own, so the test
            // saves no preferences)
            var got = new List<bool>();
            var lone = AutopilotView.ShareSwitch(false, got.Add);
            host.Add(lone);
            lone.value = true;
            Assert.AreEqual("Data sharing on", lone.text, "the words follow the switch");
            lone.value = false;
            Assert.AreEqual("Data sharing off", lone.text, "and back");
            CollectionAssert.AreEqual(new[] { true, false }, got);
            host.Remove(lone);

            // Research agent: the same switch, "Research agent on/off" (as it is now: nothing chosen, this plugin's
            // own agent service, so on)
            var research = host.Q(className: "research").Q(className: "setting-control").Q<Toggle>(className: "research-toggle");
            Assert.IsNotNull(research);
            Assert.AreEqual("Research agent", research.tooltip);
            Assert.IsNull(Preferences.Current.ResearchAgent, "not chosen");
            Assert.IsFalse(PlaytestSettings.Get().UseWithAura);
            Assert.IsTrue(research.value, "Use with Aura off, nothing chosen: on");
            Assert.AreEqual(AutopilotView.ResearchState(research.value), research.text);
            Assert.AreEqual("Research agent on", AutopilotView.ResearchState(true));
            Assert.AreEqual("Research agent off", AutopilotView.ResearchState(false));
            var researchPill = research.Q(className: "unity-toggle__checkmark");
            var researchWords = research.Q<Label>(className: "unity-toggle__text");
            Assert.IsNotNull(researchPill);
            Assert.IsNotNull(researchWords);
            Assert.AreEqual(8, researchWords.worldBound.xMin - researchPill.worldBound.xMax, 0.5f, "8 px from the pill to its words");
            Assert.AreEqual(researchPill.worldBound.center.y, researchWords.worldBound.center.y, 2f, "its words centred with the pill");
            Assert.AreEqual(pill.worldBound.width, researchPill.worldBound.width, 0.5f, "the sharing switch's pill");
            got.Clear();
            lone = AutopilotView.ResearchSwitch(true, got.Add);
            host.Add(lone);
            lone.value = false;
            Assert.AreEqual("Research agent off", lone.text, "the words follow the switch");
            lone.value = true;
            Assert.AreEqual("Research agent on", lone.text, "and back");
            CollectionAssert.AreEqual(new[] { false, true }, got);
            host.Remove(lone);
            // Claude Code & Codex: the two commands, 6 px apart
            var rows = host.Q(className: "assistants").Q(className: "setting-control").Query(className: "connect-command").ToList();
            Assert.AreEqual(2, rows.Count);
            Assert.AreEqual(6, rows[1].worldBound.yMin - rows[0].worldBound.yMax, 0.5f, "between the commands");
        }

        /// <summary>What the Research agent switch shows with nothing chosen: off for runs through the Aura app (Use
        /// with Aura), on for this plugin's own agent service, signed in to Aura or with the user's own keys; a choice
        /// beats both. (SetUp turns Use with Aura off and TearDown puts it back.)</summary>
        [Test]
        public void TheResearchAgentSwitchFollowsUseWithAuraUntilChosen()
        {
            Assert.IsFalse(AutopilotView.ResearchShown(null, true), "through the Aura app: off");
            Assert.IsTrue(AutopilotView.ResearchShown(null, false), "this plugin's own agent service: on");
            Assert.IsTrue(AutopilotView.ResearchShown(true, true), "the choice beats Use with Aura");
            Assert.IsFalse(AutopilotView.ResearchShown(false, false), "the choice beats the own agent service");

            Toggle Switch()
            {
                Show(new Vector2Int(560, 900), Settings);
                return host.Q(className: "research").Q<Toggle>(className: "research-toggle");
            }

            // Use with Aura on, nothing chosen: off
            PlaytestSettings.Get().UseWithAura = true;
            var toggle = Switch();
            Assert.IsFalse(toggle.value, "Use with Aura, nothing chosen: off");
            Assert.AreEqual("Research agent off", toggle.text);

            // Use with Aura off: on, whatever the account (signed in to Aura, before any account frame, own keys)
            PlaytestSettings.Get().UseWithAura = false;
            Preferences.Update(new Dictionary<string, object> { ["access"] = Preferences.AccessAura });
            Assert.IsTrue(Switch().value, "access aura, no account frame: on");
            var c = Client();
            c.TestReceive($"{{\"type\": \"account\", \"mode\": \"aura\", \"email\": \"{Email}\", \"signed_in\": true}}");
            toggle = Switch();
            Assert.IsTrue(toggle.value, "signed in to Aura on the own agent service: on");
            Assert.AreEqual("Research agent on", toggle.text);
            c.TestReceive("{\"type\": \"account\", \"mode\": \"own_keys\", \"signed_in\": false, \"keys\": {}}");
            Assert.IsTrue(Switch().value, "own keys: on");
            Assert.IsNull(Preferences.Current.ResearchAgent, "showing saves nothing");

            // A choice beats both: on through the Aura app, off on the own agent service
            Preferences.Update(new Dictionary<string, object> { ["research_agent"] = true });
            PlaytestSettings.Get().UseWithAura = true;
            Assert.IsTrue(Switch().value, "chosen on, Use with Aura");
            Preferences.Update(new Dictionary<string, object> { ["research_agent"] = false });
            PlaytestSettings.Get().UseWithAura = false;
            toggle = Switch();
            Assert.IsFalse(toggle.value, "chosen off, own agent service");
            Assert.AreEqual("Research agent off", toggle.text);
        }

        /// <summary>Flipping the Research agent switch saves the choice in preferences.json (merged: the other keys
        /// stay), and from then on the page shows it, whatever Use with Aura says.</summary>
        [Test]
        public void TheResearchAgentSwitchSavesTheChoice()
        {
            Preferences.Update(new Dictionary<string, object> { ["share_data"] = false });
            PlaytestSettings.Get().UseWithAura = true;  // (put back by TearDown)
            Show(new Vector2Int(560, 900), Settings);
            var toggle = host.Q(className: "research").Q<Toggle>(className: "research-toggle");
            Assert.IsFalse(toggle.value, "Use with Aura, nothing chosen: off");
            Assert.IsNull(Preferences.Research(), "the hello leaves it out");
            toggle.value = true;
            var raw = Preferences.ReadRaw();
            Assert.AreEqual(true, raw["research_agent"]);
            Assert.AreEqual(true, raw["onboarded"], "the other keys stay");
            Assert.AreEqual(false, raw["share_data"], "the other keys stay");
            Assert.AreEqual(true, Preferences.Research(), "the next hello says it");
            Assert.AreEqual("Research agent on", toggle.text);

            // Shown again: on, as saved, though through the Aura app; then off, saved too, and kept with Use with
            // Aura off
            Show(new Vector2Int(560, 900), Settings);
            toggle = host.Q(className: "research").Q<Toggle>(className: "research-toggle");
            Assert.IsTrue(toggle.value);
            Assert.AreEqual("Research agent on", toggle.text);
            toggle.value = false;
            Assert.AreEqual(false, Preferences.ReadRaw()["research_agent"]);
            Assert.AreEqual(false, Preferences.Research());
            Assert.AreEqual("Research agent off", toggle.text);
            PlaytestSettings.Get().UseWithAura = false;
            Show(new Vector2Int(560, 900), Settings);
            Assert.IsFalse(host.Q(className: "research").Q<Toggle>(className: "research-toggle").value, "the choice beats the own agent service");
        }

        // -- Autopilot in Claude Code & Codex: a run started there, and how to set them up ---------------------------

        [Test]
        public void TheSettingsPageSaysHowToRunFromClaudeCodeOrCodex()
        {
            Check("Settings, Autopilot in Claude Code & Codex", Settings, null, "Autopilot in Claude Code & Codex",
                "One-time CLI setup to run Autopilot through Claude Code or Codex using your Claude or ChatGPT subscriptions, respectively.",
                "autopilot connect claude", "autopilot connect codex");
            Assert.AreEqual("One-time CLI setup to run Autopilot through Claude Code or Codex using your Claude or ChatGPT subscriptions, respectively.", AutopilotView.AssistantsLine);

            // After Data sharing and Research agent, before Uninstall Autopilot; nothing under the commands
            Show(new Vector2Int(560, 900), Settings);
            var words = Words();
            StringAssert.DoesNotContain("Then ask it", words);
            StringAssert.DoesNotContain("Runs still use", words);
            var sharing = words.IndexOf(ShareLine, StringComparison.Ordinal);
            var research = words.IndexOf(ResearchLine, StringComparison.Ordinal);
            var card = words.IndexOf("Autopilot in Claude Code & Codex", StringComparison.OrdinalIgnoreCase);
            var remove = words.IndexOf("Uninstall Autopilot", StringComparison.OrdinalIgnoreCase);
            Assert.That(sharing >= 0 && research > sharing && card > research && remove > card,
                $"sharing {sharing}, research agent {research}, the card {card}, uninstall {remove}");

            // Each command's Copy puts it on the clipboard
            var copied = EditorGUIUtility.systemCopyBuffer;
            try
            {
                var rows = host.Q(className: "assistants").Query(className: "connect-command").ToList();
                Assert.AreEqual(2, rows.Count);
                foreach (var row in rows)
                {
                    var command = row.Q<Label>(className: "code").text;
                    EditorGUIUtility.systemCopyBuffer = "";
                    Press(row.Query(className: "btn").ToList().Single(b => b.Q<Label>()?.text == "Copy"));
                    Assert.AreEqual(command, EditorGUIUtility.systemCopyBuffer);
                }
                CollectionAssert.AreEqual(new[] { "autopilot connect claude", "autopilot connect codex" },
                    rows.Select(r => r.Q<Label>(className: "code").text).ToArray());
            }
            finally
            {
                EditorGUIUtility.systemCopyBuffer = copied;
            }
        }

        [Test]
        public void ARunStartedFromClaudeCodeShowsWhereItCameFrom()
        {
            var c = Client();
            AutopilotSession.TestHook(c);
            var mine = new List<AutopilotRun>();
            try
            {
                var going = AutopilotSession.Current;
                if (going != null) going.Ended = AutopilotText.UnixNow;
                c.TestReceive("{\"type\": \"run_started\", \"run\": {\"kind\": \"verify\", \"claim\": \"Verify that the crate turns green\", \"by\": \"claude-code\"}}");
                var run = AutopilotSession.Current;
                Assert.IsNotNull(run);
                mine.Add(run);
                // Live, as a run the window started: its request, then where it came from
                Check("live, from Claude Code", null, null, "LIVE", "Stop", "Verify that the crate turns green", "Started from Claude Code");
                // The result keeps the line under the request
                c.TestReceive("{\"type\": \"result\", \"result\": {\"verdict\": \"CONFIRMED\", \"reason\": \"It turns green.\"}}");
                Check("result, from Claude Code", v => v.SelectedId = run.Id, null, "CONFIRMED", "Verify that the crate turns green", "Started from Claude Code");
                Show(new Vector2Int(560, 900), v => v.SelectedId = run.Id);
                var words = Words();
                Assert.Greater(words.IndexOf("Started from Claude Code", StringComparison.Ordinal), words.IndexOf("Verify that the crate turns green", StringComparison.Ordinal), "under the request");

                // While the first-run setup is open it stays open
                c.TestReceive("{\"type\": \"run_started\", \"run\": {\"kind\": \"goal\", \"goal\": \"Light a lantern\", \"by\": \"codex\"}}");
                mine.Add(AutopilotSession.Current);
                var view = Show(new Vector2Int(560, 900), v => Setup(v, 3, AutopilotView.ChoiceAura));
                Assert.IsTrue(view.SetupOpen);
                words = Words();
                StringAssert.Contains("Privacy settings", words);
                StringAssert.DoesNotContain("Started from Codex", words);
                StringAssert.DoesNotContain("Light a lantern", words);
            }
            finally
            {
                AutopilotSession.TestHook(clientWas);
                foreach (var r in mine.Where(r => r != null))
                {
                    if (r.Active) r.Ended = AutopilotText.UnixNow;
                    AutopilotSession.Runs.Remove(r);
                }
            }
        }

        // -- removing Autopilot from this computer ------------------------------------------------------------------

        [Test]
        public void RemovingAutopilotFromThisComputer()
        {
            const string removeLine = "Removes the agent service, your keys, your Aura sign-in and your setup choices, for every project.";
            Assert.AreEqual(removeLine, AutopilotView.RemoveLine);
            // The Settings page's last card: its name and the button (what it removes only in the confirm)
            notSaid.Add(removeLine);
            Check("Settings, Uninstall Autopilot", Settings, null, "Uninstall Autopilot", "Uninstall");
            notSaid.Clear();
            // Confirmed in place, deleting what Autopilot learned only when ticked (never ticked at first)
            const string deleteLabel = "[Optional] Delete Autopilot’s memories for all projects in this machine across all editors. I understand that this action is destructive and deletes Autopilot’s memories for my entire team.";
            Assert.AreEqual(deleteLabel, AutopilotView.DeleteMemoryLabel);
            notSaid.AddRange(new[] { "Shared run data already sent", "Shared runs not sent yet", "Keep Autopilot's memories" });
            Check("Settings, removing", v =>
            {
                Settings(v);
                v.TestConfirmRemove();
            }, null, "Uninstall Autopilot", "Uninstall Autopilot from this computer?", removeLine, deleteLabel, "Uninstall", "Cancel");
            notSaid.Clear();
            var view = Show(new Vector2Int(560, 900), v =>
            {
                Settings(v);
                v.TestConfirmRemove();
            });
            Toggle DeleteBox() => host.Query<Toggle>().ToList().Single(t => t.text == AutopilotView.DeleteMemoryLabel);
            Assert.IsFalse(DeleteBox().value, "kept unless ticked");
            var cards = host.Query(className: "card-sec").ToList();
            Assert.IsTrue(cards.Last().ClassListContains("remove-card"), "the last card");
            Assert.IsTrue(cards.Last().ClassListContains("confirm"));
            // Ticked, then Cancel: asked again, it is unticked
            DeleteBox().value = true;
            Press(host.Query(className: "btn").ToList().Single(b => b.Q<Label>()?.text == "Cancel"));
            Layout();
            // (the ask button, never the confirm's: both say Uninstall; the confirm is closed by now)
            Assert.IsNull(host.Q(className: "uninstall-confirm"), "the confirm closed");
            Press(host.Query(className: "uninstall-ask").ToList().Single());
            Layout();
            Assert.IsFalse(DeleteBox().value, "unticked each time it asks");
            // In a narrow window its words wrap beside the box, inside the card
            Show(new Vector2Int(320, 900), v =>
            {
                Settings(v);
                v.TestConfirmRemove();
            });
            var words = DeleteBox().Q<Label>(className: "unity-toggle__text");
            var card = host.Q(className: "remove-card");
            Assert.IsNotNull(words);
            Assert.LessOrEqual(words.worldBound.xMax, card.worldBound.xMax + 0.5f, "inside the card");
            Assert.Greater(words.worldBound.height, 2.5f * words.resolvedStyle.fontSize, "on more than one line");
            var need = words.MeasureTextSize(words.text, words.contentRect.width, VisualElement.MeasureMode.AtMost, 0, VisualElement.MeasureMode.Undefined);
            Assert.LessOrEqual(need.x, words.contentRect.width + 1f, "every line fits");

            // While a run goes: greyed, saying why
            var c = Client();
            c.StartRun(new Dictionary<string, object> { ["kind"] = "goal", ["goal"] = "Reach the gate" });
            Show(new Vector2Int(560, 900), Settings);
            var remove = host.Query(className: "uninstall-ask").ToList().Single();
            Assert.AreEqual("Uninstall", remove.Q<Label>().text);
            Assert.IsTrue(remove.ClassListContains("off"));
            Assert.AreEqual(AutopilotView.AccountBusyTip, remove.tooltip);
            client.Disconnect();
            client = null;

            // How it went, in place of everything else (no run history, no setup: preferences.json may be gone)
            notSaid.AddRange(new[] { "Run history", "History", "New run", "Rerun setup", "Welcome to Autopilot" });
            Preferences.Update(new Dictionary<string, object> { ["onboarded"] = false });
            Uninstall.TestState(Uninstall.Phase.Running);
            Check("removing", null, null, "Uninstalling Autopilot from this computer…");
            Uninstall.TestState(Uninstall.Phase.Removed, Path.Combine(folder, "autopilot-uninstall-20261019-120000.log"));
            Check("removed", null, null, "Uninstallation successful", AutopilotView.RemovedNext, "Set up again", "View logs");
            Uninstall.TestState(Uninstall.Phase.RemovedButSome);
            Check("removed, but for some files", null, null, "Uninstallation successful", AutopilotView.RemovedNext, "View logs",
                "Set up again");
            Uninstall.TestState(Uninstall.Phase.Failed);
            Check("couldn't remove", null, null, "Couldn’t uninstall Autopilot from this computer.", "View logs", "Try again", "Cancel");
            notSaid.Clear();

            // Set up again: the first-run setup from its first step
            Uninstall.TestState(Uninstall.Phase.Removed);
            view = Show(new Vector2Int(560, 900), null);
            view.SetUpAgain();
            Assert.IsFalse(Uninstall.Holds);
            Assert.IsTrue(view.SetupOpen);
            Assert.AreEqual(1, view.SetupStep);
            Layout();
            StringAssert.Contains("Welcome to Autopilot", Words());
        }

        static UsageEntry Entry(string id, double started, string kind, string prompt, Dictionary<string, object> bill)
        {
            return new UsageEntry
            {
                Id = id, Started = started, Kind = kind, Prompt = prompt, Bill = bill,
                Usage = Json.ParseObject("{\"final\": true, \"models\": [{\"kind\": \"llm\", \"model\": \"claude-opus-5-5\", \"calls\": 1234, \"input\": 123456789, \"output\": 9876543, " +
                                         "\"cache_read\": 987654321, \"cache_write\": 12345678}, {\"kind\": \"decision\", \"model\": \"a-decision-model-with-a-long-name-v12\", \"calls\": 456789, \"input\": 98765432, \"output\": 456789}]}"),
            };
        }

        static Dictionary<string, object> Bill(double usd, bool billed = true) => Billing.Kept(Json.ParseObject($"{{\"usd\": {usd.ToString(System.Globalization.CultureInfo.InvariantCulture)}, \"billed\": {(billed ? "true" : "false")}}}"));

        [Test]
        public void SettingsPageShowsTheBills()
        {
            var now = AutopilotText.UnixNow;
            const string longPrompt = "Verify that opening the chest beside the second bridge gives the player exactly one coin and the counter goes up by one";
            var entries = AutopilotUsage.Entries;
            entries.Add(Entry("layout-u1", now - 4000, "verify", "Verify that x", null));  // (BYOK: no bill)
            entries.Add(Entry("layout-u2", now - 3000, "goal", "Reach the gate", Bill(0.593177)));
            entries.Add(Entry("layout-u3", now - 2000, "explore", "Explore for 5 minutes", Bill(0.004)));
            entries.Add(Entry("layout-u4", now - 1500, "verify", "Verify that y", Bill(7, false)));  // (Aura bills it later: not shown)
            entries.Add(Entry("layout-u5", now - 1000, "verify", longPrompt, Bill(1234567.891)));
            var total = AutopilotUsage.TotalBilled(entries).Value;
            Assert.AreEqual("$1,234,568.49", Billing.Money(total));

            // This project: how many runs and the sum Aura billed; each run's row (Time, Prompt, Billed) its own bill. No
            // Selected run and no per-model table any more (the design removed them)
            notSaid.AddRange(new[] { "$7.00", "No usage yet", "Selected run", "SELECTED RUN", "claude-opus-5-5", "a-decision-model", "Cache read", "CALLS" });
            Check("Settings, bills", Settings, null, "This project · 5 runs", "Billed by Aura", "$1,234,568.49", "Time", "Prompt", "Billed",
                "$1,234,567.89", "$0.59", "< $0.01", "Reach the gate");
            notSaid.Clear();

            // A BYOK run: its Billed "—" (This project still sums the Aura runs'); the rows are read, not picked
            Show(new Vector2Int(560, 900), Settings);
            var rows = host.Query(className: "urow").ToList().Where(r => !r.ClassListContains("head")).ToList();
            Assert.AreEqual(5, rows.Count, "a row per run");
            var byok = rows.Single(r => r.Query<Label>().ToList().Any(l => l.text == "Verify that x"));
            CollectionAssert.Contains(byok.Query<Label>().ToList().Select(l => l.text).ToList(), "—", "a BYOK run shows no bill");
            foreach (var row in rows)
            {
                Assert.IsFalse(row.ClassListContains("click") || row.ClassListContains("on"), "no row pickable or lit");
                var before = row.resolvedStyle.backgroundColor;
                using (var down = PointerDownEvent.GetPooled())
                {
                    down.target = row;
                    row.SendEvent(down);
                }
                Layout();
                Assert.AreEqual(before, row.resolvedStyle.backgroundColor, "pressing a row changes nothing");
            }
            Assert.AreEqual(0, host.Query(className: "urow").ToList().Count(r => r.ClassListContains("on")), "nothing lit");
        }

        // -- the runs: connecting, the composer, a run live, its result ----------------------------------------------

        [Test]
        public void RunPagesFit()
        {
            notSaid.AddRange(new[] { "Start the agent service automatically", "Connect when the editor starts", "EXPORT PROJECT INDEX", "Export project index",
                "Plays your game in the editor", "Use with Aura", "USE WITH AURA" });
            Check("connect", null, null, "Agent service", "Not connected", "Start the agent service with", "autopilot serve", "Reconnect", "ADVANCED");
            notSaid.Clear();
            PlaytestSettings.Get().Prompt = "";
            notSaid.AddRange(new[] { "MODE", "DESIGN BRIEF", "CLAIM", "MINUTES", "DURATION", "Runs as", "RUNS AS", "remembers what each run learns",
                "Start with Verify… to check a claim", "EXPORT PROJECT INDEX" });
            Check("composer", null, v => v.TestShowComposer(ServiceUrl), "New run", AutopilotView.PromptPlaceholder, "TIME LIMIT", "Run", "Connected", "Disconnect");
            notSaid.Clear();

            var now = AutopilotText.UnixNow;
            var done = new AutopilotRun
            {
                Id = "layout-done", Kind = "verify", Started = now - 95, Ended = now,
                Prompt = "Opening the chest beside the second bridge gives the player exactly one coin, and the coin counter in the top corner goes up by one",
                Result = Json.ParseObject("{\"verdict\": \"confirmed\", \"seconds\": 95, \"attempts\": 2, \"reason\": \"The counter went from 0 to 1 when the chest opened, and stayed at 1.\", " +
                                          "\"evidence\": [\"A3 12.4 s opened the chest at the second bridge\", \"A3 12.9 s coin counter 0 -> 1\"], " +
                                          "\"findings\": [\"The chest can be opened twice. Pressing E again during its animation opens it a second time without a coin.\"]}"),
            };
            done.Findings.Add(Json.ParseObject("{\"source\": \"engine\", \"severity\": \"warning\", \"detector\": \"physics.fell_through_floor\", \"summary\": \"The player fell through the floor near the second bridge\", \"where\": {\"X\": 120, \"Y\": -40, \"Z\": 5}, \"signature\": \"s1\", \"count\": 3}"));
            AutopilotSession.Runs.Add(done);
            // (Run again, Copy report and Run folder: icons, their names in their tooltips: TheRunsActionsAreIconsOnItsIdLine)
            notSaid.AddRange(new[] { "Run again", "Copy report", "Run folder", "RUN AGAIN", "COPY REPORT", "RUN FOLDER" });
            Check("result", v => v.SelectedId = done.Id, null, "CONFIRMED", "Agent finding", "Verify a fix", "Show in level",
                "The counter went from 0 to 1");
            notSaid.Clear();
            // The verdict over its reason, on one left edge
            foreach (var size in Sizes)
            {
                Show(size, v => v.SelectedId = done.Id);
                var verdictBox = host.Q(className: "verdict");
                var reason = host.Query<Label>().ToList().First(l => l.text.StartsWith("The counter went", StringComparison.Ordinal));
                Assert.Greater(reason.worldBound.yMin, verdictBox.worldBound.yMax - 0.5f, $"the reason under the verdict at {size}");
                Assert.AreEqual(verdictBox.worldBound.xMin, reason.worldBound.xMin, 0.5f, $"one left edge at {size}");
            }

            var live = new AutopilotRun { Id = "layout-live", Kind = "explore", Started = now - 40, Prompt = "Explore for 20 minutes", Stage = 4, Attempt = 3, AttemptLines = 3, Now = "Walking to the gate on the far side of the courtyard" };
            live.Feed.Add(new AutopilotFeedLine { At = 3, Kind = "status", Text = "Launching the environment" });
            live.Feed.Add(new AutopilotFeedLine { At = 30, Kind = "attempt", Text = "Harness 3" });
            AutopilotSession.Runs.Add(live);
            notSaid.AddRange(new[] { "Harnesses so far", "HARNESSES SO FAR" });
            Check("live", v => v.SelectedId = live.Id, null, "LIVE", "Stop", "Harness attempts", "Walking to the gate");
            // (ELAPSED beside the time in a wide run area; the time alone under 520 px)
            Show(new Vector2Int(1400, 900), v => v.SelectedId = live.Id);
            StringAssert.Contains("ELAPSED", Words());
            notSaid.Clear();
        }

        // -- connecting: always by itself; the connect card's states --------------------------------------------------

        [Test]
        public void TheEditorConnectsByItself()
        {
            // Once the first-run setup is done it connects by itself, unless the user disconnected (this editor session),
            // Autopilot is removed from this computer, or it is a batch-mode or command-line run
            Assert.IsTrue(EditorHost.ConnectsByItself(true, false, false, false, null), "set up, no client yet");
            Assert.IsFalse(EditorHost.ConnectsByItself(false, false, false, false, null), "not set up: the setup connects");
            Assert.IsFalse(EditorHost.ConnectsByItself(true, true, false, false, null), "disconnected by hand");
            Assert.IsFalse(EditorHost.ConnectsByItself(true, false, true, false, null), "removed from this computer");
            Assert.IsFalse(EditorHost.ConnectsByItself(true, false, false, true, null), "batch mode");
            var c = Client();
            Assert.IsFalse(EditorHost.ConnectsByItself(true, false, false, false, c), "connected already");
            c.Disconnect();
            Assert.IsTrue(EditorHost.ConnectsByItself(true, false, false, false, c), "a client that isn't trying");
            // (a refused connection waits for Try again)
            c.TestReceive("{\"type\": \"error\", \"code\": 401, \"message\": \"The access token is wrong.\"}");
            if (!string.IsNullOrEmpty(c.Problem)) Assert.IsFalse(EditorHost.ConnectsByItself(true, false, false, false, c), "refused");
            client = null;
        }

        [Test]
        public void TheConnectCardHasNoPlainConnect()
        {
            // Disconnected by hand: Reconnect (never a plain Connect: it connects by itself), the agent service named and
            // where it comes from; the three fields under a folded Advanced
            notSaid.AddRange(new[] { "Start the agent service automatically", "Connect when the editor starts", "Plays your game in the editor" });
            Check("connect, disconnected", null, v => v.TestShowConnect(), "Agent service", "Not connected", "Start the agent service with", "autopilot serve",
                "Reconnect", "ADVANCED");
            notSaid.Clear();
            Show(new Vector2Int(560, 900), null, v => v.TestShowConnect());
            var buttons = host.Query(className: "btn").ToList().Where(b => Shown(b, host)).Select(b => b.Q<Label>()?.text).ToList();
            CollectionAssert.DoesNotContain(buttons, "Connect");
            CollectionAssert.Contains(buttons, "Reconnect");
            var advanced = host.Q<Foldout>(className: "advanced");
            Assert.IsNotNull(advanced);
            Assert.IsFalse(advanced.value, "Advanced folded at first");
            advanced.value = true;
            Layout();
            var labels = advanced.Query<Label>(className: "field-label").ToList().Select(l => l.text).ToList();
            CollectionAssert.AreEqual(new[] { "AGENT SERVICE URL", "ACCESS TOKEN · OPTIONAL", "AGENT SERVICE COMMAND · OPTIONAL" }, labels);
            var header = advanced.Q<Toggle>();
            Assert.AreEqual(0, advanced.Query<Toggle>().ToList().Count(t => t != header), "no toggles under Advanced");
            Assert.IsEmpty(Problems("connect, Advanced open", new Vector2Int(560, 900), out _));

            // With the Aura app: what runs the agent service
            PlaytestSettings.Get().UseWithAura = true;
            Check("connect, the Aura app", null, v => v.TestShowConnect(), "Agent service", AutopilotView.AuraServiceLine, "Reconnect");
            PlaytestSettings.Get().UseWithAura = false;
        }

        [Test]
        public void TheRunHistoryCountsAttempts()
        {
            var now = AutopilotText.UnixNow;
            var two = new AutopilotRun { Id = "layout-att2", Kind = "goal", Prompt = "Reach the gate", Started = now - 206, Ended = now, Result = Json.ParseObject("{\"reached\": true, \"attempts\": 2}") };
            var one = new AutopilotRun { Id = "layout-att1", Kind = "goal", Prompt = "Sell three crops", Started = now - 400, Ended = now - 300, Result = Json.ParseObject("{\"reached\": false, \"attempts\": 1}") };
            AutopilotSession.Runs.Add(one);
            AutopilotSession.Runs.Add(two);
            Assert.AreEqual("2 attempts", AutopilotView.Attempts(2));
            Assert.AreEqual("1 attempt", AutopilotView.Attempts(1));
            notSaid.AddRange(new[] { " pol", "HARNESSES", "harnesses" });
            Check("history, attempts", v => v.SelectedId = two.Id, v =>
            {
                var pop = (VisualElement)typeof(AutopilotView).GetField("pop", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(v);
                if (!v.Wide) pop.style.display = DisplayStyle.Flex;  // (narrow: the run history's dropdown, opened)
            }, "03:26 · 2 attempts", "01:40 · 1 attempt");
            notSaid.Clear();
            Show(new Vector2Int(1400, 900), v => v.SelectedId = two.Id);
            StringAssert.Contains("DURATION 03:26 // 2 ATTEMPTS", Words());
        }

        // -- the account asked for again ---------------------------------------------------------------------------

        [Test]
        public void TheAccountIsAskedForAgainAfterTheSetup()
        {
            var c = Client();
            Assert.IsTrue(SentAccount(), "the welcome asks for the account");
            // Signed in on step 1: the account frame sent then still says "none" (access isn't "aura" until Finish)
            c.TestReceive("{\"type\": \"account\", \"mode\": \"none\", \"signed_in\": false}");
            c.TestReceive($"{{\"type\": \"aura_sign_in\", \"state\": \"signed_in\", \"email\": \"{Email}\"}}");
            // (the game remembered already: Finish ends the setup, no first run)
            c.TestReceive("{\"type\": \"memory_state\", \"runs\": 2, \"notes\": true, \"lib\": false}");
            var view = Show(new Vector2Int(560, 900), v => Setup(v, 3, AutopilotView.ChoiceAura));
            sent.Clear();
            typeof(AutopilotView).GetMethod("FinishSetup", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(view, null);
            Assert.IsTrue(SentAccount(), "Finish asks for the account");
            Assert.AreEqual(Preferences.AccessAura, Preferences.Current.Access);
            Assert.IsTrue(Preferences.Current.Onboarded);
            Assert.IsFalse(view.SetupOpen, "Finish ended the setup");

            // Finish connected again: the new connection's welcome asks too, and the reply shows on the Settings page
            sent.Clear();
            c.TestConnected(true);
            c.TestReceive("{\"type\": \"welcome\", \"role\": \"editor\", \"session\": \"layout2\", \"features\": [\"account\"]}");
            Assert.IsTrue(SentAccount(), "a new connection asks for the account");
            sent.Clear();
            view.ShowPage("settings");
            Assert.IsTrue(SentAccount(), "opening the Settings page asks for the account");
            c.TestReceive($"{{\"type\": \"account\", \"mode\": \"aura\", \"email\": \"{Email}\", \"signed_in\": true}}");
            view.Refresh(true);
            Layout();
            var words = Words();
            StringAssert.Contains(Email, words);
            StringAssert.Contains("Sign out", words);
            StringAssert.DoesNotContain("SIGNED OUT", words);
            StringAssert.DoesNotContain("NOT SET UP", words);

            sent.Clear();
            view.OpenSetup();
            Assert.IsTrue(SentAccount(), "Rerun setup asks for the account");
        }

        // -- runs are always real time; Reset memory --------------------------------------------------------------

        [TestCase("Claude Code CLI", "claude")]
        [TestCase("Codex CLI", "codex")]
        public void SignedInCliRunsFromTheComposer(string label, string id)
        {
            var c = Client();
            var view = Show(new Vector2Int(360, 640), null, v => v.TestShowComposer(ServiceUrl));
            var picker = host.Q<DropdownField>(className: "run-client");
            CollectionAssert.AreEqual(new[] { "Configured agent", "Claude Code CLI", "Codex CLI" }, picker.choices);
            picker.value = label;
            host.Q<TextField>(className: "prompt-input").value = "Verify that the gate opens";
            c.TestReceive("{\"type\":\"account\",\"mode\":\"own_keys\"}");
            view.TestTickComposer();
            var button = host.Q(className: "run-button");
            Assert.IsFalse(button.enabledSelf);
            Assert.AreEqual(AutopilotStatus.CliUpdate, host.Q<Label>(className: "busy-line").text);
            Assert.AreEqual(id, PlaytestSettings.Load(PlaytestSettings.FilePath).CliClient);
            c.TestReceive("{\"type\":\"account\",\"supports_cli_launch\":true}");
            view.TestTickComposer();
            Assert.IsTrue(button.enabledSelf);
            sent.Clear();
            Press(button);
            var started = AutopilotSession.Current;
            try
            {
                var start = sent.Select(Json.ParseObject).Single(m => Json.Str(m, "type") == "start");
                var frame = Json.Obj(start, "run");
                Assert.AreEqual(id, Json.Str(frame, "cli_client"));
                Assert.AreEqual("Verify that the gate opens", Json.Str(frame, "prompt"));
                Assert.AreEqual(id, Json.Str(AutopilotView.AgainFrame(started.RunFrame), "cli_client"));
            }
            finally
            {
                if (started != null) AutopilotSession.Runs.Remove(started);
            }
        }

        [Test]
        public void CliKeySetupKeepsOnlyTheDecisionKey()
        {
            Client();
            var view = Show(new Vector2Int(360, 640), v => Setup(v, 1));
            var env = Path.Combine(folder, AutopilotEnv.FileName);
            const string before = "AUTOPILOT_AGENT=opencode\nANTHROPIC_API_KEY=existing-test-key\n";
            File.WriteAllText(env, before);
            host.Q<DropdownField>(className: "run-client").value = "Codex CLI";
            Layout();
            Assert.IsNull(host.Q<DropdownField>(className: "coding-agent"));
            Assert.IsNull(host.Q<DropdownField>(className: "llm-provider"));
            host.Query<TextField>(className: "key-input").ToList().Single().value = "jev-test";
            AgentService.TestState(AgentService.Phase.Running, ServiceUrl);
            UseMyKeys.Invoke(view, null);
            Assert.AreEqual(2, view.SetupStep);
            Assert.AreEqual(before + "TYPESAFE_API_KEY=jev-test\n", File.ReadAllText(env));
        }

        [Test]
        public void RunsAreAlwaysRealTime()
        {
            var path = Path.Combine(folder, "AutopilotPlaytest.json");
            File.WriteAllText(path, "{\"url\": \"ws://127.0.0.1:8765/v1/bridge\", \"turn_based\": true, \"mode\": \"goal\", \"prompt\": \"Reach the gate\", \"explore_minutes\": 7}");
            var s = PlaytestSettings.Load(path);
            Assert.AreEqual("Reach the gate", s.Prompt);
            Assert.AreEqual(7, s.ExploreMinutes);
            // (Explore's minutes when none were saved: 10)
            File.WriteAllText(path, "{\"url\": \"ws://127.0.0.1:8765/v1/bridge\", \"prompt\": \"Explore\"}");
            Assert.AreEqual(10, PlaytestSettings.Load(path).ExploreMinutes);
            // (an older version's saved tab, "mode", is read past: the agent service reads the kind from the words)
            Assert.AreEqual("auto", Json.Str(AutopilotView.ComposerFrame(s, out _), "kind"));
            foreach (var prompt in new[] { "Verify that the gate opens", "Reach the gate", "Explore the gate" })
            {
                s.Prompt = prompt;
                var frame = AutopilotView.ComposerFrame(s, out _);
                Assert.IsFalse(frame.ContainsKey("turn_based"), prompt);
                Assert.AreEqual("auto", Json.Str(frame, "kind"), prompt);
            }
        }

        [Test]
        public void TheTimeLimitIsKeptPerProject()
        {
            var path = Path.Combine(folder, "AutopilotPlaytest.json");
            Directory.CreateDirectory(folder);
            // (none saved: 30 minutes; kept to 1-600)
            File.WriteAllText(path, "{\"url\": \"ws://127.0.0.1:8765/v1/bridge\"}");
            Assert.AreEqual(30, PlaytestSettings.Load(path).TimeLimitMinutes);
            File.WriteAllText(path, "{\"time_limit_minutes\": 45}");
            Assert.AreEqual(45, PlaytestSettings.Load(path).TimeLimitMinutes);
            File.WriteAllText(path, "{\"time_limit_minutes\": 0}");
            Assert.AreEqual(1, PlaytestSettings.Load(path).TimeLimitMinutes);
            File.WriteAllText(path, "{\"time_limit_minutes\": 9999}");
            Assert.AreEqual(600, PlaytestSettings.Load(path).TimeLimitMinutes);
            File.WriteAllText(path, "{\"time_limit_minutes\": 1e12}");
            Assert.AreEqual(600, PlaytestSettings.Load(path).TimeLimitMinutes);

            // Saved with the project's other settings (UserSettings/AutopilotPlaytest.json), and sent with its runs
            var s = PlaytestSettings.Get();
            var was = s.TimeLimitMinutes;
            var promptBefore = s.Prompt;
            try
            {
                s.TimeLimitMinutes = 75;
                s.Save();
                var saved = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "UserSettings", "AutopilotPlaytest.json");
                Assert.AreEqual(75, PlaytestSettings.Load(saved).TimeLimitMinutes);
                // (every run: an exploration plays for it)
                foreach (var prompt in new[] { "Verify that the gate opens", "Explore the gate" })
                {
                    s.Prompt = prompt;
                    Assert.AreEqual(75, AutopilotView.ComposerFrame(s, out _)["time_limit_minutes"], prompt);
                }
            }
            finally
            {
                s.TimeLimitMinutes = was;
                s.Prompt = promptBefore;
                s.Save();
            }
        }

        [Test]
        public void ResetMemory()
        {
            var c = Client();
            sent.Clear();
            c.ClearMemory();
            Assert.AreEqual("clear_memory", Json.Str(Json.ParseObject(sent.Last()), "type"));
            c.TestReceive("{\"type\": \"memory_cleared\", \"ok\": true, \"files\": 4}");
            Assert.AreEqual("Memory reset successful", c.LastStatus);
            c.TestReceive("{\"type\": \"memory_cleared\", \"ok\": false, \"message\": \"the folder is in use\"}");
            Assert.AreEqual("Memory not reset: the folder is in use", c.LastStatus);
        }

        [Test]
        public void TheMemoryLineNeverRunsUnderResetMemory()
        {
            // (a long memory line: it shortens with an ellipsis, on one line, beside the button in the rail or the history dropdown)
            Client().TestReceive("{\"type\": \"memory_state\", \"runs\": 123456789, \"notes\": true, \"lib\": true}");
            var sizes = new[] { 300, 320, 340, 360, 420 }.Select(w => new Vector2Int(w, 700)).Concat(Sizes).ToArray();
            foreach (var size in sizes)
            {
                Show(size, null, v =>
                {
                    var pop = (VisualElement)typeof(AutopilotView).GetField("pop", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(v);
                    if (!v.Wide) pop.style.display = DisplayStyle.Flex;  // (narrow: the run history's dropdown, opened)
                });
                var rows = host.Query(className: "savedata").ToList().Where(e => Shown(e, host)).ToList();
                Assert.AreEqual(1, rows.Count, $"one memory line shown at {size}");
                var row = rows[0].worldBound;
                var line = rows[0].Q<Label>(className: "nowrap");
                var reset = rows[0].Q(className: "savedata-reset");
                Assert.IsNotNull(line, $"what it remembers at {size}");
                StringAssert.Contains("123456789 runs", line.text);
                StringAssert.DoesNotContain("past", line.text);
                StringAssert.DoesNotContain("notes", line.text);
                Assert.AreEqual(TextOverflow.Ellipsis, line.resolvedStyle.textOverflow, "it shortens");
                Assert.AreEqual(WhiteSpace.NoWrap, line.resolvedStyle.whiteSpace, "on one line");
                Assert.LessOrEqual(line.worldBound.xMax, reset.worldBound.xMin - 7.5f, $"a gap before Reset memory at {size}");
                Assert.LessOrEqual(reset.worldBound.xMax, row.xMax + 0.5f, $"Reset memory inside its line at {size}");
                Assert.AreEqual(line.worldBound.center.y, reset.worldBound.center.y, 2f, $"on the button's line at {size}");
                var name = rows[0].Q<Label>(className: "savedata-name");
                Assert.LessOrEqual(name.MeasureTextSize(name.text, 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined).x,
                    name.contentRect.width + 1f, $"MEMORY in full at {size}");
                var label = reset.Q<Label>();
                Assert.LessOrEqual(label.MeasureTextSize(label.text, 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined).x,
                    label.contentRect.width + 1f, $"RESET MEMORY… on one line at {size}");
            }
        }

        // -- the dogfooding round -----------------------------------------------------------------------------

        static List<VisualElement> RerunButtons(VisualElement top) =>
            top.Query(className: "rerun-setup").ToList().Where(e => Shown(e, top)).ToList();

        static int CountNamed(VisualElement top, string upper) =>
            top.Query(className: "btn").ToList().Count(b => Shown(b, top) && b.Q<Label>()?.text == upper);

        static void Press(VisualElement button)
        {
            using (var e = KeyDownEvent.GetPooled('\n', KeyCode.Return, EventModifiers.None))
            {
                e.target = button;
                button.SendEvent(e);
            }
        }

        static List<VisualElement> Gears(VisualElement top) =>
            top.Query(className: "settings-button").ToList().Where(e => Shown(e, top)).ToList();

        [Test]
        public void SettingsIsAGearAndRerunSetupIsOnTheSettingsPage()
        {
            // The header (the rail's head when wide, the top bar when narrow): the gear and New run, no Rerun setup, no
            // account chip, whatever the account; the gear a glyph alone, named Settings in its tooltip
            notSaid.AddRange(new[] { "Rerun setup", "RERUN SETUP", "Settings" });
            Check("the header, not set up", null, null, "New run");
            foreach (var size in Sizes)
            {
                var shown = Show(size, null);
                Assert.AreEqual(0, RerunButtons(host).Count, $"no Rerun setup in the header at {size}");
                Assert.AreEqual(0, host.Query(className: "account-chip").ToList().Count, $"no account chip at {size}");
                var gears = Gears(host);
                Assert.AreEqual(1, gears.Count, $"one gear at {size}");
                var gear = gears[0];
                Assert.AreEqual(AutopilotView.SettingsName, gear.tooltip);
                Assert.IsNull(gear.Q<Label>(), "the gear has no words");
                Assert.AreEqual(AutopilotUI.Glyph.Shape.Gear, gear.Q<AutopilotUI.Glyph>().Is);
                Assert.IsFalse(gear.ClassListContains("on"));
                var newRun = gear.parent.Query(className: "btn").ToList().First(b => b.Q<Label>()?.text == "New run");
                Assert.IsTrue(gear.worldBound.xMax <= newRun.worldBound.xMin + 0.5f || gear.worldBound.yMax <= newRun.worldBound.yMin + 0.5f,
                    $"the gear before New run at {size}");
                // Pressed (Enter, as a click does): the Settings page, the gear lit while it shows; pressed again, the runs
                Press(gear);
                Layout();
                Assert.AreEqual("settings", shown.Page, $"the gear opens the Settings page at {size}");
                var lit = Gears(host);
                Assert.AreEqual(1, lit.Count, $"one gear on the Settings page at {size}");
                Assert.IsTrue(lit[0].ClassListContains("on"), $"lit on the Settings page at {size}");
                StringAssert.Contains("Settings", Words());
                Press(lit[0]);
                Layout();
                Assert.AreEqual("runs", shown.Page, $"pressed again: the runs at {size}");
                Assert.IsFalse(Gears(host)[0].ClassListContains("on"), $"not lit on the runs at {size}");
            }
            notSaid.Clear();

            var c = Client();
            const string longEmail = "someone.with.a.very.long.name@a-studio-with-a-long-domain.example";
            c.TestReceive($"{{\"type\": \"account\", \"mode\": \"aura\", \"email\": \"{longEmail}\", \"signed_in\": true}}");
            notSaid.AddRange(new[] { longEmail, "RERUN SETUP" });  // (the header no longer says who runs are billed to)
            Check("the header, Aura", null, null, "New run");
            notSaid.Clear();
            c.TestReceive("{\"type\": \"account\", \"mode\": \"own_keys\", \"signed_in\": false, \"keys\": {\"decision\": null, \"llm\": \"••••3c2e\"}}");
            notSaid.Add("RERUN SETUP");
            Check("the header, BYOK", null, null, "New run");
            notSaid.Clear();

            // It opens the setup from its first step (Account: the login menu), wherever the setup was left
            var view = Show(new Vector2Int(360, 640), null);
            view.SetupStep = 3;
            view.SetupChoice = AutopilotView.ChoiceKeys;
            view.RerunSetup();
            Assert.IsTrue(view.SetupOpen);
            Assert.AreEqual(1, view.SetupStep);
            Assert.AreEqual("", view.SetupChoice);
            Layout();
            StringAssert.Contains("Welcome to Autopilot", Words());

            // The Settings page: one Rerun setup, in the Account card (Sign out stays)
            c.TestReceive($"{{\"type\": \"account\", \"mode\": \"aura\", \"email\": \"{Email}\", \"signed_in\": true}}");
            Show(new Vector2Int(560, 900), v => v.Page = "settings");
            Assert.AreEqual(1, CountNamed(host, "Rerun setup"), "the Account card's");
            Assert.AreEqual(AutopilotView.RerunSetupTip, RerunButtons(host)[0].tooltip);
            Assert.AreEqual(1, CountNamed(host, "Sign out"));
            Assert.AreEqual(0, CountNamed(host, "Change sign-in"));
            Assert.AreEqual(0, CountNamed(host, "Setup…"));

            // While a run goes: greyed, says why, and opens nothing
            c.StartRun(new Dictionary<string, object> { ["kind"] = "goal", ["goal"] = "Reach the gate" });
            Assert.IsTrue(c.RunActive);
            view = Show(new Vector2Int(560, 900), null);
            view.RerunSetup();
            Assert.IsFalse(view.SetupOpen, "no setup while a run goes");
            Show(new Vector2Int(560, 900), v => v.Page = "settings");
            var midRun = RerunButtons(host);
            Assert.AreEqual(1, midRun.Count);
            foreach (var b in midRun)
            {
                Assert.IsTrue(b.ClassListContains("off"), "the Settings page's waits for the run too");
                Assert.AreEqual(AutopilotView.AccountBusyTip, b.tooltip);
            }
        }

        [Test]
        public void TheSelectedRunReadsWell()
        {
            var now = AutopilotText.UnixNow;
            var run = new AutopilotRun { Id = "layout-pick", Kind = "goal", Prompt = "Reach the gate", Started = now - 60, Ended = now, Result = Json.ParseObject("{\"reached\": true}") };
            var other = new AutopilotRun { Id = "layout-other", Kind = "goal", Prompt = "Sell three crops", Started = now - 300, Ended = now - 200, Result = Json.ParseObject("{\"reached\": false}") };
            AutopilotSession.Runs.Add(other);
            AutopilotSession.Runs.Add(run);
            foreach (var size in new[] { new Vector2Int(1400, 900), new Vector2Int(360, 640) })
            {
                Show(size, v => v.SelectedId = run.Id);
                if (size.x <= AutopilotView.WideFrom)
                {
                    // (narrow: the run history's dropdown, opened)
                    host.Q(className: "history-pop").style.display = DisplayStyle.Flex;
                    Layout();
                }
                var rows = host.Query(className: "hist").ToList().Where(r => Shown(r, host)).ToList();
                var on = rows.Single(r => r.ClassListContains("on"));
                var off = rows.First(r => !r.ClassListContains("on"));
                var bg = on.resolvedStyle.backgroundColor;
                // (the Flatness's selected row: #2a1b10 and a 2 px accent rule on its left, never an accent fill under the words)
                Assert.AreEqual(AutopilotUI.Hex("#2a1b10"), bg, $"the selected row's ground at {size}");
                Assert.AreEqual(2f, on.resolvedStyle.borderLeftWidth, 0.01f, "a rule on its left");
                Assert.AreEqual(AutopilotUI.Accent, on.resolvedStyle.borderLeftColor);
                Assert.AreEqual(AutopilotUI.Text, on.Q<Label>(className: "hist-title").resolvedStyle.color, "its words high-contrast");
                // (its verdict tag as in any other row: tinted, Goal hit's surface)
                Assert.AreEqual(AutopilotUI.Hex("#16281a"), on.Q(className: "tag").resolvedStyle.backgroundColor, "the tag tinted");
                Assert.AreEqual(AutopilotUI.Hex("#2c1715"), off.Q(className: "tag").resolvedStyle.backgroundColor, "Goal missed's tag tinted");
            }
        }

        [Test]
        public void TheComposerHasNoKindAndAlwaysATimeLimit()
        {
            PlaytestSettings.Get().Prompt = "";
            PlaytestSettings.Get().TimeLimitMinutes = 45;
            Show(new Vector2Int(360, 640), null, v => v.TestShowComposer(ServiceUrl));
            var box = host.Q<TextField>(className: "prompt-input");
            Assert.IsNotNull(box);
            Assert.AreEqual(AutopilotView.PromptPlaceholder, box.textEdition.placeholder);
            VisualElement RunRow() => host.Q(className: "run-row");
            VisualElement Minutes() => host.Q(className: "minutes-row");
            // (Run sits in the box's last row, the Time limit before it, whatever is typed)
            Assert.AreEqual(DisplayStyle.Flex, RunRow().resolvedStyle.display, "the box's last row");
            var run = RunRow().Query(className: "btn").ToList().Single(b => b.Q<Label>()?.text == "Run");
            Assert.IsTrue(run.ClassListContains("primary"), "Run filled with the accent");
            Assert.IsTrue(box.parent.ClassListContains("composer") && RunRow().parent == box.parent, "Run inside the composer's box");
            // Its default: the one last set (PlaytestSettings.TimeLimitMinutes, per project); each change kept
            var field = Minutes().Q<IntegerField>();
            Assert.AreEqual(45, field.value);
            Assert.AreEqual(AutopilotView.TimeLimitTip, Minutes().tooltip);
            field.value = 9999;
            Assert.AreEqual(600, field.value, "kept to 1-600");
            Assert.AreEqual(600, PlaytestSettings.Get().TimeLimitMinutes);
            field.value = 20;
            Assert.AreEqual(20, PlaytestSettings.Get().TimeLimitMinutes);
            // (saved: in UserSettings/AutopilotPlaytest.json, and the next composer starts with it)
            var saved = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "UserSettings", "AutopilotPlaytest.json");
            Assert.AreEqual(20, PlaytestSettings.Load(saved).TimeLimitMinutes, "saved");
            Show(new Vector2Int(360, 640), null, v => v.TestShowComposer(ServiceUrl));
            Assert.AreEqual(20, Minutes().Q<IntegerField>().value, "the next composer starts with it");
            box = host.Q<TextField>(className: "prompt-input");
            run = RunRow().Query(className: "btn").ToList().Single(b => b.Q<Label>()?.text == "Run");

            foreach (var text in new[] { "", "Verify that doors open", "Explore the game for bugs: farm area", "Reach the castle gate", "Can the player swim?" })
            {
                box.value = text;
                Layout();
                // No kind guessed as you type: no tag; TIME LIMIT and its field at the row's right, before Run
                Assert.IsNull(RunRow().Q(className: "tag"), text);
                Assert.AreEqual(DisplayStyle.Flex, Minutes().resolvedStyle.display, text);
                StringAssert.Contains("TIME LIMIT", Words());
                Assert.LessOrEqual(Minutes().worldBound.xMax, run.worldBound.xMin + 0.5f, "before Run");
                Assert.AreEqual(RunRow().worldBound.xMax, run.worldBound.xMax, 0.5f, "Run at the row's right");
                Assert.AreEqual(text, PlaytestSettings.Get().Prompt, "kept as typed");
                Assert.IsEmpty(Problems(text, new Vector2Int(360, 640), out _));
            }
            // At every size: no kind, the Time limit
            notSaid.AddRange(new[] { "VERIFY", "EXPLORE", "GOAL", "DURATION" });
            PlaytestSettings.Get().Prompt = "Explore the game for bugs: farm area";
            Check("composer, explore", null, v => v.TestShowComposer(ServiceUrl), "TIME LIMIT");
            notSaid.Clear();
        }

        [Test]
        public void TheHarnessAttemptsStayOnOneRow()
        {
            var now = AutopilotText.UnixNow;
            foreach (var n in new[] { 3, 24, 30 })
            {
                var live = new AutopilotRun { Id = "layout-attempts-" + n, Kind = "goal", Started = now - 300, Prompt = "Reach the gate", Stage = AutopilotText.StageRollout, Attempt = n, AttemptLines = n };
                AutopilotSession.Runs.Add(live);
                var sizes = new[] { 300, 320, 360, 400, 560, 700 }.Select(w => new Vector2Int(w, 700)).Concat(Sizes);
                foreach (var size in sizes)
                {
                    Show(size, v => v.SelectedId = live.Id);
                    Layout();
                    var at = $"{n} attempts at {size.x}x{size.y}";
                    var row = host.Q(className: "attempts");
                    Assert.AreEqual(Wrap.NoWrap, row.resolvedStyle.flexWrap, $"one row, never wrapping: {at}");
                    var squares = row.Query(className: "attempt-box").ToList();
                    Assert.AreEqual(Math.Min(n, AutopilotView.MaxAttemptBoxes), squares.Count, $"at most 24 squares: {at}");
                    var shown = squares.Where(q => Shown(q, host)).ToList();
                    Assert.Greater(shown.Count, 0, at);
                    Assert.IsTrue(shown.Last().ClassListContains("current"), $"the one going shown: {at}");
                    var y = shown[0].worldBound.center.y;
                    foreach (var q in shown) Assert.AreEqual(y, q.worldBound.center.y, 0.5f, $"side by side: {at}");
                    for (var i = 1; i < shown.Count; ++i) Assert.Greater(shown[i].worldBound.xMin, shown[i - 1].worldBound.xMax, $"left to right: {at}");
                    var label = row.Q<Label>(className: "lbl");
                    var count = row.Q<Label>(className: "attempt-count");
                    Assert.AreEqual(y, count.worldBound.center.y, 2f, $"the count on the row: {at}");
                    Assert.AreEqual(n.ToString(), count.text);
                    Assert.Less(row.worldBound.height, 24f, $"one line tall: {at}");
                    Assert.LessOrEqual(label.worldBound.xMax, shown[0].worldBound.xMin + 0.5f, at);
                    if (n <= 24 && size.x >= 900) Assert.AreEqual(squares.Count, shown.Count, $"all shown when they fit: {at}");
                    Assert.IsEmpty(Problems("live, attempts", size, out _));
                }
                live.Ended = now;
            }
        }

        /// <summary>Unreal needed SSettle (Slate showed new content a frame before its layout); UI Toolkit lays a panel
        /// out, and dispatches the geometry changes its pages fit themselves on (the stage names, the attempt squares),
        /// again until it is settled, in the same update, before that update repaints. So a content swap (Live to Result
        /// and back, a new run's Live page) is drawn first as it settles: one layout pass after the swap leaves nothing
        /// for a second one to move.</summary>
        [Test]
        public void ASwappedPageIsSettledInItsFirstLayout()
        {
            var now = AutopilotText.UnixNow;
            var run = new AutopilotRun { Id = "layout-swap", Kind = "goal", Started = now - 200, Prompt = "Reach the gate", Stage = AutopilotText.StageRollout, Attempt = 3, AttemptLines = 3 };
            AutopilotSession.Runs.Add(run);
            foreach (var size in new[] { new Vector2Int(320, 700), new Vector2Int(900, 400) })
            {
                var view = Show(size, v => v.SelectedId = run.Id);
                foreach (var swap in new[] { "result", "live" })
                {
                    run.Ended = swap == "result" ? now : 0;
                    if (swap == "result") run.Result = new Dictionary<string, object> { ["achieved"] = true, ["reason"] = "Reached the gate at 1:12." };
                    view.Refresh(true);
                    validate.Invoke(panel, null);  // (one update's layout, as the frame that first draws it has)
                    var first = host.Query<VisualElement>().ToList().Select(e => e.worldBound).ToList();
                    validate.Invoke(panel, null);
                    var settled = host.Query<VisualElement>().ToList().Select(e => e.worldBound).ToList();
                    Assert.AreEqual(first.Count, settled.Count, $"{swap} at {size}");
                    for (var i = 0; i < first.Count; ++i)
                        Assert.IsTrue(first[i] == settled[i] || float.IsNaN(first[i].x) && float.IsNaN(settled[i].x),
                            $"{swap} at {size.x}x{size.y}: an element moved after the first layout ({first[i]} -> {settled[i]})");
                }
            }
            run.Ended = now;
        }

        [Test]
        public void StageNamesAreNeverCut()
        {
            var now = AutopilotText.UnixNow;
            foreach (var stage in new[] { 4, 6 })
            {
                var live = new AutopilotRun { Id = "layout-stages-" + stage, Kind = "explore", Started = now - 300, Prompt = "Explore for 20 minutes", Stage = stage, Attempt = 12, AttemptLines = 12 };
                AutopilotSession.Runs.Add(live);
                var sizes = new[] { 300, 320, 340, 360, 400, 450, 500, 560, 640, 700 }.Select(w => new Vector2Int(w, 700)).Concat(Sizes);
                foreach (var size in sizes)
                {
                    Show(size, v => v.SelectedId = live.Id);
                    var names = host.Query<Label>(className: "stage-name").ToList();
                    Assert.AreEqual(AutopilotText.Stages.Length, names.Count);
                    foreach (var name in names)
                    {
                        // Each word of a name fits the room its label has (a name wraps between its words, never in one)
                        foreach (var word in name.text.Split(' '))
                        {
                            var need = name.MeasureTextSize(word, 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined).x;
                            Assert.LessOrEqual(need, name.contentRect.width + 0.5f, $"\"{word}\" of {name.text} needs {need:0.#} px, it has {name.contentRect.width:0.#} at {size.x}x{size.y}");
                        }
                        var all = name.MeasureTextSize(name.text, name.contentRect.width, VisualElement.MeasureMode.AtMost, 0, VisualElement.MeasureMode.Undefined);
                        Assert.LessOrEqual(all.y, name.contentRect.height + 0.5f, $"{name.text}'s lines fit its height at {size.x}x{size.y}");
                        var cell = name.parent.worldBound;
                        var row = name.parent.parent.worldBound;
                        Assert.IsTrue(cell.xMin >= row.xMin - 0.5f && cell.xMax <= row.xMax + 0.5f, $"{name.text}'s cell in its row at {size.x}x{size.y}");
                    }
                    StringAssert.Contains("ROLLOUT · H12", string.Join("|", names.Select(n => n.text)));
                    Assert.IsEmpty(Problems("live, stage " + stage, size, out _));
                }
                if (stage == 6) StringAssert.Contains(AutopilotText.SavingMemory, Words(), "the report stage's words while it saves to memory");
                live.Ended = now;
            }
        }

        [Test]
        public void TheFeedEndsWithTheMemoryLine()
        {
            var now = AutopilotText.UnixNow;
            var run = new AutopilotRun { Id = "layout-feed", Kind = "goal", Prompt = "Reach the gate", Started = now - 60, Ended = now, Result = Json.ParseObject("{\"reached\": true}") };
            run.Feed.Add(new AutopilotFeedLine { At = 50, Kind = "stage", Text = "Result: Goal hit" });
            run.Feed.Add(new AutopilotFeedLine { At = 50, Kind = "memory", Text = AutopilotText.MemoryUpdated });
            AutopilotSession.Runs.Add(run);
            Check("feed, memory", v =>
            {
                v.SelectedId = run.Id;
                v.Tab = "feed";
            }, null, "MEMORY", AutopilotText.MemoryUpdated);
        }

        [Test]
        public void TheRecordingShowsOnTheResult()
        {
            var now = AutopilotText.UnixNow;
            var video = Path.Combine(folder, "recording.mp4");
            File.WriteAllBytes(video, new byte[] { 0, 0, 0, 24 });
            var run = new AutopilotRun
            {
                Id = "layout-rec", Kind = "verify", Prompt = "Verify that the gate opens", Started = now - 130, Ended = now,
                Result = Json.ParseObject("{\"verdict\": \"confirmed\", \"reason\": \"It opened.\"}"), Recording = video, RecordingSeconds = 123.4,
            };
            AutopilotSession.Runs.Add(run);
            notSaid.AddRange(new[] { "OPEN VIDEO", "PLAY" });
            Check("result, recording", v => v.SelectedId = run.Id, null, "CONFIRMED", "0:00 / 02:03");
            notSaid.Clear();
            // Play / Pause and show in folder: icon buttons, their names in their tooltips
            Show(new Vector2Int(560, 900), v => v.SelectedId = run.Id);
            var icons = host.Query(className: "rec-controls").First().Query(className: "icon").ToList();
            Assert.AreEqual(2, icons.Count);
            Assert.AreEqual(RecordingPlayer.PlayName, icons[0].tooltip);
            Assert.AreEqual(AutopilotUI.Glyph.Shape.Play, icons[0].Q<AutopilotUI.Glyph>().Is);
            Assert.AreEqual(RecordingPlayer.ShowName, icons[1].tooltip);
            Assert.AreEqual(Application.platform == RuntimePlatform.WindowsEditor ? "Show in File Explorer"
                : Application.platform == RuntimePlatform.OSXEditor ? "Show in Finder" : "Show in folder", icons[1].tooltip);
            Assert.AreEqual(AutopilotUI.Glyph.Shape.Folder, icons[1].Q<AutopilotUI.Glyph>().Is, "the folder, as Run folder");
            Assert.IsNull(icons[0].Q<Label>(), "no words on the play button");
            foreach (var size in Sizes)
            {
                Show(size, v => v.SelectedId = run.Id);
                var frame = host.Q(className: "rec-frame");
                Assert.IsNotNull(frame, $"the player at {size}");
                var r = frame.worldBound;
                var page = host.Q(className: "page").worldBound;
                Assert.Greater(r.width, 100, $"the box has a width at {size}");
                Assert.AreEqual(size.x > AutopilotView.WideFrom ? 230f : 200f, r.height, 1.5f, $"230 px tall (200 narrow) at {size} ({r.width:0.#}x{r.height:0.#})");
                Assert.LessOrEqual(r.xMax, page.xMax + 0.5f);
                // (under the verdict; no readouts under it)
                Assert.Greater(r.yMin, host.Q(className: "verdict").worldBound.yMax - 0.5f, $"under the verdict at {size}");
                Assert.IsNull(host.Q(className: "readouts"), $"no readouts at {size}");
            }

            // No recording (an older run, a headless game), or one gone: nothing
            notSaid.AddRange(new[] { "OPEN VIDEO", "0:00 /" });
            run.Recording = "";
            Check("result, no recording", v => v.SelectedId = run.Id, null, "CONFIRMED");
            notSaid.Clear();
            // Gone from its place: the player kept, Play and Show in File Explorer disabled, saying so
            run.Recording = Path.Combine(folder, "gone.mp4");
            Check("result, the recording gone", v => v.SelectedId = run.Id, null, "CONFIRMED", RecordingPlayer.GoneLine);
            Assert.IsNotNull(host.Q(className: "rec-frame"));
            foreach (var icon in host.Q(className: "rec-controls").Query(className: "icon").ToList())
                Assert.IsFalse(icon.enabledSelf, icon.tooltip);
        }

        [Test]
        public void ARunWithNoRecordingSaysWhyInItsPlace()
        {
            var now = AutopilotText.UnixNow;
            const string why = "the video couldn't be made (ffmpeg exited with code 1): its frames are in C:\\Users\\someone\\AppData\\Local\\Autopilot\\projects\\orbcourse-3f2a9c\\runs\\20261015-101500\\frames";
            var run = new AutopilotRun
            {
                Id = "layout-norec", Kind = "verify", Prompt = "Verify that the gate opens", Started = now - 130, Ended = now,
                Result = Json.ParseObject($"{{\"verdict\": \"confirmed\", \"reason\": \"It opened.\", \"memory\": {{\"updated\": false}}, \"no_recording\": {Json.Write(why)}}}"),
            };
            run.NoRecording = AutopilotRun.NoRecordingLine(run.Result);
            AutopilotSession.Runs.Add(run);
            notSaid.AddRange(new[] { "OPEN VIDEO", "0:00 /" });
            var sizes = new[] { 300, 320, 340 }.Select(w => new Vector2Int(w, 700)).Concat(Sizes).ToArray();
            Check("result, no recording, why", v => v.SelectedId = run.Id, null, "CONFIRMED", "No recording: the video couldn't be made", "frames");
            foreach (var size in sizes)
            {
                Show(size, v => v.SelectedId = run.Id);
                var line = host.Q<Label>(className: "no-recording");
                Assert.IsNotNull(line, $"the line at {size}");
                Assert.IsTrue(line.ClassListContains("t-muted") && line.ClassListContains("small"), "muted, small");
                Assert.AreEqual(WhiteSpace.Normal, line.resolvedStyle.whiteSpace, "it wraps");
                var r = line.worldBound;
                var need = line.MeasureTextSize(line.text, line.contentRect.width, VisualElement.MeasureMode.AtMost, 0, VisualElement.MeasureMode.Undefined);
                Assert.LessOrEqual(need.x, line.contentRect.width + 1f, $"its words fit its width at {size}");
                Assert.LessOrEqual(need.y, line.contentRect.height + 0.5f, $"its lines fit its height at {size}");
                Assert.LessOrEqual(r.xMax, host.Q(className: "page").worldBound.xMax + 0.5f, $"inside the page at {size}");
                // (where the player goes: under the verdict)
                Assert.Greater(r.yMin, host.Q(className: "verdict").worldBound.yMax - 0.5f, $"under the verdict at {size}");
                Assert.IsEmpty(Problems("result, no recording", size, out _));
            }

            // An older agent service: its own line
            run.Result = Json.ParseObject("{\"verdict\": \"confirmed\", \"reason\": \"It opened.\"}");
            run.NoRecording = AutopilotRun.NoRecordingLine(run.Result);
            Check("result, older agent service", v => v.SelectedId = run.Id, null, "CONFIRMED", AutopilotText.NoRecordingOlderService);

            // Never beside a player, never while the run goes, and nothing in a run kept before the line
            var video = Path.Combine(folder, "recording.mp4");
            File.WriteAllBytes(video, new byte[] { 0, 0, 0, 24 });
            run.Recording = video;
            Show(new Vector2Int(560, 480), v => v.SelectedId = run.Id);
            Assert.IsNotNull(host.Q(className: "rec-frame"));
            Assert.IsNull(host.Q(className: "no-recording"), "no line beside the player");
            run.Recording = "";
            run.NoRecording = "";
            Show(new Vector2Int(560, 480), v => v.SelectedId = run.Id);
            Assert.IsNull(host.Q(className: "no-recording"), "a run kept before the line: nothing");
            StringAssert.DoesNotContain("No recording", Words());
            run.NoRecording = AutopilotText.NoRecordingOlderService;
            run.Ended = 0;
            Show(new Vector2Int(560, 480), v => v.SelectedId = run.Id);
            Assert.IsNull(host.Q(className: "no-recording"), "never while the run goes");
            run.Ended = now;
            notSaid.Clear();
        }

        [Test]
        public void ARunsAlertsShowBeforeItsReason()
        {
            var now = AutopilotText.UnixNow;
            const string alert = "Autopilot couldn't look at the game's picture: the agent's LLM provider has no model that looks at pictures (DeepSeek's models can't look at pictures). A claim about how something looks (an animation, an effect, the UI) can't be settled without it: choose an LLM provider whose models look at pictures (Anthropic, OpenAI, Google or OpenRouter).";
            var run = new AutopilotRun
            {
                Id = "layout-alert", Kind = "verify", Prompt = "Verify that the guard waves", Started = now - 90, Ended = now,
                Result = Json.ParseObject($"{{\"verdict\": \"INCONCLUSIVE\", \"reason\": \"The wave couldn't be looked at.\", \"memory\": {{\"updated\": false}}, \"alerts\": [{Json.Write(alert)}]}}"),
            };
            AutopilotSession.Runs.Add(run);
            var sizes = new[] { 300, 320, 340 }.Select(w => new Vector2Int(w, 700)).Concat(Sizes).ToArray();
            Check("result, an alert", v => v.SelectedId = run.Id, null, "INCONCLUSIVE", "couldn't look at the game's picture", "choose an LLM provider");
            foreach (var size in sizes)
            {
                Show(size, v => v.SelectedId = run.Id);
                var box = host.Q(className: "result-alert");
                Assert.IsNotNull(box, $"the alert at {size}");
                Assert.IsTrue(box.ClassListContains("problem") && box.ClassListContains("amber"), "an amber problem box");
                var line = box.Q<Label>();
                Assert.AreEqual(WhiteSpace.Normal, line.resolvedStyle.whiteSpace, "it wraps");
                var need = line.MeasureTextSize(line.text, line.contentRect.width, VisualElement.MeasureMode.AtMost, 0, VisualElement.MeasureMode.Undefined);
                Assert.LessOrEqual(need.y, line.contentRect.height + 0.5f, $"its lines fit its height at {size}");
                Assert.LessOrEqual(box.worldBound.xMax, host.Q(className: "page").worldBound.xMax + 0.5f, $"inside the page at {size}");
                // (before the verdict and its reason)
                Assert.LessOrEqual(box.worldBound.yMax, host.Q(className: "verdict").worldBound.yMin + 0.5f, $"above the verdict at {size}");
                Assert.IsEmpty(Problems("result, an alert", size, out _));
            }

            // None in a result without alerts
            run.Result = Json.ParseObject("{\"verdict\": \"confirmed\", \"reason\": \"It waved.\", \"memory\": {\"updated\": false}}");
            Show(new Vector2Int(560, 480), v => v.SelectedId = run.Id);
            Assert.IsNull(host.Q(className: "result-alert"));
            StringAssert.DoesNotContain("couldn't look", Words());
        }

        [Test]
        public void ARunWithNoFindingsHasNoFindingsSection()
        {
            var now = AutopilotText.UnixNow;
            var run = new AutopilotRun
            {
                Id = "layout-nofind", Kind = "verify", Prompt = "Verify that the gate opens", Started = now - 60, Ended = now,
                Result = Json.ParseObject("{\"verdict\": \"confirmed\", \"reason\": \"It opened.\", \"evidence\": [\"A1 3.2 s the gate opened\"], \"memory\": {\"updated\": false}}"),
            };
            AutopilotSession.Runs.Add(run);
            notSaid.AddRange(new[] { "FINDINGS", "Findings", "· NONE", "· none", "AGENT FINDING" });
            Check("result, no findings", v => v.SelectedId = run.Id, null, "CONFIRMED", "EVIDENCE");
            notSaid.Clear();
            StringAssert.DoesNotContain("Findings", ReportText(run), "nor in the copied report");

            // With findings: their headers as before
            run.Result = Json.ParseObject("{\"verdict\": \"confirmed\", \"reason\": \"It opened.\", \"findings\": [\"The gate can be opened twice. Pressing E again opens it a second time.\"]}");
            run.Findings.Add(Json.ParseObject("{\"source\": \"engine\", \"severity\": \"warning\", \"detector\": \"physics.fell_through_floor\", \"summary\": \"The player fell through the floor\", \"signature\": \"s9\", \"count\": 1}"));
            Check("result, findings", v => v.SelectedId = run.Id, null, "CONFIRMED", "AGENT FINDING", "ENGINE FINDINGS · 1");
            StringAssert.Contains("## Findings", ReportText(run));
        }

        static string ReportText(AutopilotRun run) =>
            (string)typeof(AutopilotView).GetMethod("Report", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { run });

        // -- the run's id line and its icons; the composer's link and Disconnect ----------------------------------------

        [Test]
        public void TheRunsActionsAreIconsOnItsIdLine()
        {
            EditorHost.TestClient(null);  // (not connected: Run again greyed)
            var now = AutopilotText.UnixNow;
            var dir = Path.Combine(folder, "run-icons");
            Directory.CreateDirectory(dir);
            var run = new AutopilotRun
            {
                Id = "layout-20261016-235900", Kind = "verify", Prompt = "Verify that the gate opens when the lever beside it is pulled twice",
                Started = now - 60, Ended = now, Result = Json.ParseObject("{\"verdict\": \"confirmed\", \"reason\": \"It opened.\"}"),
                ReportDir = dir, RunFrame = Json.ParseObject("{\"kind\": \"verify\", \"claim\": \"the gate opens\"}"),
            };
            AutopilotSession.Runs.Add(run);
            var names = new[] { AutopilotView.RunAgainName, AutopilotView.CopyReportName, AutopilotView.RunFolderName };
            Assert.AreEqual(new[] { "Run again", "Copy report", "Run folder" }, names);
            var shapes = new[] { AutopilotUI.Glyph.Shape.Retry, AutopilotUI.Glyph.Shape.Clipboard, AutopilotUI.Glyph.Shape.Folder };
            var sizes = new[] { 300, 320, 340 }.Select(w => new Vector2Int(w, 700)).Concat(Sizes).ToArray();
            foreach (var withReport in new[] { false, true })
            {
                // (an old run's report.html in its folder: no Report button any more, the three icons alone)
                if (withReport) File.WriteAllText(Path.Combine(dir, "report.html"), "<html></html>");
                foreach (var size in sizes)
                {
                    Show(size, v => v.SelectedId = run.Id);
                    var at = $"at {size}{(withReport ? ", with an old report.html" : "")}";
                    var rows = host.Query(className: "run-head").ToList();
                    Assert.AreEqual(1, rows.Count, $"one id line {at}");
                    var row = rows[0];
                    Assert.AreEqual(FlexDirection.Row, row.resolvedStyle.flexDirection);
                    // The id line: left, growing; Run again, Copy report and Run folder its siblings at its right, in order
                    var id = row.Q<Label>(className: "run-id");
                    Assert.AreSame(row, id.parent);
                    StringAssert.StartsWith("RUN layout-20261016-235900 // VERIFY", id.text);
                    Assert.Greater(id.resolvedStyle.flexGrow, 0f, "the id line grows");
                    Assert.AreEqual(row.worldBound.xMin, id.worldBound.xMin, 0.5f, $"the id line at the left {at}");
                    var icons = row.Query(className: "icon").ToList();
                    Assert.AreEqual(3, icons.Count, at);
                    for (var i = 0; i < 3; ++i)
                    {
                        Assert.AreSame(row, icons[i].parent, $"{names[i]} beside the id line {at}");
                        Assert.AreEqual(names[i], icons[i].tooltip);
                        Assert.AreEqual(shapes[i], icons[i].Q<AutopilotUI.Glyph>().Is);
                        Assert.IsNull(icons[i].Q<Label>(), $"no words on {names[i]}");
                        var r = icons[i].worldBound;
                        // (on the row: centred on it when wide, at its top when narrow, where the id line may wrap)
                        if (size.x > AutopilotView.WideFrom) Assert.AreEqual(row.worldBound.center.y, r.center.y, 0.5f, $"{names[i]} on the row {at}");
                        else Assert.AreEqual(row.worldBound.yMin, r.yMin, 0.5f, $"{names[i]} at the row's top {at}");
                        Assert.GreaterOrEqual(r.xMin, id.worldBound.xMax - 0.5f, $"{names[i]} after the words {at}");
                        if (i > 0) Assert.GreaterOrEqual(r.xMin, icons[i - 1].worldBound.xMax - 0.5f, $"{names[i]} after {names[i - 1]} {at}");
                    }
                    Assert.IsTrue(icons[0].ClassListContains("primary"), "Run again in the accent");
                    // (not connected: greyed, but pressed it says why on the status line)
                    Assert.IsTrue(icons[0].ClassListContains("off"), $"Run again greyed without the agent service {at}");
                    Assert.IsTrue(icons[2].enabledSelf, $"Run folder: the run's folder is there {at}");
                    // Run folder last, at the row's right: no Report button
                    var last = icons[2];
                    Assert.AreSame(last, row.Query(className: "btn").ToList().Last(), $"Run folder the last button {at}");
                    Assert.AreEqual(row.worldBound.xMax, last.worldBound.xMax, 0.5f, $"right-aligned {at}");
                    StringAssert.DoesNotContain("Report", string.Join("|", row.Query<Label>().ToList().Select(l => l.text)), at);
                    // The prompt under the row, the page's gap between them (14 px; 12 in a narrow window)
                    var prompt = host.Q(className: "page").Query<Label>().ToList().First(l => l.text.StartsWith("Verify that the gate", StringComparison.Ordinal));
                    var gap = prompt.worldBound.yMin - row.worldBound.yMax;
                    Assert.AreEqual(size.x > AutopilotView.WideFrom ? 14f : 12f, gap, 0.5f, $"the prompt under the row {at} ({gap:0.#} px)");
                    // Each once, nowhere else (no row of buttons with words at the page's end)
                    var words = Words();
                    foreach (var w in new[] { "Run again", "Copy report", "Run folder" }) StringAssert.DoesNotContain(w, words, at);
                    Assert.AreEqual(3, host.Query(className: "icon").ToList().Count(e => names.Contains(e.tooltip)), $"each once {at}");
                    Assert.IsEmpty(Problems("result, its icons", size, out _));
                }
            }
            // Not connected, pressed: the status line says why, and nothing starts
            Show(new Vector2Int(1400, 900), v => v.SelectedId = run.Id);
            Press(host.Q(className: "run-head").Query(className: "icon").First());
            Assert.AreEqual(AutopilotStatus.RunOffline, AutopilotStatus.Shown(UnityEditor.EditorApplication.timeSinceStartup));
            // Connected, no run going: Run again can be pressed
            Client();
            Show(new Vector2Int(560, 480), v => v.SelectedId = run.Id);
            var again = host.Q(className: "run-head").Query(className: "icon").First();
            Assert.IsTrue(again.enabledSelf && !again.ClassListContains("off"), "Run again, connected");
        }

        [Test]
        public void TheLinkAndDisconnectAreOneRow()
        {
            // The composer's foot: the link (and the address) and Disconnect on one row at the right, at any width
            PlaytestSettings.Get().Prompt = "";
            var sizes = new[] { 300, 320, 340 }.Select(w => new Vector2Int(w, 700)).Concat(Sizes).ToArray();
            foreach (var size in sizes)
            {
                Show(size, null, v => v.TestShowComposer(ServiceUrl));
                // (● CONNECTED: the dot drawn, the fonts have none)
                var label = host.Query<Label>().ToList().Single(l => l.text == "CONNECTED");
                var link = label.parent;
                Assert.IsTrue(link.ClassListContains("dotlabel") && link.Q(className: "round-dot") != null, $"the dot before CONNECTED at {size}");
                var row = link.parent;
                var disconnect = row.Query(className: "btn").ToList().Single(b => b.Q<Label>()?.text == "Disconnect");
                Assert.AreSame(row, disconnect.parent, $"siblings at {size}");
                Assert.AreEqual(Wrap.NoWrap, row.resolvedStyle.flexWrap, $"one row at {size}");
                Assert.AreEqual(disconnect.worldBound.center.y, link.worldBound.center.y, 0.5f, $"the link on Disconnect's row at {size}");
                Assert.LessOrEqual(link.worldBound.xMax, disconnect.worldBound.xMin + 0.5f, $"the link before Disconnect at {size}");
                Assert.AreEqual(row.worldBound.xMax, disconnect.worldBound.xMax, 0.5f, $"Disconnect at the right at {size}");
                var url = row.Q<Label>(className: "link-url");
                Assert.GreaterOrEqual(url.worldBound.xMin, link.worldBound.xMax - 0.5f, $"the address after the link at {size}");
                Assert.LessOrEqual(url.worldBound.xMax, disconnect.worldBound.xMin + 0.5f, $"the address before Disconnect at {size}");
                // (at the right: when the address has all the room it needs, the room is left of the link)
                var full = url.MeasureTextSize(url.text, 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined).x;
                if (url.contentRect.width >= full - 0.5f)
                    Assert.Greater(link.worldBound.xMin, row.worldBound.xMin + 0.5f, $"right-aligned at {size}");
                Assert.IsEmpty(Problems("composer, the link", size, out _));
            }
        }

        // -- the status line; Reset memory confirmed in place -------------------------------------------------------

        static readonly MethodInfo TickMethod = typeof(AutopilotView).GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic);

        VisualElement StatusBar() => host.Q(className: "status-bar");

        [Test]
        public void TheStatusLineSaysWhatAClickDid()
        {
            var now = AutopilotText.UnixNow;
            var run = new AutopilotRun
            {
                Id = "layout-status", Kind = "verify", Prompt = "Verify that the gate opens", Started = now - 60, Ended = now,
                Result = Json.ParseObject("{\"verdict\": \"confirmed\", \"reason\": \"It opened.\"}"),
            };
            AutopilotSession.Runs.Add(run);
            try
            {
                AutopilotStatus.TestAge(60);
                foreach (var size in Sizes)
                {
                    // Nothing said: no line, no room taken
                    var view = Show(size, v => v.SelectedId = run.Id);
                    view.UpdateStatus();
                    Layout();
                    Assert.AreEqual(DisplayStyle.None, StatusBar().resolvedStyle.display, $"hidden with nothing to say at {size}");
                    // Copy report: said on the main column's last row, one line, its whole text in its tooltip
                    Press(host.Query(className: "icon").ToList().Single(b => b.tooltip == AutopilotView.CopyReportName));
                    view.UpdateStatus();
                    Layout();
                    var bar = (Label)StatusBar();
                    Assert.AreEqual(DisplayStyle.Flex, bar.resolvedStyle.display, $"shown at {size}");
                    Assert.AreEqual(AutopilotStatus.CopiedReport, bar.text);
                    Assert.AreEqual(bar.text, bar.tooltip);
                    Assert.AreSame(bar.parent.Children().Last(), bar, "the main column's last row");
                    Assert.AreEqual(host.worldBound.yMax, bar.worldBound.yMax, 0.5f, $"at the window's foot at {size}");
                    Assert.AreEqual(TextOverflow.Ellipsis, bar.resolvedStyle.textOverflow);
                    Assert.AreEqual(AutopilotUI.Hex("#8b8f98"), bar.resolvedStyle.color);
                    Assert.AreEqual(11f, bar.resolvedStyle.fontSize, 0.01f);
                    Assert.IsEmpty(Problems("the status line", size, out _));
                    // After 8 s: gone
                    AutopilotStatus.TestAge(AutopilotStatus.Seconds + 0.1);
                    view.UpdateStatus();
                    Layout();
                    Assert.AreEqual(DisplayStyle.None, StatusBar().resolvedStyle.display, $"gone after 8 s at {size}");
                }
                // Never on the setup's pages
                AutopilotStatus.Say(AutopilotStatus.Copied);
                var setup = Show(new Vector2Int(1400, 900), v => Setup(v, 1));
                setup.UpdateStatus();
                Layout();
                Assert.AreEqual(DisplayStyle.None, StatusBar().resolvedStyle.display, "not on the setup");
                // The words
                Assert.AreEqual("Told the agent: physics.fell_through_floor is not a bug", AutopilotStatus.NotABug("physics.fell_through_floor"));
                Assert.AreEqual("Not connected: connect to send feedback", AutopilotStatus.FeedbackOffline);
                Assert.AreEqual("Open the scene to show this finding in it", AutopilotStatus.NoScene);
                Assert.AreEqual("Nothing in the open scene near that place", AutopilotStatus.NothingNear);
                Assert.AreEqual("Couldn’t save the usage to C:\\x\\AutopilotUsage.json (error 5)", AutopilotStatus.UsageNotSaved("C:\\x\\AutopilotUsage.json", 5));
            }
            finally
            {
                AutopilotStatus.TestAge(60);
            }
        }

        [Test]
        public void ResetMemoryIsConfirmedInPlace()
        {
            var c = Client();
            c.TestReceive("{\"type\": \"memory_state\", \"runs\": 12, \"notes\": true, \"lib\": false}");
            foreach (var size in new[] { new Vector2Int(1400, 900), new Vector2Int(360, 640) })
            {
                var view = Show(size, null);
                VisualElement Reset() => host.Query(className: "savedata-reset").ToList().First(b => Shown(b, host) || !view.Wide);
                Press(Reset());
                Layout();
                // Asked in place: the question, its line, Reset memory and Cancel (the dropdown opened when narrow)
                var ask = host.Query(className: "reset-confirm").ToList().Where(e => Shown(e, host)).ToList();
                Assert.AreEqual(1, ask.Count, $"the confirm in place at {size}");
                var words = Words();
                StringAssert.Contains(AutopilotView.ResetQuestion, words);
                StringAssert.Contains(AutopilotView.ResetLine, words);
                Assert.IsEmpty(Problems("reset, asked", size, out _));
                // Cancel: nothing sent, the footer back
                sent.Clear();
                Press(ask[0].Query(className: "btn").ToList().Single(b => b.Q<Label>().text == "Cancel"));
                Layout();
                Assert.IsFalse(sent.Any(s => Json.Str(Json.ParseObject(s), "type") == "clear_memory"), "Cancel sends nothing");
                Assert.AreEqual(0, host.Query(className: "reset-confirm").ToList().Count);
                // Reset memory: sent, going, then how it went
                Press(Reset());
                Layout();
                Press(host.Query(className: "reset-yes").ToList().First(e => Shown(e, host)));
                Assert.AreEqual("clear_memory", Json.Str(Json.ParseObject(sent.Last()), "type"));
                Layout();
                StringAssert.Contains(AutopilotView.ResettingLine, Words());
                c.TestReceive("{\"type\": \"memory_cleared\", \"ok\": true, \"files\": 4}");
                c.TestReceive("{\"type\": \"memory_state\", \"runs\": 0, \"notes\": false, \"lib\": false}");
                TickMethod.Invoke(view, null);
                Layout();
                words = Words();
                StringAssert.Contains("Memory reset successful", words);
                StringAssert.Contains("· empty", words);
                Assert.IsEmpty(Problems("reset, done", size, out _));
                c.TestReceive("{\"type\": \"memory_state\", \"runs\": 12, \"notes\": true, \"lib\": false}");
            }
            // While a run goes: greyed, its tooltip saying why; pressed, the status line says so and nothing is asked
            c.StartRun(new Dictionary<string, object> { ["kind"] = "goal", ["goal"] = "Reach the gate" });
            Show(new Vector2Int(1400, 900), null);
            var busy = host.Query(className: "savedata-reset").ToList().First(b => Shown(b, host));
            Assert.IsTrue(busy.ClassListContains("off"));
            Assert.AreEqual(AutopilotView.ResetBusyTip, busy.tooltip);
            Press(busy);
            Layout();
            Assert.AreEqual(0, host.Query(className: "reset-confirm").ToList().Count, "nothing asked mid-run");
            Assert.AreEqual(AutopilotStatus.ResetBusy, AutopilotStatus.Shown(UnityEditor.EditorApplication.timeSinceStartup));
            AutopilotStatus.TestAge(60);
        }

        // -- the design's pieces: the gear's outline, evidence prefixes, stopping --------------------------------------

        [Test]
        public void TheGearIsTheDesignsOutline()
        {
            var points = AutopilotUI.GearOutline;
            Assert.Greater(points.Count, 100, "the outline with its arcs");
            foreach (var p in points)
                Assert.IsTrue(p.x >= 1.5f && p.x <= 22.5f && p.y >= 1.5f && p.y <= 22.5f, $"{p} inside the 24 px box");
            Assert.AreEqual(points[0].x, points[points.Count - 1].x, 0.05f, "it closes");
            Assert.AreEqual(points[0].y, points[points.Count - 1].y, 0.05f, "it closes");
            // (the teeth's tops at y = 2 and 22, as the path has them)
            Assert.AreEqual(2f, points.Min(p => p.y), 0.05f);
            Assert.AreEqual(22f, points.Max(p => p.y), 0.05f);
        }

        [Test]
        public void EvidencePrefixesAreTheHarnessAndTime()
        {
            foreach (var (line, prefix) in new[] { ("H1 12.4 s picked up orb 1 of 4", "H1 12.4 s"), ("H2 3:20 stood in the gate's doorway", "H2 3:20"), ("A3 12.4 s opened the chest", "A3 12.4 s"), ("H2 pressed E", "H2") })
            {
                var m = AutopilotView.EvidencePrefix.Match(line);
                Assert.IsTrue(m.Success, line);
                Assert.AreEqual(prefix, m.Groups[1].Value, line);
            }
            Assert.IsFalse(AutopilotView.EvidencePrefix.Match("The gate opened at 3:20").Success, "free text stays plain");
        }

        [Test]
        public void StopReadsStoppingAndLocksTheSteerBox()
        {
            var c = Client();
            c.StartRun(new Dictionary<string, object> { ["kind"] = "goal", ["goal"] = "Reach the gate" });
            var now = AutopilotText.UnixNow;
            var live = new AutopilotRun { Id = "layout-stopping", Kind = "goal", Started = now - 40, StageSince = now - 40, Prompt = "Reach the gate", Stage = AutopilotText.StageRollout, Attempt = 1, AttemptLines = 1 };
            AutopilotSession.Runs.Add(live);
            Assert.AreSame(live, AutopilotSession.Current);
            Show(new Vector2Int(1400, 900), v => v.SelectedId = live.Id, v => v.TestUpdateSteer());
            StopButtonIs("Stop", true);
            AutopilotSession.Stop();
            Assert.IsTrue(live.Stopping);
            Assert.AreEqual(AutopilotSession.StoppingLine, live.Feed.Last().Text, "the feed says so");
            foreach (var size in Sizes)
            {
                Show(size, v => v.SelectedId = live.Id, v => v.TestUpdateSteer());
                StopButtonIs("Stopping…", false);
                var row = host.Q(className: "steer");
                Assert.IsTrue(row.Q(className: "steer-box").ClassListContains("locked"), $"the steer box locked at {size}");
                Assert.IsFalse(row.Q<TextField>().enabledInHierarchy, $"the box locked at {size}");
                Assert.IsFalse(row.Q(className: "btn").enabledInHierarchy, $"Steer locked at {size}");
                Assert.IsEmpty(Problems("live, stopping", size, out _));
            }
        }

        void StopButtonIs(string label, bool enabled)
        {
            var b = host.Q(className: "live-head").Query(className: "btn").ToList().Single();
            Assert.AreEqual(label, b.Q<Label>().text);
            Assert.AreEqual(enabled, b.enabledInHierarchy, label);
        }

        // -- the steer box -------------------------------------------------------------------------------------------

        [Test]
        public void TheSteerBoxWaitsForTheRollout()
        {
            var c = Client();
            c.StartRun(new Dictionary<string, object> { ["kind"] = "explore", ["minutes"] = 20 });
            Assert.IsTrue(c.CanSteer);
            var now = AutopilotText.UnixNow;
            var live = new AutopilotRun { Id = "layout-steer", Kind = "explore", Started = now - 40, StageSince = now - 40, Prompt = "Explore for 20 minutes", Stage = 1 };
            AutopilotSession.Runs.Add(live);
            Assert.AreSame(live, AutopilotSession.Current);
            var sizes = new[] { 300, 320, 340, 360, 400 }.Select(w => new Vector2Int(w, 700)).Concat(Sizes);
            foreach (var size in sizes)
            {
                Rect? first = null;
                foreach (var stage in new[] { 0, 1, 2, 3, AutopilotText.StageRollout, AutopilotText.StageResults, AutopilotText.StageReport })
                {
                    live.Stage = stage;
                    var at = $"at {AutopilotText.Stages[stage]}, {size.x}x{size.y}";
                    var view = Show(size, v => v.SelectedId = live.Id, v => v.TestUpdateSteer());
                    var row = host.Q(className: "steer");
                    var field = row.Q<TextField>();
                    var button = row.Q(className: "btn");
                    var rollout = stage == AutopilotText.StageRollout;
                    // Shown at every stage of a run going (same place), typed in at every stage; Steer sends only at the
                    // Rollout, its tooltip saying so before
                    Assert.AreEqual(DisplayStyle.Flex, row.resolvedStyle.display, $"the steer row shown {at}");
                    Assert.IsTrue(field.enabledInHierarchy, $"the box can be typed in {at}");
                    Assert.AreEqual(AutopilotView.SteerHint, field.textEdition.placeholder, at);
                    Assert.AreEqual("You can steer once the game is playing", AutopilotView.SteerWaitHint);
                    Assert.AreEqual(rollout ? "" : AutopilotView.SteerWaitHint, button.tooltip, at);
                    // What was typed stays, and the button can't send it before the Rollout
                    field.value = "go round the rocks";
                    view.TestUpdateSteer();
                    Assert.AreEqual(rollout, button.enabledInHierarchy, $"the Steer button {at}");
                    Assert.AreEqual("go round the rocks", field.value, at);
                    if (rollout)
                    {
                        // (one line of words: a new line typed or pasted is taken out)
                        field.value = "go round\nthe rocks\r\n";
                        Assert.AreEqual("go round the rocks", field.value, at);
                    }
                    // Both hints fit the box (no clipping) at every width checked: it wraps them, as tall as the longer needs
                    field.value = "";
                    view.TestUpdateSteer();
                    Layout();
                    var text = field.Q<TextElement>();
                    var room = text.contentRect;
                    foreach (var hint in new[] { AutopilotView.SteerHint })
                    {
                        foreach (var word in hint.Split(' '))
                        {
                            var w = text.MeasureTextSize(word, 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined).x;
                            Assert.LessOrEqual(w, room.width + 0.5f, $"the hint's word \"{word}\" needs {w:0.#} px, the box has {room.width:0.#} {at}");
                        }
                        var need = text.MeasureTextSize(hint, room.width, VisualElement.MeasureMode.AtMost, 0, VisualElement.MeasureMode.Undefined);
                        Assert.Greater(need.y, 0f, $"the hint measured {at}");
                        Assert.LessOrEqual(need.y, room.height + 0.5f, $"\"{hint}\" needs {need.y:0.#} px of height, the box has {room.height:0.#} {at}");
                    }
                    Assert.LessOrEqual(field.worldBound.xMax, button.worldBound.xMin + 0.5f, $"the box beside the button {at}");
                    Assert.IsEmpty(Problems("live, steer " + at, size, out _));
                    // The same place at every stage: no jump when the Rollout comes or goes
                    var r = row.worldBound;
                    first ??= r;
                    Assert.AreEqual(first.Value.yMin, r.yMin, 0.5f, $"the steer row's top {at}");
                    Assert.AreEqual(first.Value.height, r.height, 0.5f, $"the steer row's height {at}");
                }
            }
        }
    }
}
