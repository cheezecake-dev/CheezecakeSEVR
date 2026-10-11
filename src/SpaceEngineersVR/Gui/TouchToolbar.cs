using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Sandbox.Game.Components;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems.TextSurfaceScripts;
using Sandbox.Game.GUI;
using Sandbox.Game.Localization;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using Sandbox.Graphics;
using SpaceEngineersVR.Hands;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;
using VRage;
using VRage.Audio;
using VRage.Game;
using VRage.Game.GUI.TextPanel;
using VRage.Utils;
using VRageMath;
using VRageRender;
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
    /// screen; RenderDebug TouchToolbarScreen=n puts it on screen n (0-based) instead, and TouchToolbar=0 turns it all off.
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
    internal static class TouchToolbar
    {
        /// <summary>Debug: no toolbar on any cockpit screen (<see cref="Rendering.RenderDebug"/> TouchToolbar=0).</summary>
        public static bool Off { get; set; }

        /// <summary>Debug: the screen (surface index) to use when none is set to Touch Toolbar; -1 picks the central-most one (TouchToolbarScreen).</summary>
        public static int ScreenOverride { get; set; } = -1;

        /// <summary>
        /// A screen whose middle is farther than this from the dominant shoulder is out of the drawn arm's reach: the laser
        /// and trigger tap it. Between the screens the screen-glass run measured in the Red Miner: the lower one in reach
        /// (0.56 m from the eyes), the others 1.1-1.2 m from the shoulder and not.
        /// </summary>
        private const double ArmReachMeters = 0.8;

        /// <summary>The same, from the eyes, while the shoulder is not known.</summary>
        private const double EyeReachMeters = 0.75;

        /// <summary>
        /// A screen goes out of reach this far past the reach and comes back in this far inside it, metres: the drop pod's
        /// screen sat 1 cm past the 0.80 m reach (2026-10-10), where the solved shoulder's sway would turn the laser on and off.
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
        private static string laserOwner; // who had the hand when last logged: null (the laser), "stick" or "touch"
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
                Log.Error(e, "Touch toolbar could not be hooked; cockpit screens keep their own content");
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

        /// <summary>
        /// The screens the toolbar goes on, and why (logged when it changes): those set to Touch Toolbar in the screen's
        /// settings; with none, TouchToolbarScreen's, else the central-most if the setting is on; else none. False while the
        /// central-most pick waits for the shoulder and the screens' glass.
        /// </summary>
        private static bool Wanted(MyCockpit cockpit, List<int> into, out string why)
        {
            int count = SurfaceCount(cockpit);
            for (int i = 0; i < count; i++)
            {
                MyTextPanelComponent panel = Panel(cockpit, i);
                if (panel != null && panel.ContentType == ContentType.SCRIPT && panel.Script == TouchToolbarScript.Id)
                    into.Add(i);
            }
            if (into.Count > 0)
                why = $"set to {TouchToolbarScript.DisplayName} in the screen's settings: screen{(into.Count > 1 ? "s" : "")} {string.Join(", ", into)}";
            else if (ScreenOverride >= 0 && ScreenOverride < count)
            {
                into.Add(ScreenOverride);
                why = $"no screen is set to {TouchToolbarScript.DisplayName}; TouchToolbarScreen={ScreenOverride}";
            }
            else if (!VRSettings.TouchToolbar)
                why = $"no screen is set to {TouchToolbarScript.DisplayName}, and the setting \"Touch toolbar on the cockpit screen\" is off";
            else
            {
                int area = PickScreen(cockpit, out string pick);
                if (area == NotYet)
                {
                    why = null;
                    return false;
                }
                into.Add(area);
                why = $"no screen is set to {TouchToolbarScript.DisplayName}, so the setting's fallback: screen {area}, {pick}";
            }
            if (why != lastCase)
            {
                lastCase = why;
                Log.Info($"Touch toolbar: {cockpit.BlockDefinition.Id.SubtypeName} {cockpit.EntityId}: {why}");
            }
            return true;
        }

        /// <summary>The cockpit this player pilots, working and with at least one screen; null otherwise.</summary>
        private static MyCockpit Seat()
        {
            MySession session = MySession.Static;
            if (session?.LocalCharacter == null || !(session.ControlledEntity is MyCockpit cockpit))
                return null;
            if (cockpit.Pilot != session.LocalCharacter || cockpit.Closed || !cockpit.IsFunctional || !cockpit.IsWorking)
                return null;
            return SurfaceCount(cockpit) > 0 ? cockpit : null;
        }

        private static readonly AccessTools.FieldRef<MyCockpit, MyMultiTextPanelComponent> multiPanel =
            AccessTools.FieldRefAccess<MyCockpit, MyMultiTextPanelComponent>("m_multiPanel");

        private static int SurfaceCount(MyCockpit cockpit) => multiPanel(cockpit)?.SurfaceCount ?? 0;

        private static MyTextPanelComponent Panel(MyCockpit cockpit, int area)
        {
            MyMultiTextPanelComponent multi = multiPanel(cockpit);
            return multi != null && area >= 0 && area < multi.SurfaceCount ? multi.GetPanelComponent(area) : null;
        }

        private static Claim Take(MyCockpit cockpit, int area, string why)
        {
            var render = cockpit.Render as MyRenderComponentScreenAreas;
            MyTextPanelComponent panel = Panel(cockpit, area);
            if (render == null || panel == null)
            {
                if (refusedFor != cockpit.EntityId)
                    Log.Info($"Touch toolbar: {cockpit.BlockDefinition.Id.SubtypeName} {cockpit.EntityId} has no screen to draw on (render {(render == null ? "none" : "ok")}, screen {area})");
                refusedFor = cockpit.EntityId;
                retryAt = frame + 120;
                return null;
            }

            var size = new Vector2I((int)panel.TextureSize.X, (int)panel.TextureSize.Y);
            var c = new Claim
            {
                Cockpit = cockpit,
                Area = area,
                Panel = panel,
                Render = render,
                Texture = $"SEVRTouchToolbar_{cockpit.EntityId}_{area}",
                TextureSize = size,
                Surface = panel.SurfaceSize,
                Middle = LocalMiddle(cockpit, area),
            };
            c.Aspect = c.Surface / new Vector2(size.X, size.Y);
            c.Layout = Arrange(c.Surface);
            MyRenderProxy.CreateGeneratedTexture(c.Texture, size.X, size.Y, MyGeneratedTextureType.RGBA, 1, null, generateMipmaps: true, immediatelyReady: false);
            Draw(c, true);
            c.Ids = (uint[])render.RenderObjectIDs?.Clone();
            c.AppliedAt = frame;
            Apply(c, c.Texture);

            string was = wanted.TryGetValue(render, out Dictionary<int, string> paths) && paths.TryGetValue(area, out string path) ? path ?? "(hidden)" : "(not seen)";
            var at = new StringBuilder();
            for (int i = 0; i < Slots; i++)
                at.Append(i == 0 ? "" : " ").Append(i + 1).Append('@').Append(Pixel(c, c.Layout.Slots[i].Center));
            Log.Info($"Touch toolbar: on {cockpit.BlockDefinition.Id.SubtypeName} {cockpit.EntityId} '{cockpit.CubeGrid.DisplayName}' screen {area} '{panel.Name}' " +
                     $"({why}; texture {size.X}x{size.Y}, surface {c.Surface.X:0}x{c.Surface.Y:0}, layout {c.Layout.Columns}x{c.Layout.Rows}, cell {c.Layout.Cell:0} px; " +
                     $"{(Far(c, out string reach) ? "laser" : "touch")}: {reach}); it showed {was}. " +
                     $"Touch pixels (sprite coordinates): {at}, left@{Pixel(c, c.Layout.Left.Center)}, right@{Pixel(c, c.Layout.Right.Center)}");
            return c;
        }

        private static void Release(Claim c, string why)
        {
            if (laserOn == c)
            {
                laserOn = null;
                laserDown = false;
            }
            laserHit = false;
            laserAt = null;
            FlushPending(true);
            string back = "(not seen)";
            if (!c.Cockpit.Closed)
            {
                if (wanted.TryGetValue(c.Render, out Dictionary<int, string> paths) && paths.TryGetValue(c.Area, out string path))
                {
                    Apply(c, path);
                    back = path ?? "(hidden)";
                }
                // The panel skips a texture it thinks it already set: make it send its own again on its next update.
                if (c.Panel != null)
                    previousTexture(c.Panel) = "\u0001SEVR";
            }
            MyRenderProxy.DestroyGeneratedTexture(c.Texture);
            Log.Info($"Touch toolbar: gave screen {c.Area} of {c.Cockpit.BlockDefinition.Id.SubtypeName} {c.Cockpit.EntityId} back ({why}); it shows {back} again");
        }

        private static bool Same(uint[] a, uint[] b)
        {
            if (a == null || b == null)
                return a == b;
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }

        // ---- Which screen: the override, else the central-most one (on the seat's centre line, which keeps it in reach). ----

        private const int NotYet = -2;

        /// <summary>Screens whose sideways offsets differ by less than this are equally central: the nearest one wins.</summary>
        private const float CentralTie = 0.03f;

        /// <summary>Each cockpit's pick and its reason; one made before every screen's glass was in lasts only while seated.</summary>
        private static readonly Dictionary<long, (int Area, string Why, bool Final, int Seat)> picked = new Dictionary<long, (int, string, bool, int)>();

        private static readonly AccessTools.FieldRef<MyShipController, Vector3> headLocal =
            AccessTools.FieldRefAccess<MyShipController, Vector3>("m_headLocalPosition");

        /// <summary>
        /// The surface to draw on, and why (for the log): the one whose picture's middle is least to the side of the
        /// seat's centre line (the seat's forward axis through its head), the nearest of equally central ones.
        /// <see cref="NotYet"/> while the shoulder or some screen's glass is still to come, for up to
        /// <see cref="PickWaitFrames"/> after sitting. A pick made with every screen's glass is kept for the cockpit; one
        /// made without, until the pilot leaves the seat.
        /// </summary>
        private static int PickScreen(MyCockpit cockpit, out string why)
        {
            int count = SurfaceCount(cockpit);
            if (picked.TryGetValue(cockpit.EntityId, out var known) && known.Area < count && (known.Final || known.Seat == seatedAt))
            {
                why = known.Why;
                return known.Area;
            }

            Hand hand = VRSettings.DominantHand;
            string side = hand.ToString().ToLowerInvariant();
            bool shoulderKnown = HandIK.TryShoulder(hand, MySession.Static?.LocalCharacter, out Vector3D shoulder);
            bool eyesKnown = TryEyes(out Vector3D eyes);
            var middles = new Vector3?[count];
            int found = Middles(cockpit, middles);
            if ((!shoulderKnown || found < count) && frame - seatedAt < SettleFrames + PickWaitFrames)
            {
                why = null;
                return NotYet;
            }

            // The seat's centre line, block space: through its head, along the block's forward (-Z); sideways is X.
            MatrixD toWorld = cockpit.PositionComp.WorldMatrixRef;
            Vector3 head = headLocal(cockpit);
            string headFrom = "the seat's head";
            if (head == Vector3.Zero && eyesKnown)
            {
                head = (Vector3)Vector3D.Transform(eyes, cockpit.PositionComp.WorldMatrixNormalizedInv);
                headFrom = "the eyes (the seat has no head position)";
            }

            var seen = new StringBuilder();
            int best = -1;
            float bestOffset = float.MaxValue, bestNear = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                seen.Append($" {i} '{Panel(cockpit, i)?.Name}'");
                if (!middles[i].HasValue)
                {
                    seen.Append(" no glass;");
                    continue;
                }
                Vector3 local = middles[i].Value;
                float sideways = local.X - head.X, offset = Math.Abs(sideways), near = Vector3.Distance(local, head);
                Vector3D middle = Vector3D.Transform((Vector3D)local, toWorld);
                double fromEyes = eyesKnown ? Vector3D.Distance(eyes, middle) : double.NaN;
                double fromShoulder = shoulderKnown ? Vector3D.Distance(shoulder, middle) : double.NaN;
                bool inReach = shoulderKnown ? fromShoulder <= ArmReachMeters : eyesKnown && fromEyes <= EyeReachMeters;
                seen.Append($" {offset:0.00} m {(sideways >= 0f ? "right" : "left")} of the centre line")
                    .Append(eyesKnown ? $", {fromEyes:0.00} m from the eyes" : ", eyes unknown")
                    .Append(shoulderKnown ? $", {fromShoulder:0.00} m from the {side} shoulder" : ", shoulder unknown")
                    .Append(inReach ? ", in reach;" : ", out of reach;");
                bool better = best < 0 || offset < bestOffset - CentralTie || (offset < bestOffset + CentralTie && near < bestNear);
                if (better)
                {
                    best = i;
                    bestOffset = offset;
                    bestNear = near;
                }
            }

            if (best < 0)
            {
                best = 0;
                why = "no screen's glass found, so screen 0";
            }
            else
            {
                var middle = Vector3D.Transform((Vector3D)middles[best].Value, toWorld);
                string reach = shoulderKnown
                    ? (Vector3D.Distance(shoulder, middle) <= ArmReachMeters ? "in the arm's reach, so touch" : "out of the arm's reach, so the laser and trigger")
                    : eyesKnown ? (Vector3D.Distance(eyes, middle) <= EyeReachMeters ? "in reach of the eyes (shoulder unknown), so touch" : "out of reach of the eyes (shoulder unknown), so the laser")
                    : "reach unknown";
                why = $"the central-most, {bestOffset:0.00} m off the centre line through {headFrom}" +
                      $"{(count > 1 ? $" (ties within {CentralTie * 100f:0} cm go to the nearest)" : "")}; {reach}";
            }
            picked[cockpit.EntityId] = (best, why, found == count, seatedAt);
            string seated = shoulderKnown && eyesKnown ? $"; the {side} shoulder is {Vector3D.Distance(shoulder, eyes):0.00} m from the eyes, seated" : "";
            Log.Info($"Touch toolbar: {cockpit.BlockDefinition.Id.SubtypeName} {cockpit.EntityId} screens ({found} of {count} with glass):{seen} picked {best}: {why}{seated}");
            return best;
        }

        /// <summary>Where the eyes are (world): the game's camera, which the headset moves.</summary>
        private static bool TryEyes(out Vector3D eyes)
        {
            eyes = MySector.MainCamera?.Position ?? Vector3D.Zero;
            return MySector.MainCamera != null;
        }

        // ---- The screen's glass (Hands.ScreenGlass: the triangles and UVs of the model the pilot sees). ----

        private static void Fill(MyCockpit cockpit)
        {
            glass.Clear();
            int used = 0;
            ScreenGlass.AddScreens(cockpit, glass, glassPool, ref used);
        }

        /// <summary>The middle of a sheet's picture (model space).</summary>
        private static Vector3 Middle(ScreenGlass.Sheet sheet) =>
            ScreenGlass.PointAt(sheet, (sheet.UVMin + sheet.UVMax) / 2f, out Vector3 p, out _) ? p : sheet.Centre;

        /// <summary>Each surface's picture middle (block space); how many surfaces have their glass yet.</summary>
        private static int Middles(MyCockpit cockpit, Vector3?[] into)
        {
            Fill(cockpit);
            int found = 0;
            foreach (ScreenGlass.Screen s in glass)
            {
                if (s.Surface < 0 || s.Surface >= into.Length || into[s.Surface].HasValue)
                    continue;
                into[s.Surface] = Middle(s.Sheet);
                found++;
            }
            glass.Clear();
            return found;
        }

        /// <summary>The middle of one surface's picture (block space), or null while its glass loads (or if it has none).</summary>
        private static Vector3? LocalMiddle(MyCockpit cockpit, int area)
        {
            Fill(cockpit);
            Vector3? middle = null;
            foreach (ScreenGlass.Screen s in glass)
                if (s.Surface == area)
                {
                    middle = Middle(s.Sheet);
                    break;
                }
            glass.Clear();
            return middle;
        }

        /// <summary>The laser (world) meets the claimed screen's glass from in front: how far along it, and the picture's UV there.</summary>
        private static bool Ray(Claim c, Vector3D fromWorld, Vector3D dirWorld, out float distance, out Vector2 uv)
        {
            distance = float.MaxValue;
            uv = default;
            bool any = false;
            Fill(c.Cockpit);
            foreach (ScreenGlass.Screen s in glass)
            {
                if (s.Surface != c.Area)
                    continue;
                var from = (Vector3)Vector3D.Transform(fromWorld, s.FromWorld);
                var dir = (Vector3)Vector3D.TransformNormal(dirWorld, s.FromWorld);
                if (Vector3.Dot(dir, s.Sheet.Normal) >= 0f)
                    continue;
                foreach (ScreenGlass.Tri t in s.Sheet.Tris)
                {
                    // Moller-Trumbore.
                    Vector3 e1 = t.B - t.A, e2 = t.C - t.A;
                    Vector3 p = Vector3.Cross(dir, e2);
                    float det = Vector3.Dot(e1, p);
                    if (Math.Abs(det) < 1e-9f)
                        continue;
                    float inv = 1f / det;
                    Vector3 o = from - t.A;
                    float u = Vector3.Dot(o, p) * inv;
                    if (u < 0f || u > 1f)
                        continue;
                    Vector3 q = Vector3.Cross(o, e1);
                    float v = Vector3.Dot(dir, q) * inv;
                    if (v < 0f || u + v > 1f)
                        continue;
                    float along = Vector3.Dot(e2, q) * inv;
                    if (along <= 0f || along >= distance)
                        continue;
                    distance = along;
                    uv = t.UA * (1f - u - v) + t.UB * u + t.UC * v;
                    any = true;
                }
            }
            glass.Clear();
            return any;
        }

        /// <summary>How far the claimed screen's middle is from the dominant shoulder (else the eyes), and whether that is out of reach.</summary>
        private static bool Reach(Claim c, out double distance, out bool fromShoulder)
        {
            distance = 0.0;
            fromShoulder = false;
            if (!c.Middle.HasValue)
                return false;
            Vector3D middle = Vector3D.Transform((Vector3D)c.Middle.Value, c.Cockpit.PositionComp.WorldMatrixRef);
            if (HandIK.TryShoulder(VRSettings.DominantHand, MySession.Static?.LocalCharacter, out Vector3D shoulder))
            {
                fromShoulder = true;
                distance = Vector3D.Distance(shoulder, middle);
                return true;
            }
            if (!TryEyes(out Vector3D eyes))
                return false;
            distance = Vector3D.Distance(eyes, middle);
            return true;
        }

        /// <summary>
        /// The claimed screen is out of the drawn arm's reach: the laser and trigger tap it. It goes out of reach
        /// <see cref="ReachSlack"/> past the reach and comes back in as far inside it, so a screen at the reach does not
        /// turn the laser on and off as the shoulder sways; while neither the shoulder nor the eyes are known it is in reach.
        /// </summary>
        private static bool Far(Claim c)
        {
            if (!Reach(c, out double distance, out bool fromShoulder))
                return false;
            double limit = fromShoulder ? ArmReachMeters : EyeReachMeters;
            bool far = c.OutOfReach.HasValue ? distance > (c.OutOfReach.Value ? limit - ReachSlack : limit + ReachSlack) : distance > limit;
            if (c.OutOfReach.HasValue && far != c.OutOfReach.Value)
                Log.Info($"Touch toolbar: screen {c.Area} '{c.Panel?.Name}' now {(far ? "out of reach, so the laser and trigger" : "in reach, so touch")}: " +
                         $"{distance:0.00} m from the {(fromShoulder ? VRSettings.DominantHand.ToString().ToLowerInvariant() + " shoulder" : "eyes")}, " +
                         $"reach {limit:0.00} m, {ReachSlack * 100:0} cm either way to change");
            c.OutOfReach = far;
            return far;
        }

        private static bool Far(Claim c, out string reach)
        {
            if (!Reach(c, out double distance, out bool fromShoulder))
            {
                reach = c.Middle.HasValue ? "neither the shoulder nor the eyes are known" : "its glass is not loaded yet";
                return false;
            }
            double limit = fromShoulder ? ArmReachMeters : EyeReachMeters;
            reach = $"{distance:0.00} m from the {(fromShoulder ? VRSettings.DominantHand.ToString().ToLowerInvariant() + " shoulder" : "eyes")}, reach {limit:0.00} m";
            return distance > limit;
        }

        // ---- The layout, in the surface's own pixels (the area the game stretches over the whole texture). ----

        private sealed class Layout
        {
            public RectangleF[] Slots = new RectangleF[TouchToolbar.Slots];
            public RectangleF Left, Right, Tab;
            public float Cell;
            public int Columns, Rows;
        }

        /// <summary>The nine slots as one row, two rows or a square, whichever gives the biggest slots, under a row with the page and its arrows.</summary>
        private static Layout Arrange(Vector2 surface)
        {
            const float gap = 0.07f, header = 0.5f;
            float pad = 0.035f * Math.Min(surface.X, surface.Y);
            float width = surface.X - 2f * pad, height = surface.Y - 2f * pad;
            Layout best = null;
            foreach (int columns in new[] { 9, 5, 3 })
            {
                int rows = (Slots + columns - 1) / columns;
                float cell = Math.Min(width / (columns + gap * (columns - 1)), height / (header + gap + rows + gap * (rows - 1)));
                if (best != null && cell <= best.Cell)
                    continue;
                best = new Layout { Cell = cell, Columns = columns, Rows = rows };
            }

            Layout l = best;
            float c = l.Cell, g = gap * c, h = header * c;
            float gridWidth = l.Columns * c + (l.Columns - 1) * g;
            float blockHeight = h + g + l.Rows * c + (l.Rows - 1) * g;
            float left = (surface.X - gridWidth) / 2f, top = (surface.Y - blockHeight) / 2f;

            float arrow = Math.Min(1.5f * c, gridWidth * 0.3f);
            l.Left = new RectangleF(left, top, arrow, h);
            l.Right = new RectangleF(left + gridWidth - arrow, top, arrow, h);
            float tab = h * 66f / 46f;
            l.Tab = new RectangleF(surface.X / 2f - tab / 2f, top, tab, h);

            float y = top + h + g;
            for (int i = 0; i < Slots; i++)
            {
                int row = i / l.Columns, column = i % l.Columns;
                int inRow = Math.Min(l.Columns, Slots - row * l.Columns);
                float rowLeft = left + (gridWidth - (inRow * c + (inRow - 1) * g)) / 2f;
                l.Slots[i] = new RectangleF(rowLeft + column * (c + g), y + row * (c + g), c, c);
            }
            return l;
        }

        /// <summary>
        /// A point of the layout as the pixel a touch there reports: the surface's sprite coordinates (ScreenGlass.Pixel,
        /// UV times the surface's size, offset by half of what the texture has beyond it). The layout itself spans the
        /// surface's size stretched over the whole texture, so its point is the UV times the surface's size.
        /// </summary>
        private static string Pixel(Claim c, Vector2 point)
        {
            Vector2 p = ScreenGlass.Pixel(c.Panel, point / c.Surface);
            return $"{p.X:0},{p.Y:0}";
        }

        /// <summary>A touch's pixel (sprite coordinates) as a point of the layout.</summary>
        private static Vector2 Point(Claim c, Vector2 pixel) => ScreenGlass.UV(c.Panel, pixel) * c.Surface;

        private static int HitTest(Layout l, Vector2 p)
        {
            for (int i = 0; i < Slots; i++)
                if (l.Slots[i].Contains(p))
                    return i;
            if (Grow(l.Left).Contains(p))
                return LeftArrow;
            if (Grow(l.Right).Contains(p))
                return RightArrow;
            return NoTarget;
        }

        /// <summary>The arrows take touches a little beyond what is drawn, as they are short.</summary>
        private static RectangleF Grow(RectangleF r) => new RectangleF(r.X - r.Height * 0.15f, r.Y - r.Height * 0.25f, r.Width + r.Height * 0.3f, r.Height * 1.5f);

        // ---- Drawing: the game's offscreen sprite renderer, into our texture, only when what it shows has changed. ----

        private static void Draw(Claim c, bool force)
        {
            MyToolbar toolbar = Toolbar(c);
            key.Clear();
            if (toolbar != null)
            {
                key.Append(toolbar.CurrentPage).Append('/').Append(toolbar.PageCount).Append('|').Append(toolbar.SelectedSlot ?? -1);
                for (int i = 0; i < Slots; i++)
                {
                    MyToolbarItem item = toolbar.GetSlotItem(i);
                    key.Append('|');
                    if (item != null)
                        key.Append(item.GetHashCode()).Append(item.Enabled ? '+' : '-').Append(item.IconText).Append('~').Append(item.DisplayName)
                            .Append('~').Append(item.Icons != null && item.Icons.Length > 0 ? item.Icons[0] : "");
                }
            }
            key.Append("|p").Append(c.Pressed).Append(c.PressedOver ? '*' : '.').Append("|h").Append(c.Hover);
            string now = key.ToString();
            if (!force && now == c.Drawn)
                return;
            c.Drawn = now;

            string target = c.Texture;
            Layout l = c.Layout;
            float cell = l.Cell;

            // The page row: arrows at the ends, the page's tab in the middle.
            DrawArrow(c, l.Left, LeftArrow, new Vector2(0f, -1f));
            DrawArrow(c, l.Right, RightArrow, new Vector2(0f, 1f));
            Sprite(target, PageActive, l.Tab, Color.White);
            int page = toolbar?.CurrentPage ?? 0;
            Text(target, "White", (page + 1).ToString(), l.Tab.X, l.Tab.Y + l.Tab.Height * 0.14f, l.Tab.Width, l.Tab.Height * 0.66f, Color.White, MyRenderTextAlignmentEnum.Align_Center);

            string off = MyTexts.GetString(MySpaceTexts.SwitchText_Off);
            for (int i = 0; i < Slots; i++)
            {
                RectangleF r = l.Slots[i];
                MyToolbarItem item = toolbar?.GetSlotItem(i);
                bool selected = toolbar?.SelectedSlot == i;
                bool pressed = c.Pressed == i && c.PressedOver;
                Sprite(target, selected || pressed ? ItemActive : ItemInactive, r, Color.White);
                if (pressed)
                    Sprite(target, White, r, Color.White * 0.22f);
                else if (c.Hover == i)
                    Sprite(target, White, r, Color.White * 0.1f);

                if (item != null)
                {
                    string state = item.IconText?.ToString() ?? string.Empty;
                    bool isOff = state.Length > 0 && string.Equals(state, off, StringComparison.OrdinalIgnoreCase);
                    float fade = !item.Enabled ? 0.35f : isOff ? 0.4f : 1f;
                    var icon = new RectangleF(r.X + cell * 0.15f, r.Y + cell * 0.06f, cell * 0.7f, cell * 0.7f);
                    if (item.Icons != null)
                        foreach (string texture in item.Icons)
                            if (!string.IsNullOrEmpty(texture))
                                Sprite(target, texture, icon, Color.White * fade);
                    if (!string.IsNullOrEmpty(item.SubIcon))
                        Sprite(target, item.SubIcon, new RectangleF(r.X + cell * 0.62f, r.Y + cell * 0.05f, cell * 0.33f, cell * 0.33f), Color.White * fade);
                    if (state.Length > 0)
                        Text(target, "White", state, r.X + cell * 0.05f, r.Y + cell * 0.55f, cell * 0.88f, cell * 0.2f, isOff ? OffGrey : IconBlue, MyRenderTextAlignmentEnum.Align_Right);
                    Text(target, "White", ShortName(item), r.X + cell * 0.06f, r.Y + cell * 0.77f, cell * 0.88f, cell * 0.17f, NameColor * (item.Enabled ? 1f : 0.6f), MyRenderTextAlignmentEnum.Align_Center);
                }
                Text(target, selected || pressed ? "White" : "Blue", (i + 1).ToString(), r.X + cell * 0.08f, r.Y + cell * 0.03f, cell * 0.4f, cell * 0.22f, Color.White, MyRenderTextAlignmentEnum.Align_Left);
            }
            MyRenderProxy.RenderOffscreenTexture(target, c.Aspect, ClearColor(c.Panel));
        }

        /// <summary>
        /// The colour the texture is cleared to, with the alpha the game gives its own screen content: the panel's
        /// BackgroundAlpha (0 unless a transparent LCD sets it), as MyRenderComponentScreenAreas.RenderSpritesToTexture
        /// does. The texture is the screen material's colour-metal map, whose alpha is the metalness (the standard
        /// material's FeedOutputInternal), and the offscreen sprites never write alpha (BlendAlphaPremultNoAlphaChannel),
        /// so the clear's alpha is the whole screen's metalness. At 255 the screen was a full metal: albedo
        /// base*(1-metal) is 0, so it neither glowed (emissive is albedo * emissive) nor took diffuse light.
        /// </summary>
        private static Color ClearColor(MyTextPanelComponent panel)
        {
            Color clear = Background;
            clear.A = panel?.BackgroundAlpha ?? 0;
            return clear;
        }

        private static void DrawArrow(Claim c, RectangleF r, int which, Vector2 right)
        {
            bool pressed = c.Pressed == which && c.PressedOver;
            Sprite(c.Texture, pressed ? ItemActive : ItemInactive, r, Color.White);
            if (pressed)
                Sprite(c.Texture, White, r, Color.White * 0.22f);
            else if (c.Hover == which)
                Sprite(c.Texture, White, r, Color.White * 0.1f);
            float size = r.Height * 0.55f;
            Sprite(c.Texture, Triangle, new RectangleF(r.X + r.Width / 2f - size / 2f, r.Y + r.Height / 2f - size / 2f, size, size), IconBlue, right);
        }

        private static void Sprite(string target, string texture, RectangleF r, Color color, Vector2? right = null) =>
            MyRenderProxy.DrawSpriteAtlas(texture, r.Center, Vector2.Zero, Vector2.One, right ?? Vector2.UnitX, Vector2.One, color,
                new Vector2(r.Width, r.Height) / 2f, ignoreBounds: false, target);

        private static readonly Dictionary<string, float> lineHeights = new Dictionary<string, float>();

        /// <summary>Text <paramref name="px"/> high in a box, smaller (to 70 %) and then cut short with ".." if it does not fit.</summary>
        private static void Text(string target, string font, string text, float x, float y, float width, float px, Color color, MyRenderTextAlignmentEnum align)
        {
            if (string.IsNullOrEmpty(text))
                return;
            float scale = Fit(font, ref text, width, px);
            MyRenderProxy.DrawStringAligned((int)MyStringHash.GetOrCompute(font), new Vector2(x, y), color, text, scale, float.PositiveInfinity,
                false, target, (int)Math.Round(width), align);
        }

        // ---- The script's own picture (TouchToolbarScript): the same layout in the screen's sprites, for whoever is not piloting here. ----

        private static readonly Color SlotFill = new Color(22, 34, 41), SlotEdge = new Color(70, 96, 108);

        /// <summary>
        /// The cockpit's toolbar page in a script's sprite frame: sprite coordinates span the texture, the visible surface
        /// in its middle. Only the game's LCD textures can be drawn there, so no item icons.
        /// </summary>
        internal static void DrawSprites(MySpriteDrawFrame frame, MyCockpit cockpit, Vector2 texture, Vector2 surface)
        {
            Vector2 corner = (texture - surface) / 2f;
            frame.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple", texture / 2f, texture, Background));
            MyToolbar toolbar = cockpit?.Toolbar;
            if (toolbar == null)
            {
                SpriteText(frame, "White", "Touch Toolbar: for a cockpit's screens", corner.X + surface.X * 0.05f, corner.Y + surface.Y * 0.44f,
                    surface.X * 0.9f, surface.Y * 0.1f, IconBlue, TextAlignment.CENTER);
                return;
            }
            Layout l = Arrange(surface);
            float cell = l.Cell;
            SpriteArrow(frame, Shift(l.Left, corner), -MathHelper.PiOver2);
            SpriteArrow(frame, Shift(l.Right, corner), MathHelper.PiOver2);
            RectangleF tab = Shift(l.Tab, corner);
            SpriteRect(frame, "SquareSimple", tab, SlotEdge);
            SpriteText(frame, "White", (toolbar.CurrentPage + 1).ToString(), tab.X, tab.Y + tab.Height * 0.14f, tab.Width, tab.Height * 0.66f, Color.White, TextAlignment.CENTER);

            string off = MyTexts.GetString(MySpaceTexts.SwitchText_Off);
            for (int i = 0; i < Slots; i++)
            {
                RectangleF r = Shift(l.Slots[i], corner);
                MyToolbarItem item = toolbar.GetSlotItem(i);
                bool selected = toolbar.SelectedSlot == i;
                SpriteRect(frame, "SquareSimple", r, SlotFill);
                SpriteRect(frame, "SquareHollow", r, selected ? IconBlue : SlotEdge);
                if (item != null)
                {
                    string state = item.IconText?.ToString() ?? string.Empty;
                    bool isOff = state.Length > 0 && string.Equals(state, off, StringComparison.OrdinalIgnoreCase);
                    if (state.Length > 0)
                        SpriteText(frame, "White", state, r.X + cell * 0.05f, r.Y + cell * 0.08f, cell * 0.88f, cell * 0.2f, isOff ? OffGrey : IconBlue, TextAlignment.RIGHT);
                    SpriteText(frame, "White", ShortName(item), r.X + cell * 0.06f, r.Y + cell * 0.42f, cell * 0.88f, cell * 0.2f, NameColor * (item.Enabled ? 1f : 0.6f), TextAlignment.CENTER);
                }
                SpriteText(frame, selected ? "White" : "Blue", (i + 1).ToString(), r.X + cell * 0.08f, r.Y + cell * 0.03f, cell * 0.4f, cell * 0.22f, Color.White, TextAlignment.LEFT);
            }
        }

        private static RectangleF Shift(RectangleF r, Vector2 by) => new RectangleF(r.X + by.X, r.Y + by.Y, r.Width, r.Height);

        private static void SpriteRect(MySpriteDrawFrame frame, string sprite, RectangleF r, Color color, float rotation = 0f) =>
            frame.Add(new MySprite(SpriteType.TEXTURE, sprite, r.Center, new Vector2(r.Width, r.Height), color, null, TextAlignment.CENTER, rotation));

        private static void SpriteArrow(MySpriteDrawFrame frame, RectangleF r, float rotation)
        {
            SpriteRect(frame, "SquareSimple", r, SlotFill);
            SpriteRect(frame, "SquareHollow", r, SlotEdge);
            float size = r.Height * 0.55f;
            SpriteRect(frame, "Triangle", new RectangleF(r.Center.X - size / 2f, r.Center.Y - size / 2f, size, size), IconBlue, rotation);
        }

        /// <summary>As <see cref="Text"/>, as a text sprite: its box from x, width wide, the text px high, smaller or cut short to fit.</summary>
        private static void SpriteText(MySpriteDrawFrame frame, string font, string text, float x, float y, float width, float px, Color color, TextAlignment align)
        {
            if (string.IsNullOrEmpty(text))
                return;
            float scale = Fit(font, ref text, width, px);
            float at = align == TextAlignment.LEFT ? x : align == TextAlignment.RIGHT ? x + width : x + width / 2f;
            frame.Add(new MySprite(SpriteType.TEXT, text, new Vector2(at, y), new Vector2(width, px), color, font, align, scale));
        }

        /// <summary>The scale for text px high, smaller (to 70 %) and then cut short with ".." if it is wider than width.</summary>
        private static float Fit(string font, ref string text, float width, float px)
        {
            if (!lineHeights.TryGetValue(font, out float line))
                lineHeights[font] = line = Math.Max(1f, MyGuiManager.MeasureStringRaw(font, "0", 1f).Y);
            float scale = px / line;
            float measured = MyGuiManager.MeasureStringRaw(font, text, scale).X;
            if (measured > width)
            {
                scale *= Math.Max(0.7f, width / measured);
                measured = MyGuiManager.MeasureStringRaw(font, text, scale).X;
            }
            while (measured > width && text.Length > 2)
            {
                text = text.Substring(0, text.Length - (text.EndsWith("..") ? 3 : 1)).TrimEnd() + "..";
                measured = MyGuiManager.MeasureStringRaw(font, text, scale).X;
            }
            return scale;
        }

        /// <summary>The block's or group's name of a terminal action ("Lights - Toggle" is "Lights"), else the item's name.</summary>
        private static string ShortName(MyToolbarItem item)
        {
            string name = item.DisplayName?.ToString() ?? string.Empty;
            int dash = name.IndexOf(" - ", StringComparison.Ordinal);
            return (dash > 0 ? name.Substring(0, dash) : name).Trim();
        }

        private static MyToolbar Toolbar(Claim c) => MyToolbarComponent.CurrentToolbar ?? c.Cockpit.Toolbar;

        // ---- Drive: a screen's content, read and set as the game's screen settings do. ----

        /// <summary>
        /// Drive "content": lists the seated cockpit's screens' content. "content n &lt;script id|touch&gt;" first sets screen n
        /// to that script through the game's own properties (ContentType, Script), as its screen settings do: synced, and
        /// kept with the world.
        /// </summary>
        internal static bool DriveContent(string args, out string detail)
        {
            if (!(MySession.Static?.ControlledEntity is MyCockpit cockpit))
            {
                detail = "not seated in a cockpit";
                return false;
            }
            string[] words = (args ?? string.Empty).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int count = SurfaceCount(cockpit);
            if (words.Length >= 2)
            {
                MyTextPanelComponent panel = int.TryParse(words[0], out int area) ? Panel(cockpit, area) : null;
                if (panel == null)
                {
                    detail = $"no screen {words[0]} (0..{count - 1})";
                    return false;
                }
                string id = words[1].Equals("touch", StringComparison.OrdinalIgnoreCase) ? TouchToolbarScript.Id : words[1];
                if (MyTextSurfaceScriptFactory.Instance == null || !MyTextSurfaceScriptFactory.Instance.Scripts.TryGetValue(id, out _))
                {
                    detail = $"no script '{id}' among the screens' scripts";
                    return false;
                }
                string before = ContentOf(panel);
                panel.ContentType = ContentType.SCRIPT;
                panel.Script = id;
                Log.Info($"Touch toolbar: drive set screen {area} of {cockpit.BlockDefinition.Id.SubtypeName} {cockpit.EntityId} from {before} to {ContentOf(panel)}");
            }
            var text = new StringBuilder();
            for (int i = 0; i < count; i++)
                text.Append(i == 0 ? "" : "; ").Append(i).Append(' ').Append(ContentOf(Panel(cockpit, i)));
            detail = text.ToString();
            return true;
        }

        private static string ContentOf(MyTextPanelComponent panel) =>
            panel == null ? "none" : panel.ContentType == ContentType.SCRIPT ? $"script '{panel.Script}'" : panel.ContentType.ToString().ToLowerInvariant();

        // ---- Touches: a finger (ScreenTouch), or the laser and trigger on a screen out of reach. ----

        private static void OnTouched(MyCubeBlock block, int surfaceIndex, Vector2 pixel, TouchPhase phase, Hand hand)
        {
            try
            {
                foreach (Claim c in claims)
                    if (ReferenceEquals(block, c.Cockpit) && surfaceIndex == c.Area)
                    {
                        Touch(c, pixel, phase, hand, "touch");
                        return;
                    }
            }
            catch (Exception e)
            {
                Failed(e, "a touch");
            }
        }

        private static void Touch(Claim c, Vector2 pixel, TouchPhase phase, Hand hand, string how)
        {
            int at = HitTest(c.Layout, Point(c, pixel));
            switch (phase)
            {
                case TouchPhase.Down:
                    c.Pressed = at;
                    c.PressedOver = at != NoTarget;
                    break;
                case TouchPhase.Move:
                    c.PressedOver = c.Pressed != NoTarget && at == c.Pressed;
                    break;
                case TouchPhase.Up:
                    int was = c.Pressed;
                    c.Pressed = NoTarget;
                    c.PressedOver = false;
                    if (was != NoTarget && at == was)
                        Activate(c, was, hand, how);
                    break;
            }
            Draw(c, false);
        }

        private static void Activate(Claim c, int target, Hand hand, string how)
        {
            MyToolbar toolbar = Toolbar(c);
            if (toolbar == null)
            {
                Log.Info($"Touch toolbar: {how} on {(target >= LeftArrow ? "an arrow" : $"slot {target + 1}")} with no toolbar");
                return;
            }
            if (target == LeftArrow || target == RightArrow)
            {
                int from = toolbar.CurrentPage;
                MyGuiAudio.PlaySound(MyGuiSounds.HudClick);
                if (target == LeftArrow)
                    toolbar.PageDown();
                else
                    toolbar.PageUp();
                if (MySession.Static != null)
                    MySession.Static.ToolbarPageSwitches++;
                Haptics.Pulse(hand, 0.35f, 0.03f);
                Log.Info($"Touch toolbar: {how} on the {(target == LeftArrow ? "left" : "right")} arrow: page {from + 1} -> {toolbar.CurrentPage + 1} of {toolbar.PageCount}");
                return;
            }

            MyToolbarItem item = toolbar.GetSlotItem(target);
            var p = new Pending
            {
                Due = frame + LogAfterFrames,
                Toolbar = toolbar,
                Slot = target,
                Page = toolbar.CurrentPage,
                How = how,
                Item = item,
                Name = item?.DisplayName?.ToString() ?? "(empty)",
                Kind = item?.GetType().Name ?? "-",
                StateBefore = item?.IconText?.ToString() ?? "",
                SelectedBefore = toolbar.SelectedSlot,
            };
            if (!toolbar.CanPlayerActivateItems)
            {
                p.Refused = "the toolbar does not take activations now";
                FlushOne(p);
                return;
            }
            // As the slot's number key does in the seat (MyToolbarComponent.HandleInput).
            toolbar.ActivateItemAtSlot(target);
            Haptics.Pulse(hand, 0.35f, 0.03f);
            pending.Add(p);
        }

        private sealed class Pending
        {
            public int Due, Slot, Page;
            public int? SelectedBefore;
            public MyToolbar Toolbar;
            public MyToolbarItem Item;
            public string How, Name, Kind, StateBefore, Refused;
        }

        private static void FlushPending(bool all)
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                if (!all && pending[i].Due > frame)
                    continue;
                Pending p = pending[i];
                pending.RemoveAt(i);
                FlushOne(p);
            }
        }

        /// <summary>The one line for an activation: the slot, its item and page, and what the game shows for it after.</summary>
        private static void FlushOne(Pending p)
        {
            string after;
            if (p.Refused != null)
                after = p.Refused;
            else
            {
                string state = p.Item?.IconText?.ToString() ?? "";
                int? selected = p.Toolbar?.SelectedSlot;
                after = $"state '{p.StateBefore}' -> '{state}', selected slot {Slot(p.SelectedBefore)} -> {Slot(selected)}";
            }
            Log.Info($"Touch toolbar: {p.How} on slot {p.Slot + 1}, page {p.Page + 1}: {p.Name} ({p.Kind}); {after}");
        }

        private static string Slot(int? slot) => slot.HasValue ? (slot.Value + 1).ToString() : "none";

        /// <summary>
        /// From the seat's input (<see cref="VRControls"/>), before the triggers go to the game: a screen out of reach is
        /// tapped by pointing the dominant hand at it and pulling the trigger, which is then the screen's until let go.
        /// The hand is the laser's only while nothing else has it (<see cref="LaserOwner"/>): holding the stick it is the
        /// stick's, and its trigger fires; at a screen's glass it is touching, and the touch's.
        /// </summary>
        internal static void SeatedInput()
        {
            try
            {
                laserHit = false;
                laserAt = null;
                Hand hand = VRSettings.DominantHand;
                // The nearest screen out of reach the dominant hand points at.
                Claim on = null;
                Vector2 pixel = OffScreen;
                float nearest = 4f;
                MatrixD aim = MatrixD.Identity;
                bool aiming = !Off && claims.Count > 0 && VRInput.Active && LaserOwner(hand) == null && HandWorld.TryGet(hand, out MatrixD _, out aim);
                if (aiming)
                    foreach (Claim c in claims)
                        if (Far(c) && Ray(c, aim.Translation, aim.Forward, out float distance, out Vector2 uv) && distance < nearest)
                        {
                            nearest = distance;
                            on = c;
                            pixel = ScreenGlass.Pixel(c.Panel, uv);
                            laserHit = true;
                            laserAt = c;
                        }
                foreach (Claim c in claims)
                    c.Hover = c == on ? HitTest(c.Layout, Point(c, pixel)) : NoTarget;

                if (on != null && !laserDown && VRInput.IsNewPressed(hand, VRButtons.Trigger))
                {
                    VRControls.SwallowTrigger(hand);
                    laserDown = true;
                    laserOn = on;
                    Touch(on, pixel, TouchPhase.Down, hand, "laser");
                }
                else if (laserDown && laserOn != null)
                {
                    Vector2 at = on == laserOn ? pixel : OffScreen;
                    if (aiming && VRInput.IsPressed(hand, VRButtons.Trigger))
                        Touch(laserOn, at, TouchPhase.Move, hand, "laser");
                    else
                    {
                        Claim was = laserOn;
                        laserDown = false;
                        laserOn = null;
                        Touch(was, at, TouchPhase.Up, hand, "laser");
                    }
                }
                else
                {
                    laserDown = false;
                    laserOn = null;
                }
            }
            catch (Exception e)
            {
                Failed(e, "the laser");
            }
        }

        /// <summary>
        /// Who has the dominant hand instead of the laser, or null: the stick it holds (<see cref="ControlGrip.Holds"/>; the
        /// hand is drawn on the stick, not where the controller points from, and its trigger fires), or the screen its
        /// fingertip or palm is at (<see cref="TouchOwns"/>: it touches that). A change of owner is logged, at most once a second.
        /// </summary>
        private static string LaserOwner(Hand hand)
        {
            string owner = null, kind = null;
            if (ControlGrip.Holds(hand))
            {
                owner = "it holds the stick, whose trigger fires";
                kind = "stick";
            }
            else if (MySession.Static?.LocalCharacter is MyCharacter pilot && HandWorld.TryGetNow(pilot, hand, out MatrixD grip, out _, out _))
            {
                ScreenGlass.Screen at = ScreenGlass.At(hand, FingerPress.Fingertip(hand, grip), grip.Translation, TouchOwns, out float distance);
                if (at != null)
                {
                    owner = $"it is touching {at.Key} ({(distance >= 0f ? $"{distance * 100f:0} cm in front" : $"{-distance * 100f:0} cm through")})";
                    kind = "touch";
                }
            }
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (kind != laserOwner && now - laserOwnerLoggedAt >= System.Diagnostics.Stopwatch.Frequency)
            {
                laserOwnerLoggedAt = now;
                laserOwner = kind;
                Log.Info($"Touch toolbar: the {hand.ToString().ToLowerInvariant()} hand is {(owner == null ? "free for the laser" : $"not the laser's: {owner}")}");
            }
            return owner;
        }

        /// <summary>
        /// The laser, in the frame's draw: where the dominant controller's aim meets the screen the input found it on, as
        /// the controller is in the camera being drawn (the seat's input worked it out from the previous draw's camera),
        /// drawn from the drawn hand's index fingertip (<see cref="FingerPress.Fingertip(Hand, in MatrixD)"/>; the aim
        /// pose's own origin is below the drawn hand); nothing when it no longer meets that screen, so no line is left
        /// behind where the hand was. Given in the pilot's space (<see cref="Rendering.MotionExtrapolation.AddLine"/>), so
        /// it stays on the hand and the screen when the view and the ship are moved on past the game frame: given in the
        /// world it moved against the cockpit while the ship fell (2026-10-11).
        /// </summary>
        private static void DrawLaser()
        {
            if (!laserHit || laserAt == null || !MyTransparentGeometry.HasCamera)
                return;
            Hand hand = VRSettings.DominantHand;
            if (!HandWorld.TryGet(hand, out MatrixD grip, out MatrixD aim)
                || !Ray(laserAt, aim.Translation, aim.Forward, out float length, out Vector2 _) || length < 0.02f)
                return;
            Vector3D from = FingerPress.Fingertip(hand, grip);
            Vector3D along = aim.Translation + aim.Forward * length - from;
            double reach = along.Length();
            if (reach < 0.02)
                return;
            var dir = (Vector3)(along / reach);
            Rendering.MotionExtrapolation.AddLine(LaserMaterial, LaserColor, from, dir, (float)reach - 0.005f, 0.002f);
            Rendering.MotionExtrapolation.AddPoint(DotMaterial, Vector4.One, from + along * ((reach - 0.004) / reach), 0.006f);
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
