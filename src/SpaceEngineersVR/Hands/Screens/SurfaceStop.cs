using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using HarmonyLib;
using Havok;
using Sandbox;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;
using VRage.Game.Entity;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// The drawn hand against solid things: block hulls, consoles, walls, floors, voxel rock (<see cref="IHandContactSource"/>,
    /// clamped by <see cref="HandStop"/>). A ball at the index fingertip (1 cm) and one at the palm (3 cm) are swept, each from
    /// where it was resolved last frame to this frame's target, so a fast hand cannot pass a thin wall between two frames.
    /// The first thing the sweep meets gives a plane the ball's centre may not go behind; a second sweep along that plane
    /// finds an inside corner (floor and wall). The push out of both is the contact; the palm or the tip, whichever is deeper.
    /// Glass is swept on its own triangles as well (<see cref="GlassPanes"/>): windows and canopies stand outside their
    /// collision, and a cockpit's interior model has none, so the seated hand stops on the canopy though its seat is passed.
    ///
    /// Not stopped by: the character, anything that is not a grid or voxels (tools, floating items, other characters,
    /// safe zones, sensors), projections, the seat's own block and the seat grid's blocks round the seated body, and a
    /// block or voxel the ball was already inside when the sweep began (a door closing on the hand, a teleport), so a hand
    /// is never trapped. With no fresh last frame (first use, either stop switched back on, a pause, a teleport) a ball
    /// starts over where the hand is, unswept, so a hand already through something is not caught; a real hand more than
    /// 30 cm past where its ball is held is let through the same way. Hands holding a tool, the other hand on the tool, a hand holding a floating item or the external
    /// view screen are left alone; an empty hand and a hand on a hull (<see cref="HullGrab"/>) are stopped.
    ///
    /// Game thread (HandIK's postfix on UpdateAfterSimulation, after the physics step), never inside the simulation. Draws
    /// only: nothing here moves the body or the camera. RenderDebug SurfaceStop=0 turns it off, SurfaceStopGlass=0 only the
    /// glass, SurfaceStopTrace=1 logs each touch (depth, normal, what was hit) and each exclusion; the cost is on the Perf line.
    /// </summary>
    internal sealed class SurfaceStop : IHandContactSource
    {
        public static bool Off { get; set; }
        public static bool Trace { get; set; }
        /// <summary>Debug: the hands pass glass again, hulls still stop them (<see cref="Rendering.RenderDebug"/> SurfaceStopGlass=0; for players, <see cref="VRSettings.HandsStopOnGlass"/>).</summary>
        public static bool GlassOff { get; set; }

        private const float TipRadius = 0.01f, PalmRadius = 0.03f, InsideProbe = 0.002f, SeatProbe = 0.05f;
        /// <summary>The second sweep starts this far off the first plane, so it does not meet that plane again.</summary>
        private const double Lift = 0.001;
        /// <summary>A sweep shorter than this is not made (the hand did not move against where it was).</summary>
        private const double Still = 0.0005;
        /// <summary>Farther than this from last frame's point, the ball starts over where it is (a teleport, a respawn).</summary>
        private const double Reset = 1.0;
        /// <summary>
        /// A real hand this far past where its ball was held is let through and starts over where it is (then inside, so passed):
        /// a hand that went round a wall, or came back through one it started beyond, is never kept on the wrong side.
        /// </summary>
        private const double MaxCatch = 0.3;
        /// <summary>Not asked for this long (or <see cref="GapFrames"/>): the hand starts over where it is, nothing stale is caught.</summary>
        private const double GapSeconds = 0.15;
        private const ulong GapFrames = 2, TouchFrames = 30, SeatRefreshFrames = 60;

        private static readonly SurfaceStop instance = new SurfaceStop();
        private static HkShape tipBall, palmBall, insideBall, seatBall;
        private static bool shapesMade;
        private static readonly FeatureGuard guard = new FeatureGuard(nameof(SurfaceStop));

        /// <summary>One ball's history: where it was resolved last, kept in the frame of what it touches or stands on.</summary>
        private sealed class Track
        {
            public bool Valid;
            public MyEntity Ref;
            public Vector3D Local;
            public ulong Frame;
            public long At;
            public MyEntity Touched;
            public ulong TouchedFrame;
            public bool Touching;
            public ulong LoggedFrame;
        }

        // Left tip, left palm, right tip, right palm.
        private static readonly Track[] tracks = { new Track(), new Track(), new Track(), new Track() };

        private static ulong preparedFrame = ulong.MaxValue;
        private static MyCubeBlock seat;
        private static MyEntity support;
        private static readonly HashSet<MySlimBlock> enclosing = new HashSet<MySlimBlock>();
        private static MyCubeBlock enclosingSeat;
        private static ulong enclosingFrame;
        private static ulong eyeFrame = ulong.MaxValue;
        private static Vector3D eye;

        private static readonly List<MyPhysics.HitInfo> hits = new List<MyPhysics.HitInfo>();
        private static readonly List<HkBodyCollision> inside = new List<HkBodyCollision>();
        private static readonly List<MyEntity> supports = new List<MyEntity>();
        private static readonly Vector3D[] onePoint = new Vector3D[1];
        private static Vector3D insideAt;
        private static bool insideValid;

        // Cost, read and cleared by the Perf line (another thread).
        private static long costTicks, costCasts, costProbes, costMaxTicks;
        private static int costFrames;
        private static ulong costFrame = ulong.MaxValue;
        private static long frameTicks;

        private static readonly ulong[] loggedKind = new ulong[4];

        public static void Patch(Harmony harmony)
        {
            HandStop.Register(instance);
        }

        public bool TryContact(Hand hand, Vector3D tip, Vector3D palm, out HandContact contact)
        {
            contact = default;
            if (Off || !guard.On)
                return false;
            MyCharacter character = MySession.Static?.LocalCharacter;
            if (character == null || character.IsDead || character.Closed || character.Physics == null || MySandboxGame.Static == null
                || !MyPhysics.CheckThread() || MyPhysics.InsideSimulation)
                return false;
            int h = hand == Hand.Left ? 0 : 2;
            if (LeftAlone(character, hand))
            {
                tracks[h].Valid = tracks[h + 1].Valid = false;
                tracks[h].Touching = tracks[h + 1].Touching = false;
                return false;
            }

            long start = Stopwatch.GetTimestamp();
            ulong frame = MySandboxGame.Static.SimulationFrameCounter;
            try
            {
                if (!shapesMade)
                {
                    tipBall = new HkSphereShape(TipRadius).Base;
                    palmBall = new HkSphereShape(PalmRadius).Base;
                    insideBall = new HkSphereShape(InsideProbe).Base;
                    seatBall = new HkSphereShape(SeatProbe).Base;
                    shapesMade = true;
                }
                Prepare(character, frame);
                bool t = Resolve(character, hand, "tip", tracks[h], tip, tipBall, TipRadius, frame, out HandContact atTip);
                bool p = Resolve(character, hand, "palm", tracks[h + 1], palm, palmBall, PalmRadius, frame, out HandContact atPalm);
                if (t && (!p || atTip.Depth > atPalm.Depth))
                    contact = atTip;
                else if (p)
                    contact = atPalm;
                return t || p;
            }
            catch (Exception e)
            {
                tracks[h].Valid = tracks[h + 1].Valid = false;
                guard.Failed(e, "Surface stop failed");
                return false;
            }
            finally
            {
                long spent = Stopwatch.GetTimestamp() - start;
                if (frame != costFrame)
                {
                    if (frameTicks > Interlocked.Read(ref costMaxTicks))
                        Interlocked.Exchange(ref costMaxTicks, frameTicks);
                    frameTicks = 0;
                    costFrame = frame;
                    Interlocked.Increment(ref costFrames);
                }
                frameTicks += spent;
                Interlocked.Add(ref costTicks, spent);
            }
        }

        /// <summary>Hands this source does not stop: a held tool, the other hand on it, a held floating item, the view screen.</summary>
        private static bool LeftAlone(MyCharacter character, Hand hand)
        {
            if (hand == VRSettings.DominantHand && character.CurrentWeapon != null && !character.IsSitting)
                return true;
            Matrix any = Matrix.Identity;
            if (TwoHand.Settle(hand, ref any, out _) > 0f)
                return true;
            string owner = GripClaim.Owner(hand);
            return owner != null && owner != HullGrab.Name && owner != GrabUse.ClaimName && owner != HeadGestures.ClaimName;
        }

        /// <summary>Once a frame: the seat and the blocks round the seated body, or what the feet stand on.</summary>
        private static void Prepare(MyCharacter character, ulong frame)
        {
            if (frame == preparedFrame)
                return;
            preparedFrame = frame;
            seat = null;
            support = null;
            if (character.IsSitting)
            {
                seat = character.Parent as MyCubeBlock;
                if (seat == null && MySession.Static.ControlledEntity is MyCockpit cockpit && cockpit.Pilot == character)
                    seat = cockpit;
            }
            if (seat == null)
            {
                enclosing.Clear();
                enclosingSeat = null;
                supports.Clear();
                character.Physics.CharacterProxy?.GetSupportingEntities(supports);
                foreach (MyEntity e in supports)
                {
                    MyEntity top = e.GetTopMostParent();
                    if (top is MyCubeGrid || top is MyVoxelBase)
                    {
                        support = top;
                        break;
                    }
                }
                supports.Clear();
                return;
            }
            if (seat == enclosingSeat && frame - enclosingFrame < SeatRefreshFrames)
                return;
            // The seat grid's blocks the seated body is inside (the seat itself is left out by its block, below).
            enclosing.Clear();
            enclosingSeat = seat;
            enclosingFrame = frame;
            Quaternion none = Quaternion.Identity;
            Vector3D chest = character.PositionComp.WorldAABB.Center;
            for (int i = 0; i < 2; i++)
            {
                Vector3D at = i == 0 ? Eye(character, frame) : chest;
                inside.Clear();
                MyPhysics.GetPenetrationsShape(seatBall, ref at, ref none, inside, MyPhysics.CollisionLayers.CharacterCollisionLayer);
                Interlocked.Increment(ref costProbes);
                foreach (HkBodyCollision c in inside)
                {
                    if (c.Body?.UserObject is MyPhysicsBody body && body.Entity is MyCubeGrid grid && grid == seat.CubeGrid)
                    {
                        MySlimBlock block = BlockOf(grid, c.ShapeKey);
                        if (block != null && block != seat.SlimBlock)
                            enclosing.Add(block);
                    }
                }
            }
            inside.Clear();
            if (Trace && enclosing.Count > 0)
                Log.Info($"Surface stop: seated in {Describe(seat)}; {enclosing.Count} block(s) round the body are passed through");
        }

        /// <summary>The eyes in the world (the seated body's blocks are looked for round them).</summary>
        private static Vector3D Eye(MyCharacter character, ulong frame)
        {
            if (frame == eyeFrame)
                return eye;
            eyeFrame = frame;
            if (HandWorld.TryGetTrackingToBody(character, out MatrixD trackingToModel, out _))
            {
                Vector3 head = GameHead.Take() ? GameHead.TrackingPose.Translation : Vector3.Zero;
                eye = Vector3D.Transform(Vector3D.Transform((Vector3D)head, trackingToModel), character.PositionComp.WorldMatrixRef);
            }
            else
                eye = character.GetHeadMatrix(false).Translation;
            return eye;
        }

        private static bool Resolve(MyCharacter character, Hand hand, string part, Track track, Vector3D target, HkShape ball, float radius, ulong frame, out HandContact contact)
        {
            contact = default;
            long now = Stopwatch.GetTimestamp();
            // No last frame, or a stale one (the hand stop or this source off, the hand away or left alone, a pause, a
            // teleport): the ball starts over where the hand is now and nothing is swept, so a hand already through a
            // surface is not caught; next frame it sweeps from inside that surface, which is passed.
            Vector3D from = target;
            bool fresh = track.Valid && frame - track.Frame <= GapFrames && now - track.At <= GapSeconds * Stopwatch.Frequency
                         && (track.Ref == null || !track.Ref.MarkedForClose);
            if (fresh)
            {
                from = track.Ref == null ? track.Local : Vector3D.Transform(track.Local, track.Ref.PositionComp.WorldMatrixRef);
                fresh = Vector3D.DistanceSquared(from, target) <= Reset * Reset;
            }
            if (!fresh)
            {
                from = target;
                track.Touched = null;
                track.Touching = false;
            }

            Vector3D resolved = target;
            bool touching = false;
            Hit first = default, second = default;
            int sweeps = 0;
            if (fresh && Vector3D.DistanceSquared(from, target) > Still * Still)
            {
                sweeps++;
                if (Sweep(character, hand, part, from, target, ball, radius, frame, out first) && Vector3D.Dot(first.Centre - target, first.Normal) > 0.0)
                {
                    touching = true;
                    resolved = target + first.Normal * Vector3D.Dot(first.Centre - target, first.Normal);
                    // Along the first plane to there: an inside corner (the floor meeting a wall) is the second plane.
                    Vector3D lifted = first.Centre + first.Normal * Lift;
                    if (Vector3D.DistanceSquared(lifted, resolved) > Still * Still)
                    {
                        sweeps++;
                        if (Sweep(character, hand, part, lifted, resolved, ball, radius, frame, out second))
                        {
                            for (int i = 0; i < 4; i++)
                            {
                                resolved += second.Normal * Math.Max(0.0, Vector3D.Dot(second.Centre - resolved, second.Normal));
                                resolved += first.Normal * Math.Max(0.0, Vector3D.Dot(first.Centre - resolved, first.Normal));
                            }
                        }
                    }
                    if (Vector3D.DistanceSquared(resolved, target) > MaxCatch * MaxCatch)
                    {
                        if (Trace)
                            Log.Info(string.Format(CultureInfo.InvariantCulture, "Surface stop: {0} {1} let through {2}, the real hand {3:0.0} cm past it",
                                hand, part, Describe(first.What), Vector3D.Distance(resolved, target) * 100.0));
                        touching = false;
                        resolved = target;
                        track.Touched = null;
                    }
                }
            }

            MyEntity touched = touching ? first.Owner : null;
            if (touched != null)
            {
                track.Touched = touched;
                track.TouchedFrame = frame;
            }
            MyEntity frameOf = track.Touched != null && !track.Touched.MarkedForClose && frame - track.TouchedFrame <= TouchFrames
                ? track.Touched
                : seat?.CubeGrid ?? support;
            track.Ref = frameOf;
            track.Local = frameOf == null ? resolved : Vector3D.Transform(resolved, frameOf.PositionComp.WorldMatrixNormalizedInv);
            track.Frame = frame;
            track.At = now;
            track.Valid = true;

            Vector3D push = resolved - target;
            double depth = push.Length();
            bool was = track.Touching;
            track.Touching = touching && depth > 1e-6;
            if (!track.Touching)
                return false;

            contact.Point = first.Point;
            contact.Normal = (Vector3)(push / depth);
            contact.Depth = (float)depth;
            contact.Source = SurfaceOf(first.What);
            if (Trace && (!was || frame - track.LoggedFrame >= 30))
            {
                track.LoggedFrame = frame;
                Vector3 up = (Vector3)character.PositionComp.WorldMatrixRef.Up;
                Log.Info(string.Format(CultureInfo.InvariantCulture,
                    "Surface stop: {0} {1} {2} {3}, depth {4:0.0} cm, normal ({5:0.00},{6:0.00},{7:0.00}) up.n {8:0.00}; swept {9:0.0} cm{10}, hit at {11:0.000}, contact {12:0.0} mm off the ball{13}; {14} sweep(s)",
                    hand, part, was ? "still on" : "touches", Describe(first.What), depth * 100.0,
                    contact.Normal.X, contact.Normal.Y, contact.Normal.Z, Vector3.Dot(contact.Normal, up),
                    Vector3D.Distance(from, target) * 100.0, track.Ref == null ? "" : " in " + Describe(track.Ref), first.Fraction,
                    (Vector3D.Dot(first.Centre - first.Point, first.Normal) - radius) * 1000.0,
                    second.Owner != null ? $", corner with {Describe(second.What)} n ({second.Normal.X:0.00},{second.Normal.Y:0.00},{second.Normal.Z:0.00})" : "",
                    sweeps));
            }
            return true;
        }

        private struct Hit
        {
            public float Fraction;
            public Vector3D Centre;
            public Vector3D Point;
            public Vector3D Normal;
            public MyEntity Owner;
            public object What;
        }

        /// <summary>The first solid thing a ball meets going from <paramref name="from"/> to <paramref name="to"/>.</summary>
        private static bool Sweep(MyCharacter character, Hand hand, string part, Vector3D from, Vector3D to, HkShape ball, float radius, ulong frame, out Hit best)
        {
            best = default;
            best.Fraction = float.MaxValue;
            MatrixD at = MatrixD.CreateTranslation(from);
            hits.Clear();
            insideValid = false;
            Interlocked.Increment(ref costCasts);
            MyPhysics.CastShapeReturnContactBodyDatas(to, ball, ref at, (uint)MyPhysics.CollisionLayers.CharacterCollisionLayer, 0f, hits);
            Vector3D way = to - from;
            double length = way.Length();
            foreach (MyPhysics.HitInfo hit in hits)
            {
                HkHitInfo info = hit.HkHitInfo;
                if (info.HitFraction >= best.Fraction)
                    continue;
                Vector3D normal = info.Normal;
                if (Vector3D.Dot(normal, way) > 0.0)
                    continue; // a back face: going away from it
                MyEntity owner = Solid(info, out MySlimBlock block, out object what);
                if (owner == null)
                    continue;
                if (seat != null && block != null && (block == seat.SlimBlock || enclosing.Contains(block)))
                {
                    Excluded(0, hand, part, frame, "the seat's own block", what);
                    continue;
                }
                if (info.HitFraction * length < Still && StartsInside(from, owner, info, block))
                {
                    Excluded(1, hand, part, frame, "already inside", what);
                    continue;
                }
                best.Fraction = info.HitFraction;
                best.Centre = from + way * info.HitFraction;
                best.Point = hit.Position;
                best.Normal = Vector3D.Normalize(normal);
                best.Owner = owner;
                best.What = what;
            }
            hits.Clear();
            // Glass is often outside the collision, and a cockpit's interior (seen from its seat) has none, so panes are
            // tested on their own triangles. Never passed with the seat's block: the seated hand reaching out meets it.
            if (!GlassOff && VRSettings.HandsStopOnGlass)
            {
                if (GlassPanes.Sweep(hand, frame, from, to, radius, Eye(character, frame), best.Fraction, out GlassPanes.Contact glass))
                {
                    best.Fraction = glass.Fraction;
                    best.Centre = glass.Centre;
                    best.Point = glass.Point;
                    best.Normal = glass.Normal;
                    best.Owner = glass.Owner;
                    best.What = glass.What;
                }
                else if (glass.Behind != null)
                    Excluded(2, hand, part, frame, "came back through it from its far side", glass.Behind);
            }
            return best.Owner != null;
        }

        /// <summary>A grid (not a projection) or voxels, with the block hit; null for anything the hand passes.</summary>
        internal static MyEntity Solid(HkHitInfo info, out MySlimBlock block, out object what)
        {
            block = null;
            what = null;
            if (!(info.Body?.UserObject is MyPhysicsBody body) || body.IsPhantom)
                return null;
            MyEntity hit = info.GetHitEntity() as MyEntity;
            if (hit == null || hit.MarkedForClose)
                return null;
            if (hit is MyVoxelBase)
            {
                what = hit;
                return hit;
            }
            if (!(hit.GetTopMostParent() is MyCubeGrid grid) || grid.Projector != null)
                return null;
            if (hit == grid)
            {
                if (info.ShapeKeyCount > 0)
                    block = BlockOf(grid, info.GetShapeKey(0));
            }
            else
            {
                for (MyEntity e = hit; e != null && block == null; e = e.Parent)
                    block = (e as MyCubeBlock)?.SlimBlock;
            }
            what = (object)block?.FatBlock ?? (object)block ?? grid;
            return grid;
        }

        internal static MySlimBlock BlockOf(MyCubeGrid grid, uint key)
        {
            if (key == uint.MaxValue || grid.Physics?.Shape == null)
                return null;
            try
            {
                return grid.Physics.Shape.GetBlockFromShapeKey(key);
            }
            catch
            {
                // A shape key the shape cannot resolve (for example one from before the grid changed) means no block.
                return null;
            }
        }

        /// <summary>Whether the sweep began with the ball's centre inside what it hit (then it is passed, never a trap).</summary>
        private static bool StartsInside(Vector3D from, MyEntity owner, HkHitInfo info, MySlimBlock block)
        {
            Interlocked.Increment(ref costProbes);
            if (owner is MyVoxelBase)
            {
                onePoint[0] = from;
                MyVoxelBase voxels = info.GetHitEntity() as MyVoxelBase ?? (MyVoxelBase)owner;
                return voxels.IsAnyOfPointInside(onePoint);
            }
            if (!insideValid || insideAt != from)
            {
                insideAt = from;
                insideValid = true;
                inside.Clear();
                Quaternion none = Quaternion.Identity;
                MyPhysics.GetPenetrationsShape(insideBall, ref insideAt, ref none, inside, MyPhysics.CollisionLayers.CharacterCollisionLayer);
            }
            uint key = info.ShapeKeyCount > 0 ? info.GetShapeKey(0) : uint.MaxValue;
            foreach (HkBodyCollision c in inside)
            {
                if (c.Body != info.Body)
                    continue;
                if (c.ShapeKey == key || block == null)
                    return true;
                if (owner is MyCubeGrid grid && BlockOf(grid, c.ShapeKey) == block)
                    return true;
            }
            return false;
        }

        private static void Excluded(int kind, Hand hand, string part, ulong frame, string why, object what)
        {
            if (!Trace || frame - loggedKind[kind] < 60)
                return;
            loggedKind[kind] = frame;
            Log.Info($"Surface stop: {hand} {part} passes {Describe(what)} ({why})");
        }

        /// <summary>
        /// What <see cref="HandStop"/> sees the hand on: a functional block (a console, a cockpit, a door) is its own surface,
        /// armor and the rest of a grid is the grid's, voxels are the map's; so sliding along a hull is one touch, one tick.
        /// </summary>
        private sealed class Surface
        {
            public readonly MyEntity Entity;
            private readonly string name;

            public Surface(MyEntity entity, string name)
            {
                Entity = entity;
                this.name = name;
            }

            public override bool Equals(object obj) => obj is Surface other && other.Entity == Entity;
            public override int GetHashCode() => Entity.GetHashCode();
            public override string ToString() => name;
        }

        private static readonly Dictionary<MyEntity, Surface> surfaces = new Dictionary<MyEntity, Surface>();

        private static object SurfaceOf(object what)
        {
            if (what is GlassPanes.Glass glass)
                return glass; // a block's glass is its own surface, apart from the block's hull
            MyEntity entity = (MyEntity)(what as MyCubeBlock) ?? (MyEntity)(what as MySlimBlock)?.CubeGrid ?? what as MyEntity;
            if (entity == null)
                return null;
            if (surfaces.TryGetValue(entity, out Surface known) && !entity.MarkedForClose)
                return known;
            if (surfaces.Count > 64)
                surfaces.Clear();
            Surface made = new Surface(entity, entity is MyCubeGrid grid ? $"the hull of {grid.DisplayName}" : Describe(entity));
            surfaces[entity] = made;
            return made;
        }

        internal static string Describe(object what)
        {
            switch (what)
            {
                case MyCubeBlock fat:
                    return $"{fat.BlockDefinition?.Id.SubtypeName ?? fat.GetType().Name} on {fat.CubeGrid?.DisplayName}";
                case MySlimBlock slim:
                    return $"{slim.BlockDefinition?.Id.SubtypeName ?? "block"} on {slim.CubeGrid?.DisplayName}";
                case MyVoxelBase voxels:
                    return $"voxels {voxels.StorageName ?? voxels.GetType().Name}";
                case GlassPanes.Glass glass:
                    return glass.ToString();
                case MyEntity entity:
                    return entity.DisplayName ?? entity.GetType().Name;
                default:
                    return "nothing";
            }
        }

        /// <summary>For the Perf line: the cost since the last call, per simulation frame that asked.</summary>
        internal static string PerfText()
        {
            int frames = Interlocked.Exchange(ref costFrames, 0);
            long ticks = Interlocked.Exchange(ref costTicks, 0);
            long casts = Interlocked.Exchange(ref costCasts, 0);
            long probes = Interlocked.Exchange(ref costProbes, 0);
            long max = Interlocked.Exchange(ref costMaxTicks, 0);
            long glass = GlassPanes.TakeTested();
            if (Off)
                return "; surface stop off";
            if (frames == 0)
                return "; surface stop not asked";
            double ms = 1000.0 / Stopwatch.Frequency;
            return string.Format(CultureInfo.InvariantCulture, "; surface stop {0:0.000} ms/frame avg / {1:0.000} max, {2:0.0} casts + {3:0.00} probes + {4} a frame",
                ticks * ms / frames, max * ms, (double)casts / frames, (double)probes / frames,
                GlassOff || !VRSettings.HandsStopOnGlass ? "glass off" : ((double)glass / frames).ToString("0", CultureInfo.InvariantCulture) + " glass triangles");
        }
    }
}
