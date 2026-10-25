// HudShown: whether the headset shows the HUD now, for every reason it may not.
using Sandbox.Game.Gui;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The one answer to "is the HUD shown?", for what draws beside it: the API's HudVisible (a mod's own HUD hides with it,
    /// <see cref="Now"/>) and the mods' HUDs moved onto the visor (<see cref="ModHudBillboards"/>, <see cref="OnVisor"/>).
    /// </summary>
    /// <remarks>
    /// The game's own reasons, read in the decompiled MyGuiScreenHudSpace and MyHud: without <c>MyHud.IsVisible</c> (set each
    /// update: no live local character, or the medical room's screen open, and not at a turret controller) the HUD screen draws
    /// nothing; in HUD mode 0 (<c>IsHudMinimal</c>, which Tab and the actions wheel's HUD entry set), with <c>MinimalHud</c> (which
    /// mods and scripts set) or in a cutscene (<c>CutsceneHud</c>) it draws only the hazard icons and the chat. And the visor's
    /// own: the helmet open with the helmet view on (<see cref="HelmetHud.Hidden"/>: the HUD is on the visor, swung up), and the
    /// suit's battery empty with Suit power failing on (<see cref="SuitPower.Out"/>). Its flickers as it runs low are not counted:
    /// the HUD is still there between them.
    /// </remarks>
    internal static class HudShown
    {
        /// <summary>The HUD is shown now, by the game and on the helmet's visor. Game thread (MyHud's state is the game thread's).</summary>
        public static bool Now =>
            MyHud.IsVisible && !MyHud.IsHudMinimal && !MyHud.MinimalHud && !MyHud.CutsceneHud && OnVisor;

        /// <summary>
        /// The visor shows its HUD now: the helmet not open with the helmet view on, the suit's battery not out. Only the visor's
        /// own reasons: on a flat screen the game hiding its HUD does not hide other mods' HUDs, so neither does this. Any thread.
        /// </summary>
        public static bool OnVisor => !HelmetHud.Hidden && !SuitPower.Out;
    }
}
