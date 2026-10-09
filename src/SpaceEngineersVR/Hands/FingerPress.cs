using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using Sandbox;
using Sandbox.Game.Components;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.Gui;
using Sandbox.Game.SessionComponents.Clipboard;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.Entity.UseObject;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// Press the world's push-buttons by touching them with the tip of a finger. Each hand has an index
    /// fingertip in the world; a use object (the game's own, and only the push-buttons of <see cref="IsPressSwitch"/>: each
    /// button of a button panel, a jukebox's and a vending machine's browse buttons) is touched when the tip is inside its
    /// detector volume, or within <see cref="TouchMargin"/> of it, and
    /// that presses it the way the Use key would, once per touch.
    /// </summary>
    /// <remarks>
    /// <para>Where the objects are: the game keeps every detector as a box in its block's own space
    /// (<see cref="MyUseObjectsComponent.DetectorInteractiveObjects"/>: a use object, and the box's matrix, whose scale
    /// is its size). The game's own use ray tests the same boxes (<c>MyUseObjectsComponent.RaycastDetectors</c>) as
    /// oriented boxes in the world, <c>detector matrix * block world matrix</c>; so does this, for a point. The blocks
    /// to look at come from the game's own area query (<c>MyGamePruningStructure.GetAllEntitiesInSphere</c>, which
    /// gives a grid's blocks near the sphere too, as <c>MyCharacterDetectorComponent.EnableDetectorsInArea</c> uses it).</para>
    /// <para>How it is pressed: the game's Use key calls <c>MyCharacter.Use()</c>, which acts on whatever the character's
    /// detector component says is the use object (<c>MyCharacterDetectorComponent.UseObject</c>), and does the rest
    /// itself (the sound, the access check, the multiplayer event, the terminal screen). So the touch makes that property
    /// answer with the touched object for the length of one call, and calls <c>Use()</c>. A held use
    /// (<c>ContinuousUsage</c>, as a medical room's) goes on with <c>UseContinues()</c> every frame while the tip stays
    /// in, and <c>UseFinished()</c> follows when it leaves, as the key's held and release do.</para>
    /// <para>One touch, one press: a touched object stays latched until the tip is more than <see cref="ReleaseDistance"/>
    /// from it, and of several boxes touched at once only the nearest is pressed. While a press is not allowed (a tool
    /// in that hand, a menu up, a ladder) touches are still noticed and latched, so a tip already inside an object when
    /// the block ends does not press it then.</para>
    /// <para>Known limits: a fingertip is a point, and the check is once per simulation frame, so the hand's own movement
    /// since the last frame is swept too (in the body's frame, so a ship's or the body's motion does not count);
    /// something moving through a still hand faster than a few centimetres a frame is not caught.</para>
    /// </remarks>
    internal static class FingerPress
    {
        /// <summary>Debug: no finger pressing (<see cref="Rendering.RenderDebug"/> FingerPress=0).</summary>
        public static bool Off { get; set; }

        /// <summary>Debug: every use object can be touched, not only the allow-list (<see cref="Rendering.RenderDebug"/> FingerPressAll=1).</summary>
        public static bool All { get; set; }

        /// <summary>
        /// The use objects a fingertip may press: the ones whose whole purpose is a push-button, that do one thing, once,
        /// and cost nothing if a swinging hand sets them off. Left out on purpose (hands at rest or swinging would trigger
        /// them): doors (<c>door</c>, <c>advanceddoor</c>), seats (<c>cockpit</c>, <c>cryopod</c>), terminals
        /// (<c>terminal</c>, <c>services</c>, <c>store</c>, <c>ATM</c>, <c>contract</c>, the jukebox's and vending machine's
        /// screens), cargo and inventory (<c>inventory</c>, <c>conveyor</c>), medical rooms and the like (<c>block</c>, held
        /// use), <c>ladder</c>, <c>wardrobe</c>, and the vending machine's Buy, which spends money.
        /// </summary>
        private static readonly Type[] PressSwitches =
        {
            typeof(SpaceEngineers.Game.Entities.UseObjects.MyUseObjectPanelButton),
            typeof(SpaceEngineers.Game.Entities.UseObjects.VendingMachine.MyUseObjectJukeboxNext),
            typeof(SpaceEngineers.Game.Entities.UseObjects.VendingMachine.MyUseObjectJukeboxPrevious),
            typeof(SpaceEngineers.Game.Entities.UseObjects.VendingMachine.MyUseObjectJukeboxPause),
            typeof(SpaceEngineers.Game.Entities.UseObjects.VendingMachine.MyUseObjectVendingMachineNext),
            typeof(SpaceEngineers.Game.Entities.UseObjects.VendingMachine.MyUseObjectVendingMachinePrevious),
        };

        /// <summary>Whether a fingertip may press this use object: it is a push-button of the allow-list, or <see cref="All"/> is on.</summary>
        internal static bool IsPressSwitch(object use)
        {
            if (use == null)
                return false;
            if (All)
                return true;
            foreach (Type type in PressSwitches)
            {
                if (type.IsInstanceOfType(use))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Metres from the controller's grip pose, along its aim, to the tip of the index finger. The grip pose is the
        /// middle of the closed hand; the avatar's palm bone sits <c>HandIK.WristBack</c> (0.08 m) behind it and a hand
        /// is about 0.17 m from the wrist to the index fingertip, which puts the tip 0.09 m ahead of the grip.
        /// </summary>
        public static double FingertipReach => ReachDebug > 0.0 ? ReachDebug : DefaultFingertipReach;

        public const double DefaultFingertipReach = 0.09;

        /// <summary>RenderDebug FingerReach=&lt;millimetres&gt;: <see cref="FingertipReach"/> to try, for fitting the tip to the glove (0 = the default).</summary>
        internal static double ReachDebug;

        /// <summary>A use object is touched when the tip is inside its detector box or this close to it, metres.</summary>
        public const double TouchMargin = 0.015;

        /// <summary>
        /// The tip has to leave a touched object by this much beyond <see cref="TouchMargin"/> before it can be pressed
        /// again, metres (1 cm of hysteresis, so a trembling hand on the edge presses once).
        /// </summary>
        public const double ReleaseMargin = 0.01;

        public const double ReleaseDistance = TouchMargin + ReleaseMargin;

        /// <summary>
        /// How wide the game's block query looks around the tip, metres. A block comes back when its cell box meets the
        /// sphere and the exact box test then decides; a detector can reach out of its block's cells (a door's, a seat's),
        /// so this is wider than the touch itself.
        /// </summary>
        public const double GatherRadius = 0.5;

        /// <summary>The hand's movement since the last frame is swept when it is no more than this (else it is a jump), metres.</summary>
        public const double MaxSweep = 0.25;

        private const double SweepStep = 0.01;
        private const int MaxSweepSamples = 32;

        /// <summary>A press is a click on the pressing hand: short, and about as strong as placing a block.</summary>
        public const float PulseAmplitude = 0.6f, PulseSeconds = 0.04f;

        /// <summary>Longer than this between two frames this hand was looked at (a pause, a menu) and the first look back only latches.</summary>
        private const double GapSeconds = 0.3;

        /// <summary>A candidate this near is kept for the log of what the fingertip is close to (at most a line every 1.5 s a hand, 200 a run).</summary>
        private const double NearDistance = 0.06;

        private const int MaxNearLogs = 200;
        private static readonly double NearLogEvery = 1.5 * Stopwatch.Frequency;

        private static int errors, useErrors, nearLogs;
        private static bool hooked;

        // ---- Game side ----

        /// <summary>A hook that cannot be made leaves the plugin running without finger pressing, and says so in the log.</summary>
        public static void Patch(Harmony harmony)
        {
            try
            {
                PatchGame(harmony);
            }
            catch (Exception e)
            {
                hooked = false;
                Log.Error(e, "Finger press could not be hooked; touching with a fingertip will not press anything");
            }
        }

        private static void PatchGame(Harmony harmony)
        {
            // The use object the game acts on is whatever the character's detector component answers; both detector
            // classes answer through UseObject (the closest one overrides it). Without this, nothing is pressed.
            Type closest = AccessTools.TypeByName("Sandbox.Game.Entities.Character.Components.MyCharacterClosestDetectorComponent")
                           ?? throw new MissingMemberException("MyCharacterClosestDetectorComponent");
            var targets = new[]
            {
                AccessTools.PropertyGetter(typeof(MyCharacterDetectorComponent), nameof(MyCharacterDetectorComponent.UseObject))
                    ?? throw new MissingMemberException(nameof(MyCharacterDetectorComponent), "UseObject"),
                AccessTools.DeclaredProperty(closest, nameof(MyCharacterDetectorComponent.UseObject))?.GetGetMethod(true)
                    ?? throw new MissingMemberException(closest.Name, "UseObject"),
            };
            foreach (var target in targets)
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(FingerPress), nameof(AnswerUseObject)));

            harmony.Patch(AccessTools.Method(typeof(MyCharacter), nameof(MyCharacter.UpdateAfterSimulation)),
                postfix: new HarmonyMethod(typeof(FingerPress), nameof(AfterSimulation)));
            hooked = true;
            Log.Info("Finger press: fingertip touch on use objects hooked (character Use, detector UseObject x2)");
        }

        /// <summary>The use object the detector answers with while a touch is being pressed (this thread, for the length of one call).</summary>
        [ThreadStatic]
        private static IMyUseObject answer;

        private static void AnswerUseObject(ref IMyUseObject __result)
        {
            if (answer != null)
                __result = answer;
        }

        /// <summary>One hand's touches and where its tip was last frame.</summary>
        private sealed class HandTouch
        {
            public readonly Touch Touch = new Touch();
            public Vector3D? PreviousInBody;
            public long SeenAt;
            public long NearLoggedAt;
            public IMyUseObject Held;

            public void Forget()
            {
                Touch.Reset();
                PreviousInBody = null;
                SeenAt = 0;
                Held = null;
            }
        }

        private static readonly HandTouch[] hands = { new HandTouch(), new HandTouch() };
        private static readonly Hand[] bothHands = { Hand.Left, Hand.Right };
        private static readonly List<MyEntity> nearby = new List<MyEntity>();

        /// <summary>Game thread, each simulation frame, for the local character.</summary>
        private static void AfterSimulation(MyCharacter __instance)
        {
            if (errors >= 3 || !hooked || __instance != MySession.Static?.LocalCharacter)
                return;
            try
            {
                bool live = !Off && VRSettings.FingerPress && VRInput.Active && HandTools.IsLocalOnFoot(__instance)
                            && !__instance.Closed && __instance.PositionComp != null;
                if (!live)
                {
                    ForgetAll(__instance);
                    return;
                }

                // Nothing is pressed up a ladder (Use there lets go of it) or while a menu has the focus; a hand with a tool
                // or the builder in it does not press either.
                object focused = MyScreenManager.GetScreenWithFocus();
                bool building = (MyCubeBuilder.Static != null && MyCubeBuilder.Static.IsActivated)
                                || (MyClipboardComponent.Static != null && MyClipboardComponent.Static.IsActive);
                bool holdsTool = __instance.CurrentWeapon != null || building;
                MatrixD bodyFromWorld = __instance.PositionComp.WorldMatrixNormalizedInv;
                MatrixD bodyToWorld = MatrixD.Invert(bodyFromWorld);
                foreach (Hand hand in bothHands)
                    Step(hand, __instance, MayPress(__instance.IsOnLadder, focused, holdsTool, hand, VRSettings.DominantHand), bodyFromWorld, bodyToWorld);
            }
            catch (Exception e)
            {
                if (errors++ < 3)
                    Log.Error(e, "Finger press failed" + (errors >= 3 ? "; it stays off for this run" : string.Empty));
            }
        }

        /// <summary>
        /// Whether a hand may press now. Not up a ladder (Use there lets go of it), not unless the game screen has the
        /// focus (any menu, the terminal, the toolbar config and the chat all take it), and not the hand that holds a tool
        /// or the builder (the dominant one: <see cref="HandTools"/> holds a tool in it). Seated is out before this: no hand is
        /// looked at unless the player is on foot (<see cref="HandTools.IsLocalOnFoot"/>).
        /// </summary>
        internal static bool MayPress(bool onLadder, object focusedScreen, bool holdsTool, Hand hand, Hand dominant) =>
            !onLadder && focusedScreen is MyGuiScreenGamePlay && !(holdsTool && hand == dominant);

        private static void ForgetAll(MyCharacter character)
        {
            foreach (HandTouch state in hands)
            {
                if (state.Held != null && character != null)
                    Finish(character, state.Held);
                state.Forget();
            }
        }

        private static void Step(Hand hand, MyCharacter character, bool armed, in MatrixD bodyFromWorld, in MatrixD bodyToWorld)
        {
            HandTouch state = hands[(int)hand];
            // This frame's head, not the previous draw's camera: a body in motion would leave the tip a frame behind the hand.
            if (!HandWorld.TryGetNow(character, hand, out MatrixD grip, out MatrixD aim, out _))
            {
                if (state.Held != null)
                    Finish(character, state.Held);
                state.Forget();
                return;
            }

            Vector3D tip = Fingertip(grip, aim);
            Tracking.HandResidual.NotePress(hand, tip);
            long now = Stopwatch.GetTimestamp();
            if (state.SeenAt == 0 || now - state.SeenAt > GapSeconds * Stopwatch.Frequency)
            {
                armed = false;
                state.PreviousInBody = null;
            }
            state.SeenAt = now;

            // Where the tip was, in the body's frame, brought to the world as it stands now: the hand's own movement.
            Vector3D from = tip;
            if (state.PreviousInBody.HasValue)
            {
                Vector3D previous = Vector3D.Transform(state.PreviousInBody.Value, bodyToWorld);
                if (Vector3D.DistanceSquared(previous, tip) <= MaxSweep * MaxSweep)
                    from = previous;
            }
            state.PreviousInBody = Vector3D.Transform(tip, bodyFromWorld);

            state.Touch.Begin();
            Gather(state.Touch, character, from, tip);
            Touch.Outcome result = state.Touch.End(armed);

            if (result.Released != null)
            {
                Finish(character, (IMyUseObject)result.Released);
                state.Held = null;
            }
            if (result.Pressed)
            {
                state.Held = (IMyUseObject)result.Press.Key;
                Press(hand, character, state.Held, result.Press);
            }
            else if (armed && result.Held != null && state.Held != null && state.Held.ContinuousUsage)
            {
                Continue(character, state.Held);
            }
            if (result.Nearest.HasValue)
                LogNear(hand, state, result.Nearest.Value, now);
        }

        /// <summary>Every detector box of every block near the tip, as touch candidates.</summary>
        private static void Gather(Touch touch, MyCharacter character, Vector3D from, Vector3D tip)
        {
            nearby.Clear();
            var sphere = new BoundingSphereD(tip, GatherRadius);
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
                        if (!IsPressSwitch(use) || use.SupportedActions == UseActionEnum.None || ReferenceEquals(use.Owner, character))
                            continue;
                        MatrixD box = (MatrixD)pair.Value.Matrix * world;
                        double distance = SweptDistanceToBox(box, from, tip);
                        if (distance <= NearDistance)
                            touch.Add(use, distance, Vector3D.Distance(tip, box.Translation), use.Dummy?.Name ?? pair.Value.DetectorName);
                    }
                }
            }
            nearby.Clear();
        }

        /// <summary>The Use key's press of a use object: the character's own <c>Use()</c> with the detector answering with this object.</summary>
        private static void Press(Hand hand, MyCharacter character, IMyUseObject use, Touch.Contact contact)
        {
            try
            {
                MyCharacterDetectorComponent detector = character.GetDetectorComponent();
                if (detector == null)
                    return;
                answer = use;
                try
                {
                    character.Use();
                }
                finally
                {
                    answer = null;
                }
                Haptics.Pulse(hand, PulseAmplitude, PulseSeconds);
                Log.Info($"Finger press: {hand} fingertip pressed '{contact.Tag}' {use.GetType().Name} on {Describe(use.Owner)} " +
                         $"({contact.Distance * 100:0.0} cm from the box; primary {use.PrimaryAction}, secondary {use.SecondaryAction})");
            }
            catch (Exception e)
            {
                if (useErrors++ < 3)
                    Log.Error(e, $"Finger press: the game's Use failed on {Describe(use?.Owner)}");
            }
        }

        /// <summary>A held use goes on while the tip stays in (the key's held calls this every frame).</summary>
        private static void Continue(MyCharacter character, IMyUseObject use)
        {
            try
            {
                answer = use;
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
                    Log.Error(e, $"Finger press: the game's UseContinues failed on {Describe(use.Owner)}");
            }
        }

        /// <summary>The tip left a held use (the key's release).</summary>
        private static void Finish(MyCharacter character, IMyUseObject use)
        {
            try
            {
                if (character.GetDetectorComponent() == null)
                    return;
                answer = use;
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
                    Log.Error(e, $"Finger press: the game's UseFinished failed on {Describe(use.Owner)}");
            }
        }

        private static void LogNear(Hand hand, HandTouch state, Touch.Contact nearest, long now)
        {
            if (nearLogs >= MaxNearLogs || now - state.NearLoggedAt < NearLogEvery)
                return;
            state.NearLoggedAt = now;
            nearLogs++;
            Log.Info($"Finger press: {hand} fingertip {nearest.Distance * 100:0.0} cm from '{nearest.Tag}' " +
                     $"{(nearest.Key as IMyUseObject)?.GetType().Name} on {Describe((nearest.Key as IMyUseObject)?.Owner)}");
        }

        private static string Describe(VRage.ModAPI.IMyEntity owner)
        {
            if (owner == null)
                return "nothing";
            return owner is MyCubeBlock block && block.BlockDefinition != null
                ? $"{owner.GetType().Name} {block.BlockDefinition.Id.SubtypeName}"
                : owner.GetType().Name;
        }

        // ---- The maths: no game state, so it can be run on its own ----

        /// <summary>The tip of the index finger in the world: the grip pose's position, <see cref="FingertipReach"/> along the aim.</summary>
        internal static Vector3D Fingertip(in MatrixD grip, in MatrixD aim)
        {
            Vector3D forward = aim.Forward;
            double length = forward.Length();
            return length > 1e-9 ? grip.Translation + forward * (FingertipReach / length) : grip.Translation;
        }

        /// <summary>
        /// Metres from a point to an oriented box, 0 when it is inside. The box is given as the game keeps a detector: a
        /// matrix whose position is the box's centre, whose axes (the rows) point along its sides and whose scale is its
        /// size (<c>new MyOrientedBoundingBoxD(matrix)</c>). A box with a flat side has no inside to touch and answers infinity.
        /// </summary>
        internal static double DistanceToBox(in MatrixD box, Vector3D point)
        {
            var frame = new BoxFrame(box);
            return frame.Distance(point);
        }

        /// <summary>The nearest the box comes to the path from <paramref name="from"/> to <paramref name="to"/>, sampled every centimetre or so.</summary>
        internal static double SweptDistanceToBox(in MatrixD box, Vector3D from, Vector3D to)
        {
            var frame = new BoxFrame(box);
            double best = frame.Distance(to);
            Vector3D path = to - from;
            double length = path.Length();
            if (best == 0.0 || length < 1e-6)
                return best;
            int samples = (int)Math.Min(MaxSweepSamples, Math.Ceiling(length / SweepStep));
            for (int i = 0; i < samples; i++)
            {
                double d = frame.Distance(from + path * (i / (double)samples));
                if (d < best)
                    best = d;
            }
            return best;
        }

        private readonly struct BoxFrame
        {
            private readonly Vector3D center, x, y, z, half;
            private readonly bool valid;

            public BoxFrame(in MatrixD box)
            {
                center = box.Translation;
                Vector3D right = box.Right, up = box.Up, back = box.Backward;
                double lx = right.Length(), ly = up.Length(), lz = back.Length();
                valid = lx > 1e-9 && ly > 1e-9 && lz > 1e-9;
                x = valid ? right / lx : Vector3D.Zero;
                y = valid ? up / ly : Vector3D.Zero;
                z = valid ? back / lz : Vector3D.Zero;
                half = new Vector3D(lx, ly, lz) * 0.5;
            }

            public double Distance(Vector3D point)
            {
                if (!valid)
                    return double.PositiveInfinity;
                Vector3D d = point - center;
                double ox = Math.Max(Math.Abs(Vector3D.Dot(d, x)) - half.X, 0.0);
                double oy = Math.Max(Math.Abs(Vector3D.Dot(d, y)) - half.Y, 0.0);
                double oz = Math.Max(Math.Abs(Vector3D.Dot(d, z)) - half.Z, 0.0);
                return Math.Sqrt(ox * ox + oy * oy + oz * oz);
            }
        }

        /// <summary>
        /// One hand's touches: which use objects the tip is at this frame, which of them it has already pressed, and which
        /// one is pressed now. Every frame: <see cref="Begin"/>, <see cref="Add"/> for each candidate, <see cref="End"/>.
        /// </summary>
        internal sealed class Touch
        {
            internal struct Contact
            {
                public object Key;
                public double Distance, Center;
                public string Tag;
            }

            internal struct Outcome
            {
                /// <summary><see cref="Press"/> is a new press this frame.</summary>
                public bool Pressed;
                public Contact Press;

                /// <summary>An object that was held and is not any more (the tip left it, or another was pressed, or pressing stopped).</summary>
                public object Released;

                /// <summary>The object pressed by this touch that the tip is still at.</summary>
                public object Held;

                /// <summary>The nearest candidate this frame, touching or not.</summary>
                public Contact? Nearest;
            }

            private readonly List<Contact> contacts = new List<Contact>();
            private readonly HashSet<object> latched = new HashSet<object>();
            private readonly List<object> scratch = new List<object>();
            private object held;

            public void Begin() => contacts.Clear();

            /// <summary>A candidate: <paramref name="distance"/> is the metres from the tip to its box (0 inside).</summary>
            public void Add(object key, double distance, double centerDistance, string tag = null)
            {
                contacts.Add(new Contact { Key = key, Distance = distance, Center = centerDistance, Tag = tag });
            }

            public Outcome End(bool armed)
            {
                var outcome = new Outcome();

                // Leaving: a latched object that is no longer within the release distance is free to be pressed again.
                scratch.Clear();
                foreach (object key in latched)
                {
                    if (!Present(key))
                        scratch.Add(key);
                }
                foreach (object key in scratch)
                    latched.Remove(key);
                if (held != null && !latched.Contains(held))
                {
                    outcome.Released = held;
                    held = null;
                }

                // The nearest touched object: inside beats near, then the one whose middle is closest.
                bool found = false;
                Contact best = default;
                Contact? nearest = null;
                foreach (Contact contact in contacts)
                {
                    if (!nearest.HasValue || Better(contact, nearest.Value))
                        nearest = contact;
                    if (contact.Distance <= TouchMargin && (!found || Better(contact, best)))
                    {
                        best = contact;
                        found = true;
                    }
                }
                outcome.Nearest = nearest;

                if (found && !latched.Contains(best.Key))
                {
                    latched.Add(best.Key);
                    if (armed)
                    {
                        if (held != null && outcome.Released == null)
                            outcome.Released = held;
                        held = best.Key;
                        outcome.Pressed = true;
                        outcome.Press = best;
                    }
                }

                // Not allowed to press now: whatever was held is let go, but stays latched.
                if (!armed && held != null)
                {
                    outcome.Released = outcome.Released ?? held;
                    held = null;
                }
                outcome.Held = held;
                return outcome;
            }

            /// <summary>Forget everything (the hand is not tracked, or pressing is off).</summary>
            public void Reset()
            {
                contacts.Clear();
                latched.Clear();
                held = null;
            }

            private bool Present(object key)
            {
                foreach (Contact contact in contacts)
                {
                    if (contact.Distance <= ReleaseDistance && Equals(contact.Key, key))
                        return true;
                }
                return false;
            }

            private static bool Better(Contact a, Contact b) =>
                a.Distance < b.Distance || (a.Distance == b.Distance && a.Center < b.Center);
        }
    }
}
