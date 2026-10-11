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
    /// HudVisor=0 puts the whole HUD back on the flat panel (<see cref="HudVisor"/>), HudVisorCylinder=0 draws its pieces on flat quads
    /// even when the runtime offers cylinder layers,
    /// PanelCursor=0 turns off <see cref="PanelCursor"/>, WorldMenu=0 makes menus head-locked like the HUD (<see cref="WorldMenu"/>),
    /// FullView=0 turns off <see cref="FullView"/>, VRInput=0 stops the hands reaching the game (<see cref="VRControls"/>),
    /// BloodPulse=0 gives the blood overlay the game's own flashes back (<see cref="BloodPulse"/>); BloodTest=N draws it as if the
    /// character were at N percent health (20 and below pulses), BloodPhase=N holds its beat N percent of the way through (0 the
    /// trough, 40 the peak), BloodHit=N adds a hit every N ms, BloodTrace=1 logs once a second the blood sprites the game sent
    /// and how many eye frames drew one, and what the overlay covers 2, 10, 20, 30 and 50% in from each eye's outer edge (<see cref="FullView"/>),
    /// Laser=0 turns off the laser pointer, back to the plain mouse (<see cref="Laser"/>),
    /// HandIK=0 stops the arms following the controllers (<see cref="HandIK"/>; 2 no finger curl, 3 elbow from the body's hint
    /// only, 4 the game's own solver; HandIKPitch, HandIKRoll, HandIKYaw turn the hands round the grip, degrees; HandIKX/Y/Z
    /// shift them, mm; HandIKStretch=25 the arm's stretch limit, %; HandIKShrug=20 the collarbone's reach, degrees;
    /// HandIKTwist=100 the forearm's share of the twist, %; HandIKSwing=6 the elbow's swing per frame, degrees;
    /// HandIKFrame=0 places the hands, the hand tools and the fingertips by the previous draw's camera, as before; HandIKLead=0
    /// reads the controllers at the display time of the headset frame they are read in, not predicted for the frame drawn from them;
    /// HandIKHeld=0 leaves the arm that holds a tool to the game's own arm IK, which does not stretch, as before).
    /// HandIKPin=0 lets a hand on a tool stop short past the arm's stretch, as before, instead of staying on the tool's grip; HandIKSway=0
    /// leaves the held arm's shoulder swaying as the animation has it; HandIKEase=1 logs the left arm's stretch every frame
    /// while it eases onto or off a tool's grip (<see cref="Tracking.HandIK"/>); HandIKCap=130 the most any arm is drawn stretched, % (0: no cap);
    /// HandIKJump=N logs every frame an arm's reach changes by more than N cm beyond what the hand did against the head, split into its causes,
    /// and the 20 frames after (<see cref="Tracking.ArmJump"/>).
    /// ControlGrip=0/1 turns "Hands on the controls to fly" off or on whatever the setting says; ControlGripLog=1 logs what the hands
    /// ask of a pilot seat and what reaches the game each time it changes (<see cref="Input.ControlGrip"/>); ControlGripTake=14 how
    /// close to the pose's palm a grip takes a stick or throttle, cm.
    /// HandResidual=1 logs once a second how far the drawn palms are from the controllers; HandGhost=1 draws each controller's
    /// grip pose as a small axis triad, with a dot where the palm belongs (<see cref="Tracking.HandResidual"/>); HandGhost=2 also draws
    /// the handle as an orange bar and a cyan dot where the finger press takes the index fingertip to be (FingerReach=&lt;mm&gt; tries another reach past the finger's last joint).
    /// ToolSteady=1 logs once a second, while a tool is held on foot, how far the tool, the controller it is placed from, the palm on
    /// it, the camera off the head and the body's push ahead moved in the view (<see cref="Tracking.ToolSteady"/>).
    /// Vignette=0 turns off the comfort vignette (<see cref="Vignette"/>), VignetteTest=1 draws it at full strength without any motion.
    /// Haptics=0 turns off the controllers' buzzing (<see cref="Hands.Haptics"/>).
    /// BuildMode=0 gives back the on-foot buttons with a block in hand (A jumps, B the jetpack, X the dampers, no turning by hand; <see cref="Hands.BuildMode"/>).
    /// ToolbarWheel=0 turns off the hands' radial wheel, =1 (the default) is the toolbar's slots, =2 the game's system radial only (what the wheel was
    /// before the slots), =3 the block palette (<see cref="ToolbarWheel"/>).
    /// VRKeyboard=0 turns off the on-screen keyboard (<see cref="VRKeyboardHook"/>).
    /// HandTools=0 turns off the hand as the tool, aim and build ray, back to the head's (<see cref="Hands.HandTools"/>).
    /// ExternalView=1 forces the chase-camera screen on, =0 off, whatever the setting says (<see cref="ExternalView"/>).
    /// Laser=0 turns off the laser pointer, back to the plain mouse (<see cref="Laser"/>).
    /// PerfMeter=0 turns off the frame-time meter, the "Perf" lines in the log every 10 s (<see cref="PerfMeter"/>).
    /// GpuPasses=1 puts the per-pass GPU timestamps back, for the Perf line's GPU time by pass (<see cref="PerfMeter.PassMarks"/>; off by default: with them the game ended in NVIDIA's driver).
    /// ChaseCamera=0 gives the game's own third-person ship camera (<see cref="Tracking.ChaseCamera"/>).
    /// WristPanel=0 turns off the wrist button and the wrist panel (<see cref="WristButton"/>).
    /// WristStatus=0 gives the HUD its status bars and suit icons back and takes the screen off the gauntlet (<see cref="WristStatus"/>).
    /// WristStatusBright=N scales the gauntlet screen's brightness to N percent (100 as built, and at most), to judge it in the headset.
    /// WristStatusAll=1 draws the gauntlet screen's parts that show only at times (radiation panel, medicine, cryo and bottle marks) all the time, to check their place.
    /// QuickMenuNative=0 gives the wrist panel its plain look back, a flat background and text-only buttons, in place of the game's own menu look (<see cref="QuickActionsScreen.Plain"/>; from the next opening).
    /// MainMenuVR=0 gives the main menu back as a flat panel stretched over the window (<see cref="MainMenu"/>).
    /// ZoomInset=0 takes the zoom inset away: a camera block's, turret's or searchlight's zoom changes nothing in the headset, as before (<see cref="ZoomInset"/>);
    /// ZoomInsetHalfRate=1 draws the inset's picture every other frame, to weigh its cost.
    /// DeathView=0 makes the game's death camera and the respawn screen's camera the view again, as before (<see cref="DeathView"/>).
    /// SoftwareCursor=0 stops the game drawing its own cursor into the panel; only the OS cursor is left, which is not in the panel (<see cref="SoftwareCursor"/>).
    /// FingerPress=0 turns off pressing buttons, doors and terminals by touching them with a fingertip (<see cref="Hands.FingerPress"/>).
    /// FingerPress=0 turns off pressing buttons by touching them with a fingertip (<see cref="Hands.FingerPress"/>);
    /// TouchToolbar=0 keeps the ship's toolbar off every cockpit screen (<see cref="TouchToolbar"/>); TouchToolbarScreen=n puts it
    /// on screen n (0-based) instead of the central-most one when no screen is set to Touch Toolbar.
    /// FingerPressAll=1 lifts its allow-list so doors, terminals and the rest can be touched too (for testing).
    /// ScreenGlass=0 turns off the screens' glass: no touch on screens, no stopping on them, no lines (<see cref="Hands.ScreenGlass"/>, <see cref="Hands.ScreenTouch"/>).
    /// HandStop=0 turns off the drawn hand stopping on solid things, of every kind (<see cref="Hands.HandStop"/>).
    /// TouchTrace=1 logs, every frame a fingertip is on a screen's glass or near it (or the drawn hand is stopped), the tip's distance
    /// to the glass (as the touch reads it, at the headset frame's display time, and drawn), Down/Up, the hand stop's state and the
    /// seated head's offset, held and pilot's head (<see cref="Hands.ScreenTouch.Trace"/>).
    /// HeldTrace=1 logs, every game frame a tool is held on foot, each placement of the tool (the early one in the weapon's update
    /// and the one as the arms are solved): placed or why not, the hand snapshot read, the grip, aim and palm poses against
    /// each other, the weapon written and read back, and the arm target taken from it (<see cref="Hands.HeldTrace"/>).
    /// ViewGrab=0 turns off moving the external view screen with a grip on it (<see cref="Hands.ViewScreenGrab"/>).
    /// GrabUse=0 turns off using things by closing the grip on them (<see cref="Hands.GrabUse"/>); the right grip is then Use along the ray only.
    /// GrabAll=0 puts back grab to use's allow-list of before (seats, cryo pods, airtight and advanced doors, medical rooms, cargo, terminals; on foot only).
    /// GrabUseTrace=1 logs each grab-to-use grip's line of reach from the shoulder: clear, or the block in the way.
    /// HeadGestures=0 turns off the helmet grip and the temple tap for the lamp (<see cref="Hands.HeadGestures"/>).
    /// ToolGrasp=0 holds a tool by the game's wrist spot instead of the middle of the hand, as before (<see cref="Hands.HeldItem.WristGrasp"/>).
    /// TwoHand=0 turns off holding the tool in both hands; the other grip opens the toolbar wheel as before (<see cref="Hands.TwoHand"/>).
    /// TwoHandStay=0 lets go of the tool's second grip when the hand pulls 15 cm off it, TwoHandStay=1 keeps it while the grip is held,
    /// whatever the "Two-hand grip stays on" setting says (<see cref="Hands.TwoHand.Stays"/>).
    /// TwoHandRoll=90 lets the other hand turn round the handle it holds to suit its forearm, up to 90 degrees each way (0, the
    /// default: exactly as the game's LeftHand spot has it); TwoHandSlide=30 moves it 30 mm along the handle toward its little finger.
    /// SurfaceStop=0 lets the drawn hands pass through hulls, walls, floors and rock (<see cref="Hands.SurfaceStop"/>);
    /// SurfaceStopGlass=0 lets them pass through window and canopy glass only (<see cref="Hands.GlassPanes"/>);
    /// SurfaceStopTrace=1 logs each touch (depth, normal, what) and each thing passed.
    /// Kickback=0 turns off the kick of a two-handed tool or gun used in one hand, whatever the "Tool and weapon kickback" setting
    /// says; KickbackTrace=1 logs it every frame while an item is in use or settling (<see cref="Hands.Kickback"/>). These three take
    /// fractions: KickScale=x multiplies all of the kick (1 as built), KickAir=x is the share of a grinder's or drill's kick left
    /// spinning in the air (0.8 as built), KickTwoHand=x the share of the one-handed kick left with both hands on the item (0.33 as built).
    /// Reload=0 turns off reloading a hand weapon from the hands, both the grip at the magazine and the right stick click (<see cref="Hands.Reload"/>); ReloadTrace=1 logs every frame the other hand is within 25 cm of the held weapon's magazine.
    /// InputLog=0 stops the log line for each headset button press and release, and the menu focus lines beside them (<see cref="InputLog"/>; on by default, at most 10 lines a second).
    /// VRPrompts=0 gives the game's prompts their keyboard names back (<see cref="VRPrompts"/>); VRRadial=0 gives back the game's gamepad radial menus, labels and entries (<see cref="Gui.VRRadialMenus"/>); ActionsWheel=0 has the left stick click on the tools wheel open the game's radial screen instead of the wheel at the hand (<see cref="Gui.ActionsWheel"/>); HudToolbar=1 shows the HUD's bottom toolbar on foot too (<see cref="HudToolbar"/>).
    /// BodyClearance=0 lets the eye go into the character's own body (<see cref="Tracking.BodyGuard"/>), CrouchWithHead=0 stops the head's crouch crouching
    /// the character, SeatClamp=0 lets the head leave the seated character's head by any distance, back into the body too (<see cref="Tracking.CockpitHead"/>),
    /// HeadLock=0 lets the head on foot go any distance from the character's head (<see cref="Tracking.BodyGuard"/>);
    /// HeadPush=0 gives the lock its soft hold back, which leaves the on-foot centre where the last recentre put it and holds a head far from it off the view, in place of the pushed edge that moves the centre with the head (<see cref="Tracking.GameHead.PushOnFoot"/>).
    /// EyeHold=0 lets the eye follow the animated head bone on foot and upright (a tool coming up or going away, a walk that stops), as before, instead of the headset (<see cref="Tracking.EyeHold"/>);
    /// EyeTrace=1 logs a line for every simulation frame on foot with the movement state, the body's drop and the eye's height above the feet.
    /// BodyForward=0 draws the body on foot where the game puts it under the eye, as before, instead of 12 cm further forward so the shoulders sit as a person's do;
    /// BodyForwardMm=n draws it n millimetres forward instead (<see cref="Tracking.BodyShift"/>); BodyForwardHold=0 brings the body forward again as soon as
    /// the room in front of the eye allows, at 0.4 m/s, as before, instead of holding it back 0.75 s after the room last held it and then at 0.25 m/s.
    /// SuitLightEye=0 gives the game's own placement of the suit's lamp back (15 cm above the head bone, aimed along the body), in place of the lamp at the
    /// eye, a few cm above it and ahead, aimed where the head looks (<see cref="SuitLight"/>); SuitLightEye=2 also logs once a second whether the lamp was placed in the camera's frame, and charges an empty suit battery once so the lamp has power.
    /// RecentreAll=0 makes a recentre only turn, as before, leaving the eye where it is (<see cref="Tracking.GameHead.Recentre"/>).
    /// SeatedPlay=1 is seated play and SeatedPlay=0 standing play, whatever the Play position setting says (<see cref="VRSettings.SeatedPlay"/>).
    /// CameraShake=0 (Off), 1 (Low, a quarter) or 2 (Full) sets how much of the game's camera shake, camera spring and walking head bob reaches the view, whatever the Camera shake setting says (<see cref="Tracking.CameraShake"/>).
    /// ExitUpright=0 leaves getting up from a seat in gravity to the game, which places the character tilted as the vehicle is and then turns it upright over the next frames, the view with it (<see cref="Tracking.ExitUpright"/>); ExitUprightTrace=1 logs every frame of the 3 s after getting up: the body's tilt and the view's pitch, roll and yaw from the frame before.
    /// ZeroGPivot=0 leaves zero-g pitch and roll from the hands turning the body about its middle, as the game's jetpack does, instead of about the eyes (<see cref="Tracking.ZeroGPivot"/>).
    /// MenuHoldRecentre=0 makes the left Menu button what it was before the hold: the game menu or Back from the press, and no recentre (<see cref="Input.VRControls.MenuHoldOff"/>).
    /// SharedShadows=1 fits one set of sun shadow cascades for both eyes (<see cref="SharedShadows"/>; off until checked in game);
    /// SharedSpotShadows=1 draws the spot light shadow maps once, in the first eye, ProbeOnce=1 steps the environment probe once a frame (<see cref="SceneOnce"/>; both off until checked in game);
    /// DynamicResolution=0/1 turns dynamic resolution off or on whatever the setting says, RenderScale=n (percent) holds the eyes at n% each way
    /// (<see cref="Rendering.DynamicResolution"/>); MotionExtrapolation=0 draws the view and its carrier where the game frame put them, in 60 Hz steps,
    /// instead of moved on by how much older the game frame is than usual (<see cref="Rendering.MotionExtrapolation"/>); LaserRide=0 gives the lasers
    /// in the world again, so they are not moved on with the view and the cockpit (<see cref="Rendering.MotionExtrapolation.AddLine"/>); ToolPartsRide=0 leaves
    /// the held tool's parts (the drill's spike, the grinder's disc) where the game frame put them again, so they slide against the tool while the body moves
    /// (<see cref="Rendering.MotionExtrapolation.PartsOff"/>); DesktopIpd=0 (mm) sets how far
    /// apart the desktop eyes are, in place of the IPD setting (0 draws both from one point, so the two halves of the window should match);
    /// ShadowFreeze=1 is the renderer's own frozen shadow camera (the cascades stop following the view), UsageSkip=0 stops it skipping
    /// cascades few pixels use.
    /// Drive=0 stops the test command file being read and run (<see cref="Input.Drive"/>).
    /// FakeHandsPoll=N reads the fake hands' file every N frames (1 to 60), not every 15 (<see cref="Input.DesktopHands"/>), for a test that presses twice within half a second.
    /// FocusMute=1 gives the game's own mute when its window is in the background back, even in the headset (<see cref="SoundFocus"/>; by default the sound stays on in VR).
    /// HeadsetAudio=0 plays the game's sound through the Windows default output, not the headset's audio device (<see cref="HeadsetAudio"/>); read when the
    /// game starts its sound (at start-up, or again after its audio device went away).
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

        /// <summary>
        /// One switch's value as the control file has it right now, or null when it is not there. For a switch read once, when
        /// something starts, which can be before <see cref="Update"/> first reads the file.
        /// </summary>
        public static int? Peek(string name)
        {
            string text;
            try
            {
                if (!File.Exists(ControlFile))
                    return null;
                text = File.ReadAllText(ControlFile);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            int? found = null;
            foreach (string part in text.Split(new[] { ' ', ',', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] pair = part.Split('=');
                if (pair.Length == 2 && pair[0].Equals(name, StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                    found = value;
            }
            return found;
        }

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
            SceneOnce.SpotShadowsEachEye = true;
            SceneOnce.ProbeEachPass = true;
            Rendering.DynamicResolution.Debug = null;
            Rendering.DynamicResolution.FixedScale = null;
            Rendering.MotionExtrapolation.On = true;
            Rendering.MotionExtrapolation.RideOff = false;
            Rendering.MotionExtrapolation.PartsOff = false;
            EyeOffset.Off = false;
            OnceBillboards.Shared = false;
            HudCamera.Off = false;
            HudVisor.Off = false;
            HudVisor.NoCylinder = false;
            PanelCursor.Off = false;
            WorldMenu.Off = false;
            FullView.Off = false;
            FullView.Trace = false;
            BloodPulse.Off = false;
            BloodPulse.Test = null;
            BloodPulse.TestPhase = null;
            BloodPulse.TestHits = null;
            VRControls.Off = false;
            Laser.Off = false;
            Drive.Off = false;
            SoundFocus.GameMute = false;
            HandIK.ResetDebug();
            Tracking.HandResidual.On = false;
            Tracking.HandResidual.Ghost = false;
            Tracking.HandResidual.Handle = false;
            Tracking.ToolSteady.On = false;
            Vignette.Off = false;
            Vignette.Test = false;
            Hands.Haptics.Off = false;
            Hands.BuildMode.Off = false;
            ToolbarWheel.Mode = ToolbarWheel.ModeSlots;
            VRKeyboardHook.Off = false;
            Hands.HandTools.Off = false;
            ExternalView.Debug = null;
            PerfMeter.Off = false;
            PerfMeter.PassMarks = false;
            StallWatchdog.Off = false;
            Tracking.ChaseCamera.Off = false;
            WristButton.Off = false;
            WristStatus.Off = false;
            WristStatus.Brightness = 1f;
            WristStatus.ShowAll = false;
            QuickActionsScreen.Plain = false;
            MainMenu.Off = false;
            ZoomInset.Off = false;
            ZoomInset.HalfRate = false;
            DeathView.Off = false;
            SoftwareCursor.Off = false;
            Hands.FingerPress.Off = false;
            Hands.ScreenGlass.Off = false;
            Hands.HandStop.Off = false;
            Hands.ScreenTouch.Trace = false;
            Hands.HeldTrace.On = false;
            TouchToolbar.Off = false;
            TouchToolbar.ScreenOverride = -1;
            Hands.FingerPress.All = false;
            Hands.FingerPress.ReachDebug = 0.0;
            Hands.GrabUse.Off = false;
            Hands.GrabUse.All = true;
            Hands.GrabUse.Trace = false;
            Hands.ViewScreenGrab.Off = false;
            Hands.HeadGestures.Off = false;
            Hands.HeldItem.WristGrasp = false;
            Hands.TwoHand.Off = false;
            Hands.TwoHand.StaysDebug = null;
            Hands.TwoHand.RollMost = Hands.TwoHand.DefaultRollMost;
            Hands.TwoHand.SlideDebug = 0f;
            Hands.SurfaceStop.Off = false;
            Hands.SurfaceStop.GlassOff = false;
            Hands.SurfaceStop.Trace = false;
            Hands.Kickback.Off = false;
            Hands.Kickback.Trace = false;
            Hands.Kickback.Scale = 1.0;
            Hands.Kickback.Air = Hands.Kickback.AirShare;
            Hands.Kickback.TwoHands = Hands.Kickback.TwoHandShare;
            Hands.Reload.Off = false;
            Hands.Reload.Trace = false;
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
            Tracking.BodyGuard.PushOff = false;
            Tracking.BodyGuard.HoldOff = false;
            Tracking.BodyGuard.TraceOn = false;
            Tracking.BodyShift.Off = false;
            Tracking.BodyShift.DebugMm = null;
            Tracking.BodyShift.HoldOff = false;
            SuitLight.Off = false;
            SuitLight.Diag = false;
            Tracking.GameHead.RecentreTurnOnly = false;
            VRSettings.SeatedPlayOverride = null;
            VRSettings.CameraShakeOverride = null;
            Tracking.ExitUpright.Off = false;
            Tracking.ExitUpright.Trace = false;
            Tracking.ZeroGPivot.Off = false;
            Input.DesktopHands.PollEveryFrames = 15;
            Input.VRControls.MenuHoldOff = false;
            Input.ControlGrip.Debug = null;
            Input.ControlGrip.LogInputs = false;
            Input.ControlGrip.TakeDebug = null;

            var applied = new List<string>();
            foreach (string part in (text ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] pair = part.Split('=');
                // The kick's tuning takes fractions (KickScale=1.5); every other switch is a whole number.
                if (pair.Length == 2 && double.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double factor) && !double.IsNaN(factor))
                {
                    if (pair[0].Equals("KickScale", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.Kickback.Scale = Math.Max(0.0, Math.Min(5.0, factor));
                        applied.Add($"KickScale={Hands.Kickback.Scale:0.##}");
                        continue;
                    }
                    if (pair[0].Equals("KickAir", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.Kickback.Air = Math.Max(0.0, Math.Min(1.5, factor));
                        applied.Add($"KickAir={Hands.Kickback.Air:0.##}");
                        continue;
                    }
                    if (pair[0].Equals("KickTwoHand", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.Kickback.TwoHands = Math.Max(0.0, Math.Min(1.0, factor));
                        applied.Add($"KickTwoHand={Hands.Kickback.TwoHands:0.##}");
                        continue;
                    }
                }
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
                    if (pair[0].Equals("BodyForward", StringComparison.OrdinalIgnoreCase))
                    {
                        Tracking.BodyShift.Off = setting == 0;
                        applied.Add($"BodyForward={!Tracking.BodyShift.Off}");
                        continue;
                    }
                    if (pair[0].Equals("BodyForwardHold", StringComparison.OrdinalIgnoreCase))
                    {
                        Tracking.BodyShift.HoldOff = setting == 0;
                        applied.Add($"BodyForwardHold={!Tracking.BodyShift.HoldOff}");
                        continue;
                    }
                    if (pair[0].Equals("BodyForwardMm", StringComparison.OrdinalIgnoreCase))
                    {
                        Tracking.BodyShift.DebugMm = setting;
                        applied.Add($"BodyForwardMm={setting}");
                        continue;
                    }
                    if (pair[0].Equals("HeadLock", StringComparison.OrdinalIgnoreCase))
                    {
                        Tracking.BodyGuard.LockOff = setting == 0;
                        applied.Add($"HeadLock={!Tracking.BodyGuard.LockOff}");
                        continue;
                    }
                    if (pair[0].Equals("HeadPush", StringComparison.OrdinalIgnoreCase))
                    {
                        Tracking.BodyGuard.PushOff = setting == 0;
                        applied.Add($"HeadPush={!Tracking.BodyGuard.PushOff}");
                        continue;
                    }
                    if (pair[0].Equals("EyeHold", StringComparison.OrdinalIgnoreCase))
                    {
                        Tracking.BodyGuard.HoldOff = setting == 0;
                        applied.Add($"EyeHold={!Tracking.BodyGuard.HoldOff}");
                        continue;
                    }
                    if (pair[0].Equals("EyeTrace", StringComparison.OrdinalIgnoreCase))
                    {
                        Tracking.BodyGuard.TraceOn = setting != 0;
                        applied.Add($"EyeTrace={Tracking.BodyGuard.TraceOn}");
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
                    if (pair[0].Equals("CameraShake", StringComparison.OrdinalIgnoreCase))
                    {
                        VRSettings.CameraShakeOverride = Math.Max(0, Math.Min(2, setting));
                        applied.Add($"CameraShake={VRSettings.CameraShakeOverride} ({new[] { "Off", "Low", "Full" }[VRSettings.CameraShakeOverride.Value]})");
                        continue;
                    }
                    if (pair[0].Equals("FakeHandsPoll", StringComparison.OrdinalIgnoreCase))
                    {
                        Input.DesktopHands.PollEveryFrames = Math.Max(1, Math.Min(60, setting));
                        applied.Add($"FakeHandsPoll={Input.DesktopHands.PollEveryFrames}");
                        continue;
                    }
                    if (pair[0].Equals("ZeroGPivot", StringComparison.OrdinalIgnoreCase))
                    {
                        Tracking.ZeroGPivot.Off = setting == 0;
                        applied.Add($"ZeroGPivot={!Tracking.ZeroGPivot.Off}");
                        continue;
                    }
                    if (pair[0].Equals("ExitUpright", StringComparison.OrdinalIgnoreCase))
                    {
                        Tracking.ExitUpright.Off = setting == 0;
                        applied.Add($"ExitUpright={!Tracking.ExitUpright.Off}");
                        continue;
                    }
                    if (pair[0].Equals("ExitUprightTrace", StringComparison.OrdinalIgnoreCase))
                    {
                        Tracking.ExitUpright.Trace = setting != 0;
                        applied.Add($"ExitUprightTrace={Tracking.ExitUpright.Trace}");
                        continue;
                    }
                    if (pair[0].Equals("ControlGrip", StringComparison.OrdinalIgnoreCase))
                    {
                        Input.ControlGrip.Debug = setting != 0;
                        applied.Add($"ControlGrip={setting != 0}");
                        continue;
                    }
                    if (pair[0].Equals("ControlGripLog", StringComparison.OrdinalIgnoreCase))
                    {
                        Input.ControlGrip.LogInputs = setting != 0;
                        applied.Add($"ControlGripLog={setting != 0}");
                        continue;
                    }
                    if (pair[0].Equals("ControlGripTake", StringComparison.OrdinalIgnoreCase))
                    {
                        Input.ControlGrip.TakeDebug = Math.Max(1, Math.Min(60, setting)) / 100f;
                        applied.Add($"ControlGripTake={Input.ControlGrip.TakeRadius * 100f:0} cm");
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
                    if (pair[0].Equals("HudVisor", StringComparison.OrdinalIgnoreCase))
                    {
                        HudVisor.Off = setting == 0;
                        applied.Add($"HudVisor={!HudVisor.Off}");
                        continue;
                    }
                    if (pair[0].Equals("HudVisorCylinder", StringComparison.OrdinalIgnoreCase))
                    {
                        HudVisor.NoCylinder = setting == 0;
                        applied.Add($"HudVisorCylinder={!HudVisor.NoCylinder}");
                        continue;
                    }
                    if (pair[0].Equals("FullView", StringComparison.OrdinalIgnoreCase))
                    {
                        FullView.Off = setting == 0;
                        applied.Add($"FullView={!FullView.Off}");
                        continue;
                    }
                    if (pair[0].Equals("BloodPulse", StringComparison.OrdinalIgnoreCase))
                    {
                        BloodPulse.Off = setting == 0;
                        applied.Add($"BloodPulse={!BloodPulse.Off}");
                        continue;
                    }
                    if (pair[0].Equals("BloodTest", StringComparison.OrdinalIgnoreCase))
                    {
                        BloodPulse.Test = Math.Max(0, Math.Min(setting, 100)) / 100f;
                        applied.Add($"BloodTest={BloodPulse.Test * 100:0}% health");
                        continue;
                    }
                    if (pair[0].Equals("BloodPhase", StringComparison.OrdinalIgnoreCase))
                    {
                        BloodPulse.TestPhase = Math.Max(0, Math.Min(setting, 100)) / 100f;
                        applied.Add($"BloodPhase={BloodPulse.TestPhase * 100:0}% of a beat");
                        continue;
                    }
                    if (pair[0].Equals("BloodHit", StringComparison.OrdinalIgnoreCase))
                    {
                        BloodPulse.TestHits = setting > 0 ? Math.Max(setting, 50) / 1000f : (float?)null;
                        applied.Add(setting > 0 ? $"BloodHit=every {BloodPulse.TestHits * 1000:0} ms" : "BloodHit=off");
                        continue;
                    }
                    if (pair[0].Equals("BloodTrace", StringComparison.OrdinalIgnoreCase))
                    {
                        FullView.Trace = setting != 0;
                        applied.Add($"BloodTrace={FullView.Trace}");
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
                    if (pair[0].Equals("HeadsetAudio", StringComparison.OrdinalIgnoreCase))
                    {
                        // Read with Peek when the sound starts; listed here so it is not reported as ignored.
                        applied.Add($"HeadsetAudio={setting != 0} (when the game next starts its sound)");
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
                    if (pair[0].Equals("ToolSteady", StringComparison.OrdinalIgnoreCase))
                    {
                        Tracking.ToolSteady.On = setting != 0;
                        applied.Add($"ToolSteady={Tracking.ToolSteady.On}");
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
                    if (pair[0].Equals("BuildMode", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.BuildMode.Off = setting == 0;
                        applied.Add($"BuildMode={!Hands.BuildMode.Off}");
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
                    if (pair[0].Equals("PerfMeter", StringComparison.OrdinalIgnoreCase))
                    {
                        PerfMeter.Off = setting == 0;
                        applied.Add($"PerfMeter={!PerfMeter.Off}");
                        continue;
                    }
                    if (pair[0].Equals("GpuPasses", StringComparison.OrdinalIgnoreCase))
                    {
                        PerfMeter.PassMarks = setting != 0;
                        applied.Add($"GpuPasses={PerfMeter.PassMarks}");
                        continue;
                    }
                    if (pair[0].Equals("StallStacks", StringComparison.OrdinalIgnoreCase))
                    {
                        StallWatchdog.Off = setting == 0;
                        applied.Add($"StallStacks={!StallWatchdog.Off}");
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
                    if (pair[0].Equals("WristStatus", StringComparison.OrdinalIgnoreCase))
                    {
                        WristStatus.Off = setting == 0;
                        applied.Add($"WristStatus={!WristStatus.Off}");
                        continue;
                    }
                    if (pair[0].Equals("WristStatusBright", StringComparison.OrdinalIgnoreCase))
                    {
                        WristStatus.Brightness = Math.Max(0, setting) / 100f;
                        applied.Add($"WristStatusBright={setting}%");
                        continue;
                    }
                    if (pair[0].Equals("WristStatusAll", StringComparison.OrdinalIgnoreCase))
                    {
                        WristStatus.ShowAll = setting != 0;
                        applied.Add($"WristStatusAll={WristStatus.ShowAll}");
                        continue;
                    }
                    if (pair[0].Equals("QuickMenuNative", StringComparison.OrdinalIgnoreCase))
                    {
                        QuickActionsScreen.Plain = setting == 0;
                        applied.Add($"QuickMenuNative={!QuickActionsScreen.Plain}");
                        continue;
                    }
                    if (pair[0].Equals("MainMenuVR", StringComparison.OrdinalIgnoreCase))
                    {
                        MainMenu.Off = setting == 0;
                        applied.Add($"MainMenuVR={!MainMenu.Off}");
                        continue;
                    }
                    if (pair[0].Equals("ZoomInset", StringComparison.OrdinalIgnoreCase))
                    {
                        ZoomInset.Off = setting == 0;
                        applied.Add($"ZoomInset={!ZoomInset.Off}");
                        continue;
                    }
                    if (pair[0].Equals("ZoomInsetHalfRate", StringComparison.OrdinalIgnoreCase))
                    {
                        ZoomInset.HalfRate = setting != 0;
                        applied.Add($"ZoomInsetHalfRate={ZoomInset.HalfRate}");
                        continue;
                    }
                    if (pair[0].Equals("DeathView", StringComparison.OrdinalIgnoreCase))
                    {
                        DeathView.Off = setting == 0;
                        applied.Add($"DeathView={!DeathView.Off}");
                        continue;
                    }
                    if (pair[0].Equals("SoftwareCursor", StringComparison.OrdinalIgnoreCase))
                    {
                        SoftwareCursor.Off = setting == 0;
                        applied.Add($"SoftwareCursor={!SoftwareCursor.Off}");
                        continue;
                    }
                    if (pair[0].Equals("TouchToolbar", StringComparison.OrdinalIgnoreCase))
                    {
                        TouchToolbar.Off = setting == 0;
                        applied.Add($"TouchToolbar={!TouchToolbar.Off}");
                        continue;
                    }
                    if (pair[0].Equals("TouchToolbarScreen", StringComparison.OrdinalIgnoreCase))
                    {
                        TouchToolbar.ScreenOverride = setting;
                        applied.Add($"TouchToolbarScreen={setting}");
                        continue;
                    }
                    if (pair[0].Equals("FingerPress", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.FingerPress.Off = setting == 0;
                        applied.Add($"FingerPress={!Hands.FingerPress.Off}");
                        continue;
                    }
                    if (pair[0].Equals("ScreenGlass", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.ScreenGlass.Off = setting == 0;
                        applied.Add($"ScreenGlass={!Hands.ScreenGlass.Off}");
                        continue;
                    }
                    if (pair[0].Equals("HandStop", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.HandStop.Off = setting == 0;
                        applied.Add($"HandStop={!Hands.HandStop.Off}");
                        continue;
                    }
                    if (pair[0].Equals("TouchTrace", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.ScreenTouch.Trace = setting != 0;
                        applied.Add($"TouchTrace={Hands.ScreenTouch.Trace}");
                        continue;
                    }
                    if (pair[0].Equals("HeldTrace", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.HeldTrace.On = setting != 0;
                        applied.Add($"HeldTrace={Hands.HeldTrace.On}");
                        continue;
                    }
                    if (pair[0].Equals("ViewGrab", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.ViewScreenGrab.Off = setting == 0;
                        applied.Add($"ViewGrab={!Hands.ViewScreenGrab.Off}");
                        continue;
                    }
                    if (pair[0].Equals("GrabUse", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.GrabUse.Off = setting == 0;
                        applied.Add($"GrabUse={!Hands.GrabUse.Off}");
                        continue;
                    }
                    if (pair[0].Equals("GrabAll", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.GrabUse.All = setting != 0;
                        applied.Add($"GrabAll={Hands.GrabUse.All}");
                        continue;
                    }
                    if (pair[0].Equals("GrabUseTrace", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.GrabUse.Trace = setting != 0;
                        applied.Add($"GrabUseTrace={Hands.GrabUse.Trace}");
                        continue;
                    }
                    if (pair[0].Equals("HeadGestures", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.HeadGestures.Off = setting == 0;
                        applied.Add($"HeadGestures={!Hands.HeadGestures.Off}");
                        continue;
                    }
                    if (pair[0].Equals("ToolGrasp", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.HeldItem.WristGrasp = setting == 0;
                        applied.Add($"ToolGrasp={!Hands.HeldItem.WristGrasp}");
                        continue;
                    }
                    if (pair[0].Equals("TwoHand", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.TwoHand.Off = setting == 0;
                        applied.Add($"TwoHand={!Hands.TwoHand.Off}");
                        continue;
                    }
                    if (pair[0].Equals("SurfaceStop", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.SurfaceStop.Off = setting == 0;
                        applied.Add($"SurfaceStop={!Hands.SurfaceStop.Off}");
                        continue;
                    }
                    if (pair[0].Equals("SurfaceStopGlass", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.SurfaceStop.GlassOff = setting == 0;
                        applied.Add($"SurfaceStopGlass={!Hands.SurfaceStop.GlassOff}");
                        continue;
                    }
                    if (pair[0].Equals("SurfaceStopTrace", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.SurfaceStop.Trace = setting != 0;
                        applied.Add($"SurfaceStopTrace={Hands.SurfaceStop.Trace}");
                        continue;
                    }
                    if (pair[0].Equals("Reload", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.Reload.Off = setting == 0;
                        applied.Add($"Reload={!Hands.Reload.Off}");
                        continue;
                    }
                    if (pair[0].Equals("ReloadTrace", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.Reload.Trace = setting != 0;
                        applied.Add($"ReloadTrace={Hands.Reload.Trace}");
                        continue;
                    }
                    if (pair[0].Equals("TwoHandStay", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.TwoHand.StaysDebug = setting != 0;
                        applied.Add($"TwoHandStay={setting != 0}");
                        continue;
                    }
                    if (pair[0].Equals("TwoHandRoll", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.TwoHand.RollMost = setting;
                        applied.Add($"TwoHandRoll={setting} deg");
                        continue;
                    }
                    if (pair[0].Equals("TwoHandSlide", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.TwoHand.SlideDebug = setting / 1000f;
                        applied.Add($"TwoHandSlide={setting} mm");
                        continue;
                    }
                    if (pair[0].Equals("Kickback", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.Kickback.Off = setting == 0;
                        applied.Add($"Kickback={!Hands.Kickback.Off}");
                        continue;
                    }
                    if (pair[0].Equals("KickbackTrace", StringComparison.OrdinalIgnoreCase))
                    {
                        Hands.Kickback.Trace = setting != 0;
                        applied.Add($"KickbackTrace={Hands.Kickback.Trace}");
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
                    if (pair[0].Equals("SharedSpotShadows", StringComparison.OrdinalIgnoreCase))
                    {
                        SceneOnce.SpotShadowsEachEye = setting == 0;
                        applied.Add($"SharedSpotShadows={!SceneOnce.SpotShadowsEachEye}");
                        continue;
                    }
                    if (pair[0].Equals("ProbeOnce", StringComparison.OrdinalIgnoreCase))
                    {
                        SceneOnce.ProbeEachPass = setting == 0;
                        applied.Add($"ProbeOnce={!SceneOnce.ProbeEachPass}");
                        continue;
                    }
                    if (pair[0].Equals("DynamicResolution", StringComparison.OrdinalIgnoreCase))
                    {
                        Rendering.DynamicResolution.Debug = setting != 0;
                        applied.Add($"DynamicResolution={setting != 0}");
                        continue;
                    }
                    if (pair[0].Equals("MotionExtrapolation", StringComparison.OrdinalIgnoreCase))
                    {
                        Rendering.MotionExtrapolation.On = setting != 0;
                        applied.Add($"MotionExtrapolation={setting != 0}");
                        continue;
                    }
                    if (pair[0].Equals("LaserRide", StringComparison.OrdinalIgnoreCase))
                    {
                        Rendering.MotionExtrapolation.RideOff = setting == 0;
                        applied.Add($"LaserRide={setting != 0}");
                        continue;
                    }
                    if (pair[0].Equals("ToolPartsRide", StringComparison.OrdinalIgnoreCase))
                    {
                        Rendering.MotionExtrapolation.PartsOff = setting == 0;
                        applied.Add($"ToolPartsRide={setting != 0}");
                        continue;
                    }
                    if (pair[0].Equals("RenderScale", StringComparison.OrdinalIgnoreCase))
                    {
                        int percent = Math.Max(30, Math.Min(100, setting));
                        Rendering.DynamicResolution.FixedScale = percent / 100f;
                        applied.Add($"RenderScale={percent}%");
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
