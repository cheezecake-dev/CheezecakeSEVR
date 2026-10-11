using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems.TextSurfaceScripts;
using VRage.Game.GUI.TextPanel;
using VRageMath;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// "Touch Toolbar" among a screen's scripts in the game's own screen settings (Content: Script), as the game's own
    /// Artificial Horizon or Gravity are: a cockpit screen set to it is where <see cref="TouchToolbar"/> puts the ship's
    /// toolbar for its pilot, and the choice is kept with the world as any screen's content is.
    /// </summary>
    /// <remarks>
    /// What the script draws itself, in the screen's sprites, is what everyone but the pilot sees (and the pilot while
    /// not seated): the cockpit's toolbar page, its slots with their numbers, names and On/Off, without the items' icons
    /// (a script's sprites can only use the game's LCD textures). The seated pilot's picture of that screen is
    /// <see cref="TouchToolbar"/>'s own, with the icons, and takes the touches. Added to the game's list by
    /// <see cref="TouchToolbar"/> after each MyTextSurfaceScriptFactory.LoadScripts.
    /// </remarks>
    [MyTextSurfaceScript(Id, DisplayName)]
    internal sealed class TouchToolbarScript : MyTextSurfaceScriptBase
    {
        public const string Id = "SEVR_TouchToolbar";
        public const string DisplayName = "Touch Toolbar";

        public override ScriptUpdate NeedsUpdate => ScriptUpdate.Update10;

        public TouchToolbarScript(Sandbox.ModAPI.Ingame.IMyTextSurface surface, VRage.Game.ModAPI.Ingame.IMyCubeBlock block, Vector2 size)
            : base(surface, block, size)
        {
            m_backgroundColor = TouchToolbar.Background;
            m_foregroundColor = TouchToolbar.IconBlue;
        }

        public override void Run()
        {
            base.Run();
            if (m_surface == null)
                return;
            using (MySpriteDrawFrame frame = m_surface.DrawFrame())
                TouchToolbar.DrawSprites(frame, m_block as MyCockpit, m_surface.TextureSize, m_surface.SurfaceSize);
        }
    }
}
