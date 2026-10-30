using System;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using SpaceEngineers.Game.Entities.Blocks;
using VRage.Game;
using VRage.Game.ModAPI.Interfaces;
using VRageMath;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// Dead, choosing where to respawn, watching a cutscene or looking through a block's fixed camera: the player stands in
    /// a still, dark space, and the game's own camera is shown on a screen ahead instead of being the view. The respawn
    /// screen is worked with the laser as any menu is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What the game does from death to the respawn screen, all of it moving the camera by itself: the character dies
    /// (MyCharacter.DieInternal) and its view becomes the death camera, a look-at from a spot above and behind the body that
    /// turns to follow the ragdoll's head as it falls (MyCharacter.GetViewMatrix:4913, with SwitchToSpectatorCameraAfterDeath);
    /// after the respawn countdown (StartRespawn, the "respawn in" notice) the player is killed off (UpdateRespawnAndLooting,
    /// Sync.Players.KillPlayer) and the respawn screen opens (MyGuiScreenMedicals), which puts the camera on the chosen medical
    /// room or survival kit, or out over a planet, and moves it as the choice changes. Driving the eyes from any of that
    /// would swing the view with nothing the player did. A scenario's cutscene does the same from start to end: while it
    /// plays the view is a MyCutsceneCamera, flown along the cutscene's waypoints and cut between its nodes
    /// (MySessionComponentCutscenes.UpdateBeforeSimulation and CutsceneUpdate), until the controller is given back.
    /// </para>
    /// <para>
    /// So while it lasts no eye sees the world: both are black with the laser's beam, as the main menu's are
    /// (<see cref="MainMenu"/>), and the game's camera - whatever it is doing - is drawn by the external view's pass
    /// (<see cref="ExternalView"/>, <see cref="ExternalView.Kind.Forced"/>) onto the screen's quad layer, standing still in the
    /// tracking space: level ahead of the head as the death began, and once the respawn screen is up, behind its panel and as
    /// wide as it looks, so the menu is over the game's view as on a monitor. Nothing moves the view but the head. It lasts
    /// while the local character is dead, the respawn screen is open, a cutscene's camera is the view, or the player has no
    /// character at all (between the two, and before a first spawn), and the spectator camera is not being flown. Leaving it
    /// changes the camera's controller, so the world fades in (<see cref="CameraCut"/>).
    /// </para>
    /// <para>
    /// A block's camera (<see cref="FixedCamera"/>) is the same screen for another reason: the game gives it no look-around,
    /// so a head turning or moving the view would show more than the game does. The screen shows exactly the block's view at
    /// the game's field of view, its zoom included, as a monitor does; the block is aimed as in the game, and the view turns
    /// with it. When the screen cannot be shown (turned off, or failed), the eyes are the block's camera with the head's turn
    /// and move taken out (<see cref="Tracking.CameraHeadLink.FixedInCamera"/>), never the head's view.
    /// </para>
    /// </remarks>
    internal static class ForcedView
    {
        /// <summary>How far behind a menu standing ahead the screen is put, metres.</summary>
        private const float BehindMenu = 0.25f;

        /// <summary>The camera a cutscene flies (MyCutsceneCamera, internal to the game).</summary>
        private static readonly Type CutsceneCamera = typeof(MySession).Assembly.GetType("Sandbox.Engine.Utils.MyCutsceneCamera");

        // ---- game thread ----
        private static volatile bool showing;
        private static volatile bool forBlock;
        private static string why;

        private const string CutsceneReason = "a cutscene is playing";
        private static readonly AccessTools.FieldRef<MySessionComponentCutscenes, Cutscene> playing =
            AccessTools.FieldRefAccess<MySessionComponentCutscenes, Cutscene>("m_currentCutscene");

        /// <summary>Game thread: a cutscene is on the screen (as of the last camera sent).</summary>
        public static bool InCutscene => ReferenceEquals(why, CutsceneReason);

        /// <summary>Game thread: the cutscene on the screen lets itself be skipped (Cutscene.CanBeSkipped).</summary>
        public static bool CutsceneSkippable()
        {
            var cutscenes = MySession.Static?.GetComponent<MySessionComponentCutscenes>();
            return InCutscene && cutscenes != null && playing(cutscenes)?.CanBeSkipped == true;
        }
        private static int errors;

        /// <summary>Frames the forced view failed to draw (render thread); at three it is off for the rest of the run.</summary>
        private static volatile int drawErrors;

        // ---- render thread ----
        private static bool placed, behindMenu;
        private static Matrix pose = Matrix.Identity;
        private static float width = 1f;

        /// <summary>Any thread: the forced view is on (decided on the game thread with the frame's camera).</summary>
        public static bool Showing => showing;

        /// <summary>Any thread: the forced view is on for a block's camera (<see cref="FixedCamera"/>), whose picture is the player's whole view.</summary>
        public static bool ForBlock => forBlock;

        /// <summary>Render thread: this frame's eyes were the forced view's (no world in them).</summary>
        public static bool Drawing { get; set; }

        /// <summary>The screen in the tracking space, facing +z. Render thread.</summary>
        internal static Matrix Pose => pose;

        /// <summary>The screen's width, metres. Render thread.</summary>
        internal static float Width => width;

        /// <summary>
        /// Game thread, as the camera goes to the renderer (<see cref="ExternalView"/>'s Capture): whether this frame is the
        /// forced view's. Logged as it starts, as its reason changes, and as it ends.
        /// </summary>
        internal static bool Wanted()
        {
            bool failed = errors >= 3 || drawErrors >= 3;
            bool block = false;
            string reason = failed ? null : Reason(out block);
            if (reason != why)
            {
                if (why == null)
                    Log.Info($"Forced view: on ({reason}); the eyes are a still, dark space and the game's own camera is on a screen ahead");
                else if (reason == null)
                    Log.Info($"Forced view: over ({(failed ? "it failed" : "the character's own view again")}); the eyes show the world again");
                else
                    Log.Info($"Forced view: {reason}");
                why = reason;
            }
            showing = reason != null;
            forBlock = showing && block;
            return showing;
        }

        /// <summary>Why the forced view is wanted now, or null when it is not; <paramref name="block"/>: for a block's camera.</summary>
        private static string Reason(out bool block)
        {
            block = false;
            try
            {
                MySession session = MySession.Static;
                if (session == null)
                    return null;
                if (session.CameraController?.GetType() == CutsceneCamera)
                    return CutsceneReason;
                MyCharacter character = session.LocalCharacter;
                if (character != null && character.IsDead)
                    return "the character is dead";
                if (Sync.Players?.RespawnComponent?.IsInRespawnScreen() == true)
                    return "the respawn screen is open";
                if (character == null && session.LocalHumanPlayer != null && !session.IsCameraUserControlledSpectator())
                    return "no character";
                if (Tracking.GameHead.Tracked && FixedCamera(session.CameraController) is string camera)
                {
                    block = true;
                    return $"the view is {camera}, which the game gives no look-around";
                }
                return null;
            }
            catch (Exception e)
            {
                if (++errors >= 3)
                    Log.Error(e, "Forced view: telling whether the player is dead failed three times; it is off for the rest of the run");
                else
                    Log.Error(e, "Forced view: telling whether the player is dead failed");
                return null;
            }
        }

        /// <summary>
        /// What the camera is, when it is a block's that the game turns only with the block: a camera block (also as a turret
        /// control block's camera: MyTurretControlBlock:2698), a turret or a searchlight. Their camera controllers turn nothing
        /// (MyCameraBlock.Rotate:513, MyLargeTurretBase's IMyCameraController.Rotate:2647, MySearchlight.Rotate:1338 are
        /// empty) and their view is the block's camera point (MyCameraBlock.GetViewMatrix:493, MyLargeTurretBase.GetViewMatrix
        /// :2625, MySearchlight.GetViewMatrix:1316), which moves only as the turret or searchlight is aimed. Null otherwise.
        /// Game thread.
        /// </summary>
        internal static string FixedCamera(IMyCameraController controller)
        {
            switch (controller)
            {
                case MyCameraBlock camera when camera.IsActive: return "a camera block's";
                case MyLargeTurretBase _: return "a turret's";
                case MySearchlight _: return "a searchlight's";
                default: return null;
            }
        }

        /// <summary>
        /// Render thread, each frame of the forced view, before its picture: where the screen stands. The first frame, level
        /// ahead of the head as it faces then, a little further than a menu stands; once a menu stands ahead (the respawn
        /// screen: <see cref="WorldMenu"/>), behind it and as wide as it looks. It does not move otherwise.
        /// </summary>
        internal static void Place(in Matrix head)
        {
            float distance = VRSettings.MenuDistance + BehindMenu;
            if (WorldMenu.TryGetAhead(out Vector3 menuFrom, out Vector3 menuAlong))
            {
                if (!behindMenu)
                {
                    Put(menuFrom, menuAlong, distance);
                    behindMenu = placed = true;
                    Vector3 at = pose.Translation;
                    Log.Info($"Forced view: screen put behind the menu, {distance:0.00} m ahead and {width:0.00} m wide, at ({at.X:0.00}, {at.Y:0.00}, {at.Z:0.00}) in the tracking space");
                }
                return;
            }
            if (placed)
                return;
            Vector3 forward = head.Forward;
            Vector3 along = new Vector3(forward.X, 0f, forward.Z);
            if (along.LengthSquared() < 0.04f)
            {
                Vector3 up = head.Up;
                along = forward.Y < 0f ? new Vector3(up.X, 0f, up.Z) : new Vector3(-up.X, 0f, -up.Z);
            }
            if (along.LengthSquared() < 1e-6f)
                along = Vector3.Forward;
            along.Normalize();
            Put(head.Translation, along, distance);
            placed = true;
            Vector3 put = pose.Translation;
            Log.Info($"Forced view: screen placed level ahead, {distance:0.00} m away and {width:0.00} m wide, at ({put.X:0.00}, {put.Y:0.00}, {put.Z:0.00}) in the tracking space");
        }

        /// <summary>The screen <paramref name="distance"/> from <paramref name="from"/> along <paramref name="along"/> (level), upright, facing back, as wide as a menu looks.</summary>
        private static void Put(Vector3 from, Vector3 along, float distance)
        {
            width = 2f * distance * (float)Math.Tan(MathHelper.ToRadians(VRSettings.MenuWidthDegrees) / 2f);
            pose = Matrix.CreateWorld(from + along * distance, along, Vector3.Up);
        }

        /// <summary>Render thread: a forced view frame could not be drawn (the eyes draw the world instead). Three turn it off.</summary>
        internal static void DrawFailed(Exception e)
        {
            int n = ++drawErrors;
            if (n >= 3)
                Log.Error(e, "Forced view: drawing it failed three times; the game's camera is the view again for the rest of the run");
            else
                Log.Error(e, "Forced view: drawing it failed; this frame the eyes show the game's camera");
        }

        /// <summary>Render thread: the forced view's frames have ended; the next one is placed afresh.</summary>
        internal static void Ended()
        {
            placed = behindMenu = false;
        }
    }
}
