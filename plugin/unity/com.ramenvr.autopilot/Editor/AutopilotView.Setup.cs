// The Autopilot window's first-run setup, shown in place of the window's content until preferences.json says
// "onboarded" (and again from Rerun setup: the Settings page, the window's menu), as one setup for every engine and project:
//
//   1 ACCOUNT           sign in with Aura (the Aura app's agent service, or the user's own signing in through the
//                       browser: the agent service does it, asked over the connection), or the user's own model keys,
//                       Coding Agent and LLM provider (written to Autopilot/autopilot.env), or decide later
//   2 AGENT SERVICE     installed once and started with the editor (AgentService.cs); skipped with the Aura app
//   3 PRIVACY SETTINGS  whether runs' data is shared, and a recap; Finish (Next when step 4 may follow) writes
//                       preferences.json and connects
//   4 FIRST RUN         only when the agent service remembers nothing about this game (its memory_state): an Explore
//                       run offered, recommended and skippable; with something remembered the setup closes by itself

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using static Autopilot.Playtest.Editor.AutopilotUI;

namespace Autopilot.Playtest.Editor
{
    public sealed partial class AutopilotView
    {
        public const string TrainLine = "Enabling this sends recordings, screenshots, and agent traces and memories to the developers of Autopilot." +
                                        " For runs from your own Claude Code or Codex, the agent traces are its conversation during the run.";

        /// <summary>The Aura card's line: what signing in with Aura means.</summary>
        public const string AuraLine = "Autopilot is maintained by the developers behind Aura, the multiagent AI solution to " +
                                       "Unreal and Unity game development. You can use your Aura subscription to run Autopilot. " +
                                       "New users get free Aura credits.";

        public const string ChoiceAuraApp = "aura_app";
        public const string ChoiceAura = "aura";
        public const string ChoiceKeys = "own_keys";
        public const string ChoiceLater = "later";

        // (the window keeps these across script reloads; what was typed in the key fields is never kept)
        public bool SetupOpen;
        public int SetupStep = 1;
        public string SetupAccess = "";
        /// <summary>What step 1 chose: aura_app | aura | own_keys | later ("" not yet).</summary>
        public string SetupChoice = "";
        public string SetupEmail = "";
        /// <summary>A browser sign-in is being prepared (the agent service installed, started, connected to).</summary>
        public bool SigningIn;
        bool creatingAccount;  // (Create Account opened Aura's account creation page: the Aura card says Sign in next)
        bool setupShare = true;
        string setupProblem = "";
        bool keysShown;
        string typedDecision = "", typedLlm = "";
        // The BYOK Coding Agent picked, and the LLM provider last picked for OpenCode ("" not picked: the account
        // frame's, else autopilot.env's)
        string pickedAgent = "", pickedProvider = "";
        // The LLM provider typedLlm was typed for, and the field it's typed in (FollowLlmProvider)
        string typedLlmFor = "";
        TextField llmInput;
        double nextKick;
        // Finished, though preferences.json couldn't be written: not asked again in this editor session
        static bool finishedThisSession;

        /// <summary>Whether the first-run setup shows (never in batch mode, nor once Autopilot is removed from this
        /// computer: Set up again opens it).</summary>
        // (not while another plugin carries the connection, HostLink: it needs no agent service or account of the window's)
        bool ShowsSetup => !Uninstall.Holds && (SetupOpen || (!Application.isBatchMode && !finishedThisSession && !Preferences.Current.Onboarded
                                                              && EditorHost.Client?.Linked != true));

        /// <summary>Open the first-run setup again (the window's menu, the Settings page).</summary>
        public void OpenSetup()
        {
            SetupOpen = true;
            SetupStep = 1;
            SetupAccess = "";
            SetupChoice = "";
            SetupEmail = "";
            SigningIn = false;
            creatingAccount = false;
            setupProblem = "";
            keysShown = false;
            pickedAgent = "";
            pickedProvider = "";
            EditorHost.Client?.ClearSignIn();
            EditorHost.Client?.AskAccount();  // (what the account is now, whatever was chosen before)
            Changed();
            Refresh(true);
        }

        void GoToStep(int step)
        {
            SetupStep = Math.Max(1, Math.Min(4, step));
            setupProblem = "";
            if (SetupStep == 3) setupShare = Preferences.Current.ShareData;
            if (SetupStep == 2) StartServiceForSetup();
            Changed();
            Refresh(true);
        }

        /// <summary>What the setup shows depends on: built again only when it changes (what is being typed stays).</summary>
        string SetupKey()
        {
            var c = EditorHost.Client;
            var s = PlaytestSettings.Get();
            return $"setup|{SetupStep}|{Wide}|{setupProblem}|{Aura.IsInstalled()}|{SetupChoice}|{SigningIn}|{keysShown}|{creatingAccount}|" +
                   $"{c?.SignIn.State}|{c?.AccountVersion}|{c != null && c.Connected}|{c != null && c.CanSignIn}|" +
                   $"{AgentService.Current}|{AgentService.Version}|{s.ServiceCommand.Length > 0}|" +
                   $"{c != null && c.RunActive}|{MemoryHasSomething(c)}|{MemoryEmpty(c)}|{c?.Problem}";
        }

        // -- the frame: stepper and title ---------------------------------------------------------------------

        void BuildSetup()
        {
            content.Clear();
            // (every step in one column: the window's width up to SetupWidth, centred beyond it, whatever the step holds)
            var page = PageBox("setup" + (Wide ? " wide" : ""), true);
            page.Add(Stepper());
            if (SetupStep == 4) BuildSetupFirstRun(page);
            else if (SetupStep == 3) BuildSetupPrivacy(page);
            else if (SetupStep == 2) BuildSetupService(page);
            else BuildSetupAccount(page);
            if (!string.IsNullOrEmpty(setupProblem)) page.Add(Problem("red", setupProblem));
        }

