using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Gui;
using Sandbox.Game.GUI;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using Sandbox.Graphics;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Gui;
using SpaceEngineersVR.Input;
using VRage.Game;
using VRage.Game.ObjectBuilders.Definitions;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The HUD's bottom toolbar (the hotbar) is left out on foot and on the jetpack, where the tools wheel (left grip) and the
    /// wrist panel do its job; a short label names the tool you pick instead. Seated, in a camera view, in the G menu and the
    /// terminal, and in flat play, it is drawn as the game draws it. Off with the VR setting "Show toolbar on foot"
    /// (<see cref="VRSettings.ShowToolbarOnFoot"/>); RenderDebug HudToolbar=1 shows it again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The game draws the hotbar in MyGuiScreenHudSpace (RecreateControls builds <c>m_toolbarControl</c> as a MyGuiControlToolbar,
    /// Draw sets its Visible every frame). Its Visible stays true here: MyToolbarComponent.HandleInput only reads the toolbar keys
    /// while the gameplay screen has the focus or the control says it is shown (OnVisibleChanged), and the hands' slot picks
    /// go through the same path. So the control is skipped in its own Draw instead (the grid, the selected item's name, the
    /// page numbers and the colour panels are all drawn there). Only the HUD's own control is skipped: the G menu and the
    /// terminal have their own.
    /// </para>
    /// <para>
    /// The plate behind the hotbar, and the lines of text around it, are not the control: the Default HUD (Content\Data\Hud)
    /// draws them as stat controls (MyStatControls, "CenterPlate" and "CenterPlateController"), each with a visible
    /// condition. While the toolbar is hidden, the plate, the "Customize Toolbar" and "Color" lines and the other keyboard
    /// hints on it get a condition that is never true (the game's own mechanism, so they fade as the game would fade them), and
    /// swap back to their own the moment the toolbar is shown. The oxygen, temperature and gravity readouts stay.
    /// </para>
    /// <para>
    /// The state is worked out once a frame, before the HUD draws, so the control, the plate and the label agree. A tool
    /// switch while it is hidden shows the item's name for a moment on the game's own notification font and shadow.
    /// </para>
    /// </remarks>
    internal static class HudToolbar
    {
        /// <summary>Debug: the toolbar is shown as the game shows it (<see cref="RenderDebug"/> HudToolbar=1).</summary>
        public static bool ForceShown { get; set; }

        /// <summary>The toolbar is hidden this frame. Readable from any thread; set once a frame, before the HUD draws.</summary>
        public static bool Hidden => hidden;

        /// <summary>The label for the tool just picked fades in this long.</summary>
        public const double FadeInSeconds = 0.12;

        /// <summary>It stays at full strength this long after the last switch.</summary>
        public const double HoldSeconds = 1.5;

        /// <summary>It then fades out over this long.</summary>
        public const double FadeOutSeconds = 0.4;

        /// <summary>A selection change this soon after the toolbar appeared (a world loading, leaving a seat) is the game settling, not a pick.</summary>
        public const double SettleSeconds = 1.0;

        /// <summary>Where the label is, in the HUD's normalized coordinates: centred, where the top of the hotbar was.</summary>
        private static readonly Vector2 LabelPosition = new Vector2(0.5f, 0.86f);

        /// <summary>Where a notice (<see cref="Notice"/>) is: above the tool label's place, so the two never overlap.</summary>
        private static readonly Vector2 NoticePosition = new Vector2(0.5f, 0.79f);

        private static readonly string[] KeptStats = { "environment_oxygen_level", "environment_temperature_level", "artificial_gravity", "natural_gravity" };
        private static readonly string[] KeptTexts = { "{LOC:AGravity}", "{LOC:PGravity}" };
        private static readonly string[] PlateTextures = { "CenterPlate", "CenterPlateController" };

        private static volatile bool hidden;
        private static volatile string labelText;
        private static volatile float labelAlpha;
        private static volatile string noticeText, pendingNotice;
        private static volatile float noticeAlpha;
        private static bool broken, drawBroken, statsBroken;

        private static readonly LabelFade Fade = new LabelFade();
        private static readonly LabelFade NoticeFade = new LabelFade();
        private static MyToolbar trackedToolbar;
        private static int? trackedSlot;
        private static string trackedName;
        private static double baselineAt;

        private static readonly FieldInfo ToolbarField = AccessTools.Field(typeof(MyGuiScreenHudSpace), "m_toolbarControl");
        private static readonly FieldInfo BindingsField = AccessTools.Field(typeof(MyStatControls), "m_bindings");
        private static readonly MethodInfo InitConditions = AccessTools.Method(typeof(MyStatControls), "InitConditions", new[] { typeof(ConditionBase) });
        private static FieldInfo styleField;
        private static MyGuiScreenHudSpace cachedHud;
        private static object cachedToolbar;

        /// <summary>A condition that is never true: it names no stat, so the game never finds one for it (MyStatControls.InitConditions) and its Eval says no.</summary>
        private static readonly StatCondition Never = new StatCondition { StatId = MyStringHash.GetOrCompute("VRHudToolbarNever") };

        private static readonly ConditionalWeakTable<MyStatControls, Gate> Gates = new ConditionalWeakTable<MyStatControls, Gate>();
        private static readonly ConditionalWeakTable<MyObjectBuilder_StatVisualStyle, Original> Originals = new ConditionalWeakTable<MyObjectBuilder_StatVisualStyle, Original>();

        private sealed class Original
        {
            public ConditionBase Condition;
        }

        /// <summary>The bindings of one MyStatControls whose visibility follows the toolbar.</summary>
        private sealed class Gate
        {
            public readonly List<MyObjectBuilder_StatVisualStyle> Styles = new List<MyObjectBuilder_StatVisualStyle>();

            public void Apply(bool hide)
            {
                foreach (MyObjectBuilder_StatVisualStyle style in Styles)
                {
                    if (Originals.TryGetValue(style, out Original original))
                        style.VisibleCondition = hide ? Never : original.Condition;
                }
            }
        }

        public static void Patch(Harmony harmony)
        {
            Hook(harmony, "the HUD's draw", AccessTools.Method(typeof(MyGuiScreenHudSpace), nameof(MyGuiScreenHudSpace.Draw)), prefix: nameof(BeforeHudDraw));
            Hook(harmony, "the toolbar control's draw", AccessTools.Method(typeof(MyGuiControlToolbar), nameof(MyGuiControlToolbar.Draw), new[] { typeof(float), typeof(float) }), prefix: nameof(BeforeToolbarDraw));
            Hook(harmony, "the HUD plate's stat controls", AccessTools.Method(typeof(MyStatControls), nameof(MyStatControls.Draw), new[] { typeof(float), typeof(float) }), prefix: nameof(BeforeStatsDraw));
            Hook(harmony, "the HUD notifications' draw", AccessTools.Method(typeof(MyHudNotifications), nameof(MyHudNotifications.Draw)), postfix: nameof(AfterNotificationsDraw));
        }

        private static void Hook(Harmony harmony, string what, MethodBase target, string prefix = null, string postfix = null)
        {
            try
            {
                if (target == null)
                    throw new MissingMethodException(what);
                harmony.Patch(target,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(HudToolbar), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(HudToolbar), postfix));
            }
            catch (Exception e)
            {
                Log.Error(e, $"HUD toolbar ({what}) could not be hooked; the game's toolbar stays as it is there");
            }
        }

        // ---- the state ----

        /// <summary>
        /// Whether the toolbar is hidden (true), or shown (false, with the reason in <paramref name="why"/>). It is hidden when
        /// all of these hold: in VR, the player's own living character is the one controlled
        /// (not a seat, not the spectator camera), the toolbar the game has up is the character's (it follows the controlled
        /// entity a tick late, which keeps a seat's toolbar from showing the character's items, and the other way round), the
        /// view is not a camera block's, and no menu has the focus. The tools wheel, the actions wheel and the wrist panel are
        /// not menus here: they are how the hands do what the hotbar did.
        /// </summary>
        internal static bool Decide(bool forceShown, bool settingOn, bool vrActive, bool onFoot, bool characterToolbar, bool cameraView, bool menu, out string why)
        {
            why = null;
            if (forceShown)
                why = "RenderDebug HudToolbar=1";
            else if (settingOn)
                why = "Show toolbar on foot is on";
            else if (!vrActive)
                why = "flat play";
            else if (!onFoot)
                why = "not on foot";
            else if (!characterToolbar)
                why = "the toolbar is not the character's yet";
            else if (cameraView)
                why = "camera view";
            else if (menu)
                why = "a menu has the focus";
            return why == null;
        }

        private static bool Compute(out string why, out MyToolbar toolbar)
        {
            toolbar = null;
            MySession session = MySession.Static;
            bool onFoot = false, cameraView = false, menu = false;
            if (session != null)
            {
                MyCharacter character = session.ControlledEntity as MyCharacter;
                onFoot = character != null && character == session.LocalCharacter && !character.IsDead && !session.IsCameraUserControlledSpectator();
                cameraView = session.CameraController is MyCameraBlock { IsActive: true };
                toolbar = MyToolbarComponent.CurrentToolbar;
            }
            bool characterToolbar = toolbar != null && toolbar.ToolbarType == MyToolbarType.Character;
            if (onFoot && characterToolbar)
                menu = MenuHasFocus();
            return Decide(ForceShown, VRSettings.ShowToolbarOnFoot, VRInput.Active, onFoot, characterToolbar, cameraView, menu, out why);
        }

        private static bool MenuHasFocus()
        {
            if (ToolbarWheel.Owns)
                return false;
            MyGuiScreenBase focus = MyScreenManager.GetScreenWithFocus();
            if (focus == null || focus is MyGuiScreenGamePlay)
                return false;
            return !(focus is QuickActionsScreen || focus is ActionsWheelScreen || focus is MyGuiControlRadialMenuBase);
        }

        // ---- the hooks ----

        /// <summary>Once a frame, before the HUD draws anything: the state, and the label.</summary>
        private static void BeforeHudDraw()
        {
            if (broken)
                return;
            try
            {
                bool nowHidden = Compute(out string why, out MyToolbar toolbar);
                if (nowHidden != hidden)
                {
                    hidden = nowHidden;
                    Log.Info(nowHidden ? "HUD toolbar: hidden (on foot)" : "HUD toolbar: shown (" + why + ")");
                }
                double now = Seconds();
                Track(nowHidden, toolbar, now);
                labelAlpha = Fade.Update(now);
                labelText = Fade.Text;
                UpdateNotice(now);
            }
            catch (Exception e)
            {
                broken = true;
                hidden = false;
                labelText = null;
                Log.Error(e, "HUD toolbar: could not work out the state; the game's toolbar is shown");
            }
        }

        /// <summary>The HUD's own toolbar control is not drawn while hidden; its Visible, and so the toolbar keys, are left alone.</summary>
        private static bool BeforeToolbarDraw(MyGuiControlToolbar __instance)
        {
            if (!hidden || drawBroken)
                return true;
            try
            {
                return !ReferenceEquals(__instance, HudControl());
            }
            catch (Exception e)
            {
                drawBroken = true;
                Log.Error(e, "HUD toolbar: the control could not be found; the game's toolbar is shown");
                return true;
            }
        }

        private static object HudControl()
        {
            MyGuiScreenHudSpace hud = MyGuiScreenHudSpace.Static;
            if (!ReferenceEquals(hud, cachedHud))
            {
                cachedToolbar = hud == null ? null : ToolbarField.GetValue(hud);
                cachedHud = hud;
            }
            return cachedToolbar;
        }

        /// <summary>Each MyStatControls the HUD draws: the plate's are found the first time and follow the state from then on.</summary>
        private static void BeforeStatsDraw(MyStatControls __instance)
        {
            if (statsBroken)
                return;
            try
            {
                if (!Gates.TryGetValue(__instance, out Gate gate))
                {
                    gate = Prepare(__instance);
                    Gates.Add(__instance, gate);
                }
                if (gate.Styles.Count > 0)
                    gate.Apply(hidden);
            }
            catch (Exception e)
            {
                statsBroken = true;
                Log.Error(e, "HUD toolbar: the plate could not be hooked; it stays as the game draws it");
            }
        }

        /// <summary>The label, after the game's own notifications so it is on the same layer, with the same font and shadow.</summary>
        private static void AfterNotificationsDraw()
        {
            if (MyHud.MinimalHud || MyHud.CutsceneHud)
                return;
            float alpha = labelAlpha;
            string text = labelText;
            if (alpha > 0.004f && !string.IsNullOrEmpty(text))
            {
                try
                {
                    DrawLabel(text, alpha, LabelPosition);
                }
                catch (Exception e)
                {
                    labelText = null;
                    Fade.Clear();
                    Log.Error(e, "HUD toolbar: the tool label could not be drawn");
                }
            }
            alpha = noticeAlpha;
            text = noticeText;
            if (alpha > 0.004f && !string.IsNullOrEmpty(text))
            {
                try
                {
                    DrawLabel(text, alpha, NoticePosition);
                }
                catch (Exception e)
                {
                    noticeText = null;
                    NoticeFade.Clear();
                    Log.Error(e, "HUD toolbar: the notice could not be drawn");
                }
            }
        }

        /// <summary>One line of the label's font, size and shadow, centred on <paramref name="position"/>.</summary>
        private static void DrawLabel(string text, float alpha, Vector2 position)
        {
            const string font = "White";
            float scale = MyGuiSandbox.GetDefaultTextScaleWithLanguage() * 1.2f;
            Vector2 size = MyGuiManager.MeasureString(font, text, scale);
            Vector2 shadowPosition = position;
            Vector2 shadowSize = size;
            MyGuiTextShadows.DrawShadow(ref shadowPosition, ref shadowSize, null, alpha);
            MyGuiManager.DrawString(font, text, position, scale, Color.White * alpha, MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER);
        }

        /// <summary>
        /// Says something on the screen for a moment, in the tool label's style, whatever is held or sat in (the tool label itself
        /// is cleared every frame in a seat). Any thread. It is shown from the next frame the wheel is not up, since the actions
        /// wheel has the HUD off while it is: a switch made on the wheel is seen as the wheel closes. A newer notice replaces it.
        /// </summary>
        public static void Notice(string text)
        {
            if (!string.IsNullOrEmpty(text))
                pendingNotice = text;
        }

        private static void UpdateNotice(double now)
        {
            string pending = pendingNotice;
            if (pending != null && !ActionsWheel.Showing())
            {
                pendingNotice = null;
                NoticeFade.Show(pending, now);
                Log.Info($"HUD toolbar: notice \"{pending}\"");
            }
            noticeAlpha = NoticeFade.Update(now);
            noticeText = NoticeFade.Text;
        }

        // ---- the plate ----

        /// <summary>Finds the center plate's controls in a MyStatControls and notes the visible condition each of them starts with.</summary>
        private static Gate Prepare(MyStatControls controls)
        {
            var gate = new Gate();
            var styles = new List<MyObjectBuilder_StatVisualStyle>();
            bool plate = false;
            var bindings = BindingsField.GetValue(controls) as IEnumerable;
            if (bindings == null)
                return gate;
            foreach (object binding in bindings)
            {
                if (styleField == null)
                    styleField = binding.GetType().GetField("Style", BindingFlags.Public | BindingFlags.Instance);
                var style = styleField.GetValue(binding) as MyObjectBuilder_StatVisualStyle;
                if (style == null)
                    continue;
                styles.Add(style);
                if (style is MyObjectBuilder_ImageStatVisualStyle image && IsPlate(image.Texture))
                    plate = true;
            }
            if (!plate)
                return gate;

            int kept = 0;
            foreach (MyObjectBuilder_StatVisualStyle style in styles)
            {
                if (Keeps(style))
                {
                    kept++;
                    continue;
                }
                if (!Originals.TryGetValue(style, out Original original))
                {
                    if (ReferenceEquals(style.VisibleCondition, Never))
                        continue;
                    original = new Original { Condition = style.VisibleCondition };
                    Originals.Add(style, original);
                }
                // A world loaded since the first time has new stats; the game only gave them to the condition the style had when
                // the controls were made (ours, if the toolbar was hidden then).
                if (original.Condition != null && InitConditions != null)
                    InitConditions.Invoke(null, new object[] { original.Condition });
                gate.Styles.Add(style);
            }
            Log.Info($"HUD toolbar: {gate.Styles.Count} parts of the center plate follow the toolbar, {kept} readouts stay");
            return gate;
        }

        private static bool IsPlate(MyStringHash texture)
        {
            foreach (string name in PlateTextures)
            {
                if (texture == MyStringHash.GetOrCompute(name))
                    return true;
            }
            return false;
        }

        /// <summary>The readouts of the plate that stay: oxygen, temperature and gravity (the lines that name the gravity too).</summary>
        private static bool Keeps(MyObjectBuilder_StatVisualStyle style)
        {
            foreach (string name in KeptStats)
            {
                if (style.StatId == MyStringHash.GetOrCompute(name))
                    return true;
            }
            if (style is MyObjectBuilder_TextStatVisualStyle text && text.Text != null)
            {
                foreach (string kept in KeptTexts)
                {
                    if (text.Text.Equals(kept, StringComparison.Ordinal))
                        return true;
                }
            }
            return false;
        }

        // ---- the label ----

        private static double Seconds() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        /// <summary>Watches the selected tool: a pick while the toolbar is hidden shows its name, putting the tool away fades the label out.</summary>
        private static void Track(bool nowHidden, MyToolbar toolbar, double now)
        {
            if (!nowHidden || toolbar == null)
            {
                Fade.Clear();
                trackedToolbar = null;
                return;
            }
            int? slot = toolbar.SelectedSlot;
            string name = slot.HasValue ? NameOf(toolbar) : null;
            if (!ReferenceEquals(toolbar, trackedToolbar))
            {
                trackedToolbar = toolbar;
                trackedSlot = slot;
                trackedName = name;
                baselineAt = now;
                return;
            }
            if (slot == trackedSlot && name == trackedName)
                return;
            trackedSlot = slot;
            trackedName = name;
            if (now - baselineAt < SettleSeconds)
                return;
            if (string.IsNullOrEmpty(name))
            {
                Fade.Dismiss(now);
                Log.Info("HUD toolbar: tool put away");
            }
            else
            {
                Fade.Show(name, now);
                Log.Info($"HUD toolbar: tool label \"{name}\"");
            }
        }

        private static string NameOf(MyToolbar toolbar)
        {
            MyToolbarItem item = toolbar.SelectedItem;
            return item?.DisplayName?.ToString();
        }

        /// <summary>
        /// The label's strength over time: fades in, holds, fades out, eased at both ends. A new tool replaces the text and
        /// starts again from the strength the label has, so a fast run of switches neither stacks labels nor blinks.
        /// </summary>
        internal sealed class LabelFade
        {
            private enum Phase { Idle, In, Hold, Out }

            private Phase phase;
            private double start;
            private float from;

            public string Text { get; private set; }

            public float Alpha(double now)
            {
                switch (phase)
                {
                    case Phase.In:
                        return from + (1f - from) * Smooth((now - start) / FadeInSeconds);
                    case Phase.Hold:
                        return 1f;
                    case Phase.Out:
                        return from * (1f - Smooth((now - start) / FadeOutSeconds));
                    default:
                        return 0f;
                }
            }

            /// <summary>Moves to the next phase when its time is up; returns the strength now.</summary>
            public float Update(double now)
            {
                if (phase == Phase.In && now - start >= FadeInSeconds)
                {
                    start += FadeInSeconds;
                    phase = Phase.Hold;
                }
                if (phase == Phase.Hold && now - start >= HoldSeconds)
                {
                    start += HoldSeconds;
                    from = 1f;
                    phase = Phase.Out;
                }
                if (phase == Phase.Out && now - start >= FadeOutSeconds)
                    Clear();
                return Alpha(now);
            }

            public void Show(string text, double now)
            {
                from = Alpha(now);
                Text = text;
                phase = Phase.In;
                start = now;
            }

            public void Dismiss(double now)
            {
                if (phase == Phase.Idle || phase == Phase.Out)
                    return;
                from = Alpha(now);
                phase = Phase.Out;
                start = now;
            }

            public void Clear()
            {
                phase = Phase.Idle;
                Text = null;
            }

            private static float Smooth(double t)
            {
                if (t <= 0d)
                    return 0f;
                if (t >= 1d)
                    return 1f;
                return (float)(t * t * (3d - 2d * t));
            }
        }
    }
}
