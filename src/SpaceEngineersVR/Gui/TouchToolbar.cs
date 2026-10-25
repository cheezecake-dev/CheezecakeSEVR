using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Sandbox.Game.Components;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems.TextSurfaceScripts;
using Sandbox.Game.Localization;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using SpaceEngineersVR.Hands;
using SpaceEngineersVR.Tracking;
using VRage;
using VRage.Audio;
using VRage.Game;
using VRage.Game.GUI.TextPanel;
using VRage.Utils;
using VRageMath;
using VRageRender.Messages;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// The ship's toolbar on the cockpit's own screens, touched to use it: the page's nine slots in the game's toolbar
    /// look (its slot frames, the items' icons, the slot numbers, On/Off as the game writes it), the page and two page
    /// buttons. A tap on a slot activates it as its number key does in the seat; the arrows turn the page as the game's
    /// page keys do. A screen out of a seated arm's reach takes the dominant hand's laser and trigger instead.
    /// </summary>
    /// <remarks>
    /// Which screens: the player sets a screen to it in the game's own screen settings (Content: Script, "Touch Toolbar",
    /// <see cref="TouchToolbarScript"/>), saved with the world as any content choice is. With none set to it, the
    /// setting "Touch toolbar on the cockpit screen" (<see cref="VRSettings.TouchToolbar"/>) puts it on the central-most
    /// screen while that screen still shows what the cockpit came with (an app the player picks there wins); RenderDebug TouchToolbarScreen=n puts it on screen n (0-based) instead, and TouchToolbar=0 turns it all off.
    /// The pilot's picture is local only, and nothing of the block changes: this machine's picture of the screen area is
    /// swapped for the toolbar drawn into a texture of our own (the game's offscreen sprite renderer, as an LCD script's
    /// sprites are drawn), the screen's material pointed at it with the game's own
    /// <see cref="MyRenderComponentScreenAreas.ChangeTexture"/>, a render message on this client: the screen's own mesh and
    /// material, lit as the game's content is because the texture is cleared with the panel's alpha (see
    /// <see cref="ClearColor"/>). While it is ours, the
    /// panel's own calls to change that area's texture are recorded and held back; when the pilot leaves the seat (or the
    /// block stops working, or the screen is no longer wanted), the last texture the panel asked for is put back and the
    /// panel is made to send it again on its next update.
    /// </remarks>
    internal static partial class TouchToolbar
    {
        /// <summary>Debug: no toolbar on any cockpit screen (<see cref="Rendering.RenderDebug"/> TouchToolbar=0).</summary>
        public static bool Off { get; set; }

        /// <summary>Debug: the screen (surface index) to use when none is set to Touch Toolbar; -1 picks the central-most one (TouchToolbarScreen).</summary>
        public static int ScreenOverride { get; set; } = -1;

        /// <summary>
        /// A screen whose middle is farther than this from the dominant shoulder is out of the drawn arm's reach: the laser
        /// and trigger tap it. Chosen to separate the screens of a large cockpit: the lowest is in reach (about 0.56 m from the
        /// eyes), the others are 1.1-1.2 m from the shoulder.
        /// </summary>
        private const double ArmReachMeters = 0.8;

        /// <summary>The same, from the eyes, while the shoulder is not known.</summary>
        private const double EyeReachMeters = 0.75;

        /// <summary>
        /// A screen goes out of reach this far past the reach and comes back in this far inside it, metres. The hysteresis
        /// keeps a screen that sits right at the reach limit from turning the laser on and off with the solved shoulder's sway.
        /// </summary>
        private const double ReachSlack = 0.05;

        /// <summary>
        /// The dominant fingertip or palm this close in front of a screen's glass, or through it, is touching it: the hand is
        /// the touch's then, and draws no laser and taps nothing with the trigger, metres.
        /// </summary>
        private const double TouchOwns = 0.10;

        /// <summary>How long the pick waits, after sitting, for the shoulder and every screen's glass before it picks with what it has.</summary>
        private const int PickWaitFrames = 180;

        /// <summary>The pick waits this long after sitting down, so the shoulder is the seated one.</summary>
        private const int SettleFrames = 30;

        /// <summary>How long after a slot is activated its one log line is written, so it can say what the game made of it.</summary>
        private const int LogAfterFrames = 20;

        internal const int Slots = 9;
        private const int LeftArrow = 100, RightArrow = 101, NoTarget = -1;

        /// <summary>A pixel no screen has: the laser let go off the glass.</summary>
        private static readonly Vector2 OffScreen = new Vector2(-10000f, -10000f);

        private const string ItemInactive = @"Textures\GUI\Icons\HUD 2017\ToolbarItemInactive.png";
        private const string ItemActive = @"Textures\GUI\Icons\HUD 2017\ToolbarItemActive.png";
        private const string PageActive = @"Textures\GUI\Icons\HUD 2017\ToolbarPageActive.png";
        private const string Triangle = @"Textures\Sprites\Triangle.dds";
        private const string White = @"Textures\Sprites\WhiteSprite.DDS";

        internal static readonly Color Background = new Color(8, 14, 19);
        internal static readonly Color IconBlue = new Color(192, 237, 249);
        internal static readonly Color OffGrey = new Color(140, 153, 161);
        internal static readonly Color NameColor = new Color(205, 220, 228);

        private static readonly Vector4 LaserColor = new Vector4(0.55f, 0.85f, 1f, 0.6f);
        private static readonly MyStringId LaserMaterial = MyStringId.GetOrCompute("SquareIgnoreDepth");
        private static readonly MyStringId DotMaterial = MyStringId.GetOrCompute("WhiteDot");

        /// <summary>The texture each screen area last asked for (null: hidden), whoever holds it now.</summary>
        private static readonly ConditionalWeakTable<MyRenderComponentScreenAreas, Dictionary<int, string>> wanted =
            new ConditionalWeakTable<MyRenderComponentScreenAreas, Dictionary<int, string>>();

        private static readonly AccessTools.FieldRef<MyTextPanelComponent, string> previousTexture =
            AccessTools.FieldRefAccess<MyTextPanelComponent, string>("m_previousTextureID");

        // The screens' glass (Hands.ScreenGlass): filled and read in one go, as the pool's entries are reused on the next fill.
        private static readonly List<ScreenGlass.Screen> glass = new List<ScreenGlass.Screen>(), glassPool = new List<ScreenGlass.Screen>();

        private static readonly List<Pending> pending = new List<Pending>();
        private static readonly StringBuilder key = new StringBuilder();

        /// <summary>The screens held now, each with its own texture, layout and finger.</summary>
        private static readonly List<Claim> claims = new List<Claim>();
        private static readonly List<int> want = new List<int>();
        private static bool applying;
        private static int frame, errors, retryAt, seatedAt;
        private static long refusedFor, seatedIn;
        private static string lastCase;

        // The laser, worked out in the seat's input and drawn in the frame's draw (from the hand where that draw has it).
        private static bool laserHit, laserDown;
        private static Claim laserOn, laserAt;
        private static string laserOwner; // who had the hand when last logged: null (the laser), "control" or "touch"
        private static long laserOwnerLoggedAt;

        public static void Patch(Harmony harmony)
        {
            try
            {
                harmony.Patch(AccessTools.Method(typeof(MyRenderComponentScreenAreas), nameof(MyRenderComponentScreenAreas.ChangeTexture)),
                    prefix: new HarmonyMethod(typeof(TouchToolbar), nameof(BeforeChangeTexture)));
                harmony.Patch(AccessTools.Method(typeof(MySession), nameof(MySession.DrawSync)),
                    postfix: new HarmonyMethod(typeof(TouchToolbar), nameof(Update)));
                harmony.Patch(AccessTools.Method(typeof(MyTextSurfaceScriptFactory), nameof(MyTextSurfaceScriptFactory.LoadScripts)),
                    postfix: new HarmonyMethod(typeof(TouchToolbar), nameof(AfterLoadScripts)));
                AfterLoadScripts();
                ScreenTouch.Touched += OnTouched;
                Log.Info("Touch toolbar: hooked (the ship's toolbar on a cockpit screen while seated, local only; \"Touch Toolbar\" among the screens' scripts)");
            }
            catch (Exception e)
            {
                PatchRegistry.Lost(e, "Touch toolbar could not be hooked; cockpit screens keep their own content");
                throw;
            }
        }

        // ---- "Touch Toolbar" among the game's screen scripts. ----

        private static readonly AccessTools.FieldRef<MyTextSurfaceScriptFactory, Dictionary<string, MyTextSurfaceScriptFactory.ScriptInfo>> factoryScripts =
            AccessTools.FieldRefAccess<MyTextSurfaceScriptFactory, Dictionary<string, MyTextSurfaceScriptFactory.ScriptInfo>>("m_scripts");

        /// <summary>
        /// The game fills its list of screen scripts at each session's load from its own assembly, the world's mods and the
        /// plugins it was started with (MyTextSurfaceScriptFactory.LoadScripts); ours is added straight after, before the
        /// world's blocks pick their scripts (a block whose script is not in the list forgets it).
        /// </summary>
        private static void AfterLoadScripts()
        {
            try
            {
                MyTextSurfaceScriptFactory factory = MyTextSurfaceScriptFactory.Instance;
                if (factory == null)
                    return;
                factoryScripts(factory)[TouchToolbarScript.Id] = new MyTextSurfaceScriptFactory.ScriptInfo
                {
                    Id = TouchToolbarScript.Id,
                    DisplayName = MyStringId.GetOrCompute(TouchToolbarScript.DisplayName),
                    Type = typeof(TouchToolbarScript),
                };
                Log.Info($"Touch toolbar: \"{TouchToolbarScript.DisplayName}\" ({TouchToolbarScript.Id}) is among the screens' scripts ({factory.Scripts.Count} in all)");
            }
            catch (Exception e)
            {
                Log.Error(e, "Touch toolbar: could not add \"Touch Toolbar\" to the screens' scripts");
            }
        }

        // ---- The screen's texture: the panel's calls are recorded, and held back while the area is ours. ----

        private static bool BeforeChangeTexture(MyRenderComponentScreenAreas __instance, int area, string path)
        {
            if (applying)
                return true;
            try
            {
                wanted.GetOrCreateValue(__instance)[area] = path;
                foreach (Claim c in claims)
                    if (ReferenceEquals(c.Render, __instance) && c.Area == area)
                        return false;
                return true;
            }
            catch (Exception e)
            {
                Failed(e, "recording a screen's texture");
                return true;
            }
        }

        private static void Apply(Claim c, string path)
        {
            applying = true;
            try
            {
                c.Render.ChangeTexture(c.Area, path);
            }
            finally
            {
                applying = false;
            }
        }

        // ---- Each frame: take the screens, keep them drawn, give them back. ----

        private static void Update()
        {
            if (errors >= 3)
                return;
            try
            {
                frame++;
                FlushPending(false);
                MyCockpit seat = Off ? null : Seat();
                long seatId = seat?.EntityId ?? 0;
                if (seatId != seatedIn)
                {
                    seatedIn = seatId;
                    seatedAt = frame;
                    lastCase = null;
                }

                // Which screens: those set to Touch Toolbar, else the fallback; not decided while the fallback's pick waits.
                want.Clear();
                bool decided = seat == null;
                string why = null;
                if (seat != null && frame - seatedAt >= SettleFrames)
                    decided = Wanted(seat, want, out why);

                for (int i = claims.Count - 1; i >= 0; i--)
                {
                    Claim c = claims[i];
                    string gone = c.Cockpit.Closed ? "the cockpit is gone"
                        : MySession.Static == null ? "the session ended"
                        : Off ? "turned off"
                        : seat != c.Cockpit ? "left the seat"
                        : !c.Cockpit.IsWorking || !c.Cockpit.IsFunctional ? "the cockpit stopped working"
                        : c.Panel != Panel(c.Cockpit, c.Area) ? "the screen was rebuilt"
                        : decided && !want.Contains(c.Area) ? $"no longer wanted: {why}"
                        : null;
                    if (gone != null)
                    {
                        claims.RemoveAt(i);
                        Release(c, gone);
                    }
                }
                if (seat != null && decided && frame >= retryAt)
                    foreach (int area in want)
                        if (!claims.Exists(c => c.Area == area))
                        {
                            Claim c = Take(seat, area, why);
                            if (c != null)
                                claims.Add(c);
                        }

                foreach (Claim c in claims)
                {
                    // The render objects come and go (the model changes, the block is hidden and shown): point them at ours again.
                    uint[] ids = c.Render.RenderObjectIDs;
                    if (!Same(ids, c.Ids) || frame - c.AppliedAt >= 120)
                    {
                        c.Ids = (uint[])ids?.Clone();
                        c.AppliedAt = frame;
                        Apply(c, c.Texture);
                    }
                    // A screen whose glass was still loading when it was taken: the laser needs it.
                    if (!c.Middle.HasValue && frame % 30 == 0)
                        c.Middle = LocalMiddle(c.Cockpit, c.Area);
                    Draw(c, false);
                }
                DrawLaser();
            }
            catch (Exception e)
            {
                Failed(e, "updating");
            }
        }

        private sealed class Claim
        {
            public MyCockpit Cockpit;
            public int Area;
            public MyTextPanelComponent Panel;
            public MyRenderComponentScreenAreas Render;
            public string Texture, Drawn;
            public Vector2I TextureSize;
            public Vector2 Surface, Aspect;
            public Layout Layout;
            /// <summary>The middle of the screen's picture (block space), from its glass; null while it loads.</summary>
            public Vector3? Middle;
            public uint[] Ids;
            public int AppliedAt;
            public int Pressed = NoTarget, Hover = NoTarget;
            public bool PressedOver;
            /// <summary>Out of reach (the laser's) or in it (touch's), as the laser last decided; null until then.</summary>
            public bool? OutOfReach;
        }

        private static void Failed(Exception e, string what)
        {
            if (errors++ < 3)
                Log.Error(e, $"Touch toolbar failed ({what})");
            if (errors < 3)
                return;
            for (int i = claims.Count - 1; i >= 0; i--)
            {
                Claim c = claims[i];
                claims.RemoveAt(i);
                try
                {
                    Release(c, "it failed");
                }
                catch (Exception)
                {
                    // Gone either way.
                }
            }
        }
    }
}