        VisualElement Stepper()
        {
            var row = Row("stepper");
            string Done1()
            {
                switch (SetupChoice)
                {
                    case ChoiceAura: return " · AURA";
                    case ChoiceAuraApp: return " · AURA APP";
                    case ChoiceKeys: return " · BYOK";
                    default: return "";
                }
            }

            var steps = new List<(string, bool)>
            {
                ("1 ACCOUNT" + (SetupStep > 1 ? Done1() : ""), SetupStep > 1),
                ("2 AGENT SERVICE", SetupStep > 2),
                ("3 PRIVACY SETTINGS", SetupStep > 3),
            };
            // The first run: a step unless the agent service already said it remembers this game
            if (SetupStep == 4 || !MemoryHasSomething(EditorHost.Client)) steps.Add(("4 FIRST RUN", false));
            for (var i = 0; i < steps.Count; ++i)
            {
                var now = SetupStep == i + 1;
                var state = now ? "now" : steps[i].Item2 ? "done" : "todo";
                var step = Box("step " + state + (i > 0 ? " next" : ""));
                step.Add(Box("step-bar"));
                step.Add(L(steps[i].Item1, "lbl step-label"));
                row.Add(step);
            }
            return row;
        }

        /// <summary>A step's title: the logo, then the words (18 / 600, as written).</summary>
        static VisualElement Title(string text)
        {
            var head = Row("setup-head");
            head.Add(new Logo(20));
            head.Add(L(text, "setup-title"));
            return head;
        }

        static Label Para(string text, string classes = "small t-sec2")
        {
            var l = L(text, classes);
            l.style.whiteSpace = WhiteSpace.Normal;
            return l;
        }

        /// <summary>A card's content: a row (text left, buttons right) when wide, a column when narrow.</summary>
        VisualElement Split()
        {
            return Box(Wide ? "row split" : "col split narrow");
        }

        /// <summary>A setup card's head: its micro-label and, after it, a tag.</summary>
        static VisualElement CardHead(string label, VisualElement tag = null)
        {
            var head = Row("wrap");
            head.Add(Lbl(label));
            if (tag != null)
            {
                tag.style.marginLeft = 8;
                head.Add(tag);
            }
            return head;
        }

        // -- step 1: account -------------------------------------------------------------------------------------

        void BuildSetupAccount(VisualElement page)
        {
            page.Add(Title("Welcome to Autopilot"));

            var client = EditorHost.Client;
            var sign = client?.SignIn;
            var going = SigningIn || (sign != null && sign.Going);
            var signedIn = !going && sign != null && sign.State == SignInState.SignedIn;

            if (going) page.Add(SignInWaitingCard(client, true));
            else if (signedIn) page.Add(SignedInCard(client));
            else page.Add(AuraCard(sign));

            // (while the sign-in gets ready or waits: no keys card, Cancel brings the choices back; else the keys card
            // folded, Show opens it, Hide folds it again; dimmed once signed in)
            if (!going) page.Add(KeysCard(signedIn));
        }

        VisualElement AuraCard(SignInState sign)
        {
            var failed = sign != null && sign.State == SignInState.Failed;
            var card = Card("plate stack-10 setup-card" + (failed ? " bad" : ""));
            // (a failed sign-in: Not signed in and Try again)
            card.Add(CardHead("Sign in with Aura", failed ? Tag("Not signed in", "red") : null));
            if (failed)
            {
                card.Add(Para(sign.Message, "t-red"));
                var again = Box("buttons");
                again.Add(Btn("Try again", BeginSignIn, "primary"));
                card.Add(again);
                return card;
            }
            if (creatingAccount)
            {
                // (Create Account opened Aura's account creation page: once the account is made, Sign in as below)
                var next = Card("plate stack-10 setup-card going");
                next.Add(CardHead("Sign in with Aura", Tag("In your browser", "info")));
                var finish = L("Finish creating your Aura account", "big");
                finish.style.whiteSpace = WhiteSpace.Normal;
                next.Add(finish);
                next.Add(Para("Once you have created your account, sign in and start using Autopilot", "setup-text"));
                var signIn = Box("buttons");
                signIn.Add(Btn("Sign in", BeginSignIn, "primary"));
                next.Add(signIn);
                return next;
            }
            // (both in the browser, the Aura app installed or not: Aura's account creation page first, its sign-in
            // second; the Aura app's own choice, UseAuraApp, isn't offered)
            card.Add(Para(AuraLine, "setup-text"));
            var buttons = Box("buttons");
            buttons.Add(Btn("Create Account", CreateAccount, "primary"));
            buttons.Add(Btn("Sign in", BeginSignIn, "quiet"));
            card.Add(buttons);
            return card;
        }

        /// <summary>Aura's account creation page in the browser, and the Aura card's next page: Sign in once the
        /// account is made.</summary>
        void CreateAccount()
        {
            Application.OpenURL(SignUpUrl());
            creatingAccount = true;
            Changed();
            Refresh(true);
        }

        /// <summary>Aura's account creation page (&lt;server&gt;/auth/signup?src=autopilot) on the Aura server the sign-in goes to:
        /// autopilot.env's AUTOPILOT_AURA_SERVER, else the one the package was built for (AgentService~/settings.env),
        /// else Aura's own (https://app.tryaura.dev).</summary>
        public static string SignUpUrl(string envPath = null, string installerDir = null)
        {
            const string key = "AUTOPILOT_AURA_SERVER";
            var server = AutopilotEnv.Value(key, envPath);
            var dir = installerDir ?? AgentService.InstallerDir();
            if (string.IsNullOrWhiteSpace(server) && dir != null) server = AutopilotEnv.Value(key, System.IO.Path.Combine(dir, "settings.env"));
            server = (server ?? "").Trim().TrimEnd('/');
            return (server.Length > 0 ? server : "https://app.tryaura.dev") + "/auth/signup?src=autopilot";
        }

