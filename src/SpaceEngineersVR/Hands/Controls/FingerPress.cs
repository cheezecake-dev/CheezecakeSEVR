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
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.Entity.UseObject;
using VRageMath;
using VRageRender.Animations;

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
    internal static partial class FingerPress
    {
        /// <summary>Debug: no finger pressing (<see cref="Rendering.RenderDebug"/> FingerPress=0).</summary>
        public static bool Off { get; set; }

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

        /// <summary>Whether a fingertip may press this use object: it is a push-button of the allow-list.</summary>
        internal static bool IsPressSwitch(object use)
        {
            if (use == null)
                return false;
            bool all = false;
            TestAidsPressAll(ref all);
            return all || IsPushButton(use);
        }

        /// <summary>A test aid (renderdebug FingerPressAll=1) lets the fingertip press every use object, not only the allow-list.</summary>
        static partial void TestAidsPressAll(ref bool all);

        /// <summary>Whether this use object is one of the push-buttons the fingertip owns, whatever a test aid allows (<see cref="GrabUse"/> leaves them alone).</summary>
        internal static bool IsPushButton(object use)
        {
            if (use == null)
                return false;
            foreach (Type type in PressSwitches)
            {
                if (type.IsInstanceOfType(use))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Metres from the index finger's last joint (SE_Rig?_Index_3) to the glove's fingertip, along the bone (a finger
        /// points along its bones' -X): the furthest of the 65 glove vertices skinned at least half to that bone
        /// (RightGlove and LeftGlove in SE_astronaut_LOD0.mwm, bind pose), which lie within 0.3 cm of the bone's line.
        /// </summary>
        public static double FingertipReach
        {
            get
            {
                double reach = DefaultFingertipReach;
                TestAidsFingertipReach(ref reach);
                return reach;
            }
        }

        public const double DefaultFingertipReach = 0.033;

        /// <summary>A test aid (renderdebug FingerReach=&lt;millimetres&gt;): another <see cref="FingertipReach"/> to try, for fitting the tip to the glove.</summary>
        static partial void TestAidsFingertipReach(ref double reach);

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

        private static readonly FeatureGuard guard = new FeatureGuard(nameof(FingerPress));
        private static int useErrors, nearLogs;
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
                PatchRegistry.Lost(e, "Finger press could not be hooked; touching with a fingertip will not press anything");
                throw;
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
            if (!hooked || __instance != MySession.Static?.LocalCharacter || !guard.On)
                return;
            try
            {
                bool live = !Off && VRSettings.FingerPress && VRInput.Active && (HandTools.IsLocalOnFoot(__instance) || SeatedFirstPerson(__instance))
                            && !__instance.Closed && __instance.PositionComp != null;
                if (!live)
                {
                    ForgetAll(__instance);
                    return;
                }

                // Nothing is pressed up a ladder (Use there lets go of it) or while a menu has the focus; a hand with a tool
                // in it does not press either, though a hand with a block in it does (IsHandTool). Seated, the tool is the
                // ship's, not in the hand (as screen touch has it).
                object focused = MyScreenManager.GetScreenWithFocus();
                bool holdsTool = IsHandTool(__instance.CurrentWeapon) && !__instance.IsSitting;
                MatrixD bodyFromWorld = __instance.PositionComp.WorldMatrixNormalizedInv;
                MatrixD bodyToWorld = MatrixD.Invert(bodyFromWorld);
                foreach (Hand hand in bothHands)
                {
                    // A hand holding the seat's stick or throttle is drawn on it, not where the controller is: it presses nothing.
                    bool onControl = __instance.IsSitting && (ControlGrip.Holds(hand) || Cockpits.CockpitControls.HandBusy(hand));
                    Step(hand, __instance, !onControl && MayPress(__instance.IsOnLadder, focused, holdsTool, hand, VRSettings.DominantHand), bodyFromWorld, bodyToWorld);
                }
            }
            catch (Exception e)
            {
                guard.Failed(e, "Finger press failed");
            }
        }

        /// <summary>
        /// Whether a hand may press now. Not up a ladder (Use there lets go of it), not unless the game screen has the
        /// focus (any menu, the terminal, the toolbar config and the chat all take it), and not the hand that holds a tool
        /// (the dominant one: <see cref="HandTools"/> holds a tool in it; see <see cref="IsHandTool"/>). Only on foot or seated
        /// in first person (<see cref="SeatedFirstPerson"/>), where the hands are drawn on the controllers, is a hand looked at at all.
        /// </summary>
        internal static bool MayPress(bool onLadder, object focusedScreen, bool holdsTool, Hand hand, Hand dominant) =>
            !onLadder && focusedScreen is MyGuiScreenGamePlay && !(holdsTool && hand == dominant);

        /// <summary>
        /// What the character holds is a hand tool or weapon (a grinder, welder or drill, a gun), which keeps the dominant hand
        /// from pressing and from grabbing to use (<see cref="GrabUse"/>). The block placer is not one: the game puts it in
        /// hand with a block or the colour tool (MyToolbarItemCubeBlock.Activate switches to MyObjectBuilder_CubePlacer), and it
        /// has no model (MyBlockPlacerBase.Init gives it none), so the hand holds nothing and a fingertip or a grip on something
        /// usable uses it as an empty hand's does. Build mode does not claim the right grip (<see cref="BuildMode"/>: the left
        /// stick click turns the block), so the grip is an empty hand's everywhere.
        /// </summary>
        internal static bool IsHandTool(object weapon) => weapon != null && !(weapon is MyBlockPlacerBase);

        /// <summary>
        /// The local character sits in the seat it controls and sees from it in first person, so its arms are on the
        /// controllers in the cab (as Grab to use reaches from a seat): a camera block, a remote control, a turret or the
        /// seat's third-person camera is no place to press from.
        /// </summary>
        internal static bool SeatedFirstPerson(MyCharacter character)
        {
            MySession session = MySession.Static;
            return character != null && session != null && character == session.LocalCharacter && character.IsSitting && !character.IsDead
                   && session.ControlledEntity is MyCockpit cockpit && cockpit.Pilot == character
                   && session.CameraController == cockpit && (cockpit.IsInFirstPersonView || cockpit.ForceFirstPersonCamera);
        }

        /// <summary>
        /// A use object the fingertip may press for this character: a push-button (<see cref="IsPressSwitch"/>) that does
        /// something, not the character's own, and not the seat it sits in (the hands rest in it; Use there would get up).
        /// </summary>
        private static bool Pressable(IMyUseObject use, MyCharacter character) =>
            IsPressSwitch(use) && use.SupportedActions != UseActionEnum.None && !ReferenceEquals(use.Owner, character)
            && !(use.Owner is MyCockpit seat && seat.Pilot == character);

        private static readonly List<MyEntity> nearButton = new List<MyEntity>();

        /// <summary>
        /// Whether <paramref name="point"/> is within <paramref name="range"/> metres of the box of a push-button the
        /// fingertip presses (not the character's own), and which (<paramref name="what"/>). Game thread.
        /// </summary>
        internal static bool NearButton(MyCharacter character, Vector3D point, double range, out string what)
        {
            what = null;
            nearButton.Clear();
            var sphere = new BoundingSphereD(point, GatherRadius);
            MyGamePruningStructure.GetAllEntitiesInSphere(ref sphere, nearButton);
            try
            {
                foreach (MyEntity entity in nearButton)
                {
                    if (entity == null || entity.Closed || entity.MarkedForClose || entity.PositionComp == null || entity.Components == null)
                        continue;
                    if (!entity.Components.TryGet(out MyUseObjectsComponentBase found) || !(found is MyUseObjectsComponent component))
                        continue;
                    MatrixD world = entity.PositionComp.WorldMatrixRef;
                    var detectors = component.DetectorInteractiveObjects;
                    // The game edits this table under its own lock when a block's detectors change.
                    lock (detectors)
                    {
                        foreach (KeyValuePair<uint, MyUseObjectsComponent.DetectorData> pair in detectors)
                        {
                            IMyUseObject use = pair.Value.UseObject;
                            if (!Pressable(use, character))
                                continue;
                            double distance = DistanceToBox((MatrixD)pair.Value.Matrix * world, point);
                            if (distance > range)
                                continue;
                            what = $"'{use.Dummy?.Name ?? pair.Value.DetectorName}' {use.GetType().Name} on {Describe(use.Owner)}, {distance * 100:0} cm from its box";
                            return true;
                        }
                    }
                }
                return false;
            }
            finally
            {
                nearButton.Clear();
            }
        }

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
            if (!HandWorld.TryGetNow(character, hand, out MatrixD grip, out _, out _))
            {
                if (state.Held != null)
                    Finish(character, state.Held);
                state.Forget();
                return;
            }

            Vector3D tip = Fingertip(hand, grip);
            Tracking.HandResidual.NotePress(hand, tip);
            tip = Reach.Limit(character, tip);
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
                // The game edits this table under its own lock when a block's detectors change.
                lock (detectors)
                {
                    foreach (KeyValuePair<uint, MyUseObjectsComponent.DetectorData> pair in detectors)
                    {
                        IMyUseObject use = pair.Value.UseObject;
                        if (!Pressable(use, character))
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

        // ---- The index fingertip: the drawn glove's ----
        // The touches, the presses and the hand stop all take the fingertip to be the drawn glove's: the index finger's
        // last joint as the arm solve left it (HandIK.SolveArm, after the fingers are curled), FingertipReach on along the
        // bone, kept relative to the palm bone; and the palm bone where the arm solve is asked to put it for the hand's
        // pose (HandIK.PalmTarget: on the runtime's palm pose, or on the grip pose where there is none). So the tip is the
        // one the player sees, wherever the runtime's grip and aim poses are, before anything holds the drawn hand back
        // (the hand stop's push, the arm's reach, the pull back onto a seat's control).

        /// <summary>The right index finger's last joint in the right palm bone's frame at the bind pose (SE_astronaut.mwm), metres (the left: Z mirrored).</summary>
        private static readonly Vector3 BindJointInRightPalm = new Vector3(-0.1911f, 0.0083f, 0.0288f);

        /// <summary>That bone's -X (the way the finger points) in the right palm bone's frame at the bind pose (the left: Z mirrored).</summary>
        private static readonly Vector3 BindAlongInRightPalm = new Vector3(-0.9954f, -0.0873f, -0.0392f);

        private static readonly Vector3[] drawnTip = new Vector3[2];
        private static readonly bool[] drawnTipNoted = new bool[2];

        /// <summary>
        /// The drawn index fingertip in the hand's palm bone's frame, metres (the frame of <see cref="HandIK.PalmTarget"/>):
        /// as the arm solve last posed the finger (<see cref="NoteDrawnFinger"/>); before it has, the glove's at the bind pose,
        /// the finger straight (right hand (-0.224, +0.006, +0.030)).
        /// </summary>
        internal static Vector3 TipInPalm(Hand hand)
        {
            int i = (int)hand;
            if (drawnTipNoted[i])
                return drawnTip[i];
            float mirror = hand == Hand.Left ? -1f : 1f;
            Vector3 joint = new Vector3(BindJointInRightPalm.X, BindJointInRightPalm.Y, BindJointInRightPalm.Z * mirror);
            Vector3 along = new Vector3(BindAlongInRightPalm.X, BindAlongInRightPalm.Y, BindAlongInRightPalm.Z * mirror);
            return joint + along * (float)FingertipReach;
        }

        /// <summary>
        /// Game thread, from the arm solve, once the hand's fingers are posed (<see cref="HandIK"/>): keeps where the index
        /// fingertip is drawn relative to the palm bone. <paramref name="palm"/> and <paramref name="indexTip"/> index
        /// <paramref name="bones"/>: the palm bone and the index finger's last joint (-1 when the model has none: the tip
        /// stays as it was).
        /// </summary>
        internal static void NoteDrawnFinger(Hand hand, MyCharacterBone[] bones, int palm, int indexTip)
        {
            if (bones == null || palm < 0 || indexTip < 0 || palm >= bones.Length || indexTip >= bones.Length)
                return;
            Matrix joint = bones[indexTip].AbsoluteTransform;
            Vector3 back = joint.Right;
            float length = back.Length();
            if (length < 1e-6f)
                return;
            Vector3 tip = joint.Translation - back * ((float)FingertipReach / length);
            Vector3 inPalm = Vector3.Transform(tip, Matrix.Invert(bones[palm].AbsoluteTransform));
            if (!inPalm.IsValid() || inPalm.Length() > 0.5f)
                return;
            drawnTip[(int)hand] = inPalm;
            drawnTipNoted[(int)hand] = true;
        }

        /// <summary>The index fingertip in the tracking space, for this hand state (see the remarks above <see cref="TipInPalm"/>).</summary>
        internal static Vector3 FingertipInTracking(Hand hand, in HandState state) =>
            Vector3.Transform(TipInPalm(hand), HandIK.PalmTarget(hand, state));

        /// <summary>
        /// The index fingertip where <paramref name="trackingTo"/> takes the tracking space (the world, or the body's model
        /// space), for this hand state.
        /// </summary>
        internal static Vector3D Fingertip(Hand hand, in HandState state, in MatrixD trackingTo) =>
            Vector3D.Transform((Vector3D)FingertipInTracking(hand, state), trackingTo);

        /// <summary>
        /// The index fingertip, from the hand's grip pose placed in the world (or any space) as <see cref="HandWorld.TryGet"/>
        /// and <see cref="HandWorld.TryGetNow"/> place it from <see cref="VRInput.Get"/>: the tip is held where it is
        /// relative to the grip pose (the palm and grip poses are on the one controller), and carried by
        /// <paramref name="grip"/>.
        /// </summary>
        internal static Vector3D Fingertip(Hand hand, in MatrixD grip)
        {
            HandState state = VRInput.Get(hand);
            Vector3 inGrip = Vector3.Transform(FingertipInTracking(hand, state), Matrix.Invert(state.Grip));
            return Vector3D.Transform((Vector3D)inGrip, grip);
        }

        // ---- The maths: no game state, so it can be run on its own ----

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
