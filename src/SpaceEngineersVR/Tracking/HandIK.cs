using System;
using HarmonyLib;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using VRageMath;
using VRageRender.Animations;

namespace SpaceEngineersVR.Tracking
{
    /// <summary>
    /// The local character's arms follow the tracked controllers, every game frame (<see cref="ArmSolver"/>). The game
    /// only puts the hands on a held item's targets (MyCharacter.UpdateHandBones, and the right arm not at all while
    /// sitting); here the palm's target is the runtime's palm pose when it has one, else the controller's grip pose
    /// through the handle offset (<see cref="HandGrip"/>, <see cref="PalmTarget"/>). The fingers curl with the trigger
    /// and squeeze, and lift off where the controller senses no finger (<see cref="IndexCurl"/>, <see cref="ThumbCurl"/>).
    /// </summary>
    /// <remarks>
    /// The hook is a postfix on MyCharacter.UpdateAfterSimulation. By then the frame's animation is applied
    /// (MySessionComponentAnimationSystem.PostProcessAnimations, before the simulation) and so is the game's own item
    /// IK (UpdateHeadAndWeapon, in the same method), and nothing touches the bones before they are sent to the renderer
    /// (MyRenderComponentSkinnedEntity.Draw sends BoneAbsoluteTransforms as they stand). The next frame's animation sets
    /// every bone again, so an arm that is not solved in a frame goes back to the animation by itself.
    /// Which arms: the left one always. The right one (the one the game holds items in) when it is empty, or when
    /// seated; with an item in hand the game's IK keeps it, which follows the item, which follows the hand.
    /// The hands are placed in the body's own space by the head matrix this frame's camera is built from
    /// (<see cref="HandWorld.TryGetTrackingToBody"/>), so they ride with the body at any speed. Not covered: the bones
    /// are fixed when the simulation ticks, from the controllers as last read, and the renderer's late head correction
    /// (<see cref="CameraHeadLink"/>) does not reach them. The headset's controllers are therefore read predicted for when
    /// the frame drawn from them is shown, by the lead XrInput measures (RenderDebug HandIKLead=0 turns that off);
    /// <see cref="HandResidual"/> measures what is left.
    /// </remarks>
    internal static class HandIK
    {
        /// <summary>How far the fingers curl with no squeeze or trigger at all: a relaxed hand, not a flat one.</summary>
        private const float RelaxedCurl = 0.2f;

        /// <summary>The thumb with nothing pressed: resting on the controller's face.</summary>
        private const float RelaxedThumb = 0.4f;

        /// <summary>The index finger off the trigger (a controller with a trigger touch sensor): lifted a little off it.</summary>
        private const float LiftedIndex = 0.05f;

        /// <summary>The thumb touching nothing (a controller with thumb touch sensors): lifted off the controller's face.</summary>
        private const float LiftedThumb = 0f;

        /// <summary>How far a finger moves toward a touch sensor's new curl in one simulation frame: a lift or a landing takes about 80 ms at 60 Hz.</summary>
        private const float TouchStep = 0.2f;

        // ---- RenderDebug (HandIK...=) ----

        /// <summary>
        /// 0 off, 1 everything, 2 without finger curl, 3 the elbow from the body's hint only (not from the hand's twist
        /// and bend), 4 the game's own iterative solver (no hinge, no hint, no reach handling, no curl), for comparison.
        /// </summary>
        public static int Mode { get; private set; } = 1;

        /// <summary>Degrees about the grip's X, Y, Z (a hand held forward: pitch, roll, yaw), mirrored for the left hand.</summary>
        private static Vector3 turn;

        /// <summary>Millimetres along the grip's X, Y, Z (right, back toward the wrist, down the handle), mirrored for the left hand.</summary>
        private static Vector3 shift;

        private static ArmSolver.Settings settings = ArmSolver.Settings.Default;

