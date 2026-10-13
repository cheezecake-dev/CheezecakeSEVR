using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.OpenXR;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// An exception of ours in the eyes' frame (<see cref="EyeRenderer"/>) neither stops VR nor ends the game: the eyes go off,
    /// black with the panel placed in each and the laser's beam, as the main menu's (<see cref="MainMenu"/>), so the menus stay
    /// usable, and a message on the panel says what happened. The eyes are tried again once the world is ready, the character
    /// alive and in control, no menu is open, and something has changed since: the message was closed, or the world, the
    /// character or what it controls is another. That happens at most twice in a run; a third failure leaves the eyes off
    /// until the game is restarted, with the menus still usable to save and leave.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before this such an exception went up into the game's render thread, whose handler puts a "Graphics device driver has
    /// crashed" box on the monitor, holding the render thread, and so the headset's view, frozen until it is closed, and then
    /// ends the game.
    /// </para>
    /// <para>
    /// Only exceptions of ours are taken (<see cref="Ours"/>). One the game's own code threw with none of ours on its way, one
    /// from the GPU or its driver, from the headset's runtime, or for want of memory goes on to the game exactly as before, and
    /// so does a second failure while the eyes are off. A hang throws nothing and is left alone; the stall watchdog reports it.
    /// The exception is logged in full, and each time the eyes go off or come back is a line of its own.
    /// </para>
    /// <para>
    /// While the eyes are off the game is given no head, so its own head angles hold the view, as when the headset is lost,
    /// and no eye sees the world. An exception from one of our hooks on the game thread is not taken here: it goes up through
    /// the game's update, as before.
    /// </para>
    /// </remarks>
    // From ColbyJack1134's SpaceEngineersVR (Plugin/Main.cs), with his permission.
    internal static class MenuFallback
    {
        /// <summary>How many times in a run the eyes are tried again after a failure.</summary>
        private const int MaxResumes = 2;

        private static readonly Assembly Own = typeof(MenuFallback).Assembly;

        /// <summary>The eyes are off and the menus shown instead. Set on the render thread, cleared on the game thread.</summary>
        private static volatile bool active;

        /// <summary>Set on the render thread, before <see cref="active"/>: the game thread has yet to take note of the world and put the message up.</summary>
        private static volatile bool entered;

        /// <summary>How many times the eyes were tried again. Written on the game thread.</summary>
        private static volatile int resumes;

        // ---- game thread ----
        private static object failedSession, failedCharacter, failedControl;
        private static bool sawMenu, stopped;

        /// <summary>Any thread: the eyes are off after a failure of ours, and the menus are shown instead.</summary>
        public static bool Active => active;

        /// <summary>
        /// The exception is ours: thrown by our code, or on its way up through it, and not the GPU's, the headset runtime's or for
        /// want of memory. One that came up through a reflection call into the game (its scene drawing, say) is ours only when a
        /// frame of ours is on its way between where it was thrown and that call; otherwise it is the game's own.
        /// </summary>
        public static bool Ours(Exception e)
        {
            Exception thrown = e;
            while (thrown is TargetInvocationException && thrown.InnerException != null)
                thrown = thrown.InnerException;
            if (thrown is OutOfMemoryException || thrown is ThreadAbortException || thrown is ExternalException
                || thrown is SharpDX.SharpDXException || thrown is XrException)
                return false;
            if (ReferenceEquals(thrown, e))
                return true;
            StackFrame[] frames = new StackTrace(thrown, false).GetFrames();
            if (frames == null)
                return false;
            foreach (StackFrame frame in frames)
            {
                if (frame.GetMethod()?.DeclaringType?.Assembly == Own)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Render thread, from the catch of a frame that threw <paramref name="e"/>, which <see cref="Ours"/> said is ours: the eyes
        /// are off from now on, and the game thread puts the message up (<see cref="Update"/>).
        /// </summary>
        public static void Fail(Exception e, string context)
        {
            Log.Error(e, $"{context} failed; the eyes are off and the menus stay up in VR, " +
                         (resumes < MaxResumes ? "until the message about it is closed" : "for the rest of the run"));
            entered = true;
            active = true;
        }

        /// <summary>
        /// Game thread, every frame (<see cref="Plugin.Update"/>): after a failure, takes note of the world as it is and puts the
        /// message up; then, while the eyes are off, turns them on again when <see cref="CanResume"/> says so.
        /// </summary>
        public static void Update()
        {
            if (!active || stopped)
                return;
            try
            {
                MySession session = MySession.Static;
                MyGuiScreenBase focus = MyScreenManager.GetScreenWithFocus();
                bool menu = focus != null && !(focus is MyGuiScreenGamePlay);
                if (entered)
                {
                    entered = false;
                    failedSession = session;
                    failedCharacter = session?.LocalCharacter;
                    failedControl = session?.ControlledEntity;
                    // Without the message nothing would mark the player's go-ahead; the world's state alone decides then.
                    sawMenu = !Tell();
                    return;
                }
                sawMenu |= menu;
                bool changed = sawMenu || !ReferenceEquals(session, failedSession) || !ReferenceEquals(session?.LocalCharacter, failedCharacter)
                               || !ReferenceEquals(session?.ControlledEntity, failedControl);
                bool worldReady = session != null && MyGuiScreenGamePlay.Static?.LoadingDone == true;
                if (!CanResume(worldReady, session?.LocalCharacter?.IsDead == false, session?.ControlledEntity?.Entity != null, menu, changed, resumes))
                    return;
                failedSession = failedCharacter = failedControl = null;
                resumes++;
                active = false;
                Log.Info($"The eyes are on again after the failure (try {resumes} of {MaxResumes})");
            }
            catch (Exception e)
            {
                stopped = true;
                Log.Error(e, "Following the world for the eyes' return failed; the eyes stay off and the menus up for the rest of the run");
            }
        }

        /// <summary>
        /// The eyes may be tried again: the world is loaded, the character alive and in control of something, no menu has the
        /// focus, something changed since the failure, and the tries are not used up.
        /// </summary>
        internal static bool CanResume(bool worldReady, bool characterAlive, bool controlled, bool menu, bool changed, int tries) =>
            worldReady && characterAlive && controlled && !menu && changed && tries < MaxResumes;

        /// <summary>The message on the panel: what happened and what the player can do. False when it could not be put up.</summary>
        private static bool Tell()
        {
            string text = resumes < MaxResumes
                ? "The VR view failed, so the world is hidden for now; the menus still work.\n\n" +
                  "Close this message to try the view again. The game's menu can save and leave.\n\nDetails are in SpaceEngineersVR.log."
                : "The VR view failed again, so the world stays hidden until the game is restarted; the menus still work.\n\n" +
                  "Save and leave from the game's menu.\n\nDetails are in SpaceEngineersVR.log.";
            try
            {
                MyGuiSandbox.AddScreen(MyGuiSandbox.CreateMessageBox(MyMessageBoxStyleEnum.Error,
                    messageText: new StringBuilder(text), messageCaption: new StringBuilder(PatchRegistry.PluginName)));
                return true;
            }
            catch (Exception e)
            {
                Log.Error(e, "The message that the eyes are off could not be shown");
                return false;
            }
        }
    }
}