        void UseAuraApp()
        {
            CancelSignIn();
            var s = PlaytestSettings.Get();
            s.UseWithAura = true;
            s.Save();
            SetupChoice = ChoiceAuraApp;
            SetupAccess = Preferences.AccessAura;
            SetupEmail = "";
            EditorHost.Client?.AskAccount();
            GoToStep(3);
        }

        VisualElement KeysCard(bool dimmed)
        {
            if (!keysShown)
            {
                // (folded until Show; dimmed once signed in)
                var closed = Card("plate setup-card plate-row quietcard" + (dimmed ? " dimmed" : ""));
                closed.Add(Lbl("Bring your own keys (BYOK)", "grow"));
                closed.Add(Btn("Show", () =>
                {
                    keysShown = true;
                    Refresh(true);
                }, "quiet small"));
                return closed;
            }

            var card = Card("plate stack-10 setup-card");
            var head = Row("plate-row");
            head.Add(Lbl("Bring your own keys (BYOK)", "grow"));
            head.Add(Btn("Hide", () =>
            {
                keysShown = false;
                Refresh(true);
            }, "quiet small"));
            card.Add(head);
            // (stacked, each full width, in every layout: the LLM key shares its row with the provider's dropdown)
            var fields = Box("col keys");
            fields.Add(RunClientField(() => Refresh(true)));
            fields.Add(KeyField("Decision provider API key", "Optional", typedDecision, v => typedDecision = v));
            if (string.IsNullOrEmpty(PlaytestSettings.Get().CliClient))
            {
                var provider = ProviderPick();
                fields.Add(CodingAgentField(provider));
                var llm = LlmKeyField(provider);
                llm.style.marginBottom = 0;
                fields.Add(llm);
            }
            else fields.Add(Para("Use your TypeSafe key for Jev and sign in through the selected CLI."));
            card.Add(fields);
            var buttons = Box("buttons");
            buttons.Add(Btn("Use my keys", UseMyKeys, Wide ? "" : "full"));
            card.Add(buttons);
            return card;
        }

        static VisualElement KeyField(string label, string placeholder, string value, Action<string> changed)
        {
            var box = Box("keyfield");
            box.Add(Lbl(label, "field-label"));
            box.Add(KeyInput(placeholder, value, changed));
            return box;
        }

        static TextField KeyInput(string placeholder, string value, Action<string> changed)
        {
            var f = Field(value, false, changed, placeholder);
            f.isPasswordField = true;
            f.AddToClassList("mono-field");
            f.AddToClassList("key-input");
            return f;
        }

        /// <summary>The Coding Agent's field: a dropdown the width of the card (Claude Code, Codex, OpenCode). Picking one
        /// sets the provider's dropdown to the provider it needs.</summary>
        VisualElement CodingAgentField(DropdownField provider)
        {
            var box = Box("keyfield");
            box.Add(Lbl("Coding Agent", "field-label"));
            var names = new List<string>();
            foreach (var id in CodingAgents.All) names.Add(CodingAgents.Name(id));
            var pick = new DropdownField(names, Array.IndexOf(CodingAgents.All, SetupCodingAgent())) { tooltip = "Coding Agent" };
            pick.AddToClassList("key-pick");
            pick.AddToClassList("coding-agent");
            pick.RegisterValueChangedCallback(e =>
            {
                pickedAgent = CodingAgents.All[Math.Max(0, pick.index)];
                ShowProvider(provider);
                FollowLlmProvider();
            });
            box.Add(pick);
            return box;
        }

        /// <summary>The LLM key's field: the provider's dropdown, then the key filling the rest of the row.</summary>
        VisualElement LlmKeyField(DropdownField provider)
        {
            var box = Box("keyfield");
            box.Add(Lbl("LLM provider API key", "field-label"));
            var row = Row("keyrow");
            row.Add(provider);
            FollowLlmProvider();  // (built again after an account frame: its provider may be another)
            llmInput = KeyInput("Optional", typedLlm, v => typedLlm = v);  // (the dropdown names the provider)
            row.Add(llmInput);
            box.Add(row);
            return box;
        }

        /// <summary>The provider the LLM key is saved under: Claude Code's Anthropic, Codex's OpenAI, else OpenCode's.</summary>
        string SetupKeyProvider() => CodingAgents.ProviderOf(SetupCodingAgent()) ?? SetupLlmProvider();

        /// <summary>A key typed for one LLM provider is never saved as another's: when the provider changes (a pick,
        /// an account frame), the key typed is emptied.</summary>
        void FollowLlmProvider()
        {
            var provider = SetupKeyProvider();
            if (provider == typedLlmFor) return;
            if (typedLlmFor != "")
            {
                typedLlm = "";
                llmInput?.SetValueWithoutNotify("");
            }
            typedLlmFor = provider;
        }

        /// <summary>The LLM provider's dropdown, as the Coding Agent picked wants it (ShowProvider).</summary>
        DropdownField ProviderPick()
        {
            var pick = new DropdownField { tooltip = "LLM provider" };
            pick.AddToClassList("key-pick");
            pick.AddToClassList("llm-provider");
            pick.RegisterValueChangedCallback(e =>
            {
                // (only OpenCode's can be picked: the others show their one provider, locked)
                if (SetupCodingAgent() == CodingAgents.OpenCode && pick.index >= 0) pickedProvider = LlmProviders.All[pick.index];
                FollowLlmProvider();
            });
            ShowProvider(pick);
            return pick;
        }