        /// <summary>RenderDebug: a "HandIK..." key. False if it is not one of these.</summary>
        public static bool SetDebug(string name, int value)
        {
            switch (name.ToLowerInvariant())
            {
                case "handik": Mode = value; return true;
                case "handikpitch": turn.X = value; return true;
                case "handikroll": turn.Y = value; return true;
                case "handikyaw": turn.Z = value; return true;
                case "handikx": shift.X = value; return true;
                case "handiky": shift.Y = value; return true;
                case "handikz": shift.Z = value; return true;
                case "handikstretch": settings.Stretch = value / 100f; return true;
                case "handikshrug": settings.Shrug = value; return true;
                case "handiktwist": settings.TwistShare = value / 100f; return true;
                case "handikswing": settings.SwivelStep = value; return true;
                case "handikframe": HandWorld.PreviousCamera = value == 0; return true;
                case "handiklead": OpenXR.XrInput.Lead = value != 0; return true;
                default: return false;
            }
        }

        public static void ResetDebug()
        {
            Mode = 1;
            turn = shift = Vector3.Zero;
            settings = ArmSolver.Settings.Default;
            HandWorld.PreviousCamera = false;
            OpenXR.XrInput.Lead = true;
        }

        // ---- the hook ----

        private static int errors;
        private static long lastNote;

        /// <summary>Counts the local character's simulation frames; an arm not solved in the last couple starts its elbow afresh.</summary>
        private static int frame;

        /// <summary>The bone indices of the character last seen: the same skeleton lays out every character the same.</summary>
        private static MyCharacter indicesFor;
        private static readonly int[] leftFingers = new int[15], rightFingers = new int[15];
        private static ArmSolver.Chain leftChain, rightChain;

        public static void Patch(Harmony harmony)
        {
            try
            {
                harmony.Patch(AccessTools.Method(typeof(MyCharacter), nameof(MyCharacter.UpdateAfterSimulation)),
                    postfix: new HarmonyMethod(typeof(HandIK), nameof(AfterSimulation)));
            }
            catch (Exception e)
            {
                Log.Error(e, "Hand IK could not be hooked; the arms keep the game's animation");
            }
        }

        /// <summary>Game thread, each simulation frame, after the character's own update.</summary>
        private static void AfterSimulation(MyCharacter __instance)
        {
            if (Mode == 0 || errors >= 3 || !VRInput.Active)
                return;
            try
            {
                if (__instance != MySession.Static?.LocalCharacter || __instance.IsDead || __instance.Closed || __instance.IsOnLadder)
                    return;
                MyCharacterBone[] bones = __instance.AnimationController?.CharacterBones;
                if (bones == null || __instance.PositionComp == null)
                    return;

                Bones indices = Fields.Read(__instance);
                if (indices.Left.Start < 0 || indices.Right.Start < 0)
                    return;
                if (indicesFor != __instance)
                {
                    indicesFor = __instance;
                    FindFingers(__instance, 'L', leftFingers);
                    FindFingers(__instance, 'R', rightFingers);
                    leftChain = ArmSolver.Chain.Measure(bones, indices.Left.Start, indices.Left.Fore, indices.Left.Palm);
                    rightChain = ArmSolver.Chain.Measure(bones, indices.Right.Start, indices.Right.Fore, indices.Right.Palm);
                    Log.Info($"Hand IK: arm bones {indices.Left.Start}/{indices.Left.Fore}/{indices.Left.Palm} and {indices.Right.Start}/{indices.Right.Fore}/{indices.Right.Palm}, " +
                             $"forearm bones {leftChain?.Fore.Length ?? 0}/{rightChain?.Fore.Length ?? 0}, reach {leftChain?.Reach ?? 0f:F3}/{rightChain?.Reach ?? 0f:F3} m, " +
                             $"fingers found left {Found(leftFingers)} right {Found(rightFingers)} of 15");
                    if (leftChain == null || rightChain == null)
                        Log.Info("Hand IK: an arm is not the shape the solver knows (upper arm, forearm bones in a line, palm); that arm keeps the animation");
                }

                frame++;
                // A hand the camera places away from this body (the spectator's camera, a camera block, a camera still where
                // the character was before a teleport or a respawn) is not reached for: the arms stay as the animation has them.
                if (!HandWorld.TryGetTrackingToBody(__instance, out MatrixD trackingToModel, out ViewRefusal why))
                {
                    bool tracked = VRInput.Get(Hand.Left).Tracked || VRInput.Get(Hand.Right).Tracked;
                    ReportView(tracked ? why : ViewRefusal.None);
                    return;
                }
                HandResidual.Begin(__instance, HandWorld.LastBodySource);
                SolveArm(bones, Hand.Left, leftChain, leftFingers, trackingToModel);
                bool holding = __instance.CurrentWeapon != null && !__instance.IsSitting;
                if (!holding)
                    SolveArm(bones, Hand.Right, rightChain, rightFingers, trackingToModel);
                else
                    NoteHeld(Hand.Right, rightChain, trackingToModel);
                ReportView(ViewRefusal.None);
            }
            catch (Exception e)
            {
                Log.Error(e, ++errors < 3 ? "Hand IK failed" : "Hand IK failed again; switched off, the arms keep the game's animation");
            }
        }

