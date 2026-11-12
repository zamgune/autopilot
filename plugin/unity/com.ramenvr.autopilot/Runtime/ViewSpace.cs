// view's spatial reads (docs/protocol.md), as Unreal's PlaytestOpsView.cpp, PlaytestOpsOpenings.cpp, PlaytestOpsSonar.cpp
// and PlaytestOpsLook.cpp tell them: what the player holds (`held`), the openings it sees round it and how far its body
// could go across the view (`openings`, `free`), the ways up it sees (`ways_up`, with their lanes), what is straight
// above its head (`overhead`, `under`), sonar's `point_blank`, a thing's top surface (`top_slope_degrees`, a slope's
// `low_end`, `high_end`, `uphill` and `lane`), and how it is drawn besides its meshes (`look.lights`, `look.effects`,
// `look.highlighted`; `scene.sun`).
//
// The geometry is worked out in the kit's axes in metres (KX forward = Unity z, KY right = Unity x, KZ up = Unity y:
// Unreal's axes, so the Unreal code's arithmetic carries over as it is) and told in the kit frame (centimetres). Every
// probe is the player's own body's, as a player finds things by looking and trying: lines stopped by what stops its
// CharacterController (the layers its layer collides with; triggers never), its capsule swept, lines down onto the
// floor. Never navigation data.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Autopilot.Playtest
{
    /// <summary>A thing's own box: its parts' bounds in its own frame (the transform's axes), as Unreal's FPlaytestShape;
    /// in the kit's axes, metres.</summary>
    public sealed class OwnBox
    {
        public Vector3 Center, Half;
        public readonly Vector3[] Axis = { new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1) };

        public int UpIndex()
        {
            var best = 0;
            for (var i = 1; i < 3; ++i)
                if (Mathf.Abs(Axis[i].z) > Mathf.Abs(Axis[best].z))
                    best = i;
            return best;
        }

        public Vector3 TopNormal()
        {
            var k = UpIndex();
            return Axis[k] * (Axis[k].z >= 0 ? 1f : -1f);
        }

        public float HalfAlong(int i) => i == 0 ? Half.x : i == 1 ? Half.y : Half.z;

        /// <summary>Whether a point is at it: inside it grown on every side by a quarter of its size along that axis, at
        /// least reach.</summary>
        public bool Holds(Vector3 p, float reach)
        {
            var d = p - Center;
            for (var i = 0; i < 3; ++i)
                if (Mathf.Abs(Vector3.Dot(d, Axis[i])) > HalfAlong(i) + Mathf.Max(reach, HalfAlong(i) * 0.5f))
                    return false;
            return true;
        }

        public float Distance(Vector3 p)
        {
            var d = p - Center;
            var sq = 0f;
            for (var i = 0; i < 3; ++i)
            {
                var a = Mathf.Abs(Vector3.Dot(d, Axis[i])) - HalfAlong(i);
                if (a > 0) sq += a * a;
            }
            return Mathf.Sqrt(sq);
        }

        /// <summary>The own box of a thing from its renderers (each mesh's own bounds taken into the thing's frame); the
        /// world box (square to the world) for a part with no mesh bounds of its own.</summary>
        public static OwnBox Of(GameObject thing, IEnumerable<Renderer> parts, Bounds world)
        {
            var t = thing.transform;
            var local = new Bounds();
            var any = false;
            void Add(Vector3 worldPoint)
            {
                var p = t.InverseTransformPoint(worldPoint);
                if (!any)
                {
                    local = new Bounds(p, Vector3.zero);
                    any = true;
                }
                else local.Encapsulate(p);
            }
            foreach (var r in parts)
            {
                if (r == null) continue;
                Bounds own;
                Transform frame;
                var mf = r.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                {
                    own = mf.sharedMesh.bounds;
                    frame = r.transform;
                }
                else if (r is SkinnedMeshRenderer sk)
                {
                    own = sk.localBounds;
                    frame = sk.rootBone != null ? sk.rootBone : sk.transform;
                }
                else
                {
                    own = r.bounds;
                    frame = null;
                }
                for (var i = 0; i < 8; ++i)
                {
                    var c = own.center + Vector3.Scale(own.extents, new Vector3((i & 1) != 0 ? 1 : -1, (i & 2) != 0 ? 1 : -1, (i & 4) != 0 ? 1 : -1));
                    Add(frame != null ? frame.TransformPoint(c) : c);
                }
            }
            var box = new OwnBox();
            if (!any)
            {
                box.Center = KitSpace.K(world.center);
                box.Half = KitSpace.KSize(world.extents);
                return box;
            }
            var s = t.lossyScale;
            box.Center = KitSpace.K(t.TransformPoint(local.center));
            // (the local axes x, y, z: Unity right, up, forward, each in the kit's axes)
            var ext = Vector3.Scale(local.extents, new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z)));
            box.Half = ext;
            box.Axis[0] = KitSpace.K(t.rotation * Vector3.right);
            box.Axis[1] = KitSpace.K(t.rotation * Vector3.up);
            box.Axis[2] = KitSpace.K(t.rotation * Vector3.forward);
            return box;
        }
    }

    /// <summary>The kit's axes in metres and back, and what a look at the room shares.</summary>
    public static class KitSpace
    {
        /// <summary>A Unity point or direction in the kit's axes (metres).</summary>
        public static Vector3 K(Vector3 u) => new Vector3(u.z, u.x, u.y);

        /// <summary>A size (no sign): the same permutation.</summary>
        public static Vector3 KSize(Vector3 u) => new Vector3(u.z, u.x, u.y);

        /// <summary>A kit-axes point (metres) in Unity's.</summary>
        public static Vector3 U(Vector3 k) => new Vector3(k.y, k.z, k.x);

        /// <summary>A kit-axes point (metres) told in the kit frame (centimetres, whole).</summary>
        public static Dictionary<string, object> Vec(Vector3 k) => Frame.Point(U(k), 0);

        public static Dictionary<string, object> Size(Vector3 halfK) => Frame.Point(U(halfK * 2f), 0);

        /// <summary>A kit yaw (degrees) as a direction across the floor in the kit's axes.</summary>
        public static Vector3 DirOf(double degrees)
        {
            var r = degrees * Math.PI / 180.0;
            return new Vector3((float)Math.Cos(r), (float)Math.Sin(r), 0f);
        }

        public static float Dist2D(Vector3 a, Vector3 b) => new Vector2(a.x - b.x, a.y - b.y).magnitude;

        public static double YawOf2D(Vector3 from, Vector3 to) => Math.Atan2(to.y - from.y, to.x - from.x) * 180.0 / Math.PI;

        public static Vector3 Flat(Vector3 v) => new Vector3(v.x, v.y, 0f);

        public static Vector3 SafeNormal(Vector3 v) => v.sqrMagnitude < 1e-10f ? Vector3.zero : v.normalized;

        public static float PointDistToSegment2D(Vector3 p, Vector3 a, Vector3 b)
        {
            var ab = new Vector2(b.x - a.x, b.y - a.y);
            var ap = new Vector2(p.x - a.x, p.y - a.y);
            var t = ab.sqrMagnitude < 1e-10f ? 0f : Mathf.Clamp01(Vector2.Dot(ap, ab) / ab.sqrMagnitude);
            return (ap - ab * t).magnitude;
        }
    }

    /// <summary>A hit of a probe, in the kit's axes (metres).</summary>
    public struct KHit
    {
        public Vector3 Point, Normal;
        public float Distance;
        public Collider Collider;
        public bool StartedInside;
        public GameObject Thing => Collider != null ? View.ThingOf(Collider.gameObject) : null;
    }