        /// <summary>The provider's dropdown for the Coding Agent picked: Claude Code's Anthropic or Codex's OpenAI,
        /// locked; with OpenCode, any of LlmProviders.All, on the one last picked for it.</summary>
        void ShowProvider(DropdownField pick)
        {
            var only = CodingAgents.ProviderOf(SetupCodingAgent());
            var names = new List<string>();
            if (only != null) names.Add(LlmProviders.Name(only));
            else foreach (var id in LlmProviders.All) names.Add(LlmProviders.Name(id));
            pick.choices = names;
            pick.SetValueWithoutNotify(LlmProviders.Name(only ?? SetupLlmProvider()));
            pick.SetEnabled(only == null);
        }

        /// <summary>The Coding Agent's dropdown before one is picked: the latest account frame's `coding_agent` when the
        /// agent service sent one, else autopilot.env's AUTOPILOT_AGENT, else OpenCode (unknown ones: OpenCode).</summary>
        public static string PreselectedCodingAgent(AccountInfo account, string envPath = null)
        {
            if (account?.CodingAgentSaid != null) return CodingAgents.Normalize(account.CodingAgentSaid);
            return AutopilotEnv.SavedCodingAgent(envPath);
        }

        /// <summary>OpenCode's LLM provider before one is picked: the latest account frame's `llm_provider` when the
        /// agent service sent one for OpenCode (a frame without `coding_agent` is OpenCode's), else autopilot.env's
        /// AUTOPILOT_OPENCODE_PROVIDER, else Anthropic (unknown ones: Anthropic).</summary>
        public static string PreselectedLlmProvider(AccountInfo account, string envPath = null)
        {
            if (account?.LlmProviderSaid != null && CodingAgents.Normalize(account.CodingAgent) == CodingAgents.OpenCode)
                return LlmProviders.Normalize(account.LlmProviderSaid);
            return AutopilotEnv.SavedLlmProvider(envPath);
        }

        string SetupCodingAgent()
        {
            return string.IsNullOrEmpty(pickedAgent) ? PreselectedCodingAgent(EditorHost.Client?.Account) : pickedAgent;
        }

        /// <summary>OpenCode's LLM provider: the one last picked for it, else the preselected one.</summary>
        string SetupLlmProvider()
        {
            return string.IsNullOrEmpty(pickedProvider) ? PreselectedLlmProvider(EditorHost.Client?.Account) : pickedProvider;
        }

        void UseMyKeys()
        {
            var agent = SetupCodingAgent();
            var provider = SetupLlmProvider();
            var keys = string.IsNullOrEmpty(PlaytestSettings.Get().CliClient)
                ? AutopilotEnv.ByokKeys(typedDecision, agent, provider, typedLlm)
                : new Dictionary<string, string> { [AutopilotEnv.TypeSafeKey] = typedDecision };
            if (!AutopilotEnv.Write(keys))
            {
                setupProblem = $"Couldn’t save the keys to {AutopilotEnv.DefaultPath} (error {AutopilotEnv.LastError})";
                Refresh(true);
                return;
            }
            typedDecision = "";
            typedLlm = "";
            pickedAgent = agent;  // (Back shows what was saved)
            pickedProvider = provider;
            CancelSignIn();
            var s = PlaytestSettings.Get();
            s.UseWithAura = false;
            s.Save();
            SetupChoice = ChoiceKeys;
            SetupAccess = Preferences.AccessOwnKeys;
            SetupEmail = "";
            GoToStep(2);
        }

        // -- the browser sign-in (section 2 of the spec: done by the agent service, asked over the connection) -------

        /// <summary>Sign in with Aura through the browser: the agent service is installed, started and connected to
        /// as needed (DriveSignIn, each tick), then asked to sign in.</summary>
        void BeginSignIn()
        {
            creatingAccount = false;
            var s = PlaytestSettings.Get();
            if (s.UseWithAura)
            {
                // (the sign-in is the user's own agent service's, not the Aura app's)
                s.UseWithAura = false;
                s.Save();
                var c = EditorHost.Client;
                if (c != null && (c.WantConnected || c.Connected)) EditorHost.Disconnect(false);
            }
            EditorHost.Client?.ClearSignIn();
            if (AgentService.Current == AgentService.Phase.Failed || AgentService.Current == AgentService.Phase.InstallFailed)
                AgentService.TryAgain();
            SigningIn = true;
            nextKick = 0;
            Changed();
            DriveSignIn();
            Refresh(true);
        }

        /// <summary>Stop a sign-in being prepared or waited for (the agent service is told; it shows nothing).</summary>
        void CancelSignIn()
        {
            SigningIn = false;
            EditorHost.Client?.CancelAuraSignIn();
            Changed();
        }

        /// <summary>A sign-in asked already whose connection to the agent service closed: shown as being prepared again,
        /// and the agent service brought back the same way (the client asks again once connected).</summary>
        static bool SignInAway(PlaytestClient client) => client != null && client.SignIn.Going && !client.Connected;

        /// <summary>Each tick while a sign-in is being prepared: get the agent service up and connected, then ask.</summary>
        void DriveSignIn()
        {
            var client = EditorHost.Client;
            if (!SigningIn && !SignInAway(client)) return;
            if (Uninstall.Holds) return;  // (removed from this computer: nothing is started)
            if (SigningIn && client != null && client.CanSignIn)
            {
                SigningIn = false;
                client.AskAuraSignIn();
                Changed();
                MarkDirty();
                return;
            }
            if (SigningIn && client != null && client.Connected && client.BrainRole != null && !client.BrainHasAccount)
            {
                SigningIn = false;
                setupProblem = "This agent service can’t sign in to Aura: install it again, then try again.";
                Changed();
                MarkDirty();
                return;
            }
            switch (AgentService.Current)
            {
                case AgentService.Phase.Idle:
                    AgentService.Start();
                    break;
                case AgentService.Phase.NotInstalled:
                    AgentService.Install();  // (asking to sign in is asking for what it needs)
                    break;
                case AgentService.Phase.Running:
                {
                    var now = EditorApplication.timeSinceStartup;
                    if ((client == null || !client.Connected) && now >= nextKick)
                    {
                        nextKick = now + 4;
                        var s = PlaytestSettings.Get();
                        EditorHost.Ensure().Connect(s.Url, s.Token, false);
                    }
                    break;
                }
            }
        }

