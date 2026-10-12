using System;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRageMath;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// Dead, or choosing where to respawn: the player stands in a still, dark space, and the game's own camera is shown on a
    /// screen ahead instead of being the view. The respawn screen is worked with the laser as any menu is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What the game does from death to the respawn screen, all of it moving the camera by itself: the character dies
    /// (MyCharacter.DieInternal) and its view becomes the death camera, a look-at from a spot above and behind the body that
    /// turns to follow the ragdoll's head as it falls (MyCharacter.GetViewMatrix:4913, with SwitchToSpectatorCameraAfterDeath);
    /// after the respawn countdown (StartRespawn, the "respawn in" notice) the player is killed off (UpdateRespawnAndLooting,
    /// Sync.Players.KillPlayer) and the respawn screen opens (MyGuiScreenMedicals), which puts the camera on the chosen medical
    /// room or survival kit, or out over a planet, and moves it as the choice changes. Driving the eyes from any of that
    /// would swing the view with nothing the player did.
    /// </para>
    /// <para>
    /// So while it lasts no eye sees the world: both are black with the laser's beam, as the main menu's are
    /// (<see cref="MainMenu"/>), and the game's camera - whatever it is doing - is drawn by the external view's pass
    /// (<see cref="ExternalView"/>, <see cref="ExternalView.Kind.Death"/>) onto the screen's quad layer, standing still in the
    /// tracking space: level ahead of the head as the death began, and once the respawn screen is up, behind its panel and as
    /// wide as it looks, so the menu is over the game's view as on a monitor. Nothing moves the view but the head. It lasts
    /// while the local character is dead, the respawn screen is open, or the player has no character at all (between the
    /// two, and before a first spawn), and the spectator camera is not being flown.
    /// </para>
    /// </remarks>
    internal static class DeathView
    {
        /// <summary>How far behind a menu standing ahead the screen is put, metres.</summary>
        private const float BehindMenu = 0.25f;

        /// <summary>Debug: the game's death camera and the respawn screen's camera are the view again (<see cref="RenderDebug"/> DeathView=0).</summary>
        public static bool Off { get; set; }

        // ---- game thread ----
        private static volatile bool showing;
        private static string why;
        private static int errors;

        /// <summary>Frames the death view failed to draw (render thread); at three it is off for the rest of the run.</summary>
        private static volatile int drawErrors;

        // ---- render thread ----
        private static bool placed, behindMenu;
        private static Matrix pose = Matrix.Identity;
        private static float width = 1f;

        /// <summary>Any thread: the death view is on (decided on the game thread with the frame's camera).</summary>
        public static bool Showing => showing;

        /// <summary>Render thread: this frame's eyes were the death view's (no world in them).</summary>
        public static bool Drawing { get; set; }

        /// <summary>The screen in the tracking space, facing +z. Render thread.</summary>
        internal static Matrix Pose => pose;

        /// <summary>The screen's width, metres. Render thread.</summary>
        internal static float Width => width;

        /// <summary>
        /// Game thread, as the camera goes to the renderer (<see cref="ExternalView"/>'s Capture): whether this frame is the
        /// death view's. Logged as it starts, as its reason changes, and as it ends.
        /// </summary>
        internal static bool Wanted()
        {
            bool failed = errors >= 3 || drawErrors >= 3;
            string reason = Off || failed ? null : Reason();
            if (reason != why)
            {
                if (why == null)
                    Log.Info($"Death view: on ({reason}); the eyes are a still, dark space and the game's own camera is on a screen ahead");
                else if (reason == null)
                    Log.Info($"Death view: over ({(Off ? "turned off" : failed ? "it failed" : "a living character again")}); the eyes show the world again");
                else
                    Log.Info($"Death view: {reason}");
                why = reason;
            }
            showing = reason != null;
            return showing;
        }

        /// <summary>Why the death view is wanted now, or null when it is not.</summary>
        private static string Reason()
        {
            try
            {
                MySession session = MySession.Static;
                if (session == null)
                    return null;
                MyCharacter character = session.LocalCharacter;
                if (character != null && character.IsDead)
                    return "the character is dead";
                if (Sync.Players?.RespawnComponent?.IsInRespawnScreen() == true)
                    return "the respawn screen is open";
                if (character == null && session.LocalHumanPlayer != null && !session.IsCameraUserControlledSpectator())
                    return "no character";
                return null;
            }
            catch (Exception e)
            {
                if (++errors >= 3)
                    Log.Error(e, "Death view: telling whether the player is dead failed three times; it is off for the rest of the run");
                else
                    Log.Error(e, "Death view: telling whether the player is dead failed");
                return null;
            }
        }

        /// <summary>
        /// Render thread, each frame of the death view, before its picture: where the screen stands. The first frame, level
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
                    Log.Info($"Death view: screen put behind the menu, {distance:0.00} m ahead and {width:0.00} m wide, at ({at.X:0.00}, {at.Y:0.00}, {at.Z:0.00}) in the tracking space");
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
            Log.Info($"Death view: screen placed level ahead, {distance:0.00} m away and {width:0.00} m wide, at ({put.X:0.00}, {put.Y:0.00}, {put.Z:0.00}) in the tracking space");
        }

        /// <summary>The screen <paramref name="distance"/> from <paramref name="from"/> along <paramref name="along"/> (level), upright, facing back, as wide as a menu looks.</summary>
        private static void Put(Vector3 from, Vector3 along, float distance)
        {
            width = 2f * distance * (float)Math.Tan(MathHelper.ToRadians(VRSettings.MenuWidthDegrees) / 2f);
            pose = Matrix.CreateWorld(from + along * distance, along, Vector3.Up);
        }

        /// <summary>Render thread: a death view frame could not be drawn (the eyes draw the world instead). Three turn it off.</summary>
        internal static void DrawFailed(Exception e)
        {
            int n = ++drawErrors;
            if (n >= 3)
                Log.Error(e, "Death view: drawing it failed three times; the game's camera is the view again for the rest of the run");
            else
                Log.Error(e, "Death view: drawing it failed; this frame the eyes show the game's camera");
        }

        /// <summary>Render thread: the death view's frames have ended; the next one is placed afresh.</summary>
        internal static void Ended()
        {
            placed = behindMenu = false;
        }
    }
}
