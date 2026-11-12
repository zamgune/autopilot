// The player, as Unity has no player class of its own: the object the context op was told (context {"player"}), else
// the one tagged "Player", else the one with a PlayerInput, else the one with a CharacterController (the main camera's
// first), else the main camera's top object with a Rigidbody. Its eyes: the main camera (else the camera under it).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Autopilot.Playtest
{
    public static class Player
    {
        static string chosenPath;
        static GameObject cached;
        static int cachedFrame = -1;
        public static string FoundBy { get; private set; } = "";

        /// <summary>The player chosen by path (context {"player": path}); "" goes back to finding it.</summary>
        public static void Choose(string path)
        {
            chosenPath = string.IsNullOrEmpty(path) ? null : path;
            cachedFrame = -1;
        }

        public static GameObject Find()
        {
            if (cachedFrame == Time.frameCount && cached != null) return cached;
            cached = Search();
            cachedFrame = Time.frameCount;
            return cached;
        }

        static GameObject Search()
        {
            if (chosenPath != null)
            {
                var o = Paths.Resolve(chosenPath);
                var go = o as GameObject ?? (o as Component)?.gameObject;
                if (go != null)
                {
                    FoundBy = "chosen";
                    return go;
                }
            }
            GameObject tagged = null;
            try
            {
                tagged = GameObject.FindWithTag("Player");
            }
            catch
            {
                // (no Player tag)
            }
            if (tagged != null && !Paths.Hidden(tagged))
            {
                FoundBy = "tag";
                return tagged;
            }
#if AUTOPILOT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            foreach (var p in UnityEngine.InputSystem.PlayerInput.all)
            {
                if (p != null && p.isActiveAndEnabled)
                {
                    FoundBy = "player_input";
                    return p.gameObject;
                }
            }
#endif
#if AUTOPILOT_PHYSICS
            var controllers = Loaded.All<CharacterController>(FindObjectsInactive.Exclude);
            if (controllers.Length > 0)
            {
                var cam = UnityEngine.Camera.main;
                var mine = cam != null ? controllers.FirstOrDefault(c => cam.transform.IsChildOf(c.transform)) : null;
                FoundBy = "character_controller";
                return (mine ?? controllers[0]).gameObject;
            }
            var main = UnityEngine.Camera.main;
            if (main != null)
            {
                for (var t = main.transform; t != null; t = t.parent)
                {
                    if (t.GetComponent<Rigidbody>() != null)
                    {
                        FoundBy = "camera_rigidbody";
                        return t.gameObject;
                    }
                }
            }
#endif
            FoundBy = "";
            return null;
        }

        public static UnityEngine.Camera Eyes(GameObject player)
        {
            var main = UnityEngine.Camera.main;
            if (main != null && main.isActiveAndEnabled) return main;
            if (player != null)
            {
                var own = player.GetComponentInChildren<UnityEngine.Camera>();
                if (own != null) return own;
            }
            UnityEngine.Camera best = null;
            foreach (var c in UnityEngine.Camera.allCameras)
                if (best == null || c.depth > best.depth)
                    best = c;
            return best;
        }

        /// <summary>The body's middle (a CharacterController's or capsule's centre, else the transform).</summary>
        public static Vector3 Center(GameObject player, out float halfHeight, out float radius)
        {
            halfHeight = 0;
            radius = 0;
#if AUTOPILOT_PHYSICS
            var cc = player.GetComponent<CharacterController>();
            if (cc != null)
            {
                var s = player.transform.lossyScale;
                halfHeight = Mathf.Max(cc.height * 0.5f, cc.radius) * Mathf.Abs(s.y);
                radius = cc.radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z));
                return player.transform.TransformPoint(cc.center);
            }
            var capsule = player.GetComponent<CapsuleCollider>();
            if (capsule != null)
            {
                var s = player.transform.lossyScale;
                halfHeight = capsule.height * 0.5f * Mathf.Abs(s.y);
                radius = capsule.radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z));
                return player.transform.TransformPoint(capsule.center);
            }
#endif
            return player.transform.position;
        }

        static Vector3 lastPos;
        static double lastPosTime = -1;
        static GameObject lastPosOf;
        static Vector3 trackedVelocity;

#if AUTOPILOT_PHYSICS
        internal static Vector3 LinearVelocity(Rigidbody body)
        {
#if UNITY_6000_0_OR_NEWER
            return body.linearVelocity;
#else
            return body.velocity;
#endif
        }