        /// <summary>
        /// The hand's arm was put on its controller in the last simulation frames, from this frame's head (first person, on
        /// foot or seated), so what is on the arm (the astronaut's gauntlet, <see cref="Input.WristLook"/>) is where the
        /// controller is. False with the solver off, after it failed, in third person, and while the view is not the
        /// character's (the arms are then left to the animation).
        /// </summary>
        internal static bool ArmFollows(Hand hand)
        {
            ArmSolver.Chain chain = hand == Hand.Left ? leftChain : rightChain;
            return Mode != 0 && errors < 3 && chain != null && shown == ViewRefusal.None && frame - chain.SolvedFrame <= 2
                   && HandWorld.LastBodySource == HandWorld.BodySource.Head;
        }

        /// <summary>Where the palm bone sits relative to the controller's grip pose, with the RenderDebug turn and shift (row vectors).</summary>
        internal static Matrix PalmFromGrip(Hand hand) => HandGrip.PalmFromGrip(hand == Hand.Left, turn, shift / 1000f);

        /// <summary>
        /// The hand that holds an item is the game's (its arm IK takes the item's RightHand), not solved here; for the
        /// residual log (RenderDebug HandResidual=1) it is still measured against where the controller's palm belongs,
        /// which is how far the held item's hand is from the real one.
        /// </summary>
        private static void NoteHeld(Hand hand, ArmSolver.Chain chain, in MatrixD trackingToModel)
        {
            if (chain == null || !HandResidual.On)
                return;
            HandState state = VRInput.Get(hand);
            if (!state.Tracked)
                return;
            Matrix target = (Matrix)((MatrixD)PalmTarget(hand, state) * trackingToModel);
            HandResidual.Solved(hand, chain.Palm, target, target);
        }

        /// <summary>
        /// The palm bone's pose in the tracking space: on the runtime's palm pose when the hand has one this frame
        /// (<see cref="HandState.PalmTracked"/>), else on the grip pose through the handle offset (<see cref="HandGrip"/>).
        /// </summary>
        internal static Matrix PalmTarget(Hand hand, in HandState state) => state.PalmTracked
            ? HandGrip.PalmFromPalmPose(hand == Hand.Left, turn, shift / 1000f) * state.Palm
            : PalmFromGrip(hand) * state.Grip;

        /// <summary>Per hand: the palm pose placed the palm last time (null: not placed yet), for the log.</summary>
        private static readonly bool?[] fromPalmPose = new bool?[2];
        private static int sourceChanges;

        /// <summary>Per hand: the index and thumb curls the touch sensors ask for, eased (<see cref="TouchStep"/>); negative: not started.</summary>
        private static readonly float[] indexCurl = { -1f, -1f }, thumbCurl = { -1f, -1f };

