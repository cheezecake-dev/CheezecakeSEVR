using System;
using System.Reflection;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.SessionComponents.Clipboard;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Input.Bindings;
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
    internal static partial class BuildMode
    {
        /// <summary>Debug: the old on-foot buttons with a block in hand (<see cref="Rendering.RenderDebug"/> BuildMode=0).</summary>
        public static bool Off { get; set; }

        /// <summary>How much nearer the next 90 degree step has to be than the one shown before the block moves to it, degrees: a single-axis turn steps at 50 degrees and back at 40.</summary>
        public const double SnapHysteresisDegrees = 10.0;

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

        // The distance, symmetry, the size and the stage.
        private static double distanceDown = -1d, distanceNext;
        private static int distanceDirection;
        private static float distanceBefore;
        private static int distanceLogIn;
        private static string distanceOf;
        private static bool building;
        private static int stageLogIn, sizeLogIn;
        private static string stageAsked, stageBefore, sizeBefore;
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
            PatchRegistry.Hook(harmony, typeof(BuildMode), "Build mode", "the rotation hints stay in the corner of each eye",
                "rotation hints", AccessTools.Method(typeof(MyBlockBuilderRotationHints), nameof(MyBlockBuilderRotationHints.CalculateRotationHints)),
                prefix: nameof(NoRotationHints));
        }

        /// <summary>
        /// Prefix of MyBlockBuilderRotationHints.CalculateRotationHints: while the picture is VR's, the hints are worked out but
        /// not drawn. They are a flat widget in a corner of the screen (a viewport of their own) labelled with the rotate keys;
        /// in the headset that is the edge of the lens, and the hand turns the block. The game's own switches do not fit: its
        /// RotationHints setting is the player's saved option, and MyFakes.ENABLE_ROTATION_HINTS misses the paste's hints.
        /// </summary>
        private static void NoRotationHints(ref bool draw)
        {
            if (Rendering.StereoPatches.VRPicture)
                draw = false;
        }

        /// <summary>From <see cref="HandPlacement.HookNow"/>, on the first frame of a session: the cube builder's own methods can be patched by then.</summary>
        internal static void HookBuilder(Harmony harmony)
        {
            PatchRegistry.Apply("block removal", () =>
            {
                harmony.Patch(AccessTools.Method(typeof(MyCubeBuilder), "Remove", Type.EmptyTypes),
                    prefix: new HarmonyMethod(typeof(BuildMode), nameof(BeforeRemove)));
                Log.Info("Build mode: block removal hooked (MyCubeBuilder.Remove)");
            }, "Build mode could not hook block removal; removing gives no pulse");
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
            bool modifier = building && ControllerBindings.IsActive(VRAction.BuildModifier);
            if (modifier != Modifier)
                Log.Info(modifier ? "Build: left stick click held, the right hand and stick turn and move what is in hand" : "Build: left stick click let go");
            Modifier = modifier;
            ModifierStick(modifier, stick);
            StickBusy = modifier || (StickBusy && stick.Length() > StickRelease);
            if (!active)
            {
                placeAt = removeAt = -1d;
                placeWas = removeWas = false;
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
            SizeAndStage(builder);
            Distance(seconds, "block");
            Symmetry();
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

        // ---- A, B, X and the stick click ----

        /// <summary>
        /// Large or small block (the right stick click, by default): the block's other size (CUBE_BUILDER_CUBESIZE_MODE; the game
        /// switches only a block whose group has one). Next and previous stage (A and B with the left stick click held), in
        /// creative and survival: NEXT_BLOCK_STAGE, PREV_BLOCK_STAGE; the game steps only a block that has stages, past those not
        /// available here. A press made for a stage is not jump or the jetpack (ControllerBindings: it belongs to the click).
        /// </summary>
        private static void SizeAndStage(MyCubeBuilder builder)
        {
            if (ControllerBindings.Started(VRAction.CubeSize))
            {
                CubeSize = true;
                sizeBefore = Name(builder.CurrentBlockDefinition);
                sizeLogIn = 2;
            }
            bool next = ControllerBindings.Started(VRAction.NextStage), previous = !next && ControllerBindings.Started(VRAction.PreviousStage);
            if (next || previous)
            {
                NextStage = next;
                PrevStage = !next;
                stageAsked = next ? "next" : "previous";
                stageBefore = Name(builder.CurrentBlockDefinition);
                stageLogIn = 2;
            }
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
        /// <summary>Symmetry on/off (a tap of X, by default) and the next symmetry plane (X held), with a tick on the hand as the hold takes.</summary>
        private static void Symmetry()
        {
            if (ControllerBindings.Started(VRAction.SymmetryPlane))
            {
                SymmetryCycle = true;
                symmetryAsked = "plane";
                symmetryLogIn = 2;
                Haptics.Pulse(ControllerBindings.HandOf(VRAction.SymmetryPlane), TickAmplitude, TickSeconds);
            }
            else if (ControllerBindings.Started(VRAction.Symmetry))
            {
                SymmetryTap = true;
                symmetryAsked = "switch";
                symmetryLogIn = 2;
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
    }
}