#endif

        public static Vector3 Velocity(GameObject player)
        {
#if AUTOPILOT_PHYSICS
            var cc = player.GetComponent<CharacterController>();
            if (cc != null && cc.enabled) return cc.velocity;
            var rb = player.GetComponent<Rigidbody>();
            if (rb != null) return LinearVelocity(rb);
#endif
            // From where it was at an earlier frame
            var now = Time.timeAsDouble;
            if (lastPosOf == player && lastPosTime >= 0 && now - lastPosTime > 1e-4)
                trackedVelocity = (player.transform.position - lastPos) / (float)(now - lastPosTime);
            lastPos = player.transform.position;
            lastPosTime = now;
            lastPosOf = player;
            return trackedVelocity;
        }

        /// <summary>Whether the player stands on something (null when it can't tell), and its velocity.</summary>
        public static bool? Grounded(GameObject player, out Vector3 velocity)
        {
            velocity = Velocity(player);
            // The game's own flag first (a field named isGrounded / grounded / onGround)
            foreach (var mb in player.GetComponents<MonoBehaviour>())
            {
                if (mb == null || !Reflect.IsGameType(mb.GetType())) continue;
                foreach (var name in new[] { "isGrounded", "IsGrounded", "grounded", "Grounded", "onGround", "OnGround", "_isGrounded", "m_IsGrounded", "m_Grounded" })
                {
                    var f = mb.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f != null && f.FieldType == typeof(bool)) return (bool)f.GetValue(mb);
                    var p = mb.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (p != null && p.PropertyType == typeof(bool) && p.GetIndexParameters().Length == 0 && p.GetMethod != null)
                    {
                        try
                        {
                            return (bool)p.GetValue(mb);
                        }
                        catch
                        {
                            // next
                        }
                    }
                }
            }
#if AUTOPILOT_PHYSICS
            var cc = player.GetComponent<CharacterController>();
            if (cc != null && cc.enabled)
            {
                if (cc.isGrounded) return true;
                return StandingOn(player, out _, out _) != null;
            }
            if (player.GetComponent<Collider>() != null)
                return StandingOn(player, out _, out _) != null;
#endif
            return null;
        }

#if AUTOPILOT_PHYSICS
        /// <summary>What is under the feet (within a step), with the floor's normal.</summary>
        public static Collider StandingOn(GameObject player, out Vector3 normal, out float distance)
        {
            normal = Vector3.up;
            distance = 0;
            var center = Center(player, out var halfHeight, out var radius);
            if (radius <= 0) radius = 0.25f;
            var step = 0.3f;
            var cc = player.GetComponent<CharacterController>();
            if (cc != null) step = Mathf.Max(0.1f, cc.stepOffset);
            var origin = center + Vector3.up * 0.05f;
            var reach = Mathf.Max(0f, halfHeight - radius) + step + 0.05f;
            var hits = Physics.SphereCastAll(origin, radius * 0.9f, Vector3.down, reach, ~0, QueryTriggerInteraction.Ignore);
            Collider best = null;
            var bestDist = float.MaxValue;
            foreach (var h in hits)
            {
                if (h.collider == null || h.collider.transform.IsChildOf(player.transform)) continue;
                if (h.distance <= 0 && h.point == Vector3.zero) continue;  // (started inside)
                if (h.distance < bestDist)
                {
                    bestDist = h.distance;
                    best = h.collider;
                    normal = h.normal;
                }
            }
            distance = bestDist;
            return best;
        }
