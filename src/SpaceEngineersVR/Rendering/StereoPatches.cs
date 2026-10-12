using System;
using System.Diagnostics;
using HarmonyLib;
using Sandbox;
using Sandbox.Engine.Platform.VideoMode;
using Sandbox.Engine.Utils;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using SpaceEngineersVR.Gui;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.OpenXR;
using SpaceEngineersVR.Tracking;
using VRage;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// Puts the game into VR mode: the desktop window becomes a fixed-size mirror (the player's own resolution and
    /// window mode stay in their config), and the renderer draws the scene once per eye (<see cref="EyeRenderer"/>).
    /// </summary>
    internal static partial class StereoPatches
    {
        /// <summary>The plugin's Harmony instance, kept by <see cref="Apply"/> for the second phase (<see cref="PatchRenderer"/>). Written once at startup, before any other thread reads it.</summary>
        private static Harmony harmony;

        /// <summary>
        /// Called before the game's Main. Only hooks the video settings: patching the renderer itself runs
        /// MyRender11's static constructor, which needs the game's file system, and that is not up yet.
        /// </summary>
        public static void Apply(Harmony harmony)
        {
            StereoPatches.harmony = harmony;
            // Patches: MyVideoSettingsManager.Initialize (postfix, starts phase two and switches to the VR device settings),
            // .Apply (prefix, stops the game replacing them) and .WriteCurrentSettingsToConfig (postfix, restores the player's
            // own video config on disk).
            harmony.Patch(AccessTools.Method(typeof(MyVideoSettingsManager), nameof(MyVideoSettingsManager.Initialize)),
                postfix: new HarmonyMethod(typeof(StereoPatches), nameof(UseVRDeviceSettings)));
            harmony.Patch(AccessTools.Method(typeof(MyVideoSettingsManager), nameof(MyVideoSettingsManager.Apply), new[] { typeof(MyRenderDeviceSettings) }),
                prefix: new HarmonyMethod(typeof(StereoPatches), nameof(KeepVRDeviceSettings)));
            harmony.Patch(AccessTools.Method(typeof(MyVideoSettingsManager), nameof(MyVideoSettingsManager.WriteCurrentSettingsToConfig)),
                postfix: new HarmonyMethod(typeof(StereoPatches), nameof(KeepUserVideoConfig)));
        }

        /// <summary>
        /// Second phase: the file system is up and the render device does not exist yet. Each feature patches through
        /// <see cref="PatchRegistry"/> (which says which are core): one that fails is off and the rest go on, and a core
        /// one failing stops here with VR off.
        /// </summary>
        /// <remarks>
        /// These are the picture, the sound and the menus. They go on whether or not the headset is there yet: they have to
        /// be in place before the render device is made and the render thread runs them, and the headset may connect later
        /// in the run. Until it is first found the picture's hooks leave the game's own picture alone (<see cref="VRPicture"/>);
        /// what the player can do is <see cref="PatchPlay"/>'s.
        /// </remarks>
        private static void PatchRenderer()
        {
            Feature(nameof(HeadsetGpu), HeadsetGpu.Patch);
            Feature(nameof(PerfMeter), PerfMeter.Patch);
            Feature(nameof(EyeRenderer), EyeRenderer.Patch);
            Feature(nameof(EyeOffset), EyeOffset.Patch);
            Feature(nameof(SharedShadows), SharedShadows.Patch);
            Feature(nameof(SceneOnce), SceneOnce.Patch);
            Feature(nameof(ParticlesOnce), ParticlesOnce.Patch);
            Feature(nameof(ExposureOnce), ExposureOnce.Patch);
            Feature(nameof(OffCentreAO), OffCentreAO.Patch);
            Feature(nameof(CentredFlares), CentredFlares.Patch);
            Feature(nameof(DebugShapes), DebugShapes.Patch);
            Feature(nameof(GpuPasses), GpuPasses.Patch);
            Feature(nameof(LensMask), LensMask.Patch);
            Feature(nameof(SoundFocus), SoundFocus.Patch);
            Feature(nameof(HeadsetAudio), HeadsetAudio.Patch);
            Feature(nameof(ExternalView), ExternalView.Patch);
            Feature(nameof(GuiLayout), GuiLayout.Patch);
            Feature(nameof(HudCamera), HudCamera.Patch);
            Feature(nameof(HudToolbar), HudToolbar.Patch);
            Feature(nameof(WristStatus), WristStatus.Patch);
            Feature(nameof(HudVisor), HudVisor.Patch);
            Feature(nameof(WorldMenu), WorldMenu.Patch);
            Feature(nameof(MainMenu), MainMenu.Patch);
            Feature(nameof(PanelCursor), PanelCursor.Patch);
            Feature(nameof(FullView), FullView.Patch);
            Feature(nameof(BloodPulse), BloodPulse.Patch);
        }

        /// <summary>
        /// Third phase, the first time a headset is found (<see cref="PatchForHeadset"/>), on the game thread: the features
        /// that change what the player can do. The tracked head and body, the camera, the controls, the hands and the VR
        /// menus. Without a headset none of them is patched; once on they stay on, and after the headset is lost each one
        /// that changes play does so only while a session has the focus again.
        /// </summary>
        private static void PatchPlay()
        {
            Feature(nameof(CharacterHead), CharacterHead.Patch);
            Feature(nameof(CockpitHead), CockpitHead.Patch);
            Feature(nameof(WallFade), WallFade.Patch);
            Feature(nameof(BodyFollow), BodyFollow.Patch);
            Feature(nameof(ZeroGPivot), ZeroGPivot.Patch);
            Feature(nameof(MagBootsSmoothing), MagBootsSmoothing.Patch);
            Feature(nameof(BodyGuard), BodyGuard.Patch);
            Feature(nameof(BodyShift), BodyShift.Patch);
            Feature(nameof(CameraHeadLink), CameraHeadLink.Patch);
            Feature(nameof(CameraShake), CameraShake.Patch);
            Feature(nameof(ExitUpright), ExitUpright.Patch);
            Feature(nameof(StandUpright), StandUpright.Patch);
            Feature(nameof(CameraCut), CameraCut.Patch);
            Feature(nameof(SuitLight), SuitLight.Patch);
            Feature(nameof(ChaseCamera), ChaseCamera.Patch);
            Feature(nameof(ThirdPersonGate), ThirdPersonGate.Patch);
            Feature(nameof(InputPatches), InputPatches.Patch);
            Feature(nameof(Laser), Laser.Patch);
            Feature(nameof(HandIK), HandIK.Patch);
            Feature(nameof(HandResidual), HandResidual.Patch);
            Feature(nameof(Hands.Haptics), Hands.Haptics.Patch);
            Feature(nameof(Hands.HandTools), Hands.HandTools.Patch);
            Feature(nameof(Hands.FingerPress), Hands.FingerPress.Patch);
            Feature(nameof(Hands.ScreenTouch), Hands.ScreenTouch.Patch);
            Feature(nameof(Gui.TouchToolbar), Gui.TouchToolbar.Patch);
            Feature(nameof(Hands.GrabUse), Hands.GrabUse.Patch);
            Feature(nameof(Hands.ShipModel), Hands.ShipModel.Patch);
            Feature(nameof(Hands.QuickInventory), Hands.QuickInventory.Patch);
            Feature(nameof(Hands.Ping), Hands.Ping.Patch);
            Feature(nameof(Hands.HeadGestures), Hands.HeadGestures.Patch);
            PatchRegistry.Apply(nameof(Hands.VoiceTalk), Hands.VoiceTalk.Register);
            Feature(nameof(Hands.ItemGrab), Hands.ItemGrab.Patch);
            Feature(nameof(Hands.HullGrab), Hands.HullGrab.Patch);
            Feature(nameof(Hands.TwoHand), Hands.TwoHand.Patch);
            Feature(nameof(Hands.SurfaceStop), Hands.SurfaceStop.Patch);
            Feature(nameof(Hands.Kickback), Hands.Kickback.Patch);
            Feature(nameof(Hands.Reload), Hands.Reload.Patch);
            Feature(nameof(WristButton), WristButton.Patch);
            Feature(nameof(Hands.Datapad), Hands.Datapad.Patch);
            Feature(nameof(VROptionsEntry), VROptionsEntry.Patch);
            Feature(nameof(VRKeyboardHook), VRKeyboardHook.Patch);
            Feature(nameof(Input.VRPrompts), Input.VRPrompts.Patch);
            Feature(nameof(Gui.VRRadialMenus), Gui.VRRadialMenus.Patch);
            Feature(nameof(Gui.ActionsWheel), Gui.ActionsWheel.Patch);
        }

        private static void Feature(string name, Action<Harmony> patch) => PatchRegistry.Apply(name, () => patch(harmony));

        /// <summary>
        /// The picture is VR's: the eyes, the mirror window, the GUI on its panel at <see cref="GuiLayout.Size"/>, the cursor
        /// through the panel, the HUD's projection and the blood overlay. Off until a headset is first found, so a PC with
        /// an OpenXR runtime and no headset shows the game as it is; after that off again while the headset is away
        /// (<see cref="FollowHeadset"/>). Read from any thread.
        /// </summary>
        internal static bool VRPicture => vrPicture;

        private static volatile bool vrPicture;

        /// <summary>
        /// Game thread: the picture turned to VR's (true) or back to the game's (false), at startup with the headset there,
        /// when it is first found, and at each switch while it comes and goes (<see cref="FollowHeadset"/>). For what has to
        /// be set or undone when VR comes and goes; what is decided every frame reads <see cref="VRPicture"/>.
        /// </summary>
        internal static event Action<bool> PictureChanged;

        private static void SetPicture(bool vr)
        {
            vrPicture = vr;
            Action<bool> handlers = PictureChanged;
            if (handlers == null)
                return;
            foreach (Action<bool> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(vr);
                }
                catch (Exception e)
                {
                    Log.Error(e, $"{handler.Method.DeclaringType?.Name}: following the picture to {(vr ? "VR's" : "the game's")} failed");
                }
            }
        }

        /// <summary>
        /// The picture went back to the game's because the headset is away (<see cref="FollowHeadset"/>); VR's again when its
        /// session has the focus. Read from any thread.
        /// </summary>
        internal static bool PlayingFlat => playingFlat;

        private static volatile bool playingFlat;

        /// <summary>How long the headset has to be away before the picture goes back to the game's: a runtime's own blink between states is not.</summary>
        private const double AwaySeconds = 2.0;

        /// <summary>
        /// Off for now: on Windows the picture's return from flat to VR reset the GPU's driver (a TDR, 2026-10-12, after
        /// flat play at the player's exclusive fullscreen), so with the headset away the picture stays VR's, as before.
        /// </summary>
        private static readonly bool FlatWhenAway = false;

        /// <summary>When the headset was first seen away in this stretch (Stopwatch ticks); 0 while it is in use.</summary>
        private static long awaySince;

        /// <summary>The renderer's features are on (<see cref="PatchRenderer"/>). Game thread.</summary>
        private static bool rendererPatched;

        /// <summary>The play features are on, or were tried (<see cref="PatchPlay"/>).</summary>
        private static bool playPatched;

        private static bool HeadsetFound
        {
            get
            {
                bool found = HeadsetWatcher.HasHeadset;
                if (!found)
                    VRWithoutHeadset(ref found);
                return found;
            }
        }

        /// <summary>A test build's switch for VR without a headset (src/SpaceEngineersVR.TestAids/); never set in a player's build.</summary>
        static partial void VRWithoutHeadset(ref bool on);

        /// <summary>
        /// Game thread, every frame (<see cref="Plugin.Update"/>): the first time the headset is found, later in the run than
        /// the renderer's patching, the play features go on, from the same thread and point in the frame a game plugin
        /// patches from in its Init, and the picture turns to VR's (<see cref="SwitchPicture"/>) unless a core one failed.
        /// </summary>
        internal static void PatchForHeadset()
        {
            if (playPatched || !rendererPatched || PatchRegistry.VROff || !HeadsetFound)
                return;
            PatchPlayOnce();
            if (!PatchRegistry.VROff)
                TurnOnPicture();
            PatchRegistry.Finish(null);
            if (!PatchRegistry.VROff)
                SwitchPicture("headset found");
        }

        private static void TurnOnPicture()
        {
            SetPicture(true);
            PatchRegistry.Apply(nameof(SoftwareCursor), SoftwareCursor.Apply);
        }

        /// <summary>
        /// The headset is in use: its session is shown (Visible, or Focused). Below that (Idle, Synchronized, Stopping), or
        /// without a session (lost, refused, never started), it is away. The runtime's dashboard leaves it Visible.
        /// </summary>
        private static bool HeadsetInUse
        {
            get
            {
                XrSessionState state = XrSession.State;
                bool inUse = XrSession.Exists && (state == XrSessionState.Visible || state == XrSessionState.Focused);
                if (!inUse)
                    VRWithoutHeadset(ref inUse);
                return inUse;
            }
        }

        /// <summary>
        /// Game thread, every frame (<see cref="Plugin.Update"/>), once the headset has been found: the picture goes back to
        /// the game's when the headset has been away for <see cref="AwaySeconds"/> (it died, sleeps, was taken off or
        /// unplugged, or its runtime closed), so the game goes on on the monitor with the keyboard and mouse; it turns to
        /// VR's again when the session has the focus, a new one after a loss included.
        /// </summary>
        internal static void FollowHeadset()
        {
            if (!playPatched || PatchRegistry.VROff || !FlatWhenAway)
                return;
            if (playingFlat)
            {
                if (XrSession.Focused)
                    ReturnToVR();
                return;
            }
            if (!vrPicture || HeadsetInUse)
            {
                awaySince = 0;
                return;
            }
            long now = Stopwatch.GetTimestamp();
            if (awaySince == 0)
                awaySince = now;
            else if (now - awaySince >= (long)(AwaySeconds * Stopwatch.Frequency))
                GoFlat();
        }

        /// <summary>
        /// The picture back to the game's own, as with no headset at startup: the player's own resolution and window mode,
        /// the GUI at the window's size (UpdateScreenSize; open screens rebuild their controls), the game's cursor, the
        /// screen resources at the window's size (<see cref="EyeRenderer.ToWindowResolution"/>). The session, if there is
        /// one, is kept, and the render thread keeps its frame loop going without layers until it has the focus again.
        /// </summary>
        private static void GoFlat()
        {
            awaySince = 0;
            playingFlat = true;
            XrSessionState state = XrSession.State;
            string why = XrSession.Exists ? $"session {state}" : "no session";
            MyRenderDeviceSettings? user = userVideo;
            Log.Info($"Headset away for {AwaySeconds:0} s ({why}): playing flat, " +
                     (user.HasValue ? $"the player's {user.Value.BackBufferWidth}x{user.Value.BackBufferHeight} {user.Value.WindowMode}" : "the window as it is") +
                     ", until the headset session has the focus again");
            SetPicture(false);
            if (user.HasValue)
            {
                try
                {
                    MyVideoSettingsManager.Apply(user.Value);
                }
                catch (Exception e)
                {
                    Log.Error(e, "Could not put the window back to the player's video settings; it stays the VR mirror");
                }
            }
            EyeRenderer.ToWindowResolution();
            MyViewport viewport = MySandboxGame.ScreenViewport;
            MySandboxGame.UpdateScreenSize((int)viewport.Width, (int)viewport.Height, viewport);
            Notify("Headset away: playing flat");
        }

        /// <summary>The headset session has the focus again: the picture turns to VR's the way it does when the headset is first found.</summary>
        private static void ReturnToVR()
        {
            playingFlat = false;
            SetPicture(true);
            SwitchPicture("headset back");
            Notify("Headset back: VR");
        }

        /// <summary>A short notification on the HUD, in a world; none in the menus.</summary>
        private static void Notify(string text)
        {
            try
            {
                MyHudNotifications notifications = MyHud.Notifications;
                if (notifications == null || MySession.Static == null)
                    return;
                notifications.Add(new MyHudNotificationDebug(text, NotifyMs, level: MyNotificationLevel.Important));
            }
            catch (Exception e)
            {
                Log.Error(e, $"The notification \"{text}\" could not be shown");
            }
        }

        private const int NotifyMs = 3000;

        /// <summary>
        /// The picture turning to VR's while the game runs, as a change of resolution does: the device settings go to the
        /// mirror window's (and <see cref="KeepVRDeviceSettings"/> keeps them from then on), the screen resources are made
        /// again at the eye size (the headset session starts in that), and the GUI is laid out again at
        /// <see cref="GuiLayout.Size"/>. The game's UpdateScreenSize rebuilds the open screens' controls itself when the GUI's
        /// size changes (MyScreenManager.RecreateControls), as on a change of resolution.
        /// </summary>
        private static void SwitchPicture(string why)
        {
            Log.Info($"The picture turns to VR's ({why}): the mirror window, the screen resources at the eye size, the GUI at {GuiLayout.Size.X}x{GuiLayout.Size.Y}");
            try
            {
                MyRenderDeviceSettings settings = MyVideoSettingsManager.CurrentDeviceSettings;
                ForceVRDeviceSettings(ref settings);
                MyVideoSettingsManager.Apply(settings);
            }
            catch (Exception e)
            {
                Log.Error(e, "Could not switch the window to the VR mirror; it keeps the player's settings");
            }
            DynamicResolution.Remake(why);
            MySandboxGame.UpdateScreenSize(MySandboxGame.ScreenSize.X, MySandboxGame.ScreenSize.Y, MySandboxGame.ScreenViewport);
        }

        private static void PatchPlayOnce()
        {
            playPatched = true;
            if (HeadsetWatcher.HasHeadset)
                Log.Info("Headset found: patching what changes play");
            PatchPlay();
        }

        /// <summary>
        /// The game builds its device settings here, on the main thread, just before it starts the render thread
        /// and creates the device. The mirror window size rides along into device creation.
        /// </summary>
        private static void UseVRDeviceSettings(ref MyRenderDeviceSettings? __result)
        {
            if (PatchRegistry.VROff)
                return;
            try
            {
                PatchRenderer();
                rendererPatched = true;
                if (HeadsetFound)
                {
                    TurnOnPicture();
                    PatchPlayOnce();
                }
                else
                {
                    Log.Info("No headset yet: the game's own picture until one is found, and what changes play is patched then");
                }
                // Our own patch is running in MyVideoSettingsManager.Initialize: the registry leaves that one for the first frame.
                PatchRegistry.Finish(AccessTools.Method(typeof(MyVideoSettingsManager), nameof(MyVideoSettingsManager.Initialize)));
            }
            catch (Exception e)
            {
                Log.Error(e, "Patching the renderer failed; continuing without VR");
                return;
            }
            if (PatchRegistry.VROff || !vrPicture)
                return;

            if (!__result.HasValue)
            {
                Log.Warn("Game has no saved video settings yet; stereo starts from the next launch");
                return;
            }

            MyRenderDeviceSettings settings = __result.Value;
            ForceVRDeviceSettings(ref settings);
            __result = settings;
            Log.Info($"VR mode: mirror window {settings.BackBufferWidth}x{settings.BackBufferHeight}, adapter {settings.AdapterOrdinal} " +
                     $"(user setting {userVideo.Value.BackBufferWidth}x{userVideo.Value.BackBufferHeight} {userVideo.Value.WindowMode} VSync {userVideo.Value.VSync} kept in config)");
        }

        private static MyRenderDeviceSettings? userVideo;

        /// <summary>
        /// Every later change of device settings (options screen, the game re-applying the config after start-up)
        /// goes through Apply. Without this the game switches back to the player's fullscreen mode and drops stereo. While
        /// the picture is the game's (<see cref="PlayingFlat"/>) they are the player's own.
        /// </summary>
        private static void KeepVRDeviceSettings(ref MyRenderDeviceSettings settings)
        {
            if (!userVideo.HasValue || !vrPicture)
                return;

            MyRenderDeviceSettings requested = settings;
            ForceVRDeviceSettings(ref settings);
            if (!requested.Equals(ref settings))
                Log.Info($"Device settings change kept in VR mode (requested {requested.BackBufferWidth}x{requested.BackBufferHeight} {requested.WindowMode}, " +
                         $"stereo {requested.UseStereoRendering})");
        }

        /// <summary>In VR the desktop window is only a mirror: a modest window on one monitor, stereo on.</summary>
        private static void ForceVRDeviceSettings(ref MyRenderDeviceSettings settings)
        {
            // What the player asked for is what goes back into their config.
            if (settings.WindowMode != MyWindowModeEnum.Window || settings.BackBufferWidth != VRSettings.MirrorWidth || settings.BackBufferHeight != VRSettings.MirrorHeight)
                userVideo = settings;
            else if (!userVideo.HasValue)
                userVideo = settings;
            else if (settings.VSync != (XrRuntime.HasSystem ? 0 : userVideo.Value.VSync))
            {
                // The mirror window re-applied with a VSync the plugin did not set: the player changed it in the options.
                MyRenderDeviceSettings user = userVideo.Value;
                user.VSync = settings.VSync;
                userVideo = user;
            }

            // Keen's side-by-side stereo mode stays off: half its passes are mono, and EyeRenderer draws each eye whole.
            settings.UseStereoRendering = false;
            settings.WindowMode = MyWindowModeEnum.Window;
            settings.BackBufferWidth = VRSettings.MirrorWidth;
            settings.BackBufferHeight = VRSettings.MirrorHeight;
            // With a headset its runtime paces the frames (xrWaitFrame); waiting for the monitor as well would fight it.
            playerVSync = userVideo.Value.VSync;
            if (XrRuntime.HasSystem)
                settings.VSync = 0;
        }

        /// <summary>The VSync the player's settings ask for, kept for the time without a headset; -1 before the game has settings.</summary>
        private static volatile int playerVSync = -1;

        private static System.Reflection.FieldInfo deviceSettings;

        /// <summary>
        /// Render thread, when a session starts or ends during the run: no VSync while the headset runtime paces the frames,
        /// the player's own without it. The renderer reads it from its device settings at every Present (MyRender11.Present).
        /// </summary>
        internal static void HeadsetPacing(bool headset)
        {
            int vsync = headset ? 0 : playerVSync;
            if (vsync < 0)
                return;
            try
            {
                if (deviceSettings == null)
                    deviceSettings = AccessTools.Field(typeof(MyDX11Render).Assembly.GetType("VRageRender.MyRender11", throwOnError: true), "m_settings")
                                     ?? throw new MissingFieldException("MyRender11", "m_settings");
                var settings = (MyRenderDeviceSettings)deviceSettings.GetValue(null);
                if (settings.VSync == vsync)
                    return;
                settings.VSync = vsync;
                deviceSettings.SetValue(null, settings);
                Log.Info($"VSync {(vsync == 0 ? "off: the headset paces the frames" : $"back to the player's setting ({vsync})")}");
            }
            catch (Exception e)
            {
                Log.Error(e, "Could not change VSync for the headset");
            }
        }

        /// <summary>
        /// The game writes its live device settings back to the config whenever it saves video options. Put the
        /// player's own resolution, window mode and VSync back so the VR mirror window and the headset's VSync off never
        /// replace them, and nothing of the plugin's stays in SpaceEngineers.cfg after it is removed. While the picture is the
        /// game's (<see cref="PlayingFlat"/>) the live settings are the player's, and stay as the game wrote them.
        /// </summary>
        private static void KeepUserVideoConfig()
        {
            if (!userVideo.HasValue || !vrPicture)
                return;

            MyRenderDeviceSettings user = userVideo.Value;
            MyConfig config = MySandboxGame.Config;
            config.ScreenWidth = user.BackBufferWidth;
            config.ScreenHeight = user.BackBufferHeight;
            config.WindowMode = user.WindowMode;
            config.VerticalSync = user.VSync;
        }
    }
}