        /// <summary>The Aura card while a sign-in is prepared or waits for the browser (setup: the setup's card; else
        /// the Settings page's account corner).</summary>
        VisualElement SignInWaitingCard(PlaytestClient client, bool setup)
        {
            var away = SignInAway(client);
            var waiting = !away && client != null && client.SignIn.State == SignInState.Waiting;
            var card = Card("plate stack-10 setup-card going");
            card.Add(CardHead("Sign in with Aura", waiting ? Tag("In your browser", "info") : Tag("Getting ready", "info")));

            var phase = AgentService.Current;
            var stuck = (SigningIn || away) && (phase == AgentService.Phase.NoInstaller || phase == AgentService.Phase.Failed || phase == AgentService.Phase.InstallFailed);
            if (stuck)
            {
                card.RemoveFromClassList("going");
                card.AddToClassList("bad");
                ServiceProblem(card, phase);
                var b = Box("buttons");
                if (phase != AgentService.Phase.NoInstaller)
                {
                    b.Add(Btn(ViewLogs, AgentService.OpenLog));
                    b.Add(Btn("Try again", AgentService.TryAgain, "primary"));
                }
                b.Add(Btn("Cancel", CancelSignIn, "quiet"));
                card.Add(b);
                return card;
            }
            if (!waiting)
            {
                // Getting ready: the agent service set up, started, then Aura's page opened in the browser
                var now = PreparingStep(client);
                var steps = Box("col prep-steps");
                var words = new[] { "Setting up the agent service…", "Starting the agent service…", "Finish signing in in your browser…" };
                for (var i = 0; i < words.Length; ++i) steps.Add(PrepStep(words[i], i < now ? "done" : i == now ? "now" : "todo"));
                steps.style.marginBottom = 0;
                card.Add(steps);
                var b = Box("buttons");
                b.Add(Btn("Cancel", CancelSignIn, "quiet"));
                card.Add(b);
                return card;
            }

            var url = client.SignIn.Url;
            card.Add(Progress("Finish signing in in your browser…", "big"));
            void Open()
            {
                if (!string.IsNullOrEmpty(url)) Application.OpenURL(url);
            }
            void Copy() => EditorGUIUtility.systemCopyBuffer = url;
            if (Wide)
            {
                var link = Row("linkbox");
                var u = L(url, "grow nowrap");
                u.tooltip = url;
                link.Add(u);
                link.Add(Btn("Copy link", Copy, "quiet small"));
                card.Add(link);
                var buttons = Box("buttons");
                buttons.Add(Btn("Open again", Open));
                buttons.Add(Btn("Cancel", CancelSignIn, "quiet"));
                card.Add(buttons);
                return card;
            }
            // Narrow: the link in full, the two buttons side by side, Cancel under them
            var box = Box("linkbox narrow");
            var full = L(AutopilotText.Breakable(url));
            full.tooltip = url;
            box.Add(full);
            card.Add(box);
            var pair = Row("pair");
            pair.Add(Btn("Open again", Open, "half first"));  // (first: the gap after it; USS has no :first-child)
            pair.Add(Btn("Copy link", Copy, "half"));
            card.Add(pair);
            var cancel = Btn("Cancel", CancelSignIn, "quiet");
            cancel.style.alignSelf = Align.FlexStart;
            card.Add(cancel);
            return card;
        }

        /// <summary>Which of the three getting-ready steps is going: 0 set up (installing), 1 started and connected
        /// to, 2 the browser opened on Aura's page.</summary>
        static int PreparingStep(PlaytestClient client)
        {
            if (client != null && client.SignIn.State == SignInState.Asked && client.Connected) return 2;
            var phase = AgentService.Current;
            return phase == AgentService.Phase.Installing || phase == AgentService.Phase.NotInstalled ? 0 : 1;
        }

        static VisualElement PrepStep(string text, string state)
        {
            var row = Row("prep-step " + state);
            if (state == "done") row.Add(new Check(14));
            else if (state == "now") row.Add(new Spinner());
            else row.Add(Box("ring"));
            var l = L(text, state == "now" ? "body-500 grow" : state == "done" ? "t-sec2 grow" : "t-muted grow");
            row.Add(l);
            if (state == "done") row.Add(L("done", "mono tiny t-muted"));
            return row;
        }

        VisualElement SignedInCard(PlaytestClient client)
        {
            var card = Card("plate stack-10 setup-card ok");
            card.Add(CardHead("Sign in with Aura", Tag("Signed in", "lime")));
            var who = Box("signed-as");
            who.Add(L("Signed in as", "small t-muted"));
            who.Add(L(client.SignIn.Email, "big"));
            card.Add(who);
            var buttons = Box("buttons");
            buttons.Add(Btn("Continue", () =>
            {
                SetupChoice = ChoiceAura;
                SetupAccess = Preferences.AccessAura;
                SetupEmail = client.SignIn.Email;
                GoToStep(2);
            }, "primary"));
            buttons.Add(Btn("Use another account", () =>
            {
                var c = EditorHost.Client;
                if (c == null || !c.CanSignIn)
                {
                    BeginSignIn();
                    return;
                }
                c.AuraSignOut();
                c.AskAuraSignIn();
                Refresh(true);
            }, "quiet"));
            card.Add(buttons);
            return card;
        }