#endif

        /// <summary>pose: {"at", "eye", "yaw", "pitch", "velocity", "on_ground", "feet", "player", "camera"} in the kit frame.</summary>
        public static Dictionary<string, object> Pose()
        {
            var player = Find();
            if (player == null) throw new RcError("No player (no object tagged Player, with a PlayerInput or a CharacterController): context {\"player\": path} names it");
            var center = Center(player, out var halfHeight, out _);
            var eyes = Eyes(player);
            var view = eyes != null ? eyes.transform : player.transform;
            var grounded = Grounded(player, out var velocity);
            var result = new Dictionary<string, object>
            {
                ["at"] = Frame.Point(center),
                ["eye"] = Frame.Point(eyes != null ? eyes.transform.position : center),
                ["yaw"] = Json.Round(Frame.Yaw(view.rotation), 2),
                ["pitch"] = Json.Round(Frame.Pitch(view.rotation), 2),
                ["velocity"] = Frame.Point(velocity),
                ["on_ground"] = grounded,
                ["feet"] = Frame.Point(center - Vector3.up * halfHeight),
                ["player"] = Paths.Of(player),
                ["camera"] = eyes != null ? Paths.Of(eyes.gameObject) : "",
            };
            return result;
        }

        // -- movement: how the player moves, best effort, kit units -------------------------------------------

        static readonly string[] SpeedNames = { "walkSpeed", "moveSpeed", "movementSpeed", "speed", "runSpeed", "maxSpeed", "MoveSpeed", "WalkSpeed", "Speed", "m_WalkSpeed", "m_MoveSpeed", "_moveSpeed", "_speed" };
        static readonly string[] JumpSpeedNames = { "jumpSpeed", "jumpVelocity", "jumpForce", "jumpPower", "JumpSpeed", "JumpForce", "m_JumpSpeed", "_jumpForce" };
        static readonly string[] JumpHeightNames = { "jumpHeight", "JumpHeight", "m_JumpHeight", "_jumpHeight" };
        static readonly string[] AirControlNames = { "airControl", "AirControl", "m_AirControl", "_airControl", "airControlFactor", "airControlMultiplier" };
        static readonly string[] GravityNames = { "gravity", "Gravity", "gravityValue", "m_Gravity", "_gravity", "gravityScale" };

        static bool FindNumber(GameObject player, string[] names, out double value, out string source)
        {
            value = 0;
            source = null;
            foreach (var name in names)
            {
                foreach (var mb in player.GetComponents<MonoBehaviour>())
                {
                    if (mb == null || !Reflect.IsGameType(mb.GetType())) continue;
                    for (var t = mb.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                    {
                        var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                        if (f == null || !(f.FieldType == typeof(float) || f.FieldType == typeof(double) || f.FieldType == typeof(int))) continue;
                        value = Convert.ToDouble(f.GetValue(mb));
                        source = Paths.Of(mb) + "." + name;
                        return true;
                    }
                }
            }
            return false;
        }

        public static Dictionary<string, object> Movement()
        {
            var player = Find();
            if (player == null) throw new RcError("No player: context {\"player\": path} names it");
            var sources = new Dictionary<string, object>();
            object walk = null, jump = null, gravity = null, half = null, radius = null, step = null, slope = null, air = null;
            // How much the player steers in the air (0 none, 1 as on the ground), from the script's field when it has one
            if (FindNumber(player, AirControlNames, out var airControl, out var airSource))
            {
                air = Json.Round(airControl, 3);
                sources["air_control"] = airSource;
            }
            if (FindNumber(player, SpeedNames, out var speed, out var src))
            {
                walk = Json.Round(speed * Frame.Scale, 1);
                sources["walk_speed"] = src;
            }
#if AUTOPILOT_PHYSICS
            double g = Math.Abs(Physics.gravity.y);
            var gSource = "Physics.gravity";
#elif AUTOPILOT_PHYSICS2D
            double g = Math.Abs(Physics2D.gravity.y);
            var gSource = "Physics2D.gravity";
#else
            double g = 9.81;
            var gSource = "default (9.81)";
#endif
            if (FindNumber(player, GravityNames, out var gField, out var gsrc) && Math.Abs(gField) > 0.01)
            {
                if (gsrc.EndsWith(".gravityScale", StringComparison.Ordinal)) g *= Math.Abs(gField);
                else g = Math.Abs(gField);
                gSource = gsrc;
            }
            gravity = Json.Round(g * Frame.Scale, 1);
            sources["gravity"] = gSource;
            if (FindNumber(player, JumpSpeedNames, out var js, out var jsrc))
            {
                jump = Json.Round(js * Frame.Scale, 1);
                sources["jump_z"] = jsrc;
            }
            else if (FindNumber(player, JumpHeightNames, out var jh, out var jhsrc))
            {
                jump = Json.Round(Math.Sqrt(2 * g * jh) * Frame.Scale, 1);  // the speed that rises that high
                sources["jump_z"] = jhsrc + " (sqrt(2 g h))";
            }
#if AUTOPILOT_PHYSICS
            var cc = player.GetComponent<CharacterController>();
            if (cc != null)
            {
                Center(player, out var hh, out var r);
                half = Json.Round(hh * Frame.Scale, 1);
                radius = Json.Round(r * Frame.Scale, 1);
                step = Json.Round(cc.stepOffset * player.transform.lossyScale.y * Frame.Scale, 1);
                slope = Json.Round(cc.slopeLimit, 1);
                sources["shape"] = Paths.Of(cc);
            }
            else if (player.GetComponent<CapsuleCollider>() != null)
            {
                Center(player, out var hh, out var r);
                half = Json.Round(hh * Frame.Scale, 1);
                radius = Json.Round(r * Frame.Scale, 1);
                sources["shape"] = Paths.Of(player.GetComponent<CapsuleCollider>());
            }
#endif
            return new Dictionary<string, object>
            {
                ["walk_speed"] = walk,
                ["jump_z"] = jump,
                ["gravity"] = gravity,
                ["half_height"] = half,
                ["radius"] = radius,
                ["step"] = step,
                ["max_slope"] = slope,
                ["air_control"] = air,
                ["sources"] = sources,
            };
        }

        /// <summary>The walking speed in metres per second (5 when the game doesn't say).</summary>
        public static float WalkSpeed(GameObject player)
        {
            return FindNumber(player, SpeedNames, out var v, out _) && v > 0 ? (float)v : 5f;
        }
    }
}
