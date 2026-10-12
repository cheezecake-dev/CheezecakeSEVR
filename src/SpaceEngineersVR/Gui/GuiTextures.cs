// GuiTextures: game content paths shared by more than one screen.
namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// Keys of the game's own GUI textures that several screens draw. They are content paths as the game's content
    /// loader spells them, with backslashes on every platform; they are not file system paths and must not be normalised.
    /// </summary>
    internal static class GuiTextures
    {
        /// <summary>Folder of the HUD's icons; the icon file name follows.</summary>
        public const string Hud = "Textures\\GUI\\Icons\\HUD 2017\\";

        /// <summary>The "back" icon of the radial menus' pages.</summary>
        public const string Back = Hud + "RotateCounterClockWise.png";

        /// <summary>The spectator camera, the icon of the external view.</summary>
        public const string Spectator = Hud + "CameraSpectator.png";

        /// <summary>A plain blank texture, for drawing flat colour.</summary>
        public const string Blank = "Textures\\GUI\\Blank.dds";

        // The sectors and centre circle of the game's radial menu, drawn by both wheels.
        public const string RadialSector = "Textures\\GUI\\Controls\\RadialSectorUnSelected.dds";
        public const string RadialSectorSelected = "Textures\\GUI\\Controls\\RadialSectorSelected.dds";
        public const string RadialCentre = "Textures\\GUI\\Controls\\RadialCentralCircle.dds";
        public const string RadialCentreSelected = "Textures\\GUI\\Controls\\RadialCentralCircleSelected.dds";
    }
}
