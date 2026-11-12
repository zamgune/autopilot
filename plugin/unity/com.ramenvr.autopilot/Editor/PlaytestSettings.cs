// The Autopilot window's settings, per user and per project: UserSettings/AutopilotPlaytest.json (Unity's per-user folder,
// kept out of version control), never in Assets/ or ProjectSettings/.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Autopilot.Playtest.Editor
{
    public sealed class PlaytestSettings
    {
        public string Url = PlaytestClient.DefaultUrl;
        public string Token = "";
        /// <summary>Use Autopilot with Aura: connect to the agent service the Aura app runs instead of the URL above (runs go through Aura, on the user's Aura plan).</summary>
        public bool UseWithAura;
        /// <summary>The command line that starts the agent service, "serve ..." going after it (e.g. a repository's
        /// .venv python with -m autopilot); empty: found by itself (PATH, then where the installer puts it).</summary>
        public string ServiceCommand = "";
        /// <summary>Which signed-in CLI should run new tests here. Empty uses the configured Autopilot agent.</summary>
        public string CliClient = "";
        /// <summary>The first-run setup's Explore minutes (Default Explore Minutes, 1 to 600).</summary>
        public int ExploreMinutes = 10;
        /// <summary>How the game is meant to work: the design, the rules players are told. Sent with every run from the
        /// composer (design_brief) and with an Explore run (brief), as Unreal's Design Brief; empty: none.</summary>
        public string DesignBrief = "";
        /// <summary>Show the policy log (the Policy log tab): the hello asks the agent service for every policy step
        /// (`inspect`). Applies from the next connection.</summary>
        public bool ShowPolicyLog = true;
        /// <summary>The composer's Time limit, the one it was last set to: the whole time a run may take, in minutes
        /// (an exploration's minutes of play), sent with each one as time_limit_minutes (docs/protocol.md, Runs).</summary>
        public int TimeLimitMinutes = DefaultTimeLimitMinutes;
        public const int DefaultTimeLimitMinutes = 30;
        public const int MinTimeLimitMinutes = 1;
        public const int MaxTimeLimitMinutes = 600;

        /// <summary>A time limit kept to what a run may ask for (1 to 600 minutes).</summary>
        public static int ClampTimeLimit(int minutes) => Math.Min(MaxTimeLimitMinutes, Math.Max(MinTimeLimitMinutes, minutes));
        // (runs are always real time: a "turn_based" an older version saved is read past and no longer written; so are
        // the "mode" and "brief" of the composer's tabs before it read the kind from its words (the Design Brief is its
        // own setting, "design_brief"), and "auto_connect",
        // "auto_start_service" and "hide_aura_notice": the editor always connects, and starts the agent service, by
        // itself)
        /// <summary>The composer's text: the agent service reads the kind of run from its words.</summary>
        public string Prompt = "";

        static PlaytestSettings instance;

        /// <summary>Said after every Save (the window and the Preferences page both show these settings).</summary>
        public static event Action Saved;

        /// <summary>The file the settings are kept in (UserSettings/AutopilotPlaytest.json).</summary>
        public static string FilePath => Path.Combine(Directory.GetParent(Application.dataPath).FullName, "UserSettings", "AutopilotPlaytest.json");

        public static PlaytestSettings Get()
        {
            if (instance != null) return instance;
            instance = Load(FilePath);
            return instance;
        }

        /// <summary>The settings saved in <paramref name="path"/> (the defaults where it doesn't say, or can't be
        /// read).</summary>
        public static PlaytestSettings Load(string path)
        {
            var instance = new PlaytestSettings();
            try
            {
                if (File.Exists(path))
                {
                    var o = Json.ParseObject(File.ReadAllText(path));
                    instance.Url = Json.Str(o, "url", instance.Url);
                    instance.Token = Json.Str(o, "token", "");
                    instance.UseWithAura = Json.Bool(o, "use_with_aura", false);
                    instance.ServiceCommand = Json.Str(o, "service_command", "");
                    var cli = Json.Str(o, "cli_client");
                    if (cli == CodingAgents.Claude || cli == CodingAgents.Codex) instance.CliClient = cli;
                    instance.ExploreMinutes = ClampTimeLimit((int)Json.Num(o, "explore_minutes", 10));
                    instance.DesignBrief = Json.Str(o, "design_brief", "");
                    instance.ShowPolicyLog = Json.Bool(o, "show_policy_log", true);
                    instance.TimeLimitMinutes = ClampTimeLimit((int)Math.Min(MaxTimeLimitMinutes, Math.Max(0, Json.Num(o, "time_limit_minutes", DefaultTimeLimitMinutes))));
                    instance.Prompt = Json.Str(o, "prompt", "");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Autopilot] Couldn't read " + path + ": " + e.Message);
            }
            return instance;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, Json.Pretty(new Dictionary<string, object>
                {
                    ["url"] = Url,
                    ["token"] = Token,
                    ["use_with_aura"] = UseWithAura,
                    ["service_command"] = ServiceCommand,
                    ["cli_client"] = CliClient,
                    ["explore_minutes"] = ExploreMinutes,
                    ["design_brief"] = DesignBrief,
                    ["show_policy_log"] = ShowPolicyLog,
                    ["time_limit_minutes"] = TimeLimitMinutes,
                    ["prompt"] = Prompt,
                }));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Autopilot] Couldn't save " + FilePath + ": " + e.Message);
            }
            Saved?.Invoke();
        }
    }
}