        static VisualElement Progress(string text, string classes = "")
        {
            var row = Row("progress");
            row.Add(new Spinner());
            var l = L(text, classes);
            l.style.whiteSpace = WhiteSpace.Normal;
            l.AddToClassList("grow");
            row.Add(l);
            return row;
        }

        // -- step 2: the agent service ---------------------------------------------------------------------------

        /// <summary>The agent service at another address (the Agent service URL): the editor doesn't start it.</summary>
        static bool ServiceElsewhere(PlaytestSettings s) => !s.UseWithAura && !string.IsNullOrEmpty(s.Url) && s.Url != PlaytestClient.DefaultUrl;

        public const string WaitingForServiceLine = "Waiting for the agent service: start it with autopilot serve.";

        void StartServiceForSetup()
        {
            if (SetupChoice == ChoiceAuraApp || ServiceElsewhere(PlaytestSettings.Get())) return;
            if (AgentService.Current == AgentService.Phase.Idle) AgentService.Start();
        }

        void BuildSetupService(VisualElement page)
        {
            var phase = AgentService.Current;
            page.Add(Title("Set up the agent service"));

            VisualElement Later() => Btn("I’ll do it later", () => GoToStep(3), "quiet");
            VisualElement Waiting()
            {
                var b = Btn("Continue", () => GoToStep(3), "primary wide-btn");
                b.SetEnabled(false);
                return b;
            }

            // (the agent service at another address: the editor doesn't start it; Continue once it is connected)
            var client = EditorHost.Client;
            if (ServiceElsewhere(PlaytestSettings.Get()) && (client == null || !client.Connected))
            {
                var wait = Card("plate stack-10 setup-card");
                var head = Row();
                head.Add(Dot(Amber));
                head.Add(L(WaitingForServiceLine, "body-500 grow"));
                wait.Add(head);
                page.Add(wait);
                StepNav(page, Later(), Waiting());
                return;
            }
            if (ServiceElsewhere(PlaytestSettings.Get())) phase = AgentService.Phase.Running;

            switch (phase)
            {
                case AgentService.Phase.Running:
                {
                    var ok = Card("plate plate-row status-ready" + (Wide ? "" : " wrap"));
                    ok.Add(new Check());
                    var words = Box("col grow");
                    words.Add(L("Agent service ready", "ready-title"));
                    words.Add(Para("Starts with your editor", "small t-sec2"));
                    if (!Wide) words.Add(L(AgentService.Url, "ready-url"));
                    ok.Add(words);
                    if (Wide) ok.Add(L(AgentService.Url, "ready-url nowrap"));
                    page.Add(ok);
                    StepNav(page, Btn("Continue", () => GoToStep(3), "primary wide-btn"));
                    return;
                }
                case AgentService.Phase.NotInstalled:
                {
                    var card = Card("plate stack-10 setup-card");
                    var head = Row();
                    head.Add(Dot(Amber));
                    head.Add(L("Not installed on this computer", "body-500 grow"));
                    card.Add(head);
                    card.Add(Para(NotInstalledLine, "setup-text"));
                    page.Add(card);
                    StepNav(page, Later(), Btn("Install", AgentService.Install, "primary wide-btn"));
                    return;
                }
                case AgentService.Phase.Installing:
                    page.Add(StatusLine(Progress("Installing the agent service…", "body-500")));
                    StepNav(page, Waiting());
                    return;
                case AgentService.Phase.Starting:
                    page.Add(StatusLine(Progress("Starting the agent service…", "body-500")));
                    StepNav(page, Waiting());
                    return;
                case AgentService.Phase.Failed:
                case AgentService.Phase.InstallFailed:
                {
                    var card = Card("plate stack-10 setup-card bad");
                    ServiceProblem(card, phase);
                    var b = Box("buttons");
                    b.Add(Btn(ViewLogs, AgentService.OpenLog, "small"));
                    b.Add(Btn("Try again", AgentService.TryAgain, "primary small"));
                    card.Add(b);
                    page.Add(card);
                    StepNav(page, Later());
                    return;
                }
                case AgentService.Phase.NoInstaller:
                {
                    var box = Card("plate stack-10 setup-card");
                    ServiceProblem(box, phase);
                    page.Add(box);
                    StepNav(page, Later());
                    return;
                }
                default:
                    // (opened again after a script reload, or it ended meanwhile: start it)
                    EditorApplication.delayCall += StartServiceForSetup;
                    page.Add(StatusLine(Progress("Starting the agent service…", "body-500")));
                    StepNav(page, Waiting());
                    return;
            }
        }

        /// <summary>Step 2's Not installed line.</summary>
        public const string NotInstalledLine = "One-time installation takes approximately 1 minute.";

        /// <summary>Step 2's buttons, under a rule: Back on the left and the others on the right when wide; when
        /// narrow, the others full width (the last, the main one, first) and Back under them.</summary>
        void StepNav(VisualElement page, params VisualElement[] buttons)
        {
            var back = Btn("Back", () => GoToStep(1), "quiet");
            if (Wide)
            {
                var nav = Row("setup-nav wrap");
                nav.Add(back);
                nav.Add(Spacer());
                foreach (var b in buttons)
                {
                    b.AddToClassList("nav-btn");
                    nav.Add(b);
                }
                page.Add(nav);
                return;
            }
            var col = Box("col setup-nav-narrow");
            for (var i = buttons.Length - 1; i >= 0; --i)
            {
                var b = buttons[i];
                b.AddToClassList("stretch");
                if (b.ClassListContains("quiet")) b.AddToClassList("outline");
                col.Add(b);
            }
            back.AddToClassList("back");
            col.Add(back);
            page.Add(col);
        }

        /// <summary>Something going, on a plate with the info border (installing, starting, checking).</summary>
        static VisualElement StatusLine(VisualElement inner)
        {
            var box = Card("plate plate-row going status-line");
            box.Add(inner);
            inner.style.flexGrow = 1;
            return box;
        }