#if AUTOPILOT_PHYSICS
    /// <summary>The player's body as it probes the room, as Unreal's FBodyProbe: what stops it (the layers its layer
    /// collides with), its capsule, how high it steps and how steep a floor it stands on. In the kit's axes, metres.</summary>
    public sealed class BodyProbe
    {
        public GameObject Player;
        public int Mask = Physics.DefaultRaycastLayers;
        public float Radius, HalfHeight, StepHeight = 0.3f, WalkableZ = 0.7f;
        public float Feet;
        public Vector3 Me;  // (the body's middle)
        /// <summary>Also left out of every probe: what the player holds (a held thing's colliders aren't the room).</summary>
        public HashSet<Transform> Ignored = new HashSet<Transform>();

        public static BodyProbe Of(GameObject player)
        {
            var center = Autopilot.Playtest.Player.Center(player, out var halfHeight, out var radius);
            if (radius <= 0 || halfHeight <= 0) return null;
            var probe = new BodyProbe { Player = player, Radius = radius, HalfHeight = halfHeight, Me = KitSpace.K(center) };
            probe.Feet = probe.Me.z - halfHeight;
            var cc = player.GetComponent<CharacterController>();
            if (cc != null)
            {
                probe.StepHeight = Mathf.Max(0.05f, cc.stepOffset * Mathf.Abs(player.transform.lossyScale.y));
                probe.WalkableZ = Mathf.Cos(cc.slopeLimit * Mathf.Deg2Rad);
            }
            // (what its layer collides with: Physics settings' layer collision matrix)
            var layer = player.layer;
            var mask = 0;
            for (var i = 0; i < 32; ++i)
                if (!Physics.GetIgnoreLayerCollision(layer, i))
                    mask |= 1 << i;
            probe.Mask = mask & ~(1 << 2);  // (never Ignore Raycast)
            return probe;
        }

        bool Skip(Collider c)
        {
            if (c == null) return true;
            var t = c.transform;
            if (t.IsChildOf(Player.transform)) return true;
            foreach (var i in Ignored)
                if (i != null && t.IsChildOf(i))
                    return true;
            return false;
        }

        /// <summary>The first thing along a line (kit points) that stops the body.</summary>
        public bool Line(Vector3 a, Vector3 b, out KHit hit) => Cast(a, b, Mask, out hit);

        /// <summary>The first thing along a line that stops a line of sight (any layer but Ignore Raycast).</summary>
        public bool Sight(Vector3 a, Vector3 b, out KHit hit) => Cast(a, b, Physics.DefaultRaycastLayers, out hit);

        bool Cast(Vector3 a, Vector3 b, int mask, out KHit hit)
        {
            hit = default;
            var d = b - a;
            var dist = d.magnitude;
            if (dist < 1e-5f) return false;
            var best = float.MaxValue;
            foreach (var h in Physics.RaycastAll(KitSpace.U(a), KitSpace.U(d / dist), dist, mask, QueryTriggerInteraction.Ignore))
            {
                if (Skip(h.collider) || h.distance >= best) continue;
                best = h.distance;
                hit = new KHit { Point = KitSpace.K(h.point), Normal = KitSpace.K(h.normal), Distance = h.distance, Collider = h.collider };
            }
            return best < float.MaxValue;
        }

        /// <summary>Its own capsule, a hair thinner and lifted a step's height off the feet, swept from one place for the
        /// feet to another: whether something stops it on the way (starting inside something stops it at once).</summary>
        public bool Sweep(Vector3 fromFeet, Vector3 toFeet, out KHit hit)
        {
            hit = default;
            var r = Mathf.Max(0.01f, Radius - 0.02f);
            var half = Mathf.Max(r, HalfHeight - StepHeight * 0.5f - 0.02f);
            var lift = new Vector3(0, 0, StepHeight + 0.02f + half);
            var from = fromFeet + lift;
            var d = toFeet + lift - from;
            var dist = d.magnitude;
            var up = new Vector3(0, 0, half - r);
            var p1 = KitSpace.U(from + up);
            var p2 = KitSpace.U(from - up);
            if (dist < 1e-5f)
            {
                foreach (var c in Physics.OverlapCapsule(p1, p2, r, Mask, QueryTriggerInteraction.Ignore))
                {
                    if (Skip(c)) continue;
                    hit = new KHit { Point = fromFeet, Collider = c, StartedInside = true };
                    return true;
                }
                return false;
            }
            var dir = KitSpace.U(d / dist);
            var best = float.MaxValue;
            foreach (var h in Physics.CapsuleCastAll(p1, p2, r, dir, dist, Mask, QueryTriggerInteraction.Ignore))
            {
                if (Skip(h.collider)) continue;
                var inside = h.distance <= 0f && h.point == Vector3.zero;
                if (h.distance >= best) continue;
                best = h.distance;
                hit = new KHit
                {
                    Point = inside ? from - lift : KitSpace.K(h.point), Normal = inside ? -d / dist : KitSpace.K(h.normal),
                    Distance = h.distance, Collider = h.collider, StartedInside = inside,
                };
            }
            if (best == float.MaxValue) return false;
            // (where the feet are when it stops)
            var at = fromFeet + (toFeet - fromFeet) * (best / dist);
            hit.Point = hit.StartedInside ? fromFeet : hit.Point;
            Location = at;
            return true;
        }

        /// <summary>Where the feet were when the last sweep stopped.</summary>
        public Vector3 Location;

        /// <summary>The floor under a place, straight down from `from` by `depth`: how high it is and whether a body
        /// stands on it.</summary>
        public bool Floor(Vector3 from, float depth, out float z, out bool walkable)
        {
            z = 0;
            walkable = false;
            if (!Line(from, from - new Vector3(0, 0, depth), out var hit)) return false;
            z = hit.Point.z;
            walkable = hit.Normal.z >= WalkableZ;
            return true;
        }
    }

    /// <summary>The spatial reads of a look (View.Look calls them).</summary>
    public static class ViewSpace
    {
        // -- openings and ways up (Unreal's PlaytestOpsOpenings.cpp: the same constants, in metres) ------------------
        const float OpeningRayDegrees = 1f, OpeningDeeper = 1.5f, OpeningDeeperShare = 0.3f, OpeningSector = 15f;
        const float OpeningSameHeight = 0.6f, OpeningFlat = 0.08f, OpeningSeenShare = 1.2f, OpeningAside = 0.12f;
        const int MaxOpenings = 5, OpeningsTried = 8, OpeningEdgeSteps = 3;
        const float WayUpEvery = 5f, WayUpPace = 0.4f, WayUpReach = 15f, WayUpRise = 1f, WayUpFootApart = 1.5f;
        const int MaxWaysUp = 3;
        const float WayUpProbe = 0.4f;
        const int WayUpSideProbes = 2;
        static readonly float[] WayUpLaneStations = { 1f / 3f, 2f / 3f };
        const float WayUpLaneStep = 0.2f, WayUpLaneReach = 6f, WayUpLaneDeep = 30f, WayUpLaneBelow = 10f;
        const int WayUpLaneHalvings = 3;

        static double Round(double v) => Math.Round(v);

        static double Cm(float metres) => Math.Round(metres * Frame.Scale);

        sealed class Ray
        {
            public float Yaw, Knee, Chest;
            public GameObject Thing;
            public float Open => Mathf.Min(Knee, Chest);
        }

        /// <summary>A look's camera: where it is (kit metres), whether a place is on screen and where.</summary>
        public sealed class Eye
        {
            public Camera Cam;
            public Vector3 At;
            public double Yaw;
            public float Fov;

            public bool OnScreen(Vector3 k, out float x, out float y)
            {
                var vp = Cam.WorldToViewportPoint(KitSpace.U(k));
                x = (vp.x - 0.5f) * 2f;
                y = (vp.y - 0.5f) * 2f;
                return vp.z > 0.01f;
            }
        }

        /// <summary>body.openings, body.free (with openings) and body.ways_up (with ways_up), as Unreal's AddOpenings.
        /// holders: the things seen, with their own boxes (what an opening leads into).</summary>
        public static void AddOpenings(Dictionary<string, object> body, BodyProbe p, Eye eye, float reach, bool openings, bool waysUp,
            List<KeyValuePair<GameObject, OwnBox>> holders)
        {
            var height = p.HalfHeight * 2f;
            var knee = Mathf.Max(p.StepHeight + 0.05f, height * 0.28f);
            var chest = Mathf.Max(knee + 0.1f, height * 0.62f);
            var fov = eye.Fov;
            var yaw = eye.Yaw;
            var n = Mathf.Clamp(Mathf.FloorToInt(fov / OpeningRayDegrees) + 1, 3, 361);
            var left = (float)(yaw - fov * 0.5);
            double Turn(Vector3 from, Vector3 to) => Round(Frame.Wrap(KitSpace.YawOf2D(from, to) - yaw));
            float Depth(float degrees, float above, out GameObject thing)
            {
                var from = new Vector3(p.Me.x, p.Me.y, p.Feet + above);
                if (p.Line(from, from + KitSpace.DirOf(degrees) * reach, out var hit))
                {
                    thing = hit.Thing;
                    return hit.Distance;
                }
                thing = null;
                return reach;
            }
            var rays = new Ray[n];
            for (var i = 0; i < n; ++i)
            {
                var ray = rays[i] = new Ray();
                ray.Yaw = Mathf.Min(left + i * OpeningRayDegrees, (float)(yaw + fov * 0.5));
                ray.Knee = Depth(ray.Yaw, knee, out var kneeThing);
                ray.Chest = Depth(ray.Yaw, chest, out var chestThing);
                ray.Thing = chestThing != null ? chestThing : kneeThing;
            }
            float Deeper(float near) => Mathf.Max(OpeningDeeper, OpeningDeeperShare * near);
            bool Standing(Ray ray) => ray.Knee < reach && ray.Chest < reach && Mathf.Abs(ray.Knee - ray.Chest) <= OpeningSameHeight;
            Vector3 PointOf(float degrees, float distance) => new Vector3(p.Me.x, p.Me.y, 0) + KitSpace.DirOf(degrees) * distance;
            var me2 = new Vector3(p.Me.x, p.Me.y, 0);

            if (openings)
            {
                var gaps = new List<Gap>();
                Vector3 WallWay(int side, int step)
                {
                    int a = side + step, b = side + 2 * step;
                    if (a < 0 || b < 0 || a >= n || b >= n || !Standing(rays[a]) || !Standing(rays[b])) return Vector3.zero;
                    var p0 = PointOf(rays[side].Yaw, rays[side].Open);
                    var p1 = PointOf(rays[a].Yaw, rays[a].Open);
                    var p2 = PointOf(rays[b].Yaw, rays[b].Open);
                    var run = p2 - p0;
                    var length = new Vector2(run.x, run.y).magnitude;
                    if (length < 0.1f) return Vector3.zero;
                    var u = run / length;
                    var w = p1 - p0;
                    if (Mathf.Abs(u.x * w.y - u.y * w.x) > OpeningFlat) return Vector3.zero;
                    return u;
                }
                Vector3 Refine(float nearYaw, float farYaw, float nearDepth)
                {
                    for (var s = 0; s < OpeningEdgeSteps; ++s)
                    {
                        var mid = (nearYaw + farYaw) * 0.5f;
                        var open = Mathf.Min(Depth(mid, knee, out _), Depth(mid, chest, out _));
                        if (open <= nearDepth + Deeper(nearDepth) * 0.5f)
                        {
                            nearYaw = mid;
                            nearDepth = open;
                        }
                        else farYaw = mid;
                    }
                    return PointOf(nearYaw, nearDepth);
                }
                for (var i = 0; i + 1 < n; ++i)
                {
                    if (!Standing(rays[i]) || rays[i + 1].Open - rays[i].Open < Deeper(rays[i].Open)) continue;
                    for (var k = i + 2; k < n; ++k)
                    {
                        if (!Standing(rays[k]) || rays[k - 1].Open - rays[k].Open < Deeper(rays[k].Open)) continue;
                        var l = PointOf(rays[i].Yaw, rays[i].Open);
                        var r = PointOf(rays[k].Yaw, rays[k].Open);
                        var across = r - l;
                        var chord = new Vector2(across.x, across.y).magnitude;
                        if (chord < 0.01f) break;
                        var outward = new Vector3(-across.y / chord, across.x / chord, 0);
                        var middle = (l + r) * 0.5f;
                        if (Vector3.Dot(outward, middle - me2) < 0) outward = -outward;
                        var beyond = float.MaxValue;
                        for (var j = i + 1; j < k; ++j) beyond = Mathf.Min(beyond, Vector3.Dot(PointOf(rays[j].Yaw, rays[j].Open) - l, outward));
                        if (beyond < p.Radius || chord < OpeningSeenShare * p.Radius) break;
                        var near = Refine(rays[i].Yaw, rays[i + 1].Yaw, rays[i].Open);
                        var far = Refine(rays[k].Yaw, rays[k - 1].Yaw, rays[k].Open);
                        var gap = new Gap { Along = WallWay(i, -1) };
                        if (gap.Along != Vector3.zero) far = near + gap.Along * Vector3.Dot(far - near, gap.Along);
                        else if ((gap.Along = WallWay(k, 1)) != Vector3.zero) near = far + gap.Along * Vector3.Dot(near - far, gap.Along);
                        across = far - near;
                        gap.Width = new Vector2(across.x, across.y).magnitude;
                        if (gap.Width >= OpeningSeenShare * p.Radius)
                        {
                            gap.Along = across / gap.Width;
                            gap.Out = new Vector3(-gap.Along.y, gap.Along.x, 0);
                            gap.Middle = (near + far) * 0.5f;
                            if (Vector3.Dot(gap.Out, gap.Middle - me2) < 0) gap.Out = -gap.Out;
                            gap.Beyond = beyond;
                            gap.Wall = rays[i].Thing != null ? rays[i].Thing : rays[k].Thing;
                            gap.Near = rays[i].Thing;
                            gap.Far = rays[k].Thing;
                            gap.Distance = KitSpace.Dist2D(gap.Middle, p.Me);
                            gaps.Add(gap);
                        }
                        i = k - 1;
                        break;
                    }
                }
                gaps.Sort((a, b) => a.Distance.CompareTo(b.Distance));
                var list = new List<object>();
                for (var g = 0; g < gaps.Count && g < OpeningsTried && list.Count < MaxOpenings; ++g)
                {
                    var gap = gaps[g];
                    var outward = gap.Out;
                    var middle = gap.Middle;
                    var top = p.Feet + chest;
                    var down = chest + 6f;
                    float beforeOut = p.Radius + 0.4f, afterOut = p.Radius + 0.6f;
                    float zBefore = p.Feet, zIn = p.Feet, zAfter = p.Feet;
                    bool wBefore = false, wIn = false, wAfter = false;
                    var before0 = middle - outward * beforeOut;
                    var after0 = middle + outward * afterOut;
                    var floor = p.Floor(new Vector3(before0.x, before0.y, top), down, out zBefore, out wBefore)
                                && p.Floor(new Vector3(middle.x, middle.y, top), down, out zIn, out wIn)
                                && p.Floor(new Vector3(after0.x, after0.y, top), down, out zAfter, out wAfter);
                    if (!floor)
                    {
                        zBefore = zIn = zAfter = p.Feet;
                    }
                    var seen = new Vector3(middle.x, middle.y, (floor ? zIn : p.Feet) + chest);
                    if (p.Sight(eye.At, seen, out var block) && block.Distance < (seen - eye.At).magnitude - 0.3f) continue;  // hidden from the eyes
                    var stepSize = floor ? Mathf.Max(Mathf.Abs(zIn - zBefore), Mathf.Abs(zAfter - zIn)) : -1f;
                    var floorOk = floor && wBefore && wIn && wAfter && stepSize <= p.StepHeight + 0.01f;
                    var passes = false;
                    foreach (var aside in new[] { 0f, OpeningAside, -OpeningAside, 2f * OpeningAside, -2f * OpeningAside })
                    {
                        var line = gap.Middle + gap.Along * aside;
                        var from = line - outward * beforeOut;
                        var to = line + outward * afterOut;
                        if (floorOk && !p.Sweep(new Vector3(from.x, from.y, zBefore), new Vector3(to.x, to.y, zAfter), out _))
                        {
                            passes = true;
                            middle = line;
                            break;
                        }
                    }
                    var after = middle + outward * afterOut;
                    var meFeet = new Vector3(p.Me.x, p.Me.y, p.Feet);
                    var afterFeet = new Vector3(after.x, after.y, floor ? zAfter : p.Feet);
                    var reachStraight = !p.Sweep(meFeet, afterFeet, out var hit);
                    var o = new Dictionary<string, object>
                    {
                        ["at"] = KitSpace.Vec(new Vector3(middle.x, middle.y, floor ? zIn : p.Feet)),
                        ["turn"] = Turn(p.Me, middle),
                        ["distance"] = Cm(KitSpace.Dist2D(middle, p.Me)),
                        ["width"] = Cm(gap.Width),
                        ["yaw"] = Round(Math.Atan2(outward.y, outward.x) * 180.0 / Math.PI),
                        ["depth_beyond"] = Cm(Mathf.Min(gap.Beyond, reach)),
                    };
                    if (floor)
                    {
                        o["floor_step"] = Cm(stepSize);
                        o["floor_above_feet"] = Cm(zIn - p.Feet);
                    }
                    o["reachable_straight"] = reachStraight;
                    if (!reachStraight)
                    {
                        o["blocked_at"] = Cm(KitSpace.Dist2D(p.Location, p.Me));
                        var by = hit.Thing;
                        if (by != null) o["blocked_by"] = Paths.NameOf(by);
                    }
                    o["passable"] = passes;
                    var inside = new Vector3(after.x, after.y, (floor ? zAfter : p.Feet) + height);
                    GameObject into = p.Sight(inside, inside + new Vector3(0, 0, 20f), out var over) ? over.Thing : null;
                    if (into == null)
                    {
                        var smallest = float.MaxValue;
                        foreach (var holder in holders)
                        {
                            var s = holder.Value;
                            var d = inside - new Vector3(0, 0, height * 0.5f) - s.Center;
                            var upI = s.UpIndex();
                            var isIn = s.HalfAlong(upI) * 2f >= height * 2f;
                            for (var a = 0; a < 3 && isIn; ++a)
                                isIn = Mathf.Abs(Vector3.Dot(d, s.Axis[a])) <= s.HalfAlong(a) + (a == upI ? 0.1f : -2f * p.Radius);
                            var volume = s.Half.x * s.Half.y * s.Half.z;
                            if (isIn && volume < smallest)
                            {
                                smallest = volume;
                                into = holder.Key;
                            }
                        }
                    }
                    if (into != null) o["into"] = Paths.NameOf(into);
                    if (gap.Wall != null) o["wall"] = Paths.NameOf(gap.Wall);
                    if (gap.Near != null && gap.Far != null) o["sides"] = new List<object> { Paths.NameOf(gap.Near), Paths.NameOf(gap.Far) };
                    list.Add(o);
                }
                body["openings"] = list;
                var free = new List<object>();
                for (var from = 0f; from < fov - 0.5f; from += OpeningSector)
                {
                    float atKnee = reach, atChest = reach;
                    foreach (var ray in rays)
                    {
                        var off = ray.Yaw - left;
                        if (off >= from - 0.01f && off < from + OpeningSector - 0.01f)
                        {
                            atKnee = Mathf.Min(atKnee, ray.Knee);
                            atChest = Mathf.Min(atChest, ray.Chest);
                        }
                    }
                    free.Add(new Dictionary<string, object>
                    {
                        ["turn"] = Round((from + Mathf.Min(from + OpeningSector, fov)) * 0.5 - fov * 0.5),
                        ["knee"] = Math.Round(atKnee * Frame.Scale / 10.0) * 10.0,
                        ["chest"] = Math.Round(atChest * Frame.Scale / 10.0) * 10.0,
                    });
                }
                body["free"] = free;
            }

            if (waysUp) body["ways_up"] = WaysUp(p, eye, reach, rays, n, left, knee, chest, height);
        }

        sealed class Gap
        {
            public Vector3 Middle, Along, Out;
            public float Width, Beyond, Distance;
            public GameObject Wall, Near, Far;
        }

        sealed class WayUp
        {
            public Vector3 Foot, TopAt;
            public float Rise, FootDistance, TopDistance;
            public double Degrees;
            public bool Side, Reach = true, Lane;
            public float Width, RightDepth, LeftDepth, Clear;
            public int RightBy, LeftBy, BelowBy;

            public WayUp Copy() => (WayUp)MemberwiseClone();
        }

        static List<object> WaysUp(BodyProbe p, Eye eye, float reach, Ray[] rays, int n, float left, float knee, float chest, float height)
        {
            var found = new List<WayUp>();
            var fov = eye.Fov;
            var yaw = eye.Yaw;
            bool FloorAt(Vector3 at, float fromZ, out float z)
            {
                return p.Floor(new Vector3(at.x, at.y, fromZ + p.StepHeight + 0.05f), p.StepHeight * 2f + 0.65f, out z, out var walkable) && walkable;
            }
            Vector3 March(Vector3 from, float fromZ, Vector3 way, float sign)
            {
                var last = new Vector3(from.x, from.y, fromZ);
                var end = last;
                var z = fromZ;
                var level = 0;
                for (var gone = WayUpPace; gone <= WayUpReach && level < 2; gone += WayUpPace)
                {
                    var next = new Vector3(from.x, from.y, 0) + way * gone;
                    var f = z;
                    if (p.Line(new Vector3(last.x, last.y, z + knee), new Vector3(next.x, next.y, z + knee), out _)
                        || !FloorAt(next, z, out f) || Mathf.Abs(f - z) > p.StepHeight + 0.01f)
                        break;
                    if ((f - z) * sign > 0.03f)
                    {
                        level = 0;
                        end = new Vector3(next.x, next.y, f);
                    }
                    else if (++level == 1 && sign < 0) end = new Vector3(next.x, next.y, f);
                    z = f;
                    last = new Vector3(next.x, next.y, f);
                }
                return end;
            }
            bool SideWayUp(float degrees, float sightTo, out WayUp way)
            {
                way = null;
                var dir = KitSpace.DirOf(degrees);
                var to = Mathf.Min(WayUpReach, Mathf.Min(reach, sightTo - p.Radius));
                var probes = 0;
                for (var o = WayUpPace; o <= to && probes < WayUpSideProbes; o += WayUpPace)
                {
                    var at = new Vector3(p.Me.x, p.Me.y, 0) + dir * o;
                    if (!p.Floor(new Vector3(at.x, at.y, eye.At.z), eye.At.z - p.Feet + p.StepHeight + 0.05f, out var z, out var walkable)
                        || !walkable || z < p.Feet + p.StepHeight + 0.05f)
                        continue;
                    var tread = new Vector3(at.x, at.y, z);
                    foreach (var k in found)
                        if (z <= k.TopAt.z + 0.05f && KitSpace.PointDistToSegment2D(tread, k.Foot, k.TopAt) <= WayUpFootApart)
                            return false;
                    var seenAt = tread + new Vector3(0, 0, 0.05f);
                    if (p.Sight(eye.At, seenAt, out var block) && block.Distance < (seenAt - eye.At).magnitude - 0.2f) continue;
                    ++probes;
                    float best = 0f, bestYaw = 0f;
                    void Try(float toward)
                    {
                        var w = KitSpace.DirOf(toward);
                        if (FloorAt(tread + w * WayUpProbe, z, out var zUp) && FloorAt(tread - w * WayUpProbe, z, out var zDown))
                        {
                            float rise = zUp - z, drop = z - zDown;
                            if (rise > 0.03f && rise <= p.StepHeight + 0.01f && drop > 0.03f && drop <= p.StepHeight + 0.01f && Mathf.Min(rise, drop) > best)
                            {
                                best = Mathf.Min(rise, drop);
                                bestYaw = toward;
                            }
                        }
                    }
                    for (var d = 0; d < 8; ++d) Try(d * 45f);
                    if (best <= 0f) continue;
                    var coarse = bestYaw;
                    Try(coarse - 22.5f);
                    Try(coarse + 22.5f);
                    var up = KitSpace.DirOf(bestYaw);
                    var foot = March(tread, z, -up, -1f);
                    var top = March(tread, z, up, 1f);
                    if (Mathf.Abs(foot.z - p.Feet) > p.StepHeight + 0.05f || top.z - foot.z < WayUpRise) continue;
                    way = new WayUp
                    {
                        Foot = foot, TopAt = top, Rise = top.z - foot.z, FootDistance = KitSpace.Dist2D(foot, p.Me), TopDistance = KitSpace.Dist2D(top, p.Me),
                        Degrees = KitSpace.YawOf2D(p.Me, foot), Side = true,
                        Reach = !p.Sweep(new Vector3(p.Me.x, p.Me.y, p.Feet), foot, out _),
                    };
                    return true;
                }
                return false;
            }
            for (var off = 0f; off <= fov + 0.01f; off += WayUpEvery)
            {
                var degrees = left + off;
                var dir = KitSpace.DirOf(degrees);
                var nearest = Mathf.Clamp(Mathf.RoundToInt(off / OpeningRayDegrees), 0, n - 1);
                var flat = rays[nearest].Open;
                float z = p.Feet, footZ = p.Feet, footDistance = -1f;
                var last = new Vector3(p.Me.x, p.Me.y, p.Feet);
                var foot = last;
                float rise = 0f, topDistance = 0f;
                var topAt = last;
                var level = 0;
                for (var o = WayUpPace; o <= Mathf.Min(WayUpReach, reach); o += WayUpPace)
                {
                    var next = new Vector3(p.Me.x, p.Me.y, 0) + dir * o;
                    var clear = z <= p.Feet + 0.05f && z >= p.Feet - 0.05f && o <= flat - p.Radius;
                    if (!clear && (p.Line(new Vector3(last.x, last.y, z + knee), new Vector3(next.x, next.y, z + knee), out _)
                                   || p.Line(new Vector3(last.x, last.y, z + chest), new Vector3(next.x, next.y, z + chest), out _)))
                        break;
                    if (!p.Floor(new Vector3(next.x, next.y, z + p.StepHeight + 0.05f), p.StepHeight * 2f + 0.65f, out var floorZ, out var walkable)
                        || !walkable || floorZ > z + p.StepHeight + 0.01f || floorZ < z - p.StepHeight - 0.01f)
                        break;
                    if (floorZ > z + 0.03f)
                    {
                        if (footDistance < 0)
                        {
                            footDistance = o - WayUpPace;
                            foot = new Vector3(last.x, last.y, z);
                            footZ = z;
                        }
                        level = 0;
                    }
                    else if (footDistance >= 0 && ++level >= 2 && z - footZ >= WayUpRise) break;
                    else if (footDistance >= 0 && level >= 3 && z - footZ < WayUpRise)
                    {
                        footDistance = -1f;
                        level = 0;
                    }
                    z = floorZ;
                    last = new Vector3(next.x, next.y, z);
                    if (footDistance >= 0 && z - footZ > rise)
                    {
                        rise = z - footZ;
                        topDistance = o;
                        topAt = last;
                    }
                }
                if (footDistance < 0 || rise < WayUpRise)
                {
                    if (SideWayUp(degrees, rays[nearest].Chest, out var side)) found.Add(side);
                    continue;
                }
                var footSeen = foot + new Vector3(0, 0, 0.3f);
                if (p.Sight(eye.At, footSeen, out var block) && block.Distance < (footSeen - eye.At).magnitude - 0.3f) continue;
                found.Add(new WayUp { Foot = foot, TopAt = topAt, Rise = rise, FootDistance = footDistance, TopDistance = topDistance, Degrees = degrees });
            }
            found.Sort((a, b) => a.FootDistance.CompareTo(b.FootDistance));
            var kept = new List<WayUp>();
            foreach (var way in found)
            {
                var same = kept.FindIndex(k => KitSpace.Dist2D(k.Foot, way.Foot) <= WayUpFootApart);
                if (same < 0)
                {
                    if (kept.Count < MaxWaysUp) kept.Add(way);
                }
                else if (way.Rise > kept[same].Rise) kept[same] = way;
            }
            foreach (var way in kept) Lanes(p, eye, way, knee, FloorAt, March);
            var ways = new List<object>();
            foreach (var way in kept)
            {
                var o = new Dictionary<string, object>
                {
                    ["turn"] = Round(Frame.Wrap(way.Degrees - yaw)),
                    ["distance_to_foot"] = Cm(way.FootDistance),
                    ["rises_by"] = Cm(way.Rise),
                    ["top_distance"] = Cm(way.TopDistance),
                    ["foot"] = KitSpace.Vec(way.Foot),
                    ["top"] = KitSpace.Vec(way.TopAt),
                };
                if (way.Side)
                {
                    o["seen_from"] = "side";
                    o["reachable_straight"] = way.Reach;
                }
                if (way.Lane)
                {
                    o["lane"] = new Dictionary<string, object>
                    {
                        ["width"] = Cm(way.Width),
                        ["right"] = SideOf(way.RightBy, way.RightDepth),
                        ["left"] = SideOf(way.LeftBy, way.LeftDepth),
                        ["below"] = new Dictionary<string, object>
                        {
                            ["clear"] = Cm(way.Clear),
                            ["by"] = way.BelowBy == 3 ? "unseen" : way.BelowBy == 2 ? "drop" : way.BelowBy == 1 ? "wall" : "reach",
                        },
                    };
                }
                var head = way.Foot + new Vector3(0, 0, height);
                if (p.Sight(head, head + new Vector3(0, 0, 20f), out var over) && over.Thing != null) o["inside"] = Paths.NameOf(over.Thing);
                ways.Add(o);
            }
            return ways;
        }

        static Dictionary<string, object> SideOf(int kind, float deep)
        {
            var told = new Dictionary<string, object> { ["by"] = kind == 2 ? "drop" : kind == 1 ? "wall" : "open" };
            if (kind == 2) told["depth"] = Cm(deep);
            return told;
        }

        delegate bool FloorAtFn(Vector3 at, float fromZ, out float z);

        static void Lanes(BodyProbe p, Eye eye, WayUp way, float knee, FloorAtFn floorAt, Func<Vector3, float, Vector3, float, Vector3> march)
        {
            bool SeenFloor(Vector3 at)
            {
                var over = at + new Vector3(0, 0, 0.05f);
                return !p.Sight(eye.At, over, out var block) || block.Distance >= (over - eye.At).magnitude - 0.2f;
            }
            float Across(Vector3 at, float fromZ, Vector3 dir, float most, out int by, out float dropDepth, bool sight)
            {
                float z = fromZ, good = 0f;
                by = 0;
                dropDepth = 0f;
                bool Try(float o, out float newZ, out int why, out float fall)
                {
                    newZ = z;
                    why = 0;
                    fall = 0;
                    var from2 = new Vector3(at.x, at.y, 0) + dir * good;
                    var to2 = new Vector3(at.x, at.y, 0) + dir * o;
                    if (p.Line(new Vector3(from2.x, from2.y, z + knee), new Vector3(to2.x, to2.y, z + knee), out _))
                    {
                        why = 1;
                        return false;
                    }
                    if (p.Floor(new Vector3(to2.x, to2.y, z + p.StepHeight + 0.05f), p.StepHeight * 2f + 0.1f, out var f, out var walk) && f >= z - p.StepHeight - 0.01f)
                    {
                        if (f > z + p.StepHeight + 0.01f || !walk)
                        {
                            why = 1;
                            return false;
                        }
                        if (sight && !SeenFloor(new Vector3(to2.x, to2.y, f)))
                        {
                            why = 3;
                            return false;
                        }
                        newZ = f;
                        return true;
                    }
                    why = sight && !SeenFloor(new Vector3(to2.x, to2.y, z)) ? 3 : 2;
                    fall = p.Floor(new Vector3(to2.x, to2.y, z), WayUpLaneDeep, out var low, out _) ? z - low : WayUpLaneDeep;
                    return false;
                }
                for (var o = WayUpLaneStep; o <= most + 0.001f; o += WayUpLaneStep)
                {
                    if (!Try(o, out var newZ, out var why, out var fall))
                    {
                        var hi = o;
                        for (var h = 0; h < WayUpLaneHalvings; ++h)
                        {
                            var mid = (good + hi) * 0.5f;
                            if (Try(mid, out var midZ, out _, out _))
                            {
                                good = mid;
                                z = midZ;
                            }
                            else hi = mid;
                        }
                        by = why;
                        dropDepth = fall;
                        return good;
                    }
                    good = o;
                    z = newZ;
                }
                return most;
            }
            var rough = KitSpace.SafeNormal(KitSpace.Flat(way.TopAt - way.Foot));
            if (rough == Vector3.zero) return;
            var side = new Vector3(-rough.y, rough.x, 0);
            var middles = new Vector3[2];
            var zs = new float[2];
            var chords = new float[2];
            var bys = new int[2, 2];
            var depths = new float[2, 2];
            for (var k = 0; k < 2; ++k)
            {
                var t = WayUpLaneStations[k];
                var at = Vector3.Lerp(way.Foot, way.TopAt, t);
                if (!floorAt(at, Mathf.Lerp(way.Foot.z, way.TopAt.z, t), out var z)) return;
                var outward = Across(at, z, side, WayUpLaneReach, out bys[k, 0], out depths[k, 0], false);
                var back = Across(at, z, -side, WayUpLaneReach, out bys[k, 1], out depths[k, 1], false);
                middles[k] = new Vector3(at.x, at.y, 0) + side * ((outward - back) * 0.5f);
                chords[k] = outward + back;
                zs[k] = z;
            }
            var up2 = KitSpace.Flat(middles[1] - middles[0]);
            up2 = up2.magnitude < p.Radius ? rough : up2.normalized;
            var square = Mathf.Abs(side.x * up2.y - side.y * up2.x);
            var foot = march(new Vector3(middles[0].x, middles[0].y, zs[0]), zs[0], -up2, -1f);
            var topAt = march(new Vector3(middles[1].x, middles[1].y, zs[1]), zs[1], up2, 1f);
            if (Mathf.Abs(foot.z - way.Foot.z) > p.StepHeight + 0.05f || topAt.z - foot.z < WayUpRise) return;
            way.Foot = foot;
            way.TopAt = topAt;
            way.Rise = topAt.z - foot.z;
            way.FootDistance = KitSpace.Dist2D(foot, p.Me);
            way.TopDistance = KitSpace.Dist2D(topAt, p.Me);
            way.Degrees = KitSpace.YawOf2D(p.Me, foot);
            way.Lane = true;
            way.Width = Mathf.Min(chords[0], chords[1]) * square;
            var sideRight = side.x * -up2.y + side.y * up2.x >= 0;
            for (var j = 0; j < 2; ++j)
            {
                var worst = 0;
                var deepest = 0f;
                for (var k = 0; k < 2; ++k)
                {
                    if ((bys[k, j] == 2 && (worst != 2 || depths[k, j] > deepest)) || (bys[k, j] == 1 && worst == 0))
                    {
                        worst = bys[k, j];
                        deepest = depths[k, j];
                    }
                }
                var right = (j == 0) == sideRight;
                if (right)
                {
                    way.RightBy = worst;
                    way.RightDepth = deepest;
                }
                else
                {
                    way.LeftBy = worst;
                    way.LeftDepth = deepest;
                }
            }
            way.Clear = Across(foot, foot.z, -up2, WayUpLaneBelow, out var belowBy, out _, true);
            way.BelowBy = belowBy;
        }

        // -- sonar's point blank (Unreal's AddSonar) -----------------------------------------------------------------

        const float SonarPointBlankWidths = 1.5f;

        /// <summary>Point blank ahead: how far past the body's side a line at its knees and one at its chest meet something
        /// that isn't floor it walks up (within 1.5 body widths); null where nothing is.</summary>
        public static Dictionary<string, object> PointBlank(BodyProbe p, double yaw)
        {
            var height = p.HalfHeight * 2f;
            var knee = Mathf.Max(p.StepHeight + 0.05f, height * 0.28f);
            var chest = Mathf.Max(knee + 0.1f, height * 0.62f);
            var ahead = new Dictionary<string, object>();
            var way = KitSpace.DirOf(yaw);
            var near = p.Radius * (1f + 2f * SonarPointBlankWidths);
            var nearest = float.MaxValue;
            GameObject met = null;
            var metNormal = Vector3.zero;
            foreach (var (key, above) in new[] { ("knee", knee), ("chest", chest) })
            {
                var from = new Vector3(p.Me.x, p.Me.y, p.Feet + above);
                if (p.Line(from, from + way * near, out var hit) && hit.Normal.z < p.WalkableZ)
                {
                    ahead[key] = Cm(Mathf.Max(0f, hit.Distance - p.Radius));
                    if (hit.Distance < nearest)
                    {
                        nearest = hit.Distance;
                        met = hit.Thing;
                        metNormal = KitSpace.Flat(hit.Normal);
                    }
                }
                else ahead[key] = null;
            }
            if (met != null) ahead["thing"] = Paths.NameOf(met);
            if (metNormal.sqrMagnitude > 1e-6f)
            {
                var flat = metNormal.normalized;
                ahead["normal"] = new Dictionary<string, object> { ["X"] = Json.Round(flat.x, 2), ["Y"] = Json.Round(flat.y, 2) };
            }
            ahead["within"] = Cm(near - p.Radius);
            return ahead;
        }

        // -- a thing's top surface (Unreal's Surface in PlaytestOpsView.cpp) -------------------------------------------

        const float SlopeMin = 5f, SlopeMax = 50f, SlopeEndInset = 0.15f;
        const float SlopeEndClear = 0.02f, SlopeEndProbe = 0.05f, SlopeEndStride = 0.2f;
        const int SlopeEndStrides = 128, SlopeEndHalvings = 5;
        static readonly float[] SlopeLaneStations = { 0.25f, 0.5f, 0.75f };
        const float SlopeLaneStep = 0.2f, SlopeLaneDeep = 30f, SlopeLaneBelow = 10f;
        const int SlopeLaneSamples = 64, SlopeLaneHalvings = 3;

        /// <summary>Whether something is a character (or held or worn by one): a CharacterController, a NavMeshAgent or
        /// an Animator with a Rigidbody up its parents.</summary>
        static bool OfCharacter(Transform t)
        {
            for (var depth = 0; t != null && depth < 16; t = t.parent, ++depth)
            {
                if (t.GetComponent<CharacterController>() != null) return true;
                foreach (var c in t.GetComponents<Component>())
                    if (c != null && c.GetType().Name == "NavMeshAgent")
                        return true;
            }
            return false;
        }

        /// <summary>Whether a thing has a visible body (a renderer drawn): what a player sees a slope meet.</summary>
        static bool Shows(Collider c, Dictionary<GameObject, bool> known)
        {
            var thing = View.ThingOf(c.gameObject);
            if (known.TryGetValue(thing, out var shows)) return shows;
            shows = thing.GetComponentsInChildren<Renderer>().Any(r => r.enabled && !Ops.IsParticles(r));
            known[thing] = shows;
            return shows;
        }

        /// <summary>The item's top_slope_degrees, and for a slope a body could walk up its low_end, high_end, lane and
        /// uphill (each end only where a line of sight from the camera reaches it), as Unreal's Surface.</summary>
        public static void Surface(Dictionary<string, object> item, GameObject thing, OwnBox b, Bounds world, BodyProbe p, Eye eye, Func<Vector3, Dictionary<string, object>> place)
        {
            var k = b.UpIndex();
            var top = b.Center + b.TopNormal() * b.HalfAlong(k);
            var above = world.max.y + 0.5f;
            var below = world.min.y - 0.5f;
            var shapes = thing.GetComponentsInChildren<Collider>().Where(c => c.enabled && !c.isTrigger).ToArray();
            float HeightAt(Vector3 at)
            {
                var height = at.z;
                var hit = false;
                var ray = new UnityEngine.Ray(KitSpace.U(new Vector3(at.x, at.y, above)), Vector3.down);
                foreach (var shape in shapes)
                {
                    if (shape.Raycast(ray, out var h, above - below) && (!hit || h.point.y > height))
                    {
                        height = h.point.y;
                        hit = true;
                    }
                }
                return height;
            }
            var found = false;
            var steepest = 0f;
            var along = 0;
            var lowEnd = top;
            var highEnd = top;
            for (var i = 0; i < 3; ++i)
            {
                if (i == k) continue;
                var half = b.HalfAlong(i);
                var reachI = Mathf.Max(0f, half - Mathf.Min(SlopeEndInset, half * 0.25f));
                var one = top + b.Axis[i] * reachI;
                var other = top - b.Axis[i] * reachI;
                one.z = HeightAt(one);
                other.z = HeightAt(other);
                var run = KitSpace.Dist2D(one, other);
                if (run < 0.01f) continue;
                var slope = Mathf.Atan2(Mathf.Abs(one.z - other.z), run) * Mathf.Rad2Deg;
                if (!found || slope > steepest)
                {
                    found = true;
                    steepest = slope;
                    along = i;
                    lowEnd = one.z <= other.z ? one : other;
                    highEnd = one.z <= other.z ? other : one;
                }
            }
            var euler = thing.transform.rotation.eulerAngles;
            var tipped = Mathf.Abs(Mathf.DeltaAngle(0, euler.x)) >= 1f || Mathf.Abs(Mathf.DeltaAngle(0, euler.z)) >= 1f;
            if (!found || (steepest < 1f && !tipped)) return;
            item["top_slope_degrees"] = Math.Round(steepest);
            if (steepest < SlopeMin || steepest > SlopeMax || highEnd.z - lowEnd.z < 0.1f) return;
            var across = 3 - k - along;
            var bodyHeight = p != null ? p.HalfHeight * 2f : 1.8f;
            var room = Mathf.Max(0.01f, (bodyHeight - SlopeEndClear) * 0.5f);
            var mask = p != null ? p.Mask : Physics.DefaultRaycastLayers;
            var known = new Dictionary<GameObject, bool>();
            bool Open(Vector3 at)
            {
                var center = KitSpace.U(at + new Vector3(0, 0, SlopeEndClear + room));
                var halfExtents = new Vector3(SlopeEndProbe, room, SlopeEndProbe);  // (Unity's axes: y up)
                foreach (var c in Physics.OverlapBox(center, halfExtents, Quaternion.identity, mask, QueryTriggerInteraction.Ignore))
                {
                    if (c == null || c.transform.IsChildOf(thing.transform)) continue;
                    if (p != null && (c.transform.IsChildOf(p.Player.transform) || p.Ignored.Any(t => t != null && c.transform.IsChildOf(t)))) continue;
                    if (OfCharacter(c.transform)) continue;
                    if (Shows(c, known)) return false;
                }
                return true;
            }
            bool SeesPoint(Vector3 at)
            {
                var over = at + new Vector3(0, 0, 0.05f);
                if (!eye.OnScreen(over, out var sx, out var sy) || Mathf.Abs(sx) > 1f || Mathf.Abs(sy) > 1f) return false;
                if (p == null) return true;
                return !p.Sight(eye.At, over, out var hit) || hit.Distance >= (over - eye.At).magnitude - 0.1f;
            }
            bool FirstOpen(Vector3 from, Vector3 to, out Vector3 result)
            {
                Vector3 At(float t)
                {
                    var q = Vector3.Lerp(from, to, t);
                    q.z = HeightAt(q);
                    return q;
                }
                var strides = Mathf.Clamp(Mathf.CeilToInt(KitSpace.Dist2D(from, to) / SlopeEndStride), 1, SlopeEndStrides);
                var covered = 0f;
                for (var i = 0; i <= strides; ++i)
                {
                    var t = (float)i / strides;
                    if (!Open(At(t)))
                    {
                        covered = t;
                        continue;
                    }
                    var hi = t;
                    for (var h = 0; i > 0 && h < SlopeEndHalvings; ++h)
                    {
                        var mid = (covered + hi) * 0.5f;
                        if (Open(At(mid))) hi = mid;
                        else covered = mid;
                    }
                    result = At(hi);
                    return true;
                }
                result = from;
                return false;
            }
            bool EndAt(Vector3 edge, Vector3 other, out Vector3 result)
            {
                foreach (var share in new[] { 0f, 1f / 3f, -1f / 3f, 2f / 3f, -2f / 3f })
                {
                    var sideOff = b.Axis[across] * (b.HalfAlong(across) * share);
                    if (FirstOpen(edge + sideOff, other + sideOff, out var q) && SeesPoint(q))
                    {
                        result = q;
                        return true;
                    }
                }
                result = top;
                return false;
            }
            if (EndAt(lowEnd, highEnd, out var end)) item["low_end"] = place(end);
            if (EndAt(highEnd, lowEnd, out end)) item["high_end"] = place(end);

            // The lane a body walks up it (see Unreal's Surface: the same rules)
            {
                var stepUp = p != null ? p.StepHeight : bodyHeight * 0.25f;
                var bodyRadius = p != null ? p.Radius : bodyHeight * 0.2f;
                var up2 = KitSpace.SafeNormal(KitSpace.Flat(highEnd - lowEnd));
                var right2 = new Vector3(-up2.y, up2.x, 0);
                var rightSign = Vector3.Dot(b.Axis[across], right2) >= 0 ? 1f : -1f;
                var halfAcross = b.HalfAlong(across);
                var edge = Mathf.Max(0f, halfAcross - Mathf.Min(SlopeEndInset, halfAcross * 0.25f));
                var samples = Mathf.Clamp(Mathf.CeilToInt(2f * edge / SlopeLaneStep), 2, SlopeLaneSamples);
                int sideBy0 = 0, sideBy1 = 0;
                float sideDepth0 = 0, sideDepth1 = 0;
                void Worse(ref int keptBy, ref float keptDepth, int hereBy, float hereDepth)
                {
                    if ((hereBy == 2 && (keptBy != 2 || hereDepth > keptDepth)) || (hereBy == 1 && keptBy == 0))
                    {
                        keptBy = hereBy;
                        keptDepth = hereDepth;
                    }
                }
                void Past(Vector3 middle, float q, float sign, out int by, out float depth)
                {
                    by = 0;
                    depth = 0;
                    var at = middle + b.Axis[across] * q;
                    var z = HeightAt(at);
                    at = middle + b.Axis[across] * (sign * (halfAcross + bodyRadius));
                    at.z = z;
                    if (!Open(at))
                    {
                        by = 1;
                        return;
                    }
                    var start = new Vector3(at.x, at.y, z + stepUp);
                    KHit floorHit = default;
                    var hitFloor = p != null ? p.Line(start, start - new Vector3(0, 0, stepUp + SlopeLaneDeep), out floorHit) : false;
                    if (hitFloor)
                    {
                        var fall = z - floorHit.Point.z;
                        if (fall > stepUp)
                        {
                            by = 2;
                            depth = fall;
                        }
                    }
                    else
                    {
                        by = 2;
                        depth = SlopeLaneDeep;
                    }
                }
                float narrowest = 0, offsets = 0;
                var runs = 0;
                var seenLane = false;
                foreach (var t in SlopeLaneStations)
                {
                    var middle = Vector3.Lerp(lowEnd, highEnd, t);
                    Vector3 AtQ(float q)
                    {
                        var at = middle + b.Axis[across] * q;
                        at.z = HeightAt(at);
                        return at;
                    }
                    float QOf(int i) => -edge + 2f * edge * i / samples;
                    var free = new bool[samples + 1];
                    for (var i = 0; i <= samples; ++i) free[i] = Open(AtQ(QOf(i)));
                    int runFrom = -1, runTo = -1;
                    for (var i = 0; i <= samples;)
                    {
                        if (!free[i])
                        {
                            ++i;
                            continue;
                        }
                        var j = i;
                        while (j < samples && free[j + 1]) ++j;
                        if (runFrom < 0 || j - i > runTo - runFrom)
                        {
                            runFrom = i;
                            runTo = j;
                        }
                        i = j + 1;
                    }
                    if (runFrom < 0) continue;
                    float Halve(float openQ, float coveredQ)
                    {
                        for (var h = 0; h < SlopeLaneHalvings; ++h)
                        {
                            var mid = (openQ + coveredQ) * 0.5f;
                            if (Open(AtQ(mid))) openQ = mid;
                            else coveredQ = mid;
                        }
                        return openQ;
                    }
                    var lo = runFrom > 0 ? Halve(QOf(runFrom), QOf(runFrom - 1)) : -edge;
                    var hi = runTo < samples ? Halve(QOf(runTo), QOf(runTo + 1)) : edge;
                    narrowest = runs == 0 ? hi - lo : Mathf.Min(narrowest, hi - lo);
                    offsets += (lo + hi) * 0.5f;
                    ++runs;
                    int by;
                    float depth;
                    if (runTo < samples)
                    {
                        by = 1;
                        depth = 0;
                    }
                    else Past(middle, edge, 1f, out by, out depth);
                    Worse(ref sideBy0, ref sideDepth0, by, depth);
                    if (runFrom > 0)
                    {
                        by = 1;
                        depth = 0;
                    }
                    else Past(middle, -edge, -1f, out by, out depth);
                    Worse(ref sideBy1, ref sideDepth1, by, depth);
                    seenLane = seenLane || SeesPoint(AtQ((lo + hi) * 0.5f));
                }
                if (runs > 0 && seenLane)
                {
                    var shift = b.Axis[across] * (offsets / runs);
                    var lane = new Dictionary<string, object> { ["width"] = Cm(narrowest) };
                    var halfway = Vector3.Lerp(lowEnd, highEnd, 0.5f) + shift;
                    halfway.z = HeightAt(halfway);
                    lane["middle"] = KitSpace.Vec(halfway);
                    var hasFoot = FirstOpen(lowEnd + shift, highEnd + shift, out var footP) && SeesPoint(footP);
                    if (hasFoot) lane["foot"] = place(footP);
                    if (FirstOpen(highEnd + shift, lowEnd + shift, out var topP) && SeesPoint(topP)) lane["top"] = place(topP);
                    lane["right"] = SideOf(rightSign > 0 ? sideBy0 : sideBy1, rightSign > 0 ? sideDepth0 : sideDepth1);
                    lane["left"] = SideOf(rightSign > 0 ? sideBy1 : sideBy0, rightSign > 0 ? sideDepth1 : sideDepth0);
                    if (hasFoot && p != null)
                    {
                        var z = footP.z;
                        var clear = SlopeLaneBelow;
                        var by = "reach";
                        for (var d = SlopeLaneStep; d <= SlopeLaneBelow + 0.001f; d += SlopeLaneStep)
                        {
                            var at = footP - up2 * d;
                            var start = new Vector3(at.x, at.y, z + stepUp);
                            if (!p.Line(start, start - new Vector3(0, 0, 2f * stepUp + 0.01f), out var floorHit))
                            {
                                at.z = z;
                                clear = d - SlopeLaneStep;
                                by = !Open(at) ? "wall" : SeesPoint(at) ? "drop" : "unseen";
                                break;
                            }
                            at.z = floorHit.Point.z;
                            if (!Open(at))
                            {
                                clear = d - SlopeLaneStep;
                                by = "wall";
                                break;
                            }
                            if (!SeesPoint(at))
                            {
                                clear = d - SlopeLaneStep;
                                by = "unseen";
                                break;
                            }
                            z = at.z;
                        }
                        lane["below"] = new Dictionary<string, object> { ["clear"] = Cm(clear), ["by"] = by };
                    }
                    item["lane"] = lane;
                }
            }
            var yawUp = KitSpace.YawOf2D(lowEnd, highEnd);
            item["uphill"] = new Dictionary<string, object> { ["yaw"] = Round(yawUp), ["turn"] = Round(Frame.Wrap(yawUp - eye.Yaw)) };
        }
    }
