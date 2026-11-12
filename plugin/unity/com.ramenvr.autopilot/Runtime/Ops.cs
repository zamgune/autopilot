// The brain's requests (docs/protocol.md), handled on the main thread.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Autopilot.Playtest
{
    public static class Ops
    {
        static bool paused;
        static float timeScaleBefore = 1f;

        /// <summary>The scene the game was launched with (play mode entered with): what restart reopens.</summary>
        public static string LaunchScene = "";

        /// <summary>The first scene of this launch (a player's restart of it starts the game over: Restart).</summary>
        public static string FirstScene = "";

        /// <summary>The editor's answer to `levels` (EditorHost): the project's scenes, not only the build's.</summary>
        public static Func<Dictionary<string, object>> EditorLevels;

        /// <summary>The editor's session still starting (EditorHost): the project index written before play mode, or
        /// play mode being entered. Said as `session` status's `loading`, so the agent service waits it out.</summary>
        public static Func<bool> EditorLoading;

        public static bool IsPaused => Application.isPlaying && (paused || Time.timeScale == 0f);

        public static void Handle(PlaytestClient client, long id, string op, Dictionary<string, object> args)
        {
            switch (op)
            {
                case "key":
                    Hands.KeyOp(client, id, args);
                    break;
                case "input":
                    Hands.InputOp(client, id, args);
                    break;
                case "pointer":
                    Hands.PointerOp(client, id, args);
                    break;
                case "view":
                    client.Respond(id, View.Look(args));
                    break;
                case "rc":
                    Detectors.BridgeActedFrame = Time.frameCount;  // (what it moves isn't the game teleporting it)
                    Rc(client, id, args);
                    break;
                case "context":
                    client.Respond(id, Context(client, args));
                    break;
                case "session":
                    Session(client, id, args);
                    break;
                case "levels":
                    client.Respond(id, EditorLevels != null ? EditorLevels() : PlayerLevels());
                    break;
                case "pause":
                    Pause(client, id, args);
                    break;
                case "move":
                    Move(client, id, args);
                    break;
                case "watch":
                    Watch.Handle(client, id, args);
                    break;
                case "screen":
                {
                    var shown = new Dictionary<string, object> { ["texts"] = ScreenText.Texts().ToDictionary(p => p.Key, p => (object)p.Value) };
                    if (Json.Bool(args, "ui", false)) shown["ui"] = ScreenUI.List();  // (and the controls, as Unreal's screen {"ui": true})
                    client.Respond(id, shown);
                    break;
                }
                case "ui":
                    client.Respond(id, new Dictionary<string, object> { ["elements"] = ScreenUI.List() });
                    break;
                case "ui_act":
                {
                    var error = ScreenUI.Act(args);
                    if (string.IsNullOrEmpty(error)) client.Respond(id, new Dictionary<string, object>());
                    else client.RespondError(id, error);
                    break;
                }
                case "timestep":
                {
                    var fixedStep = Json.Bool(args, "fixed", false);
                    Time.captureFramerate = fixedStep ? Math.Max(1, (int)Json.Num(args, "fps", 30)) : 0;
                    client.Respond(id, new Dictionary<string, object>());
                    break;
                }
                case "detectors":
                    client.Detectors.Configure(args);
                    client.Respond(id, new Dictionary<string, object>());
                    break;
                case "ping":
                    client.Respond(id, new Dictionary<string, object>
                    {
                        ["game_time"] = Application.isPlaying ? Json.Round(Time.timeAsDouble, 3) : 0.0,
                        ["wall_time"] = Json.Round((DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds, 3),
                    });
                    break;
                case "codex_manifest":
                    Codex.Manifest(client, id, args);
                    break;
                case "codex_read":
                    Codex.Read(client, id, args);
                    break;
                case "find":
                    client.Respond(id, Find(args));
                    break;
                case "transforms":
                    client.Respond(id, Transforms(args));
                    break;
                case "pose":
                    client.Respond(id, Player.Pose());
                    break;
                case "movement":
                    client.Respond(id, Player.Movement());
                    break;
                case "record":
                    Media.Record(client, id, args);
                    break;
                case "capture":
                    Media.Capture(client, id, args);
                    break;
                default:
                    client.RespondError(id, $"unknown op {op}");
                    break;
            }
        }

        static void Rc(PlaytestClient client, long id, Dictionary<string, object> args)
        {
            var verb = Json.Str(args, "verb");
            var body = Json.Obj(args, "body");
            if (body == null)
            {
                client.RespondError(id, "rc needs a body");
                return;
            }
            switch (verb)
            {
                case "call":
                    client.Respond(id, Reflect.Call(body, Bootstrap.CoroutineHost));
                    break;
                case "property":
                    client.Respond(id, Reflect.Property(body));
                    break;
                case "describe":
                    client.Respond(id, Reflect.Describe(body));
                    break;
                default:
                    client.RespondError(id, $"unknown rc verb {verb}");
                    break;
            }
        }

        // -- context ---------------------------------------------------------------------------------------

        static Dictionary<string, object> Context(PlaytestClient client, Dictionary<string, object> args)
        {
            if (args.ContainsKey("player")) Player.Choose(Json.Str(args, "player"));
            var running = client.Host.SessionRunning;
            var player = running ? Player.Find() : null;
            var cam = running ? Player.Eyes(player) : null;
            var scene = SceneManager.GetActiveScene();
            var controller = "";
#if AUTOPILOT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            if (player != null)
            {
                var pi = player.GetComponentInParent<UnityEngine.InputSystem.PlayerInput>() ?? player.GetComponentInChildren<UnityEngine.InputSystem.PlayerInput>();
                if (pi != null) controller = Paths.Of(pi);
            }
#endif
            var scenes = new List<object>();
            for (var i = 0; i < SceneManager.sceneCount; ++i)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.isLoaded) scenes.Add(string.IsNullOrEmpty(s.path) ? s.name : s.path);
            }
            var build = new List<object>();
            for (var i = 0; i < SceneManager.sceneCountInBuildSettings; ++i) build.Add(SceneUtility.GetScenePathByBuildIndex(i));
            return new Dictionary<string, object>
            {
                ["world"] = scene.IsValid() ? (string.IsNullOrEmpty(scene.path) ? scene.name : scene.path) : "",
                ["character"] = player != null ? Paths.Of(player) : "",
                ["player_found_by"] = player != null ? Player.FoundBy : "",
                ["controller"] = controller,
                ["input_subsystem"] = "",
                ["camera_manager"] = cam != null ? Paths.Of(cam.gameObject) : "",
                ["game_name"] = Application.productName,
                ["host"] = client.Host.HostName,
                ["engine"] = "unity",
                ["engine_version"] = Application.unityVersion,
                ["project_dir"] = client.Host.ProjectDir ?? "",
                ["session_running"] = running,
                ["paused"] = IsPaused,
                ["game_time"] = Application.isPlaying ? Json.Round(Time.timeAsDouble, 3) : 0.0,
                ["controls"] = running ? Hands.Controls() : new List<object>(),
                ["input"] = Hands.Capabilities(),
                ["scenes"] = scenes,
                ["build_scenes"] = build,
                ["launch_scene"] = LaunchScene,
                ["rendering"] = View.Rendering,
                ["net"] = Net.Info(),  // (how the game is networked: Netcode for GameObjects or Mirror)
                // The game's OnGUI controls routed through the bridge (screen, ui, ui_act read and click them while they
                // are drawn: never in a batch-mode player, which calls no OnGUI) and how many it drew so far
                ["imgui"] = new Dictionary<string, object>
                {
                    ["calls"] = LegacyInput.ImguiCalls,
#if AUTOPILOT_IMGUI
                    ["drawn"] = Imgui.Drawn,
#else
                    ["drawn"] = 0,
#endif
                },
            };
        }

        // -- session (a player build; the editor's host handles its own) -------------------------------------

        static void Session(PlaytestClient client, long id, Dictionary<string, object> args)
        {
            var action = Json.Str(args, "action", "status");
            if (action == "status" || action == "")
            {
                var status = new Dictionary<string, object> { ["running"] = client.Host.SessionRunning };
                status["loading"] = EditorLoading != null ? EditorLoading() : restarting > 0;  // (a scene still loading)
                client.Respond(id, status);
                return;
            }
            if (client.Host.Session(client, id, action, args)) return;
            var map = Json.Str(args, "map");
            // A player: a scene asked for that the build hasn't is refused before anything changes; a start with a map
            // while another scene plays switches to it (as Godot's game does)
            if (!string.IsNullOrEmpty(map) && action != "stop" && BuildIndexOf(map) < 0)
            {
                client.RespondError(id, $"No scene {map} in the build", 404);
                return;
            }
            if (action == "start" && !string.IsNullOrEmpty(map) && !IsActiveScene(map))
            {
                Restart(client, id, map);
                return;
            }
            if (action != "restart")
            {
                client.Respond(id, new Dictionary<string, object> { ["running"] = Application.isPlaying, ["scene"] = SceneManager.GetActiveScene().path });
                return;
            }
            Restart(client, id, map);
        }

        /// <summary>The build index of a scene asked for by build index, path or name; -1 when the build hasn't it.</summary>
        public static int BuildIndexOf(string map)
        {
            if (int.TryParse(map, out var index)) return index >= 0 && index < SceneManager.sceneCountInBuildSettings ? index : -1;
            var byPath = SceneUtility.GetBuildIndexByScenePath(map);
            if (byPath >= 0) return byPath;
            var name = System.IO.Path.GetFileNameWithoutExtension(map);
            for (var i = 0; i < SceneManager.sceneCountInBuildSettings; ++i)
                if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(SceneUtility.GetScenePathByBuildIndex(i)), name, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        /// <summary>Whether the scene playing is the one asked for (a build index, path or name).</summary>
        public static bool IsActiveScene(string map)
        {
            var active = SceneManager.GetActiveScene();
            if (!active.IsValid() || string.IsNullOrEmpty(map)) return false;
            if (int.TryParse(map, out var index)) return active.buildIndex == index;
            return string.Equals(active.path, map, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(active.name, System.IO.Path.GetFileNameWithoutExtension(map), StringComparison.OrdinalIgnoreCase);
        }

        static int restarting;

        /// <summary>Whether `map` (a scene name, path or build index) names the scene at `path`.</summary>
        public static bool SameScene(string map, string path)
        {
            if (string.IsNullOrEmpty(map) || string.IsNullOrEmpty(path)) return false;
            if (int.TryParse(map, out var index)) return SceneUtility.GetBuildIndexByScenePath(path) == index;
            return map == path || map == System.IO.Path.GetFileNameWithoutExtension(path);
        }

        /// <summary>The game's DontDestroyOnLoad objects gone (the plugin's own are hidden: they stay).</summary>
        static void DropCarried()
        {
            foreach (var go in Paths.Roots(Paths.DontDestroyOnLoadScene()).ToList())
                UnityEngine.Object.Destroy(go);
        }

        /// <summary>`levels` in a player: the build's scenes, the one playing open (docs/protocol.md).</summary>
        static Dictionary<string, object> PlayerLevels()
        {
            var levels = new List<object>();
            for (var i = 0; i < SceneManager.sceneCountInBuildSettings; ++i) levels.Add(SceneUtility.GetScenePathByBuildIndex(i));
            var active = SceneManager.GetActiveScene();
            var playing = active.IsValid() ? active.path : "";
            return new Dictionary<string, object> { ["levels"] = levels, ["open"] = playing, ["playing"] = playing };
        }

        /// <summary>Reopen the launch scene (or `map`: a scene name, path or build index) and answer once it is loaded.
        /// A player's restart; the editor's is Stop and Play (EditorHost.StopAndPlay). A player can't do that, so the
        /// game's first scene, which starts the game, gets what a new launch would: nothing carried over. Any other
        /// scene keeps it: it may need what the first scene set up.</summary>
        public static void Restart(PlaytestClient client, long id, string map)
        {
            var target = string.IsNullOrEmpty(map) ? LaunchScene : map;
            if (string.IsNullOrEmpty(target)) target = SceneManager.GetActiveScene().path;
            Hands.ReleaseAll();
            if (paused) SetPaused(false);
            var old = SceneManager.GetActiveScene().handle;
            try
            {
                if (SameScene(target, FirstScene)) DropCarried();
                if (int.TryParse(target, out var index)) SceneManager.LoadScene(index, LoadSceneMode.Single);
                else SceneManager.LoadScene(target, LoadSceneMode.Single);
            }
            catch (Exception e)
            {
                client.RespondError(id, $"Couldn't open {target}: {e.Message}", 500);
                return;
            }
            var frames = 0;
            ++restarting;
            client.AddPending(new Pending
            {
                Id = id,
                Op = "session",
                Seconds = 30,
                Tick = (p, now) =>
                {
                    var active = SceneManager.GetActiveScene();
                    if (active.handle != old && active.isLoaded && ++frames >= 2)
                    {
                        restarting = Math.Max(0, restarting - 1);
                        client.Respond(p.Id, new Dictionary<string, object> { ["running"] = true, ["scene"] = active.path });
                        return true;
                    }
                    if (now - p.Start > p.Seconds)
                    {
                        restarting = Math.Max(0, restarting - 1);
                        client.RespondError(p.Id, $"Timed out opening {target} (is it in the build's scene list?)", 500);
                        return true;
                    }
                    return false;
                },
            });
        }

        // -- pause -----------------------------------------------------------------------------------------

        static void SetPaused(bool value)
        {
            if (value && !paused)
            {
                timeScaleBefore = Time.timeScale > 0 ? Time.timeScale : 1f;
                Time.timeScale = 0f;
                AudioListener.pause = true;
            }
            else if (!value && paused)
            {
                if (Time.timeScale == 0f) Time.timeScale = timeScaleBefore;
                AudioListener.pause = false;
            }
            paused = value;
        }

        static void Pause(PlaytestClient client, long id, Dictionary<string, object> args)
        {
            if (!Application.isPlaying)
            {
                client.RespondError(id, "No game is running");
                return;
            }
            var why = Net.CantPause();
            if (why != null)
            {
                // (a networked client: its server, in another process, keeps the game going)
                client.Respond(id, new Dictionary<string, object> { ["paused"] = IsPaused, ["why"] = why });
                return;
            }
            SetPaused(Json.Bool(args, "paused", false));
            client.Respond(id, new Dictionary<string, object> { ["paused"] = IsPaused });
        }

        /// <summary>A new session starts unpaused (the bridge's own pause doesn't outlive it).</summary>
        public static void ResetSession()
        {
            paused = false;
            Time.captureFramerate = 0;
        }

        // -- find, transforms --------------------------------------------------------------------------------

        static Dictionary<string, object> Find(Dictionary<string, object> args)
        {
            var className = Json.Str(args, "class");
            var name = Json.Str(args, "name");
            var limit = (int)Json.Num(args, "limit", 10000);
            var inactive = Json.Bool(args, "inactive", false);
            // things: the scene's things as a player tells them apart (Unreal's actors): objects that aren't plain
            // groups (nothing but a Transform), at the top or under a plain group; not the parts under them
            var things = Json.Bool(args, "things", false);
            Type type = null;
            if (!string.IsNullOrEmpty(className) && className != "GameObject" && className != "Actor" && className != "Object")
            {
                type = Reflect.FindType(className);
                if (type == null || !(typeof(Component).IsAssignableFrom(type) || type.IsInterface))
                    throw new RcError($"no component type named {className}");
            }
            var objects = new List<object>();
            foreach (var go in Paths.All(inactive))
            {
                if (objects.Count >= limit) break;
                if (!string.IsNullOrEmpty(name) && go.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (type != null && go.GetComponent(type) == null) continue;
                if (things && (View.IsGroup(go.transform) || go.transform.parent != null && !View.IsGroup(go.transform.parent))) continue;
                objects.Add(Paths.Of(go));
            }
            return new Dictionary<string, object> { ["objects"] = objects };
        }

        /// <summary>A particle system's renderer (only when the project has the Particle System module).</summary>
        public static bool IsParticles(Renderer r)
        {
#if AUTOPILOT_PARTICLES
            return r is ParticleSystemRenderer;
#else
            return false;
#endif
        }

        public static Bounds? BoundsOf(GameObject go)
        {
            Bounds? box = null;
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || IsParticles(r)) continue;
                if (box == null) box = r.bounds;
                else
                {
                    var b = box.Value;
                    b.Encapsulate(r.bounds);
                    box = b;
                }
            }
#if AUTOPILOT_PHYSICS
            foreach (var c in go.GetComponentsInChildren<Collider>())
            {
                if (!c.enabled) continue;
                if (box == null) box = c.bounds;
                else
                {
                    var b = box.Value;
                    b.Encapsulate(c.bounds);
                    box = b;
                }
            }
#endif
            return box;
        }

        static Dictionary<string, object> Transforms(Dictionary<string, object> args)
        {
            var result = new Dictionary<string, object>();
            foreach (var o in Json.List(args, "objects") ?? new List<object>())
            {
                var path = Convert.ToString(o);
                var r = Paths.Resolve(path);
                var go = r as GameObject ?? (r as Component)?.gameObject;
                if (go == null)
                {
                    result[path] = null;
                    continue;
                }
                var t = go.transform;
                var velocity = Vector3.zero;
#if AUTOPILOT_PHYSICS
                var cc = go.GetComponent<CharacterController>();
                var rb = go.GetComponent<Rigidbody>();
                if (cc != null && cc.enabled) velocity = cc.velocity;
                else if (rb != null) velocity = Player.LinearVelocity(rb);
#endif
                var box = BoundsOf(go);
                result[path] = new Dictionary<string, object>
                {
                    ["location"] = Frame.Point(t.position),
                    ["rotation"] = Frame.Rotation(t.rotation),
                    ["velocity"] = Frame.Point(velocity),
                    ["center"] = Frame.Point(box?.center ?? t.position),
                    ["extent"] = Frame.Size(box?.extents ?? Vector3.zero),
                    ["active"] = go.activeInHierarchy,
                };
            }
            return new Dictionary<string, object> { ["objects"] = result };
        }

        // -- move ------------------------------------------------------------------------------------------

        /// <summary>move: {"direction": kit {"X","Y","Z"}, "scale", "seconds", "via"?: "auto"|"stick"|"keys"|"controller"}.
        /// The player walks that way for `seconds`: through the game's own move action when the bridge can drive it (a
        /// gamepad stick bound to it, else its keys, 8 ways), else by moving its CharacterController (or Rigidbody)
        /// at its walking speed. Answers {"moved": cm across the floor, "via"}.</summary>
        static void Move(PlaytestClient client, long id, Dictionary<string, object> args)
        {
            var player = Player.Find();
            if (player == null)
            {
                client.RespondError(id, "No player pawn");
                return;
            }
            var dir = Frame.DirectionToUnity(Json.Obj(args, "direction"));
            dir.y = 0;
            var scale = (float)Json.Num(args, "scale", 1.0);
            var asked = Json.Str(args, "via", "auto");
            string via = null;
#if AUTOPILOT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            UnityEngine.InputSystem.InputAction action = null;
            if (asked != "controller") via = Hands.MoveVia(asked, out action);
#endif
            if (via == null && asked != "auto" && asked != "controller")
            {
                client.RespondError(id, $"move can't go via {asked} here (no move action bound to it)");
                return;
            }
            via = via ?? "controller";
            var start = player.transform.position;
            var speed = Player.WalkSpeed(player);
            client.AddPending(new Pending
            {
                Id = id,
                Op = "move",
                Seconds = Json.Num(args, "seconds", 0.35),
                Tick = (p, now) =>
                {
                    var alive = player != null;
                    if (alive && now - p.Start < p.Seconds)
                    {
                        var want = dir.sqrMagnitude > 1e-6f ? dir.normalized * Mathf.Clamp(scale, -1f, 1f) : Vector3.zero;
                        if (via == "controller")
                        {
                            Shove(player, want * speed * Time.deltaTime);
                        }
#if AUTOPILOT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
                        else
                        {
                            var cam = Player.Eyes(player);
                            var view = cam != null ? cam.transform : player.transform;
                            var forward = view.forward;
                            forward.y = 0;
                            if (forward.sqrMagnitude < 1e-6f) forward = player.transform.forward;
                            forward.Normalize();
                            var right = new Vector3(forward.z, 0, -forward.x);
                            Hands.MoveInput(action, via, new Vector2(Vector3.Dot(want, right), Vector3.Dot(want, forward)));
                        }
#endif
                        return false;
                    }
#if AUTOPILOT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
                    Hands.MoveStop();
#endif
                    var moved = 0.0;
                    if (alive)
                    {
                        var d = player.transform.position - start;
                        d.y = 0;
                        moved = Math.Round(d.magnitude * Frame.Scale * 10) / 10;
                    }
                    client.Respond(p.Id, new Dictionary<string, object> { ["moved"] = moved, ["via"] = via });
                    return true;
                },
            });
        }

        static void Shove(GameObject player, Vector3 step)
        {
#if AUTOPILOT_PHYSICS
            var cc = player.GetComponent<CharacterController>();
            if (cc != null && cc.enabled)
            {
                cc.Move(step);
                return;
            }
            var rb = player.GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic)
            {
                rb.MovePosition(rb.position + step);
                return;
            }
#endif
            player.transform.position += step;
        }
    }
}