        /// <summary>Why the agent service isn't running, and what to do (in a card).</summary>
        void ServiceProblem(VisualElement card, AgentService.Phase phase)
        {
            if (phase != AgentService.Phase.NoInstaller)
            {
                // (the board's headline; what the agent service said under it, in mono)
                card.Add(Para(ServiceDidntStart, "t-red"));
                if (!string.IsNullOrEmpty(AgentService.Problem)) card.Add(Para(AutopilotText.Breakable(AgentService.Problem), "mono small t-muted breaks service-detail"));
                return;
            }
            // A repository checkout: no installer in the package; the command that starts it, or autopilot serve
            var head = Row();
            head.Add(Dot(Amber));
            head.Add(L("No agent service found", "body-500 grow"));
            card.Add(head);
            var s = PlaytestSettings.Get();
            var command = Box("col");
            command.Add(Lbl("Agent service command", "field-label"));
            var row = Row();
            var field = Field(s.ServiceCommand, false, v =>
            {
                s.ServiceCommand = v.Trim();
                s.Save();
            }, "e.g. C:\\autopilot\\.venv\\Scripts\\python.exe -m autopilot");
            field.AddToClassList("grow");
            field.AddToClassList("mono-field");
            row.Add(field);
            var start = Btn("Start", () =>
            {
                AgentService.TryAgain();
                Refresh(true);
            });
            start.style.marginLeft = 6;
            row.Add(start);
            command.Add(row);
            card.Add(command);
            var serve = Row("wrap serve-row");
            serve.Add(L("Or run", "t-sec2"));
            serve.Add(L("autopilot serve", "code"));
            serve.Add(Btn("Copy", () => EditorGUIUtility.systemCopyBuffer = "autopilot serve", "quiet small"));
            card.Add(serve);
        }

        // -- step 3: privacy settings ------------------------------------------------------------------------------

        void BuildSetupPrivacy(VisualElement page)
        {
            page.Add(Title("Privacy settings"));
            var card = Card("plate stack-10 setup-card");
            var share = Switch("Share my runs to improve Autopilot", setupShare);
            share.AddToClassList("share-toggle");
            share.RegisterValueChangedCallback(e => setupShare = e.newValue);
            card.Add(share);
            card.Add(Para(TrainLine, "setup-text share-text"));
            page.Add(card);

            var recap = Box("recap");
            RecapRow(recap, "Account", RecapAccount(), "t-text", null, false);
            var (service, tone, more) = RecapService();
            RecapRow(recap, "Agent service", service, tone, more, true);
            page.Add(recap);

            var finish = Btn(MemoryHasSomething(EditorHost.Client) ? "Finish" : "Next", FinishSetup, "primary wide-btn");
            var back = Btn("Back", () => GoToStep(SetupChoice == ChoiceAura || SetupChoice == ChoiceKeys ? 2 : 1), "quiet");
            if (Wide)
            {
                // (Back at the left, Next / Finish at the right)
                var buttons = Row("setup-nav plain wrap");
                buttons.Add(back);
                buttons.Add(Spacer());
                finish.AddToClassList("nav-btn");
                buttons.Add(finish);
                page.Add(buttons);
                return;
            }
            var col = Box("col setup-nav-narrow");
            col.style.borderTopWidth = 0;
            col.style.paddingTop = 0;
            finish.AddToClassList("stretch");
            col.Add(finish);
            back.AddToClassList("back");
            col.Add(back);
            page.Add(col);
        }

        void RecapRow(VisualElement recap, string key, string value, string valueClasses, string more, bool next)
        {
            var row = Box((Wide ? "row recap-row" : "col recap-row") + (next ? " next" : ""));
            var k = Lbl(key, "recap-key");
            row.Add(k);
            var v = Box("col grow");
            v.Add(Para(value, valueClasses));
            if (!string.IsNullOrEmpty(more)) v.Add(Para(more, "small t-sec2"));
            row.Add(v);
            recap.Add(row);
        }

        string RecapAccount()
        {
            switch (SetupChoice)
            {
                case ChoiceAura: return string.IsNullOrEmpty(SetupEmail) ? "Aura" : "Aura · " + SetupEmail;
                case ChoiceAuraApp: return "Aura app";
                case ChoiceKeys: return "BYOK";
                default:
                    switch (Preferences.Current.Access)
                    {
                        case Preferences.AccessAura: return "Aura";
                        case Preferences.AccessOwnKeys: return "BYOK";
                        default: return "Not chosen yet";
                    }
            }
        }

        /// <summary>The recap's agent service line: the words, their colour, and a line under them (or null).</summary>
        (string text, string classes, string more) RecapService()
        {
            if (SetupChoice == ChoiceAuraApp) return ("The Aura app runs it", "t-text", null);
            switch (AgentService.Current)
            {
                case AgentService.Phase.Running: return ("Ready", "t-text", null);
                case AgentService.Phase.Starting: return ("Starting…", "t-text", null);
                case AgentService.Phase.Installing: return ("Installing…", "t-text", null);
                case AgentService.Phase.NotInstalled:
                case AgentService.Phase.NoInstaller:
                case AgentService.Phase.InstallFailed:
                    return ("Not installed yet", "t-amber", null);
            }
            return ("Not set up yet", "t-text", null);
        }

