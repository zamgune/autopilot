// view: what the player sees and what its body feels, as Unreal's (the spatial reads are in ViewSpace.cs: held,
// openings, ways up, overhead, point blank, a thing's top surface and lane, lights, effects, highlights) and watch: how
// things move and how they are drawn.
// A "thing" is a GameObject as a player tells things apart: a renderer's object, taken up through its parents to the
// highest one that isn't a plain group (an object with nothing but a Transform, like "Pickups" or "Environment").

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace Autopilot.Playtest
{
    public static class View
    {
        /// <summary>A plain group: nothing but a Transform, at the top or in another plain group ("Level", "Pickups").
        /// An empty object inside a thing (a camera pivot, a hand socket) is part of that thing.</summary>
        public static bool IsGroup(Transform t)
        {
            for (; t != null; t = t.parent)
                if (t.GetComponents<Component>().Length > 1)
                    return false;
            return true;
        }

        public static GameObject ThingOf(GameObject go)
        {
            var t = go.transform;
            while (t.parent != null && !IsGroup(t.parent)) t = t.parent;
            return t.gameObject;
        }

        static string ClassOf(GameObject thing)
        {
            foreach (var mb in thing.GetComponents<MonoBehaviour>())
                if (mb != null && Reflect.IsGameType(mb.GetType()))
                    return mb.GetType().Name;
            foreach (var mb in thing.GetComponentsInChildren<MonoBehaviour>())
                if (mb != null && Reflect.IsGameType(mb.GetType()))
                    return mb.GetType().Name;
            var r = thing.GetComponentInChildren<Renderer>();
            return r != null ? r.GetType().Name : "GameObject";
        }

        public static bool Rendering => SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

        sealed class Seen
        {
            public GameObject Thing;
            public Bounds Box;
            public List<Renderer> Parts = new List<Renderer>();
            public float Distance;
        }

        public static Dictionary<string, object> Look(Dictionary<string, object> args)
        {
            var player = Player.Find();
            var cam = Player.Eyes(player);
            if (cam == null) throw new RcError("No camera");
            var maxDistance = (float)(Json.Num(args, "max_distance", 5000) / Frame.Scale);
            var maxCount = (int)Json.Num(args, "max_count", 24);
            var onlyList = Json.List(args, "only");
            var only = onlyList != null ? new HashSet<string>(onlyList.Select(o => Convert.ToString(o))) : null;
            // Asked for: the openings the player sees round it and how far its body could go each way (`openings`), and
            // the ways up it sees (`ways_up`), within `openings_reach` (cm); how far above the head something is
            // overhead (`overhead_reach`). A game that never asks pays nothing
            var openings = Json.Bool(args, "openings", false);
            var waysUp = Json.Bool(args, "ways_up", false);
            var openingsReach = (float)(Math.Max(100.0, Json.Num(args, "openings_reach", 3000)) / Frame.Scale);
            var overheadReach = (float)(Json.Num(args, "overhead_reach", 2000) / Frame.Scale);
            var floorSize = (float)(Json.Num(args, "floor_size", 4000) / Frame.Scale);  // (wider than this and flat: the ground, left out)
            var camPos = cam.transform.position;
            var planes = GeometryUtility.CalculateFrustumPlanes(cam);
            Vector3 feet = player != null ? Player.Center(player, out var hh, out _) - Vector3.up * hh : camPos;
            GameObject standingOn = null;
            Vector3 floorNormal = Vector3.up;
#if AUTOPILOT_PHYSICS
            if (player != null)
            {
                var floor = Player.StandingOn(player, out floorNormal, out _);
                if (floor != null) standingOn = ThingOf(floor.gameObject);
            }
#endif
            // What the player holds (as its camera draws it): never one of the things it sees
            var heldRoots = new List<Transform>();
            var held = only == null ? Drawn.Held(player, cam, out heldRoots) : new List<object>();
            // The things with a visible body
            var things = new Dictionary<GameObject, Seen>();
            foreach (var r in Loaded.All<Renderer>(FindObjectsInactive.Exclude))
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || r.forceRenderingOff) continue;
                if (Ops.IsParticles(r) || r is TrailRenderer || r is LineRenderer) continue;
                if (Paths.Hidden(r.gameObject) || r.gameObject.layer == 5 /* UI */) continue;
                if (player != null && r.transform.IsChildOf(player.transform)) continue;
                if (heldRoots.Any(h => h != null && r.transform.IsChildOf(h))) continue;
                var thing = ThingOf(r.gameObject);
                if (only != null && !only.Contains(Paths.NameOf(thing)) && !only.Contains(Paths.Of(thing))) continue;
                if (!things.TryGetValue(thing, out var seen))
                {
                    seen = new Seen { Thing = thing, Box = r.bounds };
                    things[thing] = seen;
                }
                else seen.Box.Encapsulate(r.bounds);
                seen.Parts.Add(r);
            }
            var visible = new List<Seen>();
            foreach (var s in things.Values)
            {
                if (s.Thing == standingOn && only == null) continue;  // (the ground under the feet)
                var extent = s.Box.extents;
                if (Mathf.Max(extent.x, extent.z) * 2f > floorSize && extent.y * 2f < floorSize * 0.25f) continue;  // (the ground itself)
                s.Distance = Mathf.Sqrt(s.Box.SqrDistance(camPos));
                if (s.Distance > maxDistance) continue;
                if (!GeometryUtility.TestPlanesAABB(planes, s.Box)) continue;
                if (!InSight(camPos, s, player)) continue;
                visible.Add(s);
            }
            visible.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            if (visible.Count > maxCount) visible.RemoveRange(maxCount, visible.Count - maxCount);
            // The crosshair: the nearest thing along the view's line, by its drawn box (a pickup is often a trigger,
            // which physics rays pass through), not past a solid collider nearer than it
            GameObject crosshair = null;
            Transform crosshairPart = null;  // (the collider the view's line meets: a bone's, on a character)
            var ray = new Ray(camPos, cam.transform.forward);
            var nearest = maxDistance;
#if AUTOPILOT_PHYSICS
            foreach (var h in Physics.RaycastAll(ray, maxDistance, ~0, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance))
            {
                if (player != null && h.collider.transform.IsChildOf(player.transform)) continue;
                crosshair = ThingOf(h.collider.gameObject);
                crosshairPart = h.collider.transform;
                nearest = h.distance;
                break;
            }
#endif
            foreach (var s in things.Values)
            {
                if (s.Thing == standingOn || s.Box.Contains(camPos)) continue;
                if (s.Box.IntersectRay(ray, out var d) && d < nearest)
                {
                    nearest = d;
                    crosshair = s.Thing;
                    crosshairPart = null;
                }
            }
#if AUTOPILOT_PHYSICS
            var probe = player != null ? BodyProbe.Of(player) : null;
            if (probe != null) foreach (var h in heldRoots) probe.Ignored.Add(h);
            var touchedThings = new HashSet<GameObject>();
            var touching = player != null && only == null ? Touching(player, Frame.Yaw(cam.transform.rotation), touchedThings) : null;
#endif
            var eye = new ViewSpace.Eye
            {
                Cam = cam, At = KitSpace.K(camPos), Yaw = Frame.Yaw(cam.transform.rotation),
                Fov = Camera.VerticalToHorizontalFieldOfView(cam.fieldOfView, cam.aspect > 0 ? cam.aspect : 16f / 9f),
            };
            var me = player != null ? KitSpace.K(Player.Center(player, out _, out _)) : eye.At;
            var feetK = KitSpace.K(feet).z;
            // A place on a thing: where it is, how far to turn the view toward it, how far across the floor, how high
            // against the feet
            Dictionary<string, object> Place(Vector3 k) => new Dictionary<string, object>
            {
                ["at"] = KitSpace.Vec(k),
                ["turn"] = Math.Round(Frame.Wrap(KitSpace.YawOf2D(eye.At, k) - eye.Yaw)),
                ["distance"] = Math.Round(KitSpace.Dist2D(k, me) * Frame.Scale),
                ["height_above_feet"] = Math.Round((k.z - feetK) * Frame.Scale),
            };
            // Each listed thing's own box (its top surface, what an opening leads into, the lights placed at it)
            var boxes = visible.Select(v => OwnBox.Of(v.Thing, v.Parts, v.Box)).ToList();
            // The lights helpers draw at things' places, each counted for the listed thing whose box (grown a little)
            // holds it; of several, the nearest
            var placedOn = new int[visible.Count];
            var placedBrightness = new float[visible.Count];
            if (only == null)
            {
                foreach (var light in Drawn.PlacedLights(player))
                {
                    var best = -1;
                    var nearestOff = 0f;
                    for (var i = 0; i < visible.Count; ++i)
                    {
                        var off = boxes[i].Distance(light.Key);
                        if (boxes[i].Holds(light.Key, 1f) && (best < 0 || off < nearestOff))
                        {
                            best = i;
                            nearestOff = off;
                        }
                    }
                    if (best < 0) continue;
                    ++placedOn[best];
                    placedBrightness[best] = Mathf.Max(placedBrightness[best], light.Value);
                }
            }
            var list = new List<object>();
            for (var index = 0; index < visible.Count; ++index)
            {
                var s = visible[index];
                var vp = cam.WorldToViewportPoint(s.Box.center);
                var item = new Dictionary<string, object>
                {
                    ["name"] = Paths.NameOf(s.Thing),
                    ["path"] = Paths.Of(s.Thing),
                    ["class"] = ClassOf(s.Thing),
                    ["center"] = Frame.Point(s.Box.center, 0),
                    ["size"] = Frame.Size(s.Box.size, 0),
                    ["yaw"] = Json.Round(Frame.Yaw(s.Thing.transform.rotation), 1),
                    ["distance"] = Math.Round(s.Distance * Frame.Scale),
                    ["screen_x"] = Json.Round((vp.x - 0.5) * 2, 3),
                    ["screen_y"] = Json.Round((vp.y - 0.5) * 2, 3),
                    ["top_above_feet"] = Math.Round((s.Box.max.y - feet.y) * Frame.Scale),
                    ["bottom_above_feet"] = Math.Round((s.Box.min.y - feet.y) * Frame.Scale),
                    ["crosshair_on"] = crosshair == s.Thing,
                };
                var mf = s.Thing.GetComponentInChildren<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) item["mesh"] = mf.sharedMesh.name;
                if (vp.x < 0 || vp.x > 1 || vp.y < 0 || vp.y > 1) item["partly_in_view"] = true;
                // Tipped over (a ramp: a box turned), and its own box when that isn't the box around it
                var turn = s.Thing.transform.rotation;
                if (Math.Abs(Frame.Pitch(turn)) >= 1 || Math.Abs(Frame.Roll(turn)) >= 1)
                    item["tilt"] = new Dictionary<string, object> { ["pitch"] = Math.Round(Frame.Pitch(turn)), ["roll"] = Math.Round(Frame.Roll(turn)) };
                var own = boxes[index];
                var worldCenter = KitSpace.K(s.Box.center);
                var worldHalf = s.Box.extents;  // (Unity's axes, as the own box's half sizes are)
                if ((own.Center - worldCenter).magnitude > 0.01f || Mathf.Abs(own.Half.x - worldHalf.x) > 0.005f || Mathf.Abs(own.Half.y - worldHalf.y) > 0.005f
                    || Mathf.Abs(own.Half.z - worldHalf.z) > 0.005f)
                {
                    // (Unity's own axes x, y, z are the kit's Y, Z, X: the size told along the kit's)
                    item["box"] = new Dictionary<string, object>
                    {
                        ["center"] = KitSpace.Vec(own.Center),
                        ["size"] = Frame.Size(own.Half * 2f, 0),
                        ["rotation"] = new Dictionary<string, object>
                        {
                            ["pitch"] = Json.Round(Frame.Pitch(turn), 1), ["yaw"] = Json.Round(Frame.Yaw(turn), 1), ["roll"] = Json.Round(Frame.Roll(turn), 1),
                        },
                    };
                }
#if AUTOPILOT_PHYSICS
                if (crosshair == s.Thing && crosshairPart != null && crosshairPart != s.Thing.transform && IsBone(s.Thing, crosshairPart))
                    item["crosshair_part"] = crosshairPart.name;  // (e.g. Head, Spine2)
                if (only == null) BodyCenter(item, s, camPos, player);
#endif
#if AUTOPILOT_PHYSICS
                var rb = s.Thing.GetComponentInParent<Rigidbody>();
                var speed = rb != null && !rb.isKinematic ? Player.LinearVelocity(rb).magnitude : 0f;
                if (speed > 0.01f) item["speed"] = Math.Round(speed * Frame.Scale);
                if (player != null)
                {
                    var blocks = s.Thing.GetComponentsInChildren<Collider>().Any(c => c.enabled && !c.isTrigger);
                    item["blocks_player"] = blocks;
                }
#endif
#if AUTOPILOT_PHYSICS
                if (only == null && !IsCharacter(s.Thing)) ViewSpace.Surface(item, s.Thing, boxes[index], s.Box, probe, eye, Place);
#endif
                var look = Appearance(s);
                if (only == null)
                {
                    // Its lights (its own and those placed at it), the effects playing on it, whether it is highlighted
                    var lightsOn = placedOn[index];
                    var brightest = placedBrightness[index];
                    Drawn.OwnLights(s.Thing, ref lightsOn, ref brightest);
                    if (lightsOn > 0) look["lights"] = new Dictionary<string, object> { ["on"] = lightsOn, ["brightness"] = Json.Round(brightest, 2) };
                }
                var effects = Drawn.Effects(s.Thing);
                if (effects.Count > 0) look["effects"] = effects;
                if (s.Parts.Any(Drawn.Highlighted)) look["highlighted"] = true;
                if (Watch.Limp(s.Thing)) look["limp"] = true;
                if (look.Count > 0) item["look"] = look;
                // (what it says: its first text, as Godot's; Unity's objects have no separate label)
                if (look.TryGetValue("texts", out var said) && said is List<object> texts && texts.Count > 0) item["label"] = texts[0];
                list.Add(item);
            }
            var rot = cam.transform.rotation;
            var scene = new Dictionary<string, object>();
            if (only == null)
            {
                var sun = Drawn.Sun();
                if (sun >= 0) scene["sun"] = Json.Round(sun, 2);
            }
            var result = new Dictionary<string, object>
            {
                ["visible"] = list,
                ["held"] = held,
                ["scene"] = scene,
                ["camera"] = new Dictionary<string, object>
                {
                    ["location"] = Frame.Point(camPos, 0),
                    ["yaw"] = Json.Round(Frame.Yaw(rot), 2),
                    ["pitch"] = Json.Round(Frame.Pitch(rot), 2),
                    ["fov"] = Math.Round(Camera.VerticalToHorizontalFieldOfView(cam.fieldOfView, cam.aspect > 0 ? cam.aspect : 16f / 9f)),
                },
                ["crosshair_tested"] = true,
                ["oriented"] = true,  // (each thing's box is the box around it, and `box` its own when it is turned)
                ["effects_play"] = Rendering,
            };
