using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using HarmonyLib;
using Havok;
using Sandbox.Engine.Physics;
using Sandbox.Game.Components;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Gui;
using Sandbox.Game.SessionComponents.Clipboard;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;
using VRage.Game.Components;
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
    /// floating items, before the hull and ladder grab): it is asked when a grip is pressed and says yes when the palm is
    /// within <see cref="Reach"/> of something this takes. Then the grip is this feature's until it is let go, and the Use
    /// key is not also pressed by the right hand, nor does the grip fly the ship in a seat.</para>
    /// <para>What it takes: the game keeps every use object as a detector box in its block's space
    /// (<see cref="MyUseObjectsComponent.DetectorInteractiveObjects"/>), which is what <see cref="FingerPress"/> reads
    /// too; the box nearest the palm that is taken wins. Every kind is taken (<see cref="Classify"/>) except a short list,
    /// each for a reason: the kinds other features own (the ladder: the ladder grab; the push-buttons: the fingertip), and
    /// from a seat, the other seats and pods (the game seats a standing character, not one already seated) and the
    /// wardrobe (it re-dresses the body standing in the medical room). RenderDebug GrabAll=0 puts back the allow-list of
    /// before (seat, cryo pod, airtight and advanced doors, medical room and survival kit, cargo, terminal; on foot only).</para>
    /// <para>How each is used (<see cref="Kind"/>): most things the moment the hand closes on them. A door, a hatch, a
    /// hangar door and a maintenance panel are pulled or pushed: used once the held hand has moved <see cref="DoorPull"/>
    /// from where it took hold (in the door's own frame, so a ship's motion does not count), so a stray grip does not slam
    /// it. A use that spends money (a vending machine's Buy) needs the hand held on it for <see cref="DeliberateSeconds"/>.
    /// A use that is held (a medical room's healing, <c>ContinuousUsage</c>) goes on every frame while the hand stays on
    /// it, and is finished when the hand moves away or lets go, as the key's hold and release do.</para>
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
    internal static class GrabUse
    {
        /// <summary>The name the grip claim is held under.</summary>
        public const string ClaimName = "GrabUse";

        /// <summary>After the helmet (10) and floating items (20), before the hull and ladder grab (40).</summary>
        public const int ClaimPriority = 30;

        /// <summary>Debug: no grab to use (<see cref="Rendering.RenderDebug"/> GrabUse=0).</summary>
        public static bool Off { get; set; }

        /// <summary>Debug: false puts back the allow-list of before, on foot only (<see cref="Rendering.RenderDebug"/> GrabAll=0).</summary>
        public static bool All { get; set; } = true;

        /// <summary>Debug: log each grip's line of reach, clear or what blocked it (<see cref="Rendering.RenderDebug"/> GrabUseTrace=1).</summary>
        public static bool Trace { get; set; }

        /// <summary>The line of reach is swept with a ball this wide (SurfaceStop's fingertip ball), metres.</summary>
        private const float ReachBallRadius = 0.01f;

        /// <summary>A probe this wide at the start of the line finds what the reach starts inside (the seated body's blocks), metres.</summary>
        private const float StartProbeRadius = 0.002f;

        /// <summary>A shoulder farther than this from the palm is stale (the arms not yet known); the eyes are the origin then, metres.</summary>
        private const double MaxShoulderToPalm = 1.5;

        private const int MaxTraceLogs = 400;

        /// <summary>The palm is on a use object when it is inside its detector box or this close to it, metres.</summary>
        public const double Reach = 0.15;

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

        /// <summary>The allow-list of before (RenderDebug GrabAll=0): doors pulled, the rest used on grab.</summary>
        private static readonly HashSet<string> OldDoorTypes = new HashSet<string>
        {
            "MyUseObjectAirtightDoors",
            "MyUseObjectAdvancedDoorTerminal",
        };

        private static readonly HashSet<string> OldDeviceTypes = new HashSet<string>
        {
            "MyUseObjectCockpitDoor",
            "MyUseObjectCryoChamberDoor",
            "MyUseObjectLifeSupportingBlock",
            "MyUseObjectInventory",
            "MyUseObjectTerminal",
        };

        internal static Kind Classify(IMyUseObject use, bool seated) => Classify(use, seated, out _);

        /// <summary>How a use object is grabbed, and when it is not, why (for the log).</summary>
        internal static Kind Classify(IMyUseObject use, bool seated, out string why)
        {
            why = null;
            if (use == null)
                return Kind.None;
            string name = use.GetType().Name;
            if (!All)
            {
                if (OldDoorTypes.Contains(name))
                    return Kind.Door;
                if (OldDeviceTypes.Contains(name) && !seated)
                    return Kind.Device;
                why = "not on the old list (GrabAll=0)";
                return Kind.None;
            }
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
        private static int errors, takeErrors, useErrors, highlightErrors, logs, nearLogs, frame;
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
                Log.Error(e, "Grab to use could not be hooked; grabbing things will not use them");
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
            Log.Info($"Grab to use: hooked (grip claim '{ClaimName}' at {ClaimPriority}, reach {Reach * 100:0} cm, door pull {DoorPull * 100:0} cm, " +
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
            if (!All || !character.IsSitting || !(MySession.Static.ControlledEntity is MyCockpit cockpit) || cockpit.Pilot != character
                || !(cockpit.IsInFirstPersonView || cockpit.ForceFirstPersonCamera))
                return Place.None;
            seat = MySession.Static.ControlledEntity.Entity;
            return Place.Seated;
        }

        /// <summary>Whether a hand may grab now: not up a ladder, not with a menu up, not the hand that holds a tool or the builder.</summary>
        private static bool MayGrab(MyCharacter character, Hand hand)
        {
            bool building = (MyCubeBuilder.Static != null && MyCubeBuilder.Static.IsActivated)
                            || (MyClipboardComponent.Static != null && MyClipboardComponent.Static.IsActive);
            bool holdsTool = character.CurrentWeapon != null || building;
            return FingerPress.MayPress(character.IsOnLadder, MyScreenManager.GetScreenWithFocus(), holdsTool, hand, VRSettings.DominantHand);
        }

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
            if (!TryPalm(hand, trackingToModel, modelToWorld, out Vector3D palm))
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
            if (best.Kind == Kind.Door || best.Kind == Kind.Deliberate)
                Haptics.Pulse(hand, TakeAmplitude, TakeSeconds);
            return true;
        }

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
            if (errors >= 3 || !hooked || __instance != MySession.Static?.LocalCharacter)
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
                    bool havePalm = TryPalm(hand, trackingToModel, modelToWorld, out Vector3D palm);
                    Step(hand, __instance, seat, place, holds, havePalm, palm, now, trackingToModel, modelToWorld);
                }
                if (place == Place.Seated && hover[0] == null && hover[1] == null)
                    ClearSeatHighlight();
            }
            catch (Exception e)
            {
                if (errors++ < 3)
                    Log.Error(e, "Grab to use failed" + (errors >= 3 ? "; it stays off for this run" : string.Empty));
            }
        }

        private static void Step(Hand hand, MyCharacter character, MyEntity seat, Place place, bool holds, bool havePalm, Vector3D palm, long now,
            in MatrixD trackingToModel, in MatrixD modelToWorld)
        {
            int i = (int)hand;
            Held grab = held[i];
            if (grab != null && !holds)
            {
                LetGo(hand, character, grab, "let go");
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
                                Use(hand, character, grab, $"; pulled {pulled * 100:0.0} cm");
                            break;
                        case Kind.Deliberate:
                            double seconds = (now - grab.Since) / (double)Stopwatch.Frequency;
                            if (DistanceToUse(grab.Use, palm) > HoldReach)
                            {
                                grab.Fired = true;
                                Say($"Grab use: {hand} hand moved off '{grab.Tag}' {grab.Use.GetType().Name} after {seconds:0.00} s; not used");
                            }
                            else if (seconds >= DeliberateSeconds)
                                Use(hand, character, grab, $"; held {seconds:0.00} s");
                            break;
                        default:
                            Use(hand, character, grab, string.Empty);
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

        private static void LetGo(Hand hand, MyCharacter character, Held grab, string how)
        {
            if (grab.Continuing)
                Finish(character, grab);
            double seconds = (Stopwatch.GetTimestamp() - grab.Since) / (double)Stopwatch.Frequency;
            Say($"Grab use: {hand} hand {how} '{grab.Tag}' {grab.Use?.GetType().Name} ({(grab.Used ? "used" : "not used")}, held {seconds:0.00} s)");
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

        private static bool Gone(IMyUseObject use)
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

        // ---- Finding what the palm is on ----

        /// <summary>
        /// Every use object of the blocks near the palm, in <see cref="candidates"/>, with its distance to the palm and how
        /// it is grabbed. The character's own and, in a seat, the seat's own block's are left out.
        /// </summary>
        private static void Gather(MyCharacter character, MyEntity seat, Place place, Vector3D palm)
        {
            candidates.Clear();
            nearby.Clear();
            bool seated = place == Place.Seated;
            var sphere = new BoundingSphereD(palm, GatherRadius);
            MyGamePruningStructure.GetAllEntitiesInSphere(ref sphere, nearby);
            foreach (MyEntity entity in nearby)
            {
                if (entity == null || entity.Closed || entity.MarkedForClose || entity.PositionComp == null || entity.Components == null)
                    continue;
                if (ReferenceEquals(entity, character) || (seat != null && ReferenceEquals(entity, seat)))
                    continue;
                if (!entity.Components.TryGet(out MyUseObjectsComponentBase found) || !(found is MyUseObjectsComponent component))
                    continue;
                MatrixD world = entity.PositionComp.WorldMatrixRef;
                var detectors = component.DetectorInteractiveObjects;
                lock (detectors)
                {
                    foreach (KeyValuePair<uint, MyUseObjectsComponent.DetectorData> pair in detectors)
                    {
                        IMyUseObject use = pair.Value.UseObject;
                        if (use == null || use.SupportedActions == UseActionEnum.None || ReferenceEquals(use.Owner, character)
                            || (seat != null && ReferenceEquals(use.Owner, seat)))
                            continue;
                        MatrixD box = (MatrixD)pair.Value.Matrix * world;
                        double distance = FingerPress.DistanceToBox(box, palm);
                        if (distance > NearLogDistance)
                            continue;
                        candidates.Add(new Candidate
                        {
                            Use = use,
                            Kind = Classify(use, seated, out string why),
                            Why = why,
                            Distance = distance,
                            Center = Vector3D.Distance(palm, box.Translation),
                            Tag = use.Dummy?.Name ?? pair.Value.DetectorName,
                            BoxCenter = box.Translation,
                        });
                    }
                }
            }
            nearby.Clear();
        }

        private static readonly List<Candidate> inReach = new List<Candidate>();
        private static readonly Comparison<Candidate> nearestFirst =
            (a, b) => a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance) : a.Center.CompareTo(b.Center);

        /// <summary>
        /// The use object a hand takes: of those in <see cref="candidates"/> within <see cref="Reach"/> that are taken, the
        /// nearest (inside beats near, then the one whose middle is closest) the arm reaches without going through something
        /// solid (<see cref="LineOfReach"/>). With <paramref name="trace"/>, one log line of what was checked and why.
        /// </summary>
        private static bool BestReachable(Hand hand, MyCharacter character, MyEntity seat, Vector3D palm, in MatrixD trackingToModel,
            in MatrixD modelToWorld, bool trace, out Candidate best)
        {
            best = default;
            inReach.Clear();
            foreach (Candidate c in candidates)
            {
                if (c.Kind != Kind.None && c.Distance <= Reach)
                    inReach.Add(c);
            }
            if (inReach.Count == 0)
                return false;
            inReach.Sort(nearestFirst);

            Vector3D origin = ReachOrigin(hand, character, palm, trackingToModel, modelToWorld, out string from);
            StringBuilder said = trace ? new StringBuilder() : null;
            bool found = false;
            foreach (Candidate c in inReach)
            {
                bool clear = LineOfReach(origin, palm, c.Use, seat, said != null, out string verdict);
                said?.Append(said.Length == 0 ? string.Empty : "; ").Append($"'{c.Tag}' on {Describe(c.Use.Owner)} {verdict}");
                if (clear)
                {
                    best = c;
                    found = true;
                    break;
                }
            }
            inReach.Clear();
            if (said != null && traceLogs++ < MaxTraceLogs)
                Log.Info($"Grab use: {hand} hand's reach from the {from}, {Vector3D.Distance(origin, palm) * 100:0} cm to the palm: {said}");
            return found;
        }

        private static int traceLogs;

        /// <summary>Where the arm reaches from: the hand's shoulder as last solved or animated, or the eyes before the arms are known.</summary>
        private static Vector3D ReachOrigin(Hand hand, MyCharacter character, Vector3D palm, in MatrixD trackingToModel, in MatrixD modelToWorld, out string from)
        {
            if (HandIK.TryShoulder(hand, character, out Vector3D shoulder) && Vector3D.Distance(shoulder, palm) <= MaxShoulderToPalm)
            {
                from = "shoulder";
                return shoulder;
            }
            from = "eyes";
            if (HeadTracker.TryGet(out Matrix tracked))
                return Vector3D.Transform(Vector3D.Transform((Vector3D)tracked.Translation, trackingToModel), modelToWorld);
            return character.GetHeadMatrix(false).Translation;
        }

        private static readonly List<MyPhysics.HitInfo> reachHits = new List<MyPhysics.HitInfo>();
        private static readonly List<HkBodyCollision> startsIn = new List<HkBodyCollision>();
        private static readonly Vector3D[] reachPoint = new Vector3D[1];
        private static HkShape reachBall, startProbe;
        private static bool reachShapesMade;
        private static int reachErrors;

        /// <summary>
        /// Whether nothing solid lies on the line from <paramref name="origin"/> (the shoulder, or the eyes) to the palm other
        /// than the target's own block and the seat. A 1 cm ball is swept along it with
        /// <c>MyPhysics.CastShapeReturnContactBodyDatas</c> on the character collision layer, as <see cref="SurfaceStop"/>
        /// sweeps the drawn hands, and solid is what <see cref="SurfaceStop.Solid"/> says (a grid that is not a projection, or
        /// voxels). Also passed: a back face (the line leaving something) and whatever the line starts inside (a 2 mm probe at
        /// the origin: the seated body's blocks). Physics that cannot be asked from here (inside the simulation, another
        /// thread) or a failed cast count as clear, so this never takes away a grab it could not check.
        /// </summary>
        private static bool LineOfReach(Vector3D origin, Vector3D palm, IMyUseObject use, MyEntity seat, bool note, out string verdict)
        {
            verdict = "clear";
            if (reachErrors >= 3 || !MyPhysics.CheckThread() || MyPhysics.InsideSimulation)
            {
                verdict = "not checked (physics busy)";
                return true;
            }
            Vector3D way = palm - origin;
            double length = way.Length();
            if (length < 2 * ReachBallRadius)
                return true;
            try
            {
                if (!reachShapesMade)
                {
                    reachBall = new HkSphereShape(ReachBallRadius).Base;
                    startProbe = new HkSphereShape(StartProbeRadius).Base;
                    reachShapesMade = true;
                }
                MySlimBlock target = BlockOf(use?.Owner as MyEntity);
                MySlimBlock seatBlock = BlockOf(seat);

                startsIn.Clear();
                Vector3D start = origin;
                Quaternion none = Quaternion.Identity;
                MyPhysics.GetPenetrationsShape(startProbe, ref start, ref none, startsIn, MyPhysics.CollisionLayers.CharacterCollisionLayer);

                reachHits.Clear();
                MatrixD at = MatrixD.CreateTranslation(origin);
                MyPhysics.CastShapeReturnContactBodyDatas(palm, reachBall, ref at, (uint)MyPhysics.CollisionLayers.CharacterCollisionLayer, 0f, reachHits);

                float first = float.MaxValue;
                object blocker = null;
                StringBuilder passed = note ? new StringBuilder() : null;
                int notes = 0;
                foreach (MyPhysics.HitInfo hit in reachHits)
                {
                    HkHitInfo info = hit.HkHitInfo;
                    if (Vector3D.Dot(info.Normal, way) > 0.0)
                        continue; // a back face: the line leaving something
                    MyEntity owner = SurfaceStop.Solid(info, out MySlimBlock block, out object what);
                    if (owner == null)
                        continue;
                    string why = target != null && block == target ? "the target's own block"
                        : seatBlock != null && block == seatBlock ? "the seat"
                        : StartsInside(info, owner, block, origin) ? "starts inside it"
                        : null;
                    if (why != null)
                    {
                        if (passed != null && notes++ < 4)
                            passed.Append(passed.Length == 0 ? string.Empty : ", ")
                                  .Append($"{SurfaceStop.Describe(what)} at {info.HitFraction * length * 100:0} cm ({why})");
                        continue;
                    }
                    if (info.HitFraction < first)
                    {
                        first = info.HitFraction;
                        blocker = what;
                    }
                }
                reachHits.Clear();
                startsIn.Clear();
                string alsoPassed = passed != null && passed.Length > 0 ? $"; passed {passed}" : string.Empty;
                if (blocker == null)
                {
                    verdict = "clear" + alsoPassed;
                    return true;
                }
                verdict = $"blocked by {SurfaceStop.Describe(blocker)} {first * length * 100:0} cm along{alsoPassed}";
                return false;
            }
            catch (Exception e)
            {
                reachHits.Clear();
                startsIn.Clear();
                Log.Error(e, ++reachErrors < 3 ? "Grab use: the line of reach could not be checked" : "Grab use: the line of reach failed again; not checked until the game restarts");
                verdict = "not checked (failed)";
                return true;
            }
        }

        /// <summary>Whether the line began inside what it hit (the probe at its start was in the same body and block, or in the voxels).</summary>
        private static bool StartsInside(HkHitInfo info, MyEntity owner, MySlimBlock block, Vector3D origin)
        {
            if (owner is MyVoxelBase)
            {
                reachPoint[0] = origin;
                MyVoxelBase voxels = info.GetHitEntity() as MyVoxelBase ?? (MyVoxelBase)owner;
                return voxels.IsAnyOfPointInside(reachPoint);
            }
            foreach (HkBodyCollision c in startsIn)
            {
                if (c.Body != info.Body)
                    continue;
                if (block == null || (owner is MyCubeGrid grid && SurfaceStop.BlockOf(grid, c.ShapeKey) == block))
                    return true;
            }
            return false;
        }

        /// <summary>The block an entity is, or is a part of (a door's leaf, a seat); null for anything else.</summary>
        private static MySlimBlock BlockOf(MyEntity entity)
        {
            for (MyEntity e = entity; e != null; e = e.Parent)
            {
                if (e is MyCubeBlock block)
                    return block.SlimBlock;
            }
            return null;
        }

        /// <summary>The palm in the world: the grip pose's position, put in the world as this frame's camera will have the body.</summary>
        private static bool TryPalm(Hand hand, in MatrixD trackingToModel, in MatrixD modelToWorld, out Vector3D palm)
        {
            palm = Vector3D.Zero;
            HandState state = VRInput.Get(hand);
            if (!state.Tracked)
                return false;
            palm = Vector3D.Transform(Vector3D.Transform((Vector3D)state.Grip.Translation, trackingToModel), modelToWorld);
            return true;
        }

        /// <summary>A world point in the frame of the use object's owner (a door, a seat), which moves with its ship.</summary>
        private static Vector3D ToOwnerFrame(IMyUseObject use, Vector3D world)
        {
            if (use?.Owner is MyEntity owner && owner.PositionComp != null)
                return Vector3D.Transform(world, owner.PositionComp.WorldMatrixNormalizedInv);
            return world;
        }

        /// <summary>Metres from the palm to the use object's detector box (a held use is checked against the box it took).</summary>
        private static double DistanceToUse(IMyUseObject use, Vector3D palm)
        {
            if (!(use?.Owner is MyEntity owner) || owner.PositionComp == null
                || !owner.Components.TryGet(out MyUseObjectsComponentBase found) || !(found is MyUseObjectsComponent component))
                return double.PositiveInfinity;
            MatrixD world = owner.PositionComp.WorldMatrixRef;
            double best = double.PositiveInfinity;
            lock (component.DetectorInteractiveObjects)
            {
                foreach (KeyValuePair<uint, MyUseObjectsComponent.DetectorData> pair in component.DetectorInteractiveObjects)
                {
                    if (!ReferenceEquals(pair.Value.UseObject, use))
                        continue;
                    best = Math.Min(best, FingerPress.DistanceToBox((MatrixD)pair.Value.Matrix * world, palm));
                }
            }
            return best;
        }

        // ---- The log ----

        private static void Say(string message)
        {
            long now = Stopwatch.GetTimestamp();
            if (now - logWindow > Stopwatch.Frequency)
            {
                logWindow = now;
                logsInWindow = 0;
            }
            if (logs >= MaxLogs || logsInWindow >= MaxLogsPerSecond)
                return;
            logs++;
            logsInWindow++;
            Log.Info(message);
        }

        /// <summary>What the hand is close to, taken or not, with where its middle is in tracking space (what a fake hand is placed with).</summary>
        private static void LogNear(Hand hand, Vector3D palm, long now, in MatrixD trackingToModel, in MatrixD modelToWorld)
        {
            int i = (int)hand;
            if (nearLogs >= MaxNearLogs || candidates.Count == 0 || now - nearLoggedAt[i] < NearLogEvery)
                return;
            Candidate nearest = candidates[0];
            foreach (Candidate c in candidates)
            {
                if (c.Distance < nearest.Distance || (c.Distance == nearest.Distance && c.Center < nearest.Center))
                    nearest = c;
            }
            nearLoggedAt[i] = now;
            nearLogs++;
            Vector3D centerInTracking = Vector3D.Transform(nearest.BoxCenter, MatrixD.Invert(trackingToModel * modelToWorld));
            Log.Info($"Grab use: {hand} palm {nearest.Distance * 100:0.0} cm from '{nearest.Tag}' {nearest.Use.GetType().Name} on {Describe(nearest.Use.Owner)} " +
                     $"({(nearest.Kind == Kind.None ? $"not taken: {nearest.Why ?? "?"}" : nearest.Kind.ToString())}; box middle at tracking {centerInTracking.X:0.00},{centerInTracking.Y:0.00},{centerInTracking.Z:0.00})");
        }

        /// <summary>
        /// For the drive's "uses" command (testing): every use object within <paramref name="radius"/> metres of the local
        /// character's head, nearest first, with how it would be grabbed from where the player is and where its box's middle
        /// is in the tracking space (what a fake hand is placed with: x right, y up, z back), and the head's place there.
        /// </summary>
        internal static string Survey(double radius, int max)
        {
            MyCharacter character = MySession.Static?.LocalCharacter;
            Place place = Where(character, out MyEntity seat);
            if (character?.PositionComp == null)
                return "no character";
            if (!HandWorld.TryGetTrackingToBody(character, out MatrixD trackingToModel, out var why))
                return $"no hands' frame ({why})";
            MatrixD trackingToWorld = trackingToModel * MatrixD.Invert(character.PositionComp.WorldMatrixNormalizedInv);
            MatrixD worldToTracking = MatrixD.Invert(trackingToWorld);
            Vector3D head = HeadTracker.TryGet(out Matrix tracked) ? (Vector3D)tracked.Translation : Vector3D.Zero;
            Vector3D center = Vector3D.Transform(head, trackingToWorld);
            var found = new List<(double distance, string text)>();
            var all = new List<MyEntity>();
            var sphere = new BoundingSphereD(center, radius);
            MyGamePruningStructure.GetAllEntitiesInSphere(ref sphere, all);
            foreach (MyEntity entity in all)
            {
                if (entity?.PositionComp == null || entity.Components == null || entity.Closed
                    || !entity.Components.TryGet(out MyUseObjectsComponentBase component) || !(component is MyUseObjectsComponent uses))
                    continue;
                MatrixD world = entity.PositionComp.WorldMatrixRef;
                lock (uses.DetectorInteractiveObjects)
                {
                    foreach (KeyValuePair<uint, MyUseObjectsComponent.DetectorData> pair in uses.DetectorInteractiveObjects)
                    {
                        IMyUseObject use = pair.Value.UseObject;
                        if (use == null || ReferenceEquals(use.Owner, character))
                            continue;
                        MatrixD box = (MatrixD)pair.Value.Matrix * world;
                        double distance = FingerPress.DistanceToBox(box, center);
                        if (distance > radius)
                            continue;
                        bool own = seat != null && ReferenceEquals(use.Owner, seat);
                        string reason = null;
                        Kind kind = own ? Kind.None : Classify(use, place == Place.Seated, out reason);
                        Vector3D at = Vector3D.Transform(box.Translation, worldToTracking);
                        found.Add((distance, $"{distance:0.00} m '{use.Dummy?.Name ?? pair.Value.DetectorName}' {use.GetType().Name} on {Describe(use.Owner)} " +
                                             $"[{(own ? "the seat's own" : kind == Kind.None ? "not: " + reason : kind.ToString())}] at {at.X:0.00},{at.Y:0.00},{at.Z:0.00}"));
                    }
                }
            }
            found.Sort((a, b) => a.distance.CompareTo(b.distance));
            string list = string.Join(" | ", found.Take(max).Select(f => f.text));
            return $"{place}{(seat != null ? " in " + Describe(seat) : string.Empty)}; head at tracking {head.X:0.00},{head.Y:0.00},{head.Z:0.00}; " +
                   $"{found.Count} use objects within {radius:0.#} m: {list}";
        }

        private static string Describe(IMyEntity owner)
        {
            if (owner == null)
                return "nothing";
            return owner is MyCubeBlock block && block.BlockDefinition != null
                ? $"{owner.GetType().Name} {block.BlockDefinition.Id.SubtypeName}"
                : owner.GetType().Name;
        }
    }
}
