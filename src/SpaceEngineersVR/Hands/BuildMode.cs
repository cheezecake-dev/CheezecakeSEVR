using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GUI;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.SessionComponents.Clipboard;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;
using VRage.Audio;
using VRage.Game;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// The hands while a block is in hand on foot or on the jetpack (the toolbar slot picked is a block, so the game's
    /// cube builder is active with a block): the right trigger places and the left trigger removes, as the game's own
    /// primary and secondary (<see cref="VRControls"/>); the left stick click held turns the block with the right hand, in
    /// 90 degree steps about the grid's axes, and while it is held the right stick turns it a step at a time (left,
    /// right) and sends it farther or brings it nearer (up, down) in the game's own steps (MOVE_FURTHER and MOVE_CLOSER, which
    /// the game acts on in creative only); a tap of X switches symmetry on or off (the game's USE_SYMMETRY, N) and X held half a
    /// second picks the next symmetry plane to set (SYMMETRY_SWITCH, M). A and B stay jump (the jetpack's climb) and the
    /// jetpack, as the game's pad leaves them (ControllerSchemes.sbc, BUILD: the distance is LB with DPadUp and DPadDown).
    /// A pulse marks a block placed and one removed, a tick each step the block turns, and a double buzz a trigger pull that
    /// placed or removed nothing.
    /// <para>The block's size and stage: a click of the right stick switches it between large and small (the game's
    /// CUBE_BUILDER_CUBESIZE_MODE, R). With the left stick click held, A steps to its next stage (its next variant in the group:
    /// NEXT_BLOCK_STAGE, the mouse wheel) and B to the previous one (PREV_BLOCK_STAGE), in creative and survival. The block
    /// keeps the way it faces across a stage step while the hand turns it.</para>
    /// <para>A paste pending (the game's clipboard, Ctrl+V or a blueprint) has the same turn with the left stick click held, on
    /// the pasted grids' own axes, and the click with the stick up or down sends it farther or brings it nearer (MOVE_FURTHER,
    /// MOVE_CLOSER, a tenth each). With the voxel hand the click with the stick up or down moves the brush (VOXEL_FURTHER,
    /// VOXEL_CLOSER).</para>
    /// </summary>
    /// <remarks>
    /// <para>The turn: the right hand's change of orientation since the click went down, taken into the grid's frame, is
    /// matched to the nearest of the 24 turns that keep a block on the grid, with <see cref="SnapHysteresisDegrees"/> of
    /// hysteresis so the block does not flicker at the halfway point; the block's orientation is the one it had when the
    /// click went down, turned by that. It is set where the game's own rotate keys set it
    /// (MyCubeBuilderGizmo.RotateAxis, MyCubeBuilder.cs:1806), with the game's turn animation and sound, and only to
    /// orientations the block allows (MyCubeBuilder.CheckValidBlockRotation). Off a grid (the game's dynamic mode) the block's
    /// own world orientation is turned the same way. While the click is held the builder's ray stays where it was when the
    /// click went down (held relative to the body), so twisting the hand turns the block without sliding it.</para>
    /// <para>The right grip is not building's: with a block in hand it is what it is on foot, so a hand closed on a hull, or on
    /// anything the Use key works on (a cargo container, a refinery, a terminal, a door, a seat: <see cref="GrabUse"/>, which
    /// does not count a block in hand as a tool), does that, and the game opens a block's inventory or terminal with a block in
    /// hand as without one. A floating item is still only the other hand's (<see cref="ItemGrab"/> counts the builder as a tool
    /// in the dominant hand). The left stick click is the modifier instead (<see cref="Modifier"/>): while a block, a paste or
    /// the voxel hand is up it is not sprint and not zero-g pitch and roll. Seated, the game's controls are left as they are.</para>
    /// </remarks>
    internal static class BuildMode
    {
        /// <summary>Debug: the old on-foot buttons with a block in hand (<see cref="Rendering.RenderDebug"/> BuildMode=0).</summary>
        public static bool Off { get; set; }

        /// <summary>How much nearer the next 90 degree step has to be than the one shown before the block moves to it, degrees: a single-axis turn steps at 50 degrees and back at 40.</summary>
        public const double SnapHysteresisDegrees = 10.0;

        public const double SymmetryHoldSeconds = 0.5;
        public const double DistanceRepeatDelay = 0.4, DistanceRepeatEvery = 0.08;

        /// <summary>How long after a trigger pull a block has to be placed or removed before it counts as refused, seconds.</summary>
        public const double RefuseWindow = 0.25;

        private const float StickPress = 0.7f, StickRelease = 0.3f;
        private const float TickAmplitude = 0.3f, TickSeconds = 0.015f;
        private const float RemoveAmplitude = 0.6f, RemoveSeconds = 0.03f;
        private const float RefuseAmplitude = 0.8f, RefuseSeconds = 0.05f;
        private const double RefuseGap = 0.1;

        /// <summary>A block is in hand on foot or on the jetpack, nothing else has the hands: the build-mode buttons apply this frame.</summary>
        public static bool Active { get; private set; }

        /// <summary>The left stick click is held to build: a block, a paste or the voxel brush is up. The right hand turns the block or the paste, the right stick turns and moves it, A and B pick the stage; the click is not sprint or zero-g pitch and roll.</summary>
        public static bool Modifier { get; private set; }

        /// <summary>The right hand is turning the block.</summary>
        public static bool Turning => turning;

        /// <summary>The right stick belongs to building: while the left stick click is held for it, and after until the stick is back near the middle.</summary>
        public static bool StickBusy { get; private set; }

        /// <summary>A paste is pending on foot or on the jetpack (the game's clipboard is up) and no block is in hand: the left stick click and the hand turn it, the click and the stick move it.</summary>
        public static bool Pasting { get; private set; }

        /// <summary>The voxel hand is on (creative) on foot or on the jetpack: the left stick click and the right stick move the brush.</summary>
        public static bool VoxelHand { get; private set; }

        /// <summary>A is the block's stage step this frame (pressed with the left stick click held), not jump or the jetpack's climb, until it is let go.</summary>
        public static bool TakesA => Active && stageA;

        /// <summary>B is the block's stage step this frame, not the jetpack, until it is let go.</summary>
        public static bool TakesB => Active && stageB;

        // One frame each, read by VRControls in the same frame: the game's MOVE_CLOSER, MOVE_FURTHER (and VOXEL_CLOSER,
        // VOXEL_FURTHER), USE_SYMMETRY, SYMMETRY_SWITCH, CUBE_BUILDER_CUBESIZE_MODE, NEXT_BLOCK_STAGE, PREV_BLOCK_STAGE.
        public static bool Closer { get; private set; }
        public static bool Further { get; private set; }
        public static bool SymmetryTap { get; private set; }
        public static bool SymmetryCycle { get; private set; }
        public static bool CubeSize { get; private set; }
        public static bool NextStage { get; private set; }
        public static bool PrevStage { get; private set; }

        private static readonly Matrix[] Turns = AllTurns();
        private static readonly int Identity = Array.FindIndex(Turns, m => m.Right == Vector3.Right && m.Up == Vector3.Up);

        private static AccessTools.FieldRef<MyCubeBuilder, MyCubeBuilderGizmo> gizmoRef;
        private static AccessTools.FieldRef<MyCubeBuilder, bool> animationLockRef;
        private static AccessTools.FieldRef<MyGridClipboard, Vector3> pasteForwardRef, pasteUpRef;
        private static AccessTools.FieldRef<MyGridClipboard, float> pasteAngleRef, dragDistanceRef;
        private static AccessTools.FieldRef<MySessionComponentVoxelHand, float> voxelDistanceRef;
        private static MethodInfo currentGridGetter, symmetryModeGetter;
        private static int errors;

        // The turn. A paste's turns the pasted grids' orientation (pasteTurn), as a block off a grid turns its own.
        private static bool turning, dynamicTurn, pasteTurn;
        private static MatrixD handStart, frame;
        private static Matrix localStart;
        private static int snap, steps;
        private static Vector3D rayOrigin, rayDirection;
        private static bool rayFrozen;
        private static MyCubeBlockDefinition turnBlock;

        // The right stick with the left stick click held: one thing a push, a turn (sideways, stickTurn for one frame) or the
        // distance (up or down, stickDistance while held); it acts again once back near the middle.
        private static bool modifierWas, stickArmed;
        private static int stickTurn, stickDistance;

        // The distance, X, the stick click, A and B. stageA, stageB: pressed with the click held for a stage step, until let go.
        private static double distanceDown = -1d, distanceNext;
        private static int distanceDirection;
        private static float distanceBefore;
        private static int distanceLogIn;
        private static string distanceOf;
        private static bool stageA, stageB, building;
        private static int stageLogIn, sizeLogIn;
        private static string stageAsked, stageBefore, sizeBefore;
        private static double symmetryDown = -1d;
        private static bool symmetryCycled;
        private static int symmetryLogIn;
        private static string symmetryAsked;

        // Triggers: a pull waits RefuseWindow for a block placed or removed.
        private static bool placeWas, removeWas;
        private static double placeAt = -1d, removeAt = -1d;
        private static int placed, removed, placedAtPull, removedAtPull;
        private static double secondBuzzAt = -1d, frameSeconds;
        private static Hand secondBuzzHand;

        public static void Patch(Harmony harmony)
        {
            Log.Info("Build mode: the left stick click held, the right hand turns the block in hand or the paste, the right stick left/right a 90 deg turn and up/down distance, A/B block stage (the click is not sprint or zero-g pitch and roll then); the right grip is as on foot (use, grab); A/B otherwise jump and the jetpack, right stick click block size, X symmetry, haptics on place, remove and refuse");
        }

        /// <summary>From <see cref="HandPlacement.HookNow"/>, on the first frame of a session: the cube builder's own methods can be patched by then.</summary>
        internal static void HookBuilder(Harmony harmony)
        {
            try
            {
                harmony.Patch(AccessTools.Method(typeof(MyCubeBuilder), "Remove", Type.EmptyTypes),
                    prefix: new HarmonyMethod(typeof(BuildMode), nameof(BeforeRemove)));
                Log.Info("Build mode: block removal hooked (MyCubeBuilder.Remove)");
            }
            catch (Exception e)
            {
                Log.Error(e, "Build mode could not hook block removal; removing gives no pulse");
            }
        }

        /// <summary>Game thread, once per frame from <see cref="VRControls.Update(double)"/>, after the toolbar wheel has had the hands.</summary>
        internal static void Frame(VRContext context, bool blocked, double seconds)
        {
            Closer = Further = SymmetryTap = SymmetryCycle = false;
            CubeSize = NextStage = PrevStage = false;
            frameSeconds = seconds;
            try
            {
                FrameNow(context, blocked, seconds);
            }
            catch (Exception e)
            {
                Active = Pasting = VoxelHand = Modifier = false;
                EndTurn("an error");
                if (errors++ < 3)
                    Log.Error(e, "Build mode failed this frame");
            }
        }

        private static void FrameNow(VRContext context, bool blocked, double seconds)
        {
            bool hands = !Off && !blocked && (context == VRContext.OnFoot || context == VRContext.Jetpack);
            MyCubeBuilder builder = hands ? BlockBuilder() : null;
            bool active = builder != null;
            if (active != Active)
                Log.Info(active ? $"Build mode on: {builder.CurrentBlockDefinition.Id.SubtypeName}" : "Build mode off");
            Active = active;
            // The cube builder and the clipboard take turns (each deactivates the other); a block in hand comes first all the same.
            MyClipboardComponent clipboard = hands && !active ? PasteClipboard() : null;
            NotePasting(clipboard);
            bool voxel = hands && !active && clipboard == null && MySessionComponentVoxelHand.Static?.Enabled == true;
            if (voxel != VoxelHand)
                Log.Info(voxel ? $"Voxel hand on: left stick click + right stick up/down moves the brush; material {VoxelMaterialName()}" : "Voxel hand off");
            VoxelHand = voxel;
            bool on = active || clipboard != null || voxel;
            if (on && !building)
                Log.Info("Build: A/B are jump and the jetpack; turn, distance and block stage on the left stick click: the right hand turns, the right stick turns and moves, A/B pick the stage");
            building = on;

            if (secondBuzzAt >= 0d && seconds >= secondBuzzAt)
            {
                secondBuzzAt = -1d;
                Haptics.Pulse(secondBuzzHand, RefuseAmplitude, RefuseSeconds);
            }
            ReportAfterGame(builder, clipboard);

            Vector2 stick = VRInput.Get(Hand.Right).Stick;
            bool modifier = building && VRInput.IsPressed(Hand.Left, VRButtons.StickClick);
            if (modifier != Modifier)
                Log.Info(modifier ? "Build: left stick click held, the right hand and stick turn and move what is in hand" : "Build: left stick click let go");
            Modifier = modifier;
            ModifierStick(modifier, stick);
            StickBusy = modifier || (StickBusy && stick.Length() > StickRelease);
            if (!active)
            {
                symmetryDown = -1d;
                placeAt = removeAt = -1d;
                placeWas = removeWas = false;
                stageA = stageB = false;
                if (clipboard != null)
                {
                    PasteTurn(clipboard.Clipboard);
                    Distance(seconds, "paste");
                    return;
                }
                EndTurn("build mode ended");
                if (voxel)
                    Distance(seconds, "voxel");
                else
                    distanceDown = -1d;
                return;
            }

            CheckRefused(builder, seconds);
            Turn(builder);
            SizeAndStage(builder, modifier);
            Distance(seconds, "block");
            Symmetry(seconds);
        }

        /// <summary>
        /// The right stick while the left stick click is held for building: a push sideways is one 90 degree turn (<see cref="stickTurn"/>,
        /// this frame), up or down the distance (<see cref="stickDistance"/>, + farther, while held), whichever it is pushed
        /// more; the stick does nothing more until it is back near the middle. A stick already out when the click goes down waits
        /// for that too.
        /// </summary>
        private static void ModifierStick(bool modifier, Vector2 stick)
        {
            stickTurn = 0;
            if (!modifier)
            {
                modifierWas = false;
                stickDistance = 0;
                return;
            }
            if (!modifierWas)
            {
                modifierWas = true;
                stickArmed = stick.Length() < StickRelease;
                stickDistance = 0;
            }
            if (stickArmed && (Math.Abs(stick.X) > StickPress || Math.Abs(stick.Y) > StickPress))
            {
                stickArmed = false;
                if (Math.Abs(stick.X) >= Math.Abs(stick.Y))
                    stickTurn = Math.Sign(stick.X);
                else
                    stickDistance = Math.Sign(stick.Y);
                return;
            }
            if (stickDistance != 0 && stick.Y * stickDistance < StickRelease)
                stickDistance = 0;
            if (!stickArmed && stick.Length() < StickRelease)
                stickArmed = true;
        }

        /// <summary>The cube builder with a block, or null: the slot picked is not a block, or the builder is painting or pasting.</summary>
        private static MyCubeBuilder BlockBuilder()
        {
            MyCubeBuilder builder = MyCubeBuilder.Static;
            return builder != null && builder.IsActivated && builder.CurrentBlockDefinition != null && builder.IsBuildToolActive() ? builder : null;
        }

        /// <summary>The clipboard with a paste pending (grids, a floating object or voxels), or null.</summary>
        private static MyClipboardComponent PasteClipboard()
        {
            MyClipboardComponent clipboard = MyClipboardComponent.Static;
            return clipboard != null && clipboard.IsActive ? clipboard : null;
        }

        private static void NotePasting(MyClipboardComponent clipboard)
        {
            bool pasting = clipboard != null;
            if (pasting == Pasting)
                return;
            Pasting = pasting;
            if (!pasting)
            {
                Log.Info("Paste: ended");
                return;
            }
            MyGridClipboard grids = clipboard.Clipboard;
            Log.Info(grids != null && grids.IsActive
                ? $"Paste: pending, {grids.CopiedGrids.Count} grid{(grids.CopiedGrids.Count == 1 ? string.Empty : "s")} ({grids.CopiedGridsName}), {PasteDistance(grids):F1} m out; left stick click + right hand turns it, left stick click + right stick up/down farther and nearer"
                : "Paste: pending, not grids (a floating object or voxels); left stick click + right stick up/down farther and nearer");
        }

        /// <summary>The game's own test for creative building (MyCubeBuilder.HandleAdminAndCreativeInput): it moves the block nearer and farther only then.</summary>
        private static bool Creative() =>
            MySession.Static != null && ((MySession.Static.CreativeToolsEnabled(Sync.MyId) && MySession.Static.HasCreativeRights) || MySession.Static.CreativeMode);

        // ---- Turning the block ----

        private static void Turn(MyCubeBuilder builder)
        {
            if (!Modifier)
            {
                EndTurn("left stick click let go");
                return;
            }
            if (turning && pasteTurn)
                EndTurn("a block in hand took over from the paste");
            MyCharacter character = MySession.Static?.LocalCharacter;
            if (character == null || !HandWorld.TryGetNow(character, Hand.Right, out MatrixD grip, out _, out _))
                return;
            MatrixD hand = grip.GetOrientation();
            if (!turning)
            {
                BeginTurn(builder, character, hand);
                return;
            }
            if (builder.CurrentBlockDefinition != turnBlock && !KeepTurn(builder))
                return;
            if (builder.CurrentBlockDefinition.Rotation == MyBlockRotation.None)
                return;

            // The stick: a 90 degree step per push left or right, about the body's up (ModifierStick).
            if (stickTurn != 0)
            {
                StickStep(builder, hand, character.WorldMatrix.Up, stickTurn > 0 ? -MathHelper.PiOver2 : MathHelper.PiOver2, stickTurn > 0 ? "right" : "left");
                return;
            }

            // The hand: its turn since the click went down, in the grid's frame (row vectors: hand = handStart * delta).
            MatrixD delta = MatrixD.Transpose(handStart) * hand;
            Matrix inFrame = (Matrix)(frame * delta * MatrixD.Transpose(frame));
            int best = Nearest(inFrame, dynamicTurn ? null : builder.CurrentBlockDefinition, false, out double bestAngle);
            if (best < 0 || best == snap)
                return;
            double shownAngle = Angle(Turns[snap], inFrame);
            if (bestAngle > shownAngle - SnapHysteresisDegrees)
                return;
            int was = snap;
            snap = best;
            if (Apply(builder, dynamicTurn ? Turns[best] * (Matrix)frame : localStart * Turns[best], out string facing))
            {
                steps++;
                Haptics.Pulse(Hand.Right, TickAmplitude, TickSeconds);
                Log.Info($"Build: turned by hand to {Describe(Turns[best])} from the click's start (was {Describe(Turns[was])}; hand {shownAngle:F0} deg from the old step, {bestAngle:F0} from the new); block {facing}");
            }
        }

        private static void BeginTurn(MyCubeBuilder builder, MyCharacter character, MatrixD hand)
        {
            MyCubeBuilderGizmo.MyGizmoSpaceProperties space = Gizmo(builder).SpaceDefault;
            dynamicTurn = builder.DynamicMode;
            pasteTurn = false;
            turnBlock = builder.CurrentBlockDefinition;
            handStart = hand;
            snap = Identity;
            steps = 0;
            Matrix local = space.m_localMatrixAdd.GetOrientation();
            if (dynamicTurn)
            {
                frame = space.m_worldMatrixAdd.GetOrientation();
                localStart = Matrix.Identity;
            }
            else
            {
                MyCubeGrid grid = CurrentGrid(builder);
                localStart = local;
                // On a grid its orientation; elsewhere the frame the block's orientation is in (world = local * frame).
                frame = grid != null ? grid.WorldMatrix.GetOrientation() : MatrixD.Transpose(local) * space.m_worldMatrixAdd.GetOrientation();
            }
            FreezeRay(character);
            turning = true;
            Log.Info($"Build: turn started ({(dynamicTurn ? "off a grid, the block's own axes" : CurrentGrid(builder) != null ? "on a grid" : "on the local grid")}; ray held{(rayFrozen ? string.Empty : " not available")}; block {builder.CurrentBlockDefinition.Id.SubtypeName}, rotation {builder.CurrentBlockDefinition.Rotation})");
        }

        /// <summary>Holds the builder's ray where the hand points now, relative to the body, for as long as the hand turns the block (<see cref="TryFrozenRay"/>).</summary>
        private static void FreezeRay(MyCharacter character)
        {
            rayFrozen = HandTools.TryGetRay(out Vector3D origin, out Vector3D direction);
            if (!rayFrozen)
                return;
            MatrixD toBody = MatrixD.Invert(character.WorldMatrix);
            rayOrigin = Vector3D.Transform(origin, toBody);
            rayDirection = Vector3D.TransformNormal(direction, toBody);
        }

        private static void EndTurn(string why)
        {
            if (!turning)
                return;
            turning = false;
            rayFrozen = false;
            Log.Info($"{(pasteTurn ? "Paste" : "Build")}: turn ended ({why}) after {steps} step{(steps == 1 ? string.Empty : "s")}");
        }

        /// <summary>
        /// The block in hand changed while the hand turns it (a stage step, the size switch): the game gave the new block the
        /// orientation it had last, or none (MyCubeBuilder.UpdateCubeBlockStageDefinition). A block of the same size is turned
        /// back to the way the hand holds it, so a stage step does not undo the turn; otherwise, or if the new block cannot face
        /// that way, the turn starts again from what the game shows. False when the turn ended.
        /// </summary>
        private static bool KeepTurn(MyCubeBuilder builder)
        {
            MyCubeBlockDefinition was = turnBlock, now = builder.CurrentBlockDefinition;
            turnBlock = now;
            if (was == null || now == null || was.CubeSize != now.CubeSize)
            {
                EndTurn($"block changed size, {Name(was)} -> {Name(now)}");
                return false;
            }
            MyCubeBuilderGizmo gizmo = Gizmo(builder);
            MyCubeBuilderGizmo.MyGizmoSpaceProperties space = gizmo.SpaceDefault;
            if (dynamicTurn)
            {
                // Off a grid the block's own world orientation is the turn's; the stage step set only the grid one.
                MatrixD world = Turns[snap] * (Matrix)frame;
                world.Translation = space.m_worldMatrixAdd.Translation;
                space.m_worldMatrixAdd = world;
            }
            else
            {
                Matrix held = localStart * Turns[snap];
                if (!MyCubeBuilder.CheckValidBlockRotation(held, now.Direction, now.Rotation))
                {
                    EndTurn($"{Name(now)} cannot face the way the hand held {Name(was)}");
                    return false;
                }
                MatrixD target = held;
                target.Translation = space.m_localMatrixAdd.Translation;
                CustomRotation(builder);
                gizmo.RotateAxis(ref target);
            }
            Log.Info($"Build: the turn keeps {Name(now)} facing as it held {Name(was)} ({Describe(Turns[snap])} from the click's start)");
            return true;
        }

        private static string Name(MyCubeBlockDefinition definition) => definition?.Id.SubtypeName ?? "none";

        /// <summary>A stick step: the block turned 90 degrees about the world axis nearest the grid axis, then the hand's turn starts again from here.</summary>
        private static void StickStep(MyCubeBuilder builder, MatrixD hand, Vector3D worldAxis, float angle, string direction)
        {
            Matrix now = dynamicTurn ? Turns[snap] * (Matrix)frame : localStart * Turns[snap];
            Matrix target;
            if (dynamicTurn)
                target = now * Matrix.CreateFromAxisAngle((Vector3)worldAxis, angle);
            else
            {
                Vector3 inFrame = Vector3.TransformNormal((Vector3)worldAxis, (Matrix)MatrixD.Transpose(frame));
                target = now * Matrix.CreateFromAxisAngle(GridAxis(inFrame), angle);
            }
            target = Round(target);
            if (!dynamicTurn && !MyCubeBuilder.CheckValidBlockRotation(target, builder.CurrentBlockDefinition.Direction, builder.CurrentBlockDefinition.Rotation))
            {
                Log.Info($"Build: stick turn {direction} refused, the block cannot face that way");
                return;
            }
            if (!Apply(builder, target, out string facing))
                return;
            steps++;
            Haptics.Pulse(Hand.Right, TickAmplitude, TickSeconds);
            Log.Info($"Build: turned with the stick ({direction}) 90 deg; block {facing}");
            // The hand's turn counts from here.
            if (dynamicTurn)
                frame = target;
            else
                localStart = target;
            handStart = hand;
            snap = Identity;
        }

        /// <summary>Sets the block's orientation as the game's rotate keys do, with the game's turn animation and sound. False when it did not change.</summary>
        private static bool Apply(MyCubeBuilder builder, Matrix orientation, out string facing)
        {
            MyCubeBuilderGizmo gizmo = Gizmo(builder);
            MyCubeBuilderGizmo.MyGizmoSpaceProperties space = gizmo.SpaceDefault;
            facing = string.Empty;
            if (dynamicTurn)
            {
                MatrixD world = space.m_worldMatrixAdd;
                MatrixD turned = orientation;
                turned.Translation = world.Translation;
                space.m_worldMatrixAdd = turned;
                facing = $"forward {Short(turned.Forward)} up {Short(turned.Up)} (world)";
                MyGuiAudio.PlaySound(MyGuiSounds.HudRotateBlock);
                return true;
            }
            Matrix before = space.m_localMatrixAdd;
            // The game's turn animation (MyCubeBuilder.CalculateBlockRotation sets the lock; the gizmo update slerps from
            // m_animationLastMatrix over ten frames). Started from what is shown now, so a step during a step does not jump.
            ref bool animating = ref animationLockRef(builder);
            space.m_animationLastMatrix = animating && space.m_animationProgress < 1f
                ? MatrixD.Slerp(space.m_animationLastMatrix, space.m_localMatrixAdd, space.m_animationProgress)
                : (MatrixD)space.m_localMatrixAdd;
            space.m_animationProgress = 0f;
            animating = true;
            MatrixD target = orientation;
            target.Translation = before.Translation;
            CustomRotation(builder);
            gizmo.RotateAxis(ref target);
            Matrix after = space.m_localMatrixAdd;
            facing = $"forward {Short(after.Forward)} up {Short(after.Up)} (grid)";
            if (Vector3.DistanceSquared(after.Forward, before.Forward) < 1e-4f && Vector3.DistanceSquared(after.Up, before.Up) < 1e-4f)
                return false;
            MyGuiAudio.PlaySound(MyGuiSounds.HudRotateBlock);
            return true;
        }

        /// <summary>
        /// The 24 turns nearest the hand's first; the best one allowed, and its angle from the hand's, degrees. Allowed: the
        /// block on a grid (<paramref name="block"/>, null for none) can face that way; with <paramref name="upOnly"/>, the
        /// turn keeps up where it is (a paste with one free axis).
        /// </summary>
        private static int Nearest(Matrix inFrame, MyCubeBlockDefinition block, bool upOnly, out double angle)
        {
            int best = -1;
            angle = double.MaxValue;
            for (int i = 0; i < Turns.Length; i++)
            {
                double a = Angle(Turns[i], inFrame);
                if (a >= angle)
                    continue;
                if (block != null && !MyCubeBuilder.CheckValidBlockRotation(localStart * Turns[i], block.Direction, block.Rotation))
                    continue;
                if (upOnly && Turns[i].Up != Vector3.Up)
                    continue;
                angle = a;
                best = i;
            }
            return best;
        }

        /// <summary>The angle between two orientations, degrees.</summary>
        internal static double Angle(Matrix a, Matrix b)
        {
            // trace(a^T b) = 1 + 2 cos(angle)
            Matrix d = Matrix.Transpose(a) * b;
            double cos = MathHelper.Clamp((d.M11 + d.M22 + d.M33 - 1f) * 0.5f, -1f, 1f);
            return MathHelper.ToDegrees(Math.Acos(cos));
        }

        /// <summary>The 24 orientations that keep a block's axes on a grid's: every signed permutation with determinant +1.</summary>
        internal static Matrix[] AllTurns()
        {
            var list = new List<Matrix>(24);
            Vector3[] axes = { Vector3.Right, Vector3.Left, Vector3.Up, Vector3.Down, Vector3.Backward, Vector3.Forward };
            foreach (Vector3 right in axes)
                foreach (Vector3 up in axes)
                {
                    if (Math.Abs(Vector3.Dot(right, up)) > 0.5f)
                        continue;
                    Matrix m = Matrix.Identity;
                    m.Right = right;
                    m.Up = up;
                    m.Backward = Vector3.Cross(right, up);
                    list.Add(m);
                }
            return list.ToArray();
        }

        /// <summary>The grid axis nearest a direction in the grid's frame.</summary>
        private static Vector3 GridAxis(Vector3 v)
        {
            Vector3 a = Vector3.Abs(v);
            if (a.X >= a.Y && a.X >= a.Z)
                return new Vector3(Math.Sign(v.X), 0f, 0f);
            if (a.Y >= a.Z)
                return new Vector3(0f, Math.Sign(v.Y), 0f);
            return new Vector3(0f, 0f, Math.Sign(v.Z));
        }

        private static Matrix Round(Matrix m)
        {
            Matrix r = Matrix.Identity;
            r.Right = Vector3I.Round(m.Right);
            r.Up = Vector3I.Round(m.Up);
            r.Backward = Vector3I.Round(m.Backward);
            return r;
        }

        /// <summary>A turn in words: its angle and the grid axis it is about.</summary>
        internal static string Describe(Matrix turn)
        {
            Quaternion q = Quaternion.CreateFromRotationMatrix(turn);
            double angle = MathHelper.ToDegrees(2.0 * Math.Acos(MathHelper.Clamp(Math.Abs(q.W), 0f, 1f)));
            if (angle < 1.0)
                return "no turn";
            Vector3 axis = new Vector3(q.X, q.Y, q.Z) * Math.Sign(q.W == 0f ? 1f : q.W);
            return $"{angle:F0} deg about {Short(Vector3.Normalize(axis))}";
        }

        private static string Short(Vector3D v)
        {
            string Axis(double c, string plus, string minus) => c > 0.5 ? plus : c < -0.5 ? minus : null;
            string s = Axis(v.X, "+X", "-X") ?? Axis(v.Y, "+Y", "-Y") ?? Axis(v.Z, "+Z", "-Z");
            return s ?? $"({v.X:F2},{v.Y:F2},{v.Z:F2})";
        }

        /// <summary>The builder's (or the paste's) ray while the hand turns the block or the paste: where it was when the left stick click went down, held relative to the body.</summary>
        internal static bool TryFrozenRay(MyCharacter character, out Vector3D origin, out Vector3D direction)
        {
            origin = direction = Vector3D.Zero;
            if (!turning || !rayFrozen || character == null)
                return false;
            MatrixD body = character.WorldMatrix;
            origin = Vector3D.Transform(rayOrigin, body);
            direction = Vector3D.Normalize(Vector3D.TransformNormal(rayDirection, body));
            return true;
        }

        // ---- Turning a paste ----

        /// <summary>
        /// The paste's turn, as a block's off a grid: the pasted grids' orientation (the clipboard's paste directions, which
        /// the game's rotate keys turn in MyGridClipboard.RotateAroundAxis) is the one it had when the click went down, turned by
        /// the right hand's turn since then in 90 degree steps about the paste's own axes, and by the right stick a step at a
        /// time. Snapped to a grid the game lines the paste up with that grid's axes every frame (TrySnapToSurface), which
        /// these steps keep; snapped with one free axis (OneAxisRotationMode, where the game's keys turn it about its up only)
        /// only those turns are made. The ray is held as for a block, so twisting the hand does not slide the paste.
        /// </summary>
        private static void PasteTurn(MyGridClipboard grids)
        {
            if (turning && !pasteTurn)
                EndTurn("a paste took over from the block");
            if (!Modifier || grids == null || !grids.IsActive || grids.PreviewGrids.Count == 0)
            {
                EndTurn(Modifier ? "the paste has no grids to turn" : "left stick click let go");
                return;
            }
            MyCharacter character = MySession.Static?.LocalCharacter;
            if (character == null || !HandWorld.TryGetNow(character, Hand.Right, out MatrixD grip, out _, out _))
                return;
            MatrixD hand = grip.GetOrientation();
            if (!turning)
            {
                BeginPasteTurn(grids, character, hand);
                return;
            }
            bool upOnly = grids.OneAxisRotationMode;

            // The stick: a 90 degree step per push left or right, about the paste's own axis nearest the body's up (ModifierStick).
            if (stickTurn != 0)
            {
                PasteStickStep(grids, hand, character.WorldMatrix.Up, stickTurn > 0 ? -MathHelper.PiOver2 : MathHelper.PiOver2, stickTurn > 0 ? "right" : "left", upOnly);
                return;
            }

            // The hand: its turn since the click went down, in the paste's frame (row vectors: hand = handStart * delta).
            MatrixD delta = MatrixD.Transpose(handStart) * hand;
            Matrix inFrame = (Matrix)(frame * delta * MatrixD.Transpose(frame));
            int best = Nearest(inFrame, null, upOnly, out double bestAngle);
            if (best < 0 || best == snap)
                return;
            double shownAngle = Angle(Turns[snap], inFrame);
            if (bestAngle > shownAngle - SnapHysteresisDegrees)
                return;
            int was = snap;
            snap = best;
            ApplyPaste(grids, Turns[best] * (Matrix)frame, out string facing);
            steps++;
            Haptics.Pulse(Hand.Right, TickAmplitude, TickSeconds);
            Log.Info($"Paste: turned by hand to {Describe(Turns[best])} from the click's start (was {Describe(Turns[was])}; hand {shownAngle:F0} deg from the old step, {bestAngle:F0} from the new); paste {facing}");
        }

        private static void BeginPasteTurn(MyGridClipboard grids, MyCharacter character, MatrixD hand)
        {
            PasteRefs();
            // A turn the game's keys left part-way (its orientation angle) is folded in first, as the game does before each of its own.
            applyAngle.Invoke(grids, null);
            Vector3 forward = pasteForwardRef(grids), up = pasteUpRef(grids);
            frame = Matrix.CreateWorld(Vector3.Zero, Vector3.Normalize(forward), Vector3.Normalize(up));
            dynamicTurn = false;
            pasteTurn = true;
            turnBlock = null;
            handStart = hand;
            snap = Identity;
            steps = 0;
            FreezeRay(character);
            turning = true;
            Log.Info($"Paste: turn started ({(grids.IsSnapped ? "snapped" : "not snapped")}{(grids.OneAxisRotationMode ? ", one free axis: about its up only" : string.Empty)}; ray held{(rayFrozen ? string.Empty : " not available")}; paste forward {Short(frame.Forward)} up {Short(frame.Up)} (world))");
        }

        /// <summary>A stick step: the paste turned 90 degrees about its own axis nearest the world axis, then the hand's turn starts again from here.</summary>
        private static void PasteStickStep(MyGridClipboard grids, MatrixD hand, Vector3D worldAxis, float angle, string direction, bool upOnly)
        {
            Matrix now = Turns[snap] * (Matrix)frame;
            Vector3 local = GridAxis(Vector3.TransformNormal((Vector3)worldAxis, Matrix.Transpose(now)));
            if (upOnly && Math.Abs(local.Y) < 0.5f)
            {
                Log.Info($"Paste: stick turn {direction} refused, the paste is snapped with one free axis (it turns about its up only)");
                return;
            }
            // Row vectors: a turn in the paste's own frame goes before its orientation.
            Matrix target = Round(Matrix.CreateFromAxisAngle(local, angle)) * now;
            ApplyPaste(grids, target, out string facing);
            steps++;
            Haptics.Pulse(Hand.Right, TickAmplitude, TickSeconds);
            Log.Info($"Paste: turned with the stick ({direction}) 90 deg; paste {facing}");
            // The hand's turn counts from here.
            frame = target;
            handStart = hand;
            snap = Identity;
        }

        /// <summary>Sets the paste's orientation where the game's rotate keys set it (its paste directions), with the game's turn sound.</summary>
        private static void ApplyPaste(MyGridClipboard grids, Matrix orientation, out string facing)
        {
            pasteForwardRef(grids) = orientation.Forward;
            pasteUpRef(grids) = orientation.Up;
            pasteAngleRef(grids) = 0f;
            facing = $"forward {Short(orientation.Forward)} up {Short(orientation.Up)} (world)";
            MyGuiAudio.PlaySound(MyGuiSounds.HudRotateBlock);
        }

        // ---- A, B, X and the stick click ----

        /// <summary>
        /// The right stick click: the block's other size (CUBE_BUILDER_CUBESIZE_MODE; the game switches only a block whose
        /// group has one). A and B with the left stick click held, in creative and survival: the next and the previous stage
        /// (NEXT_BLOCK_STAGE, PREV_BLOCK_STAGE; the game steps only a block that has stages, past those not available here).
        /// A button pressed for a stage is not jump or the jetpack until it is let go (<see cref="TakesA"/>, <see cref="TakesB"/>).
        /// </summary>
        private static void SizeAndStage(MyCubeBuilder builder, bool modifier)
        {
            if (VRInput.IsNewPressed(Hand.Right, VRButtons.StickClick))
            {
                CubeSize = true;
                sizeBefore = Name(builder.CurrentBlockDefinition);
                sizeLogIn = 2;
            }
            bool next = modifier && VRInput.IsNewPressed(Hand.Right, VRButtons.A), previous = modifier && VRInput.IsNewPressed(Hand.Right, VRButtons.B);
            if (next || previous)
            {
                NextStage = next;
                PrevStage = !next;
                stageAsked = next ? "next" : "previous";
                stageBefore = Name(builder.CurrentBlockDefinition);
                stageLogIn = 2;
            }
            stageA = VRInput.IsPressed(Hand.Right, VRButtons.A) && (stageA || next);
            stageB = VRInput.IsPressed(Hand.Right, VRButtons.B) && (stageB || previous);
        }

        /// <summary>The left stick click held with the right stick up farther, down nearer: one of the game's steps on the push, then repeated while held. <paramref name="of"/>: the block, the paste or the voxel brush.</summary>
        private static void Distance(double seconds, string of)
        {
            int direction = stickDistance;
            if (direction == 0)
            {
                distanceDown = -1d;
                return;
            }
            bool step = false;
            if (distanceDown < 0d || direction != distanceDirection)
            {
                distanceDown = seconds;
                distanceNext = seconds + DistanceRepeatDelay;
                distanceDirection = direction;
                step = true;
            }
            else if (seconds >= distanceNext)
            {
                distanceNext = seconds + DistanceRepeatEvery;
                step = true;
            }
            if (!step)
                return;
            Closer = direction < 0;
            Further = direction > 0;
            if (distanceLogIn == 0 || distanceOf != of)
            {
                distanceOf = of;
                distanceBefore = DistanceNow(of);
            }
            distanceLogIn = 2;
        }

        /// <summary>How far out the block, the paste or the voxel brush is, metres; NaN when it cannot be read.</summary>
        private static float DistanceNow(string of)
        {
            switch (of)
            {
                case "block":
                    return MyBlockBuilderBase.IntersectionDistance;
                case "paste":
                    MyGridClipboard grids = MyClipboardComponent.Static?.Clipboard;
                    return grids != null && grids.IsActive ? PasteDistance(grids) : float.NaN;
                case "voxel":
                    MySessionComponentVoxelHand voxel = MySessionComponentVoxelHand.Static;
                    if (voxel == null)
                        return float.NaN;
                    if (voxelDistanceRef == null)
                        voxelDistanceRef = AccessTools.FieldRefAccess<MySessionComponentVoxelHand, float>("m_position");
                    return voxelDistanceRef(voxel);
                default:
                    return float.NaN;
            }
        }

        private static void Symmetry(double seconds)
        {
            bool down = VRInput.IsPressed(Hand.Left, VRButtons.X);
            if (down && symmetryDown < 0d)
            {
                symmetryDown = seconds;
                symmetryCycled = false;
            }
            else if (down && !symmetryCycled && seconds - symmetryDown >= SymmetryHoldSeconds)
            {
                symmetryCycled = true;
                SymmetryCycle = true;
                symmetryAsked = "plane";
                symmetryLogIn = 2;
                Haptics.Pulse(Hand.Left, TickAmplitude, TickSeconds);
            }
            else if (!down && symmetryDown >= 0d)
            {
                if (!symmetryCycled)
                {
                    SymmetryTap = true;
                    symmetryAsked = "switch";
                    symmetryLogIn = 2;
                }
                symmetryDown = -1d;
            }
        }

        /// <summary>A frame or two after the game has had a distance, symmetry, size or stage press, what it made of it.</summary>
        private static void ReportAfterGame(MyCubeBuilder builder, MyClipboardComponent clipboard)
        {
            if (distanceLogIn > 0 && --distanceLogIn == 0)
            {
                float now = DistanceNow(distanceOf);
                bool same = Math.Abs(now - distanceBefore) < 0.001f;
                switch (distanceOf)
                {
                    case "block":
                        Log.Info($"Build: distance {distanceBefore:F2} -> {now:F2} m{(!same ? string.Empty : Creative() ? " (unchanged: at the game's limit)" : " (unchanged: survival, the game moves the block only in creative)")}");
                        break;
                    case "paste":
                        Log.Info($"Paste: distance {distanceBefore:F2} -> {now:F2} m{(clipboard?.Clipboard?.IsActive == true ? string.Empty : " (not grids: their distance is not read)")}");
                        break;
                    default:
                        Log.Info($"Voxel hand: distance {distanceBefore:F2} -> {now:F2} m{(same ? " (unchanged: at its limit, or no brush)" : string.Empty)}");
                        break;
                }
            }
            if (symmetryLogIn > 0 && --symmetryLogIn == 0 && builder != null)
                Log.Info($"Build: symmetry {symmetryAsked} asked; symmetry now {(builder.UseSymmetry ? "on" : "off")}, plane setting {SymmetryMode(builder)}");
            if (stageLogIn > 0 && --stageLogIn == 0 && builder != null)
            {
                string now = Name(builder.CurrentBlockDefinition);
                int stages = builder.CubeBuilderState?.CurrentBlockDefinitionStages?.Count ?? 0;
                Log.Info($"Build: stage {stageAsked} asked; block {stageBefore} -> {now}{(now != stageBefore ? string.Empty : stages == 0 ? " (unchanged: the block has no stages)" : " (unchanged: no other stage available)")}; {stages} in its group");
            }
            if (sizeLogIn > 0 && --sizeLogIn == 0 && builder != null)
            {
                string now = Name(builder.CurrentBlockDefinition);
                Log.Info($"Build: block size asked; block {sizeBefore} -> {now} ({builder.CurrentBlockDefinition?.CubeSize}){(now == sizeBefore ? " (unchanged: the block has no other size)" : string.Empty)}");
            }
        }

        // ---- Triggers and haptics ----

        /// <summary>From VRControls with this frame's triggers as the game is given them (a trigger kept by a closing radial menu is not a pull).</summary>
        internal static void Triggers(bool place, bool remove, double seconds)
        {
            // While a symmetry plane is being set the triggers set and clear it (the game then leaves the setting): no block is expected.
            string mode = null;
            bool setting = ((place && !placeWas) || (remove && !removeWas)) && SettingPlane(MyCubeBuilder.Static, out mode);
            if (setting)
                Log.Info($"Build: {(place && !placeWas ? "place" : "remove")} pull while setting the symmetry plane ({mode})");
            else
            {
                if (place && !placeWas)
                {
                    placeAt = seconds;
                    placedAtPull = placed;
                }
                if (remove && !removeWas)
                {
                    removeAt = seconds;
                    removedAtPull = removed;
                }
            }
            placeWas = place;
            removeWas = remove;
        }

        private static void CheckRefused(MyCubeBuilder builder, double seconds)
        {
            Pending(builder, seconds, ref placeAt, placed, placedAtPull, placeWas, Hand.Right, "place");
            Pending(builder, seconds, ref removeAt, removed, removedAtPull, removeWas, Hand.Left, "remove");
        }

        private static void Pending(MyCubeBuilder builder, double seconds, ref double at, int count, int countAtPull, bool stillHeld, Hand hand, string what)
        {
            if (at < 0d)
                return;
            if (count != countAtPull)
            {
                at = -1d;
                return;
            }
            if (seconds - at < RefuseWindow)
                return;
            MyCubeBuilderGizmo.MyGizmoSpaceProperties space = Gizmo(builder).SpaceDefault;
            // A line or plane being dragged out (Ctrl in the game) places on the release: wait for it.
            if (stillHeld && (space.m_startBuild.HasValue || space.m_startRemove.HasValue))
                return;
            at = -1d;
            Refuse(hand, $"{what} refused: nothing {(what == "place" ? "placed" : "removed")} ({Why(builder, space, what)})");
        }

        private static string Why(MyCubeBuilder builder, MyCubeBuilderGizmo.MyGizmoSpaceProperties space, string what)
        {
            bool creative = MySession.Static != null && (MySession.Static.CreativeMode || MySession.Static.CreativeToolsEnabled(Sandbox.Game.Multiplayer.Sync.MyId));
            if (what == "remove")
                return !creative ? "survival: the game removes blocks with the grinder" : space.m_removeBlock == null ? "no block under the ray" : "the game kept it";
            return $"{(creative ? "creative" : "survival")}, placement {(space.m_buildAllowed ? "allowed" : "blocked or out of reach")}, {(CurrentGrid(builder) != null ? "on a grid" : "no grid")}";
        }

        private static void Refuse(Hand hand, string why)
        {
            Haptics.Pulse(hand, RefuseAmplitude, RefuseSeconds);
            secondBuzzAt = frameSeconds + RefuseGap;
            secondBuzzHand = hand;
            Log.Info("Build: " + why);
        }

        /// <summary>From <see cref="Haptics"/>: the game placed a block (any way: a pull, symmetry, a line).</summary>
        internal static void NotePlaced()
        {
            placed++;
            if (Active)
                Log.Info($"Build: placed {MyCubeBuilder.Static?.CurrentBlockDefinition?.Id.SubtypeName}");
        }

        private static void BeforeRemove(List<Vector3I> ___m_tmpBlockPositionList)
        {
            try
            {
                if (___m_tmpBlockPositionList == null || ___m_tmpBlockPositionList.Count == 0)
                    return;
                removed++;
                Haptics.Pulse(Hand.Left, RemoveAmplitude, RemoveSeconds);
                Log.Info($"Build: removed {___m_tmpBlockPositionList.Count} block{(___m_tmpBlockPositionList.Count == 1 ? string.Empty : "s")} at {___m_tmpBlockPositionList[0]}");
            }
            catch (Exception e)
            {
                if (errors++ < 3)
                    Log.Error(e, "Build mode: removal pulse failed");
            }
        }

        // ---- The builder's private parts ----

        private static MyCubeBuilderGizmo Gizmo(MyCubeBuilder builder)
        {
            if (gizmoRef == null)
                gizmoRef = AccessTools.FieldRefAccess<MyCubeBuilder, MyCubeBuilderGizmo>("m_gizmo");
            if (animationLockRef == null)
                animationLockRef = AccessTools.FieldRefAccess<MyCubeBuilder, bool>("m_animationLock");
            return gizmoRef(builder);
        }

        private static AccessTools.FieldRef<MyCubeBuilder, bool> customRotationRef;

        /// <summary>
        /// As the game's rotate keys do with the default mount point on (MyCubeBuilder.HandleRotationInput): the block has been
        /// turned by hand, so the builder stops turning it back to sit on its default mount point (CheckGizmoRotation, which
        /// RotateAxis runs) until the ray moves to another grid.
        /// </summary>
        private static void CustomRotation(MyCubeBuilder builder)
        {
            if (!builder.AlignToDefault)
                return;
            if (customRotationRef == null)
                customRotationRef = AccessTools.FieldRefAccess<MyCubeBuilder, bool>("m_customRotation");
            customRotationRef(builder) = true;
        }

        private static MyCubeGrid CurrentGrid(MyCubeBuilder builder)
        {
            if (currentGridGetter == null)
                currentGridGetter = AccessTools.PropertyGetter(typeof(MyCubeBuilder), "CurrentGrid");
            return currentGridGetter?.Invoke(builder, null) as MyCubeGrid;
        }

        private static bool SettingPlane(MyCubeBuilder builder, out string mode)
        {
            mode = SymmetryMode(builder);
            return mode != "NoPlane" && mode != "Disabled" && mode != "?";
        }

        private static string SymmetryMode(MyCubeBuilder builder)
        {
            if (symmetryModeGetter == null)
                symmetryModeGetter = AccessTools.PropertyGetter(typeof(MyCubeBuilder), "SymmetrySettingMode");
            return symmetryModeGetter?.Invoke(builder, null)?.ToString() ?? "?";
        }

        // ---- The clipboard's and the voxel hand's ----

        private static MethodInfo applyAngle;

        private static void PasteRefs()
        {
            if (pasteForwardRef != null)
                return;
            pasteForwardRef = AccessTools.FieldRefAccess<MyGridClipboard, Vector3>("m_pasteDirForward");
            pasteUpRef = AccessTools.FieldRefAccess<MyGridClipboard, Vector3>("m_pasteDirUp");
            pasteAngleRef = AccessTools.FieldRefAccess<MyGridClipboard, float>("m_pasteOrientationAngle");
            applyAngle = AccessTools.Method(typeof(MyGridClipboard), "ApplyOrientationAngle")
                         ?? throw new MissingMethodException(nameof(MyGridClipboard), "ApplyOrientationAngle");
        }

        /// <summary>How far out the paste is held, metres (the clipboard's drag distance, which MOVE_CLOSER and MOVE_FURTHER change by a tenth).</summary>
        private static float PasteDistance(MyGridClipboard grids)
        {
            if (dragDistanceRef == null)
                dragDistanceRef = AccessTools.FieldRefAccess<MyGridClipboard, float>("m_dragDistance");
            return dragDistanceRef(grids);
        }

        private static AccessTools.FieldRef<MySessionComponentVoxelHand, byte> voxelMaterialRef;

        /// <summary>The voxel hand's material, by name ("?" when it cannot be read).</summary>
        internal static string VoxelMaterialName()
        {
            try
            {
                MySessionComponentVoxelHand voxel = MySessionComponentVoxelHand.Static;
                if (voxel == null)
                    return "?";
                if (voxelMaterialRef == null)
                    voxelMaterialRef = AccessTools.FieldRefAccess<MySessionComponentVoxelHand, byte>("m_selectedMaterial");
                return MyDefinitionManager.Static.GetVoxelMaterialDefinition(voxelMaterialRef(voxel))?.Id.SubtypeName ?? "?";
            }
            catch (Exception)
            {
                return "?";
            }
        }
    }
}
