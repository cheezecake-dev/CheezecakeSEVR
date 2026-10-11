using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using HarmonyLib;
using Sandbox;
using Sandbox.Definitions;
using Sandbox.Definitions.GUI;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.Gui;
using Sandbox.Game.GUI;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;
using VRage.Game;
using VRage.Game.ObjectBuilders.Definitions;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;
using VRageRender;
using VRageRender.Messages;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The suit and body status on the gauntlet. The HUD's status is drawn on the screen of the astronaut's left gauntlet,
    /// the screen itself: the glove's own material shows it, lit as the game lights that screen, on the drawn arm wherever it
    /// is (on the controller on foot and in a seat, on the animation in third person). It shows what an astronaut would read
    /// on the suit's own wrist,
    /// each element the game's own, in the HUD's order: the helmet, jetpack (magboots), broadcast and lamp icons, the
    /// radiation panel while exposed; the health, food (where the game shows it), energy, oxygen and hydrogen bars with their
    /// icons, percentages, bottle marks, low-value red and blink, and the medicine and cryo marks; the inventory fill; the
    /// oxygen and temperature outside. It is always lit, dimly, and brightens while it is looked at (the show and hide angles
    /// of <see cref="WristLook"/>). Nothing is in both places: while "Status on the wrist" is on, the HUD's copies of all of
    /// these are left out, in every view. What the helmet display is read for on the move stays on the HUD only: the speed
    /// dial, the dampeners and the gravity, and the alerts in the middle of the view (suffocating, low health, inventory full).
    /// The speed dial and the dampeners, alone on the left plate's right part then, move in toward the HUD's middle
    /// (<see cref="SpeedShift"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// What is drawn is the game's HUD, not a copy of it: the Default HUD's stat controls (MyHud.HudDefinition.StatControls,
    /// Content\Data\Hud) are MyStatControls built from styles, each bound to a stat of MyHud.Stats. The wrist builds its own
    /// MyStatControls from clones of the same styles, laid out again for the screen, so the values, the colours, the
    /// conditions and the blink are the game's, read from the same stats in the same frame. They are drawn with the game's
    /// sprite and text calls, kept instead of sent while they draw (a [ThreadStatic] switch), and sent to a sprite target of
    /// their own when what they show has changed. That target is drawn into a texture the way the game draws its LCD screens (MyRenderComponentScreenAreas):
    /// a generated texture made once (MyRenderProxy.CreateGeneratedTexture), the frame's sprites sent to its name and then
    /// MyRenderProxy.RenderOffscreenTexture, so the game's MyOffscreenRenderer draws them on a render worker into the
    /// texture on a deferred context of its own and the render thread runs that later; the texture is released with
    /// MyRenderProxy.DestroyGeneratedTexture. (Used before: the game's RenderTarget billboard material, which draws its
    /// sprites from inside the parallel transparent pass on the immediate context. The device was lost within seconds with
    /// it, three runs of three.)
    /// </para>
    /// <para>
    /// The screen. Both astronaut models (SE_astronaut.mwm, male and female) have no material of their own for it: it is a
    /// quad of the LeftGlove material, skinned wholly to <see cref="PlateBone"/>, on the glove's texture set
    /// (Textures\Models\Characters\Astronaut\Hand_cm.dds, _ng, _add, 2048 square, shared with the right glove's material),
    /// where it has a picture of its own: a blue panel in the colour-metal map (<see cref="PanelPixels"/>), metalness 0, lit
    /// by the add map's emissive (green 142 of 255) in a rounded rectangle just inside it (<see cref="ScreenPixels"/>), the
    /// only emissive on the glove. So the wrist puts its texture where the game puts an LCD's: in the material, with
    /// MyRenderProxy.ChangeMaterialTexture on the character's render object, as MyRenderComponentScreenAreas does for a
    /// block's screen and the game's skins do for the glove. The texture is the glove's whole colour-metal picture (the
    /// skin's, if one changes the left glove), drawn into it with the screen's panel redrawn: the bezel's grey on the blue
    /// border, and in the lit part the HUD's left plate at the HUD's background opacity, the controls over it, then the
    /// dimming. It is drawn again only when what it shows changes, at most 10 times a second, and always whole: the
    /// offscreen renderer clears the whole texture and makes all its mips with each draw (<see cref="Redraw"/>). The add map is the game's, so the screen glows as it did, now with the HUD's picture. The normal
    /// and add maps stay the model's or the skin's.
    /// </para>
    /// <para>
    /// The cost of a texture the offscreen renderer draws: it clears the alpha with the texture and writes none (it draws
    /// with BlendAlphaPremultNoAlphaChannel), and a colour-metal texture's alpha is the metalness. The clear is alpha 0, as
    /// the game clears an LCD (MyRenderComponentScreenAreas.RenderSpritesToTexture, BackgroundAlpha 0): the screen and the
    /// cloth are not metal, as they were not; the glove's few metal parts (about 4 % of its texels: knuckle plates, the cuff
    /// strip, bolts) lose their metalness on the left glove while the screen is on. Alpha 1 would make the whole glove metal,
    /// and a metal's colour is no albedo and no emissive: the screen would be dark.
    /// </para>
    /// <para>
    /// The HUD's parts are left out by the game's own mechanism, as <see cref="HudToolbar"/> does the toolbar plate: a
    /// condition that is never true in place of the style's own, so they fade as the game fades them, and the style's
    /// own back the moment they are wanted. Left out here: the bars controls (all of them, the medicine and cryo marks too),
    /// and in the left plate's control the icon row, its key hints and the radiation panel. The left plate itself is drawn
    /// cut to its right part, where the speed dial and the dampeners stay, and the control is moved right by
    /// <see cref="SpeedShift"/> while they are alone on it. On the center plates, whose parts
    /// <see cref="HudToolbar"/> already gates, it leaves out what <see cref="TakesFromCenterPlate"/> names: the oxygen and
    /// temperature outside, and the inventory fill (bar or gauge) with its backpack icon and key hint.
    /// </para>
    /// </remarks>
    internal static class WristStatus
    {
        /// <summary>Debug: no wrist screen, and the HUD's bars as the game draws them (<see cref="RenderDebug"/> WristStatus=0).</summary>
        public static bool Off { get; set; }

        /// <summary>Debug: the screen's brightness, 1 as built and at most 1 (<see cref="RenderDebug"/> WristStatusBright).</summary>
        public static float Brightness { get; set; } = 1f;

        /// <summary>
        /// Debug: the parts shown only at times (the radiation panel, the medicine and cryo marks, the bottle marks, the food
        /// bar) drawn all the time, to see where they fall (<see cref="RenderDebug"/> WristStatusAll=1). The wrist's controls
        /// are built again.
        /// </summary>
        public static bool ShowAll { get; set; }

        /// <summary>The screen is wanted: the setting, VR, and not switched off by the debug file.</summary>
        public static bool Active => !Off && VRSettings.WristStatus && VRInput.Active;

        /// <summary>The bone the gauntlet's screen is skinned to, all four corners at weight 1, in both astronaut models.</summary>
        public const string PlateBone = "SE_RigLForearm2";

        /// <summary>The astronaut models' material the gauntlet's screen is part of (the left glove's).</summary>
        public const string GloveMaterial = "LeftGlove";
        private static readonly MyStringId GloveMaterialId = MyStringId.GetOrCompute(GloveMaterial);

        /// <summary>The glove's colour-metal picture in both astronaut models (their LeftGlove material's), if the model does not say.</summary>
        private const string GloveTexture = "Textures\\Models\\Characters\\Astronaut\\Hand_cm.dds";

        /// <summary>
        /// The screen on the left gauntlet, in the character model's bind pose (model space, metres): read from the model's
        /// own mesh (SE_astronaut_LOD0.mwm, its LeftGlove part), the quad textured with the gauntlet's screen picture (UV u
        /// 0.669 to 0.958, v 0.549 to 0.703 of the glove texture). Right is the picture's +u (toward the hand), up its -v, the
        /// face right x up (away from the arm): the game's own picture there reads upright in that frame, and so does the
        /// wrist's. The bind-pose point goes to the world as the game skins it. Width and height are the quad's (metres).
        /// </summary>
        private sealed class Face
        {
            public string Name, Model;
            public Vector3 Centre, Right, Up;
            public float Width, Height;
        }

        // Vertices 17719-17722 of Models\Characters\Astronaut\SE_astronaut_LOD0.mwm. In SE_RigLForearm2's bind frame the
        // centre is (-0.1076, -0.0138, 0.0745) and the face (-0.091, -0.043, 0.995); in the palm bone's, (0.0629, 0.0742,
        // 0.0048) and (-0.068, 0.995, -0.073).
        private static readonly Face MaleFace = new Face
        {
            Name = "male",
            Model = "Models\\Characters\\Astronaut\\SE_astronaut.mwm",
            Centre = new Vector3(-0.5817f, 1.1555f, 0.0543f),
            Right = new Vector3(-0.6409f, -0.7676f, 0.0045f),
            Up = new Vector3(-0.0622f, 0.0578f, 0.9964f),
            Width = 0.1335f,
            Height = 0.0708f,
        };

        // Vertices 9410-9413 of Models\Characters\Astronaut\Female\SE_astronaut_LOD0.mwm (the same bind skeleton). In
        // SE_RigLForearm2's bind frame the centre is (-0.1186, 0.0009, 0.0594) and the face (-0.131, 0.069, 0.989).
        private static readonly Face FemaleFace = new Face
        {
            Name = "female",
            Model = "Models\\Characters\\Astronaut\\Female\\SE_astronaut.mwm",
            Centre = new Vector3(-0.5780f, 1.1362f, 0.0484f),
            Right = new Vector3(-0.6114f, -0.7906f, 0.0353f),
            Up = new Vector3(-0.0443f, 0.0787f, 0.9959f),
            Width = 0.1200f,
            Height = 0.0521f,
        };

        /// <summary>
        /// The share of the screen's light while it is not looked at, and while it is (the lit part is darkened by the rest,
        /// in linear light, after the HUD's picture is drawn).
        /// </summary>
        public const float DimLevel = 0.5f, FullLevel = 1f;

        /// <summary>Seconds to go from dim to full and back.</summary>
        public const float FadeSeconds = 0.15f;

        /// <summary>
        /// The eyes farther than this (metres) from the gauntlet are not the character's (a camera block, the spectator, a ship's
        /// remote control, a far third person): the screen is not looked at, and stays dim.
        /// </summary>
        public const double FarthestEyes = 1.5;

        /// <summary>
        /// Seconds between sending the glove's texture change again: a skin applied after it (the game sends the skin's
        /// textures for the glove when a world loads, with no sign on the game's side) would otherwise keep the glove's own
        /// screen.
        /// </summary>
        private const double ResendSeconds = 2.0;

        // The texture: the glove's colour-metal picture, all of it, at its size (a power of two, as the game's LCD textures:
        // the game gives a mipmapped generated texture MyResourceUtils.GetMipLevels(long side) levels, which is one more than
        // Direct3D allows for any other size; 640 got 11 of 10, and MyOffscreenRenderer.SubmitWork's CreateTexture2D failed
        // with E_INVALIDARG, the game's own render error box).
        private const int TextureSize = 2048;

        /// <summary>
        /// The screen's panel in the glove's colour-metal picture (Hand_cm.dds, 2048 square, pixels): the blue of the game's
        /// screen picture, x 1397 to 1935 and y 1143 to 1418 (measured). Painted the bezel's grey, which is all round it.
        /// </summary>
        private static readonly RectangleF PanelPixels = new RectangleF(1397f, 1143f, 539f, 276f);

        /// <summary>
        /// The lit part of the screen: where the glove's add map has its emissive (Hand_add.dds green 142, x 1402 to 1932 and
        /// y 1148 to 1414 in the colour-metal picture's pixels, its corners rounded about 14 pixels; measured). The canvas
        /// fills it.
        /// </summary>
        private static readonly RectangleF ScreenPixels = new RectangleF(1402f, 1148f, 530f, 266f);

        /// <summary>The bezel's grey round the screen in the glove's picture (30, 30, 30, measured).</summary>
        private static readonly Color Bezel = new Color(30, 30, 30);

        // The screen's canvas, in the HUD's units (a 1080-line screen's pixels): as high as the content, as wide as the lit
        // part's shape (530 x 266 pixels), drawn into that part at PixelsPerUnit.
        private const int CanvasHeight = 220, CanvasWidth = 438;
        private const float PixelsPerUnit = 266f / CanvasHeight;

        // Icon row: the game's four slots from the left of the top band.
        private const float SlotSize = 44f, SlotGap = 6f, SlotTop = 6f, SlotLeft = 10f;

        // The radiation panel: the rest of the top band, right of the slots, as the game has it right of its icon row.
        private const float RadiationLeft = SlotLeft + 4 * SlotSize + 3 * SlotGap + 8f, RadiationRight = CanvasWidth - 8f;

        // Bars: one row each, the game's order; the value to the right of the bar. The bar starts far enough in for the medicine
        // and cryo marks left of the icons (the game has them 52 and 54 left of the bar). The icons' centre is 16 left of the
        // bar, as the game's. The bar is as long as the canvas leaves room for, with its value after it.
        private const float BarLeft = 60f, BarWidth = CanvasWidth - BarLeft - 78f, BarHeight = 9f, RowFirst = 70f, RowStep = 23f, ValueGap = 4f, ValueScale = 0.8f;
        private const float IconColumn = -16f, IconHeight = 24f;

        // Under the bars: the inventory fill as one more row, then the oxygen and temperature outside on one line, each an icon
        // and its text.
        private const float EnvBelow = 21f, EnvLeft = 14f, EnvSecondLeft = CanvasWidth / 2f, EnvIcon = 17f, EnvTextScale = 0.7f;

        /// <summary>The cut of the left plate: the part right of this share of its texture's width stays on the HUD.</summary>
        private const float PlateCut = 655f / 1070f;
        private static readonly Vector2I PlateTextureSize = new Vector2I(1070, 380);

        // The sprite target and the generated texture's name (the game takes none with spaces, dots, slashes or brackets:
        // MyRenderProxy.IsValidGeneratedTextureName).
        private const string Target = "VRWristStatus";
        private const string BlankTexture = "Textures\\GUI\\Blank.dds";

        // The screen's background: the HUD's left plate picture, the part behind its bars and icons.
        private const string PlateTexture = "Textures\\GUI\\Icons\\HUD 2017\\LeftPlate.png";

        /// <summary>
        /// The part of the left plate's picture behind the canvas: its left part (left of <see cref="PlateCut"/>, where the
        /// HUD has the bars and icons), without the transparent margin left and above it, in the picture's pixels.
        /// LeftPlate.png is 1070 x 380, opaque from x 35 and y 6 (measured).
        /// </summary>
        private static readonly Rectangle PlateSource = new Rectangle(35, 6, 620, 350);

        /// <summary>
        /// The texture's clear: alpha 0, as the game clears an LCD, since the alpha is the glove's metalness and the offscreen
        /// renderer writes none (see the remarks). Every pixel's colour is drawn over it each frame.
        /// </summary>
        private static readonly Color Clear = new Color(0, 0, 0, 0);

        private static readonly MyStringHash Health = MyStringHash.GetOrCompute("player_health");
        private static readonly MyStringHash EnvOxygen = MyStringHash.GetOrCompute("environment_oxygen_level");
        private static readonly MyStringHash EnvTemperature = MyStringHash.GetOrCompute("environment_temperature_level");
        private static readonly MyStringHash Inventory = MyStringHash.GetOrCompute("player_inventory_capacity");
        private static readonly MyStringHash Radiation = MyStringHash.GetOrCompute("player_radiation");
        private static readonly MyStringHash RadiationPanel = MyStringHash.GetOrCompute("RadiationPanel");
        private static readonly MyStringHash[] RadiationTextures = { MyStringHash.GetOrCompute("RadiationPanel"), MyStringHash.GetOrCompute("RadiationIcon") };
        private static readonly MyStringHash[] InventoryTextures = { MyStringHash.GetOrCompute("BackpackIcon"), MyStringHash.GetOrCompute("InventoryRadialBackground") };
        private static readonly MyStringHash Backpack = MyStringHash.GetOrCompute("BackpackIcon");
        private const string InventoryKeyHint = "{GAME_CONTROL:INVENTORY}";
        private static readonly MyStringHash FoodRate = MyStringHash.GetOrCompute("session_food_consumption_rate");
        private static readonly MyStringHash LeftPlate = MyStringHash.GetOrCompute("LeftPlate");
        private static readonly MyStringHash[] BarStats =
        {
            MyStringHash.GetOrCompute("player_health"), MyStringHash.GetOrCompute("player_food"), MyStringHash.GetOrCompute("player_energy"),
            MyStringHash.GetOrCompute("player_oxygen"), MyStringHash.GetOrCompute("player_hydrogen"),
        };
        private static readonly MyStringHash[] SuitStats =
        {
            MyStringHash.GetOrCompute("player_helmet"), MyStringHash.GetOrCompute("player_jetpack"),
            MyStringHash.GetOrCompute("player_broadcasting"), MyStringHash.GetOrCompute("player_flashlight"),
        };
        private static readonly MyStringHash[] SuitTextures = { MyStringHash.GetOrCompute("IconsBackground"), MyStringHash.GetOrCompute("Magboots") };
        private static readonly string[] SuitKeyHints = { "{GAME_CONTROL:HELMET}", "{GAME_CONTROL:THRUSTS}", "{GAME_CONTROL:BROADCASTING}", "{GAME_CONTROL:HEADLIGHTS}" };

        // The game's icon row: slots 62 apart from x 20, on y -227, 60 square.
        private const float GameSlotLeft = 20f, GameSlotStep = 62f, GameSlotY = -227f, GameSlotSize = 60f;

        private static readonly FieldInfo BindingsField = AccessTools.Field(typeof(MyStatControls), "m_bindings");
        private static readonly FieldInfo DefinitionField = AccessTools.Field(typeof(MyStatControls), "m_objectBuilder");
        private static readonly MethodInfo InitConditions = AccessTools.Method(typeof(MyStatControls), "InitConditions", new[] { typeof(ConditionBase) });
        private static readonly MethodInfo CloneMethod = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
        private static FieldInfo styleField, controlField, workMessageField;

        /// <summary>Never true: it names no stat (MyStatControls.InitConditions finds none, and Eval says no).</summary>
        private static readonly StatCondition Never = new StatCondition { StatId = MyStringHash.GetOrCompute("VRWristStatusNever") };

        private sealed class Always : ConditionBase
        {
            public override bool Eval() => true;
        }

        private sealed class Original
        {
            public ConditionBase Condition;
        }

        /// <summary>The parts of one of the HUD's MyStatControls that the wrist takes over.</summary>
        private sealed class Gate
        {
            public readonly List<MyObjectBuilder_StatVisualStyle> Styles = new List<MyObjectBuilder_StatVisualStyle>();
            public MyObjectBuilder_StatControls Definition;
            public MyStatControlImage Plate;

            /// <summary>The bars' control (not the left plate's), and whether its parts were left out the last frame it was drawn (for the log).</summary>
            public bool Bars;
            public bool? Hidden;

            /// <summary>
            /// The left plate's control: where the game put it (null until first drawn), where it was put last, and how far
            /// right it is moved (<see cref="SpeedShift"/>).
            /// </summary>
            public Vector2? Home;
            public Vector2 Placed;
            public float Shift;

            public void Apply(bool hide)
            {
                foreach (MyObjectBuilder_StatVisualStyle style in Styles)
                {
                    if (Originals.TryGetValue(style, out Original original))
                        style.VisibleCondition = hide ? Never : original.Condition;
                }
            }
        }

        private static readonly ConditionalWeakTable<MyStatControls, Gate> Gates = new ConditionalWeakTable<MyStatControls, Gate>();
        private static readonly ConditionalWeakTable<MyObjectBuilder_StatVisualStyle, Original> Originals = new ConditionalWeakTable<MyObjectBuilder_StatVisualStyle, Original>();
        private static readonly ConditionalWeakTable<MyStatControls, object> Ours = new ConditionalWeakTable<MyStatControls, object>();

        // The wrist's own controls, built for one HUD definition in one session.
        private static readonly List<MyStatControls> wrist = new List<MyStatControls>();
        private static MyStatControls lower;
        private static ConditionBase foodShown;
        private static MyHudDefinition builtFor;
        private static MySession builtIn;
        private static bool builtAll;

        // The session that is unloading: no screen for it (its last frames would make the texture again).
        private static MySession unloaded;
        private static string screenNote;

        private static bool hookedDraw, hookedRedirect, hookedOffscreen, broken, hudBroken;
        private static int errors;

        // Game thread: the texture is made (until released), and the thread the sprites are sent from has been said.
        private static bool textureMade, sentFromSaid;

        // Render thread and its workers: the threads have been said; when this worker's drawing of the screen began.
        private static int takenOnSaid, submittedOnSaid, drawnOnSaid;
        [ThreadStatic] private static long workStart;

        // Game thread.
        private static long lastFrame;
        private static bool looked;
        private static float lookHeld, level = DimLevel;
        private static string lastState;
        private static long nextValuesAt;

        public static void Patch(Harmony harmony)
        {
            try
            {
                // Watched only, for the log and the Perf line: the game's offscreen renderer taking the screen's sprites (where
                // it processes render messages), handing them to a worker (render thread) and drawing them into its texture (the worker).
                Type offscreen = AccessTools.TypeByName("VRage.Render11.Render.MyOffscreenRenderer");
                workMessageField = AccessTools.Field(AccessTools.Inner(offscreen, "MyData"), "Message");
                harmony.Patch(AccessTools.Method(offscreen, "Add"), prefix: new HarmonyMethod(typeof(WristStatus), nameof(Taken)));
                harmony.Patch(AccessTools.Method(offscreen, "SubmitWork"), prefix: new HarmonyMethod(typeof(WristStatus), nameof(Submitting)));
                harmony.Patch(AccessTools.Method(offscreen, "RenderWork"),
                    prefix: new HarmonyMethod(typeof(WristStatus), nameof(WorkStarting)), postfix: new HarmonyMethod(typeof(WristStatus), nameof(WorkDone)));
                hookedOffscreen = workMessageField != null;
            }
            catch (Exception e)
            {
                Log.Error(e, "Wrist status: the game's offscreen renderer could not be watched; the screen still draws, without its render-side log and cost");
            }
            try
            {
                // The HUD's sprite and text calls, each taken by a prefix of its own while the wrist's controls draw.
                int count = 0;
                foreach (MethodInfo method in AccessTools.GetDeclaredMethods(typeof(MyRenderProxy)))
                {
                    string prefix = method.Name == nameof(MyRenderProxy.DrawSprite) ? nameof(TakeSprite)
                        : method.Name == nameof(MyRenderProxy.DrawSpriteExt) ? nameof(TakeSpriteExt)
                        : method.Name == nameof(MyRenderProxy.DrawString) ? nameof(TakeText)
                        : null;
                    if (prefix == null)
                        continue;
                    bool hasTarget = false;
                    foreach (ParameterInfo parameter in method.GetParameters())
                        hasTarget |= parameter.Name == "targetTexture" && parameter.ParameterType == typeof(string);
                    if (!hasTarget)
                        continue;
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(WristStatus), prefix));
                    count++;
                }
                hookedRedirect = count == 3;
                if (!hookedRedirect)
                    Log.Info($"Wrist status: {count} of the 3 sprite and text calls found; no screen on the wrist");
            }
            catch (Exception e)
            {
                Log.Error(e, "Wrist status: the HUD's sprites could not be sent to the wrist; no screen on the wrist");
            }
            try
            {
                harmony.Patch(AccessTools.Method(typeof(MySession), nameof(MySession.DrawSync)),
                    postfix: new HarmonyMethod(typeof(WristStatus), nameof(Draw)));
                hookedDraw = true;
            }
            catch (Exception e)
            {
                Log.Error(e, "Wrist status: the frame could not be hooked; no screen on the wrist");
            }
            try
            {
                harmony.Patch(AccessTools.Method(typeof(MyStatControls), nameof(MyStatControls.Draw), new[] { typeof(float), typeof(float) }),
                    prefix: new HarmonyMethod(typeof(WristStatus), nameof(BeforeStatsDraw)));
            }
            catch (Exception e)
            {
                hudBroken = true;
                Log.Error(e, "Wrist status: the HUD's bars could not be hooked; they stay as the game draws them");
            }
            MySession.OnUnloading += () =>
            {
                unloaded = MySession.Static;
                ReleaseTexture("the session is unloading");
            };
            Log.Info($"Wrist status: hooked (frame {hookedDraw}, sprites {hookedRedirect}, offscreen renderer watched {hookedOffscreen})");
        }

        // ---- the wrist's sprites ----

        // While the wrist's controls draw (game thread), their sprite and text calls are kept here instead of sent, and
        // what they show is folded into the frame's signature: positions and sizes to whole texture pixels, colours as drawn
        // (8 bits a channel), texts as written. The screen is redrawn from the kept calls only when the signature changes.
        [ThreadStatic] private static bool recording;
        private static readonly List<Call> calls = new List<Call>();
        private static ulong signature;

        private enum CallKind : byte { Sprite, SpriteExt, Text }

        private struct Call
        {
            public CallKind Kind;
            public string Texture, Mask, Text;
            public RectangleF Destination;
            public Rectangle? Source;
            public Color Color;
            public float Rotation, RotationSpeed, Scale, MaxWidth;
            public Vector2 Right, Origin, At;
            public bool IgnoreBounds, WaitTillLoaded;
            public int Font;
        }

        private static void Mix(int value)
        {
            unchecked
            {
                signature = (signature ^ (uint)value) * 1099511628211UL;
            }
        }

        private static void Mix(float value, float step) => Mix((int)Math.Round(value / step));

        private static void Mix(string text) => Mix(text == null ? 0 : text.GetHashCode());

        private static void Mix(Rectangle? rectangle)
        {
            if (rectangle == null)
            {
                Mix(-1);
                return;
            }
            Rectangle r = rectangle.Value;
            Mix(r.X);
            Mix(r.Y);
            Mix(r.Width);
            Mix(r.Height);
        }

        private static void Mix(RectangleF rectangle)
        {
            Mix(rectangle.X, 1f);
            Mix(rectangle.Y, 1f);
            Mix(rectangle.Width, 1f);
            Mix(rectangle.Height, 1f);
        }

        /// <summary>MyRenderProxy.DrawSprite while the wrist's controls draw: kept for the screen, not sent.</summary>
        private static bool TakeSprite(string texture, ref RectangleF destination, Rectangle? sourceRectangle, Color color, float rotation,
            bool ignoreBounds, bool waitTillLoaded, string targetTexture, string maskTexture, float rotSpeed)
        {
            if (!recording || targetTexture != null)
                return true;
            calls.Add(new Call
            {
                Kind = CallKind.Sprite, Texture = texture, Destination = destination, Source = sourceRectangle, Color = color,
                Rotation = rotation, IgnoreBounds = ignoreBounds, WaitTillLoaded = waitTillLoaded, Mask = maskTexture, RotationSpeed = rotSpeed,
            });
            Mix(1);
            Mix(texture);
            Mix(maskTexture);
            Mix(destination);
            Mix(sourceRectangle);
            Mix((int)color.PackedValue);
            Mix(rotation, 0.001f);
            Mix(rotSpeed, 0.001f);
            return false;
        }

        /// <summary>MyRenderProxy.DrawSpriteExt while the wrist's controls draw: kept for the screen, not sent.</summary>
        private static bool TakeSpriteExt(string texture, ref RectangleF destination, Rectangle? sourceRectangle, Color color, ref Vector2 rightVector,
            ref Vector2 origin, bool ignoreBounds, bool waitTillLoaded, string targetTexture, string maskTexture)
        {
            if (!recording || targetTexture != null)
                return true;
            calls.Add(new Call
            {
                Kind = CallKind.SpriteExt, Texture = texture, Destination = destination, Source = sourceRectangle, Color = color,
                Right = rightVector, Origin = origin, IgnoreBounds = ignoreBounds, WaitTillLoaded = waitTillLoaded, Mask = maskTexture,
            });
            Mix(2);
            Mix(texture);
            Mix(maskTexture);
            Mix(destination);
            Mix(sourceRectangle);
            Mix((int)color.PackedValue);
            Mix(rightVector.X, 0.001f);
            Mix(rightVector.Y, 0.001f);
            Mix(origin.X, 1f);
            Mix(origin.Y, 1f);
            return false;
        }

        /// <summary>MyRenderProxy.DrawString while the wrist's controls draw: kept for the screen, not sent.</summary>
        private static bool TakeText(int fontIndex, Vector2 screenCoord, Color colorMask, string text, float screenScale, float screenMaxWidth,
            bool ignoreBounds, string targetTexture)
        {
            if (!recording || targetTexture != null)
                return true;
            calls.Add(new Call
            {
                Kind = CallKind.Text, Font = fontIndex, At = screenCoord, Color = colorMask, Text = text, Scale = screenScale,
                MaxWidth = screenMaxWidth, IgnoreBounds = ignoreBounds,
            });
            Mix(3);
            Mix(fontIndex);
            Mix(screenCoord.X, 1f);
            Mix(screenCoord.Y, 1f);
            Mix((int)colorMask.PackedValue);
            Mix(text);
            Mix(screenScale, 0.001f);
            Mix(screenMaxWidth, 1f);
            return false;
        }

        /// <summary>The kept calls, sent to the screen's texture in the order the controls made them.</summary>
        private static void SendCalls()
        {
            foreach (Call call in calls)
            {
                RectangleF destination = call.Destination;
                switch (call.Kind)
                {
                    case CallKind.Sprite:
                        MyRenderProxy.DrawSprite(call.Texture, ref destination, call.Source, call.Color, call.Rotation, call.IgnoreBounds, call.WaitTillLoaded,
                            Target, call.Mask, call.RotationSpeed);
                        break;
                    case CallKind.SpriteExt:
                        Vector2 right = call.Right, origin = call.Origin;
                        MyRenderProxy.DrawSpriteExt(call.Texture, ref destination, call.Source, call.Color, ref right, ref origin, call.IgnoreBounds,
                            call.WaitTillLoaded, Target, call.Mask);
                        break;
                    default:
                        MyRenderProxy.DrawString(call.Font, call.At, call.Color, call.Text, call.Scale, call.MaxWidth, call.IgnoreBounds, Target);
                        break;
                }
            }
        }

        private static string ThreadName()
        {
            Thread thread = Thread.CurrentThread;
            return $"{thread.ManagedThreadId}{(string.IsNullOrEmpty(thread.Name) ? string.Empty : " \"" + thread.Name + "\"")}";
        }

        /// <summary>
        /// Where the game processes its render messages (a worker or the render thread): the offscreen renderer took a
        /// texture's sprites (MyOffscreenRenderer.Add, from its message).
        /// </summary>
        private static void Taken(MyRenderMessageRenderOffscreenTexture rMessage)
        {
            if (rMessage?.OffscreenTexture != Target || Interlocked.Exchange(ref takenOnSaid, 1) != 0)
                return;
            Log.Info($"Wrist status: the screen's sprites reach the offscreen renderer on thread {ThreadName()} (MyOffscreenRenderer.Add, as the game processes its render messages)");
        }

        /// <summary>Render thread: the offscreen renderer hands a texture's drawing to a worker (MyOffscreenRenderer.SubmitWork, from the scene's draw).</summary>
        private static void Submitting(object data)
        {
            if (!(workMessageField?.GetValue(data) is MyRenderMessageRenderOffscreenTexture message) || message.OffscreenTexture != Target)
                return;
            if (Interlocked.Exchange(ref submittedOnSaid, 1) == 0)
                Log.Info($"Wrist status: the render thread is thread {ThreadName()} (MyOffscreenRenderer.SubmitWork hands the screen's drawing to a worker there)");
        }

        /// <summary>A render worker: the offscreen renderer draws a texture's sprites into it, on a deferred context of its own.</summary>
        private static void WorkStarting(object data)
        {
            workStart = 0;
            if (!(workMessageField?.GetValue(data) is MyRenderMessageRenderOffscreenTexture message) || message.OffscreenTexture != Target)
                return;
            workStart = Stopwatch.GetTimestamp();
            if (Interlocked.Exchange(ref drawnOnSaid, 1) == 0)
                Log.Info($"Wrist status: the screen's sprites are drawn into its texture on render worker thread {ThreadName()}, on a deferred context of its own");
        }

        private static void WorkDone()
        {
            if (workStart == 0)
                return;
            Interlocked.Add(ref renderTicks, Stopwatch.GetTimestamp() - workStart);
            Interlocked.Increment(ref renderDraws);
            workStart = 0;
        }

        /// <summary>The screen's texture, made as the game makes an LCD's (MyRenderComponentScreenAreas.CreateTexture).</summary>
        private static void EnsureTexture()
        {
            if (textureMade)
                return;
            MyRenderProxy.CreateGeneratedTexture(Target, TextureSize, TextureSize, MyGeneratedTextureType.RGBA, 1, null, generateMipmaps: true, immediatelyReady: false);
            textureMade = true;
            drawnOnce = false;
            Log.Info($"Wrist status: texture made ({Target}, {TextureSize} x {TextureSize}, RGBA: R8G8B8A8 sRGB with mipmaps, as an LCD's)");
        }

        /// <summary>
        /// The screen's texture, released as the game releases an LCD's (MyRenderComponentScreenAreas.ReleaseTexture): the
        /// glove is given its own picture back first, as the game puts another texture on a screen before it destroys one.
        /// </summary>
        private static void ReleaseTexture(string why)
        {
            Restore(why);
            if (!textureMade)
                return;
            textureMade = false;
            drawnOnce = false;
            MyRenderProxy.DestroyGeneratedTexture(Target);
            Log.Info($"Wrist status: texture released ({why})");
        }

        // ---- the glove's material ----

        // Game thread: the render object whose left glove shows the texture (uint.MaxValue: none), the skin's change for that
        // glove it was sent with (its own textures go back with it), and when it is sent again.
        private static uint swappedOn = uint.MaxValue;
        private static MyTextureChange? swappedSkin;
        private static long resendAt;

        // The last model whose glove picture was looked up, and that picture (null: the model does not say).
        private static object pictureModel;
        private static string pictureOfModel;

        /// <summary>
        /// The glove's colour-metal picture the screen is drawn into: the skin's for the left glove if the character wears one
        /// that changes it (the game's skins change the glove's textures by its material, as the wrist does), else the model's
        /// own, read from its LeftGlove material. <paramref name="skin"/> is the skin's whole change for the glove, if any.
        /// </summary>
        private static string GlovePicture(MyCharacter character, out MyTextureChange? skin)
        {
            skin = null;
            if (character.Components.TryGet(out MyAssetModifierComponent modifiers) && modifiers.AssetModifiers != null)
            {
                foreach (MyDefinitionId id in modifiers.AssetModifiers)
                {
                    Dictionary<MyStringId, MyTextureChange> changes = MyDefinitionManager.Static.GetAssetModifierDefinitionForRender(id.SubtypeId).SkinTextureChanges;
                    if (changes != null && changes.TryGetValue(GloveMaterialId, out MyTextureChange change))
                        skin = change;
                }
            }
            if (skin?.ColorMetalFileName != null)
                return skin.Value.ColorMetalFileName;

            var model = character.Model;
            if (!ReferenceEquals(model, pictureModel))
            {
                pictureModel = model;
                pictureOfModel = null;
                var meshes = model?.GetMeshList();
                if (meshes != null)
                {
                    foreach (var mesh in meshes)
                    {
                        if (mesh?.Material?.Name == GloveMaterial && mesh.Material.Textures != null
                            && mesh.Material.Textures.TryGetValue("ColorMetalTexture", out string path) && !string.IsNullOrEmpty(path))
                        {
                            pictureOfModel = path;
                            break;
                        }
                    }
                }
            }
            return pictureOfModel ?? GloveTexture;
        }

        /// <summary>
        /// The character's left glove shows the texture: its material's colour-metal texture changed to it, as the game puts an
        /// LCD's texture on a block's screen (MyRenderProxy.ChangeMaterialTexture), the skin's other textures for the glove
        /// kept. Sent when the render object or the skin changes, and again every <see cref="ResendSeconds"/>.
        /// </summary>
        private static void Swap(uint renderObject, MyTextureChange? skin, long now)
        {
            bool sameSkin = Nullable.Equals(skin, swappedSkin);
            if (renderObject == swappedOn && sameSkin && now < resendAt)
                return;
            if (renderObject != swappedOn || !sameSkin)
            {
                if (swappedOn != uint.MaxValue && renderObject != swappedOn)
                    Restore("the character's render object changed");
                Log.Info($"Wrist status: on the gauntlet's screen (the {GloveMaterial} material of render object {renderObject}{(skin.HasValue ? ", the skin's textures for it kept" : "")})");
            }
            MyRenderProxy.ChangeMaterialTexture(renderObject, GloveMaterial, Target, skin?.NormalGlossFileName, skin?.ExtensionsFileName, skin?.AlphamaskFileName);
            swappedOn = renderObject;
            swappedSkin = skin;
            resendAt = now + (long)(ResendSeconds * Stopwatch.Frequency);
        }

        /// <summary>The glove's own textures back (the skin's, if one changes it), as the game takes a skin off.</summary>
        private static void Restore(string why)
        {
            if (swappedOn == uint.MaxValue)
                return;
            if (swappedSkin is MyTextureChange skin)
                MyRenderProxy.ChangeMaterialTexture(swappedOn, GloveMaterial, skin.ColorMetalFileName, skin.NormalGlossFileName, skin.ExtensionsFileName, skin.AlphamaskFileName);
            else
                MyRenderProxy.ChangeMaterialTexture(swappedOn, GloveMaterial);
            Log.Info($"Wrist status: the gauntlet's screen has its own picture back ({why})");
            swappedOn = uint.MaxValue;
            swappedSkin = null;
        }

        // ---- the frame ----

        private static void Draw()
        {
            if (broken || errors >= 3)
                return;
            if (!hookedDraw || !hookedRedirect)
            {
                broken = true;
                return;
            }
            long start = Stopwatch.GetTimestamp();
            try
            {
                Frame();
            }
            catch (Exception e)
            {
                recording = false;
                Log.Error(e, ++errors < 3 ? "Wrist status failed this frame" : "Wrist status failed again; the screen is off for this run and the HUD's bars are back");
                if (errors >= 3)
                    ReleaseTexture("the screen failed");
            }
            long spent = Stopwatch.GetTimestamp() - start;
            Interlocked.Increment(ref costFrames);
            Interlocked.Add(ref costTicks, spent);
            if (spent > Interlocked.Read(ref costMaxTicks))
                Interlocked.Exchange(ref costMaxTicks, spent);
        }

        // The Perf line: the game thread's frame (laying out and sending the sprites, the glove's material), and the render worker's
        // drawing of the sprites into the screen's texture (MyOffscreenRenderer.RenderWork, recorded on its deferred context).
        private static long costFrames, costTicks, costMaxTicks, renderDraws, renderTicks;

        /// <summary>For the Perf line (<see cref="PerfMeter"/>): what the screen cost since the last one.</summary>
        internal static string PerfText()
        {
            long frames = Interlocked.Exchange(ref costFrames, 0);
            long ticks = Interlocked.Exchange(ref costTicks, 0);
            long max = Interlocked.Exchange(ref costMaxTicks, 0);
            long draws = Interlocked.Exchange(ref renderDraws, 0);
            long drawTicks = Interlocked.Exchange(ref renderTicks, 0);
            if (!Active)
                return "; wrist status off";
            if (frames == 0)
                return "; wrist status no frames";
            double ms = 1000.0 / Stopwatch.Frequency;
            return string.Format(CultureInfo.InvariantCulture, "; wrist status {0:0.000} ms/frame avg / {1:0.000} max on the game thread, {2:0.000} ms a draw x {3:0.00} draws a frame on a render worker",
                ticks * ms / frames, max * ms, draws == 0 ? 0.0 : drawTicks * ms / draws, (double)draws / frames);
        }

        private static void Frame()
        {
            long now = Stopwatch.GetTimestamp();
            float seconds = lastFrame == 0 ? 0f : (float)Math.Min(0.1, (now - lastFrame) / (double)Stopwatch.Frequency);
            lastFrame = now;

            if (!Active)
            {
                string why = Off ? "off (debug WristStatus=0)" : !VRSettings.WristStatus ? "off (setting)" : "off (no VR)";
                State(why);
                ReleaseTexture("the screen is " + why);
                return;
            }
            MySession session = MySession.Static;
            if (session != unloaded)
                unloaded = null;
            else if (session != null)
            {
                State("not drawn: the session is unloading");
                return;
            }
            MyCharacter character = session?.LocalCharacter;
            if (character == null || character.IsDead || character.Closed)
            {
                State("not drawn: no live character");
                Restore("no live character");
                return;
            }
            Face face = FaceOf(character);
            if (face == null)
            {
                State("not drawn: the character's model is not an astronaut's with the gauntlet's screen", character.Model?.AssetName);
                Restore("the character's model has no known gauntlet screen");
                return;
            }
            uint renderObject = character.Render?.GetRenderObjectID() ?? uint.MaxValue;
            if (renderObject == uint.MaxValue)
            {
                State("not drawn: the character is not in the render");
                return;
            }
            if (!Build())
            {
                State("not drawn: the HUD has no status bars to show");
                Restore("the HUD has no status bars to show");
                return;
            }

            // Brighter while looked at: the gaze within the show angle of the screen and the screen turned to the eyes, held a
            // moment; dim again once past the hide angles for a moment. Eyes that are not the character's do not look at it.
            bool placed = TryScreen(character, face, out Vector3D centre, out Vector3 normal);
            MatrixD eyes = MyTransparentGeometry.HasCamera ? MySector.MainCamera.WorldMatrix : MatrixD.Identity;
            Vector3D toScreen = centre - eyes.Translation;
            double distance = toScreen.Length();
            bool near = placed && MyTransparentGeometry.HasCamera && distance > 0.01 && distance <= FarthestEyes;
            float gaze = 180f, faced = 180f;
            if (near)
            {
                Vector3 toward = (Vector3)(toScreen / distance);
                gaze = Degrees((Vector3)eyes.Forward, toward);
                faced = Degrees(normal, -toward);
            }
            bool want = near && (looked
                ? !(gaze > WristLook.HideGazeDegrees || faced > WristLook.HideFaceDegrees)
                : gaze <= WristLook.ShowGazeDegrees && faced <= WristLook.ShowFaceDegrees);
            if (want != looked)
            {
                lookHeld += seconds;
                if (lookHeld >= (want ? WristLook.ShowSeconds : WristLook.HideSeconds))
                {
                    looked = want;
                    lookHeld = 0f;
                    Log.Info($"Wrist status: {(looked ? "looked at, full brightness" : "looked away, dim")} (gaze {gaze:0} deg off it, its face {faced:0} deg from the eyes)");
                }
            }
            else
            {
                lookHeld = 0f;
            }
            float goal = looked ? FullLevel : DimLevel;
            float step = seconds / FadeSeconds * (FullLevel - DimLevel);
            level = level < goal ? Math.Min(goal, level + step) : Math.Max(goal, level - step);

            // As an LCD draws (MyRenderComponentScreenAreas.RenderSpritesToTexture), when what it shows has changed: the
            // sprites to the texture's name, then the offscreen renderer is asked for it, cleared with alpha 0; then the
            // glove's material is given the texture.
            string picture = GlovePicture(character, out MyTextureChange? skin);
            EnsureTexture();
            Redraw(picture, MathHelper.Clamp(level * Brightness, 0f, 1f), now);
            Swap(renderObject, skin, now);

            screenNote = $"; on the {face.Name} astronaut's gauntlet screen, {face.Width * 100f:0.#} x {face.Height * 100f:0.#} cm, drawn into {picture}"
                         + (placed ? $", {distance * 100.0:0} cm from the eyes" : ", its bone not found");
            State($"on the gauntlet's screen ({(character.IsSitting ? "seated" : "on foot")}, the {face.Name} astronaut)");
            Values(now);
        }

        private static float Degrees(Vector3 a, Vector3 b)
        {
            float la = a.Length(), lb = b.Length();
            if (!(la > 1e-6f) || !(lb > 1e-6f))
                return 180f;
            return MathHelper.ToDegrees((float)Math.Acos(MathHelper.Clamp(Vector3.Dot(a, b) / (la * lb), -1f, 1f)));
        }

        /// <summary>The character's model's screen: the male or female astronaut's, by the model's name (null: neither).</summary>
        private static Face FaceOf(MyCharacter character)
        {
            string name = character.Model?.AssetName;
            if (string.IsNullOrEmpty(name))
                return null;
            name = name.Replace('/', '\\');
            if (name.EndsWith(FemaleFace.Model, StringComparison.OrdinalIgnoreCase))
                return FemaleFace;
            if (name.EndsWith(MaleFace.Model, StringComparison.OrdinalIgnoreCase))
                return MaleFace;
            return null;
        }

        // The forearm bone's index in the character it was found in.
        private static MyCharacter boneOf;
        private static int boneIndex = -1;

        /// <summary>
        /// The screen's middle and face in the world as the arm is drawn: the bind-pose point and face skinned by
        /// <see cref="PlateBone"/> as the game skins them (the bone's skin transform, then its absolute transform, then the
        /// character's world matrix). False with no such bone.
        /// </summary>
        private static bool TryScreen(MyCharacter character, Face face, out Vector3D centre, out Vector3 normal)
        {
            centre = Vector3D.Zero;
            normal = Vector3.Up;
            var bones = character.AnimationController?.CharacterBones;
            if (bones == null || character.PositionComp == null)
                return false;
            if (!ReferenceEquals(boneOf, character) || boneIndex < 0 || boneIndex >= bones.Length || bones[boneIndex]?.Name != PlateBone)
            {
                boneOf = character;
                character.AnimationController.FindBone(PlateBone, out boneIndex);
            }
            if (boneIndex < 0 || boneIndex >= bones.Length || bones[boneIndex] == null)
                return false;
            var bone = bones[boneIndex];
            MatrixD world = (MatrixD)(bone.SkinTransform * bone.AbsoluteTransform) * character.PositionComp.WorldMatrixRef;
            centre = Vector3D.Transform(face.Centre, world);
            normal = Vector3.Normalize((Vector3)Vector3D.TransformNormal(Vector3.Cross(face.Right, face.Up), world));
            return centre.IsValid() && normal.IsValid();
        }

        // ---- the screen's picture ----

        // At most this many redraws a second, like a game LCD; each is the offscreen renderer's whole job, see Redraw.
        private const int RedrawsPerSecond = 10;

        // A redraw at least this often even when nothing shown changed (see Redraw).
        private const long KeepAliveSeconds = 5;

        // Game thread: whether the texture holds a picture (since it was made), the signature of that picture, and when it was
        // sent; redraws and frames counted for the values line since its last one, and in the screen's first minute.
        private static bool drawnOnce, minuteSaid;
        private static ulong drawnSignature;
        private static long drawnAt, minuteStart, countFrom;
        private static int redraws, redrawFrames, minuteRedraws, minuteFrames;

        /// <summary>
        /// The texture's picture, sent again only when what it shows has changed, at most <see cref="RedrawsPerSecond"/>
        /// times a second (a change waits for the next allowed frame, so none is lost). Each frame the wrist's controls are
        /// drawn into a list instead of sent (<see cref="Record"/>), and a signature is made of what they would draw, to the
        /// texture's pixel and the colour's 8 bits, with the glove's picture, the plate's opacity and the dimming: a bar that
        /// moves less than a pixel, or a value whose text and bar look the same, draws nothing new. A redraw is the whole
        /// texture: the game's offscreen renderer cannot draw part of one. MyOffscreenRenderer.RenderWork hands the sprites to
        /// MyRender11.DrawSpritesOffscreen, which clears the whole render target first (ClearRtv, with the clear colour, or 0
        /// without one), and then makes all the texture's mips again (GenerateMips); and a texture it finds without a render
        /// target view is reset and cleared on the render thread (SubmitWork). So the glove's picture is drawn in again with
        /// each redraw, under the screen.
        /// </summary>
        private static void Redraw(string picture, float shade, long now)
        {
            float opacity = MySandboxGame.Config?.HUDBkOpacity ?? 0.6f;
            Color plate = MyGuiControlBase.ApplyColorMaskModifiers(Vector4.One, true, opacity);
            int dim = (int)Math.Round((1f - shade) * 255f);
            signature = 14695981039346656037UL;
            Mix(picture);
            Mix((int)plate.PackedValue);
            Mix(dim);
            Record(opacity);

            if (minuteStart == 0)
                minuteStart = now;
            if (countFrom == 0)
                countFrom = now;
            redrawFrames++;
            minuteFrames++;
            // Also every KeepAliveSeconds when nothing changed, so a texture the renderer cleared (a device reset, or one it
            // found without a render target view) or a sprite drawn before its texture loaded is not left on the screen.
            bool send = !drawnOnce || (signature != drawnSignature && now - drawnAt >= Stopwatch.Frequency / RedrawsPerSecond)
                        || now - drawnAt >= KeepAliveSeconds * Stopwatch.Frequency;
            if (send)
            {
                Send(picture, plate, dim);
                drawnOnce = true;
                drawnSignature = signature;
                drawnAt = now;
                redraws++;
                minuteRedraws++;
            }
            if (!minuteSaid && now - minuteStart >= 60L * Stopwatch.Frequency)
            {
                minuteSaid = true;
                Log.Info($"Wrist status: the screen was redrawn {minuteRedraws} times in its first minute, over {minuteFrames} frames (at most {RedrawsPerSecond} a second, each the whole {TextureSize} x {TextureSize} texture)");
            }
        }

        /// <summary>
        /// The wrist's controls drawn into <see cref="calls"/>, laid out on the screen's part of the texture, and their
        /// signature mixed into <see cref="signature"/>; nothing is sent.
        /// </summary>
        private static void Record(float opacity)
        {
            // The inventory row and the line on the outside sit under the last bar: one row higher when the game shows no food bar.
            var origin = new Vector2(ScreenPixels.X, ScreenPixels.Y);
            bool food = foodShown == null || foodShown.Eval();
            if (lower != null)
            {
                Vector2 at = origin + new Vector2(0f, food ? 0f : -RowStep * PixelsPerUnit);
                if (lower.Position != at)
                    lower.Position = at;
            }

            calls.Clear();
            recording = true;
            try
            {
                foreach (MyStatControls controls in wrist)
                {
                    if (controls != lower && controls.Position != origin)
                        controls.Position = origin;
                    controls.RegisterAlphaMultiplier(VisualStyleCategory.Background, opacity);
                    controls.Draw(1f, 1f);
                }
            }
            finally
            {
                recording = false;
            }
        }

        /// <summary>
        /// The picture, to the wrist's target, and the offscreen renderer asked to draw it: the glove's colour-metal picture,
        /// all of it; the screen's panel painted the bezel's grey and its lit part black; the HUD's left plate there at the
        /// HUD's background opacity (as MyStatControlImage.Draw colours it: the colour mask times the alpha, all four
        /// channels); the controls' recorded sprites and texts over it; then the lit part darkened by <paramref name="dim"/>
        /// of 255.
        /// </summary>
        private static void Send(string picture, Color plate, int dim)
        {
            var whole = new RectangleF(0f, 0f, TextureSize, TextureSize);
            MyRenderProxy.DrawSprite(picture, ref whole, null, Color.White, 0f, true, true, Target);
            Sprite(PanelPixels, Bezel);
            Sprite(ScreenPixels, Color.Black);
            RectangleF screen = ScreenPixels;
            MyRenderProxy.DrawSprite(PlateTexture, ref screen, PlateSource, plate, 0f, true, true, Target);
            SendCalls();
            if (dim > 0)
                Sprite(ScreenPixels, new Color(0, 0, 0, dim));
            MyRenderProxy.RenderOffscreenTexture(Target, Vector2.One, Clear);
            if (!sentFromSaid)
            {
                sentFromSaid = true;
                Log.Info($"Wrist status: the screen's sprites are sent from thread {ThreadName()} (the game thread)");
            }
        }
        private static void Sprite(RectangleF rectangle, Color color) =>
            MyRenderProxy.DrawSprite(BlankTexture, ref rectangle, null, color, 0f, true, true, Target);

        /// <summary>A line when the screen's state changes; <paramref name="detail"/> is said with it, and is no change on its own.</summary>
        private static void State(string text, string detail = null)
        {
            if (text == lastState)
                return;
            lastState = text;
            Log.Info("Wrist status: " + text + (detail == null ? string.Empty : ", " + detail));
        }

        /// <summary>The fields of MyStatControls' StatBinding (a private class): the style and the control it made.</summary>
        private static bool BindingFields(object binding)
        {
            if (styleField == null && binding != null)
            {
                styleField = binding.GetType().GetField("Style", BindingFlags.Public | BindingFlags.Instance);
                controlField = binding.GetType().GetField("Control", BindingFlags.Public | BindingFlags.Instance);
            }
            return styleField != null && controlField != null;
        }

        // ---- the wrist's controls ----

        /// <summary>The wrist's MyStatControls for the HUD the game has now, built the first time and again for a new one or a new session.</summary>
        private static bool Build()
        {
            MyHudDefinition hud = MyHud.HudDefinition;
            MySession session = MySession.Static;
            if (hud == builtFor && session == builtIn && builtAll == ShowAll)
                return wrist.Count > 0;
            builtFor = hud;
            builtIn = session;
            builtAll = ShowAll;
            wrist.Clear();
            lower = null;
            foodShown = null;
            MyObjectBuilder_StatControls[] definitions = hud?.StatControls;
            if (definitions == null)
                return false;

            MyObjectBuilder_StatControls icons = null, outside = null, inventory = null;
            var bars = new List<MyObjectBuilder_StatControls>();
            foreach (MyObjectBuilder_StatControls definition in definitions)
            {
                if (definition?.StatStyles == null)
                    continue;
                foreach (MyObjectBuilder_StatVisualStyle style in definition.StatStyles)
                {
                    if (icons == null && style is MyObjectBuilder_ImageStatVisualStyle image && image.Texture == LeftPlate)
                        icons = definition;
                    if (style is MyObjectBuilder_ProgressBarStatVisualStyle && style.StatId == Health && !bars.Contains(definition))
                        bars.Add(definition);
                    if (outside == null && style is MyObjectBuilder_TextStatVisualStyle && style.StatId == EnvOxygen)
                        outside = definition;
                    if (inventory == null && style is MyObjectBuilder_ProgressBarStatVisualStyle && style.StatId == Inventory)
                        inventory = definition;
                }
            }

            var built = new StringBuilder();
            if (icons != null)
            {
                List<MyObjectBuilder_StatVisualStyle> row = IconRow(icons);
                int suit = row.Count;
                row.AddRange(RadiationPanelParts(icons));
                if (row.Count > 0)
                {
                    wrist.Add(Make(row, new Always()));
                    built.Append($"{suit} icon parts, {row.Count - suit} radiation panel parts");
                }
            }
            int barRows = 0;
            foreach (MyObjectBuilder_StatControls definition in bars)
            {
                ConditionBase when = FoodTerm(definition.VisibleCondition);
                if (ShowAll && when != null)
                {
                    // Debug: the layout with food (it has the cryo mark) whether the world has food or not.
                    if (!Food(when))
                        continue;
                    when = null;
                }
                List<MyObjectBuilder_StatVisualStyle> rows = BarRows(definition, out int count);
                if (rows.Count == 0)
                    continue;
                if (when != null && Food(when) && foodShown == null)
                    foodShown = when;
                barRows = Math.Max(barRows, count);
                wrist.Add(Make(rows, when ?? new Always()));
                built.Append($", {count} bars with {rows.Count} parts");
            }

            // Under the most bars any of them shows (the food one); moved up a row while the game shows no food bar.
            var under = new List<MyObjectBuilder_StatVisualStyle>();
            float inventoryRow = RowFirst + Math.Max(barRows, 1) * RowStep;
            if (inventory != null)
            {
                List<MyObjectBuilder_StatVisualStyle> row = InventoryRow(inventory, inventoryRow);
                under.AddRange(row);
                built.Append($", {row.Count} inventory parts");
            }
            if (outside != null)
            {
                List<MyObjectBuilder_StatVisualStyle> line = OutsideLine(outside, inventoryRow + (inventory != null ? EnvBelow : 0f));
                under.AddRange(line);
                built.Append($", {line.Count} parts of the line on the outside");
            }
            if (under.Count > 0)
            {
                lower = Make(under, new Always());
                wrist.Add(lower);
            }
            Log.Info($"Wrist status: built from the HUD's own styles: {built}{(ShowAll ? "; WristStatusAll: the radiation panel and the marks drawn all the time" : "")}");
            return wrist.Count > 0;
        }

        private static MyStatControls Make(List<MyObjectBuilder_StatVisualStyle> styles, ConditionBase visible)
        {
            var definition = new MyObjectBuilder_StatControls
            {
                Position = Vector2.Zero,
                OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
                ApplyHudScale = false,
                VisibleCondition = visible,
                StatStyles = styles.ToArray(),
            };
            var controls = new MyStatControls(definition, PixelsPerUnit);
            Ours.Add(controls, null);
            return controls;
        }

        /// <summary>A copy of one of the HUD's styles, with the condition the game gave it (not ours, if it is left out of the HUD now).</summary>
        private static T Clone<T>(T style) where T : MyObjectBuilder_StatVisualStyle
        {
            var copy = (T)CloneMethod.Invoke(style, null);
            copy.VisibleCondition = OriginalCondition(style);
            return copy;
        }

        /// <summary>The condition the game gave a style: not ours or <see cref="HudToolbar"/>'s, if either leaves it out of the HUD now.</summary>
        private static ConditionBase OriginalCondition(MyObjectBuilder_StatVisualStyle style)
        {
            if (Originals.TryGetValue(style, out Original original))
                return original.Condition;
            if (HudToolbar.TryGetOriginalCondition(style, out ConditionBase condition))
                return condition;
            return style.VisibleCondition;
        }

        private static bool In(MyStringHash id, MyStringHash[] set) => Array.IndexOf(set, id) >= 0;

        /// <summary>The helmet, jetpack and magboots, broadcast and lamp icons on their backgrounds, in the game's four slots.</summary>
        private static List<MyObjectBuilder_StatVisualStyle> IconRow(MyObjectBuilder_StatControls definition)
        {
            var row = new List<MyObjectBuilder_StatVisualStyle>();
            float k = SlotSize / GameSlotSize;
            foreach (MyObjectBuilder_StatVisualStyle style in definition.StatStyles)
            {
                if (!(style is MyObjectBuilder_ImageStatVisualStyle image))
                    continue;
                if (Math.Abs(style.OffsetPx.Y - GameSlotY) > 1f || !(In(style.StatId, SuitStats) || In(image.Texture, SuitTextures)))
                    continue;
                int slot = (int)Math.Round((style.OffsetPx.X - GameSlotLeft) / GameSlotStep);
                if (slot < 0 || slot > 3)
                    continue;
                MyObjectBuilder_ImageStatVisualStyle placed = Clone(image);
                Vector2 inSlot = new Vector2(style.OffsetPx.X - (GameSlotLeft + slot * GameSlotStep), style.OffsetPx.Y - GameSlotY) * k;
                placed.OffsetPx = new Vector2(SlotLeft + slot * (SlotSize + SlotGap), SlotTop) + inSlot;
                placed.SizePx = style.SizePx * k;
                row.Add(placed);
            }
            return row;
        }

        /// <summary>A part of the left plate's radiation panel: its panel, icon, label, bar and value (all shown while exposed).</summary>
        private static bool IsRadiation(MyObjectBuilder_StatVisualStyle style)
        {
            if (style.StatId == Radiation)
                return true;
            if (style is MyObjectBuilder_ImageStatVisualStyle image && In(image.Texture, RadiationTextures))
                return true;
            return OriginalCondition(style) is StatCondition condition && condition.StatId == Radiation;
        }

        /// <summary>
        /// The radiation panel as the game lays it out, scaled as a whole to the top band right of the icons (the game has it
        /// right of its icon row), its texts' scale with it.
        /// </summary>
        private static List<MyObjectBuilder_StatVisualStyle> RadiationPanelParts(MyObjectBuilder_StatControls definition)
        {
            var parts = new List<MyObjectBuilder_StatVisualStyle>();
            MyObjectBuilder_StatVisualStyle panel = null;
            foreach (MyObjectBuilder_StatVisualStyle style in definition.StatStyles)
            {
                if (style is MyObjectBuilder_ImageStatVisualStyle image && image.Texture == RadiationPanel)
                {
                    panel = style;
                    break;
                }
            }
            if (panel == null || !(panel.SizePx.X > 0f) || !(panel.SizePx.Y > 0f))
                return parts;
            float k = Math.Min((RadiationRight - RadiationLeft) / panel.SizePx.X, SlotSize / panel.SizePx.Y);
            var origin = new Vector2(RadiationLeft, SlotTop + (SlotSize - panel.SizePx.Y * k) / 2f);
            foreach (MyObjectBuilder_StatVisualStyle style in definition.StatStyles)
            {
                if (!IsRadiation(style))
                    continue;
                MyObjectBuilder_StatVisualStyle copy = Clone(style);
                copy.OffsetPx = origin + (style.OffsetPx - panel.OffsetPx) * k;
                copy.SizePx = style.SizePx * k;
                if (copy is MyObjectBuilder_TextStatVisualStyle text)
                    text.Scale = ((MyObjectBuilder_TextStatVisualStyle)style).Scale * k;
                if (ShowAll)
                    copy.VisibleCondition = new Always();
                parts.Add(copy);
            }
            return parts;
        }

        /// <summary>
        /// The bars, their icons, values and bottle marks, and the medicine and cryo marks, a row each in the game's order. Each
        /// part keeps its place relative to its bar (a mark with no stat goes with the nearest bar); the bar is wider and the
        /// value moves with its end.
        /// </summary>
        private static List<MyObjectBuilder_StatVisualStyle> BarRows(MyObjectBuilder_StatControls definition, out int count)
        {
            var bars = new List<MyObjectBuilder_ProgressBarStatVisualStyle>();
            foreach (MyObjectBuilder_StatVisualStyle style in definition.StatStyles)
            {
                if (style is MyObjectBuilder_ProgressBarStatVisualStyle bar && In(style.StatId, BarStats))
                    bars.Add(bar);
            }
            bars.Sort((a, b) => a.OffsetPx.Y.CompareTo(b.OffsetPx.Y));
            count = bars.Count;
            var rows = new List<MyObjectBuilder_StatVisualStyle>();
            if (bars.Count == 0)
                return rows;

            foreach (MyObjectBuilder_StatVisualStyle style in definition.StatStyles)
            {
                // The row: the bar of the same stat, or the nearest bar above or below.
                int row = -1;
                float nearest = float.MaxValue;
                for (int i = 0; i < bars.Count; i++)
                {
                    if (style.StatId != MyStringHash.NullOrEmpty && bars[i].StatId == style.StatId)
                    {
                        row = i;
                        break;
                    }
                    float d = Math.Abs(style.OffsetPx.Y - bars[i].OffsetPx.Y);
                    if (style.StatId == MyStringHash.NullOrEmpty && d < nearest)
                    {
                        nearest = d;
                        row = i;
                    }
                }
                if (row < 0)
                    continue;
                MyObjectBuilder_ProgressBarStatVisualStyle own = bars[row];
                float barTop = RowFirst + row * RowStep - BarHeight / 2f;
                float barEnd = own.OffsetPx.X + own.SizePx.X;
                MyObjectBuilder_StatVisualStyle copy = Clone(style);
                if (style is MyObjectBuilder_ProgressBarStatVisualStyle)
                {
                    copy.OffsetPx = new Vector2(BarLeft, barTop);
                    copy.SizePx = new Vector2(BarWidth, BarHeight);
                }
                else if (copy is MyObjectBuilder_TextStatVisualStyle text)
                {
                    text.OffsetPx = new Vector2(BarLeft + BarWidth + ValueGap + (style.OffsetPx.X - barEnd), barTop + (style.OffsetPx.Y - own.OffsetPx.Y));
                    text.Scale = ValueScale;
                }
                else
                {
                    copy.OffsetPx = new Vector2(BarLeft + (style.OffsetPx.X - own.OffsetPx.X), barTop + (style.OffsetPx.Y - own.OffsetPx.Y));
                    if (ShowAll && style.StatId == MyStringHash.NullOrEmpty)
                        copy.VisibleCondition = new Always();
                }
                rows.Add(copy);
            }
            return rows;
        }

        /// <summary>
        /// The inventory fill: the game's straight bar of it (the keyboard center plate's) as one more bar row, the backpack
        /// icon in the icons' column at their height.
        /// </summary>
        private static List<MyObjectBuilder_StatVisualStyle> InventoryRow(MyObjectBuilder_StatControls definition, float centre)
        {
            var row = new List<MyObjectBuilder_StatVisualStyle>();
            foreach (MyObjectBuilder_StatVisualStyle style in definition.StatStyles)
            {
                if (style is MyObjectBuilder_ProgressBarStatVisualStyle bar && style.StatId == Inventory)
                {
                    MyObjectBuilder_ProgressBarStatVisualStyle copy = Clone(bar);
                    float height = style.SizePx.Y > 0f ? style.SizePx.Y : BarHeight;
                    copy.OffsetPx = new Vector2(BarLeft, centre - height / 2f);
                    copy.SizePx = new Vector2(BarWidth, height);
                    row.Add(copy);
                }
                else if (style is MyObjectBuilder_ImageStatVisualStyle image && image.Texture == Backpack && style.SizePx.Y > 0f)
                {
                    MyObjectBuilder_ImageStatVisualStyle copy = Clone(image);
                    copy.SizePx = style.SizePx * (IconHeight / style.SizePx.Y);
                    copy.OffsetPx = new Vector2(BarLeft + IconColumn - copy.SizePx.X / 2f, centre - copy.SizePx.Y / 2f);
                    row.Add(copy);
                }
            }
            return row;
        }

        /// <summary>
        /// The oxygen and the temperature outside, side by side as the game has them: its icon and text for each level, an icon
        /// and its text left-aligned from each start.
        /// </summary>
        private static List<MyObjectBuilder_StatVisualStyle> OutsideLine(MyObjectBuilder_StatControls definition, float centre)
        {
            var line = new List<MyObjectBuilder_StatVisualStyle>();
            foreach (MyObjectBuilder_StatVisualStyle style in definition.StatStyles)
            {
                float left;
                if (style.StatId == EnvOxygen)
                    left = EnvLeft;
                else if (style.StatId == EnvTemperature)
                    left = EnvSecondLeft;
                else
                    continue;
                if (style is MyObjectBuilder_ImageStatVisualStyle image)
                {
                    MyObjectBuilder_ImageStatVisualStyle copy = Clone(image);
                    copy.OffsetPx = new Vector2(left, centre - EnvIcon / 2f);
                    copy.SizePx = new Vector2(EnvIcon);
                    line.Add(copy);
                }
                else if (style is MyObjectBuilder_TextStatVisualStyle text)
                {
                    MyObjectBuilder_TextStatVisualStyle copy = Clone(text);
                    copy.OffsetPx = new Vector2(left + EnvIcon + 7f, centre);
                    copy.SizePx = Vector2.Zero;
                    copy.TextAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER;
                    copy.Scale = EnvTextScale;
                    line.Add(copy);
                }
            }
            return line;
        }

        /// <summary>The part of a bars control's condition that picks it by whether the session uses food (the game has one with and one without).</summary>
        private static ConditionBase FoodTerm(ConditionBase condition)
        {
            if (condition is StatCondition stat)
                return stat.StatId == FoodRate ? stat : null;
            if (condition is Condition group && group.Terms != null)
            {
                foreach (ConditionBase term in group.Terms)
                {
                    ConditionBase found = FoodTerm(term);
                    if (found != null)
                        return found;
                }
            }
            return null;
        }

        private static bool Food(ConditionBase term) => term is StatCondition stat && stat.Operator == StatConditionOperator.Above;

        // ---- the HUD's own parts ----

        /// <summary>
        /// The HUD's copies of what the wrist shows are left out: always while "Status on the wrist" is on in VR, whether or not
        /// the screen is drawn this frame (third person, a camera view). They are back with the setting off, with
        /// RenderDebug WristStatus=0, without VR, and if the wrist screen failed or its hooks did. Read on the game thread,
        /// by <see cref="HudToolbar"/> too.
        /// </summary>
        internal static bool HudHidden => !Off && !hudBroken && !Failed && VRSettings.WristStatus && VRInput.Active;

        /// <summary>The wrist screen is off for this run: its hooks failed, or it failed three times.</summary>
        private static bool Failed => broken || errors >= 3;

        /// <summary>Why the HUD's copies are in or out now, for the log.</summary>
        private static string HudWhy()
        {
            if (Off)
                return "debug WristStatus=0";
            if (!VRInput.Active)
                return "no VR";
            if (!VRSettings.WristStatus)
                return "Status on the wrist is off";
            if (Failed)
                return "the wrist screen failed";
            return "Status on the wrist is on";
        }

        /// <summary>
        /// The center plates' parts the wrist shows, which <see cref="HudToolbar"/> leaves out while <see cref="HudHidden"/>
        /// (it gates those plates' parts already): the oxygen and temperature outside (icons and texts), and the inventory
        /// fill, as the keyboard plate's bar or the controller plate's gauge, with its backpack icon, the gauge's ring and the
        /// inventory key hint beside it.
        /// </summary>
        internal static bool TakesFromCenterPlate(MyObjectBuilder_StatVisualStyle style)
        {
            if (style == null)
                return false;
            if (style.StatId == EnvOxygen || style.StatId == EnvTemperature || style.StatId == Inventory)
                return true;
            if (style is MyObjectBuilder_ImageStatVisualStyle image && In(image.Texture, InventoryTextures))
                return true;
            return style is MyObjectBuilder_TextStatVisualStyle text && string.Equals(text.Text, InventoryKeyHint, StringComparison.Ordinal);
        }

        /// <summary>Each MyStatControls the HUD draws (not the wrist's): the bars and the left plate's are found the first time.</summary>
        private static void BeforeStatsDraw(MyStatControls __instance, float transitionAlpha)
        {
            if (hudBroken || Ours.TryGetValue(__instance, out _))
                return;
            try
            {
                if (!Gates.TryGetValue(__instance, out Gate gate))
                {
                    gate = Prepare(__instance);
                    Gates.Add(__instance, gate);
                }
                if (gate.Styles.Count == 0 && gate.Plate == null)
                    return;
                bool hide = HudHidden;
                gate.Apply(hide);
                if (gate.Bars && gate.Hidden != hide && gate.Definition?.VisibleCondition?.Eval() != false)
                {
                    gate.Hidden = hide;
                    Log.Info($"Wrist status: the HUD's status bars {(hide ? "left out" : "shown")} ({HudWhy()})");
                }
                if (gate.Plate != null)
                    Place(__instance, gate, hide);
                if (hide && gate.Plate != null)
                    DrawPlateCut(gate, transitionAlpha);
            }
            catch (Exception e)
            {
                hudBroken = true;
                Log.Error(e, "Wrist status: the HUD's bars could not be left out; they stay as the game draws them");
            }
        }

        // The HUD's center plates (the keyboard's and the controller's, each shown in its own controller mode): their
        // controls' definitions and the plates' pictures as the game laid them out, the latest HUD's.
        private static readonly MyStringHash[] CenterPlates = { MyStringHash.GetOrCompute("CenterPlate"), MyStringHash.GetOrCompute("CenterPlateController") };
        private static readonly MyObjectBuilder_StatControls[] centerDefinitions = new MyObjectBuilder_StatControls[2];
        private static readonly MyStatControlImage[] centerImages = new MyStatControlImage[2];

        /// <summary>
        /// The left plate's control where the game put it, or moved right by <see cref="SpeedShift"/> while the wrist has its
        /// left part (the bars and icons): the speed dial and the dampeners, alone on its right part, then sit in toward the
        /// HUD's middle. The control is moved whole (MyStatControls.Position moves its parts with it), so the dial, its texts,
        /// the dampeners and the plate's cut move together; it is put back where the game had it the frame the HUD's copies are
        /// back.
        /// </summary>
        private static void Place(MyStatControls controls, Gate gate, bool hide)
        {
            Vector2 at = controls.Position;
            if (gate.Home == null || at != gate.Placed)
                gate.Home = at;
            Vector2 home = gate.Home.Value;
            string against = null;
            float shift = hide ? SpeedShift(gate, home, at, out against) : 0f;
            var want = new Vector2(home.X + shift, home.Y);
            if (want != at)
                controls.Position = want;
            gate.Placed = want;
            if (Math.Abs(shift - gate.Shift) >= 0.5f)
            {
                gate.Shift = shift;
                MyStatControlImage plate = gate.Plate;
                Log.Info(shift > 0f
                    ? $"Wrist status: the HUD's speed dial and dampeners moved {shift:0} px right, up to the {against}: the left plate's speed part now x {plate.Position.X + plate.Size.X * PlateCut:0} to {plate.Position.X + plate.Size.X:0} of {MySandboxGame.ScreenSize.X}"
                    : "Wrist status: the HUD's speed dial and dampeners back where the game puts them");
            }
        }

        /// <summary>
        /// How far right (HUD pixels) the left plate's control moves while its left part is on the wrist: until the plate's
        /// right edge is the HUD's margin short of the left edge of the center plate in use (the keyboard's or the
        /// controller's, by its control's own condition), the margin being the left plate's own from the screen's edge (its
        /// position, 0.003 of the width in the Default HUD). Never left. At 1920 x 1080 and scale 1 with the keyboard's
        /// plate: the plate's right edge from 456 to 614, 158 px; the dial's middle from x 351 to 509. The last value is kept
        /// while no center plate shows (hud_mode 0, a turret).
        /// </summary>
        private static float SpeedShift(Gate gate, Vector2 home, Vector2 at, out string against)
        {
            against = null;
            MyStatControlImage plate = gate.Plate;
            float right = plate.Position.X + plate.Size.X - (at.X - home.X);
            float margin = (gate.Definition?.Position.X ?? 0f) * MySandboxGame.ScreenSize.X;
            for (int i = 0; i < CenterPlates.Length; i++)
            {
                if (centerImages[i] == null || centerDefinitions[i]?.VisibleCondition?.Eval() == false)
                    continue;
                against = $"{(i == 0 ? "keyboard's" : "controller's")} center plate (its left edge x {centerImages[i].Position.X:0}) less the HUD's margin ({margin:0.#} px)";
                return Math.Max(0f, centerImages[i].Position.X - margin - right);
            }
            against = "last center plate seen";
            return gate.Shift;
        }

        private static Gate Prepare(MyStatControls controls)
        {
            var gate = new Gate { Definition = DefinitionField?.GetValue(controls) as MyObjectBuilder_StatControls };
            if (!(BindingsField.GetValue(controls) is IEnumerable bindings))
                return gate;
            var styles = new List<KeyValuePair<MyObjectBuilder_StatVisualStyle, IMyStatControl>>();
            bool bars = false, plate = false;
            foreach (object binding in bindings)
            {
                if (!BindingFields(binding) || !(styleField.GetValue(binding) is MyObjectBuilder_StatVisualStyle style))
                    continue;
                var control = controlField?.GetValue(binding) as IMyStatControl;
                styles.Add(new KeyValuePair<MyObjectBuilder_StatVisualStyle, IMyStatControl>(style, control));
                bars |= style is MyObjectBuilder_ProgressBarStatVisualStyle && style.StatId == Health;
                plate |= style is MyObjectBuilder_ImageStatVisualStyle image && image.Texture == LeftPlate;
                if (style is MyObjectBuilder_ImageStatVisualStyle picture && control is MyStatControlImage drawn)
                {
                    int center = Array.IndexOf(CenterPlates, picture.Texture);
                    if (center >= 0)
                    {
                        centerDefinitions[center] = gate.Definition;
                        centerImages[center] = drawn;
                    }
                }
            }
            if (!bars && !plate)
                return gate;
            gate.Bars = bars;

            foreach (KeyValuePair<MyObjectBuilder_StatVisualStyle, IMyStatControl> pair in styles)
            {
                MyObjectBuilder_StatVisualStyle style = pair.Key;
                bool hide;
                if (bars)
                {
                    hide = true;
                }
                else if (style is MyObjectBuilder_ImageStatVisualStyle image && image.Texture == LeftPlate)
                {
                    gate.Plate = pair.Value as MyStatControlImage;
                    hide = gate.Plate != null;
                }
                else if (IsRadiation(style))
                {
                    hide = true;
                }
                else if (style is MyObjectBuilder_ImageStatVisualStyle icon)
                {
                    hide = Math.Abs(style.OffsetPx.Y - GameSlotY) <= 1f && (In(style.StatId, SuitStats) || In(icon.Texture, SuitTextures));
                }
                else
                {
                    hide = style is MyObjectBuilder_TextStatVisualStyle text && text.Text != null && Array.IndexOf(SuitKeyHints, text.Text) >= 0;
                }
                if (!hide)
                    continue;
                if (!Originals.TryGetValue(style, out Original original))
                {
                    if (ReferenceEquals(style.VisibleCondition, Never))
                        continue;
                    original = new Original { Condition = style.VisibleCondition };
                    Originals.Add(style, original);
                }
                // A world loaded since has new stats; the game only gave them to the condition the style had when the
                // controls were made (ours, if it was left out then).
                if (original.Condition != null && InitConditions != null)
                    InitConditions.Invoke(null, new object[] { original.Condition });
                gate.Styles.Add(style);
            }
            Log.Info($"Wrist status: {gate.Styles.Count} parts of the HUD's {(bars ? "status bars (with the medicine and cryo marks)" : "left plate (icon row and its key hints, the radiation panel, the plate cut to its speed half)")} are left out while \"Status on the wrist\" is on");
            return gate;
        }

        /// <summary>The left plate's right part, behind the speed dial and the dampeners, while its left part's bars and icons are out.</summary>
        private static void DrawPlateCut(Gate gate, float transitionAlpha)
        {
            ConditionBase visible = gate.Definition?.VisibleCondition;
            MyStatControlImage plate = gate.Plate;
            if (visible == null || !visible.Eval() || plate.Texture == null)
                return;
            Vector2I size = plate.Texture.SizePx.X > 0 && plate.Texture.SizePx.Y > 0 ? plate.Texture.SizePx : PlateTextureSize;
            int cut = (int)(size.X * PlateCut);
            var source = new Rectangle(cut, 0, size.X - cut, size.Y);
            float share = cut / (float)size.X;
            var destination = new RectangleF(plate.Position.X + plate.Size.X * share, plate.Position.Y, plate.Size.X * (1f - share), plate.Size.Y);
            MyRenderProxy.DrawSprite(plate.Texture.Path, ref destination, source,
                MyGuiControlBase.ApplyColorMaskModifiers(plate.ColorMask, true, transitionAlpha), 0f, false, true);
        }

        // ---- the log ----

        /// <summary>What the wrist shows against the game's own stats: on the first frame and every 10 s while shown.</summary>
        private static void Values(long now)
        {
            if (now < nextValuesAt)
                return;
            nextValuesAt = now + 10L * Stopwatch.Frequency;
            var line = new StringBuilder("Wrist status values (wrist / game):");
            foreach (MyStatControls controls in wrist)
            {
                var definition = DefinitionField.GetValue(controls) as MyObjectBuilder_StatControls;
                if (definition?.VisibleCondition != null && !definition.VisibleCondition.Eval())
                    continue;
                if (!(BindingsField.GetValue(controls) is IEnumerable bindings))
                    continue;
                foreach (object binding in bindings)
                {
                    if (!BindingFields(binding))
                        continue;
                    var style = styleField.GetValue(binding) as MyObjectBuilder_StatVisualStyle;
                    var control = controlField.GetValue(binding) as IMyStatControl;
                    if (!(style is MyObjectBuilder_TextStatVisualStyle text) || control == null || style.StatId == MyStringHash.NullOrEmpty)
                        continue;
                    if (text.Text == null || !text.Text.Contains("{STAT}"))
                        continue;
                    IMyHudStat stat = MyHud.Stats.GetStat(style.StatId);
                    if (stat == null)
                        continue;
                    line.Append(' ').Append(style.StatId.String).Append(' ').Append(control.StatString).Append(" / ").Append(stat.GetValueString())
                        .Append(" (").Append(stat.CurrentValue.ToString("0.###", CultureInfo.InvariantCulture)).Append(" of ")
                        .Append(stat.MaxValue.ToString("0.###", CultureInfo.InvariantCulture)).Append(");");
                }
            }
            IMyHudStat air = MyHud.Stats.GetStat(EnvOxygen);
            if (air != null)
                line.Append(" air outside ").Append(air.CurrentValue.ToString("0.##", CultureInfo.InvariantCulture));
            line.Append($"; HUD bars {(HudHidden ? "left out" : "shown")}; brightness {level * Brightness:0.00}{screenNote}");
            if (countFrom != 0)
                line.Append(string.Format(CultureInfo.InvariantCulture, "; redrawn {0} times in {1:0.0} s, over {2} frames",
                    redraws, (now - countFrom) / (double)Stopwatch.Frequency, redrawFrames));
            redraws = 0;
            redrawFrames = 0;
            countFrom = now;
            Log.Info(line.ToString());
        }
    }
}
