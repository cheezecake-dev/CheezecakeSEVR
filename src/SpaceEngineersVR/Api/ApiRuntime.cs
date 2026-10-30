// ApiRuntime: the plugin's one registry for SevrApi version 2, its game-thread rule, the world's end for mods' registrations, and the events' frame.
using System;
using System.IO;
using System.Threading;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Input.Bindings;
using VRage.FileSystem;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Api
{
    /// <summary>
    /// The registry behind <see cref="SevrApi"/> version 2 (<see cref="ApiCore"/>, its actions and events), one for the
    /// plugin, and what keeps it in step with the game: once a frame (<see cref="Update"/>, from <see cref="Plugin.Update"/>)
    /// the registrations made over the mod channel go when their world unloads, the count of owners' exceptions not written is
    /// written when its window ends, grip claims hear that their grip opened, and the events see what changed.
    /// </summary>
    internal static partial class ApiRuntime
    {
        public static readonly ApiCore Core = new ApiCore(OnGameThread, () => MySession.Static);
        public static readonly ApiActions Actions = new ApiActions(Core);
        public static readonly ApiEvents Events = new ApiEvents(Core);
        public static readonly ApiMenus Menus = new ApiMenus(Core, IconExists);
        public static readonly ApiScreens Screens = new ApiScreens(Core);
        public static readonly ApiGuns Guns = new ApiGuns(Core);
        public static readonly ApiCockpits Cockpits = new ApiCockpits(Core, SpaceEngineersVR.Cockpits.CockpitRig.HasControls);
        public static readonly ApiGrips Grips = new ApiGrips(Core);

        /// <summary>An outside claim holds the hand's grip still: VR is on and the grip table has it as theirs.</summary>
        private static readonly Func<int, bool> outsideHolds = hand => SevrApi.IsActive() && Hands.GripClaim.Holds((Hand)hand, ApiGrips.ClaimName);
        private static bool gripsAsked;

        private static readonly FeatureGuard guard = new FeatureGuard("SEVR API frame");
        private static MySession world;

        /// <summary>
        /// The game's main thread, where it updates (the game sets it before plugins start); a call is refused while it is
        /// not known, which cannot happen once a plugin runs.
        /// </summary>
        private static bool OnGameThread()
        {
            Thread main = MyUtils.MainThread;
            return main != null && ReferenceEquals(Thread.CurrentThread, main);
        }

        /// <summary>
        /// A menu item's icon is there: a full path as given, or a path under the game's Content folder, through the game's file
        /// system (which reads a mod's packed files too), as the game finds its own icons.
        /// </summary>
        private static bool IconExists(string icon) =>
            MyFileSystem.FileExists(Path.IsPathRooted(icon) ? icon : Path.Combine(MyFileSystem.ContentPath, icon));

        /// <summary>Test aids: a caller of the API, registering through <see cref="SevrApi.GetApi"/> as another plugin would.</summary>
        static partial void TestAidsFrame();

        /// <summary>Game thread, once a frame.</summary>
        internal static void Update()
        {
            if (!guard.On)
                return;
            try
            {
                if (!gripsAsked)
                {
                    // Asked in the grip table's order (Hands.GripPriority.Outside), only while VR is on.
                    Hands.GripClaim.Register(ApiGrips.ClaimName, Hands.GripPriority.Outside, hand => SevrApi.IsActive() && Grips.Take((int)hand));
                    gripsAsked = true;
                }
                TestAidsFrame();
                MySession session = MySession.Static;
                if (!ReferenceEquals(session, world))
                {
                    // Each mod-channel registration is tied to the world it was made in, so a world's mods registering before
                    // this frame sees it (one world loaded straight after another) keep theirs; only other worlds' go.
                    int gone = Core.EndOtherWorlds(session);
                    if (gone > 0)
                        Log.Info($"SEVR API: the world changed; {gone} registration(s) made over the mod channel in another world taken away");
                    world = session;
                }
                Core.EndExceptionWindows();
                Grips.Frame(outsideHolds);
                Vector2D tangents = SevrApi.Tangents();
                Events.Observe(new ApiEvents.State
                {
                    Active = SevrApi.IsActive(),
                    DominantHand = (int)VRSettings.DominantHand,
                    Context = SevrApi.IsActive() ? ControllerBindings.Contexts : BindingContext.None,
                    HudVisible = SevrApi.HudVisible(),
                    HudDepth = VRSettings.HudDistance,
                    HudTangentX = tangents.X,
                    HudTangentY = tangents.Y,
                    Bindings = ControllerBindings.BindingsVersion,
                });
            }
            catch (Exception e)
            {
                guard.Failed(e, "SEVR API: the world's end or the events' frame failed");
            }
        }

        /// <summary>The hand an integrator names: 0 the left, 1 the right; null for anything else.</summary>
        internal static Hand? HandOf(int hand) => hand == 0 ? Hand.Left : hand == 1 ? Hand.Right : (Hand?)null;
    }
}