        private static void SolveArm(MyCharacterBone[] bones, Hand hand, ArmSolver.Chain chain, int[] fingers, in MatrixD trackingToModel)
        {
            if (chain == null)
                return;
            HandState state = VRInput.Get(hand);
            if (!state.Tracked)
                return;
            if (frame - chain.SolvedFrame > 2)
                chain.Forget();
            chain.SolvedFrame = frame;
            bool left = hand == Hand.Left;
            LogPalmSource(hand, state.PalmTracked);
            // Tracking space to model space in double precision, as the world part of it is far from the origin.
            Matrix target = (Matrix)((MatrixD)PalmTarget(hand, state) * trackingToModel);
            // The other hand on the held tool's grip settles there (Hands.TwoHand).
            Hands.TwoHand.Settle(hand, ref target);

            if (Mode == 4)
            {
                ArmSolver.SolveGame(bones, chain, target);
                return;
            }
            ArmSolver.Settings use = settings;
            use.HandSwivel = Mode != 3;
            if (!ArmSolver.Solve(bones, chain, target, left ? -1 : 1, use, out ArmSolver.Result result))
                return;
            HandResidual.Solved(hand, chain.Palm, target, bones[chain.Palm].AbsoluteTransform);
            if (Mode != 2)
            {
                ArmSolver.Curl(bones, fingers,
                    IndexCurl(hand, state),
                    RelaxedCurl + (1f - RelaxedCurl) * state.Squeeze,
                    ThumbCurl(hand, state));
            }
            if (result.OutOfReach)
                Note($"Hand IK: {hand} hand {result.Distance:F2} m from the shoulder, the arm reaches {result.Reach:F2} m " +
                     $"(stretched x{result.Stretch:F2}, collarbone {result.Shrug:F0} deg), {result.Short * 100f:F0} cm short");
        }

        /// <summary>
        /// The index finger: on the trigger it curls with the pull, from relaxed; a controller that senses the finger off
        /// the trigger lifts it a little. Without a trigger sensor it is always on it.
        /// </summary>
        private static float IndexCurl(Hand hand, in HandState state)
        {
            float pulled = RelaxedCurl + (1f - RelaxedCurl) * state.Trigger;
            bool sensed = (state.TouchSensors & VRTouch.Trigger) != 0;
            bool on = !sensed || (state.Touch & VRTouch.Trigger) != 0 || state.Trigger > 0f;
            return Ease(indexCurl, (int)hand, on ? pulled : LiftedIndex, state.Trigger > 0f);
        }

        /// <summary>
        /// The thumb: resting on the controller's face it closes with the squeeze, as before; a controller that senses
        /// the thumb on none of its controls (stick, thumbrest, face buttons) lifts it off. Without thumb sensors it rests.
        /// </summary>
        private static float ThumbCurl(Hand hand, in HandState state)
        {
            float resting = RelaxedThumb + (1f - RelaxedThumb) * state.Squeeze;
            bool sensed = (state.TouchSensors & VRTouch.Thumb) != 0;
            const VRButtons thumbButtons = VRButtons.A | VRButtons.B | VRButtons.X | VRButtons.Y | VRButtons.StickClick | VRButtons.Menu;
            bool on = !sensed || (state.Touch & VRTouch.Thumb) != 0 || (state.Buttons & thumbButtons) != 0;
            return Ease(thumbCurl, (int)hand, on ? resting : LiftedThumb, false);
        }

        /// <summary>
        /// Moves a finger's curl toward <paramref name="wanted"/> by <see cref="TouchStep"/> a frame, so a touch sensor's
        /// on/off does not snap it; <paramref name="direct"/> (a trigger being pulled) follows at once.
        /// </summary>
        private static float Ease(float[] curls, int i, float wanted, bool direct)
        {
            float was = curls[i];
            float now = direct || was < 0f ? wanted : was + MathHelper.Clamp(wanted - was, -TouchStep, TouchStep);
            curls[i] = now;
            return now;
        }

