using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using HarmonyLib;
using SpaceEngineersVR.Gui;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// Debug aid: switches the renderer's own passes on and off (MyRender11.DebugOverrides), to find which pass
    /// differs between the eyes. Controlled by %APPDATA%\SpaceEngineers\SpaceEngineersVR.renderdebug, re-read about
    /// once a second, e.g. "SSAO=0 Shadows=0"; names are MyRenderDebugOverrides fields, or one of <see cref="Paths"/>.
    /// RightFirst=1 draws the right eye first; EyeOffset=0 turns off <see cref="EyeOffset"/>,
    /// SharedBillboards=1 lets the eyes share <see cref="OnceBillboards"/>, HudCamera=0 turns off <see cref="HudCamera"/>,
    /// PanelCursor=0 turns off <see cref="PanelCursor"/>, WorldMenu=0 makes menus head-locked like the HUD (<see cref="WorldMenu"/>),
    /// FullView=0 turns off <see cref="FullView"/>, VRInput=0 stops the hands reaching the game (<see cref="VRControls"/>),
    /// Laser=0 turns off the laser pointer, back to the plain mouse (<see cref="Laser"/>),
    /// HandIK=0 stops the arms following the controllers (<see cref="HandIK"/>; 2 no finger curl, 3 elbow from the body's hint
    /// only, 4 the game's own solver; HandIKPitch, HandIKRoll, HandIKYaw turn the hands round the grip, degrees; HandIKX/Y/Z
    /// shift them, mm; HandIKStretch=25 the arm's stretch limit, %; HandIKShrug=20 the collarbone's reach, degrees;
    /// HandIKTwist=100 the forearm's share of the twist, %; HandIKSwing=6 the elbow's swing per frame, degrees;
    /// HandIKFrame=0 places the hands, the hand tools and the fingertips by the previous draw's camera, as before; HandIKLead=0
    /// reads the controllers at the display time of the headset frame they are read in, not predicted for the frame drawn from them).
    /// HandResidual=1 logs once a second how far the drawn palms are from the controllers; HandGhost=1 draws each controller's
    /// grip pose as a small axis triad, with a dot where the palm belongs (<see cref="Tracking.HandResidual"/>); HandGhost=2 also draws
    /// the handle as an orange bar and a cyan dot where the finger press takes the index fingertip to be (FingerReach=&lt;mm&gt; tries another reach).
    /// Vignette=0 turns off the comfort vignette (<see cref="Vignette"/>), VignetteTest=1 draws it at full strength without any motion.
    /// Haptics=0 turns off the controllers' buzzing (<see cref="Hands.Haptics"/>).
    /// ToolbarWheel=0 turns off the hands' radial wheel, =1 (the default) is the toolbar's slots, =2 the game's system radial only (what the wheel was
    /// before the slots), =3 the block palette (<see cref="ToolbarWheel"/>).
    /// VRKeyboard=0 turns off the on-screen keyboard (<see cref="VRKeyboardHook"/>).
    /// HandTools=0 turns off the hand as the tool, aim and build ray, back to the head's (<see cref="Hands.HandTools"/>).
    /// ExternalView=1 forces the chase-camera screen on, =0 off, whatever the setting says (<see cref="ExternalView"/>).
    /// Laser=0 turns off the laser pointer, back to the plain mouse (<see cref="Laser"/>).
    /// ChaseCamera=0 gives the game's own third-person ship camera (<see cref="Tracking.ChaseCamera"/>).
    /// WristPanel=0 turns off the wrist button and the wrist panel (<see cref="WristButton"/>).
    /// WristLook=0 stops looking at the astronaut's gauntlet from opening the wrist panel; Y still does (<see cref="WristLook"/>).
    /// MainMenuVR=0 gives the main menu back as a flat panel stretched over the window (<see cref="MainMenu"/>).
    /// SoftwareCursor=0 stops the game drawing its own cursor into the panel; only the OS cursor is left, which is not in the panel (<see cref="SoftwareCursor"/>).
    /// FingerPress=0 turns off pressing buttons, doors and terminals by touching them with a fingertip (<see cref="Hands.FingerPress"/>).
    /// FingerPress=0 turns off pressing buttons by touching them with a fingertip (<see cref="Hands.FingerPress"/>);
    /// FingerPressAll=1 lifts its allow-list so doors, terminals and the rest can be touched too (for testing).
    /// GrabUse=0 turns off using seats, doors, medical rooms, cargo and terminals by closing the grip on them (<see cref="Hands.GrabUse"/>); the right grip is then Use along the ray only.
    /// Holsters=0 turns off drawing tools from the body and the helmet and lamp gestures (<see cref="Hands.Holsters"/>).
    /// TwoHand=0 turns off holding the tool in both hands; the other grip opens the toolbar wheel as before (<see cref="Hands.TwoHand"/>).
    /// InputLog=0 stops the log line for each headset button press and release, and the menu focus lines beside them (<see cref="InputLog"/>; on by default, at most 10 lines a second).
    /// VRPrompts=0 gives the game's prompts their keyboard names back (<see cref="VRPrompts"/>); VRRadial=0 gives back the game's gamepad radial menus, labels and entries (<see cref="Gui.VRRadialMenus"/>); ActionsWheel=0 has the left stick click on the tools wheel open the game's radial screen instead of the wheel at the hand (<see cref="Gui.ActionsWheel"/>); HudToolbar=1 shows the HUD's bottom toolbar on foot too (<see cref="HudToolbar"/>).
    /// BodyClearance=0 lets the eye go into the character's own body (<see cref="Tracking.BodyGuard"/>), CrouchWithHead=0 stops the head's crouch crouching
    /// the character, SeatClamp=0 lets the head leave the seated character's head by any distance, back into the body too (<see cref="Tracking.CockpitHead"/>),
    /// HeadLock=0 lets the head on foot go any distance from the character's head (<see cref="Tracking.BodyGuard"/>).
    /// SuitLightEye=0 gives the game's own placement of the suit's lamp back (15 cm above the head bone, aimed along the body), in place of the lamp at the
    /// eye, a few cm above it and ahead, aimed where the head looks (<see cref="SuitLight"/>); SuitLightEye=2 also logs once a second whether the lamp was placed in the camera's frame, and charges an empty suit battery once so the lamp has power.
    /// RecentreAll=0 makes a recentre only turn, as before, leaving the eye where it is (<see cref="Tracking.GameHead.Recentre"/>).
    /// SeatedPlay=1 is seated play and SeatedPlay=0 standing play, whatever the Play position setting says (<see cref="VRSettings.SeatedPlay"/>).
    /// MenuHoldRecentre=0 makes the left Menu button what it was before the hold: the game menu or Back from the press, and no recentre (<see cref="Input.VRControls.MenuHoldOff"/>).
    /// SharedShadows=1 fits one set of sun shadow cascades for both eyes (<see cref="SharedShadows"/>; off until checked in game); DesktopIpd=0 (mm) sets how far
    /// apart the desktop eyes are, in place of the IPD setting (0 draws both from one point, so the two halves of the window should match);
    /// ShadowFreeze=1 is the renderer's own frozen shadow camera (the cascades stop following the view), UsageSkip=0 stops it skipping
    /// cascades few pixels use.
    /// Drive=0 stops the test command file being read and run (<see cref="Input.Drive"/>).
    /// FocusMute=1 gives the game's own mute when its window is in the background back, even in the headset (<see cref="SoundFocus"/>; by default the sound stays on in VR).
    /// Removing a name restores it.
    /// </summary>
    internal static class RenderDebug
    {
        private static readonly string ControlFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceEngineers", "SpaceEngineersVR.renderdebug");

        private static readonly Type Render11 = typeof(MyDX11Render).Assembly.GetType("VRageRender.MyRender11", throwOnError: true);
        private static readonly PropertyInfo Overrides = AccessTools.Property(Render11, "DebugOverrides");

        /// <summary>Other switches, as a static field path (struct fields are written back).</summary>
        private static readonly Dictionary<string, (Type Type, string[] Path)> Paths = new Dictionary<string, (Type, string[])>(StringComparer.OrdinalIgnoreCase)
        {
            ["CascadesEveryFrame"] = (typeof(MyDX11Render).Assembly.GetType("VRageRender.MyShadowCascades", throwOnError: true),
                new[] { "Settings", "Data", "UpdateCascadesEveryFrame" }),
            ["UsageSkip"] = (Render11, new[] { "Settings", "ShadowCascadeUsageBasedSkip" }),
            ["ShadowFreeze"] = (Render11, new[] { "Settings", "ShadowCameraFrozen" }),
            ["EyeAdaptation"] = (Render11, new[] { "Postprocess", "EnableEyeAdaptation" }),
        };

        private static readonly Dictionary<FieldInfo, bool> original = new Dictionary<FieldInfo, bool>();
        private static readonly Dictionary<string, bool> originalPaths = new Dictionary<string, bool>();
        private static string lastText;

        /// <summary>Draw the right eye first, to tell state left over between the eyes from the eyes themselves.</summary>
        public static bool RightFirst { get; private set; }

        /// <summary>The desktop eyes' separation, metres, in place of the IPD setting; null for the setting.</summary>
        public static float? DesktopIpd { get; private set; }

        /// <param name="frame">Frame counter; the control file is read about once a second.</param>
        public static void Update(int frame)
        {
            if (frame % 60 != 1)
                return;
            string text = null;
            try
            {
                if (File.Exists(ControlFile))
                    text = File.ReadAllText(ControlFile).Trim();
            }
            catch (IOException)
            {
                return;
            }
            if (text == lastText)
                return;
            lastText = text;

            object overrides = Overrides.GetValue(null);
            foreach (KeyValuePair<FieldInfo, bool> restore in original)
                restore.Key.SetValue(overrides, restore.Value);
            original.Clear();
            foreach (KeyValuePair<string, bool> restore in originalPaths)
                SetPath(Paths[restore.Key].Type, null, Paths[restore.Key].Path, 0, restore.Value);
            originalPaths.Clear();
            RightFirst = false;
            DesktopIpd = null;
            SharedShadows.Off = true;
            EyeOffset.Off = false;
            OnceBillboards.Shared = false;
            HudCamera.Off = false;
            PanelCursor.Off = false;
            WorldMenu.Off = false;
            FullView.Off = false;
            VRControls.Off = false;
            Laser.Off = false;
            Drive.Off = false;
            SoundFocus.GameMute = false;
            HandIK.ResetDebug();
            Tracking.HandResidual.On = false;
            Tracking.HandResidual.Ghost = false;
            Tracking.HandResidual.Handle = false;
            Vignette.Off = false;
            Vignette.Test = false;
            Hands.Haptics.Off = false;
            ToolbarWheel.Mode = ToolbarWheel.ModeSlots;
            VRKeyboardHook.Off = false;
            Hands.HandTools.Off = false;
            ExternalView.Debug = null;
            Tracking.ChaseCamera.Off = false;
            WristButton.Off = false;
            WristLook.Off = false;
            MainMenu.Off = false;
            SoftwareCursor.Off = false;
            Hands.FingerPress.Off = false;
            Hands.FingerPress.All = false;
            Hands.FingerPress.ReachDebug = 0.0;
            Hands.GrabUse.Off = false;
            Hands.Holsters.Off = false;
            Hands.TwoHand.Off = false;
            Hands.ItemGrab.Off = false;
            Hands.ItemGrab.Trace = false;
            Hands.HullGrab.HullOff = false;
            Hands.HullGrab.LadderOff = false;
            InputLog.Off = false;
            VRPrompts.Off = false;
            Gui.VRRadialMenus.Off = false;
            Gui.ActionsWheel.Off = false;
            HudToolbar.ForceShown = false;
            Tracking.BodyGuard.Off = false;
            Tracking.BodyGuard.CrouchOff = false;
            Tracking.BodyGuard.SeatClampOff = false;
            Tracking.BodyGuard.LockOff = false;
            SuitLight.Off = false;
            SuitLight.Diag = false;
            Tracking.GameHead.RecentreTurnOnly = false;
            VRSettings.SeatedPlayOverride = null;
            Input.VRControls.MenuHoldOff = false;

            var applied = new List<string>();
            foreach (string part in (text ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] pair = part.Split('=');
                if (pair.Length == 2 && int.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int setting))
                {
                    if (pair[0].Equals("RightFirst", StringComparison.OrdinalIgnoreCase))
                    {
                        RightFirst = setting != 0;
                        applied.Add($"RightFirst={RightFirst}");
                        continue;
                    }
                    if (pair[0].Equals("BodyClearance", StringComparison.OrdinalIgnoreCase))
                    {
                        Tracking.BodyGuard.Off = setting == 0;
                        applied.Add($"BodyClearance={!Tracking.BodyGuard.Off}");
                        continue;
                    }
                    if (pair[0].Equals("CrouchWithHead", StringComparison.OrdinalIgnoreCase))
                    {
                        Tracking.BodyGuard.CrouchOff = setting == 0;
                        applied.Add($"CrouchWithHead={!Tracking.BodyGuard.CrouchOff}");
                        continue;
                    }
                    if (pair[0].Equals("HeadLock", StringComparison.OrdinalIgnoreCase))
                    {
                        Tracking.BodyGuard.LockOff = setting == 0;
                        applied.Add($"HeadLock={!Tracking.BodyGuard.LockOff}");
                        continue;
                    }
                    if (pair[0].Equals("SuitLightEye", StringComparison.OrdinalIgnoreCase))
                    {
                        SuitLight.Off = setting == 0;
                        SuitLight.Diag = setting >= 2;
                        applied.Add($"SuitLightEye={(setting == 0 ? "game's placement" : setting >= 2 ? "at the eye, with the frame log" : "at the eye")}");
                        continue;
                    }
                    if (pair[0].Equals("SeatClamp", StringComparison.OrdinalIgnoreCase))
                    {
                        Tracking.BodyGuard.SeatClampOff = setting == 0;
                        applied.Add($"SeatClamp={!Tracking.BodyGuard.SeatClampOff}");
                        continue;
                    }
                    if (pair[0].Equals("RecentreAll", StringComparison.OrdinalIgnoreCase))
                    {
                        Tracking.GameHead.RecentreTurnOnly = setting == 0;
                        applied.Add($"RecentreAll={!Tracking.GameHead.RecentreTurnOnly}");
                        continue;
                    }
                    if (pair[0].Equals("SeatedPlay", StringComparison.OrdinalIgnoreCase))
                    {
                        VRSettings.SeatedPlayOverride = setting != 0;
                        applied.Add($"SeatedPlay={VRSettings.SeatedPlayOverride}");
                        continue;
                    }
                    if (pair[0].Equals("MenuHoldRecentre", StringComparison.OrdinalIgnoreCase))
                    {
                        Input.VRControls.MenuHoldOff = setting == 0;
                        applied.Add($"MenuHoldRecentre={!Input.VRControls.MenuHoldOff}");
                        continue;
                    }
                    if (pair[0].Equals("SharedBillboards", StringComparison.OrdinalIgnoreCase))
                    {
                        OnceBillboards.Shared = setting != 0;
                        applied.Add($"SharedBillboards={OnceBillboards.Shared}");
                        continue;
                    }
                    if (pair[0].Equals("HudCamera", StringComparison.OrdinalIgnoreCase))
                    {
                        HudCamera.Off = setting == 0;
                        applied.Add($"HudCamera={!HudCamera.Off}");
                        continue;
                    }
                    if (pair[0].Equals("FullView", StringComparison.OrdinalIgnoreCase))
                    {
                        FullView.Off = setting == 0;
                        applied.Add($"FullView={!FullView.Off}");
                        continue;
                    }
                    if (pair[0].Equals("VRInput", StringComparison.OrdinalIgnoreCase))
                    {
                        VRControls.Off = setting == 0;
                        applied.Add($"VRInput={!VRControls.Off}");
                        continue;
                    }
                    if (pair[0].Equals("Drive", StringComparison.OrdinalIgnoreCase))
                    {
                        Drive.Off = setting == 0;
                        applied.Add($"Drive={!Drive.Off}");
                        continue;
                    }
                    if (pair[0].Equals("FocusMute", StringComparison.OrdinalIgnoreCase))
                    {
                        SoundFocus.GameMute = setting != 0;
                        applied.Add($"FocusMute={SoundFocus.GameMute}");
                        continue;
                    }
                    if (pair[0].Equals("Laser", StringComparison.OrdinalIgnoreCase))
                    {
                        Laser.Off = setting == 0;
                        applied.Add($"Laser={!Laser.Off}");
                        continue;
                    }
                    if (pair[0].Equals("HandResidual", StringComparison.OrdinalIgnoreCase))
                    {
                        Tracking.HandResidual.On = setting != 0;
                        applied.Add($"HandResidual={Tracking.HandResidual.On}");
                        continue;
                    }
                    if (pair[0].Equals("HandGhost", StringComparison.OrdinalIgnoreCase))
                    {
                        Tracking.HandResidual.Ghost = setting != 0;
                        Tracking.HandResidual.Handle = setting >= 2;
                        applied.Add($"HandGhost={setting}");
                        continue;
                    }
                    if (pair[0].StartsWith("HandIK", StringComparison.OrdinalIgnoreCase) && HandIK.SetDebug(pair[0], setting))
                    {
                        applied.Add($"{pair[0]}={setting}");
                        continue;
                    }
                    if (pair[0].Equals("Vignette", StringComparison.OrdinalIgnoreCase))
                    {
                        Vignette.Off = setting == 0;
                        applied.Add($"Vignette={!Vignette.Off}");
                        continue;
                    }
                    if (pair[0].Equals("VignetteTest", StringComparison.OrdinalIgnoreCase))
                    {
                        Vignette.Test = setting != 0;
                        applied.Add($"VignetteTest={Vignette.Test}");
                        continue;
                    }
                    if (pair[0].Equals("Haptics", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.Haptics.Off = setting == 0;
                        applied.Add($"Haptics={!Hands.Haptics.Off}");
                        continue;
                    }
                    if (pair[0].Equals("ToolbarWheel", StringComparison.OrdinalIgnoreCase))
                    {
                        ToolbarWheel.Mode = setting == 0 ? ToolbarWheel.ModeOff : setting == 2 ? ToolbarWheel.ModeSystem : setting == 3 ? ToolbarWheel.ModePalette : ToolbarWheel.ModeSlots;
                        applied.Add($"ToolbarWheel={(setting == 0 ? "off" : setting == 2 ? "system radial" : setting == 3 ? "block palette" : "toolbar slots")}");
                        continue;
                    }
                    if (pair[0].Equals("VRKeyboard", StringComparison.OrdinalIgnoreCase))
                    {
                        VRKeyboardHook.Off = setting == 0;
                        applied.Add($"VRKeyboard={!VRKeyboardHook.Off}");
                        continue;
                    }
                    if (pair[0].Equals("HandTools", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.HandTools.Off = setting == 0;
                        applied.Add($"HandTools={!Hands.HandTools.Off}");
                        continue;
                    }
                    if (pair[0].Equals("ExternalView", StringComparison.OrdinalIgnoreCase))
                    {
                        ExternalView.Debug = setting != 0;
                        applied.Add($"ExternalView={ExternalView.Debug}");
                        continue;
                    }
                    if (pair[0].Equals("ChaseCamera", StringComparison.OrdinalIgnoreCase))
                    {
                        Tracking.ChaseCamera.Off = setting == 0;
                        applied.Add($"ChaseCamera={!Tracking.ChaseCamera.Off}");
                        continue;
                    }
                    if (pair[0].Equals("WristPanel", StringComparison.OrdinalIgnoreCase))
                    {
                        WristButton.Off = setting == 0;
                        applied.Add($"WristPanel={!WristButton.Off}");
                        continue;
                    }
                    if (pair[0].Equals("WristLook", StringComparison.OrdinalIgnoreCase))
                    {
                        WristLook.Off = setting == 0;
                        applied.Add($"WristLook={!WristLook.Off}");
                        continue;
                    }
                    if (pair[0].Equals("MainMenuVR", StringComparison.OrdinalIgnoreCase))
                    {
                        MainMenu.Off = setting == 0;
                        applied.Add($"MainMenuVR={!MainMenu.Off}");
                        continue;
                    }
                    if (pair[0].Equals("SoftwareCursor", StringComparison.OrdinalIgnoreCase))
                    {
                        SoftwareCursor.Off = setting == 0;
                        applied.Add($"SoftwareCursor={!SoftwareCursor.Off}");
                        continue;
                    }
                    if (pair[0].Equals("FingerPress", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.FingerPress.Off = setting == 0;
                        applied.Add($"FingerPress={!Hands.FingerPress.Off}");
                        continue;
                    }
                    if (pair[0].Equals("GrabUse", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.GrabUse.Off = setting == 0;
                        applied.Add($"GrabUse={!Hands.GrabUse.Off}");
                        continue;
                    }
                    if (pair[0].Equals("Holsters", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.Holsters.Off = setting == 0;
                        applied.Add($"Holsters={!Hands.Holsters.Off}");
                        continue;
                    }
                    if (pair[0].Equals("TwoHand", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.TwoHand.Off = setting == 0;
                        applied.Add($"TwoHand={!Hands.TwoHand.Off}");
                        continue;
                    }
                    if (pair[0].Equals("GrabItems", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.ItemGrab.Off = setting == 0;
                        applied.Add($"GrabItems={!Hands.ItemGrab.Off}");
                        continue;
                    }
                    if (pair[0].Equals("GrabItemsTrace", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.ItemGrab.Trace = setting != 0;
                        applied.Add($"GrabItemsTrace={Hands.ItemGrab.Trace}");
                        continue;
                    }
                    if (pair[0].Equals("HullGrab", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.HullGrab.HullOff = setting == 0;
                        applied.Add($"HullGrab={!Hands.HullGrab.HullOff}");
                        continue;
                    }
                    if (pair[0].Equals("LadderGrab", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.HullGrab.LadderOff = setting == 0;
                        applied.Add($"LadderGrab={!Hands.HullGrab.LadderOff}");
                        continue;
                    }
                    if (pair[0].Equals("VRPrompts", StringComparison.OrdinalIgnoreCase))
                    {
                        VRPrompts.Off = setting == 0;
                        applied.Add($"VRPrompts={!VRPrompts.Off}");
                        continue;
                    }
                    if (pair[0].Equals("VRRadial", StringComparison.OrdinalIgnoreCase))
                    {
                        Gui.VRRadialMenus.Off = setting == 0;
                        applied.Add($"VRRadial={!Gui.VRRadialMenus.Off}");
                        continue;
                    }
                    if (pair[0].Equals("ActionsWheel", StringComparison.OrdinalIgnoreCase))
                    {
                        Gui.ActionsWheel.Off = setting == 0;
                        applied.Add($"ActionsWheel={!Gui.ActionsWheel.Off}");
                        continue;
                    }
                    if (pair[0].Equals("HudToolbar", StringComparison.OrdinalIgnoreCase))
                    {
                        HudToolbar.ForceShown = setting != 0;
                        applied.Add($"HudToolbar={(HudToolbar.ForceShown ? 1 : 0)}");
                        continue;
                    }
                    if (pair[0].Equals("InputLog", StringComparison.OrdinalIgnoreCase))
                    {
                        InputLog.Off = setting == 0;
                        applied.Add($"InputLog={!InputLog.Off}");
                        continue;
                    }
                    if (pair[0].Equals("FingerReach", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.FingerPress.ReachDebug = setting / 1000.0;
                        applied.Add($"FingerReach={setting} mm");
                        continue;
                    }
                    if (pair[0].Equals("FingerPressAll", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.FingerPress.All = setting != 0;
                        applied.Add($"FingerPressAll={Hands.FingerPress.All}");
                        continue;
                    }
                    if (pair[0].Equals("PanelCursor", StringComparison.OrdinalIgnoreCase))
                    {
                        PanelCursor.Off = setting == 0;
                        applied.Add($"PanelCursor={!PanelCursor.Off}");
                        continue;
                    }
                    if (pair[0].Equals("WorldMenu", StringComparison.OrdinalIgnoreCase))
                    {
                        WorldMenu.Off = setting == 0;
                        applied.Add($"WorldMenu={!WorldMenu.Off}");
                        continue;
                    }
                    if (pair[0].Equals("SharedShadows", StringComparison.OrdinalIgnoreCase))
                    {
                        SharedShadows.Off = setting == 0;
                        applied.Add($"SharedShadows={!SharedShadows.Off}");
                        continue;
                    }
                    if (pair[0].Equals("DesktopIpd", StringComparison.OrdinalIgnoreCase))
                    {
                        DesktopIpd = Math.Max(0, setting) / 1000f;
                        applied.Add($"DesktopIpd={Math.Max(0, setting)} mm");
                        continue;
                    }
                    if (pair[0].Equals("EyeOffset", StringComparison.OrdinalIgnoreCase))
                    {
                        EyeOffset.Off = setting == 0;
                        applied.Add($"EyeOffset={!EyeOffset.Off}");
                        continue;
                    }
                    if (Paths.TryGetValue(pair[0], out var path))
                    {
                        if (!originalPaths.ContainsKey(pair[0]))
                            originalPaths[pair[0]] = (bool)GetPath(path.Type, null, path.Path, 0);
                        SetPath(path.Type, null, path.Path, 0, setting != 0);
                        applied.Add($"{pair[0]}={setting != 0}");
                        continue;
                    }
                }
                FieldInfo field = pair.Length == 2 ? AccessTools.Field(overrides.GetType(), pair[0]) : null;
                if (field == null || field.FieldType != typeof(bool) || !int.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                {
                    Log.Warn($"Render debug: ignored '{part}'");
                    continue;
                }
                original[field] = (bool)field.GetValue(overrides);
                field.SetValue(overrides, value != 0);
                applied.Add($"{field.Name}={value != 0}");
            }
            Log.Info(applied.Count > 0 ? "Render debug: " + string.Join(", ", applied) : "Render debug off");
        }

        private static object GetPath(Type type, object obj, string[] path, int i)
        {
            FieldInfo field = AccessTools.Field(type, path[i]);
            object value = field.GetValue(obj);
            return i == path.Length - 1 ? value : GetPath(field.FieldType, value, path, i + 1);
        }

        private static void SetPath(Type type, object obj, string[] path, int i, object value)
        {
            FieldInfo field = AccessTools.Field(type, path[i]);
            if (i == path.Length - 1)
            {
                field.SetValue(obj, value);
                return;
            }
            object child = field.GetValue(obj); // a struct comes back boxed: change the box, then write it back
            SetPath(field.FieldType, child, path, i + 1, value);
            if (field.FieldType.IsValueType)
                field.SetValue(obj, child);
        }
    }
}
