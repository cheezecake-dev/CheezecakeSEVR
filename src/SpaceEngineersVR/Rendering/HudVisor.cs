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
    /// The HUD's fixed parts on the helmet's visor, as one display curved round the head, instead of on the flat panel
    /// (<see cref="HudPanel"/>): the toolbar, the status plates, block info, the notifications, chat, the quest log and the
    /// rest, each where the game lays it out beside its neighbours, on one cylinder round the eyes, with the top row moved
    /// up and the bottom row moved down to the visor's rim so the middle of the view is clear. The markers - ore,
    /// antennas, target locks, the selection box - and the crosshair stay on the flat panel, where <see cref="HudCamera"/>
    /// places them so they point at what they mark.
    /// <para>
    /// The parts are the game's own drawing, not redrawn. On the game thread each part's draw is bracketed by two render
    /// messages that draw nothing (a DrawString of scale 0, whose text is a string instance only this class has; the
    /// renderer returns at once from a string of scale 0, so a tag that is never taken out is harmless): the HUD screen's
    /// controls by which of its fields they are (MyGuiScreenBase.DrawElements and DrawControls call each control's Draw
    /// through <see cref="DrawControl"/>), the stat controls (the HUD definition's status plates and icons) and the gravity
    /// indicator by the corner their definition aligns them to, the notifications, and the voice chat, objective line and
    /// performance warning. Before the renderer draws the GUI (<see cref="EyeRenderer"/>, MyRender11.RenderMainSprites),
    /// <see cref="Split"/> takes the messages between each pair of tags out of the main sprite queue, in their order;
    /// the rest is drawn into the panel. Each part is drawn by the game's sprite renderer into one visor image, which goes
    /// to the headset as one composition layer: a piece of a cylinder round the head (XR_KHR_composition_layer_cylinder),
    /// head-locked in the VIEW space at <see cref="VRSettings.HudDistance"/>, in the GUI's own pixels, sampled once by the
    /// runtime.
    /// </para>
    /// <para>
    /// The display (<see cref="Lay"/>, <see cref="Measure"/>), with the helmet view on: the HUD projected onto the visor's glass
    /// from inside the helmet, as one picture. The GUI is laid round the cylinder whole, at one scale, every part where the game
    /// draws it beside its neighbours (the plates along the bottom with the toolbar between them, the quest log at the top
    /// left), <see cref="VRSettings.HudWidthDegrees"/> wide, moved out to <see cref="Push"/> times that up to <see cref="SideMost"/>
    /// each side; nothing is cut out of it or moved apart. <see cref="VRSettings.HudTextSize"/> grows each part away from the
    /// edge or corner it belongs to. Like a helmet display it is collimated: it is seen at <see cref="VRSettings.HudDistance"/>,
    /// where the eyes can rest on it, not on the glass a hand's width away. It is light projected onto the glass
    /// (<see cref="Projected"/>): most of its light is added to the view and only a little of it hides what is behind, so
    /// plates and shadows do not stand in front of the world. The game's backing plates are left off the glass entirely, and each
    /// part's light glows a little round it as a projection's does (<see cref="Glow"/>). It rides on the glass: as the helmet opens the glass swings up
    /// and the HUD with it (<see cref="HelmetHud.GlassCover"/>), until it goes at half-way (<see cref="HelmetHud.Hidden"/>), and
    /// it comes down into place as the helmet closes. It is fixed to the helmet, not the head (<see cref="VRSettings.HudOnHelmet"/>):
    /// the helmet gives only a little as the head turns (<see cref="HelmetTurn"/>), so looking far round looks away from it. With <see cref="VRSettings.HudTextBacking"/> above 0 a soft dark band runs
    /// round the rim behind the rows and the sides (<see cref="DrawBand"/>), fading out before the display's edge and toward
    /// the middle, instead of a plate behind each text. A cylinder layer, or a flat one where the runtime has no cylinders.
    /// </para>
    /// <para>
    /// The zone layout, as before the helmet view (with it off, or with RenderDebug HudVisorZones=1): each part on a layer of its own, moved out toward its edge or corner by its own anchor and turned to face the
    /// eyes both ways (<see cref="Pack"/>, <see cref="Place"/>), with a plate behind each text when backed. The anchors moved
    /// the corners apart from the middles, which cut rows the game drew together into separate boxes: the reason for the one
    /// display.
    /// </para>
    /// <para>
    /// What a part does not draw is not on the visor: the status bars the wrist has taken (<see cref="WristStatus"/>) and
    /// the toolbar left out on foot (<see cref="HudToolbar"/>) draw nothing, and the speed dial the wrist moves in is
    /// where it moved it. It fails safe: tags that do not pair, or a part with no layer or no room in the visor image,
    /// leave that frame's HUD on the flat panel as without the visor; after <see cref="FaultsToStop"/> such frames, or
    /// any error, or a frame the runtime refuses, the whole HUD stays on the flat panel for the rest of the run. The
    /// desktop window shows the visor as the headset does (<see cref="MirrorLayers"/>), and so it does with no headset
    /// session running (the headset asleep or not started): the same display, curved round the head, drawn into the window. The setting "HUD on the visor" off, or RenderDebug HudVisor=0:
    /// all of it on the flat panel.
    /// </para>
    /// </summary>
    internal static class HudVisor
    {
        /// <summary>Debug: the whole HUD back on the flat panel (<see cref="RenderDebug"/> HudVisor=0).</summary>
        public static bool Off { get; set; }

        /// <summary>Debug: the HUD on flat quads even where cylinder layers are offered (<see cref="RenderDebug"/> HudVisorCylinder=0): the one display on one, the zone layout's parts each facing the eyes.</summary>
        public static bool NoCylinder { get; set; }

        /// <summary>Debug: the zone layout, each part on a layer of its own, as before the one display (<see cref="RenderDebug"/> HudVisorZones=1).</summary>
        public static bool Zones { get; set; }

        /// <summary>
        /// How far out the HUD is moved from straight ahead. The one display: its sides go this many times as far out as the
        /// flat panel's, but not past <see cref="SideMost"/> (the GUI is widened with them, so nothing moves apart).
        /// The zone layout: the corner or edge a part belongs to (its anchor) goes this many times as far, to the same limits;
        /// the part moves with its anchor. Parts of one anchor keep their places to each other, and the outer ones move
        /// farther, so nothing that was apart on the panel comes together.
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

        /// <summary>The zone layout's visor image's side, pixels (less where the runtime's images are smaller). The GUI is 1920x1080.</summary>
        private const int ImageSide = 2048;

        /// <summary>The one display's image at most, pixels each way (less where the runtime's images are smaller); larger, the zone layout is used.</summary>
        private const int DisplaySide = 4096;

        /// <summary>
        /// Degrees of clear room round the GUI's frame on the display, so the band (<see cref="DrawBand"/>) fades out before the
        /// layer's edge and a part's slack round its text is not cut.
        /// </summary>
        private const float Rim = 2f;

        /// <summary>
        /// The one display as light projected onto the glass: the share of its coverage (alpha) that is turned into added light,
        /// so only the rest hides the world behind it. 0 would be a picture painted over the view; 1 pure light, which a bright
        /// sky washes out. Live from RenderDebug HudVisorLight.
        /// </summary>
        public static float Projected { get; set; } = DefaultProjected;

        public const float DefaultProjected = 0.6f;

        /// <summary>The HUD's colours on the visor, in the options screen's order; <see cref="VRSettings.HudColour"/> is an index into it.</summary>
        public static readonly string[] ColourNames = { "Game's colours", "Green", "Amber", "Cyan" };

        /// <summary>
        /// Each colour's filter on the display's light, red, green and blue (<see cref="Colour"/>): the game's own colours pass
        /// whole; the others are a helmet display's phosphor colours, the game's white text coming out in that colour.
        /// </summary>
        private static readonly Vector3[] ColourFilters =
        {
            new Vector3(1f, 1f, 1f),
            new Vector3(0.35f, 1f, 0.45f),
            new Vector3(1f, 0.72f, 0.2f),
            new Vector3(0.35f, 0.95f, 1f),
        };

        /// <summary>The least <see cref="VRSettings.HudOpacity"/> may fade the display to: always something of it shows.</summary>
        private const float LeastOpacity = 0.1f;

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

            /// <summary>Image pixels to a GUI pixel where it is drawn in the visor image.</summary>
            public float Scale = 1f;

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

        private static float uiScale = 1f;
        private static object image;
        private static (float Distance, float Width, bool Cylinder, float Size, bool Display) loggedFor;

        /// <summary>This frame's parts are on the one display (<see cref="Lay"/>), not in zones (<see cref="Pack"/>).</summary>
        private static bool display;

        // The one display (Measure): image pixels to a radian round the cylinder, and to a unit of height over the distance
        // (the same, so its pixels are square); where the GUI's top left is in the image; the image's row level with the eyes; and why it last
        // did not fit, logged once.
        private static float perRadian, centreRow;
        private static Vector2 origin;
        private static string displayRefused;

        // The GUI's sprite queue, by target (its types are internal); drawing goes through GameSprites.
        private static FieldInfo spritesManager, drawQueue, queueMessages;

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
        private static int Budget => Math.Min(XrRuntime.MaxLayerCount > 0 ? XrRuntime.MaxLayerCount : XrSession.MostLayers, XrSession.MostLayers)
            - OtherLayers;

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
                tagging = on && hooked && Shown && InPlay();
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
        /// The picture is VR's but no headset session runs (the headset asleep, or not started yet): the desktop window shows the
        /// visor as the headset would (<see cref="MirrorLayers"/>), when it draws the headset's layers at all.
        /// </summary>
        private static bool WindowShown => StereoPatches.VRPicture && !XrSession.Running && MirrorLayers.Available;

        /// <summary>The visor is seen: in the headset, or in the window without one.</summary>
        private static bool Shown => HeadsetShown || WindowShown;

        /// <summary>The visor's pieces are curved: the runtime offers cylinder layers, or the window draws them itself.</summary>
        private static bool Curved => (XrRuntime.HasCylinder || !XrSession.Running) && !NoCylinder;

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
            bool curved = Curved;
            bool window = WindowShown;
            int key = on ? (curved ? (Zones ? 6 : 1) : 2) + (window ? 10 : 0) : failed ? 3 : Off ? 4 : 5;
            // On, it is told once the headset's runtime is there to say how many layers it takes, or the window shows it; a failure has its own log.
            if (key == stateKey || on && !Shown)
                return;
            stateKey = key;
            if (on && window)
            {
                Log.Info("HUD on the visor: on, in the desktop window without a headset session, as one display curved round the head");
                return;
            }
            if (on)
            {
                string layers = curved && !Zones ? "one display, a cylinder layer (XR_KHR_composition_layer_cylinder) round the head"
                    : curved ? "cylinder layers (XR_KHR_composition_layer_cylinder), one a zone (RenderDebug HudVisorZones=1)"
                    : "flat layers facing the eyes, one a zone" + (NoCylinder ? " (RenderDebug HudVisorCylinder=0)" : " (the runtime offers no cylinder layers)");
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
            bool wanted = hooked && Wanted && Shown;
            Tally(wanted);
            if (!hooked || failed)
                return;
            try
            {
                var queue = (IDictionary)drawQueue.GetValue(spritesManager.GetValue(null));
                object data;
                lock (SpriteQueueLock.Gate)
                    data = queue?[DefaultTarget];
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
                    take = Arrange(out fault);
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
                    Vector2 size = GameSprites.Measure(text.FontIndex, text.Text, text.ScreenScale);
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
                    Vector2 size = GameSprites.Measure(text.FontIndex, text.Text, text.ScreenScale);
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
        /// Lays this frame's parts out in the visor image: on the one display (<see cref="Lay"/>) with the helmet view on, where
        /// the runtime offers cylinder layers and the image fits (<see cref="Measure"/>), else in zones (<see cref="Pack"/>). False, with the
        /// reason, when they cannot all go on the visor: then none of them does this frame.
        /// </summary>
        private static bool Arrange(out string fault)
        {
            display = HelmetView.On && !Zones && Measure();
            return display ? Lay(out fault) : Pack(out fault);
        }

        /// <summary>Sets a part's <see cref="Piece.Rect"/>, its drawing with <see cref="Pad"/> round it inside the GUI; false when it has nothing on screen.</summary>
        private static bool Bound(Piece piece)
        {
            if (piece.Right <= piece.Left || piece.Bottom <= piece.Top)
                return false;
            int left = Math.Max((int)Math.Floor(piece.Left) - Pad, 0), top = Math.Max((int)Math.Floor(piece.Top) - Pad, 0);
            int right = Math.Min((int)Math.Ceiling(piece.Right) + Pad, gui.X), bottom = Math.Min((int)Math.Ceiling(piece.Bottom) + Pad, gui.Y);
            if (right <= left || bottom <= top)
                return false;
            piece.Rect = new Rectangle(left, top, right - left, bottom - top);
            return true;
        }

        /// <summary>
        /// The one display's size, and where the GUI goes on it, from the settings: <see cref="VRSettings.HudWidthDegrees"/>
        /// wide, moved out (<see cref="Out"/>) to <see cref="SideMost"/> each side, one image pixel to a GUI pixel, the GUI whole;
        /// and <see cref="Rim"/> clear all round. At the defaults (55 degrees, a 1920x1080 GUI) the GUI is 60 degrees wide at 32
        /// pixels a degree, about 17.6 up and down, and the image 2048x1208 pixels. False (logged once) when the runtime's images
        /// are too small for it: then the zone layout is used.
        /// </summary>
        private static bool Measure()
        {
            float halfWidth = Out(MathHelper.ToRadians(VRSettings.HudWidthDegrees / 2f), SideMost);
            perRadian = gui.X / (2f * halfWidth);
            float margin = MathHelper.ToRadians(Rim) * perRadian;
            origin = new Vector2(margin, margin);
            centreRow = origin.Y + gui.Y / 2f;
            var size = new Vector2I((int)Math.Ceiling(gui.X + 2f * margin), (int)Math.Ceiling(gui.Y + 2f * margin));
            int maxWidth = Math.Min(DisplaySide, XrRuntime.MaxImageWidth > 0 ? XrRuntime.MaxImageWidth : DisplaySide);
            int maxHeight = Math.Min(DisplaySide, XrRuntime.MaxImageHeight > 0 ? XrRuntime.MaxImageHeight : DisplaySide);
            if (size.X > maxWidth || size.Y > maxHeight)
            {
                string reason = $"the one display needs a {size.X}x{size.Y} px image and the headset's are at most {maxWidth}x{maxHeight}";
                if (reason != displayRefused)
                {
                    displayRefused = reason;
                    Log.Info("HUD on the visor: " + reason + "; its parts go on in zones instead");
                }
                return false;
            }
            imageSize = size;
            return true;
        }

        /// <summary>
        /// Finds each part with something on screen its place on the one display: where the GUI has it, grown by <see cref="VRSettings.HudTextSize"/> away
        /// from the edge or corner it belongs to (its anchor), which stays where it was, and kept inside the image. The parts
        /// are drawn at the text size, so a larger part is sharp, not sampled up. False, with the reason, when the runtime
        /// has no layer for the display.
        /// </summary>
        private static bool Lay(out string fault)
        {
            fault = null;
            float size = VRSettings.HudTextSize;
            int count = 0;
            for (int z = 0; z < ZoneCount; z++)
            {
                Piece piece = pieces[z];
                if (!Bound(piece))
                    continue;
                count++;
                float pivotX = Math.Max(piece.Rect.Left, Math.Min(piece.Rect.Right, Anchors[z].X * gui.X));
                float pivotY = Math.Max(piece.Rect.Top, Math.Min(piece.Rect.Bottom, Anchors[z].Y * gui.Y));
                int width = Math.Min((int)Math.Ceiling(piece.Rect.Width * size), imageSize.X);
                int height = Math.Min((int)Math.Ceiling(piece.Rect.Height * size), imageSize.Y);
                float left = origin.X + pivotX + (piece.Rect.X - pivotX) * size;
                float top = origin.Y + pivotY + (piece.Rect.Y - pivotY) * size;
                int x = Math.Max(0, Math.Min(imageSize.X - width, (int)Math.Round(left)));
                int y = Math.Max(0, Math.Min(imageSize.Y - height, (int)Math.Round(top)));
                piece.InImage = new Rectangle(x, y, width, height);
                piece.Scale = size;
            }
            if (count == 0)
                return false;
            if (Budget < 1)
            {
                fault = "the runtime has no layer for the visor";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Finds each part with something on screen a place in the visor image (shelves, tallest first): the zone layout.
        /// False, with the reason, when they do not all have a layer and a place: then none of them goes on the visor this frame.
        /// </summary>
        private static bool Pack(out string fault)
        {
            fault = null;
            int count = 0;
            for (int z = 0; z < ZoneCount; z++)
            {
                Piece piece = pieces[z];
                if (!Bound(piece))
                    continue;
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
                    foreach (Piece piece in pieces)
                        piece.Scale = scale;
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
        /// they were. On a failure they go back into the panel too. Without a headset frame (<paramref name="toHeadset"/>
        /// false) the visor image is made all the same, for the desktop window alone (<see cref="MirrorLayers"/>).
        /// </summary>
        /// <returns>The parts are on the visor this frame (<see cref="Attach"/>).</returns>
        public static bool Show(object panel, bool onVisor, bool toHeadset = true)
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
                if (!display)
                    DrawPlates();
                image = EyeRenderer.Borrow("VR HUD visor", imageSize);
                EyeRenderer.Clear(image);
                if (display)
                    DrawBand(image);
                foreach (Piece piece in pieces)
                {
                    if (piece.Messages.Count > 0 && (!display || piece.InImage.Width > 0))
                        Draw(image, piece, true);
                }
                Colour(image);
                if (display)
                {
                    Glow(image);
                    Project(image);
                }
                if (display && VRSettings.LowStatWarnings)
                    SuitPower.DrawIndicators(image, imageSize);
                Fade(image, Opacity);
                float suitPowerLevel = SuitPower.Level;
                Fade(image, suitPowerLevel);
                if (display && VRSettings.LowStatWarnings && suitPowerLevel > 0f)
                    SuitPower.DrawEnergyIndicator(image, imageSize);
                if (display)
                    PowerOn(image);
                if (toHeadset)
                {
                    SharpDX.Direct3D11.Texture2D swapImage = XrSession.AcquireVisorImage(imageSize.X, imageSize.Y);
                    try
                    {
                        EyeRenderer.CopyToImage(image, swapImage);
                    }
                    finally
                    {
                        XrSession.ReleaseVisorImage();
                    }
                }
                if (display)
                    PlaceDisplay();
                else
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

        /// <summary>
        /// The one display as projected light: the image's alpha times 1 less <see cref="Projected"/>, its colour kept, so over the
        /// view its light adds and its coverage hides less (<see cref="GameSprites.ScaleAlpha"/>). The band under it was drawn
        /// that much stronger (<see cref="BandPixels"/>), so the backing hides as much as its setting says. Without the blend
        /// state the display stays as drawn.
        /// </summary>
        private static void Project(object target)
        {
            float kept = 1f - MathHelper.Clamp(Projected, 0f, 1f);
            if (kept >= 1f || GameSprites.ScaleAlpha == null)
                return;
            GameSprites.DrawTextures(target, imageSize, new Color(0f, 0f, 0f, kept), GameSprites.ScaleAlpha,
                (Gui.GuiTextures.Blank, new RectangleF(0f, 0f, imageSize.X, imageSize.Y)));
        }

        /// <summary>
        /// How strong the projection's glow is round the HUD's light, near (a quarter of the image's size, a tight halo) and wide
        /// (an eighth, a soft bloom): shares of the HUD's own light added back round it. Live from RenderDebug HudVisorGlow (a scale on both).
        /// </summary>
        private const float GlowNear = 0.35f, GlowWide = 0.22f;

        public static float GlowScale { get; set; } = 1f;

        /// <summary>
        /// The light of a projection on glass: each part's light scatters a little round it, in its own colour. The display is
        /// drawn smaller (a quarter, then an eighth of its size), which blurs it; each is made pure light (its alpha taken to none,
        /// <see cref="GameSprites.ScaleAlpha"/>) and dimmed (<see cref="GameSprites.Multiply"/>), then drawn back over the display at
        /// full size, where with no alpha it adds. Before <see cref="Project"/>, so the display's own coverage is turned to light
        /// after. Without the blend states, no glow.
        /// </summary>
        private static void Glow(object target)
        {
            float scale = Math.Max(0f, GlowScale);
            if (scale <= 0f || GameSprites.Multiply == null || GameSprites.ScaleAlpha == null)
                return;
            var near = new Vector2I(Math.Max(8, imageSize.X / 4), Math.Max(8, imageSize.Y / 4));
            var wide = new Vector2I(Math.Max(4, imageSize.X / 8), Math.Max(4, imageSize.Y / 8));
            object nearImage = null, wideImage = null;
            try
            {
                nearImage = EyeRenderer.Borrow("VR HUD glow near", near);
                wideImage = EyeRenderer.Borrow("VR HUD glow wide", wide);
                EyeRenderer.Clear(nearImage);
                EyeRenderer.Clear(wideImage);
                EyeRenderer.Blend(nearImage, target, new MyViewport(0f, 0f, near.X, near.Y));
                EyeRenderer.Blend(wideImage, nearImage, new MyViewport(0f, 0f, wide.X, wide.Y));
                AsLight(nearImage, near, GlowNear * scale);
                AsLight(wideImage, wide, GlowWide * scale);
                var whole = new MyViewport(0f, 0f, imageSize.X, imageSize.Y);
                EyeRenderer.Blend(target, nearImage, whole);
                EyeRenderer.Blend(target, wideImage, whole);
            }
            finally
            {
                if (nearImage != null)
                    EyeRenderer.Release(nearImage);
                if (wideImage != null)
                    EyeRenderer.Release(wideImage);
            }
        }

        /// <summary>An image made pure light: its alpha taken to none, its colour times <paramref name="strength"/>.</summary>
        private static void AsLight(object image, Vector2I size, float strength)
        {
            var whole = new RectangleF(0f, 0f, size.X, size.Y);
            GameSprites.DrawTextures(image, size, new Color(0f, 0f, 0f, 0f), GameSprites.ScaleAlpha, (Gui.GuiTextures.Blank, whole));
            float k = MathHelper.Clamp(strength, 0f, 1f);
            GameSprites.DrawTextures(image, size, new Color(k, k, k, 1f), GameSprites.Multiply, (Gui.GuiTextures.Blank, whole));
        }

        /// <summary>
        /// The parts' light filtered to the HUD's colour (<see cref="VRSettings.HudColour"/>, a multiply): white comes out in it, other
        /// colours dimmer by what the filter holds back. The game's own colours, or without the blend state, as drawn.
        /// </summary>
        private static void Colour(object target)
        {
            int colour = VRSettings.HudColour;
            if (colour <= 0 || colour >= ColourFilters.Length || GameSprites.Multiply == null)
                return;
            Vector3 filter = ColourFilters[colour];
            GameSprites.DrawTextures(target, imageSize, new Color(filter.X, filter.Y, filter.Z, 1f), GameSprites.Multiply,
                (Gui.GuiTextures.Blank, new RectangleF(0f, 0f, imageSize.X, imageSize.Y)));
        }

        /// <summary>How strongly the HUD shows (<see cref="VRSettings.HudOpacity"/>), at least <see cref="LeastOpacity"/>; 1 without the blend states.</summary>
        private static float Opacity => GameSprites.Multiply == null || GameSprites.ScaleAlpha == null
            ? 1f
            : MathHelper.Clamp(VRSettings.HudOpacity, LeastOpacity, 1f);

        /// <summary>The image faded to <paramref name="level"/>: its light and its coverage alike (a multiply, then an alpha scale), so it fades as light and never as a dark sheet.</summary>
        private static void Fade(object target, float level)
        {
            if (level >= 1f || GameSprites.Multiply == null || GameSprites.ScaleAlpha == null)
                return;
            var whole = new RectangleF(0f, 0f, imageSize.X, imageSize.Y);
            GameSprites.DrawTextures(target, imageSize, new Color(level, level, level, 1f), GameSprites.Multiply, (Gui.GuiTextures.Blank, whole));
            GameSprites.DrawTextures(target, imageSize, new Color(0f, 0f, 0f, level), GameSprites.ScaleAlpha, (Gui.GuiTextures.Blank, whole));
        }

        /// <summary>How long the display takes to power on as the visor seals, seconds: short, so the HUD's information is not held back. Also <see cref="SuitPower"/>'s own ramp back from a dead battery, the same effect reused rather than a second one invented.</summary>
        internal const double PowerOnSeconds = 0.22;

        /// <summary>
        /// The projector powering on as the visor seals (<see cref="HelmetHud.RestoredAt"/>): for <see cref="PowerOnSeconds"/> the
        /// display comes up from a third of its light with two quick flickers (<see cref="Fade"/>). Every part is there from the first frame.
        /// Without the blend states it comes up at once.
        /// </summary>
        private static void PowerOn(object target)
        {
            long since = System.Diagnostics.Stopwatch.GetTimestamp() - HelmetHud.RestoredAt;
            double t = since / (PowerOnSeconds * System.Diagnostics.Stopwatch.Frequency);
            if (HelmetHud.RestoredAt == 0 || t >= 1.0 || t < 0.0 || GameSprites.Multiply == null || GameSprites.ScaleAlpha == null)
                return;
            Fade(target, PowerOnLevel(t));
        }

        /// <summary>The power-on ramp's own shape, from a third of the light at t=0 to full at t=1, with two quick flickers along the way - shared with <see cref="SuitPower"/>'s own recovery from a dead battery.</summary>
        internal static float PowerOnLevel(double t)
        {
            float level = 0.35f + 0.65f * (float)t;
            if ((t > 0.22 && t < 0.3) || (t > 0.52 && t < 0.58))
                level *= 0.4f;
            return level;
        }

        /// <summary>The share of the display's coverage <see cref="Project"/> keeps this frame: 1 when it does not run.</summary>
        private static float Kept => GameSprites.ScaleAlpha == null ? 1f : Math.Max(0.05f, 1f - MathHelper.Clamp(Projected, 0f, 1f));

        /// <summary>Room, pixels, a plate keeps round the text it backs; plates this close are one.</summary>
        private const float PlatePad = 4f;

        /// <summary>
        /// The zone layout's backing: puts a dark plate behind each block of text, first in its part so everything else draws over it. Texts whose plates
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
                    sprite.Texture = Gui.GuiTextures.Blank;
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

        /// <summary>
        /// This frame's visor as the headset has it, for the desktop window (<see cref="MirrorLayers"/>): the image and the pieces
        /// of it, head-locked. False when the HUD is not on the visor this frame. Until <see cref="Discard"/>.
        /// </summary>
        public static bool TryGetVisor(out object visorImage, out XrSession.VisorPiece[] visorPieces, out int count, out bool curved)
        {
            visorImage = onHeadset ? image : null;
            visorPieces = placed;
            count = onHeadset ? placedCount : 0;
            curved = cylinder;
            return visorImage != null && count > 0;
        }

        /// <summary>After the headset has its copy of the panel: the parts on the visor drawn into it too, for a window that shows the HUD flat.</summary>
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
            object renderer = GameSprites.Rent();
            try
            {
                bool clip = inImage && uiScale == 1f;
                if (clip)
                    GameSprites.ScissorPush(renderer, piece.Rect);
                // On the glass only light is projected: the game's backing plates stay on the flat window, not on the visor.
                bool light = inImage && display;
                foreach (MySpriteDrawRenderMessage message in piece.Messages)
                {
                    if (light && message is MyRenderMessageDrawSprite sprite && Backing(sprite.Texture))
                        continue;
                    if (light)
                        NoteTexture(message);
                    GameSprites.Process(renderer, message);
                }
                for (int i = piece.Scissors + (clip ? 1 : 0); i > 0; i--)
                    GameSprites.ScissorPop(renderer);
                // The renderer is given the GUI's size to write into its shaders and a viewport the part's scale times larger.
                float k = inImage ? piece.Scale : 1f;
                float shiftX = inImage ? piece.InImage.X - piece.Rect.X * k : 0f, shiftY = inImage ? piece.InImage.Y - piece.Rect.Y * k : 0f;
                var full = new MyViewport(shiftX, shiftY, gui.X * k, gui.Y * k);
                var bound = new MyViewport(shiftX + gui.X * k * (1f - uiScale) / 2f, shiftY + gui.Y * k * (1f - uiScale) / 2f, gui.X * k * uiScale, gui.Y * k * uiScale);
                GameSprites.Draw(renderer, target, bound, full, new Vector2(gui.X, gui.Y));
            }
            finally
            {
                GameSprites.Return(renderer);
            }
        }

        /// <summary>
        /// A texture the game draws behind the HUD's parts as their backing plate (Textures\GUI\Icons\HUD 2017\...Plate.png:
        /// LeftPlate, RightPlate and the like). On the glass a plate would be a dark panel stuck on the view, so the one display
        /// leaves them out (<see cref="Draw"/>); the parts' text, bars and icons are the light the visor projects.
        /// </summary>
        private static bool Backing(string texture) =>
            texture != null && texture.IndexOf("HUD 2017", StringComparison.OrdinalIgnoreCase) >= 0
            && texture.EndsWith("Plate.png", StringComparison.OrdinalIgnoreCase);

        private static readonly HashSet<string> texturesSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The most textures <see cref="NoteTexture"/> logs in a run.</summary>
        private const int TexturesLogged = 80;

        /// <summary>Logs each texture the first time it is drawn on the glass (up to <see cref="TexturesLogged"/>): for choosing what else to leave off it.</summary>
        private static void NoteTexture(MySpriteDrawRenderMessage message)
        {
            if (texturesSeen.Count >= TexturesLogged || !(message is MyRenderMessageDrawSprite sprite) || string.IsNullOrEmpty(sprite.Texture))
                return;
            if (texturesSeen.Add(sprite.Texture))
                Log.Info("HUD on the visor: drawn on the glass: " + sprite.Texture);
        }

        /// <summary>
        /// The zone layout: where each part on the visor goes, round the head in the VIEW space. Its middle is where the flat panel shows it,
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
            cylinder = Curved;
            if (loggedFor != (distance, widthDegrees, cylinder, VRSettings.HudTextSize, false))
            {
                loggedFor = (distance, widthDegrees, cylinder, VRSettings.HudTextSize, false);
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

        /// <summary>
        /// The one display round the head in the VIEW space: the whole visor image on a cylinder of radius
        /// <see cref="VRSettings.HudDistance"/> about the head, its middle straight ahead, <see cref="perRadian"/> pixels to a
        /// radian round it and to a unit of height over the distance, so its pixels are square and the image's row
        /// <see cref="centreRow"/> is level with the eyes. A cylinder layer's image is centred on its pose, so the pose is moved
        /// up or down to where the image's middle row is. Without cylinder layers, a flat quad there, as wide as the cylinder's
        /// arc. Turned up with the visor's glass as it swings (<see cref="GlassSwing"/>). Logs the display, and each part where
        /// it is on it, once for the settings.
        /// </summary>
        private static void PlaceDisplay()
        {
            float distance = VRSettings.HudDistance;
            cylinder = Curved;
            float middleUp = (centreRow - imageSize.Y / 2f) / perRadian * distance;
            // On the glass: swung up with it in the helmet's space, then with the helmet's turn from the head (row vectors).
            Matrix onGlass = Matrix.CreateRotationX(GlassSwing);
            if (VRSettings.HudOnHelmet)
                onGlass *= HelmetTurn.FromHead();
            Quaternion swing = Quaternion.CreateFromRotationMatrix(onGlass);
            Vector3 centre = Vector3.Transform(new Vector3(0f, middleUp, 0f), onGlass);
            Vector3 middle = Vector3.Transform(new Vector3(0f, middleUp, -distance), onGlass);
            placed[0] = new XrSession.VisorPiece
            {
                Rect = new XrRect2Di { X = 0, Y = 0, Width = imageSize.X, Height = imageSize.Y },
                Orientation = new XrQuaternionf { X = swing.X, Y = swing.Y, Z = swing.Z, W = swing.W },
                Centre = new XrVector3f { X = centre.X, Y = centre.Y, Z = centre.Z },
                Middle = new XrVector3f { X = middle.X, Y = middle.Y, Z = middle.Z },
                Radius = distance,
                Width = imageSize.X / perRadian * distance,
                Height = imageSize.Y / perRadian * distance,
            };
            placedCount = 1;

            float perDegree = perRadian * (float)Math.PI / 180f;
            if (loggedFor != (distance, VRSettings.HudWidthDegrees, cylinder, VRSettings.HudTextSize, true))
            {
                loggedFor = (distance, VRSettings.HudWidthDegrees, cylinder, VRSettings.HudTextSize, true);
                foreach (Piece piece in pieces)
                    piece.Logged = false;
                Log.Info($"HUD on the visor: projected on the glass as one display, {imageSize.X}x{imageSize.Y} px on a {(cylinder ? "cylinder" : "flat")} layer {distance:0.00} m out, " +
                         $"{imageSize.X / perDegree:0.0} degrees wide (the GUI {gui.X / perDegree:0.0}, whole), from {Elevation(0f):0.0} up to {-Elevation(imageSize.Y):0.0} down, " +
                         $"{perDegree:0.0} px a degree; parts at {VRSettings.HudTextSize:0.00}x; {Projected:P0} of its coverage as light " +
                         $"({(GameSprites.ScaleAlpha != null ? "RenderDebug HudVisorLight" : "no blend state for it: drawn as painted")})");
            }
            for (int z = 0; z < ZoneCount; z++)
            {
                Piece piece = pieces[z];
                if (piece.Logged || piece.Messages.Count == 0 || piece.InImage.Width == 0)
                    continue;
                piece.Logged = true;
                Rectangle at = piece.InImage;
                Log.Info($"HUD on the visor: the {Names[z]} part, {piece.Rect.Width}x{piece.Rect.Height} px of the GUI at ({piece.Rect.X}, {piece.Rect.Y}), " +
                         $"{at.Width / perDegree:0.0} x {Elevation(at.Top) - Elevation(at.Bottom):0.0} degrees on the display; its middle " +
                         $"{(at.X + at.Width / 2f - imageSize.X / 2f) / perDegree:0.0} degrees right and {Elevation(at.Y + at.Height / 2f):0.0} up");
            }
        }

        /// <summary>
        /// How far the visor's glass has swung up from closed, radians: helmet_open's swing times how far open it is
        /// (<see cref="HelmetHud.GlassCover"/>). The HUD is projected on the glass, so it turns up with it. 0 without the helmet worn.
        /// </summary>
        private static float GlassSwing => HelmetHud.Worn ? (1f - HelmetHud.GlassCover) * HelmetHud.GlassSwingAngle : 0f;

        /// <summary>The angle up from level to a row of the one display's image, degrees.</summary>
        private static float Elevation(float row) => MathHelper.ToDegrees((float)Math.Atan((centreRow - row) / perRadian));

        // ---- The band (the one display) ----

        /// <summary>Texels each way of the band's texture, stretched over the whole display.</summary>
        private const int BandSize = 256;

        /// <summary>Degrees over which the band fades out inward, past its depth.</summary>
        private const float BandSoft = 5f;

        /// <summary>
        /// How deep the band is at full strength, as a share of the GUI, from the GUI's frame inward: along the top and the bottom
        /// a quarter of its height, about the depth of the quest log and of the plates and the toolbar; along the sides a fifth
        /// of its width, about block info's.
        /// </summary>
        private const float BandRows = 0.25f, BandSides = 0.18f;

        private static readonly GeneratedTexture band = new GeneratedTexture("VR HUD visor band", BandSize, BandPixels);

        // What the band's texels were made for: the display's size, degrees, its depths, degrees, and the backing.
        private static (float Wide, float High, float Rows, float Sides, float Backing) bandFor;

        /// <summary>
        /// The text backing on the one display (<see cref="VRSettings.HudTextBacking"/> above 0): one soft dark band round the
        /// rim, under the parts, instead of a plate behind each text. As dark as the backing along the rows and the sides, to
        /// <see cref="BandRows"/> and <see cref="BandSides"/> of the GUI in, fading out over <see cref="BandSoft"/> degrees past
        /// that toward the middle, and fading in over <see cref="Rim"/> from the layer's edge, so no edge of it shows as a line.
        /// The texture is made again when the display's size or the backing changes.
        /// </summary>
        private static void DrawBand(object target)
        {
            float backing = VRSettings.HudTextBacking;
            if (backing <= 0f)
                return;
            float perDegree = perRadian * (float)Math.PI / 180f;
            // Drawn as much stronger as the projection will take its coverage down (Project), so it backs as the setting says.
            var wanted = (imageSize.X / perDegree, imageSize.Y / perDegree, Rim + BandRows * gui.Y / perDegree, Rim + BandSides * gui.X / perDegree, Math.Min(1f, backing / Kept));
            if (wanted != bandFor)
            {
                bandFor = wanted;
                band.Remake();
            }
            object texture = band.Get();
            if (texture != null)
                EyeRenderer.Blend(target, texture, new MyViewport(0f, 0f, imageSize.X, imageSize.Y));
        }

        /// <summary>The band's texels for <see cref="bandFor"/>: black, premultiplied, its alpha the band's strength.</summary>
        private static byte[] BandPixels(int size)
        {
            var (wide, high, rows, sides, backing) = bandFor;
            var texels = new byte[size * size * 4];
            for (int j = 0; j < size; j++)
            {
                float top = (j + 0.5f) / size * high, bottom = high - top;
                for (int i = 0; i < size; i++)
                {
                    float left = (i + 0.5f) / size * wide, right = wide - left;
                    float edge = Math.Min(Math.Min(left, right), Math.Min(top, bottom));
                    float deep = Math.Max(Math.Max(Within(top, rows), Within(bottom, rows)), Math.Max(Within(left, sides), Within(right, sides)));
                    float alpha = backing * Smooth(edge / Rim) * deep;
                    texels[(j * size + i) * 4 + 3] = (byte)Math.Round(Math.Min(1f, alpha) * 255f);
                }
            }
            return texels;
        }

        /// <summary>1 within <paramref name="depth"/> degrees of an edge, fading to 0 over <see cref="BandSoft"/> past it.</summary>
        private static float Within(float fromEdge, float depth) => 1f - Smooth((fromEdge - depth) / BandSoft);

        private static float Smooth(float t)
        {
            t = t < 0f ? 0f : t > 1f ? 1f : t;
            return t * t * (3f - 2f * t);
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

        /// <summary>Finds the GUI's sprite queue, and reaches the game's sprite renderer (<see cref="GameSprites"/>).</summary>
        private static void Bind()
        {
            Assembly render11 = typeof(MyDX11Render).Assembly;
            Type managers = render11.GetType("VRage.Render11.Common.MyManagers", throwOnError: true);
            Type messageData = render11.GetType("VRage.Render11.Sprites.MySpriteMessageData", throwOnError: true);

            // MyManagers.SpritesManager.m_drawQueue[target].Messages: the sprite messages waiting to be drawn, by target.
            spritesManager = AccessTools.Field(managers, "SpritesManager");
            drawQueue = AccessTools.Field(spritesManager.FieldType, "m_drawQueue");
            queueMessages = AccessTools.Field(messageData, "Messages");
            if (drawQueue == null || !typeof(IDictionary).IsAssignableFrom(drawQueue.FieldType)
                || queueMessages == null || queueMessages.FieldType != typeof(MyList<MySpriteDrawRenderMessage>))
                throw new MissingFieldException("MySpritesManager.m_drawQueue or MySpriteMessageData.Messages is not what it was");
            GameSprites.Bind();
        }
    }
}
