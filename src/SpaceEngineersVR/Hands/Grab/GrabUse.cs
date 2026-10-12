using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;
using VRage.Game.Entity;
using VRage.Game.Entity.UseObject;
using VRage.ModAPI;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// Use things by reaching out and taking hold of them. A grip that starts with the hand on anything the Use key works
    /// on (a seat, a door or hatch, a terminal, a screen, cargo, a medical room, a store, a farm plot, ...) is that thing's
    /// Use: the game's own, on that object, the same as the Use key does on what the ray points at. On foot, and from a
    /// seat for what is within arm's reach of it. A grip in empty air keeps its old meaning (Use along the hand's ray on
    /// the right hand on foot, the toolbar wheel on the left; in a seat the ship's controls), so the ray Use only happens
    /// when nothing is within reach of the hand. Either hand can grab.
    /// </summary>
    /// <remarks>
    /// <para>Who owns the grip: <see cref="GripClaim"/>. <see cref="Take"/> is the taker (priority 30, after the helmet and
    /// floating items, before the hull and ladder grab and before the colour tool): it is asked when a
    /// grip is pressed and says yes when the palm is within <see cref="Reach"/> of something this takes. Then the grip is this
    /// feature's until it is let go, and the Use key is not also pressed by the right hand, nor does the grip fly the ship in
    /// a seat. A hand that holds a tool or a weapon does not grab; a block in hand is not a tool
    /// (<see cref="FingerPress.IsHandTool"/>), so with a block in hand the right grip on a cargo container, a terminal, a
    /// door or a seat uses it as on foot (build mode does not claim the grip: the left stick click turns the block).</para>
    /// <para>What it takes: the game keeps every use object as a detector box in its block's space
    /// (<see cref="MyUseObjectsComponent.DetectorInteractiveObjects"/>), which is what <see cref="FingerPress"/> reads
    /// too; the box nearest the palm that is taken wins. Every kind is taken (<see cref="Classify"/>) except a short list,
    /// each for a reason: the kinds other features own (the ladder: the ladder grab; the push-buttons: the fingertip), and
    /// from a seat, the other seats and pods (the game seats a standing character, not one already seated) and the
    /// wardrobe (it re-dresses the body standing in the medical room). A test aid (renderdebug GrabAll=0) restricts it to
    /// the allow-list of before (seat, cryo pod, airtight and advanced doors, medical room and survival kit, cargo, terminal; on foot only).</para>
    /// <para>How each is used (<see cref="Kind"/>): most things the moment the hand closes on them. A door, a hatch, a
    /// hangar door and a maintenance panel are pulled or pushed: used once the held hand has moved <see cref="DoorPull"/>
    /// from where it took hold (in the door's own frame, so a ship's motion does not count), so a stray grip does not slam
    /// it. A use that spends money (a vending machine's Buy) needs the hand held on it for <see cref="DeliberateSeconds"/>.
    /// A use that is held (a medical room's healing, <c>ContinuousUsage</c>) goes on every frame while the hand stays on
    /// it, and is finished when the hand moves away or lets go, as the key's hold and release do. The right grip that goes down
    /// at a container (<see cref="QuickInventory.HoldsBack"/>: the hand points at one or rests on one) keeps its claim but waits:
    /// the use is due as above and done when the grip is let go, once, unless a deposit was taken meanwhile (the stick click with
    /// the grip held), which drops it (<see cref="UseOrWait"/>, <see cref="LetGo"/>).</para>
    /// <para>In a seat (first person, the seat's own view): the seat itself and everything else of the seat's own block
    /// are left out, so a grip on the armrest does nothing and standing up stays on its button. A grip anywhere that is not
    /// on a use object is the seat's: the hand flying and the roll.</para>
    /// <para>Line of reach (<see cref="LineOfReach"/>): a use object is taken only when nothing solid lies between the arm's
    /// shoulder (the eyes before the arms are known) and the palm, other than the target's own block and the seat, so a
    /// real hand pushed through a wall or a cockpit's side does not use what is behind it. The line is swept with a 1 cm
    /// ball the way <see cref="SurfaceStop"/> sweeps the drawn hands (the character collision layer; solid is a grid that is
    /// not a projection, or voxels). What is blocked is not highlighted and gets no grip claim, so the hull grab or nothing
    /// takes that grip. RenderDebug GrabUseTrace=1 logs each grip's line: clear, or the block in the way.</para>
    /// <para>The use is the game's: <c>MyCharacter.Use()</c> with the character's detector answering with the grabbed
    /// object for that call (the same seam <see cref="FingerPress"/> uses), so the sound, the access check, the multiplayer
    /// request and the screens are the game's. <c>Use()</c> does not look at whether the character sits; in a seat the
    /// game's own detection is off, so only this answers.</para>
    /// <para>Feedback: within reach of something that can be taken, the game's outline highlight is put on it and the
    /// hand gets a faint tick when it first comes into reach; a click when a door or a paid use is taken, and a stronger
    /// pulse with the use itself. On foot the game's own detection clears the outline; in a seat this does.</para>
    /// </remarks>
    internal static partial class GrabUse
    {
        /// <summary>The name the grip claim is held under.</summary>
        public const string ClaimName = "GrabUse";

        /// <summary>After the helmet (10) and floating items (20), before the hull and ladder grab (40).</summary>
        public const int ClaimPriority = 30;

        /// <summary>Debug: no grab to use (<see cref="Rendering.RenderDebug"/> GrabUse=0).</summary>
        public static bool Off { get; set; }

        /// <summary>Debug: log each grip's line of reach, clear or what blocked it (<see cref="Rendering.RenderDebug"/> GrabUseTrace=1).</summary>
        public static bool Trace { get; set; }

        /// <summary>A shoulder farther than this from the palm is stale (the arms not yet known); the eyes are the origin then, metres.</summary>
        private const double MaxShoulderToPalm = 1.5;

        private const int MaxTraceLogs = 400;

        /// <summary>The palm is on a use object when it is inside its detector box or this close to it, metres.</summary>
        public const double UseRadius = 0.15;

        /// <summary>A held door is used after the hand has moved this far from where it took hold, metres.</summary>
        public const double DoorPull = 0.12;

        /// <summary>A use that costs something is used after the hand has held it this long, seconds.</summary>
        public const double DeliberateSeconds = 0.6;

        /// <summary>A held use (a medical room's) goes on, and a paid one stays armed, while the palm is within this of the box, metres.</summary>
        public const double HoldReach = 0.3;

        /// <summary>How wide the game's block query looks around the palm; a detector can reach out of its block's cells (a door's, a seat's).</summary>
        public const double GatherRadius = 0.6;

        /// <summary>A use object this near the palm, taken or not, is kept for the log of what the hand is close to.</summary>
        private const double NearLogDistance = 0.6;

        /// <summary>The faint tick when the palm comes into reach, a click when a door is taken, the pulse of a use.</summary>
        public const float TickAmplitude = 0.2f, TickSeconds = 0.02f;
        public const float TakeAmplitude = 0.35f, TakeSeconds = 0.03f;
        public const float UseAmplitude = 0.6f, UseSeconds = 0.05f;

        private const double TickEverySeconds = 0.4;
        private const double GapSeconds = 0.3;
        private const int HoverEveryFrames = 3;
        private const int MaxLogsPerSecond = 6;
        private const int MaxLogs = 600;
        private const int MaxNearLogs = 200;
        private static readonly double NearLogEvery = 1.5 * Stopwatch.Frequency;

        /// <summary>What kind of grab a use object is.</summary>
        internal enum Kind
        {
            /// <summary>Not taken.</summary>
            None,

            /// <summary>Used as the hand closes (a held use goes on while the hand stays on it).</summary>
            Device,

            /// <summary>Pulled or pushed: used once the held hand has moved <see cref="DoorPull"/>.</summary>
            Door,

            /// <summary>Costs something: used once the hand has stayed on it for <see cref="DeliberateSeconds"/>.</summary>
            Deliberate,
        }

        /// <summary>Where the player is, for what the hands may reach.</summary>
        internal enum Place
        {
            /// <summary>No grabbing: dead, a camera block, a remote control, a turret, a seat seen from outside.</summary>
            None,

            OnFoot,

            /// <summary>In a seat, seen from it: what is within arm's reach, except the seat's own block.</summary>
            Seated,
        }

        // The use objects, by the type's name (some of the types are internal to the game, so they are not named in code).
        // Every kind of the game (SE 1, Sandbox.Game and SpaceEngineers.Game) and what it does here:
        //   door (MyUseObjectAirtightDoors: airtight hangar doors), advanceddoor (MyUseObjectAdvancedDoorTerminal),
        //   a door's terminal detector (MyUseObjectDoorTerminal: the sliding door), maintenance (MyUseObjectMaintenancePanel:
        //   a panel that opens and closes)                                                     -> pulled or pushed
        //   vendingMachineBuy (MyUseObjectVendingMachineBuy: spends money)                     -> held DeliberateSeconds
        //   ladder (MyUseObjectLadder)                                                         -> left to the ladder grab
        //   panel, jukeboxNext/Previous/Pause, vendingMachineNext/Previous                     -> left to the fingertip
        //   cockpit, cryopod (another seat), wardrobe                                          -> on foot only
        //   cockpit, cryopod, block (medical room, survival kit: held), inventory and conveyor (cargo, connectors, ports),
        //   terminal, textpanel (LCDs), ATM, store, contract, services, billboard, vendingMachine (its screen), jukebox,
        //   wardrobe, farmplot (harvests, plants the seed in hand, or opens it, as the game picks), forageable (picks it),
        //   and any kind a mod adds                                                            -> used as the hand closes
        private static readonly HashSet<string> DoorTypes = new HashSet<string>
        {
            "MyUseObjectAirtightDoors",
            "MyUseObjectAdvancedDoorTerminal",
            "MyUseObjectDoorTerminal",
            "MyUseObjectMaintenancePanel",
        };

        private static readonly HashSet<string> DeliberateTypes = new HashSet<string>
        {
            "MyUseObjectVendingMachineBuy",
        };

        /// <summary>Kinds another feature owns, and whose.</summary>
        private static readonly Dictionary<string, string> Owned = new Dictionary<string, string>
        {
            ["MyUseObjectLadder"] = "a ladder: the ladder grab's",
        };

        /// <summary>Kinds not used from a seat, and why.</summary>
        private static readonly Dictionary<string, string> NotSeated = new Dictionary<string, string>
        {
            ["MyUseObjectCockpitDoor"] = "another seat: the game seats a standing character",
            ["MyUseObjectCryoChamberDoor"] = "a cryo pod: the game seats a standing character",
            ["MyUseObjectWardrobe"] = "a wardrobe: the game re-dresses a standing character",
        };

        internal static Kind Classify(IMyUseObject use, bool seated) => Classify(use, seated, out _);

        /// <summary>
        /// A test aid (renderdebug GrabAll=0) puts back the allow-list of before: <paramref name="listed"/>, and how the use
        /// object of type <paramref name="name"/> is grabbed by that list, or why not.
        /// </summary>
        static partial void TestAidsOldList(string name, bool seated, ref bool listed, ref Kind kind, ref string why);

        /// <summary>A test aid (renderdebug GrabAll=0) keeps grabbing to the character on foot, as the allow-list of before did.</summary>
        static partial void TestAidsSeatedGrab(ref bool allowed);

        /// <summary>How a use object is grabbed, and when it is not, why (for the log).</summary>
        internal static Kind Classify(IMyUseObject use, bool seated, out string why)
        {
            why = null;
            if (use == null)
                return Kind.None;
            string name = use.GetType().Name;
            bool listed = false;
            Kind kind = Kind.None;
            TestAidsOldList(name, seated, ref listed, ref kind, ref why);
            if (listed)
                return kind;
            if (FingerPress.IsPushButton(use))
            {
                why = "a push-button: the fingertip's";
                return Kind.None;
            }
            if (Owned.TryGetValue(name, out why))
                return Kind.None;
            if (use is MyFloatingObject || use is MyCharacter)
            {
                why = "not a block's";
                return Kind.None;
            }
            if (seated && NotSeated.TryGetValue(name, out why))
                return Kind.None;
            if (DoorTypes.Contains(name))
                return Kind.Door;
            return DeliberateTypes.Contains(name) ? Kind.Deliberate : Kind.Device;
        }

        /// <summary>A use object near the palm.</summary>
        private struct Candidate
        {
            public IMyUseObject Use;
            public Kind Kind;
            public double Distance, Center;
            public string Tag, Why;
            public Vector3D BoxCenter;
        }

        /// <summary>What a hand has hold of.</summary>
        private sealed class Held
        {
            public IMyUseObject Use;
            public Kind Kind;
            public string Tag;
            public double Distance;
            public Place Place;

            /// <summary>Where the palm took hold, in the owner's own frame (so a moving ship does not count as pulling).</summary>
            public Vector3D StartLocal;

            /// <summary>Fired: used (or, for a paid use, given up on as the hand moved off it).</summary>
            public bool Fired, Used, Continuing;

            /// <summary>The use is due but waits for the grip to be let go (<see cref="QuickInventory.HoldsBack"/>), and how it would have been said.</summary>
            public bool HeldBack;
            public string HeldBackHow;
            public long Since;
        }

        private static readonly Held[] held = new Held[2];
        private static readonly IMyUseObject[] hover = new IMyUseObject[2];
        private static readonly long[] tickedAt = new long[2];
        private static readonly long[] seenAt = new long[2];
        private static readonly long[] nearLoggedAt = new long[2];
        private static readonly Hand[] bothHands = { Hand.Left, Hand.Right };
        private static readonly List<MyEntity> nearby = new List<MyEntity>();
        private static readonly List<Candidate> candidates = new List<Candidate>();

        private static bool hooked, seatHighlight;
        private static Place lastPlace;
        private static readonly FeatureGuard guard = new FeatureGuard(nameof(GrabUse));
        private static int takeErrors, useErrors, highlightErrors, logs, nearLogs, frame;
        private static long logWindow;
        private static int logsInWindow;
        private static Action<IMyUseObject> highlight;
        private static Action<MyHudSelectedObject> removeHighlight;

        // ---- Game side ----

        public static void Patch(Harmony harmony)
        {
            try
            {
                PatchGame(harmony);
            }
            catch (Exception e)
            {
                hooked = false;
                PatchRegistry.Lost(e, "Grab to use could not be hooked; grabbing things will not use them");
                throw;
            }
        }

        private static void PatchGame(Harmony harmony)
        {
            // The use object the game acts on is whatever the character's detector component answers (see FingerPress).
            Type closest = AccessTools.TypeByName("Sandbox.Game.Entities.Character.Components.MyCharacterClosestDetectorComponent")
                           ?? throw new MissingMemberException("MyCharacterClosestDetectorComponent");
            var getters = new[]
            {
                AccessTools.PropertyGetter(typeof(MyCharacterDetectorComponent), nameof(MyCharacterDetectorComponent.UseObject))
                    ?? throw new MissingMemberException(nameof(MyCharacterDetectorComponent), "UseObject"),
                AccessTools.DeclaredProperty(closest, nameof(MyCharacterDetectorComponent.UseObject))?.GetGetMethod(true)
                    ?? throw new MissingMemberException(closest.Name, "UseObject"),
            };
            foreach (var getter in getters)
                harmony.Patch(getter, postfix: new HarmonyMethod(typeof(GrabUse), nameof(AnswerUseObject)));

            harmony.Patch(AccessTools.Method(typeof(MyCharacter), nameof(MyCharacter.UpdateAfterSimulation)),
                postfix: new HarmonyMethod(typeof(GrabUse), nameof(AfterSimulation)));

            // The game's own highlight of a use object (outline, colour by access); a missing one only costs the highlight.
            try
            {
                var method = AccessTools.Method(typeof(MyCharacterDetectorComponent), "HandleInteractiveObject", new[] { typeof(IMyUseObject) });
                highlight = (Action<IMyUseObject>)Delegate.CreateDelegate(typeof(Action<IMyUseObject>), method
                    ?? throw new MissingMethodException(nameof(MyCharacterDetectorComponent), "HandleInteractiveObject"));
            }
            catch (Exception e)
            {
                highlight = null;
                Log.Error(e, "Grab to use: the game's use-object highlight is not available; the hand only ticks");
            }
            try
            {
                // Internal to the game; in a seat nothing else takes the outline off.
                var method = AccessTools.Method(typeof(MyHudSelectedObject), "RemoveHighlight", Type.EmptyTypes);
                removeHighlight = (Action<MyHudSelectedObject>)Delegate.CreateDelegate(typeof(Action<MyHudSelectedObject>), method
                    ?? throw new MissingMethodException(nameof(MyHudSelectedObject), "RemoveHighlight"));
            }
            catch (Exception e)
            {
                removeHighlight = null;
                Log.Error(e, "Grab to use: the game's highlight cannot be taken off; in a seat no outline is put on");
            }

            hooked = true;
            GripClaim.Register(ClaimName, ClaimPriority, Take);
            Log.Info($"Grab to use: hooked (grip claim '{ClaimName}' at {ClaimPriority}, use radius {UseRadius * 100:0} cm, door pull {DoorPull * 100:0} cm, " +
                     $"paid use held {DeliberateSeconds:0.0} s, highlight {(highlight != null ? "on" : "off")})");
        }

        /// <summary>The use object the detector answers with while a grab is being used (this thread, for the length of one call).</summary>
        [ThreadStatic]
        private static IMyUseObject answer;

        private static void AnswerUseObject(ref IMyUseObject __result)
        {
            if (answer != null)
                __result = answer;
        }

        private static bool Enabled => hooked && !Off && VRSettings.GrabToUse && VRInput.Active;

        /// <summary>Runs a game call with the character's detector answering with <paramref name="use"/>, as <see cref="Use"/> does (<see cref="QuickInventory"/>'s take, dump and production).</summary>
        internal static void AnswerWith(IMyUseObject use, Action call)
        {
            answer = use;
            try
            {
                call();
            }
            finally
            {
                answer = null;
            }
        }

        /// <summary>What a hand is on now on foot (in reach, nothing in the way, highlighted), with nothing held in it; null otherwise.</summary>
        internal static IMyUseObject OnFoot(Hand hand)
        {
            IMyUseObject use = hover[(int)hand];
            return Enabled && lastPlace == Place.OnFoot && held[(int)hand] == null && use != null && !Gone(use) ? use : null;
        }

        /// <summary>What a hand has taken hold of on foot (its grip is the grab's until it is let go); null otherwise. <see cref="QuickInventory"/> keeps it as the target while the grip is held.</summary>
        internal static IMyUseObject Grabbed(Hand hand)
        {
            Held grab = held[(int)hand];
            return Enabled && lastPlace == Place.OnFoot && grab != null && !Gone(grab.Use) ? grab.Use : null;
        }

        /// <summary>
        /// Where the local character is for grabbing, and the seat it sits in. Seated means the player's own character
        /// sits in the seat it controls and sees from it in first person (the hands are drawn there); a camera block, a
        /// remote control, a turret or the seat's third-person camera is no place to reach from.
        /// </summary>
        internal static Place Where(MyCharacter character, out MyEntity seat)
        {
            seat = null;
            if (character == null || character.Closed || character.IsDead || character.PositionComp == null
                || MySession.Static == null || character != MySession.Static.LocalCharacter)
                return Place.None;
            if (HandTools.IsLocalOnFoot(character))
                return Place.OnFoot;
            bool seatedGrab = true;
            TestAidsSeatedGrab(ref seatedGrab);
            if (!seatedGrab || !character.IsSitting || !(MySession.Static.ControlledEntity is MyCockpit cockpit) || cockpit.Pilot != character
                || !(cockpit.IsInFirstPersonView || cockpit.ForceFirstPersonCamera))
                return Place.None;
            seat = MySession.Static.ControlledEntity.Entity;
            return Place.Seated;
        }

        /// <summary>
        /// Whether a hand may grab now: not up a ladder, not with a menu up, not the hand that holds a tool. A block in hand is
        /// not a tool (<see cref="FingerPress.IsHandTool"/>): with a block or the colour tool in hand, or a paste up with no tool
        /// in hand, the right grip on something usable uses it as on foot, as does the right grip anywhere else (build mode no
        /// longer claims it), and this claim is asked before the colour tool's.
        /// </summary>
        private static bool MayGrab(MyCharacter character, Hand hand) =>
            FingerPress.MayPress(character.IsOnLadder, MyScreenManager.GetScreenWithFocus(), FingerPress.IsHandTool(character.CurrentWeapon), hand, VRSettings.DominantHand);

        // ---- The grip claim ----

        /// <summary>Asked when a grip is pressed: yes when the palm is on something that can be taken, which then holds the grip.</summary>
        private static bool Take(Hand hand)
        {
            // GripClaim drops a taker that throws for good, so a failure here only declines this grip.
            try
            {
                return TakeCore(hand);
            }
            catch (Exception e)
            {
                if (takeErrors++ < 3)
                    Log.Error(e, "Grab to use could not decide on a grip");
                return false;
            }
        }

        private static bool TakeCore(Hand hand)
        {
            if (!Enabled)
                return false;
            MyCharacter character = MySession.Static?.LocalCharacter;
            Place place = Where(character, out MyEntity seat);
            if (place == Place.None || !MayGrab(character, hand))
                return false;
            if (!HandWorld.TryGetTrackingToBody(character, out MatrixD trackingToModel, out _))
                return false;
            MatrixD modelToWorld = MatrixD.Invert(character.PositionComp.WorldMatrixNormalizedInv);
            if (!TryPalm(hand, character, trackingToModel, modelToWorld, out Vector3D palm))
                return false;
            Gather(character, seat, place, palm);
            if (!BestReachable(hand, character, seat, palm, trackingToModel, modelToWorld, Trace, out Candidate best))
                return false;

            var grab = new Held
            {
                Use = best.Use,
                Kind = best.Kind,
                Tag = best.Tag,
                Distance = best.Distance,
                Place = place,
                StartLocal = ToOwnerFrame(best.Use, palm),
                Since = Stopwatch.GetTimestamp(),
            };
            held[(int)hand] = grab;
            hover[(int)hand] = null;
            Say($"Grab use: {hand} hand took hold of '{best.Tag}' {best.Use.GetType().Name} on {Describe(best.Use.Owner)} " +
                $"({best.Distance * 100:0.0} cm from the box; {Verb(best)}; {(place == Place.Seated ? $"seated in {Describe(seat)}" : "on foot")})");
            string instead = GripInstead(hand);
            if (instead != null)
                Log.Info($"Build: {hand.ToString().ToLowerInvariant()} grip on {Describe(best.Use.Owner)} ('{best.Tag}' {best.Use.GetType().Name}) uses it ({Verb(best)}) instead of {instead}");
            if (best.Kind == Kind.Door || best.Kind == Kind.Deliberate)
                Haptics.Pulse(hand, TakeAmplitude, TakeSeconds);
            return true;
        }

        /// <summary>
        /// For the log: what the right grip would have done had this not taken it (the colour tool's claim is asked after this
        /// one), or null when the grip is not its now.
        /// </summary>
        private static string GripInstead(Hand hand) =>
            hand == Hand.Right && PaintMode.Active ? "the colour tool's skin cycle" : null;

        private static string Verb(in Candidate c)
        {
            switch (c.Kind)
            {
                case Kind.Door: return $"used after {DoorPull * 100:0} cm of pull";
                case Kind.Deliberate: return $"used after {DeliberateSeconds:0.0} s held";
                default: return c.Use.ContinuousUsage ? "used now, and on while held" : "used now";
            }
        }

        // ---- Each frame ----

        /// <summary>Game thread, each simulation frame, for the local character.</summary>
        private static void AfterSimulation(MyCharacter __instance)
        {
            if (!hooked || __instance != MySession.Static?.LocalCharacter || !guard.On)
                return;
            try
            {
                frame++;
                // Asked every frame, on and off, so the claim notices a grip let go while a menu (a terminal this just opened) was up.
                bool leftHolds = GripClaim.Holds(Hand.Left, ClaimName), rightHolds = GripClaim.Holds(Hand.Right, ClaimName);

                MyEntity seat = null;
                Place place = Enabled ? Where(__instance, out seat) : Place.None;
                if (place != lastPlace)
                {
                    // The outline put on in a seat is this feature's to take off; on foot the game's detection does.
                    ClearSeatHighlight();
                    lastPlace = place;
                }
                MatrixD trackingToModel = MatrixD.Identity;
                if (place != Place.None && !HandWorld.TryGetTrackingToBody(__instance, out trackingToModel, out _))
                    place = Place.None;
                if (place == Place.None)
                {
                    ForgetAll(__instance, leftHolds, rightHolds);
                    ClearSeatHighlight();
                    return;
                }

                MatrixD modelToWorld = MatrixD.Invert(__instance.PositionComp.WorldMatrixNormalizedInv);
                long now = Stopwatch.GetTimestamp();
                foreach (Hand hand in bothHands)
                {
                    bool holds = hand == Hand.Left ? leftHolds : rightHolds;
                    bool havePalm = TryPalm(hand, __instance, trackingToModel, modelToWorld, out Vector3D palm);
                    Step(hand, __instance, seat, place, holds, havePalm, palm, now, trackingToModel, modelToWorld);
                }
                if (place == Place.Seated && hover[0] == null && hover[1] == null)
                    ClearSeatHighlight();
            }
            catch (Exception e)
            {
                guard.Failed(e, "Grab to use failed");
            }
        }

        private static void Step(Hand hand, MyCharacter character, MyEntity seat, Place place, bool holds, bool havePalm, Vector3D palm, long now,
            in MatrixD trackingToModel, in MatrixD modelToWorld)
        {
            int i = (int)hand;
            Held grab = held[i];
            if (grab != null && !holds)
            {
                LetGo(hand, character, grab, "let go", true);
                grab = held[i] = null;
            }
            if (holds && grab == null)
            {
                // The claim says this feature holds the grip but nothing is held (the feature was switched off and on).
                GripClaim.Release(hand, ClaimName);
                return;
            }

            if (grab != null)
            {
                if (Gone(grab.Use))
                {
                    GripClaim.Release(hand, ClaimName);
                    LetGo(hand, character, grab, "the thing went away");
                    held[i] = null;
                    return;
                }
                if (!havePalm)
                    return;
                if (!grab.Fired)
                {
                    switch (grab.Kind)
                    {
                        case Kind.Door:
                            double pulled = (ToOwnerFrame(grab.Use, palm) - grab.StartLocal).Length();
                            if (pulled >= DoorPull)
                                UseOrWait(hand, character, grab, $"; pulled {pulled * 100:0.0} cm");
                            break;
                        case Kind.Deliberate:
                            double seconds = (now - grab.Since) / (double)Stopwatch.Frequency;
                            if (DistanceToUse(grab.Use, palm) > HoldReach)
                            {
                                grab.Fired = true;
                                Say($"Grab use: {hand} hand moved off '{grab.Tag}' {grab.Use.GetType().Name} after {seconds:0.00} s; not used");
                            }
                            else if (seconds >= DeliberateSeconds)
                                UseOrWait(hand, character, grab, $"; held {seconds:0.00} s");
                            break;
                        default:
                            UseOrWait(hand, character, grab, string.Empty);
                            break;
                    }
                }
                else if (grab.Used && grab.Use.ContinuousUsage)
                {
                    ContinueHeld(character, grab, palm);
                }
                return;
            }

            // Nothing in this hand: the feedback of what the hand is within reach of.
            if (!havePalm || !MayGrab(character, hand))
            {
                hover[i] = null;
                seenAt[i] = 0;
                return;
            }
            bool gap = seenAt[i] == 0 || now - seenAt[i] > GapSeconds * Stopwatch.Frequency;
            seenAt[i] = now;
            if ((frame + i) % HoverEveryFrames != 0 && !gap)
            {
                // Between looks the last answer stands, and the highlight is kept up (the game's own detection clears it every tenth frame).
                if (hover[i] != null && !Gone(hover[i]))
                    Highlight(hover[i], place);
                return;
            }

            Gather(character, seat, place, palm);
            LogNear(hand, palm, now, trackingToModel, modelToWorld);
            if (BestReachable(hand, character, seat, palm, trackingToModel, modelToWorld, false, out Candidate best))
            {
                if (!ReferenceEquals(hover[i], best.Use))
                {
                    hover[i] = best.Use;
                    if (now - tickedAt[i] > TickEverySeconds * Stopwatch.Frequency)
                    {
                        tickedAt[i] = now;
                        Haptics.Pulse(hand, TickAmplitude, TickSeconds);
                    }
                }
                Highlight(best.Use, place);
            }
            else
            {
                hover[i] = null;
            }
        }

        /// <summary>
        /// The grab's use, now; or, with the right grip's Use held back at a container (<see cref="QuickInventory.HoldsBack"/>:
        /// the stick click deposits with the grip held), noted as due and done when the grip is let go (<see cref="LetGo"/>).
        /// </summary>
        private static void UseOrWait(Hand hand, MyCharacter character, Held grab, string how)
        {
            if (hand != Hand.Right || !QuickInventory.HoldsBack)
            {
                Use(hand, character, grab, how);
                return;
            }
            grab.Fired = true;
            grab.HeldBack = true;
            grab.HeldBackHow = how;
        }

        /// <summary>The Use key's press of the grabbed object: the character's own <c>Use()</c> with the detector answering with it.</summary>
        private static void Use(Hand hand, MyCharacter character, Held grab, string how)
        {
            grab.Fired = true;
            try
            {
                if (character.GetDetectorComponent() == null)
                    return;
                answer = grab.Use;
                try
                {
                    character.Use();
                }
                finally
                {
                    answer = null;
                }
                grab.Used = true;
                grab.Continuing = grab.Use.ContinuousUsage;
                Haptics.Pulse(hand, UseAmplitude, UseSeconds);
                Say($"Grab use: {hand} hand used '{grab.Tag}' {grab.Use.GetType().Name} on {Describe(grab.Use.Owner)} " +
                    $"(primary {grab.Use.PrimaryAction}, secondary {grab.Use.SecondaryAction}; {(grab.Place == Place.Seated ? "seated" : "on foot")}{how})");
            }
            catch (Exception e)
            {
                if (useErrors++ < 3)
                    Log.Error(e, $"Grab to use: the game's Use failed on {Describe(grab.Use?.Owner)}");
            }
        }

        /// <summary>A held use goes on while the palm stays on the thing (the key's held calls this every frame), and ends when it moves away.</summary>
        private static void ContinueHeld(MyCharacter character, Held grab, Vector3D palm)
        {
            if (!grab.Continuing)
                return;
            if (DistanceToUse(grab.Use, palm) > HoldReach)
            {
                Finish(character, grab);
                return;
            }
            try
            {
                answer = grab.Use;
                try
                {
                    character.UseContinues();
                }
                finally
                {
                    answer = null;
                }
            }
            catch (Exception e)
            {
                if (useErrors++ < 3)
                    Log.Error(e, $"Grab to use: the game's UseContinues failed on {Describe(grab.Use.Owner)}");
            }
        }

        private static void Finish(MyCharacter character, Held grab)
        {
            grab.Continuing = false;
            try
            {
                if (character == null || character.GetDetectorComponent() == null)
                    return;
                answer = grab.Use;
                try
                {
                    character.UseFinished();
                }
                finally
                {
                    answer = null;
                }
            }
            catch (Exception e)
            {
                if (useErrors++ < 3)
                    Log.Error(e, $"Grab to use: the game's UseFinished failed on {Describe(grab.Use?.Owner)}");
            }
        }

        /// <summary>The Use key's press and release of an object once, for a right grip's Use that waited (<see cref="QuickInventory"/>): the same call as <see cref="Use"/>, then <c>UseFinished()</c> for a held use. False when the game's call failed.</summary>
        internal static bool Tap(MyCharacter character, IMyUseObject use)
        {
            try
            {
                if (character.GetDetectorComponent() == null)
                    return false;
                answer = use;
                try
                {
                    character.Use();
                    if (use.ContinuousUsage)
                        character.UseFinished();
                }
                finally
                {
                    answer = null;
                }
                Haptics.Pulse(Hand.Right, UseAmplitude, UseSeconds);
                return true;
            }
            catch (Exception e)
            {
                if (useErrors++ < 3)
                    Log.Error(e, $"Grab to use: the game's Use failed on {Describe(use?.Owner)}");
                return false;
            }
        }

        private static void LetGo(Hand hand, MyCharacter character, Held grab, string how, bool released = false)
        {
            if (grab.HeldBack)
                UseHeldBack(hand, character, grab, how, released);
            if (grab.Continuing)
                Finish(character, grab);
            double seconds = (Stopwatch.GetTimestamp() - grab.Since) / (double)Stopwatch.Frequency;
            Say($"Grab use: {hand} hand {how} '{grab.Tag}' {grab.Use?.GetType().Name} ({(grab.Used ? "used" : "not used")}, held {seconds:0.00} s)");
        }

        /// <summary>
        /// A use that waited for the right grip, at the end of the grip: done now when the grip was <paramref name="released"/>
        /// and no deposit was taken meanwhile (<see cref="QuickInventory.Deposited"/>), dropped otherwise. One log line either way.
        /// </summary>
        private static void UseHeldBack(Hand hand, MyCharacter character, Held grab, string how, bool released)
        {
            grab.HeldBack = false;
            string outcome;
            if (QuickInventory.Deposited)
                outcome = "dropped for a deposit";
            else if (Gone(grab.Use))
                outcome = "dropped (the thing went away)";
            else if (!released)
                outcome = $"dropped ({how})";
            else
            {
                Use(hand, character, grab, grab.HeldBackHow + "; held back until the grip was let go");
                outcome = grab.Used ? "used once the grip was let go" : "not used (the game's Use failed)";
            }
            QuickInventory.NoteHeldBack(grab.Use, "the hand on it", outcome);
        }

        private static void ForgetAll(MyCharacter character, bool leftHolds, bool rightHolds)
        {
            foreach (Hand hand in bothHands)
            {
                int i = (int)hand;
                Held grab = held[i];
                if (grab != null)
                {
                    LetGo(hand, character, grab, "dropped");
                    held[i] = null;
                }
                if (hand == Hand.Left ? leftHolds : rightHolds)
                    GripClaim.Release(hand, ClaimName);
                hover[i] = null;
                seenAt[i] = 0;
            }
        }

        internal static bool Gone(IMyUseObject use)
        {
            IMyEntity owner = use?.Owner;
            return owner == null || owner.Closed || owner.MarkedForClose;
        }

        private static void Highlight(IMyUseObject use, Place place)
        {
            if (highlight == null || highlightErrors >= 3 || (place == Place.Seated && removeHighlight == null))
                return;
            try
            {
                highlight(use);
                if (place == Place.Seated)
                    seatHighlight = true;
            }
            catch (Exception e)
            {
                if (highlightErrors++ < 3)
                    Log.Error(e, "Grab to use: the game's highlight failed");
            }
        }

        /// <summary>Takes off the outline this put on in a seat, where the game's own detection (which clears it on foot) does not run.</summary>
        private static void ClearSeatHighlight()
        {
            if (!seatHighlight)
                return;
            seatHighlight = false;
            try
            {
                MyHudSelectedObject selected = MyHud.SelectedObjectHighlight;
                if (selected != null && removeHighlight != null)
                    removeHighlight(selected);
            }
            catch (Exception e)
            {
                if (highlightErrors++ < 3)
                    Log.Error(e, "Grab to use: the game's highlight could not be taken off");
            }
        }
    }
}
