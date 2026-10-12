using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Sandbox.Definitions.GUI;
using Sandbox.Game.GUI;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using VRage.Game.ObjectBuilders.Definitions;
using VRage.Utils;
using VRageMath;
using VRageRender;

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
    /// MyRenderProxy.DestroyGeneratedTexture. (The game's RenderTarget billboard material is not used: it draws its
    /// sprites from inside the parallel transparent pass on the immediate context, which lost the device within seconds.)
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
    internal static partial class WristStatus
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
        /// screen picture, x 1397 to 1935 and y 1143 to 1418 (read from the texture). Painted the bezel's grey, which is all round it.
        /// </summary>
        private static readonly RectangleF PanelPixels = new RectangleF(1397f, 1143f, 539f, 276f);

        /// <summary>
        /// The lit part of the screen: where the glove's add map has its emissive (Hand_add.dds green 142, x 1402 to 1932 and
        /// y 1148 to 1414 in the colour-metal picture's pixels, its corners rounded about 14 pixels; read from the texture). The canvas
        /// fills it.
        /// </summary>
        private static readonly RectangleF ScreenPixels = new RectangleF(1402f, 1148f, 530f, 266f);

        /// <summary>The bezel's grey round the screen in the glove's picture (30, 30, 30, sampled from the texture).</summary>
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
        /// LeftPlate.png is 1070 x 380, opaque from x 35 and y 6.
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
            PatchRegistry.Apply("offscreen", () =>
            {
                // Watched only, for the log and the Perf line: the game's offscreen renderer taking the screen's sprites (where
                // it processes render messages), handing them to a worker (render thread) and drawing them into its texture (the worker).
                Type offscreen = AccessTools.TypeByName("VRage.Render11.Render.MyOffscreenRenderer");
                workMessageField = AccessTools.Field(AccessTools.Inner(offscreen, "MyData"), "Message")
                                   ?? throw new MissingFieldException("MyOffscreenRenderer.MyData", "Message");
                harmony.Patch(AccessTools.Method(offscreen, "Add"), prefix: new HarmonyMethod(typeof(WristStatus), nameof(Taken)));
                harmony.Patch(AccessTools.Method(offscreen, "SubmitWork"), prefix: new HarmonyMethod(typeof(WristStatus), nameof(Submitting)));
                harmony.Patch(AccessTools.Method(offscreen, "RenderWork"),
                    prefix: new HarmonyMethod(typeof(WristStatus), nameof(WorkStarting)), postfix: new HarmonyMethod(typeof(WristStatus), nameof(WorkDone)));
                hookedOffscreen = true;
            }, "Wrist status: the game's offscreen renderer could not be watched; the screen still draws, without its render-side log and cost");
            PatchRegistry.Apply("sprites", () =>
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
                if (count != 3)
                    throw new MissingMethodException($"Wrist status: {count} of the 3 sprite and text calls found");
                hookedRedirect = true;
            }, "Wrist status: the HUD's sprites could not be sent to the wrist; no screen on the wrist");
            PatchRegistry.Apply("frame", () =>
            {
                harmony.Patch(AccessTools.Method(typeof(MySession), nameof(MySession.DrawSync)),
                    postfix: new HarmonyMethod(typeof(WristStatus), nameof(Draw)));
                hookedDraw = true;
            }, "Wrist status: the frame could not be hooked; no screen on the wrist");
            if (!PatchRegistry.Apply("bars", () =>
                {
                    harmony.Patch(AccessTools.Method(typeof(MyStatControls), nameof(MyStatControls.Draw), new[] { typeof(float), typeof(float) }),
                        prefix: new HarmonyMethod(typeof(WristStatus), nameof(BeforeStatsDraw)));
                }, "Wrist status: the HUD's bars could not be hooked; they stay as the game draws them"))
                hudBroken = true;
            MySession.OnUnloading += () =>
            {
                unloaded = MySession.Static;
                ReleaseTexture("the session is unloading");
            };
            Log.Info($"Wrist status: hooked (frame {hookedDraw}, sprites {hookedRedirect}, offscreen renderer watched {hookedOffscreen})");
        }
    }
}