#if !AUTOPILOT_PHYSICS
            // (no Physics module: nothing to probe the room with)
            result["omitted"] = new List<object> { "openings", "ways_up", "point_blank", "lane", "top_slope", "overhead" };
#endif
            if (only != null) result["only"] = only.Select(o => (object)o).ToList();
            var body = new Dictionary<string, object>();
            if (player != null)
            {
                var grounded = Player.Grounded(player, out var velocity);
                body["velocity"] = Frame.Point(velocity, 0);
                body["feet_z"] = Math.Round(feet.y * Frame.Scale);
                body["on_ground"] = grounded ?? false;
                body["falling"] = grounded == false && velocity.y < -0.05f;
                body["crouched"] = false;
                if (standingOn != null)
                {
                    body["standing_on"] = Paths.NameOf(standingOn);
                    body["floor_slope_degrees"] = Math.Round(Vector3.Angle(floorNormal, Vector3.up));
                }
#if AUTOPILOT_PHYSICS
                if (touching != null) body["touching"] = touching;
                if (only == null && probe != null)
                {
                    // What is overhead: the first thing straight above the head, and the room up to it; only what the
                    // player perceives (a thing in view, or one the body is against, or the head nearly touches). With
                    // openings or ways up asked for, `under`: what is straight above whatever is in view
                    var head = new Vector3(probe.Me.x, probe.Me.y, probe.Me.z + probe.HalfHeight);
                    if (overheadReach > 0 && probe.Sight(head, head + new Vector3(0, 0, overheadReach), out var up) && up.Thing != null)
                    {
                        var above = up.Thing;
                        if (visible.Any(v => v.Thing == above) || touchedThings.Contains(above) || up.Distance <= 0.1f)
                            body["overhead"] = new Dictionary<string, object> { ["thing"] = Paths.NameOf(above), ["clearance"] = Math.Round(up.Distance * Frame.Scale) };
                        if (openings || waysUp)
                            body["under"] = new Dictionary<string, object> { ["thing"] = Paths.NameOf(above), ["clearance"] = Math.Round(up.Distance * Frame.Scale) };
                    }
                    if (openings || waysUp)
                    {
                        var holders = visible.Select((v, i) => new KeyValuePair<GameObject, OwnBox>(v.Thing, boxes[i])).ToList();
                        ViewSpace.AddOpenings(body, probe, eye, openingsReach, openings, waysUp, holders);
                    }
                }
                if (only == null && Json.Bool(args, "sonar", false))
                {
                    var sonar = Sonar(player, args, Frame.Yaw(rot));
                    if (sonar != null)
                    {
                        if (probe != null) sonar["point_blank"] = ViewSpace.PointBlank(probe, Frame.Yaw(rot));
                        body["sonar"] = sonar;
                    }
                }
#endif
            }
            result["body"] = body;
            return result;
        }

        static bool InSight(Vector3 eye, Seen s, GameObject player)
        {
#if AUTOPILOT_PHYSICS
            var targets = new[] { s.Box.center, new Vector3(s.Box.center.x, s.Box.max.y - 0.01f, s.Box.center.z), s.Box.ClosestPoint(eye) };
            foreach (var target in targets)
            {
                var dir = target - eye;
                var dist = dir.magnitude;
                if (dist < 0.01f) return true;
                var blocked = false;
                foreach (var h in Physics.RaycastAll(eye, dir / dist, dist - 0.02f, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (player != null && h.collider.transform.IsChildOf(player.transform)) continue;
                    if (h.collider.transform.IsChildOf(s.Thing.transform)) continue;
                    blocked = true;
                    break;
                }
                if (!blocked) return true;
            }
            return false;
#else
            return true;
#endif
        }

        static Dictionary<string, object> Appearance(Seen s)
        {
            var look = new Dictionary<string, object>();
            var parts = new SortedSet<string>();
            foreach (var r in s.Parts.OrderByDescending(r => r.bounds.size.sqrMagnitude).Take(8))
            {
                var mf = r.GetComponent<MeshFilter>();
                var name = mf != null && mf.sharedMesh != null ? mf.sharedMesh.name : r is SkinnedMeshRenderer sk && sk.sharedMesh != null ? sk.sharedMesh.name : r is SpriteRenderer sr && sr.sprite != null ? sr.sprite.name : null;
                if (!string.IsNullOrEmpty(name)) parts.Add(name);
            }
            if (parts.Count > 0) look["parts"] = parts.Select(p => (object)p).ToList();
            var texts = new List<object>();
            foreach (var tm in s.Thing.GetComponentsInChildren<TextMesh>())
                if (texts.Count < 4 && !string.IsNullOrWhiteSpace(tm.text)) texts.Add(Cut(tm.text));
#if AUTOPILOT_TMP
            foreach (var t in s.Thing.GetComponentsInChildren<TMPro.TMP_Text>())
                if (texts.Count < 4 && t.isActiveAndEnabled && !string.IsNullOrWhiteSpace(t.text)) texts.Add(Cut(t.text));
#endif
#if AUTOPILOT_UGUI
            foreach (var t in s.Thing.GetComponentsInChildren<UnityEngine.UI.Text>())
                if (texts.Count < 4 && t.isActiveAndEnabled && !string.IsNullOrWhiteSpace(t.text)) texts.Add(Cut(t.text));
#endif
            if (texts.Count > 0) look["texts"] = texts;
            var animator = s.Thing.GetComponentInChildren<Animator>();
            if (animator != null && animator.isActiveAndEnabled && animator.runtimeAnimatorController != null)
            {
                var clips = animator.GetCurrentAnimatorClipInfo(0);
                if (clips.Length > 0 && clips[0].clip != null) look["animation"] = clips[0].clip.name;
            }
            return look;
        }

        static string Cut(string s)
        {
            s = s.Trim();
            return s.Length > 40 ? s.Substring(0, 40) : s;
        }

#if AUTOPILOT_PHYSICS
        /// <summary>body.sonar (docs/protocol.md, view with sonar): how far the player's body could walk each way
        /// before something stops it, felt out with its own capsule (swept a step above the floor, a little thinner) and
        /// rays down onto the floor; never navigation data. ways: [{"turn" (right of the view), "clear" (cm across the
        /// floor), "by": wall|drop|steep|reach, "thing"?, "normal"? {X,Y}, "drop"? (cm), "rise"? (cm)}].</summary>
        static Dictionary<string, object> Sonar(GameObject player, Dictionary<string, object> args, double viewYaw)
        {
            var center = Player.Center(player, out var halfHeight, out var radius);
            if (radius <= 0 || halfHeight <= 0) return null;  // (no capsule to feel with)
            var cc = player.GetComponent<CharacterController>();
            var stepHeight = cc != null ? Mathf.Max(0.05f, cc.stepOffset * Mathf.Abs(player.transform.lossyScale.y)) : 0.3f;
            var maxSlope = cc != null ? cc.slopeLimit : 45f;
            var reach = (float)(Math.Max(50.0, Math.Min(5000.0, Json.Num(args, "sonar_reach", 1500))) / Frame.Scale);
            var turnsList = Json.List(args, "sonar_turns") ?? new List<object> { 0.0, -30.0, 30.0, -60.0, 60.0, -90.0, 90.0, -135.0, 135.0, 180.0 };
            var r = radius * 0.9f;
            const float seg = 0.5f;
            var dropAt = Mathf.Max(0.6f, stepHeight * 2f);
            var ways = new List<object>();
            var casts = 0;
            foreach (var t in turnsList.Take(16))
            {
                var turn = Convert.ToDouble(t, System.Globalization.CultureInfo.InvariantCulture);
                var yaw = (viewYaw + turn) * Math.PI / 180.0;
                var dir = new Vector3((float)Math.Sin(yaw), 0f, (float)Math.Cos(yaw));
                var at = center;
                var startY = at.y;
                var travelled = 0f;
                var way = new Dictionary<string, object> { ["turn"] = Json.Round(turn, 1), ["by"] = "reach" };
                var guard = 0;
                while (travelled < reach - 1e-3f && guard++ < 96 && casts < 512)
                {
                    var len = Mathf.Min(seg, reach - travelled);
                    // The capsule, its bottom a step above the feet (steps and bumps in the floor don't stop it), thinner
                    var top = at + Vector3.up * (halfHeight - r);
                    var bottom = at - Vector3.up * (halfHeight - r) + Vector3.up * stepHeight;
                    if (bottom.y > top.y) bottom.y = top.y;
                    ++casts;
                    RaycastHit? wall = null;
                    foreach (var h in Physics.CapsuleCastAll(top, bottom, r, dir, len, ~0, QueryTriggerInteraction.Ignore))
                    {
                        if (h.collider == null || h.collider.transform.IsChildOf(player.transform)) continue;
                        if (h.distance <= 0f)
                        {
                            // Overlapping it at the start: a wall only if it lies ahead
                            Vector3 near;
                            try
                            {
                                near = h.collider.ClosestPoint(at);
                            }
                            catch
                            {
                                near = h.collider.bounds.ClosestPoint(at);
                            }
                            var to = near - at;
                            to.y = 0;
                            if (Vector3.Dot(to, dir) <= 0.01f) continue;
                        }
                        if (wall == null || h.distance < wall.Value.distance) wall = h;
                    }
                    if (wall != null)
                    {
                        var hit = wall.Value;
                        var n = hit.normal;
                        if (Vector3.Angle(n, Vector3.up) <= maxSlope && hit.distance > 0f && guard < 95)
                        {
                            at += Vector3.up * 0.25f;  // walkable floor rising: go up it
                            continue;
                        }
                        travelled += Mathf.Max(0f, hit.distance);
                        way["by"] = "wall";
                        way["thing"] = Paths.NameOf(ThingOf(hit.collider.gameObject));
                        var flat = new Vector3(n.x, 0, n.z);
                        if (flat.sqrMagnitude > 1e-6f) flat.Normalize();
                        way["normal"] = new Dictionary<string, object> { ["X"] = Json.Round(flat.z, 2), ["Y"] = Json.Round(flat.x, 2) };
                        break;
                    }
                    var next = at + dir * len;
                    // The floor under the body's middle there
                    var feet = next.y - halfHeight;
                    RaycastHit? floor = null;
                    foreach (var h in Physics.RaycastAll(next, Vector3.down, halfHeight + 30f, ~0, QueryTriggerInteraction.Ignore))
                    {
                        if (h.collider.transform.IsChildOf(player.transform)) continue;
                        if (floor == null || h.distance < floor.Value.distance) floor = h;
                    }
                    var below = floor != null ? feet - floor.Value.point.y : 31f;
                    if (below > dropAt)
                    {
                        // The edge, to a 32nd of the segment: where the floor under the body's middle ends
                        float lo = 0f, hi = len;
                        for (var i = 0; i < 5; ++i)
                        {
                            var mid = (lo + hi) / 2f;
                            var probe = at + dir * mid;
                            if (Physics.Raycast(probe, Vector3.down, halfHeight + dropAt, ~0, QueryTriggerInteraction.Ignore)) lo = mid;
                            else hi = mid;
                        }
                        travelled += lo;
                        way["by"] = "drop";
                        way["drop"] = below < 30f ? (object)Json.Round(below * Frame.Scale, 0) : null;
                        break;
                    }
                    if (floor != null && Vector3.Angle(floor.Value.normal, Vector3.up) > maxSlope + 1f)
                    {
                        way["by"] = "steep";
                        way["thing"] = Paths.NameOf(ThingOf(floor.Value.collider.gameObject));
                        break;
                    }
                    at = next;
                    travelled += len;
                    if (floor != null) at.y = floor.Value.point.y + halfHeight + 0.01f;
                }
                way["clear"] = Json.Round(travelled * Frame.Scale, 0);
                var rise = at.y - startY;
                if (Math.Abs(rise) > stepHeight / 4f) way["rise"] = Json.Round(rise * Frame.Scale, 0);
                ways.Add(way);
            }
            return new Dictionary<string, object> { ["ways"] = ways, ["reach"] = Json.Round(reach * Frame.Scale, 0) };
        }

        /// <summary>Whether a transform is a bone of one of the thing's skinned meshes.</summary>
        static bool IsBone(GameObject thing, Transform t)
        {
            foreach (var sk in thing.GetComponentsInChildren<SkinnedMeshRenderer>())
                if (sk.bones != null && Array.IndexOf(sk.bones, t) >= 0)
                    return true;
            return false;
        }

        /// <summary>A character's (a skinned mesh's) body as seen: rays from the camera through a grid over its box
        /// against its own colliders, the middle of where they meet it (`body_center`), and how much of it shows past
        /// what is in front (`body_shown`), as Unreal's: its box's middle is often off its body.</summary>
        static void BodyCenter(Dictionary<string, object> item, Seen s, Vector3 camPos, GameObject player)
        {
            if (s.Thing.GetComponentInChildren<SkinnedMeshRenderer>() == null) return;
            var colliders = s.Thing.GetComponentsInChildren<Collider>().Where(c => c.enabled && !c.isTrigger).ToArray();
            if (colliders.Length == 0) return;
            const int n = 7;
            var toward = (s.Box.center - camPos).normalized;
            var right = Vector3.Cross(Vector3.up, toward).normalized;
            if (right.sqrMagnitude < 1e-6f) right = Vector3.right;
            var halfWide = Mathf.Max(s.Box.extents.x, s.Box.extents.z);
            var reach = s.Distance + s.Box.extents.magnitude * 2f + 1f;
            var sum = Vector3.zero;
            int hits = 0, shown = 0;
            for (var i = 0; i < n; ++i)
            {
                for (var j = 0; j < n; ++j)
                {
                    var through = s.Box.center + right * (((float)i / (n - 1) - 0.5f) * 2f * halfWide) + Vector3.up * (((float)j / (n - 1) - 0.5f) * 2f * s.Box.extents.y);
                    var ray = new Ray(camPos, (through - camPos).normalized);
                    RaycastHit best = default;
                    var hit = false;
                    foreach (var c in colliders)
                        if (c.Raycast(ray, out var h, reach) && (!hit || h.distance < best.distance))
                        {
                            best = h;
                            hit = true;
                        }
                    if (!hit) continue;
                    ++hits;
                    var hidden = false;
                    foreach (var other in Physics.RaycastAll(ray, best.distance - 0.05f, ~0, QueryTriggerInteraction.Ignore))
                    {
                        if (other.collider.transform.IsChildOf(s.Thing.transform) || player != null && other.collider.transform.IsChildOf(player.transform)) continue;
                        hidden = true;
                        break;
                    }
                    if (hidden) continue;
                    sum += best.point;
                    ++shown;
                }
            }
            if (shown == 0) return;
            item["body_center"] = Frame.Point(sum / shown, 0);
            if (shown < hits) item["body_shown"] = Json.Round((double)shown / hits, 2);
        }

        /// <summary>A character (not a surface to stand on): a CharacterController, a NavMeshAgent, or the player.</summary>
        static bool IsCharacter(GameObject thing)
        {
            if (thing.GetComponentInChildren<CharacterController>() != null) return true;
            return thing.GetComponentsInChildren<Component>().Any(c => c != null && c.GetType().Name == "NavMeshAgent");
        }

        /// <summary>What the body is against: colliders within a few centimetres of its shape (their things in
        /// <paramref name="touched"/>).</summary>
        static List<object> Touching(GameObject player, double viewYaw, HashSet<GameObject> touched)
        {
            var list = new List<object>();
            var center = Player.Center(player, out var halfHeight, out var radius);
            if (radius <= 0) return list;
            var up = Vector3.up * Mathf.Max(0, halfHeight - radius);
            var seen = new HashSet<GameObject>();
            foreach (var c in Physics.OverlapCapsule(center - up + Vector3.up * 0.05f, center + up, radius + 0.08f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (c.transform.IsChildOf(player.transform)) continue;
                var thing = ThingOf(c.gameObject);
                if (!seen.Add(thing)) continue;
                touched.Add(thing);
                Vector3 closest;
                try
                {
                    closest = c.ClosestPoint(center);
                }
                catch
                {
                    closest = c.bounds.ClosestPoint(center);
                }
                var away = closest - center;
                away.y = 0;
                if (away.sqrMagnitude < 1e-6f) continue;  // (under the feet: the floor)
                var yaw = Frame.YawOf(away);
                var normal = -away.normalized;
                list.Add(new Dictionary<string, object>
                {
                    ["thing"] = Paths.NameOf(thing),
                    ["yaw"] = Json.Round(yaw, 1),
                    ["turn"] = Json.Round(Frame.Wrap(yaw - viewYaw), 1),
                    ["normal"] = new Dictionary<string, object> { ["X"] = Json.Round(normal.z, 2), ["Y"] = Json.Round(normal.x, 2), ["Z"] = 0.0 },
                });
            }
            return list;
        }
#endif
    }

    /// <summary>watch: objects' transforms (relative to their parents) sampled every frame against the first sample; with
    /// `drawn`, as Unreal's: a thing's parts a player sees (its root and its `parts` biggest meshes, sampled in the thing's
    /// own frame, by their own paths) and how it is drawn: "part shown" / "part hidden" (a mesh), "effect started" /
    /// "effect stopped", "light on" / "light off", "animation started" / "animation ended" (its Animator's clips), "went
    /// limp" (a ragdoll), each with the `asset` it is about.</summary>
    public static class Watch
    {
        sealed class Sample
        {
            public string Path;
            public Transform T;
            public Vector3 Pos, Scale;
            public Quaternion Rot;
            public int Thing = -1;  // (a part of a watched thing: sampled in its frame)
            public bool Sampled;
            public readonly Dictionary<string, double> Found = new Dictionary<string, double>();
        }

        /// <summary>How a thing is drawn, as watch compares it.</summary>
        public sealed class Look
        {
            public HashSet<string> Parts = new HashSet<string>();
            public HashSet<string> Effects = new HashSet<string>();
            public HashSet<string> Animations = new HashSet<string>();
            public int Lights;
            public bool Limp;
        }

        sealed class WatchedThing
        {
            public GameObject Thing;
            public string Path;
            public Quaternion StartRotation;
            public bool Framed, Sampled;
            public Look Last;
            public readonly List<(double t, string what, string asset)> Found = new List<(double, string, string)>();

            public void Note(double t, string what, string asset)
            {
                if (!Found.Any(f => f.what == what && f.asset == asset)) Found.Add((t, what, asset));
            }
        }

        static string MeshName(Renderer r)
        {
            var mf = r.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null) return mf.sharedMesh.name;
            if (r is SkinnedMeshRenderer sk && sk.sharedMesh != null) return sk.sharedMesh.name;
            if (r is SpriteRenderer sr && sr.sprite != null) return sr.sprite.name;
            return null;
        }

        static bool Shows(Renderer r) => r != null && r.enabled && r.gameObject.activeInHierarchy && !r.forceRenderingOff && !Ops.IsParticles(r)
                                         && !(r is TrailRenderer) && !(r is LineRenderer);

        /// <summary>A ragdoll: an animated body (an Animator, now off) most of whose rigidbodies (three or more) move by
        /// physics, as Unreal's limp (most of a skeletal mesh's bodies simulate).</summary>
        public static bool Limp(GameObject thing)
        {
#if AUTOPILOT_PHYSICS
            var animator = thing.GetComponentInChildren<Animator>();
            if (animator == null || animator.isActiveAndEnabled && animator.enabled) return false;
            var bodies = thing.GetComponentsInChildren<Rigidbody>();
            if (bodies.Length < 3) return false;
            return bodies.Count(b => !b.isKinematic) * 2 >= bodies.Length;
#else
            return false;
#endif
        }

        /// <summary>How a thing is drawn now: its meshes shown, its effects playing, its lights on (its own, and those
        /// placed at it), its Animator's clips, whether it hangs limp.</summary>
        public static Look LookOf(GameObject thing, List<KeyValuePair<Vector3, float>> placed)
        {
            var look = new Look();
            foreach (var r in thing.GetComponentsInChildren<Renderer>())
            {
                if (!Shows(r)) continue;
                var name = MeshName(r);
                if (!string.IsNullOrEmpty(name)) look.Parts.Add(name);
            }
            foreach (var e in Drawn.Effects(thing))
                if (e is Dictionary<string, object> d)
                    look.Effects.Add((string)d["asset"]);
            var brightest = 0f;
            Drawn.OwnLights(thing, ref look.Lights, ref brightest);
            if (placed != null && placed.Count > 0)
            {
                var renderers = thing.GetComponentsInChildren<Renderer>().Where(Shows).ToList();
                if (renderers.Count > 0)
                {
                    var world = renderers[0].bounds;
                    foreach (var r in renderers) world.Encapsulate(r.bounds);
                    var box = OwnBox.Of(thing, renderers, world);
                    look.Lights += placed.Count(l => box.Holds(l.Key, 1f));
                }
            }
            foreach (var animator in thing.GetComponentsInChildren<Animator>())
            {
                if (animator == null || !animator.isActiveAndEnabled || animator.runtimeAnimatorController == null) continue;
                try
                {
                    foreach (var clip in animator.GetCurrentAnimatorClipInfo(0))
                        if (clip.clip != null && clip.weight > 0)
                            look.Animations.Add(clip.clip.name);
                }
                catch (Exception)
                {
                    // (an Animator not ready)
                }
            }
            look.Limp = Limp(thing);
            return look;
        }

        /// <summary>The parts of a thing watched for motion: its root, when it is drawn or carries what is drawn, and up
        /// to `count` of its shown meshes, the biggest first, not ones a skeleton carries (a weapon in a hand: that is
        /// its animation).</summary>
        static List<Transform> Parts(GameObject thing, int count)
        {
            var renderers = thing.GetComponentsInChildren<Renderer>().Where(Shows).ToList();
            var bones = new HashSet<Transform>();
            foreach (var sk in thing.GetComponentsInChildren<SkinnedMeshRenderer>())
                if (sk.bones != null)
                    foreach (var b in sk.bones)
                        if (b != null)
                            bones.Add(b);
            bool OnSkeleton(Transform t)
            {
                for (; t != null && t != thing.transform; t = t.parent)
                    if (bones.Contains(t))
                        return true;
                return false;
            }
            var list = new List<Transform>();
            if (renderers.Count > 0) list.Add(thing.transform);
            foreach (var r in renderers.Where(r => r.transform != thing.transform && !(r is SkinnedMeshRenderer) && !OnSkeleton(r.transform))
                         .OrderByDescending(r => r.bounds.extents.sqrMagnitude).Take(count))
                if (!list.Contains(r.transform))
                    list.Add(r.transform);
            return list;
        }

        public static void Handle(PlaytestClient client, long id, Dictionary<string, object> args)
        {
            var samples = new List<Sample>();
            var things = new List<WatchedThing>();
            var drawn = Json.Bool(args, "drawn", false);
            var partCount = Math.Max(0, Math.Min(16, (int)Math.Round(Json.Num(args, "parts", 3))));
            foreach (var o in Json.List(args, "objects") ?? new List<object>())
            {
                var path = Convert.ToString(o);
                var r = Paths.Resolve(path);
                if (drawn && r is GameObject thing)
                {
                    foreach (var part in Parts(thing, partCount))
                        samples.Add(new Sample { Path = part == thing.transform ? path : Paths.Of(part.gameObject), T = part, Thing = things.Count });
                    things.Add(new WatchedThing { Thing = thing, Path = path });
                    continue;
                }
                var t = r is GameObject go ? go.transform : r is Component c ? c.transform : null;
                if (t == null) continue;
                samples.Add(new Sample { Path = path, T = t, Pos = t.localPosition, Rot = t.localRotation, Scale = t.localScale, Sampled = true });
            }
            var turned = Json.Num(args, "turned", 1.0);
            var moved = Json.Num(args, "moved", 2.0);
            var resized = Json.Num(args, "resized", 0.02);
            client.AddPending(new Pending
            {
                Id = id,
                Op = "watch",
                Seconds = Json.Num(args, "seconds", 0.6),
                Tick = (p, now) =>
                {
                    var t = Convert.ToDouble(client.GameTime);
                    var placed = things.Count > 0 ? Drawn.PlacedLights(Player.Find()) : null;
                    foreach (var w in things)
                    {
                        if (w.Thing == null) continue;
                        if (!w.Framed)
                        {
                            w.Framed = true;
                            w.StartRotation = w.Thing.transform.rotation;
                        }
                        var look = LookOf(w.Thing, placed);
                        if (w.Sampled)
                        {
                            foreach (var part in look.Parts.Where(x => !w.Last.Parts.Contains(x))) w.Note(t, "part shown", part);
                            foreach (var part in w.Last.Parts.Where(x => !look.Parts.Contains(x))) w.Note(t, "part hidden", part);
                            foreach (var e in look.Effects.Where(x => !w.Last.Effects.Contains(x))) w.Note(t, "effect started", e);
                            foreach (var e in w.Last.Effects.Where(x => !look.Effects.Contains(x))) w.Note(t, "effect stopped", e);
                            if (look.Lights != w.Last.Lights) w.Note(t, look.Lights > w.Last.Lights ? "light on" : "light off", "");
                            foreach (var a in look.Animations.Where(x => !w.Last.Animations.Contains(x))) w.Note(t, "animation started", a);
                            foreach (var a in w.Last.Animations.Where(x => !look.Animations.Contains(x))) w.Note(t, "animation ended", a);
                            if (look.Limp && !w.Last.Limp) w.Note(t, "went limp", "");
                        }
                        w.Sampled = true;
                        w.Last = look;
                    }
                    foreach (var smp in samples)
                    {
                        if (smp.T == null) continue;
                        Vector3 pos, scale;
                        Quaternion rot;
                        if (smp.Thing >= 0)
                        {
                            // A part of a watched thing: where it is in that thing's frame (as it was turned when the watch
                            // began), and its size in the world
                            var w = things[smp.Thing];
                            if (w.Thing == null) continue;
                            var inverse = Quaternion.Inverse(w.StartRotation);
                            pos = inverse * (smp.T.position - w.Thing.transform.position);
                            rot = inverse * smp.T.rotation;
                            scale = smp.T.lossyScale;
                        }
                        else
                        {
                            pos = smp.T.localPosition;
                            rot = smp.T.localRotation;
                            scale = smp.T.localScale;
                        }
                        if (!smp.Sampled)
                        {
                            smp.Sampled = true;
                            smp.Pos = pos;
                            smp.Rot = rot;
                            smp.Scale = scale;
                            continue;
                        }
                        var delta = pos - smp.Pos;
                        var parent = smp.T.parent;
                        var dist = (smp.Thing < 0 && parent != null ? parent.TransformVector(delta) : delta).magnitude * Frame.Scale;
                        if (dist > moved && !smp.Found.ContainsKey("moved")) smp.Found["moved"] = t;
                        if (Quaternion.Angle(rot, smp.Rot) > turned && !smp.Found.ContainsKey("turned")) smp.Found["turned"] = t;
                        if ((Changed(scale.x, smp.Scale.x, resized) || Changed(scale.y, smp.Scale.y, resized) || Changed(scale.z, smp.Scale.z, resized))
                            && !smp.Found.ContainsKey("changed size"))
                            smp.Found["changed size"] = t;
                    }
                    if (now - p.Start < p.Seconds) return false;
                    var changes = new List<(double t, Dictionary<string, object> change)>();
                    foreach (var smp in samples)
                    {
                        foreach (var f in smp.Found)
                        {
                            // (parts that go by one path: each change once, the first time)
                            var same = changes.FindIndex(c => (string)c.change["object"] == smp.Path && (string)c.change["what"] == f.Key && !c.change.ContainsKey("asset"));
                            if (same >= 0)
                            {
                                if (f.Value < changes[same].t) changes[same] = (f.Value, changes[same].change);
                                continue;
                            }
                            changes.Add((f.Value, new Dictionary<string, object> { ["t"] = f.Value, ["object"] = smp.Path, ["what"] = f.Key }));
                        }
                    }
                    foreach (var w in things)
                        foreach (var f in w.Found)
                            changes.Add((f.t, new Dictionary<string, object> { ["t"] = f.t, ["object"] = w.Path, ["what"] = f.what, ["asset"] = f.asset }));
                    var result = new Dictionary<string, object>
                    {
                        ["changes"] = changes.OrderBy(c => c.t).Select(c => { c.change["t"] = c.t; return (object)c.change; }).ToList(),
                    };
                    if (drawn) result["drawn"] = true;  // (things watched as drawn)
                    client.Respond(p.Id, result);
                    return true;
                },
            });
        }

        static bool Changed(float now, float then, double fraction)
        {
            return Math.Abs(now - then) > Math.Abs(then) * fraction + 1e-6;
        }
    }
}
