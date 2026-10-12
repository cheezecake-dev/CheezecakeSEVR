using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using Havok;
using Sandbox;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using SpaceEngineersVR.Common;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;
using VRage.Game.Entity;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// Moving by hand, as in Lone Echo. Where there is no gravity, a grip pressed with the hand on a ship's or station's
    /// hull, or on an asteroid, takes hold of that spot, and the body follows the hand: pull the hand back and the body
    /// goes forward, along with the ship if it moves. Two hands climb hand over hand. Let go and the body keeps the speed
    /// it had (a push-off, or a fling), no faster than <see cref="VRSettings.PushOffMaxSpeed"/> relative to the surface;
    /// the jetpack's dampeners then act only if they were on. Held or let go, the body is never moved faster than the
    /// game's own push-off from a surface without the jetpack (<see cref="HullGrabMath.GameMaxSpeed"/>), so the hands
    /// reach no speed a jump off the hull would not. On a ladder (in gravity or not) a grip on it climbs through the
    /// game's own ladder mode: pull the hand down and the character climbs a step, push it up and it climbs down.
    /// </summary>
    /// <remarks>
    /// The body is moved by its velocity, set each simulation step just before the physics steps (a prefix on
    /// MyPhysics.Simulate, after the character's own movement and the jetpack's thrust are applied in the Simulate
    /// phase before it), so collisions hold and nothing is teleported. The anchor is a point in the surface's own frame
    /// (a grid's or a voxel map's); the velocity is the surface's own at that point plus
    /// <see cref="HullGrabMath.ServoVelocity"/> of the gap between the anchor and the hand. Two hands on: the one that
    /// took hold last leads, and the other's anchor follows its hand until it leads in turn. The ladder climbs by the game's
    /// own steps (MyCharacterLadderComponent.ProceedLadderMovement): the hand's pull is banked, and a step up or down is
    /// asked for once half a step is owed. The grip is claimed through <see cref="GripClaim"/> at
    /// <see cref="Priority"/>, after the helmet, floating items and grab-to-use, so a grip on a door stays the door's.
    /// </remarks>
    internal static partial class HullGrab
    {
        public const string Name = "HullGrab";

        public const int Priority = 40;

        /// <summary>How near the grip's middle has to be to a grid's or an asteroid's collision surface, metres, for a press to take hold.</summary>
        public const float HullReach = 0.08f;

        /// <summary>How near the grip's middle has to be to a ladder block's cell, metres.</summary>
        public const float LadderReach = 0.15f;

        /// <summary>Gravity (natural and artificial, m/s²) under which a hull can be grabbed; above <see cref="GravityLetGo"/> a hold lets go. In gravity, walking rules.</summary>
        public const float ZeroGravity = 0.25f, GravityLetGo = 0.5f;

        /// <summary>A hand this far from its anchor, metres, lets go: the body cannot follow it (it is blocked, or was moved by something else).</summary>
        public const double StretchLetGo = 1.0;

        /// <summary>Steps to wait for the game to put the character on a grabbed ladder before giving up.</summary>
        private const int LadderWaitSteps = 30;

        /// <summary>How long after a push-off the speed is logged again, steps (one second): it shows whether the speed was kept.</summary>
        private const int DriftCheckSteps = 60;

        private const float GrabAmplitude = 0.5f, GrabSeconds = 0.04f;

        /// <summary>
        /// While the body moves along the surface faster than <see cref="ScrapeMinSpeed"/> (m/s), the holding hands feel a
        /// rough scrape: a short pulse every <see cref="ScrapeEverySteps"/> steps, each a little longer than the gap so they
        /// run together, firmer with speed and roughened by noise. Asteroids scrape harder than hulls.
        /// </summary>
        private const float ScrapeMinSpeed = 0.15f, ScrapeAmplitude = 0.12f, ScrapeAmplitudePerSpeed = 0.06f, ScrapeMaxAmplitude = 0.4f;
        private const float ScrapeSeconds = 0.06f, ScrapeRockShare = 1.3f;
        private const int ScrapeEverySteps = 3;
        private const float StretchAmplitude = 0.6f, StretchSeconds = 0.1f;
        private const float StepAmplitude = 0.25f, StepSeconds = 0.03f;

        /// <summary>RenderDebug HullGrab=0: no hull grabbing (a grip on a hull means what it does in empty air).</summary>
        public static bool HullOff { get; set; }

        /// <summary>RenderDebug LadderGrab=0: no ladder grabbing.</summary>
        public static bool LadderOff { get; set; }

        private enum Kind
        {
            None,
            Hull,
            Ladder,
        }

        /// <summary>How a hold's end leaves the body's speed.</summary>
        private enum Leave
        {
            /// <summary>The push-off: the speed the hand was moving the body at.</summary>
            Push,

            /// <summary>At rest relative to the hull.</summary>
            Stop,

            /// <summary>Untouched.</summary>
            Keep,
        }

        /// <summary>What one hand holds.</summary>
        private sealed class Hold
        {
            public Kind Kind;
            public MyEntity Surface;    // a grid or a voxel map
            public Vector3D Anchor;     // in the surface's frame
            public Vector3D BodyStart;  // the body when it took hold, in the surface's frame
            public MyLadder Ladder;
            public bool AskedOn, WasOn;
            public int Steps;
            public long Since;
            public bool HasLast;
            public Vector3D Last;       // the hand in the body's model space, last step

            public void Clear()
            {
                Kind = Kind.None;
                Surface = null;
                Ladder = null;
                AskedOn = WasOn = HasLast = false;
                Steps = 0;
            }
        }

        private static readonly Hold[] holds = { new Hold(), new Hold() };

        /// <summary>The surface the push-off is measured against: the last one taken hold of.</summary>
        private static MyEntity reference;

        private static readonly MotionWindow window = new MotionWindow();

        // Each held hand this step, in the world (game thread only).
        private static readonly Vector3D[] hands = new Vector3D[2];
        private static readonly bool[] tracked = new bool[2];

        /// <summary>Ladder: the climb the held hands have pulled for and not yet been given, metres, + up.</summary>
        private static float owed;

        /// <summary>Ladder: the step asked of the game this step (+1 up, -1 down), checked after it answered.</summary>
        private static int asked;

        private static AccessTools.FieldRef<MyCharacterLadderComponent, int> ladderStep;

        private static readonly FeatureGuard guard = new FeatureGuard(nameof(HullGrab));
        private static int step;
        private static bool hooked;

        // The push-off's check a second later.
        private static int driftAt;
        private static MyEntity driftSurface;
        private static double driftSpeed;

        /// <summary>The body <see cref="placedTrackingToModel"/> is for, or null.</summary>
        private static MyCharacter placedFor;
        private static MatrixD placedTrackingToModel;

        // Query buffers (game thread only).
        private static readonly List<HkBodyCollision> hits = new List<HkBodyCollision>();
        private static readonly List<MyEntity> nearby = new List<MyEntity>();
        private static readonly HashSet<MySlimBlock> blocks = new HashSet<MySlimBlock>();

        // ---- The hooks ----

        public static void Patch(Harmony harmony)
        {
            try
            {
                harmony.Patch(AccessTools.Method(typeof(MyPhysics), nameof(MyPhysics.Simulate)),
                    prefix: new HarmonyMethod(typeof(HullGrab), nameof(BeforePhysics)));
                harmony.Patch(AccessTools.Method(typeof(MyCharacter), nameof(MyCharacter.UpdateAfterSimulation)),
                    postfix: new HarmonyMethod(typeof(HullGrab), nameof(AfterCharacterSimulation)));
                GripClaim.Register(Name, Priority, Take);
                hooked = true;
            }
            catch (Exception e)
            {
                PatchRegistry.Lost(e, "Hull grab could not be hooked; grips on hulls and ladders keep their usual meaning");
                throw;
            }

            if (!PatchRegistry.Apply("ladder", () =>
                {
                    ladderStep = AccessTools.FieldRefAccess<MyCharacterLadderComponent, int>("m_currentLadderStep");
                    harmony.Patch(AccessTools.Method(typeof(MyCharacterLadderComponent), nameof(MyCharacterLadderComponent.ProceedLadderMovement)),
                        prefix: new HarmonyMethod(typeof(HullGrab), nameof(BeforeLadderMove)),
                        postfix: new HarmonyMethod(typeof(HullGrab), nameof(AfterLadderMove)));
                    Log.Info("Hull grab: hull and ladder grabbing hooked (physics step, ladder movement)");
                }, "Hull grab: ladder climbing by hand could not be hooked; a grip on a ladder only gets on it"))
                ladderStep = null;
        }

        /// <summary>
        /// Game thread, after the simulation has moved a character: for the local one, the move from the tracking space to
        /// its model space, taken where <see cref="HandWorld.TryGetTrackingToBody"/> is meant to be asked and where the arms
        /// are solved by it (<see cref="HandIK"/>), so a hold is on the hand the player sees. Asked again before the next
        /// physics step it gives another place, by as much as 0.3 m.
        /// </summary>
        private static void AfterCharacterSimulation(MyCharacter __instance)
        {
            if (__instance != MySession.Static?.LocalCharacter)
                return;
            placedFor = HandWorld.TryGetTrackingToBody(__instance, out placedTrackingToModel, out _) ? __instance : null;
        }

        /// <summary>The move <see cref="AfterCharacterSimulation"/> last took for <paramref name="body"/>; false if none.</summary>
        private static bool TryGetTrackingToBody(MyCharacter body, out MatrixD trackingToModel)
        {
            trackingToModel = placedTrackingToModel;
            return body != null && placedFor == body;
        }

        /// <summary>Game thread, each simulation step, just before the physics steps.</summary>
        private static void BeforePhysics()
        {
            if (!hooked || !MySandboxGame.IsGameReady || !guard.On)
                return;
            try
            {
                Step();
            }
            catch (Exception e)
            {
                guard.Failed(e, "Hull grab failed");
                for (int i = 0; i < 2; i++)
                    holds[i].Clear();
                window.Clear();
                reference = null;
            }
        }

        private static void Step()
        {
            step++;
            MyCharacter body = MySession.Static?.LocalCharacter;
            CheckDrift(body);

            // Asking for the owner asks the takers on a new press, whoever asks first. Both hands are asked here every step:
            // nothing else asks for the left hand in flight, or for either hand in a menu-less frame without Use.
            for (int i = 0; i < 2; i++)
            {
                Hand hand = (Hand)i;
                string owner = GripClaim.Owner(hand);
                if (holds[i].Kind != Kind.None && owner != Name)
                    LetGo(body, hand, "let go", Leave.Push);
            }

            if (holds[0].Kind == Kind.None && holds[1].Kind == Kind.None)
            {
                owed = 0f;
                return;
            }
            if (body == null || body.Closed || body.IsDead || body.IsSitting || body.Physics == null)
            {
                for (int i = 0; i < 2; i++)
                {
                    if (holds[i].Kind != Kind.None)
                        LetGo(body, (Hand)i, "the body is gone, dead or seated", Leave.Keep);
                }
                return;
            }

            bool placed = TryGetTrackingToBody(body, out MatrixD trackingToModel);
            HoldHull(body, placed, trackingToModel);
            HoldLadder(body, placed, trackingToModel);
        }

        // ---- Hulls ----

        private static void HoldHull(MyCharacter body, bool placed, in MatrixD trackingToModel)
        {
            for (int i = 0; i < 2; i++)
            {
                Hold hold = holds[i];
                if (hold.Kind != Kind.Hull)
                    continue;
                string why = null;
                if (HullOff || !VRSettings.GrabHulls)
                    why = "hull grabbing is off";
                else if (Gone(hold.Surface))
                    why = "the surface went away";
                else if (body.IsOnLadder)
                    why = "on a ladder";
                else if (body.Gravity.Length() > GravityLetGo)
                    why = $"gravity {body.Gravity.Length():F2} m/s²";
                if (why != null)
                    LetGo(body, (Hand)i, why, Leave.Keep);
            }
            if (!placed)
                return;

            MatrixD modelToWorld = body.PositionComp.WorldMatrixRef;
            tracked[0] = tracked[1] = false;
            int lead = -1;
            bool moved = false;
            for (int i = 0; i < 2; i++)
            {
                Hold hold = holds[i];
                if (hold.Kind != Kind.Hull || !TryModel((Hand)i, trackingToModel, out Vector3D model))
                    continue;
                Vector3D hand = Reach.Limit(body, Vector3D.Transform(model, modelToWorld));
                double stretch = Vector3D.Distance(Vector3D.Transform(hold.Anchor, hold.Surface.PositionComp.WorldMatrixRef), hand);
                if (stretch > StretchLetGo)
                {
                    Haptics.Pulse((Hand)i, StretchAmplitude, StretchSeconds);
                    LetGo(body, (Hand)i, $"stretched {stretch:F2} m from the hold", Leave.Stop);
                    continue;
                }
                moved |= hold.HasLast && Vector3D.DistanceSquared(model, hold.Last) > 1e-12;
                hold.Last = model;
                hold.HasLast = true;
                hands[i] = hand;
                tracked[i] = true;
                if (lead < 0 || hold.Since > holds[lead].Since)
                    lead = i;
            }
            if (lead < 0)
                return;

            // The hand that took hold last leads, as in climbing games; the other's anchor follows its hand, so it never
            // stretches and takes over from where it is, with no jump, when the leader lets go.
            Hold leader = holds[lead];
            Hold follower = holds[1 - lead];
            if (tracked[1 - lead])
                follower.Anchor = Vector3D.Transform(hands[1 - lead], follower.Surface.PositionComp.WorldMatrixNormalizedInv);
            Vector3D anchor = Vector3D.Transform(leader.Anchor, leader.Surface.PositionComp.WorldMatrixRef);
            Vector3D along = HullGrabMath.ServoVelocity(anchor - hands[lead], body.BaseMass);
            body.Physics.LinearVelocity = (Vector3)(leader.Surface.Physics.GetVelocityAtPoint(anchor) + along);
            if (reference != leader.Surface)
            {
                reference = leader.Surface;
                window.Clear();
            }
            if (reference?.PositionComp != null)
                window.Add(Vector3D.Transform(body.PositionComp.GetPosition(), reference.PositionComp.WorldMatrixNormalizedInv), moved);
            Scrape(along.Length());
        }

        /// <summary>One step of the scrape on the holding hands, at the body's speed along the surface (m/s).</summary>
        private static void Scrape(double speed)
        {
            if (speed < ScrapeMinSpeed || step % ScrapeEverySteps != 0)
                return;
            float level = Math.Min(ScrapeMaxAmplitude, ScrapeAmplitude + ScrapeAmplitudePerSpeed * (float)speed);
            for (int i = 0; i < 2; i++)
            {
                Hold hold = holds[i];
                if (hold.Kind != Kind.Hull)
                    continue;
                float rough = (float)(0.6 + 0.4 * Haptics.Noise(step * 2L + i));
                float amplitude = level * rough * (hold.Surface is MyVoxelBase ? ScrapeRockShare : 1f);
                Haptics.Pulse((Hand)i, Math.Min(ScrapeMaxAmplitude, amplitude), ScrapeSeconds);
            }
        }

        private static bool Gone(MyEntity surface) => surface == null || surface.Closed || surface.MarkedForClose || surface.Physics == null;

        /// <summary>The hand's grip pose in the body's model space this step; false while it is not tracked.</summary>
        private static bool TryModel(Hand hand, in MatrixD trackingToModel, out Vector3D model)
        {
            HandState state = VRInput.Get(hand);
            model = state.Tracked ? Vector3D.Transform((Vector3D)state.Grip.Translation, trackingToModel) : Vector3D.Zero;
            return state.Tracked;
        }

        // ---- Ladders ----

        private static void HoldLadder(MyCharacter body, bool placed, in MatrixD trackingToModel)
        {
            float pulled = 0f;
            int counted = 0, ladders = 0;
            for (int i = 0; i < 2; i++)
            {
                Hold hold = holds[i];
                if (hold.Kind != Kind.Ladder)
                    continue;
                Hand hand = (Hand)i;
                hold.Steps++;
                if (LadderOff || !VRSettings.GrabLadders)
                {
                    LetGo(body, hand, "ladder grabbing is off", Leave.Keep);
                    continue;
                }
                if (!body.IsOnLadder)
                {
                    hold.HasLast = false;
                    if (hold.WasOn)
                        LetGo(body, hand, "off the ladder", Leave.Keep);
                    else if (!hold.AskedOn)
                    {
                        hold.AskedOn = true;
                        if (hold.Ladder != null && !hold.Ladder.Closed)
                            body.GetOnLadder(hold.Ladder);
                    }
                    else if (hold.Steps > LadderWaitSteps)
                        LetGo(body, hand, "the game did not put the character on the ladder (blocked?)", Leave.Keep);
                    continue;
                }
                if (!hold.WasOn)
                {
                    hold.WasOn = true;
                    owed = 0f;
                    Note($"Hull grab: {hand} hand climbs {Describe(body.Ladder)}, {StepLength(body):F2} m a step");
                }
                ladders++;
                if (!placed || !TryModel(hand, trackingToModel, out Vector3D model))
                    continue;
                // Model space has the body's up as +Y: a hand pulled down is a climb up.
                if (hold.HasLast)
                {
                    pulled += (float)(hold.Last.Y - model.Y);
                    counted++;
                }
                hold.Last = model;
                hold.HasLast = true;
            }
            if (ladders == 0)
                owed = 0f;
            else if (counted > 0)
                owed = HullGrabMath.ClampOwed(owed + pulled / counted, StepLength(body));
        }

        private static float StepLength(MyCharacter body)
        {
            // MyCharacterLadderComponent: a step is m_stepsPerAnimation (59) increments of 2 * DistanceBetweenPoles / 59.
            float poles = body?.Ladder?.DistanceBetweenPoles ?? 0f;
            return Math.Max(0.1f, 2f * poles);
        }

        private static bool Climbing(MyCharacterLadderComponent ladder, out MyCharacter body)
        {
            body = ladder?.Entity as MyCharacter;
            if (ladderStep == null || body == null || body != MySession.Static?.LocalCharacter || !body.IsOnLadder)
                return false;
            for (int i = 0; i < 2; i++)
            {
                if (holds[i].Kind == Kind.Ladder && holds[i].WasOn)
                    return true;
            }
            return false;
        }

        /// <summary>Before the game reads the ladder input: a step's worth of pull asks for a step, as the move keys would.</summary>
        private static void BeforeLadderMove(MyCharacterLadderComponent __instance, ref Vector3 moveIndicator)
        {
            asked = 0;
            try
            {
                if (!Climbing(__instance, out MyCharacter body) || ladderStep(__instance) != 0)
                    return;
                asked = HullGrabMath.LadderStep(owed, StepLength(body));
                // The game's ladder: move forward (-Z) climbs up, back (+Z) climbs down.
                if (asked > 0)
                    moveIndicator.Z = -1f;
                else if (asked < 0)
                    moveIndicator.Z = 1f;
            }
            catch (Exception e)
            {
                asked = 0;
                ladderStep = null;
                Log.Error(e, "Hull grab: climbing by hand failed and is off until restart");
            }
        }

        /// <summary>After: a step the game started is paid for out of the pull.</summary>
        private static void AfterLadderMove(MyCharacterLadderComponent __instance)
        {
            if (asked == 0)
                return;
            int direction = asked;
            asked = 0;
            try
            {
                if (ladderStep == null || ladderStep(__instance) == 0 || !(__instance.Entity is MyCharacter body))
                    return;
                owed -= direction * StepLength(body);
                for (int i = 0; i < 2; i++)
                {
                    if (holds[i].Kind == Kind.Ladder)
                        Haptics.Pulse((Hand)i, StepAmplitude, StepSeconds);
                }
                Note($"Hull grab: ladder step {(direction > 0 ? "up" : "down")}, {owed:+0.00;-0.00;0.00} m still owed");
            }
            catch (Exception e)
            {
                ladderStep = null;
                Log.Error(e, "Hull grab: climbing by hand failed and is off until restart");
            }
        }

        // ---- Taking hold ----

        /// <summary>The grip claim's question, on a new press: is this hand on a ladder, or on a hull or asteroid with no gravity about?</summary>
        private static bool Take(Hand hand)
        {
            if (!VRInput.Active || !hooked || !guard.On)
                return false;
            MyCharacter body = MySession.Static?.LocalCharacter;
            if (body == null || body.Closed || body.IsDead || body.IsSitting || body.Physics == null || body.PositionComp == null)
                return false;
            bool ladders = VRSettings.GrabLadders && !LadderOff;
            bool hulls = VRSettings.GrabHulls && !HullOff && !body.IsOnLadder && Floating(body);
            if (!ladders && !hulls)
                return false;
            if (!TryGetTrackingToBody(body, out MatrixD trackingToModel) || !TryModel(hand, trackingToModel, out Vector3D model))
                return false;
            Vector3D world = Reach.Limit(body, Vector3D.Transform(model, body.PositionComp.WorldMatrixRef));
            Hold hold = holds[(int)hand];

            if (ladders && TryLadder(world, out MyLadder ladder))
            {
                hold.Clear();
                hold.Kind = Kind.Ladder;
                hold.Ladder = ladder;
                hold.Since = Stopwatch.GetTimestamp();
                Haptics.Pulse(hand, GrabAmplitude, GrabSeconds);
                Note($"Hull grab: {hand} hand takes {Describe(ladder)}{(body.IsOnLadder ? " (on a ladder already)" : "")}");
                return true;
            }
            if (hulls && TrySurface(world, out MyEntity surface))
            {
                hold.Clear();
                hold.Kind = Kind.Hull;
                hold.Surface = surface;
                MatrixD fromWorld = surface.PositionComp.WorldMatrixNormalizedInv;
                hold.Anchor = Vector3D.Transform(world, fromWorld);
                hold.BodyStart = Vector3D.Transform(body.PositionComp.GetPosition(), fromWorld);
                hold.Since = Stopwatch.GetTimestamp();
                if (reference != surface)
                {
                    reference = surface;
                    window.Clear();
                }
                Haptics.Pulse(hand, GrabAmplitude, GrabSeconds);
                Note($"Hull grab: {hand} hand takes hold of {Describe(surface)} at {Format(hold.Anchor)} (its frame), " +
                     $"jetpack {(body.JetpackRunning ? "on" : "off")}, dampeners {(body.JetpackComp?.DampenersTurnedOn == true ? "on" : "off")}, " +
                     $"gravity {body.Gravity.Length():F2} m/s²");
                return true;
            }
            return false;
        }

        /// <summary>No gravity to speak of, and not standing on anything: flying, or adrift.</summary>
        private static bool Floating(MyCharacter body) =>
            body.Gravity.Length() < ZeroGravity && (body.JetpackRunning || !(body.Physics.CharacterProxy?.Supported ?? false));

        /// <summary>
        /// A grid or voxel map whose collision surface is within <see cref="HullReach"/> of the point: the game's own shape
        /// query, a ball of that radius. A grid wins over rock it is touching: a ship landed on an asteroid moves.
        /// </summary>
        private static bool TrySurface(Vector3D at, out MyEntity surface)
        {
            surface = null;
            HkSphereShape ball = new HkSphereShape(HullReach);
            try
            {
                Quaternion turn = Quaternion.Identity;
                hits.Clear();
                MyPhysics.GetPenetrationsShape(ball.Base, ref at, ref turn, hits, MyPhysics.CollisionLayers.CharacterCollisionLayer);
                foreach (HkBodyCollision hit in hits)
                {
                    VRage.ModAPI.IMyEntity found = hit.GetCollisionEntity();
                    if (found is MyCubeGrid grid && grid.Physics != null && grid.Projector == null && !grid.MarkedForClose)
                    {
                        surface = grid;
                        return true;
                    }
                    if (surface == null && found is MyVoxelBase voxel && !Gone(voxel))
                        surface = voxel;
                }
                return surface != null;
            }
            finally
            {
                hits.Clear();
                ball.Base.RemoveReference();
            }
        }

        /// <summary>The working ladder block nearest the point, of those within <see cref="LadderReach"/> of its cell.</summary>
        private static bool TryLadder(Vector3D at, out MyLadder ladder)
        {
            ladder = null;
            var ball = new BoundingSphereD(at, LadderReach);
            double best = double.MaxValue;
            try
            {
                nearby.Clear();
                MyGamePruningStructure.GetAllTopMostEntitiesInSphere(ref ball, nearby);
                foreach (MyEntity entity in nearby)
                {
                    if (!(entity is MyCubeGrid grid) || grid.Physics == null || grid.Projector != null)
                        continue;
                    blocks.Clear();
                    grid.GetBlocksInsideSphere(ref ball, blocks);
                    foreach (MySlimBlock block in blocks)
                    {
                        if (!(block.FatBlock is MyLadder found) || !found.IsFunctional)
                            continue;
                        double distance = Vector3D.DistanceSquared(found.PositionComp.WorldAABB.Center, at);
                        if (distance < best)
                        {
                            best = distance;
                            ladder = found;
                        }
                    }
                }
                return ladder != null;
            }
            finally
            {
                nearby.Clear();
                blocks.Clear();
            }
        }

        // ---- Letting go ----

        private static void LetGo(MyCharacter body, Hand hand, string why, Leave leave)
        {
            Hold hold = holds[(int)hand];
            Kind kind = hold.Kind;
            double seconds = (Stopwatch.GetTimestamp() - hold.Since) / (double)Stopwatch.Frequency;
            if (kind == Kind.Hull)
            {
                Hold other = holds[1 - (int)hand];
                if (other.Kind == Kind.Hull)
                {
                    Note($"Hull grab: {hand} hand lets go after {seconds:F1} s ({why}); the other hand still holds");
                    if (reference == hold.Surface && other.Surface != hold.Surface)
                    {
                        reference = other.Surface;
                        window.Clear();
                    }
                }
                else
                    LeaveHull(body, hand, hold, why, leave, seconds);
            }
            else if (kind == Kind.Ladder)
                Note($"Hull grab: {hand} hand lets go of the ladder after {seconds:F1} s ({why})");

            hold.Clear();
            GripClaim.Release(hand, Name);
        }

        /// <summary>
        /// The last hand off the surface: the body leaves at the speed the hand was moving it, or as <paramref name="leave"/>
        /// says. No pulse: the hand has opened, and the push-off is felt in the motion.
        /// </summary>
        private static void LeaveHull(MyCharacter body, Hand hand, Hold hold, string why, Leave leave, double seconds)
        {
            MyEntity surface = hold.Surface;
            bool usable = body?.Physics != null && !body.Closed && !Gone(surface) && surface.PositionComp != null;
            string moved = "";
            string speed = "";
            if (usable)
            {
                Vector3D position = body.PositionComp.GetPosition();
                MatrixD surfaceWorld = surface.PositionComp.WorldMatrixRef;
                Vector3D travel = Vector3D.TransformNormal(Vector3D.Transform(position, surface.PositionComp.WorldMatrixNormalizedInv) - hold.BodyStart, surfaceWorld);
                MatrixD bodyWorld = body.PositionComp.WorldMatrixRef;
                moved = $", moved {travel.Length():F2} m along the surface " +
                        $"({Vector3D.Dot(travel, bodyWorld.Right):+0.00;-0.00;0.00} right, {Vector3D.Dot(travel, bodyWorld.Up):+0.00;-0.00;0.00} up, {Vector3D.Dot(travel, bodyWorld.Forward):+0.00;-0.00;0.00} forward)";

                Vector3D surfaceVelocity = surface.Physics.GetVelocityAtPoint(position);
                if (leave == Leave.Push && reference == surface && window.Count >= 1)
                {
                    // Where the last step put the body, so the window ends now and not a step ago.
                    window.Add(Vector3D.Transform(position, surface.PositionComp.WorldMatrixNormalizedInv), false);
                    Vector3D measured = Vector3D.TransformNormal(window.Velocity(), surfaceWorld);
                    Vector3D relative = HullGrabMath.PushOff(measured, VRSettings.PushOffMaxSpeed, body.BaseMass);
                    body.Physics.LinearVelocity = (Vector3)(surfaceVelocity + relative);
                    double pushed = relative.Length();
                    double handSpeed = measured.Length();
                    string capped = pushed == 0 && handSpeed >= 0.005 ? $" (hand {handSpeed:F2}, too slow to push off)"
                        : handSpeed > pushed + 0.01 ? $" (hand {handSpeed:F2}, capped)" : "";
                    speed = $", push-off {pushed:F2} m/s relative to the surface{capped} " +
                            $"(jetpack {(body.JetpackRunning ? "on" : "off")}, dampeners {(body.JetpackComp?.DampenersTurnedOn == true ? "on" : "off")})";
                    driftAt = step + DriftCheckSteps;
                    driftSurface = surface;
                    driftSpeed = pushed;
                }
                else if (leave == Leave.Stop || leave == Leave.Push)
                {
                    body.Physics.LinearVelocity = surfaceVelocity;
                    speed = ", at rest relative to the surface";
                }
            }
            Note($"Hull grab: {hand} hand lets go of {Describe(surface)} after {seconds:F1} s ({why}){moved}{speed}");
            window.Clear();
            reference = null;
        }

        /// <summary>A second after a push-off: how fast the body is still going relative to that surface.</summary>
        private static void CheckDrift(MyCharacter body)
        {
            if (driftAt == 0 || step < driftAt)
                return;
            driftAt = 0;
            MyEntity surface = driftSurface;
            driftSurface = null;
            if (body?.Physics == null || Gone(surface) || holds[0].Kind == Kind.Hull || holds[1].Kind == Kind.Hull)
                return;
            Vector3D position = body.PositionComp.GetPosition();
            double now = (body.Physics.LinearVelocity - surface.Physics.GetVelocityAtPoint(position)).Length();
            Note($"Hull grab: 1 s after the push-off, {now:F2} m/s relative to {Describe(surface)} (left at {driftSpeed:F2}; " +
                 $"jetpack {(body.JetpackRunning ? "on" : "off")}, dampeners {(body.JetpackComp?.DampenersTurnedOn == true ? "on" : "off")})");
        }

        // ---- The log ----

        private static readonly LineThrottle notes = new LineThrottle(6);

        /// <summary>One line per grab and let-go, at most 6 a second.</summary>
        private static void Note(string line) => notes.Info(line);

        private static string Describe(MyLadder ladder) =>
            ladder == null ? "a ladder" : $"ladder '{ladder.BlockDefinition?.Id.SubtypeName}' on {ladder.CubeGrid?.DisplayName}";

        private static string Describe(MyEntity surface)
        {
            switch (surface)
            {
                case null: return "the surface";
                case MyCubeGrid grid: return grid.DisplayName;
                case MyVoxelBase voxel: return $"asteroid '{voxel.StorageName ?? voxel.DisplayName}'";
                default: return surface.DisplayName ?? surface.GetType().Name;
            }
        }

        private static string Format(Vector3D v) => $"({v.X:F2}, {v.Y:F2}, {v.Z:F2})";
    }
}
