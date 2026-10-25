// HudShownAtStart: in VR a world starts with the HUD showing, whatever HUD mode the game saved last.
using HarmonyLib;
using Sandbox.Game.Gui;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The game keeps the HUD mode the player last chose (MyHud.HudState, which Tab cycles and the actions wheel's HUD entry
    /// toggles, saved in the game's config as HudState) and starts every world with it (MyHud.LoadData). Mode 0 hides the
    /// HUD but for its hazard icons (Data/Hud/Default.sbc: every plate, bar, marker and the toolbar need hud_mode above 0;
    /// the hazard icons have no such condition), so a player who hid it once, in a flat game or by the wheel, starts in VR
    /// with only those icons flashing and no Tab key to bring the rest back. So in VR a world that would start hidden starts
    /// with the HUD shown (mode 1, the game's own default); the actions wheel's HUD entry still hides it for the session.
    /// The same when the picture turns to VR's with a world already loaded (the headset found after the load). The mode the
    /// player saved is kept in the game's config, so a flat game starts as they left it.
    /// </summary>
    internal static class HudShownAtStart
    {
        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(MyHud), nameof(MyHud.LoadData)),
                postfix: new HarmonyMethod(typeof(HudShownAtStart), nameof(AfterLoad)));
            StereoPatches.PictureChanged += vr =>
            {
                if (vr && Sandbox.Game.World.MySession.Static?.Ready == true)
                    Show("the picture turned to VR's");
            };
        }

        private static void AfterLoad()
        {
            if (StereoPatches.VRPicture)
                Show("the world loaded");
        }

        private static void Show(string when)
        {
            if (MyHud.HudState != 0)
                return;
            MyHud.SetHudState(1);
            // SetHudState saves the mode in the game's config; the player's own choice stays there for a flat game.
            Sandbox.MySandboxGame.Config.HudState = 0;
            Log.Info($"HUD: the game's saved HUD mode was hidden (mode 0: only the hazard icons); {when}, so the HUD is shown " +
                     "(mode 1, the game's default) and the saved mode is left as it was. The actions wheel's HUD entry hides it again");
        }
    }
}
