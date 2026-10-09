using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using Sandbox.Game.Components;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Character.Components;
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
    /// Use things by reaching out and taking hold of them. A grip that starts with the hand on a seat, a cryo pod, a door,
    /// a medical room or survival kit, a cargo container or a terminal block is that thing's Use: the game's own, on that
    /// object, the same as the Use key does on what the ray points at. A grip in empty air keeps its old meaning (Use
    /// along the hand's ray on the right hand, the toolbar wheel on the left), so the ray Use only happens when nothing is
    /// within reach of the hand. Either hand can grab.
    /// </summary>
    /// <remarks>
    /// <para>Who owns the grip: <see cref="GripClaim"/>. <see cref="Take"/> is the taker (priority 30, after holsters and
    /// floating items, before the hull and ladder grab): it is asked when a grip is pressed and says yes when the palm is
    /// within <see cref="Reach"/> of something this takes. Then the grip is this feature's until it is let go, and the Use
    /// key is not also pressed by the right hand.</para>
    /// <para>What it takes, and how: the game keeps every use object as a detector box in its block's space
    /// (<see cref="MyUseObjectsComponent.DetectorInteractiveObjects"/>), which is what <see cref="FingerPress"/> reads
    /// too; the box nearest the palm that is on the allow-list of <see cref="Kind"/> wins. A seat, a pod, a device
    /// (medical room, survival kit, cargo, terminal) is used the moment the hand closes on it. A door is pulled or
    /// pushed: it is used once the held hand has moved <see cref="DoorPull"/> from where it took hold (in the door's own
    /// frame, so a ship's motion does not count), so a stray grip does not slam it. A use that is held (a medical room's
    /// healing, <c>ContinuousUsage</c>) goes on every frame while the hand stays on it, and is finished when the hand
    /// moves away or lets go, as the key's hold and release do.</para>
    /// <para>The use is the game's: <c>MyCharacter.Use()</c> with the character's detector answering with the grabbed
    /// object for that call (the same seam <see cref="FingerPress"/> uses), so the sound, the access check, the multiplayer
    /// request and the screens are the game's. Left out on purpose: ladders (the ladder grab owns them), button panels
    /// and the other push-buttons (the fingertip owns them), and floating items (their own grab).</para>
    /// <para>Feedback: within reach of something that can be taken, the game's outline highlight is put on it and the
    /// hand gets a faint tick when it first comes into reach; a stronger pulse follows the use itself.</para>
    /// </remarks>
    internal static class GrabUse
    {
        /// <summary>The name the grip claim is held under.</summary>
        public const string ClaimName = "GrabUse";

        /// <summary>After holsters (10) and floating items (20), before the hull and ladder grab (40).</summary>
        public const int ClaimPriority = 30;

        /// <summary>Debug: no grab to use (<see cref="Rendering.RenderDebug"/> GrabUse=0).</summary>
        public static bool Off { get; set; }

        /// <summary>The palm is on a use object when it is inside its detector box or this close to it, metres.</summary>
        public const double Reach = 0.15;

        /// <summary>A held door is used after the hand has moved this far from where it took hold, metres.</summary>
        public const double DoorPull = 0.12;

        /// <summary>A held use (a medical room's) goes on while the palm is within this of the box, metres.</summary>
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

            /// <summary>A seat, a pod, a medical room, a cargo container, a terminal: used as the hand closes.</summary>
            Device,

            /// <summary>Pulled or pushed: used once the held hand has moved <see cref="DoorPull"/>.</summary>
            Door,
        }

        /// <summary>
        /// The use objects taken, by the type's name (some of the types are internal to the game, so they are not named in
        /// code): the doors (<c>door</c>, <c>advanceddoor</c>), and the seat (<c>cockpit</c>), the cryo pod (<c>cryopod</c>), the
        /// life-supporting blocks (<c>block</c>: medical room, survival kit), the cargo and inventories
        /// (<c>inventory</c>, <c>conveyor</c>) and the terminal blocks (<c>terminal</c>). Not here: the ladder, the button panel
        /// and the other push-buttons, the floating items, the vending machines, stores, ATMs and the rest.
        /// </summary>
        private static readonly HashSet<string> DoorTypes = new HashSet<string>
        {
            "MyUseObjectAirtightDoors",
            "MyUseObjectAdvancedDoorTerminal",
        };

        private static readonly HashSet<string> DeviceTypes = new HashSet<string>
        {
            "MyUseObjectCockpitDoor",
            "MyUseObjectCryoChamberDoor",
            "MyUseObjectLifeSupportingBlock",
            "MyUseObjectInventory",
            "MyUseObjectTerminal",
        };

        internal static Kind Classify(IMyUseObject use)
        {
            if (use == null)
                return Kind.None;
            string name = use.GetType().Name;
            if (DoorTypes.Contains(name))
                return Kind.Door;
            return DeviceTypes.Contains(name) ? Kind.Device : Kind.None;
        }

        /// <summary>A use object near the palm.</summary>
        private struct Candidate
        {
            public IMyUseObject Use;
            public Kind Kind;
            public double Distance, Center;
            public string Tag;
            public Vector3D BoxCenter;
        }

        /// <summary>What a hand has hold of.</summary>
        private sealed class Held
        {
            public IMyUseObject Use;
            public Kind Kind;
            public string Tag;
            public double Distance;

            /// <summary>Where the palm took hold, in the owner's own frame (so a moving ship does not count as pulling).</summary>
            public Vector3D StartLocal;

            public bool Fired, Continuing;
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

        private static bool hooked;
        private static int errors, takeErrors, useErrors, highlightErrors, logs, nearLogs, frame;
        private static long logWindow;
        private static int logsInWindow;
        private static Action<IMyUseObject> highlight;

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

            hooked = true;
            GripClaim.Register(ClaimName, ClaimPriority, Take);
            Log.Info($"Grab to use: hooked (grip claim '{ClaimName}' at {ClaimPriority}, reach {Reach * 100:0} cm, door pull {DoorPull * 100:0} cm, " +
                     $"highlight {(highlight != null ? "on" : "off")})");
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
            if (!HandTools.IsLocalOnFoot(character) || character.Closed || character.PositionComp == null || !MayGrab(character, hand))
                return false;
            if (!HandWorld.TryGetTrackingToBody(character, out MatrixD trackingToModel, out _))
                return false;
            MatrixD modelToWorld = MatrixD.Invert(character.PositionComp.WorldMatrixNormalizedInv);
            if (!TryPalm(hand, trackingToModel, modelToWorld, out Vector3D palm))
                return false;
            Gather(character, palm);
            if (!Best(out Candidate best) || best.Distance > Reach)
                return false;

            var grab = new Held
            {
                Use = best.Use,
                Kind = best.Kind,
                Tag = best.Tag,
                Distance = best.Distance,
                StartLocal = ToOwnerFrame(best.Use, palm),
                Since = Stopwatch.GetTimestamp(),
            };
            held[(int)hand] = grab;
            hover[(int)hand] = null;
            Say($"Grab use: {hand} hand took hold of '{best.Tag}' {best.Use.GetType().Name} on {Describe(best.Use.Owner)} " +
                $"({best.Distance * 100:0.0} cm from the box; {(best.Kind == Kind.Door ? $"used after {DoorPull * 100:0} cm of pull" : "used now")})");
            if (best.Kind == Kind.Door)
                Haptics.Pulse(hand, TakeAmplitude, TakeSeconds);
            return true;
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

                bool live = Enabled && HandTools.IsLocalOnFoot(__instance) && !__instance.Closed && __instance.PositionComp != null;
                MatrixD trackingToModel = MatrixD.Identity;
                if (live && !HandWorld.TryGetTrackingToBody(__instance, out trackingToModel, out _))
                    live = false;
                if (!live)
                {
                    ForgetAll(__instance, leftHolds, rightHolds);
                    return;
                }

                MatrixD modelToWorld = MatrixD.Invert(__instance.PositionComp.WorldMatrixNormalizedInv);
                long now = Stopwatch.GetTimestamp();
                foreach (Hand hand in bothHands)
                {
                    bool holds = hand == Hand.Left ? leftHolds : rightHolds;
                    bool havePalm = TryPalm(hand, trackingToModel, modelToWorld, out Vector3D palm);
                    Step(hand, __instance, holds, havePalm, palm, now, trackingToModel, modelToWorld);
                }
            }
            catch (Exception e)
            {
                if (errors++ < 3)
                    Log.Error(e, "Grab to use failed" + (errors >= 3 ? "; it stays off for this run" : string.Empty));
            }
        }

        private static void Step(Hand hand, MyCharacter character, bool holds, bool havePalm, Vector3D palm, long now,
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
                    if (grab.Kind == Kind.Device)
                        Use(hand, character, grab, 0.0);
                    else
                    {
                        double pulled = (ToOwnerFrame(grab.Use, palm) - grab.StartLocal).Length();
                        if (pulled >= DoorPull)
                            Use(hand, character, grab, pulled);
                    }
                }
                else if (grab.Use.ContinuousUsage)
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
                    Highlight(hover[i]);
                return;
            }

            Gather(character, palm);
            LogNear(hand, palm, now, trackingToModel, modelToWorld);
            if (Best(out Candidate best) && best.Distance <= Reach)
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
                Highlight(best.Use);
            }
            else
            {
                hover[i] = null;
            }
        }

        /// <summary>The Use key's press of the grabbed object: the character's own <c>Use()</c> with the detector answering with it.</summary>
        private static void Use(Hand hand, MyCharacter character, Held grab, double pulled)
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
                grab.Continuing = grab.Use.ContinuousUsage;
                Haptics.Pulse(hand, UseAmplitude, UseSeconds);
                Say($"Grab use: {hand} hand used '{grab.Tag}' {grab.Use.GetType().Name} on {Describe(grab.Use.Owner)} " +
                    $"(primary {grab.Use.PrimaryAction}, secondary {grab.Use.SecondaryAction}" +
                    (grab.Kind == Kind.Door ? $"; pulled {pulled * 100:0.0} cm" : string.Empty) + ")");
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
            Say($"Grab use: {hand} hand {how} '{grab.Tag}' {grab.Use?.GetType().Name} ({(grab.Fired ? "used" : "not used")}, held {seconds:0.00} s)");
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

        private static void Highlight(IMyUseObject use)
        {
            if (highlight == null || highlightErrors >= 3)
                return;
            try
            {
                highlight(use);
            }
            catch (Exception e)
            {
                if (highlightErrors++ < 3)
                    Log.Error(e, "Grab to use: the game's highlight failed");
            }
        }

        // ---- Finding what the palm is on ----

        /// <summary>Every use object of the blocks near the palm, in <see cref="candidates"/>, with its distance to the palm.</summary>
        private static void Gather(MyCharacter character, Vector3D palm)
        {
            candidates.Clear();
            nearby.Clear();
            var sphere = new BoundingSphereD(palm, GatherRadius);
            MyGamePruningStructure.GetAllEntitiesInSphere(ref sphere, nearby);
            foreach (MyEntity entity in nearby)
            {
                if (entity == null || entity.Closed || entity.MarkedForClose || entity.PositionComp == null || entity.Components == null)
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
                        if (use == null || use.SupportedActions == UseActionEnum.None || ReferenceEquals(use.Owner, character))
                            continue;
                        MatrixD box = (MatrixD)pair.Value.Matrix * world;
                        double distance = FingerPress.DistanceToBox(box, palm);
                        if (distance > NearLogDistance)
                            continue;
                        candidates.Add(new Candidate
                        {
                            Use = use,
                            Kind = Classify(use),
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

        /// <summary>The nearest use object in <see cref="candidates"/> that is taken: inside beats near, then the one whose middle is closest.</summary>
        private static bool Best(out Candidate best)
        {
            best = default;
            bool found = false;
            foreach (Candidate c in candidates)
            {
                if (c.Kind == Kind.None)
                    continue;
                if (!found || c.Distance < best.Distance || (c.Distance == best.Distance && c.Center < best.Center))
                {
                    best = c;
                    found = true;
                }
            }
            return found;
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
                     $"({(nearest.Kind == Kind.None ? "not taken" : nearest.Kind.ToString())}; box middle at tracking {centerInTracking.X:0.00},{centerInTracking.Y:0.00},{centerInTracking.Z:0.00})");
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
