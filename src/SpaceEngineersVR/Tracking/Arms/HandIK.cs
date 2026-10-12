using System;
using HarmonyLib;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using VRage.Game.Entity;
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
    /// seated; with an item in hand it is solved here too, onto the item's RightHand spot (which follows the hand), as
    /// the game's own IK for it does not stretch and leaves the glove short of the tool (RenderDebug HandIKHeld=0 leaves it to
    /// the game's).
    /// The hands are placed in the body's own space by the head matrix this frame's camera is built from
    /// (<see cref="HandWorld.TryGetTrackingToBody"/>), so they ride with the body at any speed. Not covered: the bones
    /// are fixed when the simulation ticks, from the controllers as last read, and the renderer's late head correction
    /// (<see cref="CameraHeadLink"/>) does not reach them. The headset's controllers are therefore read predicted for when
    /// the frame drawn from them is shown, by the lead XrInput measures (RenderDebug HandIKLead=0 turns that off);
    /// <see cref="HandResidual"/> measures what is left.
    /// </remarks>
    internal static partial class HandIK
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

        // The turn (degrees about the grip's X, Y, Z: in a hand held forward, pitch, roll, yaw) and the shift (millimetres along
        // them: right, back toward the wrist, down the handle) of the palm against the grip, both mirrored for the left hand, are
        // kept per controller profile (GripFit), not here.

        private static ArmSolver.Settings settings = ArmSolver.Settings.Default;

        /// <summary>
        /// The arm that holds a tool is solved here too, onto the tool's RightHand spot, with the stretch and the collarbone
        /// the other arm has, instead of by the game's own IK, which does not stretch (RenderDebug HandIKHeld=0: the game's).
        /// </summary>
        private static bool solveHeld = true;

        /// <summary>RenderDebug HandIKReach=1: each arm's reach logged every 2 s, in reach or not (for the body's place and the collarbone).</summary>
        private static bool reachLog;

        /// <summary>
        /// A hand on a tool stays on it: the hand that holds the tool, and the other hand drawn on its second grip
        /// (<see cref="ArmSolver.Settings.Pin"/>); past the stretch the arm lengthens on instead of the hand sliding along the
        /// handle as the shoulder sways (RenderDebug HandIKPin=0: it stops short).
        /// </summary>
        private static bool pinOnTool = true;

        private const bool SteadyByDefault = true;

        /// <summary>RenderDebug HandIKSway=0/1: the held arm's shoulder sway (idle breathing, the walk) as the animation has it, or damped (<see cref="SteadyShoulder"/>).</summary>
        private static bool steadyShoulder = SteadyByDefault;

        /// <summary>The held arm's shoulder follows the animation through a lag of this many seconds, and never more than <see cref="SwayHold"/> metres from it.</summary>
        private const float SwaySeconds = 0.8f, SwayHold = 0.03f;

        private static Vector3 swayShoulder;
        private static bool swayValid;

        /// <summary>The held arm's stretch (1: not stretched) and how far its shoulder was held off the animation (metres), as last solved; for <see cref="ToolSteady"/>.</summary>
        internal static float HeldStretch { get; private set; } = float.NaN;
        internal static float HeldSway { get; private set; }

        private static readonly long[] reachNoted = new long[2];

        /// <summary>RenderDebug HandIKEase=1: the left arm's stretch logged every frame while its hand eases onto or off a tool's grip, and three frames on.</summary>
        private static bool easeLog;

        private static float easeShare = float.NaN, easeLast = float.NaN;
        private static int easeTail;
        private static bool easeWas;

        /// <summary>
        /// The left arm's stretch as drawn last frame (<see cref="drawnFrame"/>), and whether it is still easing toward what the
        /// hand asks. Taking or letting go of a tool's grip, and after until it has caught up, the arm's stretch moves at most
        /// <see cref="EaseStep"/> a frame (<see cref="ArmSolver.Settings.EaseFrom"/>): the hand settles on a grip in 0.08 s and
        /// returns in 0.1 s, which moved an arm at its limit 0.05 to 0.09 a frame.
        /// </summary>
        private static float drawn;

        private static int drawnFrame = -10;
        private static bool easing;

        private const float EaseStep = 0.015f;

        /// <summary>The left arm on a tool's grip as last solved, for the two-hand trace (<see cref="Hands.TwoHand"/>).</summary>
        internal struct OffArm
        {
            /// <summary>The share of the way the hand is on the grip (0: not on it).</summary>
            public float OnTool;

            /// <summary>The arm's stretch, how far the wrist was drawn from where it holds the grip (metres), the wrist's bend and the forearm's twist (degrees).</summary>
            public float Stretch, Short, WristBend, Twist;

            /// <summary>The angle between the way the glove's closed hand points (wrist to the middle of the hand) and the way to the grip's middle, degrees.</summary>
            public float PointOff;

            /// <summary>The drawn palm against the game's own grip spot on the tool (the item's LeftHand): metres apart, and degrees turned from it.</summary>
            public float Spot, SpotTurn;
        }

        internal static OffArm LeftOnTool { get; private set; }

        /// <summary>The left palm's X axis as its forearm carried it at the last solve (<see cref="ArmSolver.Result.Carried"/>).</summary>
        private static Vector3 carried;

        /// <summary>
        /// The way the left forearm carried the palm at its last solve, if that was within the last couple of frames: what a
        /// hand on a tool's handle turns toward (<see cref="Hands.TwoHand"/>).
        /// </summary>
        internal static bool LeftCarried(out Vector3 x)
        {
            x = carried;
            return frame - drawnFrame <= 2 && carried.LengthSquared() > 0.5f;
        }

        // ---- the hook ----

        private static readonly FeatureGuard guard = new FeatureGuard(nameof(HandIK));
        private static long lastNote;

        /// <summary>Counts the local character's simulation frames; an arm not solved in the last couple starts its elbow afresh.</summary>
        private static int frame;

        /// <summary>The solver's frame count, as the Hand IK jump lines number frames (<see cref="Hands.HeldTrace"/>).</summary>
        internal static int Frame => frame;

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
                PatchRegistry.Lost(e, "Hand IK could not be hooked; the arms keep the game's animation");
                throw;
            }
        }

        /// <summary>Game thread, each simulation frame, after the character's own update.</summary>
        private static void AfterSimulation(MyCharacter __instance)
        {
            GripFit.SaveIfDirty();
            if (Mode == 0 || !VRInput.Active || !guard.On)
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
                    else
                        ArmSpan.SetCharacter(Vector3.Distance(leftChain.UpperRest.Translation, rightChain.UpperRest.Translation), leftChain.Reach + rightChain.Reach);
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
                // The seat's controls are where the animation holds the hands now, before the arms go to the controllers.
                Input.ControlGrip.Capture(__instance, bones, leftChain, rightChain, trackingToModel);
                bool holding = __instance.CurrentWeapon != null && !__instance.IsSitting;
                // The held tool is placed again by the move the arms are solved by, so the hand on its grip and the controller
                // share one frame. The weapon component places it earlier in the update (MyCharacterWeaponPositionComponent.Update)
                // from a different head, which would leave it off the arm's frame. Then the other hand's spot on it is taken again.
                if (holding && Hands.HeldItem.PlaceForArms(__instance, trackingToModel))
                    Hands.TwoHand.ToolPlaced(__instance);
                SolveArm(__instance, bones, Hand.Left, leftChain, leftFingers, trackingToModel);
                if (!holding)
                    SolveArm(__instance, bones, Hand.Right, rightChain, rightFingers, trackingToModel);
                else
                    Held(__instance, bones, rightChain, trackingToModel);
                ReportView(ViewRefusal.None);
            }
            catch (Exception e)
            {
                guard.Failed(e, "Hand IK failed");
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
            return Mode != 0 && guard.On && chain != null && shown == ViewRefusal.None && frame - chain.SolvedFrame <= 2
                   && HandWorld.LastBodySource == HandWorld.BodySource.Head;
        }

        /// <summary>Where the palm bone sits relative to the controller's grip pose, with the turn and shift fitted for the hand's controller (<see cref="GripFit"/>; row vectors).</summary>
        internal static Matrix PalmFromGrip(Hand hand)
        {
            GripFit.Fit fit = GripFit.For(hand);
            return HandGrip.PalmFromGrip(hand == Hand.Left, fit.Turn, fit.Shift / 1000f);
        }

        /// <summary>
        /// The arm that holds an item: the game's IK (MyCharacter.UpdateHandBones) has put its palm on the item's RightHand
        /// spot as far as the arm reaches, and it does not stretch: past that the arm points straight at the tool and the glove
        /// stops short of it: 4 to 6 cm with the hand at the waist 30 cm ahead, as the shoulder sits 21 to 27 cm behind the
        /// eyes. So the arm is solved here instead, onto the same spot, with the stretch and the collarbone of the other arm;
        /// with RenderDebug HandIKHeld=0 the game's arm stays, and how far short it stops is logged.
        /// </summary>
        private static void Held(MyCharacter character, MyCharacterBone[] bones, ArmSolver.Chain chain, in MatrixD trackingToModel)
        {
            NoteHeld(Hand.Right, chain, trackingToModel);
            if (chain == null || !(character.CurrentWeapon is MyEntity weapon) || character.HandItemDefinition == null || Mode == 4)
                return;
            Matrix target = (Matrix)((MatrixD)character.HandItemDefinition.RightHand * weapon.WorldMatrix * character.PositionComp.WorldMatrixNormalizedInv);
            if (Hands.HeldTrace.On)
                Hands.HeldTrace.Held(character, weapon, target, trackingToModel);
            string item = character.HandItemDefinition.Id.SubtypeName;
            if (solveHeld)
            {
                bool fresh = frame - chain.SolvedFrame > 2;
                if (fresh)
                    chain.Forget();
                chain.SolvedFrame = frame;
                HeldSway = steadyShoulder ? SteadyShoulder(bones, chain, fresh) : 0f;
                swayValid &= steadyShoulder;
                Vector3 shoulder = ArmSolver.Origin(bones[chain.Upper]);
                ArmSolver.Settings use = settings;
                ArmSpan.Apply(ref use);
                use.Pin = pinOnTool ? 1f : 0f;
                bool solved = ArmSolver.Solve(bones, chain, target, 1, use, out ArmSolver.Result result);
                HeldStretch = solved ? result.Stretch : float.NaN;
                HandState state = VRInput.Get(Hand.Right);
                if (solved && ArmJump.On && state.Tracked)
                {
                    Matrix palm = PalmTarget(Hand.Right, state);
                    ArmJump.Solved(character, bones, Hand.Right, frame, chain, palm, (Matrix)((MatrixD)palm * trackingToModel), target, shoulder,
                        trackingToModel, result, 0f, true);
                }
                if (solved)
                    NoteReach(Hand.Right, $"Right hand holding the {item}", result, bones, chain, trackingToModel);
                if (solved && result.OutOfReach && NoteDue())
                    Note($"Hand IK: Right hand holding the {item} {result.Distance:F2} m from the shoulder, the arm reaches {result.Reach:F2} m " +
                         $"(stretched x{result.Stretch:F2}, collarbone {result.Shrug:F0} deg), {result.Short * 100f:F0} cm short{Shoulder(bones, chain, trackingToModel)}");
                else if (solved && result.Stretch > 1f + use.Stretch + 0.005f && NoteDue())
                    Note($"Hand IK: Right hand kept on the {item} {result.Distance:F2} m from the shoulder: the arm stretched x{result.Stretch:F2}, " +
                         $"past x{1f + use.Stretch:F2} (collarbone {result.Shrug:F0} deg){Shoulder(bones, chain, trackingToModel)}");
                return;
            }
            HeldStretch = float.NaN;
            float distance = (target.Translation - ArmSolver.Origin(bones[chain.Upper])).Length();
            if (distance > chain.Reach && NoteDue())
                Note($"Hand IK: Right hand holding the {item} {distance:F2} m from the shoulder, the arm reaches {chain.Reach:F2} m: " +
                     $"the game's arm stops {(distance - chain.Reach) * 100f:F0} cm short of the tool{Shoulder(bones, chain, trackingToModel)}");
        }

        /// <summary>
        /// The arm that holds a tool, in first person: the collarbone's own sway (idle breathing, the walk) is damped, so the
        /// shoulder does not pump the length of an arm whose hand stays on the tool. The shoulder follows where the animation
        /// puts it through a lag of <see cref="SwaySeconds"/>, never more than <see cref="SwayHold"/> from it, so a crouch or the
        /// spine bending with the head still carries it; the body drawn ahead (<see cref="BodyShift"/>) is not sway and is left
        /// out. Moves the collarbone (the arm hangs off it); returns how far, metres.
        /// </summary>
        private static float SteadyShoulder(MyCharacterBone[] bones, ArmSolver.Chain chain, bool fresh)
        {
            Vector3 drawnAhead = new Vector3(0f, 0f, -BodyShift.Applied);
            Vector3 shoulder = ArmSolver.Origin(bones[chain.Upper]) - drawnAhead;
            if (fresh || !swayValid)
            {
                swayShoulder = shoulder;
                swayValid = true;
                return 0f;
            }
            swayShoulder += (shoulder - swayShoulder) * (1f - (float)Math.Exp(-1.0 / 60.0 / SwaySeconds));
            Vector3 off = swayShoulder - shoulder;
            float length = off.Length();
            if (length > SwayHold)
            {
                off *= SwayHold / length;
                swayShoulder = shoulder + off;
                length = SwayHold;
            }
            if (length > 1e-5f)
                ArmSolver.Move(bones[chain.Collar], off);
            return length;
        }

        /// <summary>RenderDebug HandIKReach=1: an arm's reach, in reach or not, every 2 s per hand.</summary>
        private static void NoteReach(Hand hand, string what, in ArmSolver.Result r, MyCharacterBone[] bones, ArmSolver.Chain chain, in MatrixD trackingToModel)
        {
            int i = (int)hand;
            if (!reachLog || Environment.TickCount - reachNoted[i] < 2000)
                return;
            reachNoted[i] = Environment.TickCount;
            Log.Info($"Hand IK reach: {what} {r.Distance:F2} m from the shoulder, the arm reaches {r.Reach:F2} m (stretched x{r.Stretch:F2}, " +
                     $"collarbone {r.Shrug:F0} deg), {r.Short * 100f:F0} cm short, body {BodyShift.Applied * 100f:F0} cm ahead{Shoulder(bones, chain, trackingToModel)}");
        }

        /// <summary>For the log: where an arm's shoulder is from the eyes, in the body's axes.</summary>
        private static string Shoulder(MyCharacterBone[] bones, ArmSolver.Chain chain, in MatrixD trackingToModel)
        {
            Vector3 head = GameHead.Take() ? GameHead.TrackingPose.Translation : Vector3.Zero;
            Vector3D eyes = Vector3D.Transform((Vector3D)head, trackingToModel);
            Vector3D d = (Vector3D)ArmSolver.Origin(bones[chain.Upper]) - eyes;
            return $" (the shoulder {Math.Abs(d.X):F2} m to the {(d.X >= 0 ? "right" : "left")}, {-d.Y:F2} m below and {d.Z:F2} m behind the eyes)";
        }

        /// <summary>
        /// The hand that holds an item goes on the item's RightHand, not on the controller's palm (<see cref="Held"/>); for the
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
        internal static Matrix PalmTarget(Hand hand, in HandState state)
        {
            if (!state.PalmTracked)
                return PalmFromGrip(hand) * state.Grip;
            GripFit.Fit fit = GripFit.For(hand);
            return HandGrip.PalmFromPalmPose(hand == Hand.Left, fit.Turn, fit.Shift / 1000f) * state.Palm;
        }

        /// <summary>Per hand: the palm pose placed the palm last time (null: not placed yet), for the log.</summary>
        private static readonly bool?[] fromPalmPose = new bool?[2];
        private static int sourceChanges;

        /// <summary>Per hand: the index and thumb curls the touch sensors ask for, eased (<see cref="TouchStep"/>); negative: not started.</summary>
        private static readonly float[] indexCurl = { -1f, -1f }, thumbCurl = { -1f, -1f };

        /// <summary>
        /// Where an arm's shoulder (the upper arm's root) is in the world, as last solved or animated (for the screens'
        /// reach table, <see cref="Hands.ScreenTest"/>). False before the arms are known.
        /// </summary>
        internal static bool TryShoulder(Hand hand, MyCharacter character, out Vector3D world)
        {
            world = Vector3D.Zero;
            ArmSolver.Chain chain = hand == Hand.Left ? leftChain : rightChain;
            MyCharacterBone[] bones = character?.AnimationController?.CharacterBones;
            if (chain == null || bones == null || indicesFor != character || chain.Upper < 0 || chain.Upper >= bones.Length || character.PositionComp == null)
                return false;
            world = Vector3D.Transform((Vector3D)ArmSolver.Origin(bones[chain.Upper]), character.PositionComp.WorldMatrixRef);
            return true;
        }

        private static void SolveArm(MyCharacter body, MyCharacterBone[] bones, Hand hand, ArmSolver.Chain chain, int[] fingers, in MatrixD trackingToModel)
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
            Matrix palm = PalmTarget(hand, state);
            Matrix target = (Matrix)((MatrixD)palm * trackingToModel);
            Matrix raw = target;
            // The hand stops on solid things (a screen's glass, ...) the real controller goes on through (Hands.HandStop).
            Hands.HandStop.Apply(hand, body, state, trackingToModel, ref target);
            // The other hand on the held tool's grip settles there (Hands.TwoHand), and stays on it.
            float onTool = Hands.TwoHand.Settle(hand, ref target, out Vector3 grip);

            if (Mode == 4)
            {
                ArmSolver.SolveGame(bones, chain, target);
                return;
            }
            // A hand holding the seat's control is drawn on it as the animation has it, eased in and out.
            float onControl = Input.ControlGrip.DrawShare(hand);
            if (onControl > 0f)
                Keep(bones, chain, fingers);
            ArmSolver.Settings use = settings;
            ArmSpan.Apply(ref use);
            use.HandSwivel = Mode != 3;
            use.Pin = pinOnTool ? onTool : 0f;
            use.PinAt = onTool > 0f ? grip : (Vector3?)null;
            if (left)
            {
                // Taking or letting go of a grip, and until the arm has caught up after: the stretch moves a step a frame.
                bool moving = pinOnTool && onTool > 0f && onTool < 1f;
                use.EaseFrom = (moving || easing) && frame - drawnFrame <= 2 && drawn > 0f ? drawn : 0f;
                use.EaseStep = EaseStep;
            }
            Vector3 shoulder = ArmSolver.Origin(bones[chain.Upper]);
            if (!ArmSolver.Solve(bones, chain, target, left ? -1 : 1, use, out ArmSolver.Result result))
                return;
            if (left)
            {
                easing = use.EaseFrom > 0f && Math.Abs(result.Stretch - result.Goal) > 1e-4f;
                drawn = result.Stretch;
                drawnFrame = frame;
                carried = result.Carried;
                LeftOnTool = onTool > 0f ? Off(bones, chain, target, onTool, result) : default;
            }
            if (easeLog && left)
                EaseLog(onTool, use.EaseFrom > 0f, result);
            HandResidual.Solved(hand, chain.Palm, target, bones[chain.Palm].AbsoluteTransform);
            if (Mode != 2)
            {
                ArmSolver.Curl(bones, fingers,
                    IndexCurl(hand, state),
                    RelaxedCurl + (1f - RelaxedCurl) * state.Squeeze,
                    ThumbCurl(hand, state));
            }
            if (onControl > 0f)
                BlendBack(bones, chain, onControl);
            Hands.FingerPress.NoteDrawnFinger(hand, bones, chain.Palm, fingers[2]);
            if (ArmJump.On)
                ArmJump.Solved(body, bones, hand, frame, chain, palm, raw, target, shoulder, trackingToModel, result, onTool, false);
            NoteReach(hand, $"{hand} hand", result, bones, chain, trackingToModel);
            if (result.OutOfReach && NoteDue())
                Note($"Hand IK: {hand} hand {result.Distance:F2} m from the shoulder, the arm reaches {result.Reach:F2} m " +
                     $"(stretched x{result.Stretch:F2}, collarbone {result.Shrug:F0} deg), {result.Short * 100f:F0} cm short{Shoulder(bones, chain, trackingToModel)}");
        }

        // The animated arm, kept before the solve so a hand on a seat's control can be drawn back onto it.
        private static readonly int[] kept = new int[48];
        private static readonly Quaternion[] keptRotation = new Quaternion[48];
        private static readonly Vector3[] keptTranslation = new Vector3[48];
        private static int keptCount;

        /// <summary>Keeps the animation's own turn and place of the arm's bones and the fingers (<see cref="Input.ControlGrip"/>).</summary>
        private static void Keep(MyCharacterBone[] bones, ArmSolver.Chain chain, int[] fingers)
        {
            keptCount = 0;
            void One(int i)
            {
                if (i < 0 || i >= bones.Length || bones[i] == null || keptCount >= kept.Length)
                    return;
                kept[keptCount] = i;
                keptRotation[keptCount] = bones[i].Rotation;
                keptTranslation[keptCount] = bones[i].Translation;
                keptCount++;
            }
            One(chain.Collar);
            One(chain.Upper);
            foreach (int i in chain.Fore)
                One(i);
            One(chain.Palm);
            foreach (int i in fingers)
                One(i);
        }

        /// <summary>Turns the solved arm back toward the animation's by <paramref name="share"/> (1: the animation's exactly), and carries it down the arm.</summary>
        private static void BlendBack(MyCharacterBone[] bones, ArmSolver.Chain chain, float share)
        {
            for (int k = 0; k < keptCount; k++)
            {
                MyCharacterBone bone = bones[kept[k]];
                bone.Rotation = share >= 1f ? keptRotation[k] : Quaternion.Slerp(bone.Rotation, keptRotation[k], share);
                bone.Translation = share >= 1f ? keptTranslation[k] : Vector3.Lerp(bone.Translation, keptTranslation[k], share);
            }
            bones[chain.Collar].ComputeAbsoluteTransform();
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
        /// The thumb: resting on the controller's face it closes with the squeeze; a controller that senses
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

        /// <summary>The left arm on a tool's grip as just solved: how far short of the grip the glove is and where it points.</summary>
        private static OffArm Off(MyCharacterBone[] bones, ArmSolver.Chain chain, in Matrix target, float onTool, in ArmSolver.Result result)
        {
            Matrix palm = bones[chain.Palm].AbsoluteTransform;
            Vector3 middle = HandGrip.HandleInPalm(true);
            Vector3 own = Vector3.Transform(middle, palm) - palm.Translation, toGrip = Vector3.Transform(middle, target) - palm.Translation;
            float off = own.LengthSquared() > 1e-8f && toGrip.LengthSquared() > 1e-8f
                ? MathHelper.ToDegrees((float)Math.Acos(MathHelper.Clamp(Vector3.Dot(Vector3.Normalize(own), Vector3.Normalize(toGrip)), -1f, 1f)))
                : 0f;
            OffArm arm = new OffArm
            {
                OnTool = onTool, Stretch = result.Stretch, Short = (target.Translation - palm.Translation).Length(),
                WristBend = result.WristBend, Twist = result.Twist, PointOff = off, Spot = -1f,
            };
            if (Hands.TwoHand.GripSpot(out Matrix spot))
            {
                arm.Spot = (spot.Translation - palm.Translation).Length();
                Quaternion drawnTurn = Quaternion.CreateFromRotationMatrix(Matrix.Normalize(palm).GetOrientation());
                Quaternion spotTurn = Quaternion.CreateFromRotationMatrix(Matrix.Normalize(spot).GetOrientation());
                arm.SpotTurn = MathHelper.ToDegrees(2f * (float)Math.Acos(Math.Min(1f, Math.Abs(Quaternion.Dot(drawnTurn, spotTurn)))));
            }
            return arm;
        }

        /// <summary>RenderDebug HandIKEase=1: the left arm every frame while its stretch eases (taking or letting go of a tool's grip, and until it catches up), and three frames on.</summary>
        private static void EaseLog(float onTool, bool eased, in ArmSolver.Result result)
        {
            bool easing = onTool > 0f && onTool < 1f || eased;
            if (easing && !easeWas)
                Log.Info($"Hand IK ease: Left hand {(easeShare >= 1f ? "letting go of" : "taking")} the grip; the frame before, the arm stretched x{easeLast:F4}");
            bool log = easing || easeTail > 0;
            easeTail = easing ? 3 : Math.Max(0, easeTail - 1);
            if (log)
                Log.Info($"Hand IK ease: Left hand {onTool:F3} on the grip, the arm stretched x{result.Stretch:F4} (asked x{result.Goal:F4}, at most x{result.Most:F4}), " +
                         $"{result.Distance * 100f:F1} cm from the shoulder, {result.Short * 100f:F1} cm short");
            easeShare = onTool;
            easeLast = result.Stretch;
            easeWas = easing;
        }

        /// <summary>One note every two seconds at most: a hand held out of reach is every frame.</summary>
        private static bool NoteDue() => Environment.TickCount - lastNote >= 2000;

        private static void Note(string text)
        {
            if (!NoteDue())
                return;
            lastNote = Environment.TickCount;
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