#endif

    /// <summary>How things are drawn besides their meshes (Unreal's PlaytestOpsLook.cpp): lights, effects, highlights;
    /// the sun; and what the player holds.</summary>
    public static class Drawn
    {
        /// <summary>How bright a light shines on the world: 0 when it lights nothing (off, or no intensity).</summary>
        public static float Brightness(Light light) =>
            light != null && light.isActiveAndEnabled && light.intensity > 0f ? light.intensity : 0f;

        /// <summary>scene.sun: the brightest directional light that lights the world, or -1 with none.</summary>
        public static float Sun()
        {
            var sun = -1f;
            foreach (var l in Loaded.All<Light>(FindObjectsInactive.Exclude))
                if (l != null && l.type == LightType.Directional && l.isActiveAndEnabled)
                    sun = Mathf.Max(sun, l.intensity);
            return sun;
        }

        /// <summary>A light drawn by a helper: a lit light (not a directional one: no place) on an object with no
        /// visible body of its own up its parents, not the player's, not moving: seen as the light of the thing it is
        /// at.</summary>
        public static List<KeyValuePair<Vector3, float>> PlacedLights(GameObject player)
        {
            var list = new List<KeyValuePair<Vector3, float>>();
            foreach (var l in Loaded.All<Light>(FindObjectsInactive.Exclude))
            {
                if (l == null || l.type == LightType.Directional || Paths.Hidden(l.gameObject)) continue;
                var brightness = Brightness(l);
                if (brightness <= 0f) continue;
                if (player != null && l.transform.IsChildOf(player.transform)) continue;
                var helper = true;
                for (var t = l.transform; t != null && helper; t = t.parent)
                    if (t.GetComponents<Renderer>().Any(r => r.enabled && !Ops.IsParticles(r)))
                        helper = false;
#if AUTOPILOT_PHYSICS
                var rb = l.GetComponentInParent<Rigidbody>();
                if (rb != null && !rb.isKinematic && Player.LinearVelocity(rb).magnitude >= 0.1f) helper = false;
#endif
                if (helper) list.Add(new KeyValuePair<Vector3, float>(KitSpace.K(l.transform.position), brightness));
            }
            return list;
        }

        /// <summary>A thing's own lights: on, and the brightest (its own and its children's, lit).</summary>
        public static void OwnLights(GameObject thing, ref int on, ref float brightest)
        {
            foreach (var l in thing.GetComponentsInChildren<Light>())
            {
                var b = Brightness(l);
                if (b <= 0f) continue;
                ++on;
                brightest = Mathf.Max(brightest, b);
            }
        }

        static readonly Type VisualEffectType = Type.GetType("UnityEngine.VFX.VisualEffect, UnityEngine.VFXModule");

        /// <summary>The effects playing on a thing (particle systems emitting or with particles alive, VFX Graph effects
        /// with particles): the effect's name (its object's, as the prefab it was made from is named) and its scale, the
        /// largest of several, sorted by name; at most 6, as Unreal's.</summary>
        public static List<object> Effects(GameObject thing)
        {
            var found = new SortedDictionary<string, double>(StringComparer.Ordinal);
            void Add(Component c, string asset)
            {
                if (string.IsNullOrEmpty(asset)) return;
                var s = c.transform.lossyScale;
                var scale = Math.Round(Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z))), 2);
                if (!found.TryGetValue(asset, out var had) || scale > had) found[asset] = scale;
            }
