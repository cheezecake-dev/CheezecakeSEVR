using System;
using HarmonyLib;
using Sandbox;
using Sandbox.Engine.Platform.VideoMode;
using Sandbox.Engine.Utils;
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
    internal static class StereoPatches
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
        private static void PatchRenderer()
        {
            Feature(nameof(HeadsetGpu), HeadsetGpu.Patch);
            Feature(nameof(PerfMeter), PerfMeter.Patch);
            Feature(nameof(EyeRenderer), EyeRenderer.Patch);
            Feature(nameof(EyeOffset), EyeOffset.Patch);
            Feature(nameof(SharedShadows), SharedShadows.Patch);
            Feature(nameof(SceneOnce), SceneOnce.Patch);
            Feature(nameof(ParticlesOnce), ParticlesOnce.Patch);
            Feature(nameof(CentredFlares), CentredFlares.Patch);
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
            PatchRegistry.Apply(nameof(SoftwareCursor), SoftwareCursor.Apply);
            Feature(nameof(FullView), FullView.Patch);
            Feature(nameof(BloodPulse), BloodPulse.Patch);
            Feature(nameof(CharacterHead), CharacterHead.Patch);
            Feature(nameof(CockpitHead), CockpitHead.Patch);
            Feature(nameof(BodyFollow), BodyFollow.Patch);
            Feature(nameof(ZeroGPivot), ZeroGPivot.Patch);
            Feature(nameof(MagBootsSmoothing), MagBootsSmoothing.Patch);
            Feature(nameof(BodyGuard), BodyGuard.Patch);
            Feature(nameof(BodyShift), BodyShift.Patch);
            Feature(nameof(CameraHeadLink), CameraHeadLink.Patch);
            Feature(nameof(CameraShake), CameraShake.Patch);
            Feature(nameof(ExitUpright), ExitUpright.Patch);
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
            Feature(nameof(Hands.QuickInventory), Hands.QuickInventory.Patch);
            Feature(nameof(Hands.HeadGestures), Hands.HeadGestures.Patch);
            PatchRegistry.Apply(nameof(Hands.VoiceTalk), Hands.VoiceTalk.Register);
            Feature(nameof(Hands.ItemGrab), Hands.ItemGrab.Patch);
            Feature(nameof(Hands.HullGrab), Hands.HullGrab.Patch);
            Feature(nameof(Hands.TwoHand), Hands.TwoHand.Patch);
            Feature(nameof(Hands.SurfaceStop), Hands.SurfaceStop.Patch);
            Feature(nameof(Hands.Kickback), Hands.Kickback.Patch);
            Feature(nameof(Hands.Reload), Hands.Reload.Patch);
            Feature(nameof(WristButton), WristButton.Patch);
            Feature(nameof(VROptionsEntry), VROptionsEntry.Patch);
            Feature(nameof(VRKeyboardHook), VRKeyboardHook.Patch);
            Feature(nameof(Input.VRPrompts), Input.VRPrompts.Patch);
            Feature(nameof(Gui.VRRadialMenus), Gui.VRRadialMenus.Patch);
            Feature(nameof(Gui.ActionsWheel), Gui.ActionsWheel.Patch);
        }

        private static void Feature(string name, Action<Harmony> patch) => PatchRegistry.Apply(name, () => patch(harmony));

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
                // Our own patch is running in MyVideoSettingsManager.Initialize: the registry leaves that one for the first frame.
                PatchRegistry.Finish(AccessTools.Method(typeof(MyVideoSettingsManager), nameof(MyVideoSettingsManager.Initialize)));
            }
            catch (Exception e)
            {
                Log.Error(e, "Patching the renderer failed; continuing without VR");
                return;
            }
            if (PatchRegistry.VROff)
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
        /// goes through Apply. Without this the game switches back to the player's fullscreen mode and drops stereo.
        /// </summary>
        private static void KeepVRDeviceSettings(ref MyRenderDeviceSettings settings)
        {
            if (!userVideo.HasValue)
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
            if (XrRuntime.HasSystem)
                settings.VSync = 0;
        }

        /// <summary>
        /// The game writes its live device settings back to the config whenever it saves video options. Put the
        /// player's own resolution, window mode and VSync back so the VR mirror window and the headset's VSync off never
        /// replace them, and nothing of the plugin's stays in SpaceEngineers.cfg after it is removed.
        /// </summary>
        private static void KeepUserVideoConfig()
        {
            if (!userVideo.HasValue)
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