        /// <summary>Step 3's Finish (Next when the first run may be offered): the choices saved, then connected with
        /// them; the setup ends there when the agent service already remembers this game, else goes on to step 4.</summary>
        void FinishSetup()
        {
            var changes = new Dictionary<string, object> { ["onboarded"] = true, ["share_data"] = setupShare };
            if (!string.IsNullOrEmpty(SetupAccess)) changes["access"] = SetupAccess;
            if (!Preferences.Update(changes))
            {
                Debug.LogWarning($"[Autopilot] The setup's choices couldn't be saved to {Preferences.DefaultPath}: they hold for this editor session only");
                AutopilotStatus.Say(AutopilotStatus.SetupNotSaved(Preferences.DefaultPath, Preferences.LastError));
            }
            finishedThisSession = true;
            var client = EditorHost.Client;
            var offer = !MemoryHasSomething(client);
            if (offer)
            {
                // (the setup stays open, though onboarded now: step 4 waits for what the agent service remembers)
                SetupOpen = true;
                SetupStep = 4;
                setupProblem = "";
                SigningIn = false;
                Changed();
            }
            else EndSetup();
            EditorHost.Client?.ClearSignIn();
            // The account the agent service reported may be from before the choice (a sign-in happens before Finish
            // writes access "aura"): asked again now, and again by the welcome of the connection below
            client?.AskAccount();
            // Connect with the choices made: when already connected (the setup opened again), connect again; else
            // connect now (the agent service starts with it)
            if (client != null && client.Connected && !client.RunActive)
            {
                EditorHost.Disconnect();
                EditorHost.Connect();
            }
            else if (client == null || !client.Connected) EditorHost.Connect();
            Refresh(true);
        }

        /// <summary>The setup closed (its choices saved already): the window's usual page.</summary>
        void EndSetup()
        {
            SetupOpen = false;
            SetupStep = 1;
            SetupAccess = "";
            SetupChoice = "";
            SetupEmail = "";
            SigningIn = false;
            creatingAccount = false;
            setupProblem = "";
            keysShown = false;
            pickedAgent = "";
            pickedProvider = "";
            Changed();
        }

        // -- step 4: the first run, offered when the agent service remembers nothing about this game ---------------

        /// <summary>Whether the agent service said it remembers something about this game (its memory_state: earlier
        /// runs, notes, code of its own). False while it hasn't said.</summary>
        internal static bool MemoryHasSomething(PlaytestClient client) =>
            client != null && client.MemoryKnown && (client.MemoryRuns > 0 || client.MemoryNotes || client.MemoryLib);

        /// <summary>Whether the agent service said it remembers nothing about this game (the Memory line's "empty").</summary>
        internal static bool MemoryEmpty(PlaytestClient client) =>
            client != null && client.MemoryKnown && client.MemoryRuns == 0 && !client.MemoryNotes && !client.MemoryLib;

        public const string FirstRunTitle = "Let Autopilot learn your game";
        public const string FirstRunLine = "You’ve never used Autopilot with this game before. To help Autopilot understand your game, use this first exploration run and watch it improve. Autopilot still works if you skip this step.";

        void BuildSetupFirstRun(VisualElement page)
        {
            page.Add(Title(FirstRunTitle));
            var client = EditorHost.Client;
            if (!MemoryEmpty(client))
            {
                // Connecting with the choices made, or connected and not told yet what is remembered (with something
                // remembered, the setup closes by itself: Refresh)
                var connected = client != null && client.Connected;
                page.Add(StatusLine(Progress(connected ? "Checking what Autopilot remembers about this game…" : "Connecting to the agent service…", "body-500")));
                if (!connected && client != null && !string.IsNullOrEmpty(client.Problem)) page.Add(Problem("amber", client.Problem));
                var skip = Box("buttons");
                skip.Add(Btn("Skip", SkipFirstRun, "quiet"));
                page.Add(skip);
                return;
            }

            var card = Card("plate stack-10 setup-card");
            card.Add(CardHead("Explore your game", Tag("Recommended", "lime")));
            card.Add(Para(FirstRunLine, "setup-text"));
            var s = PlaytestSettings.Get();
            card.Add(RunClientField(() => Refresh(true)));
            var minutesRow = Row("minutes-row");
            minutesRow.Add(Lbl("Minutes", "minutes-key"));
            var minutes = NumField(Math.Max(1, s.ExploreMinutes), "time-limit-field");
            minutes.RegisterValueChangedCallback(e =>
            {
                // (1 to 600 minutes)
                var kept = Math.Min(MaxMinutes, Math.Max(1, e.newValue));
                if (kept != e.newValue) minutes.SetValueWithoutNotify(kept);
                s.ExploreMinutes = kept;
                s.Save();
            });
            minutesRow.Add(minutes);
            card.Add(minutesRow);
            var buttons = Box("buttons ruled");
            var explore = Btn("Explore and finish", ExploreAndFinish, "primary wide-btn");
            // (not now: greyed, its tooltip saying why; pressed, the status line too)
            var problem = AutopilotSession.StartProblem(client, s.CliClient);
            explore.EnableInClassList("off", problem != null);
            if (problem != null) explore.tooltip = problem;
            buttons.Add(explore);
            buttons.Add(Btn("Skip and finish", SkipFirstRun, "quiet"));
            card.Add(buttons);
            page.Add(card);
        }

        /// <summary>Step 4's Explore and finish: an Explore run for the minutes set (its kind said in the frame: the
        /// agent service doesn't read it from words), shown live, and the setup closed.</summary>
        internal void ExploreAndFinish()
        {
            var s = PlaytestSettings.Get();
            var frame = ExploreFrame(s.ExploreMinutes, s.DesignBrief, s.CliClient);
            if (!AutopilotSession.Start(frame, "Explore"))
            {
                setupProblem = AutopilotSession.StartProblem(EditorHost.Client, s.CliClient);
                Refresh(true);
                return;
            }
            EndSetup();
            Composing = false;
            Page = "runs";
            Tab = "result";
            Changed();
            Refresh(true);
        }

        internal void SkipFirstRun()
        {
            EndSetup();
            Refresh(true);
        }
    }
}