#if AUTOPILOT_PARTICLES
            foreach (var ps in thing.GetComponentsInChildren<ParticleSystem>())
            {
                if (ps == null || !ps.gameObject.activeInHierarchy || !(ps.isEmitting || ps.particleCount > 0)) continue;
                var r = ps.GetComponent<ParticleSystemRenderer>();
                if (r != null && !r.enabled) continue;
                Add(ps, EffectName(ps.gameObject.name));
            }
#endif
            if (VisualEffectType != null)
            {
                foreach (var c in thing.GetComponentsInChildren(VisualEffectType))
                {
                    try
                    {
                        if (!(c is Behaviour b) || !b.isActiveAndEnabled) continue;
                        var alive = VisualEffectType.GetProperty("aliveParticleCount")?.GetValue(c);
                        if (alive is int count && count <= 0) continue;
                        var asset = VisualEffectType.GetProperty("visualEffectAsset")?.GetValue(c) as UnityEngine.Object;
                        Add(c, asset != null ? asset.name : EffectName(c.gameObject.name));
                    }
                    catch (Exception)
                    {
                        // (this Unity's VisualEffect differs: left out)
                    }
                }
            }
            var list = new List<object>();
            foreach (var e in found)
            {
                if (list.Count >= 6) break;
                list.Add(new Dictionary<string, object> { ["asset"] = e.Key, ["scale"] = e.Value });
            }
            return list;
        }

        static string EffectName(string name)
        {
            name = name ?? "";
            var clone = name.IndexOf("(Clone)", StringComparison.Ordinal);
            return (clone >= 0 ? name.Substring(0, clone) : name).Trim();
        }

        /// <summary>Whether a part is drawn highlighted: Unreal's outline (custom depth) or overlay material, as Unity games
        /// draw them: more materials on a mesh than it has submeshes (the extra ones draw over all of it: an outline or
        /// an overlay, as outline packages add), or a material or shader named as an outline or a highlight.</summary>
        public static bool Highlighted(Renderer r)
        {
            if (r == null || !r.enabled) return false;
            var materials = r.sharedMaterials;
            Mesh mesh = null;
            var mf = r.GetComponent<MeshFilter>();
            if (mf != null) mesh = mf.sharedMesh;
            else if (r is SkinnedMeshRenderer sk) mesh = sk.sharedMesh;
            if (mesh != null && materials.Length > mesh.subMeshCount && materials.Skip(mesh.subMeshCount).Any(m => m != null)) return true;
            foreach (var m in materials)
            {
                if (m == null) continue;
                if (Named(m.name) || (m.shader != null && Named(m.shader.name))) return true;
            }
            return false;
        }

        static bool Named(string name) =>
            name.IndexOf("outline", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("highlight", StringComparison.OrdinalIgnoreCase) >= 0;

        // -- held ------------------------------------------------------------------------------------------------

        /// <summary>The player's own transforms a held thing hangs from: its root, every camera and what holds one (a
        /// first-person camera's pivot), and the bones of its skinned meshes (a hand).</summary>
        static HashSet<Transform> Stops(GameObject player, Camera cam)
        {
            var stops = new HashSet<Transform> { player.transform };
            foreach (var c in player.GetComponentsInChildren<Camera>())
                for (var t = c.transform; t != null && t != player.transform; t = t.parent)
                    stops.Add(t);
            if (cam != null)
                for (var t = cam.transform; t != null; t = t.parent)
                    stops.Add(t);
            foreach (var sk in player.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (sk.bones == null) continue;
                foreach (var bone in sk.bones)
                    if (bone != null)
                        stops.Add(bone);
            }
            return stops;
        }

        /// <summary>`held`: what the player holds, as its own camera shows it: the things hanging from the player (from
        /// its body, a bone of its skinned mesh, its camera), or from the camera when the camera isn't the player's,
        /// each as the thing it is (up to the player's part it hangs from), with the meshes of it the camera draws (its
        /// culling mask: a game drawing a first-person copy for the player's camera alone shows just that). Not the
        /// player's own body: a skinned mesh hung from its root, or a mesh holding the body's middle. Each with its look
        /// (`parts`: the meshes drawn, biggest first), where it hangs (`attached_at`: the part's name), its box's middle
        /// and size, how far the camera is from that box, where its middle is on screen and whether it is in view.
        /// Nearest the camera first (by name among those as near). Also returns the transforms held (no probe stops on
        /// them).</summary>
        public static List<object> Held(GameObject player, Camera cam, out List<Transform> heldRoots)
        {
            heldRoots = new List<Transform>();
            var list = new List<object>();
            if (cam == null) return list;
            var stops = Stops(player ?? cam.gameObject, cam);
            var center = player != null ? Player.Center(player, out _, out _) : cam.transform.position;
            var groups = new Dictionary<Transform, (List<Renderer> parts, Transform at)>();
            var sources = new List<Renderer>();
            if (player != null) sources.AddRange(player.GetComponentsInChildren<Renderer>());
            if (player == null || !cam.transform.IsChildOf(player.transform)) sources.AddRange(cam.GetComponentsInChildren<Renderer>());
            foreach (var r in sources.Distinct())
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || r.forceRenderingOff || Ops.IsParticles(r)) continue;
                if (r is TrailRenderer || r is LineRenderer) continue;
                if ((cam.cullingMask & (1 << r.gameObject.layer)) == 0) continue;  // (not drawn for this camera)
                if (r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly) continue;
                if (stops.Contains(r.transform)) continue;  // (the player's own: its root's, a bone's, the camera's)
                // The thing: up its parents to just below the part it hangs from
                var root = r.transform;
                while (root.parent != null && !stops.Contains(root.parent)) root = root.parent;
                var at = root.parent;
                if (at == null) continue;
                // (the player's own body: a skinned mesh hanging from its root, or a mesh holding its middle)
                if (player != null && at == player.transform && (r is SkinnedMeshRenderer || r.bounds.Contains(center))) continue;
                if (!groups.TryGetValue(root, out var g)) groups[root] = g = (new List<Renderer>(), at);
                g.parts.Add(r);
            }
            var camPos = cam.transform.position;
            var sorted = new List<(float d, string name, Dictionary<string, object> item)>();
            foreach (var pair in groups)
            {
                var root = pair.Key;
                var parts = pair.Value.parts;
                var box = parts[0].bounds;
                foreach (var r in parts) box.Encapsulate(r.bounds);
                var sizes = new Dictionary<string, float>();
                foreach (var r in parts)
                {
                    var mf = r.GetComponent<MeshFilter>();
                    var name = mf != null && mf.sharedMesh != null ? mf.sharedMesh.name
                        : r is SkinnedMeshRenderer sk && sk.sharedMesh != null ? sk.sharedMesh.name
                        : r is SpriteRenderer sr && sr.sprite != null ? sr.sprite.name : null;
                    if (string.IsNullOrEmpty(name)) continue;
                    var size = r.bounds.extents.magnitude;
                    if (!sizes.TryGetValue(name, out var had) || size > had) sizes[name] = size;
                }
                var names = sizes.Keys.OrderByDescending(n => sizes[n]).ThenBy(n => n, StringComparer.Ordinal).Take(8).Cast<object>().ToList();
                var distance = Mathf.Sqrt(box.SqrDistance(camPos));
                var item = new Dictionary<string, object>
                {
                    ["name"] = Paths.NameOf(root.gameObject),
                    ["path"] = Paths.Of(root.gameObject),
                    ["class"] = ClassOf(root.gameObject),
                    ["look"] = new Dictionary<string, object> { ["parts"] = names },
                };
                var hangs = pair.Value.at;
                if (player == null || hangs != player.transform) item["attached_at"] = hangs.name;
                item["center"] = Frame.Point(box.center, 0);
                item["size"] = Frame.Point(box.size, 0);
                item["distance"] = Math.Round(distance * Frame.Scale);
                var vp = cam.WorldToViewportPoint(box.center);
                if (vp.z > 0.01f)
                {
                    item["screen_x"] = Json.Round((vp.x - 0.5) * 2, 2);
                    item["screen_y"] = Json.Round((vp.y - 0.5) * 2, 2);
                }
                var inView = false;
                for (var i = -1; i < 8 && !inView; ++i)
                {
                    var q = i < 0 ? box.center : box.center + Vector3.Scale(box.extents, new Vector3((i & 1) != 0 ? 1 : -1, (i & 2) != 0 ? 1 : -1, (i & 4) != 0 ? 1 : -1));
                    var v = cam.WorldToViewportPoint(q);
                    inView = v.z > 0.01f && v.x >= 0 && v.x <= 1 && v.y >= 0 && v.y <= 1;
                }
                item["in_view"] = inView;
                sorted.Add((distance, Paths.NameOf(root.gameObject), item));
                heldRoots.Add(root);
            }
            foreach (var e in sorted.OrderBy(e => e.d).ThenBy(e => e.name, StringComparer.Ordinal)) list.Add(e.item);
            return list;
        }

        static string ClassOf(GameObject thing)
        {
            foreach (var mb in thing.GetComponentsInChildren<MonoBehaviour>())
                if (mb != null && Reflect.IsGameType(mb.GetType()))
                    return mb.GetType().Name;
            var r = thing.GetComponentInChildren<Renderer>();
            return r != null ? r.GetType().Name : "GameObject";
        }
    }
}
