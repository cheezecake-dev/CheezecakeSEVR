using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using Sandbox.Game.Gui;
using Sandbox.Game.GUI;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.OpenXR;
using VRage.Game.ObjectBuilders.Definitions;
using VRage.Library.Collections;
using VRage.Utils;
using VRageMath;
using VRageRender;
using VRageRender.Messages;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The HUD's fixed parts on the helmet's visor, curved round the head, instead of on the flat panel
    /// (<see cref="HudPanel"/>): the toolbar, the status plates, block info, the notifications, chat, the quest log and the
    /// rest, each kept on its side and in its corner but moved out toward that edge and turned to face the eyes, so the
    /// middle of the view is clear and every part is as far away as the panel was. The markers - ore, antennas, target
    /// locks, the selection box - and the crosshair stay on the flat panel, where <see cref="HudCamera"/> places them so
    /// they point at what they mark.
    /// <para>
    /// The parts are the game's own drawing, not redrawn. On the game thread each part's draw is bracketed by two render
    /// messages that draw nothing (a DrawString of scale 0, whose text is a string instance only this class has; the
    /// renderer returns at once from a string of scale 0, so a tag that is never taken out is harmless): the HUD screen's
    /// controls by which of its fields they are (MyGuiScreenBase.DrawElements and DrawControls call each control's Draw
    /// through <see cref="DrawControl"/>), the stat controls (the HUD definition's status plates and icons) and the gravity
    /// indicator by the corner their definition aligns them to, the notifications, and the voice chat, objective line and
    /// performance warning. Before the renderer draws the GUI (<see cref="EyeRenderer"/>, MyRender11.RenderMainSprites),
    /// <see cref="Split"/> takes the messages between each pair of tags out of the main sprite queue, in their order;
    /// the rest is drawn into the panel. Each part is drawn by the game's sprite renderer, exactly where the GUI
    /// has it, into its own area of one visor image, which goes to the headset as one composition layer a part: a piece
    /// of a cylinder round the head (XR_KHR_composition_layer_cylinder) or, without it, a flat quad facing the eyes;
    /// head-locked in the VIEW space at <see cref="VRSettings.HudDistance"/>, in the GUI's own pixels, sampled once by the
    /// runtime.
    /// </para>
    /// <para>
    /// What a part does not draw is not on the visor: the status bars the wrist has taken (<see cref="WristStatus"/>) and
    /// the toolbar left out on foot (<see cref="HudToolbar"/>) draw nothing, and the speed dial the wrist moves in is
    /// where it moved it. It fails safe: tags that do not pair, or a part with no layer or no room in the visor image,
    /// leave that frame's HUD on the flat panel as without the visor; after <see cref="FaultsToStop"/> such frames, or
    /// any error, or a frame the runtime refuses, the whole HUD stays on the flat panel for the rest of the run. The
    /// desktop window shows the whole HUD flat. The setting "HUD on the visor" off, or RenderDebug HudVisor=0:
    /// all of it on the flat panel.
    /// </para>
    /// </summary>
    internal static class HudVisor
    {
        /// <summary>Debug: the whole HUD back on the flat panel (<see cref="RenderDebug"/> HudVisor=0).</summary>
        public static bool Off { get; set; }

        /// <summary>Debug: the parts on flat quads facing the eyes even where cylinder layers are offered (<see cref="RenderDebug"/> HudVisorCylinder=0).</summary>
        public static bool NoCylinder { get; set; }

        /// <summary>
        /// How far out a part is moved: the corner or edge it belongs to (its anchor) goes this many times as far from
        /// straight ahead as the flat panel has it, but not past <see cref="SideMost"/>, <see cref="UpMost"/> and
        /// <see cref="DownMost"/>; the part moves with its anchor. Parts of one anchor keep their places to each other,
        /// and the outer ones move farther, so nothing that was apart on the panel comes together.
        /// </summary>
        private const float Push = 1.25f;

        /// <summary>
        /// Degrees from straight ahead an anchor is moved out to at most. Sideways 30: about as far as the eyes turn before
        /// the head turns instead, and inside the middle of a Quest 3's view, where its lenses are sharp. Up 20: looking up
        /// tires sooner. Down 25: the eyes rest a little below level, and looking down is easier than up. A wider panel
        /// (<see cref="VRSettings.HudWidthDegrees"/>) whose edges are already past these is not moved.
        /// </summary>
        private const float SideMost = 30f, UpMost = 20f, DownMost = 25f;

        /// <summary>Pixels of clear edge kept round a part's drawing in the visor image, and between parts.</summary>
        private const int Pad = 4, Gap = 2;

        /// <summary>The visor image's side, pixels (less where the runtime's images are smaller). The GUI is 1920x1080.</summary>
        private const int ImageSide = 2048;

        /// <summary>Frames whose HUD has to go on the flat panel (<see cref="Fault"/>) before it stays there for the run.</summary>
        private const int FaultsToStop = 3;

        /// <summary>The layers each frame has before the visor's: the eyes, the external view and the flat panel.</summary>
        private const int OtherLayers = 3;

        private const string DefaultTarget = "DefaultOffscreenTarget";

        private static volatile bool failed;

        /// <summary>Where a part of the HUD sits: its side or corner. Notifications have their own, at the bottom.</summary>
        private enum Zone { TopLeft, Top, TopRight, Left, Right, BottomLeft, Bottom, BottomRight, Notifications }

        private const int ZoneCount = 9;

        private static readonly string[] Names =
        {
            "top left", "top", "top right", "left", "right", "bottom left", "bottom", "bottom right", "notifications",
        };

        /// <summary>Each zone's anchor, as a fraction of the GUI from its top left: the corner or edge it is moved out from.</summary>
        private static readonly Vector2[] Anchors =
        {
            new Vector2(0f, 0f), new Vector2(0.5f, 0f), new Vector2(1f, 0f),
            new Vector2(0f, 0.5f), new Vector2(1f, 0.5f),
            new Vector2(0f, 1f), new Vector2(0.5f, 1f), new Vector2(1f, 1f),
            new Vector2(0.5f, 1f),
        };

        /// <summary>The HUD screen's controls that are parts, by field, and where each is (RecreateControls places them).</summary>
        private static readonly (string Field, Zone Zone)[] ControlFields =
        {
            ("m_toolbarControl", Zone.Bottom),
            ("m_DPadControl", Zone.Bottom),
            ("m_rotatingWheelControl", Zone.Bottom),
            ("m_rotatingWheelLabel", Zone.Bottom),
            ("m_blocksLeft", Zone.BottomLeft),
            ("m_chatControl", Zone.Left),
            ("m_contextHelp", Zone.Right),
            ("m_blockInfo", Zone.Right),
            ("m_questlogsAreaPanel", Zone.TopLeft),
            ("m_questlogControl", Zone.TopLeft),
            ("m_buffInfoControl", Zone.TopLeft),
            ("m_cameraInfoMultilineControl", Zone.TopLeft),
            ("m_buildModeLabel", Zone.Top),
        };

        // ---- Game thread (and the HUD's draw task, MyGuiScreenHudSpace.DrawAsync) ----

        /// <summary>In MyGuiScreenHudSpace.Draw, in its DrawAsync, and the zone open on this thread (its index + 1; 0 for none).</summary>
        [ThreadStatic] private static int inHud, inAsync, open;

        /// <summary>The HUD's parts are being tagged: set at the start of each HUD draw.</summary>
        private static volatile bool tagging;

        private static bool hooked;
        private static int tagFont, tagged, stateKey;

        /// <summary>The tags' texts: one instance a zone to start it, one to end any. Only ever compared by reference.</summary>
        private static readonly string[] startTags = new string[ZoneCount];
        private static string endTag;

        private static Action<MyRenderMessageBase> enqueue;
        private static FieldInfo[] controlFields;
        private static readonly MyGuiControlBase[] controls = new MyGuiControlBase[ControlFields.Length];
        private static AccessTools.FieldRef<MyStatControls, MyObjectBuilder_StatControls> statDefinition;
        private static AccessTools.FieldRef<MyHudControlGravityIndicator, MyGuiDrawAlignEnum> gravityAlign;

        // ---- Render thread ----

        /// <summary>A part of the HUD this frame: its messages, where they draw in the GUI, and where it is in the visor image.</summary>
        private sealed class Piece
        {
            public readonly List<MySpriteDrawRenderMessage> Messages = new List<MySpriteDrawRenderMessage>();

            /// <summary>Where its drawing reaches in the GUI's pixels, as it is drawn there.</summary>
            public float Left, Top, Right, Bottom;

            /// <summary>Where its texts draw in the GUI's pixels as drawn there (kept only while <see cref="VRSettings.HudTextBacking"/> is on): the plates go behind them.</summary>
            public readonly List<RectangleF> Texts = new List<RectangleF>();

            /// <summary>Scissor rectangles pushed in it and not popped, to pop after it is drawn.</summary>
            public int Scissors;

            /// <summary>Its drawing with <see cref="Pad"/> round it, in the GUI; and where that is in the visor image (no size: not on the visor).</summary>
            public Rectangle Rect, InImage;

            /// <summary>Its place has been logged for the current settings.</summary>
            public bool Logged;

            public void Reset()
            {
                Left = Top = float.MaxValue;
                Right = Bottom = float.MinValue;
                Scissors = 0;
                Texts.Clear();
                Rect = InImage = default;
            }
        }

        private const int NoTag = -1, EndTag = -2;

        private static readonly Piece[] pieces = new Piece[ZoneCount];
        private static readonly int[] order = new int[ZoneCount];
        private static readonly XrSession.VisorPiece[] placed = new XrSession.VisorPiece[ZoneCount];
        private static int placedCount, faults;
        private static readonly HashSet<string> faultsLogged = new HashSet<string>();

        // Counted for the line logged once a minute while the visor is on (Tally): tags sent since the last HUD draw and
        // the HUD draws that sent any (game thread and the HUD's draw task); the GUI frames, those whose sprite queue had
        // tags, and those whose parts went to the headset (render thread); and the minute's last fault.
        private static int tagsSent, sentDraws;
        private static int guiFrames, foundFrames, visorFrames;
        private static long tallyFrom;
        private static string lastFault;

        /// <summary>This frame's parts have been taken out of the GUI's queue; and they are on their way to the headset.</summary>
        private static bool split, onHeadset, cylinder;

        private static Vector2I gui, imageSize;

        /// <summary>Image pixels to a GUI pixel in the parts on the visor this frame (<see cref="Pack"/>): the text size, or less where the image is full.</summary>
        private static float drawScale = 1f;
        private static float uiScale = 1f;
        private static object image;
        private static (float Distance, float Width, bool Cylinder, float Size) loggedFor;

        // The game's renderer, reached through methods made at start (its types are internal).
        private delegate void DrawSpritesCall(object renderer, object target, MyViewport bound, MyViewport full, Vector2 size);

        private static FieldInfo spritesManager, drawQueue, queueMessages;
        private static Func<object> getRenderer;
        private static Action<object> returnRenderer, scissorPop;
        private static Action<object, MyRenderMessageBase> process;
        private static Action<object, Rectangle> scissorPush;
        private static DrawSpritesCall drawSprites;
        private static Func<int, string, float, Vector2> measure;

        static HudVisor()
        {
            for (int i = 0; i < ZoneCount; i++)
                pieces[i] = new Piece();
        }

        public static void Patch(Harmony harmony)
        {
            try
            {
                Bind();
                // New instances, never interned: no other text is the same object.
                for (int z = 0; z < ZoneCount; z++)
                    startTags[z] = new string(new[] { '\u0001', (char)('A' + z) });
                endTag = new string(new[] { '\u0001', '.' });
                tagFont = (int)MyStringHash.GetOrCompute("White");
                // MyRenderProxy.EnqueueMessage: as the game's own DrawString sends, into the HUD task's recording when there is one.
                enqueue = (Action<MyRenderMessageBase>)Delegate.CreateDelegate(typeof(Action<MyRenderMessageBase>),
                    AccessTools.Method(typeof(MyRenderProxy), "EnqueueMessage", new[] { typeof(MyRenderMessageBase) }));

                Type hud = typeof(MyGuiScreenHudSpace);
                controlFields = new FieldInfo[ControlFields.Length];
                for (int i = 0; i < ControlFields.Length; i++)
                {
                    controlFields[i] = AccessTools.Field(hud, ControlFields[i].Field);
                    if (controlFields[i] == null || !typeof(MyGuiControlBase).IsAssignableFrom(controlFields[i].FieldType))
                        throw new MissingFieldException(hud.Name, ControlFields[i].Field);
                }
                statDefinition = AccessTools.FieldRefAccess<MyStatControls, MyObjectBuilder_StatControls>("m_objectBuilder");
                gravityAlign = AccessTools.FieldRefAccess<MyHudControlGravityIndicator, MyGuiDrawAlignEnum>("m_originAlign");

                var close = new HarmonyMethod(typeof(HudVisor), nameof(CloseOpened));
                harmony.Patch(AccessTools.Method(hud, nameof(MyGuiScreenHudSpace.Draw)),
                    prefix: new HarmonyMethod(typeof(HudVisor), nameof(BeforeHud)),
                    finalizer: new HarmonyMethod(typeof(HudVisor), nameof(AfterHud)));
                harmony.Patch(AccessTools.Method(hud, "DrawAsync"),
                    prefix: new HarmonyMethod(typeof(HudVisor), nameof(BeforeAsync)),
                    finalizer: new HarmonyMethod(typeof(HudVisor), nameof(AfterAsync)));
                // First, so the wrist's cut of the left plate (WristStatus.BeforeStatsDraw) is inside the part.
                harmony.Patch(AccessTools.Method(typeof(MyStatControls), nameof(MyStatControls.Draw), new[] { typeof(float), typeof(float) }),
                    prefix: new HarmonyMethod(typeof(HudVisor), nameof(OpenStats)) { priority = Priority.First }, finalizer: close);
                harmony.Patch(AccessTools.Method(typeof(MyHudControlGravityIndicator), nameof(MyHudControlGravityIndicator.Draw), new[] { typeof(float) }),
                    prefix: new HarmonyMethod(typeof(HudVisor), nameof(OpenGravity)), finalizer: close);
                harmony.Patch(AccessTools.Method(typeof(MyHudNotifications), nameof(MyHudNotifications.Draw), Type.EmptyTypes),
                    prefix: new HarmonyMethod(typeof(HudVisor), nameof(OpenNotifications)), finalizer: close);
                harmony.Patch(AccessTools.Method(hud, "DrawVoiceChat"), prefix: new HarmonyMethod(typeof(HudVisor), nameof(OpenVoiceChat)), finalizer: close);
                harmony.Patch(AccessTools.Method(hud, "DrawObjectiveLine"), prefix: new HarmonyMethod(typeof(HudVisor), nameof(OpenObjective)), finalizer: close);
                harmony.Patch(AccessTools.Method(hud, "DrawPerformanceWarning"), prefix: new HarmonyMethod(typeof(HudVisor), nameof(OpenWarning)), finalizer: close);

                var controlDraws = new HarmonyMethod(typeof(HudVisor), nameof(TagControlDraws));
                harmony.Patch(AccessTools.Method(typeof(MyGuiScreenBase), "DrawElements"), transpiler: controlDraws);
                harmony.Patch(AccessTools.Method(typeof(MyGuiScreenBase), "DrawControls"), transpiler: controlDraws);
                if (tagged == 0)
                    throw new MissingMethodException("MyGuiScreenBase.DrawElements and DrawControls draw no controls any more; the HUD's parts cannot be told apart");
                hooked = true;
                Log.Info("HUD on the visor: hooked (MyGuiScreenHudSpace.Draw and DrawAsync, MyStatControls.Draw, MyHudControlGravityIndicator.Draw, " +
                         $"MyHudNotifications.Draw, the voice chat, objective line and performance warning; {tagged} control draws in MyGuiScreenBase.DrawElements and DrawControls)");
            }
            catch (Exception e)
            {
                failed = true;
                PatchRegistry.Lost(e, "HUD on the visor: could not hook the HUD; the whole HUD stays on the flat panel");
                throw;
            }
        }

        private static void Fail(Exception e)
        {
            if (failed)
                return;
            failed = true;
            tagging = false;
            Log.Error(e, "HUD on the visor: drawing the HUD's parts on the visor failed; the whole HUD is back on the flat panel for the rest of the run");
        }

        private static void Fail(string reason)
        {
            if (failed)
                return;
            failed = true;
            tagging = false;
            Log.Warn("HUD on the visor: " + reason + "; the whole HUD is back on the flat panel for the rest of the run");
        }

        /// <summary>This frame's HUD goes on the flat panel, as without the visor; the reason is logged once, and the run's <see cref="FaultsToStop"/>th ends the visor.</summary>
        private static void Fault(string reason)
        {
            lastFault = reason;
            faults++;
            if (faults >= FaultsToStop)
                Fail(reason + $" ({faults} frames)");
            else if (faultsLogged.Add(reason))
                Log.Warn("HUD on the visor: " + reason + "; that frame's HUD is on the flat panel, as without the visor");
        }

        /// <summary>The visor is wanted: the setting, the debug switch, and nothing has failed.</summary>
        private static bool Wanted => VRSettings.HudVisor && !Off && !failed;

        /// <summary>Layers for the visor's parts: the runtime's most a frame (its maxLayerCount; ours is at most <see cref="XrSession.MostLayers"/>) less the others.</summary>
        private static int Budget => Math.Min(XrRuntime.MaxLayerCount > 0 ? XrRuntime.MaxLayerCount : XrSession.MostLayers, XrSession.MostLayers) - OtherLayers;

        // ---- Game thread: tagging the HUD's parts ----

        private static void BeforeHud(MyGuiScreenHudSpace __instance, out bool __state)
        {
            __state = true;
            inHud++;
            if (Interlocked.Exchange(ref tagsSent, 0) > 0)
                Interlocked.Increment(ref sentDraws);
            try
            {
                bool on = Wanted;
                Report(on);
                tagging = on && hooked && HeadsetShown && InPlay();
                if (!tagging)
                    return;
                for (int i = 0; i < controls.Length; i++)
                    controls[i] = controlFields[i].GetValue(__instance) as MyGuiControlBase;
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        private static void AfterHud(bool __state)
        {
            if (__state)
                inHud--;
        }

        private static void BeforeAsync(out bool __state)
        {
            __state = true;
            inAsync++;
        }

        private static void AfterAsync(bool __state)
        {
            if (__state)
                inAsync--;
        }

        /// <summary>The headset shows the picture: it is VR's (not the game's while the headset is away) and the session runs.</summary>
        private static bool HeadsetShown => StereoPatches.VRPicture && XrSession.Running;

        /// <summary>
        /// The panel is a visor: no menu has the focus (as <see cref="WorldMenu"/> decides it, so the panel the parts are
        /// taken from is the head-locked one; with a menu, a wheel or the wrist panel open it is world-locked and the HUD
        /// stays on it whole).
        /// </summary>
        private static bool InPlay()
        {
            MyGuiScreenBase focus = MyScreenManager.GetScreenWithFocus();
            return focus == null || focus is MyGuiScreenGamePlay;
        }

        /// <summary>Logs whether the parts go on the visor, and how, when that changes.</summary>
        private static void Report(bool on)
        {
            bool curved = XrRuntime.HasCylinder && !NoCylinder;
            int key = on ? (curved ? 1 : 2) : failed ? 3 : Off ? 4 : 5;
            // On, it is told once the headset's runtime is there to say how many layers it takes; a failure has its own log.
            if (key == stateKey || on && !HeadsetShown)
                return;
            stateKey = key;
            if (on)
            {
                string layers = curved ? "cylinder layers (XR_KHR_composition_layer_cylinder)"
                    : "flat layers facing the eyes" + (NoCylinder ? " (RenderDebug HudVisorCylinder=0)" : " (the runtime offers no cylinder layers)");
                Log.Info($"HUD on the visor: on, its parts on {layers}, head-locked in the VIEW space; the runtime takes up to {XrRuntime.MaxLayerCount} layers a frame, " +
                         $"{Math.Max(Budget, 0)} of them for the visor; the markers and the crosshair stay on the flat panel");
            }
            else if (key != 3)
            {
                Log.Info("HUD on the visor: off, the whole HUD on the flat panel (" + (Off ? "RenderDebug HudVisor=0" : "the setting \"HUD on the visor\" is off") + ")");
            }
        }

        /// <summary>Starts a part on this thread: its start tag, unless a part is already open (what follows belongs to that one).</summary>
        private static bool Open(int zone)
        {
            if (zone < 0 || open != 0)
                return false;
            try
            {
                Tag(startTags[zone]);
            }
            catch (Exception e)
            {
                Fail(e);
                return false;
            }
            open = zone + 1;
            return true;
        }

        private static void Close()
        {
            open = 0;
            try
            {
                Tag(endTag);
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        /// <summary>Sends a tag, as MyRenderProxy.DrawString sends a string: drawn at scale 0, it would draw nothing.</summary>
        private static void Tag(string text)
        {
            MyRenderMessageDrawString tag = MyRenderProxy.MessagePool.Get<MyRenderMessageDrawString>(MyRenderMessageEnum.DrawString);
            tag.Text = text;
            tag.FontIndex = tagFont;
            tag.ScreenCoord = Vector2.Zero;
            tag.ColorMask = new Color(0, 0, 0, 0);
            tag.ScreenScale = 0f;
            tag.ScreenMaxWidth = 0f;
            tag.TargetTexture = null;
            tag.IgnoreBounds = true;
            enqueue(tag);
            Interlocked.Increment(ref tagsSent);
        }

        private static void CloseOpened(bool __state)
        {
            if (__state)
                Close();
        }

        /// <summary>
        /// MyGuiScreenBase.DrawElements and DrawControls: each control's Draw goes through <see cref="DrawControl"/>. The
        /// call is changed in place, so the loops, labels and the rest of each method stay as they are.
        /// </summary>
        private static IEnumerable<CodeInstruction> TagControlDraws(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo draw = AccessTools.Method(typeof(MyGuiControlBase), nameof(MyGuiControlBase.Draw), new[] { typeof(float), typeof(float) });
            MethodInfo ours = AccessTools.Method(typeof(HudVisor), nameof(DrawControl));
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(draw))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = ours;
                    tagged++;
                }
                yield return instruction;
            }
        }

        /// <summary>A screen drawing one of its controls: inside the HUD's draw, a control that is a part is drawn between its tags.</summary>
        private static void DrawControl(MyGuiControlBase control, float transitionAlpha, float backgroundTransitionAlpha)
        {
            bool opened = inHud > 0 && tagging && Open(ZoneOf(control));
            try
            {
                control.Draw(transitionAlpha, backgroundTransitionAlpha);
            }
            finally
            {
                if (opened)
                    Close();
            }
        }

        private static int ZoneOf(MyGuiControlBase control)
        {
            if (control == null)
                return -1;
            for (int i = 0; i < controls.Length; i++)
            {
                if (ReferenceEquals(controls[i], control))
                    return (int)ControlFields[i].Zone;
            }
            return -1;
        }

        /// <summary>The zone of a HUD definition's alignment; the middle is not a part (it stays on the flat panel).</summary>
        private static int ZoneOf(MyGuiDrawAlignEnum align)
        {
            switch (align)
            {
                case MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP: return (int)Zone.TopLeft;
                case MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_TOP: return (int)Zone.Top;
                case MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_TOP: return (int)Zone.TopRight;
                case MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER: return (int)Zone.Left;
                case MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER: return (int)Zone.Right;
                case MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_BOTTOM: return (int)Zone.BottomLeft;
                case MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_BOTTOM: return (int)Zone.Bottom;
                case MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_BOTTOM: return (int)Zone.BottomRight;
                default: return -1;
            }
        }

        /// <summary>MyStatControls.Draw, in the HUD's draw task: the HUD definition's status plates, bars and icons, by their alignment.</summary>
        private static void OpenStats(MyStatControls __instance, out bool __state)
        {
            __state = false;
            if (inAsync == 0 || !tagging)
                return;
            MyObjectBuilder_StatControls definition = statDefinition(__instance);
            // Left-handed, the left plate is drawn mirrored on the right; it moves out with that corner.
            __state = definition != null
                && Open(WristStatus.SpeedPlateOnRight(__instance) ? (int)Zone.BottomRight : ZoneOf(definition.OriginAlign));
        }

        private static void OpenGravity(MyHudControlGravityIndicator __instance, out bool __state) =>
            __state = inHud > 0 && tagging && Open(ZoneOf(gravityAlign(__instance)));

        private static void OpenNotifications(out bool __state) => __state = inAsync > 0 && tagging && Open((int)Zone.Notifications);

        private static void OpenVoiceChat(out bool __state) => __state = inHud > 0 && tagging && Open((int)Zone.BottomLeft);

        private static void OpenObjective(out bool __state) => __state = inHud > 0 && tagging && Open((int)Zone.Top);

        private static void OpenWarning(out bool __state) => __state = inHud > 0 && tagging && Open((int)Zone.TopRight);

        // ---- Render thread: taking the parts out, and drawing them ----

        /// <summary>
        /// Before the GUI is drawn into the panel: takes this frame's parts out of the main sprite queue, if they can all go
        /// on the visor, and the tags out in any case.
        /// <para>
        /// No headset frame is open yet: MyRender11.FullDraw draws the GUI (RenderMainSprites, here) before the scene
        /// (DrawScene, where <see cref="EyeRenderer"/> begins the headset frame), and a frame without a scene is begun only
        /// when the GUI is shown (ConsumeMainSprites). So the parts are taken while the headset's session runs, and
        /// <see cref="Show"/>, in the headset frame, puts them on the visor or draws them back into the panel.
        /// </para>
        /// </summary>
        /// <param name="guiSize">The GUI's size, pixels.</param>
        /// <param name="scale">The renderer's main viewport scale, by which sprites that keep to bounds are drawn.</param>
        public static void Split(Vector2I guiSize, float scale)
        {
            Discard();
            string refused = XrSession.VisorRefused;
            if (refused != null)
            {
                XrSession.VisorRefused = null;
                Fail("the headset's runtime refused a frame with the HUD's parts in it (" + refused + "); it was sent again without them");
            }
            bool wanted = hooked && Wanted && HeadsetShown;
            Tally(wanted);
            if (!hooked || failed)
                return;
            try
            {
                var queue = (IDictionary)drawQueue.GetValue(spritesManager.GetValue(null));
                object data = queue?[DefaultTarget];
                if (data == null)
                    return;
                var messages = (MyList<MySpriteDrawRenderMessage>)queueMessages.GetValue(data);
                gui = guiSize;
                uiScale = scale;
                if (!Scan(messages, wanted, out string fault))
                    return;
                foundFrames++;
                bool take = false;
                if (fault == null && wanted)
                    take = Pack(out fault);
                if (fault != null)
                    Fault(fault);
                Take(messages, take);
                split = take;
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        /// <summary>
        /// Once a minute while the visor is on (<paramref name="on"/>), what it did in that minute, so a visor that takes
        /// nothing shows in the log: the HUD draws that sent tags, the GUI frames whose sprite queue had them, the frames
        /// whose parts went to the headset, and the minute's last fault. Counts this GUI frame.
        /// </summary>
        private static void Tally(bool on)
        {
            long now = Stopwatch.GetTimestamp();
            if (on && tallyFrom != 0 && now - tallyFrom >= 60L * Stopwatch.Frequency)
            {
                int sent = Interlocked.Exchange(ref sentDraws, 0);
                Log.Info($"HUD on the visor over the last minute: the HUD sent tags in {sent} of its draws; {foundFrames} of {guiFrames} GUI frames had them " +
                         $"in the sprite queue; the parts went on the visor in {visorFrames} frames; " + (lastFault == null ? "no fault" : "the last fault: " + lastFault));
                tallyFrom = 0;
            }
            if (!on || tallyFrom == 0)
            {
                tallyFrom = on ? now : 0;
                guiFrames = foundFrames = visorFrames = 0;
                Interlocked.Exchange(ref sentDraws, 0);
                lastFault = null;
            }
            if (on)
                guiFrames++;
        }

        private static int TagOf(MySpriteDrawRenderMessage message)
        {
            if (message.MessageType != MyRenderMessageEnum.DrawString || !(message is MyRenderMessageDrawString text) || text.ScreenScale != 0f)
                return NoTag;
            string s = text.Text;
            if (ReferenceEquals(s, endTag))
                return EndTag;
            for (int z = 0; z < ZoneCount; z++)
            {
                if (ReferenceEquals(s, startTags[z]))
                    return z;
            }
            return NoTag;
        }

        /// <summary>
        /// Finds the tags, checks that each start has its end before the next start, and (when <paramref name="measure"/>)
        /// works out where each part's drawing reaches. Changes nothing in the queue. False when there are no tags.
        /// </summary>
        private static bool Scan(MyList<MySpriteDrawRenderMessage> messages, bool measure, out string fault)
        {
            fault = null;
            foreach (Piece piece in pieces)
                piece.Reset();
            int zone = -1;
            bool any = false;
            for (int i = 0; i < messages.Count; i++)
            {
                MySpriteDrawRenderMessage message = messages[i];
                int tag = TagOf(message);
                if (tag == NoTag)
                {
                    if (zone >= 0 && measure)
                        Grow(pieces[zone], message);
                    continue;
                }
                any = true;
                if (tag == EndTag)
                {
                    if (zone < 0 && fault == null)
                        fault = "a part's end tag came without its start";
                    zone = -1;
                }
                else
                {
                    if (zone >= 0 && fault == null)
                        fault = "a part's start tag came inside another part";
                    zone = tag;
                }
            }
            if (zone >= 0 && fault == null)
                fault = "a part's start tag had no end";
            return any;
        }

        /// <summary>
        /// Takes the tags out of the queue, and with <paramref name="take"/> the messages of each part on the visor into the
        /// part, in their order; the rest stay where they were. A taken message is disposed after it is drawn
        /// (<see cref="Discard"/>), as the queue's own are.
        /// </summary>
        private static void Take(MyList<MySpriteDrawRenderMessage> messages, bool take)
        {
            int zone = -1, kept = 0, count = messages.Count;
            for (int i = 0; i < count; i++)
            {
                MySpriteDrawRenderMessage message = messages[i];
                int tag = TagOf(message);
                if (tag != NoTag)
                {
                    zone = tag == EndTag ? -1 : tag;
                    message.Dispose();
                    continue;
                }
                if (take && zone >= 0 && pieces[zone].InImage.Width > 0 && Moves(pieces[zone], message))
                {
                    pieces[zone].Messages.Add(message);
                    continue;
                }
                messages[kept++] = message;
            }
            for (int i = kept; i < count; i++)
                messages[i] = null;
            messages.SetSize(kept);
        }

        /// <summary>Whether a message in a part goes with it: what draws, and its scissor rectangles (a pop only for a push in it).</summary>
        private static bool Moves(Piece piece, MySpriteDrawRenderMessage message)
        {
            switch (message.MessageType)
            {
                case MyRenderMessageEnum.DrawSprite:
                case MyRenderMessageEnum.DrawSpriteExt:
                case MyRenderMessageEnum.DrawSpriteAtlas:
                case MyRenderMessageEnum.DrawString:
                case MyRenderMessageEnum.DrawStringAligned:
                case MyRenderMessageEnum.DrawVideo:
                    return true;
                case MyRenderMessageEnum.SpriteScissorPush:
                    piece.Scissors++;
                    return true;
                case MyRenderMessageEnum.SpriteScissorPop:
                    if (piece.Scissors == 0)
                        return false;
                    piece.Scissors--;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Widens a part to what a message draws, in the GUI's pixels, as MySpritesRenderer.ProcessDrawMessage places it.</summary>
        private static void Grow(Piece piece, MySpriteDrawRenderMessage message)
        {
            switch (message)
            {
                case MyRenderMessageDrawSprite sprite:
                {
                    RectangleF d = sprite.DestinationRectangle;
                    if (sprite.Rotation != 0f || sprite.RotationSpeed != 0f)
                        Around(piece, d.Center, 0.5f * (float)Math.Sqrt(d.Width * d.Width + d.Height * d.Height), sprite.IgnoreBounds);
                    else
                        Add(piece, d.X, d.Y, d.X + d.Width, d.Y + d.Height, sprite.IgnoreBounds);
                    break;
                }
                case MyRenderMessageDrawSpriteExt sprite:
                {
                    RectangleF d = sprite.DestinationRectangle;
                    if (sprite.RightVector == Vector2.UnitX)
                    {
                        Add(piece, d.X, d.Y, d.X + d.Width, d.Y + d.Height, sprite.IgnoreBounds);
                        break;
                    }
                    // Turned round its origin: anywhere within its farthest corner.
                    float far = 0f;
                    for (int corner = 0; corner < 4; corner++)
                        far = Math.Max(far, Vector2.Distance(sprite.Origin, new Vector2(corner % 2 == 0 ? d.X : d.X + d.Width, corner < 2 ? d.Y : d.Y + d.Height)));
                    Around(piece, sprite.Origin, far, sprite.IgnoreBounds);
                    break;
                }
                case MyRenderMessageDrawSpriteAtlas sprite:
                {
                    float halfX = sprite.HalfSize.X * sprite.Scale.X, halfY = sprite.HalfSize.Y * sprite.Scale.Y;
                    var centre = new Vector2(sprite.Position.X * sprite.Scale.X, sprite.Position.Y * sprite.Scale.Y);
                    if (sprite.RightVector == Vector2.UnitX)
                        Add(piece, centre.X - halfX, centre.Y - halfY, centre.X + halfX, centre.Y + halfY, sprite.IgnoreBounds);
                    else
                        Around(piece, centre, (float)Math.Sqrt(halfX * halfX + halfY * halfY), sprite.IgnoreBounds);
                    break;
                }
                case MyRenderMessageDrawStringAligned text:
                {
                    if (string.IsNullOrEmpty(text.Text))
                        break;
                    // Aligned within TextureWidthInPx of its point: anywhere from its left to that width's right.
                    Vector2 size = measure(text.FontIndex, text.Text, text.ScreenScale);
                    float slack = TextSlack(size), x = text.ScreenCoord.X, y = text.ScreenCoord.Y;
                    Add(piece, x + Math.Min(0f, text.TextureWidthInPx - size.X) - slack, y - slack,
                        x + Math.Max(size.X, text.TextureWidthInPx) + slack, y + size.Y + slack, text.IgnoreBounds);
                    float shown = ShownWidth(size.X, text.ScreenMaxWidth);
                    float start = text.Alignment == MyRenderTextAlignmentEnum.Align_Right ? text.TextureWidthInPx - shown
                        : text.Alignment == MyRenderTextAlignmentEnum.Align_Center ? (text.TextureWidthInPx - shown) / 2f : 0f;
                    Plate(piece, text, x + start, y, x + start + shown, y + size.Y);
                    break;
                }
                case MyRenderMessageDrawString text:
                {
                    if (string.IsNullOrEmpty(text.Text) || text.ScreenScale == 0f)
                        break;
                    Vector2 size = measure(text.FontIndex, text.Text, text.ScreenScale);
                    float slack = TextSlack(size), x = text.ScreenCoord.X, y = text.ScreenCoord.Y;
                    Add(piece, x - slack, y - slack, x + size.X + slack, y + size.Y + slack, text.IgnoreBounds);
                    Plate(piece, text, x, y, x + ShownWidth(size.X, text.ScreenMaxWidth), y + size.Y);
                    break;
                }
                case MyRenderMessageDrawVideo video:
                {
                    Rectangle r = video.Rectangle;
                    Add(piece, r.X, r.Y, r.X + r.Width, r.Y + r.Height, video.IgnoreBounds);
                    break;
                }
            }
        }

        /// <summary>Room round a measured text for glyphs reaching past the font's box: a fifth of its height, at most 16 px.</summary>
        private static float TextSlack(Vector2 size) => Math.Min(0.2f * size.Y, 16f);

        /// <summary>A text's width as drawn: the font cuts a line that reaches its maximum width short with an ellipsis.</summary>
        private static float ShownWidth(float width, float maxWidth) => maxWidth > 0f && maxWidth < width ? maxWidth : width;

        /// <summary>Keeps where a text draws, for the plate behind it (<see cref="DrawPlates"/>).</summary>
        private static void Plate(Piece piece, MyRenderMessageDrawString text, float left, float top, float right, float bottom)
        {
            if (VRSettings.HudTextBacking <= 0f || text.ColorMask.A == 0 || right <= left)
                return;
            Scale(ref left, ref top, ref right, ref bottom, text.IgnoreBounds);
            piece.Texts.Add(new RectangleF(left, top, right - left, bottom - top));
        }

        private static void Around(Piece piece, Vector2 centre, float radius, bool ignoreBounds) =>
            Add(piece, centre.X - radius, centre.Y - radius, centre.X + radius, centre.Y + radius, ignoreBounds);

        /// <summary>A sprite's rectangle, in its message's numbers, into the GUI's pixels: one keeping to bounds is scaled into the main viewport.</summary>
        private static void Add(Piece piece, float left, float top, float right, float bottom, bool ignoreBounds)
        {
            Scale(ref left, ref top, ref right, ref bottom, ignoreBounds);
            piece.Left = Math.Min(piece.Left, left);
            piece.Top = Math.Min(piece.Top, top);
            piece.Right = Math.Max(piece.Right, right);
            piece.Bottom = Math.Max(piece.Bottom, bottom);
        }

        private static void Scale(ref float left, ref float top, ref float right, ref float bottom, bool ignoreBounds)
        {
            if (ignoreBounds || uiScale == 1f)
                return;
            float offsetX = gui.X * (1f - uiScale) / 2f, offsetY = gui.Y * (1f - uiScale) / 2f;
            left = offsetX + left * uiScale;
            right = offsetX + right * uiScale;
            top = offsetY + top * uiScale;
            bottom = offsetY + bottom * uiScale;
        }

        /// <summary>
        /// Finds each part with something on screen a place in the visor image (shelves, tallest first). False, with the
        /// reason, when they do not all have a layer and a place: then none of them goes on the visor this frame.
        /// </summary>
        private static bool Pack(out string fault)
        {
            fault = null;
            int count = 0;
            for (int z = 0; z < ZoneCount; z++)
            {
                Piece piece = pieces[z];
                if (piece.Right <= piece.Left || piece.Bottom <= piece.Top)
                    continue;
                int left = Math.Max((int)Math.Floor(piece.Left) - Pad, 0), top = Math.Max((int)Math.Floor(piece.Top) - Pad, 0);
                int right = Math.Min((int)Math.Ceiling(piece.Right) + Pad, gui.X), bottom = Math.Min((int)Math.Ceiling(piece.Bottom) + Pad, gui.Y);
                if (right <= left || bottom <= top)
                    continue;
                piece.Rect = new Rectangle(left, top, right - left, bottom - top);
                int at = count++;
                while (at > 0 && pieces[order[at - 1]].Rect.Height < piece.Rect.Height)
                {
                    order[at] = order[at - 1];
                    at--;
                }
                order[at] = z;
            }
            if (count == 0)
                return false;
            if (count > Budget)
            {
                fault = $"the HUD has {count} parts and the runtime has layers for {Math.Max(Budget, 0)}";
                return false;
            }

            // Drawn at the text size, so a larger part is sharp, not sampled up; where that does not fit, as large as does.
            for (float scale = Math.Max(1f, VRSettings.HudTextSize); ; scale = Math.Max(1f, scale * 0.92f))
            {
                if (Shelve(count, scale, out fault))
                {
                    drawScale = scale;
                    return true;
                }
                if (scale <= 1f)
                    return false;
            }
        }

        /// <summary>Tries each part, <paramref name="scale"/> times its GUI size, on shelves in the visor image.</summary>
        private static bool Shelve(int count, float scale, out string fault)
        {
            fault = null;
            int maxWidth = XrRuntime.MaxImageWidth > 0 ? XrRuntime.MaxImageWidth : ImageSide;
            int maxHeight = XrRuntime.MaxImageHeight > 0 ? XrRuntime.MaxImageHeight : ImageSide;
            imageSize = new Vector2I(Math.Min(ImageSide, maxWidth), Math.Min(ImageSide, maxHeight));
            int x = Gap, y = Gap, shelf = 0;
            for (int i = 0; i < count; i++)
            {
                Piece piece = pieces[order[i]];
                int width = (int)Math.Ceiling(piece.Rect.Width * scale), height = (int)Math.Ceiling(piece.Rect.Height * scale);
                if (x + width + Gap > imageSize.X)
                {
                    x = Gap;
                    y += shelf + Gap;
                    shelf = 0;
                }
                if (x + width + Gap > imageSize.X || y + height + Gap > imageSize.Y)
                {
                    fault = $"the {Names[order[i]]} part ({width}x{height} px at {scale:0.00}x) has no room in the visor image";
                    for (int j = 0; j < ZoneCount; j++)
                        pieces[j].InImage = default;
                    return false;
                }
                piece.InImage = new Rectangle(x, y, width, height);
                x += width + Gap;
                shelf = Math.Max(shelf, height);
            }
            return true;
        }

        /// <summary>
        /// The panel is shown: draws the parts into the visor image and hands it to the headset when they go on the visor
        /// (<paramref name="onVisor"/>: a headset frame, the panel head-locked), and otherwise back into the panel, where
        /// they were. On a failure they go back into the panel too.
        /// </summary>
        /// <returns>The parts are on the visor this frame (<see cref="Attach"/>).</returns>
        public static bool Show(object panel, bool onVisor)
        {
            if (!split)
                return false;
            if (!onVisor || !Wanted)
            {
                DrawBack(panel);
                return false;
            }
            try
            {
                DrawPlates();
                image = EyeRenderer.Borrow("VR HUD visor", imageSize);
                EyeRenderer.Clear(image);
                foreach (Piece piece in pieces)
                {
                    if (piece.Messages.Count > 0)
                        Draw(image, piece, true);
                }
                SharpDX.Direct3D11.Texture2D swapImage = XrSession.AcquireVisorImage(imageSize.X, imageSize.Y);
                try
                {
                    EyeRenderer.CopyToImage(image, swapImage);
                }
                finally
                {
                    XrSession.ReleaseVisorImage();
                }
                Place();
                onHeadset = true;
                visorFrames++;
                return true;
            }
            catch (Exception e)
            {
                Fail(e);
                DrawBack(panel);
                return false;
            }
        }

        /// <summary>Room, pixels, a plate keeps round the text it backs; plates this close are one.</summary>
        private const float PlatePad = 4f;

        private const string PlateTexture = "Textures\\GUI\\Blank.dds";

        /// <summary>
        /// Puts a dark plate behind each block of text, first in its part so everything else draws over it. Texts whose plates
        /// touch share one, so overlapping lines do not darken each other twice. The plates are part of the part's messages
        /// and go when it does (<see cref="Discard"/>); the desktop window's copy of the panel has them too.
        /// </summary>
        private static void DrawPlates()
        {
            float opacity = VRSettings.HudTextBacking;
            if (opacity <= 0f)
                return;
            var color = new Color(0, 0, 0, (int)Math.Round(opacity * 255f));
            foreach (Piece piece in pieces)
            {
                if (piece.Messages.Count == 0 || piece.Texts.Count == 0)
                    continue;
                var plates = new List<RectangleF>(piece.Texts.Count);
                foreach (RectangleF text in piece.Texts)
                {
                    var plate = new RectangleF(text.X - PlatePad, text.Y - PlatePad, text.Width + 2f * PlatePad, text.Height + 2f * PlatePad);
                    for (int i = 0; i < plates.Count; i++)
                    {
                        if (plate.X >= plates[i].Right || plates[i].X >= plate.Right || plate.Y >= plates[i].Bottom || plates[i].Y >= plate.Bottom)
                            continue;
                        plate = Union(plate, plates[i]);
                        plates.RemoveAt(i);
                        i = -1; // the larger plate may now reach others
                    }
                    plates.Add(plate);
                }
                for (int i = plates.Count - 1; i >= 0; i--)
                {
                    MyRenderMessageDrawSprite sprite = MyRenderProxy.MessagePool.Get<MyRenderMessageDrawSprite>(MyRenderMessageEnum.DrawSprite);
                    sprite.Texture = PlateTexture;
                    sprite.DestinationRectangle = plates[i];
                    sprite.SourceRectangle = null;
                    sprite.Color = color;
                    sprite.WaitTillLoaded = true;
                    sprite.TargetTexture = null;
                    sprite.MaskTexture = null;
                    sprite.RotationSpeed = 0f;
                    sprite.Rotation = 0f;
                    sprite.IgnoreBounds = true;
                    piece.Messages.Insert(0, sprite);
                }
            }
        }

        private static RectangleF Union(RectangleF a, RectangleF b)
        {
            float left = Math.Min(a.X, b.X), top = Math.Min(a.Y, b.Y);
            return new RectangleF(left, top, Math.Max(a.X + a.Width, b.X + b.Width) - left, Math.Max(a.Y + a.Height, b.Y + b.Height) - top);
        }

        /// <summary>The parts on the visor this frame, into the headset's panel layer.</summary>
        public static void Attach(ref XrSession.Panel panel)
        {
            if (!onHeadset)
                return;
            panel.Visor = placed;
            panel.VisorCount = placedCount;
            panel.VisorCylinder = cylinder;
        }

        /// <summary>After the headset has its copy of the panel: the parts on the visor drawn into it too, for the desktop window.</summary>
        public static void Finish(object panel)
        {
            if (onHeadset)
                DrawBack(panel);
        }

        /// <summary>Lets go of this frame's parts: their messages, and the visor image.</summary>
        public static void Discard()
        {
            foreach (Piece piece in pieces)
            {
                foreach (MySpriteDrawRenderMessage message in piece.Messages)
                    message.Dispose();
                piece.Messages.Clear();
            }
            if (image != null)
                EyeRenderer.Release(image);
            image = null;
            split = onHeadset = false;
            placedCount = 0;
        }

        /// <summary>The parts drawn into the panel, as the GUI's own pass draws them there.</summary>
        private static void DrawBack(object panel)
        {
            try
            {
                foreach (Piece piece in pieces)
                {
                    if (piece.Messages.Count > 0)
                        Draw(panel, piece, false);
                }
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        /// <summary>
        /// Draws a part with the game's sprite renderer: into the panel where the GUI has it, or (<paramref name="inImage"/>)
        /// moved to its place in the visor image. The renderer is given the GUI's whole size, shifted, so every sprite and
        /// scissor rectangle lands where it would, less the shift; at UI scale 1 a scissor of the part's own rectangle
        /// keeps it inside its place (the scissors of the part itself are cut by it).
        /// </summary>
        private static void Draw(object target, Piece piece, bool inImage)
        {
            object renderer = getRenderer();
            try
            {
                bool clip = inImage && uiScale == 1f;
                if (clip)
                    scissorPush(renderer, piece.Rect);
                foreach (MySpriteDrawRenderMessage message in piece.Messages)
                    process(renderer, message);
                for (int i = piece.Scissors + (clip ? 1 : 0); i > 0; i--)
                    scissorPop(renderer);
                // The renderer is given the GUI's size to write into its shaders and a viewport drawScale times larger.
                float k = inImage ? drawScale : 1f;
                float shiftX = inImage ? piece.InImage.X - piece.Rect.X * k : 0f, shiftY = inImage ? piece.InImage.Y - piece.Rect.Y * k : 0f;
                var full = new MyViewport(shiftX, shiftY, gui.X * k, gui.Y * k);
                var bound = new MyViewport(shiftX + gui.X * k * (1f - uiScale) / 2f, shiftY + gui.Y * k * (1f - uiScale) / 2f, gui.X * k * uiScale, gui.Y * k * uiScale);
                drawSprites(renderer, target, bound, full, new Vector2(gui.X, gui.Y));
            }
            finally
            {
                returnRenderer(renderer);
            }
        }

        /// <summary>
        /// Where each part on the visor goes, round the head in the VIEW space. Its middle is where the flat panel shows it,
        /// seen from the head, moved out with its anchor (<see cref="Push"/>), at <see cref="VRSettings.HudDistance"/>, and
        /// turned to face the eyes both ways. It spans no more than the angles it spans on the flat panel, so it is the size
        /// it was there, and parts that did not overlap there do not overlap here: the anchors only move apart.
        /// </summary>
        private static void Place()
        {
            float distance = VRSettings.HudDistance;
            float widthDegrees = VRSettings.HudWidthDegrees;
            // Tangent of the angle a pixel spans in the middle of the flat panel, seen from the head.
            float perPixel = 2f * (float)Math.Tan(MathHelper.ToRadians(widthDegrees) / 2f) / gui.X;
            cylinder = XrRuntime.HasCylinder && !NoCylinder;
            if (loggedFor != (distance, widthDegrees, cylinder, VRSettings.HudTextSize))
            {
                loggedFor = (distance, widthDegrees, cylinder, VRSettings.HudTextSize);
                foreach (Piece piece in pieces)
                    piece.Logged = false;
            }

            placedCount = 0;
            for (int z = 0; z < ZoneCount; z++)
            {
                Piece piece = pieces[z];
                if (piece.Messages.Count == 0)
                    continue;
                // The anchor's row is taken down the middle, so the parts along one edge all move by as much.
                float anchorAcross = Across(Anchors[z].X * gui.X, perPixel), anchorUp = Up(gui.X / 2f, Anchors[z].Y * gui.Y, perPixel);
                float centreX = piece.Rect.X + piece.Rect.Width / 2f;
                float flatAcross = Across(centreX, perPixel), flatUp = Up(centreX, piece.Rect.Y + piece.Rect.Height / 2f, perPixel);
                float across = flatAcross + Out(anchorAcross, SideMost) - anchorAcross;
                float up = flatUp + Out(anchorUp, anchorUp >= 0f ? UpMost : DownMost) - anchorUp;
                // The text size: the part grows away from the point of it nearest its anchor, which stays where it was.
                float size = VRSettings.HudTextSize;
                float pivotX = Math.Max(piece.Rect.Left, Math.Min(piece.Rect.Right, Anchors[z].X * gui.X));
                float pivotY = Math.Max(piece.Rect.Top, Math.Min(piece.Rect.Bottom, Anchors[z].Y * gui.Y));
                float pivotAcross = Across(pivotX, perPixel) + across - flatAcross, pivotUp = Up(pivotX, pivotY, perPixel) + up - flatUp;
                across = pivotAcross + (across - pivotAcross) * size;
                up = pivotUp + (up - pivotUp) * size;
                // The flat panel is seen at a slant toward its sides, narrower than it is high there; facing the eyes, the
                // part keeps square pixels, at the smaller of the two, so it is nowhere larger than it was.
                float wide = Across(piece.Rect.Right, perPixel) - Across(piece.Rect.Left, perPixel);
                float high = Up(centreX, piece.Rect.Top, perPixel) - Up(centreX, piece.Rect.Bottom, perPixel);
                float perPixelHere = Math.Min(wide / piece.Rect.Width, high / piece.Rect.Height);
                wide = piece.Rect.Width * perPixelHere * size;
                high = piece.Rect.Height * perPixelHere * size;

                Matrix turn = Matrix.CreateRotationX(up) * Matrix.CreateRotationY(-across);
                Quaternion rotation = Quaternion.CreateFromRotationMatrix(turn);
                Vector3 middle = Vector3.Transform(new Vector3(0f, 0f, -distance), turn);
                // A cylinder's width is along its curve; a flat layer's, and either's height, is straight across.
                float width = cylinder ? wide * distance : 2f * distance * (float)Math.Tan(wide / 2f);
                float height = 2f * distance * (float)Math.Tan(high / 2f);
                placed[placedCount++] = new XrSession.VisorPiece
                {
                    Rect = new XrRect2Di { X = piece.InImage.X, Y = piece.InImage.Y, Width = piece.InImage.Width, Height = piece.InImage.Height },
                    Orientation = new XrQuaternionf { X = rotation.X, Y = rotation.Y, Z = rotation.Z, W = rotation.W },
                    Middle = new XrVector3f { X = middle.X, Y = middle.Y, Z = middle.Z },
                    Radius = distance,
                    Width = width,
                    Height = height,
                };

                if (!piece.Logged)
                {
                    piece.Logged = true;
                    Log.Info($"HUD on the visor: the {Names[z]} part, {piece.Rect.Width}x{piece.Rect.Height} px of the GUI at ({piece.Rect.X}, {piece.Rect.Y}), " +
                             $"{MathHelper.ToDegrees(wide):0.0} x {MathHelper.ToDegrees(high):0.0} degrees on a {(cylinder ? "cylinder" : "flat")} layer " +
                             $"{distance:0.00} m out; its middle {MathHelper.ToDegrees(across):0.0} degrees right and {MathHelper.ToDegrees(up):0.0} up " +
                             $"(on the flat panel {MathHelper.ToDegrees(flatAcross):0.0} and {MathHelper.ToDegrees(flatUp):0.0})");
                }
            }
        }

        /// <summary>The angle from straight ahead to a column of the flat panel, radians, right positive.</summary>
        private static float Across(float x, float perPixel) => (float)Math.Atan((x - gui.X / 2f) * perPixel);

        /// <summary>The angle up from level to a point of the flat panel, radians: less toward its sides, which are farther away.</summary>
        private static float Up(float x, float y, float perPixel)
        {
            float across = (x - gui.X / 2f) * perPixel;
            return (float)Math.Atan((gui.Y / 2f - y) * perPixel / Math.Sqrt(1f + across * across));
        }

        /// <summary>An anchor's angle moved out: <see cref="Push"/> times as far, up to <paramref name="mostDegrees"/>, never in.</summary>
        private static float Out(float angle, float mostDegrees)
        {
            float from = Math.Abs(angle);
            float to = Math.Max(from, Math.Min(from * Push, MathHelper.ToRadians(mostDegrees)));
            return angle < 0f ? -to : to;
        }

        // ---- The game's renderer ----

        /// <summary>Finds the GUI's sprite queue, and makes the calls into the renderer's internal sprite renderer and fonts.</summary>
        private static void Bind()
        {
            Assembly render11 = typeof(MyDX11Render).Assembly;
            Type managers = render11.GetType("VRage.Render11.Common.MyManagers", throwOnError: true);
            Type render = render11.GetType("VRageRender.MyRender11", throwOnError: true);
            Type sprites = render11.GetType("VRage.Render11.Sprites.MySpritesRenderer", throwOnError: true);
            Type messageData = render11.GetType("VRage.Render11.Sprites.MySpriteMessageData", throwOnError: true);
            Type rtv = render11.GetType("VRage.Render11.Resources.IRtvBindable", throwOnError: true);
            Type blend = render11.GetType("VRage.Render11.Resources.IBlendState", throwOnError: true);
            Type context = render11.GetType("VRage.Render11.RenderContext.MyRenderContext", throwOnError: true);
            Type renderFont = render11.GetType("VRageRender.MyRenderFont", throwOnError: true);

            // MyManagers.SpritesManager.m_drawQueue[target].Messages: the sprite messages waiting to be drawn, by target.
            spritesManager = AccessTools.Field(managers, "SpritesManager");
            drawQueue = AccessTools.Field(spritesManager.FieldType, "m_drawQueue");
            queueMessages = AccessTools.Field(messageData, "Messages");
            if (drawQueue == null || !typeof(IDictionary).IsAssignableFrom(drawQueue.FieldType)
                || queueMessages == null || queueMessages.FieldType != typeof(MyList<MySpriteDrawRenderMessage>))
                throw new MissingFieldException("MySpritesManager.m_drawQueue or MySpriteMessageData.Messages is not what it was");

            // MyManagers.SpritesManager.GetSpritesRenderer() and Return(renderer).
            ILGenerator il = ReflectionUtil.Begin("HudVisor.GetSpritesRenderer", typeof(object), Type.EmptyTypes, sprites, out DynamicMethod method);
            il.Emit(OpCodes.Ldsfld, spritesManager);
            il.Emit(OpCodes.Callvirt, AccessTools.Method(spritesManager.FieldType, "GetSpritesRenderer"));
            il.Emit(OpCodes.Ret);
            getRenderer = (Func<object>)method.CreateDelegate(typeof(Func<object>));

            il = ReflectionUtil.Begin("HudVisor.ReturnSpritesRenderer", null, new[] { typeof(object) }, sprites, out method);
            il.Emit(OpCodes.Ldsfld, spritesManager);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, sprites);
            il.Emit(OpCodes.Callvirt, AccessTools.Method(spritesManager.FieldType, "Return", new[] { sprites }));
            il.Emit(OpCodes.Ret);
            returnRenderer = (Action<object>)method.CreateDelegate(typeof(Action<object>));

            // renderer.ProcessDrawMessage(message, touchTextures: true, ignoreBounds: null), as the GUI's own pass adds its sprites.
            il = ReflectionUtil.Begin("HudVisor.Process", null, new[] { typeof(object), typeof(MyRenderMessageBase) }, sprites, out method);
            LocalBuilder noOverride = il.DeclareLocal(typeof(bool?));
            il.Emit(OpCodes.Ldloca_S, noOverride);
            il.Emit(OpCodes.Initobj, typeof(bool?));
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, sprites);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ldloc, noOverride);
            il.Emit(OpCodes.Callvirt, AccessTools.Method(sprites, "ProcessDrawMessage", new[] { typeof(MyRenderMessageBase), typeof(bool), typeof(bool?) }));
            il.Emit(OpCodes.Ret);
            process = (Action<object, MyRenderMessageBase>)method.CreateDelegate(typeof(Action<object, MyRenderMessageBase>));

            // renderer.ScissorStackPush(rectangle) and ScissorStackPop().
            il = ReflectionUtil.Begin("HudVisor.ScissorPush", null, new[] { typeof(object), typeof(Rectangle) }, sprites, out method);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, sprites);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Callvirt, AccessTools.Method(sprites, "ScissorStackPush", new[] { typeof(Rectangle) }));
            il.Emit(OpCodes.Ret);
            scissorPush = (Action<object, Rectangle>)method.CreateDelegate(typeof(Action<object, Rectangle>));

            il = ReflectionUtil.Begin("HudVisor.ScissorPop", null, new[] { typeof(object) }, sprites, out method);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, sprites);
            il.Emit(OpCodes.Callvirt, AccessTools.Method(sprites, "ScissorStackPop", Type.EmptyTypes));
            il.Emit(OpCodes.Ret);
            scissorPop = (Action<object>)method.CreateDelegate(typeof(Action<object>));

            // renderer.Draw(MyRender11.RC, target, ref bound, ref full, ref size, null, null): the default blend, premultiplied
            // alpha, and no depth test, as the GUI is drawn.
            il = ReflectionUtil.Begin("HudVisor.DrawSprites", null, new[] { typeof(object), typeof(object), typeof(MyViewport), typeof(MyViewport), typeof(Vector2) }, sprites, out method);
            LocalBuilder noRegion = il.DeclareLocal(typeof(MyViewport?));
            il.Emit(OpCodes.Ldloca_S, noRegion);
            il.Emit(OpCodes.Initobj, typeof(MyViewport?));
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, sprites);
            il.Emit(OpCodes.Call, AccessTools.PropertyGetter(render, "RC"));
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Castclass, rtv);
            il.Emit(OpCodes.Ldarga_S, (byte)2);
            il.Emit(OpCodes.Ldarga_S, (byte)3);
            il.Emit(OpCodes.Ldarga_S, (byte)4);
            il.Emit(OpCodes.Ldloc, noRegion);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Callvirt, AccessTools.Method(sprites, "Draw",
                new[] { context, rtv, typeof(MyViewport).MakeByRefType(), typeof(MyViewport).MakeByRefType(), typeof(Vector2).MakeByRefType(), typeof(MyViewport?), blend }));
            il.Emit(OpCodes.Ret);
            drawSprites = (DrawSpritesCall)method.CreateDelegate(typeof(DrawSpritesCall));

            // MyRender11.GetFont(id).MeasureString(text, scale, useMyRenderGuiConstants: true): a text's size as it is drawn.
            il = ReflectionUtil.Begin("HudVisor.MeasureString", typeof(Vector2), new[] { typeof(int), typeof(string), typeof(float) }, sprites, out method);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, AccessTools.Method(render, "GetFont", new[] { typeof(int) }));
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Callvirt, AccessTools.Method(renderFont, "MeasureString", new[] { typeof(string), typeof(float), typeof(bool) }));
            il.Emit(OpCodes.Ret);
            measure = (Func<int, string, float, Vector2>)method.CreateDelegate(typeof(Func<int, string, float, Vector2>));
        }
    }
}
