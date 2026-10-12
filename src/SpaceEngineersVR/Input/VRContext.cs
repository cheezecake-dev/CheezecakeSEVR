// VRContext: which of the game's control sets the hands are driving.
using System.Collections.Generic;
using System.Diagnostics;
using Sandbox;
using Sandbox.Engine.Utils;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Gui;
using SpaceEngineersVR.Tracking;
using VRage.Input;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Input
{
    /// <summary>Which of the game's control sets the hands are driving.</summary>
    internal enum VRContext
    {
        None,
        OnFoot,
        Jetpack,

        /// <summary>A ship controller: a cockpit, a remote control, a rover's seat, anything that is a MyShipController.</summary>
        Ship,

        /// <summary>A controlled turret, or another block that is flown like one (turret control block, searchlight).</summary>
        Turret,

        /// <summary>
        /// The free spectator camera is the view (F8, MySpectatorCameraController moved by the player: UserControlled, or
        /// FreeMouse); the character stays the controlled entity, and the game asks the spectator's own questions in CX_SPECTATOR.
        /// </summary>
        Spectator,
    }
}
