using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using Sandbox;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Gui;
using Sandbox.Game.SessionComponents.Clipboard;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;
using VRage;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.Entity.UseObject;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// Floating items (ore, ice, components, a dropped tool: <see cref="MyFloatingObject"/>) taken out of the air by hand.
    /// A grip that starts with the hand on an item holds it; opening the hand lets go of it at the hand's speed (a throw);
    /// letting go of it at the chest puts it in the inventory, through the game's own pick-up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hold is a velocity servo on the item's own rigid body, every simulation frame: the item is given the hand's
    /// velocity plus a correction toward where it is held, so it stays a physical body (it collides, a wall stops it, and
    /// a hand that pulls it too far through one loses it). Nothing is teleported. The correction is as fast as an arm
    /// could make it (<see cref="ArmForce"/> over the item's mass), so a heavy lump comes along sluggishly and a stack of
    /// tonnes hardly moves; in gravity the arm has to hold the item's weight out of the same force.
    /// </para>
    /// <para>
    /// The hand's pose is the controller's grip, placed in the world by the head matrix this frame's camera is built from
    /// (<see cref="HandWorld.TryGetTrackingToBody"/>, as the arms are), and its velocity is taken from its world positions
    /// over the last few frames, so it carries the body's own motion (a jetpack, a ship the player stands in).
    /// </para>
    /// <para>
    /// Putting it away is the item's own Use with the pick-up action, as the game's pick-up key does: it raises the
    /// inventory's pick-up event, which the server runs (amount that fits, removal of the item, the take sound, the
    /// inventory-full warning). An item that does not fit stays where it was let go.
    /// </para>
    /// <para>
    /// Local only. In multiplayer a client does not simulate floating objects (the server does, and its state overwrites
    /// a client's), so holding and throwing there needs the server to run the servo for the holder; the pick-up already
    /// goes through a server event and would work as is.
    /// </para>
    /// </remarks>
    internal static class ItemGrab
    {
        /// <summary>The name the grip is claimed under (<see cref="GripClaim"/>).</summary>
        public const string Name = "Items";

        /// <summary>Asked after holsters and the helmet, before grab-to-use and the hull.</summary>
        public const int Priority = 20;

        /// <summary>Debug: no grabbing (<see cref="Rendering.RenderDebug"/> GrabItems=0).</summary>
        public static bool Off { get; set; }

        /// <summary>Debug: log how a held item follows the hand and how a released one flies (<see cref="Rendering.RenderDebug"/> GrabItemsTrace=1).</summary>
        public static bool Trace { get; set; }

        // ---- What it feels like ----

        /// <summary>A hand grabs an item when its grip point is inside the item's box or this close to it, metres.</summary>
        public const double GrabReach = 0.10;

        /// <summary>The reach tick fires again only after the hand has been this much further away, metres.</summary>
        private const double ReachHysteresis = 0.03;

        /// <summary>An item held further than this from where the hand holds it (stopped by something, or too heavy to follow) is lost, metres.</summary>
        public const double BreakDistance = 0.6;

        /// <summary>
        /// How hard an arm pushes, newtons: the most it can change an item's velocity is this over the item's mass. A
        /// component or a handful of ore (3 to 50 kg) follows the hand at once; 200 kg comes along at 10 m/s^2; a tonne at
        /// 2 m/s^2, and in a gravity of 1 g anything over about 200 kg cannot be held up.
        /// </summary>
        public const float ArmForce = 2000f;

        /// <summary>How fast the item is pulled to where it is held: the fraction of the distance closed per second.</summary>
        private const double PullRate = 30.0;

        /// <summary>The fastest the pull alone moves an item (on top of the hand's own speed), metres a second.</summary>
        private const double MaxPullSpeed = 6.0;

        /// <summary>How fast the item turns to how it is held, per second of angle, and the fastest it turns, radians a second.</summary>
        private const double TurnRate = 20.0, MaxSpin = 20.0;

        /// <summary>Items this light turn with the hand at full rate; heavier ones turn slower in proportion, kilograms.</summary>
        private const float TurnLightMass = 100f;

        /// <summary>A throw is the arm's push over this long, seconds: the most a release can change the item's velocity is that much of its acceleration.</summary>
        private const float ThrowSeconds = 0.1f;

        /// <summary>The hand holds an item no further from its centre than this share of its bounding radius (a hand that reached for it pulls it in).</summary>
        private const double HoldInside = 0.8;

        /// <summary>Frames the hand's velocity is taken over (a sixtieth of a second each).</summary>
        private const int VelocityFrames = 4;

        /// <summary>
        /// The chest, in the tracking space, from the head: this far below the eyes and this far in front of them (level
        /// with the head's heading), metres; a hand let go within <see cref="ChestRadius"/> of it puts the item away.
        /// </summary>
        public const float ChestBelowEyes = 0.35f, ChestForward = 0.10f, ChestRadius = 0.25f;

        /// <summary>The haptics: a tick when a hand comes in reach of an item, a pulse on a grab, one on putting it away.</summary>
        private const float TickAmplitude = 0.15f, TickSeconds = 0.015f, GrabAmplitude = 0.5f, GrabSeconds = 0.04f, StowAmplitude = 0.7f, StowSeconds = 0.06f;

        /// <summary>A release pulse grows from the first figures at rest to the second at <see cref="ThrowFullSpeed"/> (metres a second, relative to the body).</summary>
        private const float ReleaseMinAmplitude = 0.1f, ReleaseMaxAmplitude = 0.9f, ReleaseMinSeconds = 0.02f, ReleaseMaxSeconds = 0.08f, ThrowFullSpeed = 6f;

        private const float Step = MyEngineConstants.UPDATE_STEP_SIZE_IN_SECONDS;

        // ---- State ----

        private sealed class HandHold
        {
            /// <summary>The item this hand holds, or null.</summary>
            public MyFloatingObject Item;

            /// <summary>The item's pose in the grip's frame (row vectors): where the hand holds it.</summary>
            public MatrixD ItemInGrip;

            /// <summary>The item's centre of mass in its own frame.</summary>
            public Vector3D ComLocal;

            public float Mass;
            public string What;
            public long TracedAt;

            /// <summary>The item the taker found for the press now being decided.</summary>
            public MyFloatingObject Pending;

            /// <summary>The item last in reach, for the tick (null: none).</summary>
            public MyFloatingObject InReach;

            /// <summary>The grip point's world positions, newest last, with the simulation frame of each.</summary>
            public readonly Vector3D[] Positions = new Vector3D[VelocityFrames + 1];
            public readonly ulong[] Frames = new ulong[VelocityFrames + 1];
            public int Count;

            public void Forget()
            {
                Item = null;
                Pending = null;
                InReach = null;
                Count = 0;
            }

            public void Push(Vector3D position, ulong frame)
            {
                if (Count > 0 && frame != Frames[Count - 1] + 1)
                    Count = 0; // a gap (a pause, a menu, a frame this hand was not placed): the old positions say nothing now
                if (Count == Positions.Length)
                {
                    Array.Copy(Positions, 1, Positions, 0, Count - 1);
                    Array.Copy(Frames, 1, Frames, 0, Count - 1);
                    Count--;
                }
                Positions[Count] = position;
                Frames[Count] = frame;
                Count++;
            }

            /// <summary>The grip point's world velocity over the frames kept, metres a second.</summary>
            public Vector3 Velocity()
            {
                if (Count < 2)
                    return Vector3.Zero;
                return (Vector3)((Positions[Count - 1] - Positions[0]) / ((Frames[Count - 1] - Frames[0]) * (double)Step));
            }
        }

        /// <summary>An item let go of, followed for a second in the trace.</summary>
        private struct Flight
        {
            public MyFloatingObject Item;
            public string What;
            public Hand Hand;
            public long At, TracedAt;
        }

        /// <summary>A put-away whose outcome is logged a few frames later (the pick-up is an event the server runs).</summary>
        private struct Stow
        {
            public MyFloatingObject Item;
            public string What;
            public Hand Hand;
            public MyDefinitionId Content;
            public MyFixedPoint Before;
            public ulong Frame;
        }

        private static readonly HandHold[] hands = { new HandHold(), new HandHold() };
        private static readonly Hand[] bothHands = { Hand.Left, Hand.Right };
        private static readonly List<MyEntity> nearby = new List<MyEntity>();
        private static readonly List<Flight> flights = new List<Flight>();
        private static readonly List<Stow> stows = new List<Stow>();

        private static bool hooked;
        private static int errors, takerErrors;

        // ---- Hooks ----

        public static void Patch(Harmony harmony)
        {
            try
            {
                harmony.Patch(AccessTools.Method(typeof(MyCharacter), nameof(MyCharacter.UpdateAfterSimulation)),
                    postfix: new HarmonyMethod(typeof(ItemGrab), nameof(AfterSimulation)));
                GripClaim.Register(Name, Priority, Take);
                hooked = true;
                Log.Info("Items: floating items can be grabbed, thrown and put away by hand");
            }
            catch (Exception e)
            {
                hooked = false;
                Log.Error(e, "Items: grabbing floating items could not be hooked; a grip near one means Use");
            }
        }

        /// <summary>Whether a hand may grab now: on foot, the game screen in focus, not up a ladder, and not the hand that holds a tool or the builder.</summary>
        private static bool MayGrab(MyCharacter character, Hand hand)
        {
            if (Off || !hooked || !VRSettings.GrabItems || !VRInput.Active || !HandTools.IsLocalOnFoot(character) || character.Closed
                || character.IsOnLadder || character.PositionComp == null || !(MyScreenManager.GetScreenWithFocus() is MyGuiScreenGamePlay))
                return false;
            bool building = (MyCubeBuilder.Static != null && MyCubeBuilder.Static.IsActivated)
                            || (MyClipboardComponent.Static != null && MyClipboardComponent.Static.IsActive);
            return !((character.CurrentWeapon != null || building) && hand == VRSettings.DominantHand);
        }

        /// <summary>The taker (<see cref="GripClaim"/>): this press is ours when the hand is on an item right now.</summary>
        private static bool Take(Hand hand)
        {
            HandHold state = hands[(int)hand];
            state.Pending = null;
            try
            {
                MyCharacter character = MySession.Static?.LocalCharacter;
                if (!MayGrab(character, hand) || !HandWorld.TryGetNow(character, hand, out MatrixD grip, out _, out _))
                    return false;
                state.Pending = Nearest(grip.Translation, GrabReach, out _);
                return state.Pending != null;
            }
            catch (Exception e)
            {
                if (takerErrors++ < 3)
                    Log.Error(e, "Items: deciding a grab failed");
                return false;
            }
        }

        /// <summary>Game thread, each simulation frame, after the character's own update (the physics has stepped).</summary>
        private static void AfterSimulation(MyCharacter __instance)
        {
            if (errors >= 3 || !hooked || __instance != MySession.Static?.LocalCharacter)
                return;
            try
            {
                ulong frame = MySandboxGame.Static.SimulationFrameCounter;
                FollowUp(frame);
                bool live = !Off && VRSettings.GrabItems && VRInput.Active && HandTools.IsLocalOnFoot(__instance) && !__instance.Closed
                            && __instance.PositionComp != null;
                if (!live || !HandWorld.TryGetTrackingToBody(__instance, out MatrixD trackingToModel, out _))
                {
                    LetGoAll(live ? "the hand is not placed" : "grabbing is off or the player is not on foot");
                    return;
                }
                MatrixD trackingToWorld = trackingToModel * __instance.PositionComp.WorldMatrixRef;
                foreach (Hand hand in bothHands)
                    StepHand(hand, __instance, trackingToWorld, frame);
            }
            catch (Exception e)
            {
                if (errors++ < 3)
                    Log.Error(e, "Items: the hold failed" + (errors >= 3 ? "; grabbing stays off for this run" : string.Empty));
                if (errors >= 3)
                    LetGoAll("an error");
            }
        }

        private static void StepHand(Hand hand, MyCharacter character, in MatrixD trackingToWorld, ulong frame)
        {
            HandHold state = hands[(int)hand];
            HandState input = VRInput.Get(hand);
            if (!input.Tracked)
            {
                LetGo(hand, "the hand is not tracked");
                state.Forget();
                return;
            }
            MatrixD grip = (MatrixD)input.Grip * trackingToWorld;
            state.Push(grip.Translation, frame);

            bool ours = GripClaim.Holds(hand, Name);
            if (state.Item != null)
            {
                if (!ours)
                    Release(hand, character, input);
                else if (Gone(state.Item))
                {
                    Note($"Items: {Side(hand)} hand lost {state.What}: it went away (picked up, merged or destroyed)");
                    GripClaim.Release(hand, Name);
                    state.Item = null;
                }
                else
                    Hold(hand, state, character, grip);
                return;
            }

            if (ours)
            {
                MyFloatingObject item = state.Pending;
                state.Pending = null;
                if (item == null || Gone(item))
                    GripClaim.Release(hand, Name);
                else
                    Grab(hand, state, item, grip);
                return;
            }

            // A free hand: a tick as it comes in reach of an item.
            if (!MayGrab(character, hand))
            {
                state.InReach = null;
                return;
            }
            MyFloatingObject near = Nearest(grip.Translation, GrabReach + (state.InReach != null ? ReachHysteresis : 0.0), out _);
            if (near != null && near != state.InReach)
                Haptics.Pulse(hand, TickAmplitude, TickSeconds);
            state.InReach = near;
            if (Trace)
                TraceNearest(hand, state, grip.Translation, trackingToWorld);
        }

        /// <summary>
        /// The trace, once a second for a free hand: the nearest item within 3 m, and where its centre is in the tracking
        /// space (the coordinates the hands are given in, so a test can put a hand on it).
        /// </summary>
        private static void TraceNearest(Hand hand, HandHold state, Vector3D point, in MatrixD trackingToWorld)
        {
            long ticks = Stopwatch.GetTimestamp();
            if (ticks - state.TracedAt < Stopwatch.Frequency)
                return;
            state.TracedAt = ticks;
            MyFloatingObject item = Nearest(point, 3.0, out double distance);
            if (item == null)
            {
                Log.Info($"Items trace: no floating item within 3 m of the {Side(hand)} hand");
                return;
            }
            Vector3D centre = Vector3D.Transform(item.PositionComp.WorldAABB.Center, MatrixD.Invert(trackingToWorld));
            Log.Info($"Items trace: nearest the {Side(hand)} hand is {Describe(item)} ({item.Physics.Mass:F0} kg), {distance:F2} m from its box, " +
                     $"its centre at tracking ({centre.X:F2}, {centre.Y:F2}, {centre.Z:F2})");
        }

        // ---- Grab, hold, release ----

        private static void Grab(Hand hand, HandHold state, MyFloatingObject item, in MatrixD grip)
        {
            // The other hand lets go of it: one item, one holder.
            HandHold other = hands[1 - (int)hand];
            if (other.Item == item)
            {
                Hand otherHand = (Hand)(1 - (int)hand);
                Note($"Items: {Side(otherHand)} hand passes {other.What} to the {Side(hand)} hand");
                GripClaim.Release(otherHand, Name);
                other.Item = null;
            }

            MyPhysicsComponentBase physics = item.Physics;
            MatrixD itemWorld = item.PositionComp.WorldMatrixRef;
            Vector3D com = physics.CenterOfMassWorld;
            Vector3D comLocal = Vector3D.Transform(com, item.PositionComp.WorldMatrixNormalizedInv);
            double radius = item.PositionComp.LocalAABB.HalfExtents.Length();
            MatrixD itemInGrip = HoldPose(itemWorld, comLocal, grip, radius, out double away, out double limit);

            state.Item = item;
            state.ItemInGrip = itemInGrip;
            state.ComLocal = comLocal;
            state.Mass = Math.Max(physics.Mass, 0.1f);
            state.What = Describe(item);
            state.TracedAt = 0;
            state.InReach = item;
            if (!physics.IsActive)
                physics.Activate();
            Haptics.Pulse(hand, GrabAmplitude, GrabSeconds);
            Note($"Items: {Side(hand)} hand grabbed {state.What} ({state.Mass:F1} kg), its centre {away:F2} m from the grip" +
                 (away > limit ? $", held at {limit:F2} m" : string.Empty));
        }

        /// <summary>One frame of the servo: the item gets the hand's velocity plus a pull toward where the hand holds it, as far as an arm could give it that.</summary>
        private static void Hold(Hand hand, HandHold state, MyCharacter character, in MatrixD grip)
        {
            MyFloatingObject item = state.Item;
            MyPhysicsComponentBase physics = item.Physics;
            MatrixD target = state.ItemInGrip * grip;
            Vector3D com = physics.CenterOfMassWorld;
            Vector3D error = Vector3D.Transform(state.ComLocal, target) - com;
            double distance = error.Length();
            if (distance > BreakDistance || double.IsNaN(distance))
            {
                Note($"Items: {Side(hand)} hand lost {state.What}: it is {distance:F2} m from the hand (stopped by something, or too heavy to follow)");
                GripClaim.Release(hand, Name);
                state.Item = null;
                return;
            }
            if (!physics.IsActive)
                physics.Activate();

            Vector3 handVelocity = state.Velocity();
            Vector3D gravity = MyGravityProviderSystem.CalculateTotalGravityInPoint(com);
            physics.LinearVelocity = (Vector3)Servo(physics.LinearVelocity, error, handVelocity, gravity, state.Mass);

            // Turning: toward how it is held, at a rate that drops for heavy items.
            MatrixD current = item.PositionComp.WorldMatrixRef;
            current.Translation = Vector3D.Zero;
            MatrixD wantedTurn = target;
            wantedTurn.Translation = Vector3D.Zero;
            Vector3 spin = Turn(MatrixD.Transpose(current) * wantedTurn, TurnRate * Math.Min(1.0, TurnLightMass / state.Mass));
            physics.AngularVelocity = spin;

            long ticks = Stopwatch.GetTimestamp();
            if (Trace && ticks - state.TracedAt > Stopwatch.Frequency / 4)
            {
                state.TracedAt = ticks;
                Log.Info($"Items trace: {Side(hand)} holds {state.What}: {distance:F3} m from where it is held, {Vector3D.Distance(com, grip.Translation):F3} m from the grip; " +
                         $"item {physics.LinearVelocity.Length():F2} m/s, hand {handVelocity.Length():F2} m/s, body {BodySpeed(character):F2} m/s; " +
                         $"turn {MathHelper.ToDegrees(Angle(MatrixD.Transpose(current) * wantedTurn)):F0} deg");
            }
        }

        /// <summary>The hand opened: a throw at the hand's velocity, or, at the chest, into the inventory.</summary>
        private static void Release(Hand hand, MyCharacter character, in HandState input)
        {
            HandHold state = hands[(int)hand];
            MyFloatingObject item = state.Item;
            state.Item = null;
            if (item == null || Gone(item))
                return;
            MyPhysicsComponentBase physics = item.Physics;
            Vector3 body = character.Physics?.LinearVelocity ?? Vector3.Zero;

            if (GameHead.Take() && AtChest(GameHead.TrackingPose, input.Grip.Translation))
            {
                PutAway(hand, state, character, item, body);
                return;
            }

            Vector3 handVelocity = state.Velocity();
            physics.LinearVelocity = ThrowVelocity(physics.LinearVelocity, handVelocity, state.Mass);
            if (!physics.IsActive)
                physics.Activate();

            float relative = (physics.LinearVelocity - body).Length();
            float share = MathHelper.Clamp(relative / ThrowFullSpeed, 0f, 1f);
            Haptics.Pulse(hand, MathHelper.Lerp(ReleaseMinAmplitude, ReleaseMaxAmplitude, share), MathHelper.Lerp(ReleaseMinSeconds, ReleaseMaxSeconds, share));
            Note($"Items: {Side(hand)} hand let go of {state.What}: item {physics.LinearVelocity.Length():F2} m/s, hand {handVelocity.Length():F2} m/s " +
                 $"(world), {relative:F2} m/s relative to the body");
            if (Trace)
                flights.Add(new Flight { Item = item, What = state.What, Hand = hand, At = Stopwatch.GetTimestamp() });
        }

        /// <summary>The game's own pick-up, as its key does it: the item's Use with the pick-up action.</summary>
        private static void PutAway(Hand hand, HandHold state, MyCharacter character, MyFloatingObject item, Vector3 body)
        {
            // Let go at the body's own velocity first: an item that does not fit stays at the chest instead of flying off.
            item.Physics.LinearVelocity = body;
            item.Physics.AngularVelocity = Vector3.Zero;
            MyInventory inventory = character.GetInventory();
            MyDefinitionId content = item.Item.Content.GetId();
            MyFixedPoint before = inventory?.GetItemAmount(content) ?? 0;
            MyFixedPoint fits = inventory?.ComputeAmountThatFits(content) ?? 0;
            ((IMyUseObject)item).Use(UseActionEnum.PickUp, character);
            Haptics.Pulse(hand, StowAmplitude, StowSeconds);
            Note($"Items: {Side(hand)} hand put {state.What} away at the chest: {before} in the inventory, room for {fits}");
            stows.Add(new Stow { Item = item, What = state.What, Hand = hand, Content = content, Before = before, Frame = MySandboxGame.Static.SimulationFrameCounter });
        }

        /// <summary>Lets go of a held item without a throw (the hand was lost, grabbing turned off, the player left their feet).</summary>
        private static void LetGo(Hand hand, string why)
        {
            HandHold state = hands[(int)hand];
            if (state.Item == null)
                return;
            Note($"Items: {Side(hand)} hand dropped {state.What}: {why}");
            GripClaim.Release(hand, Name);
            state.Item = null;
        }

        private static void LetGoAll(string why)
        {
            foreach (Hand hand in bothHands)
            {
                LetGo(hand, why);
                hands[(int)hand].Forget();
            }
        }

        /// <summary>The logs that come after the fact: a put-away's inventory a few frames on, a thrown item's flight.</summary>
        private static void FollowUp(ulong frame)
        {
            for (int i = stows.Count - 1; i >= 0; i--)
            {
                Stow stow = stows[i];
                if (frame < stow.Frame + 3)
                    continue;
                stows.RemoveAt(i);
                MyFixedPoint after = MySession.Static?.LocalCharacter?.GetInventory()?.GetItemAmount(stow.Content) ?? 0;
                Note($"Items: {Side(stow.Hand)} hand's {stow.What} put away: inventory {stow.Before} -> {after}, " +
                     (Gone(stow.Item) ? "the item is gone from the world" : $"{stow.Item.Item.Amount} left floating"));
            }
            if (flights.Count == 0)
                return;
            long ticks = Stopwatch.GetTimestamp();
            for (int i = flights.Count - 1; i >= 0; i--)
            {
                Flight flight = flights[i];
                double since = (ticks - flight.At) / (double)Stopwatch.Frequency;
                if (since > 1.1 || Gone(flight.Item) || !Trace)
                {
                    flights.RemoveAt(i);
                    continue;
                }
                if (ticks - flight.TracedAt < Stopwatch.Frequency / 4)
                    continue;
                flight.TracedAt = ticks;
                flights[i] = flight;
                Vector3D? grip = HandWorld.TryGetNow(MySession.Static?.LocalCharacter, flight.Hand, out MatrixD g, out _, out _) ? g.Translation : (Vector3D?)null;
                Log.Info($"Items trace: {flight.What} let go {since:F2} s ago: item {flight.Item.Physics.LinearVelocity.Length():F2} m/s" +
                         (grip.HasValue ? $", {Vector3D.Distance(flight.Item.PositionComp.GetPosition(), grip.Value):F2} m from the {Side(flight.Hand)} hand" : string.Empty));
            }
        }

        // ---- Geometry ----

        /// <summary>The floating item nearest the point within <paramref name="reach"/> of its box (null: none).</summary>
        private static MyFloatingObject Nearest(Vector3D point, double reach, out double distance)
        {
            distance = double.MaxValue;
            MyFloatingObject best = null;
            nearby.Clear();
            var sphere = new BoundingSphereD(point, reach + 0.01); // an item within reach of its box has its world box within reach too
            MyGamePruningStructure.GetAllTopMostEntitiesInSphere(ref sphere, nearby);
            foreach (MyEntity entity in nearby)
            {
                if (!(entity is MyFloatingObject item) || Gone(item) || item.m_holdingLandingGears.Count > 0)
                    continue;
                double d = DistanceToBox(item, point);
                if (d <= reach && d < distance)
                {
                    distance = d;
                    best = item;
                }
            }
            nearby.Clear();
            return best;
        }

        /// <summary>Metres from the point to the item's box (0 inside it).</summary>
        internal static double DistanceToBox(MyFloatingObject item, Vector3D point)
        {
            Vector3D local = Vector3D.Transform(point, item.PositionComp.WorldMatrixNormalizedInv);
            BoundingBox box = item.PositionComp.LocalAABB;
            Vector3D inside = Vector3D.Clamp(local, box.Min, box.Max);
            return Vector3D.Distance(local, inside);
        }

        /// <summary>
        /// The hand is at the chest: in the tracking space (the head's pose and the grip's position, as tracked), near a
        /// point below the eyes and a little in front of them, level with the head's heading.
        /// </summary>
        internal static bool AtChest(in Matrix head, Vector3 grip)
        {
            Vector3 forward = head.Forward;
            forward.Y = 0f;
            forward = forward.LengthSquared() > 1e-6f ? Vector3.Normalize(forward) : Vector3.Forward;
            Vector3 chest = head.Translation + Vector3.Down * ChestBelowEyes + forward * ChestForward;
            return Vector3.Distance(grip, chest) <= ChestRadius;
        }

        /// <summary>
        /// Where the hand holds an item, as its pose in the grip's frame (row vectors): where it was when taken, but with its
        /// centre of mass no further from the grip than <see cref="HoldInside"/> of its radius (a hand that reached for it
        /// pulls it in). <paramref name="away"/> is how far the centre was, <paramref name="limit"/> how far it may be.
        /// </summary>
        internal static MatrixD HoldPose(in MatrixD itemWorld, Vector3D comLocal, in MatrixD grip, double radius, out double away, out double limit)
        {
            MatrixD itemInGrip = itemWorld * MatrixD.Invert(grip);
            Vector3D comInGrip = Vector3D.Transform(comLocal, itemInGrip);
            away = comInGrip.Length();
            limit = Math.Max(0.02, radius * HoldInside);
            if (away > limit)
                itemInGrip.Translation += comInGrip * (limit / away - 1.0);
            return itemInGrip;
        }

        /// <summary>
        /// The servo's velocity for a held item this frame: the hand's velocity plus a pull toward where it is held
        /// (<paramref name="error"/>: from its centre of mass to there), the pull no faster than it can stop in, and the whole
        /// change no more than the arm can give it in a frame (<see cref="ArmForce"/> over <paramref name="mass"/>). The
        /// coming step adds <paramref name="gravity"/>'s pull, so the arm holds against it out of the same force.
        /// </summary>
        internal static Vector3D Servo(Vector3D now, Vector3D error, Vector3 hand, Vector3D gravity, float mass)
        {
            double reach = ArmForce / Math.Max(mass, 0.1f); // the arm's acceleration on this item, m/s^2
            double distance = error.Length();
            double pull = Math.Min(PullRate * distance, Math.Min(MaxPullSpeed, StoppingSpeed(reach, distance)));
            Vector3D wanted = hand + (distance > 1e-6 ? error * (pull / distance) : Vector3D.Zero);
            Vector3D change = wanted - gravity * Step - now;
            double most = reach * Step, size = change.Length();
            if (size > most)
                change *= most / size;
            return now + change;
        }

        /// <summary>
        /// The fastest an item can close <paramref name="distance"/> and still stop on it, slowing by <paramref name="accel"/>
        /// in steps of a frame: k frames at k, k-1, ... 1 times the step's change cover k(k+1)/2 of them.
        /// </summary>
        internal static double StoppingSpeed(double accel, double distance)
        {
            double perFrame = accel * Step;
            return perFrame * (Math.Sqrt(2.0 * distance / (perFrame * Step) + 0.25) - 0.5);
        }

        /// <summary>A released item's velocity: the hand's, as far as the arm could push it there in <see cref="ThrowSeconds"/>.</summary>
        internal static Vector3 ThrowVelocity(Vector3 now, Vector3 hand, float mass)
        {
            Vector3 change = hand - now;
            float most = ArmForce / Math.Max(mass, 0.1f) * ThrowSeconds, size = change.Length();
            if (size > most)
                change *= most / size;
            return now + change;
        }

        /// <summary>
        /// The spin that turns by <paramref name="turn"/> (a rotation of world vectors, row vectors: v * turn) at
        /// <paramref name="rate"/> of its angle a second, radians a second, world axes.
        /// </summary>
        internal static Vector3 Turn(in MatrixD turn, double rate)
        {
            Quaternion q = Quaternion.CreateFromRotationMatrix((Matrix)turn);
            if (q.W < 0f)
                q = Quaternion.Negate(q);
            double half = Math.Acos(MathHelper.Clamp(q.W, -1f, 1f));
            double sin = Math.Sin(half);
            if (sin < 1e-5)
                return Vector3.Zero;
            var axis = new Vector3D(q.X, q.Y, q.Z) / sin;
            Vector3D spin = axis * (2.0 * half * rate);
            double size = spin.Length();
            if (size > MaxSpin)
                spin *= MaxSpin / size;
            return (Vector3)spin;
        }

        /// <summary>The angle of a rotation, radians.</summary>
        private static float Angle(in MatrixD turn)
        {
            Quaternion q = Quaternion.CreateFromRotationMatrix((Matrix)turn);
            return 2f * (float)Math.Acos(MathHelper.Clamp(Math.Abs(q.W), 0f, 1f));
        }

        // ---- Words ----

        private static bool Gone(MyFloatingObject item) =>
            item == null || item.Closed || item.MarkedForClose || item.WasRemovedFromWorld || item.Physics == null || !item.Physics.Enabled || item.PositionComp == null;

        private static float BodySpeed(MyCharacter character) => character?.Physics?.LinearVelocity.Length() ?? 0f;

        private static string Side(Hand hand) => hand == Hand.Left ? "left" : "right";

        private static string Describe(MyFloatingObject item)
        {
            MyDefinitionId id = item.Item.Content.GetId();
            string type = id.TypeId.ToString();
            if (type.StartsWith("MyObjectBuilder_", StringComparison.Ordinal))
                type = type.Substring("MyObjectBuilder_".Length);
            return $"{type}/{id.SubtypeName} x{item.Item.Amount}";
        }

        // ---- The log: one line per grab and release, at most a few a second ----

        private static long noteWindow;
        private static int notesInWindow, notes;

        private static void Note(string line)
        {
            long now = Stopwatch.GetTimestamp();
            if (now - noteWindow > Stopwatch.Frequency)
            {
                noteWindow = now;
                notesInWindow = 0;
            }
            if (notesInWindow >= 6 || notes >= 1000)
                return;
            notesInWindow++;
            if (++notes == 1000)
                line += " (the last line of these this run)";
            Log.Info(line);
        }
    }
}