        /// <summary>Logs which pose places each palm, the first time and when it changes (the first few changes only: a flickering palm pose would fill the log).</summary>
        private static void LogPalmSource(Hand hand, bool palmPose)
        {
            int i = (int)hand;
            if (fromPalmPose[i] == palmPose)
                return;
            bool first = !fromPalmPose[i].HasValue;
            fromPalmPose[i] = palmPose;
            if (!first && ++sourceChanges > 10)
                return;
            Log.Info($"Hand IK: {hand} palm placed from " + (palmPose
                ? "the runtime's palm pose (XR_EXT_palm_pose: glove palm surface on the pose)"
                : "the grip pose and the guessed handle offset (HandGrip)") +
                (!first && sourceChanges == 10 ? "; further changes are not logged" : string.Empty));
        }

        /// <summary>Why the arms were last left to the animation (None: they follow the hands); logged when it changes.</summary>
        private static ViewRefusal shown;

        private static void ReportView(ViewRefusal refusal)
        {
            if (refusal == shown)
                return;
            shown = refusal;
            Log.Info(refusal == ViewRefusal.None
                ? "Hand IK: the view is the character's again, the arms follow the hands"
                : $"Hand IK: arms left to the animation, {HandWorld.Explain(refusal)}");
        }

        /// <summary>The finger bones by name, SE_Rig{L|R}_{Index|Middle|Ring|Little|Thumb}_{1|2|3} (-1: the skeleton has none).</summary>
        private static void FindFingers(MyCharacter character, char side, int[] into)
        {
            string[] names = { "Index", "Middle", "Ring", "Little", "Thumb" };
            for (int finger = 0; finger < names.Length; finger++)
            {
                for (int joint = 0; joint < 3; joint++)
                {
                    character.AnimationController.FindBone($"SE_Rig{side}_{names[finger]}_{joint + 1}", out int index);
                    into[finger * 3 + joint] = index;
                }
            }
        }

        private static int Found(int[] fingers)
        {
            int found = 0;
            foreach (int index in fingers)
                found += index >= 0 ? 1 : 0;
            return found;
        }

        /// <summary>One note a second at most: a hand held out of reach is every frame.</summary>
        private static void Note(string text)
        {
            long now = Environment.TickCount;
            if (now - lastNote < 2000)
                return;
            lastNote = now;
            Log.Info(text);
        }

        private struct Arm
        {
            public int Start, Fore, Palm;
        }

        private struct Bones
        {
            public Arm Left, Right;
        }

        /// <summary>The character's arm bone indices (private fields, set when the skeleton was found from the character definition).</summary>
        private static class Fields
        {
            private static readonly AccessTools.FieldRef<MyCharacter, int> LeftStart = AccessTools.FieldRefAccess<MyCharacter, int>("m_leftHandIKStartBone");
            private static readonly AccessTools.FieldRef<MyCharacter, int> LeftFore = AccessTools.FieldRefAccess<MyCharacter, int>("m_leftForearmBone");
            private static readonly AccessTools.FieldRef<MyCharacter, int> LeftPalm = AccessTools.FieldRefAccess<MyCharacter, int>("m_leftHandIKEndBone");
            private static readonly AccessTools.FieldRef<MyCharacter, int> RightStart = AccessTools.FieldRefAccess<MyCharacter, int>("m_rightHandIKStartBone");
            private static readonly AccessTools.FieldRef<MyCharacter, int> RightFore = AccessTools.FieldRefAccess<MyCharacter, int>("m_rightForearmBone");
            private static readonly AccessTools.FieldRef<MyCharacter, int> RightPalm = AccessTools.FieldRefAccess<MyCharacter, int>("m_rightHandIKEndBone");

            public static Bones Read(MyCharacter c) => new Bones
            {
                Left = new Arm { Start = LeftStart(c), Fore = LeftFore(c), Palm = LeftPalm(c) },
                Right = new Arm { Start = RightStart(c), Fore = RightFore(c), Palm = RightPalm(c) },
            };
        }
    }
}
