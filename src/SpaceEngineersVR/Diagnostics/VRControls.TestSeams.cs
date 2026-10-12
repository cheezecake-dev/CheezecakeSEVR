// VRControls: test seams that stand in for the game so the mapping can be driven without it.
using System;
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
    internal static partial class VRControls
    {
        /// <summary>Test seam: stands in for the controlled entity, so the mapping can be driven without a game.</summary>
        internal static VRContext? ContextOverride { get; set; }

        /// <summary>Test seam: what the game's camera is doing, with <see cref="ContextOverride"/>.</summary>
        internal static bool CameraViewOverride { get; set; }

        /// <summary>Test seam: with <see cref="ContextOverride"/>, the seat is one that does not fly the ship (a passenger seat).</summary>
        internal static bool SeatCannotFlyOverride { get; set; }

        /// <summary>Test seam: stands in for the screen that has focus when it is a menu (null: none; gameplay does not count), so a menu can be had without a game.</summary>
        internal static Func<object> MenuScreenOverride { get; set; }

        /// <summary>Test seam: stands in for the game's gameplay input pass that <see cref="WalkBesidePanel"/> runs (the pass asks the game's questions about the controls), so it can be had without a game.</summary>
        internal static Action GameplayPassOverride { get; set; }
    }
}
