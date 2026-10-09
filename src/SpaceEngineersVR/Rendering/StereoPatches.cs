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
        private static Harmony harmony;

        /// <summary>
        /// Called before the game's Main. Only hooks the video settings: patching the renderer itself runs
        /// MyRender11's static constructor, which needs the game's file system, and that is not up yet.
        /// </summary>
        public static void Apply(Harmony harmony)
        {
            StereoPatches.harmony = harmony;
            harmony.Patch(AccessTools.Method(typeof(MyVideoSettingsManager), nameof(MyVideoSettingsManager.Initialize)),
                postfix: new HarmonyMethod(typeof(StereoPatches), nameof(UseVRDeviceSettings)));
            harmony.Patch(AccessTools.Method(typeof(MyVideoSettingsManager), nameof(MyVideoSettingsManager.Apply), new[] { typeof(MyRenderDeviceSettings) }),
                prefix: new HarmonyMethod(typeof(StereoPatches), nameof(KeepVRDeviceSettings)));
            harmony.Patch(AccessTools.Method(typeof(MyVideoSettingsManager), nameof(MyVideoSettingsManager.WriteCurrentSettingsToConfig)),
                postfix: new HarmonyMethod(typeof(StereoPatches), nameof(KeepUserVideoConfig)));
        }

        /// <summary>Second phase: the file system is up and the render device does not exist yet.</summary>
        private static void PatchRenderer()
        {
            HeadsetGpu.Patch(harmony);
            EyeRenderer.Patch(harmony);
            EyeOffset.Patch(harmony);
            SharedShadows.Patch(harmony);
            SoundFocus.Patch(harmony);
            ExternalView.Patch(harmony);
            GuiLayout.Patch(harmony);
            HudCamera.Patch(harmony);
            HudToolbar.Patch(harmony);
            WorldMenu.Patch(harmony);
            MainMenu.Patch(harmony);
            PanelCursor.Patch(harmony);
            SoftwareCursor.Apply();
            FullView.Patch(harmony);
            CharacterHead.Patch(harmony);
            CockpitHead.Patch(harmony);
            BodyFollow.Patch(harmony);
            BodyGuard.Patch(harmony);
            CameraHeadLink.Patch(harmony);
            SuitLight.Patch(harmony);
            ChaseCamera.Patch(harmony);
            ThirdPersonGate.Patch(harmony);
            InputPatches.Patch(harmony);
            Laser.Patch(harmony);
            HandIK.Patch(harmony);
            HandResidual.Patch(harmony);
            Hands.Haptics.Patch(harmony);
            Hands.HandTools.Patch(harmony);
            Hands.FingerPress.Patch(harmony);
            Hands.GrabUse.Patch(harmony);
            Hands.Holsters.Patch(harmony);
            Hands.ItemGrab.Patch(harmony);
            Hands.HullGrab.Patch(harmony);
            Hands.TwoHand.Patch(harmony);
            WristButton.Patch(harmony);
            VROptionsEntry.Patch(harmony);
            VRKeyboardHook.Patch(harmony);
            Input.VRPrompts.Patch(harmony);
            Gui.VRRadialMenus.Patch(harmony);
            Gui.ActionsWheel.Patch(harmony);
        }

        /// <summary>
        /// The game builds its device settings here, on the main thread, just before it starts the render thread
        /// and creates the device. The mirror window size rides along into device creation.
        /// </summary>
        private static void UseVRDeviceSettings(ref MyRenderDeviceSettings? __result)
        {
            try
            {
                PatchRenderer();
            }
            catch (Exception e)
            {
                Log.Error(e, "Patching the renderer failed; continuing without VR");
                return;
            }

            if (!__result.HasValue)
            {
                Log.Warn("Game has no saved video settings yet; stereo starts from the next launch");
                return;
            }

            MyRenderDeviceSettings settings = __result.Value;
            ForceVRDeviceSettings(ref settings);
            __result = settings;
            Log.Info($"VR mode: mirror window {settings.BackBufferWidth}x{settings.BackBufferHeight}, adapter {settings.AdapterOrdinal} " +
                     $"(user setting {userVideo.Value.BackBufferWidth}x{userVideo.Value.BackBufferHeight} {userVideo.Value.WindowMode} kept in config)");
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
        /// player's own resolution and window mode back so the VR mirror window never replaces them.
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
        }
    }
}
