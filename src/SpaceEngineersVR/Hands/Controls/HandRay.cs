using System;
using HarmonyLib;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.SessionComponents.Clipboard;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Rendering;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// A thin line from the dominant hand along its ray, and a dot where the ray meets something, drawn while the ray is
    /// what the game uses for interacting and building (<see cref="HandUse"/>, <see cref="HandPlacement"/>), so the
    /// player can see what the hand is pointing at. Drawn as the game draws its own lines, as billboards, from the
    /// gameplay screen's draw, given in the local character's space (<see cref="MotionExtrapolation.AddLine"/>) so they stay
    /// on the hand when the body and the view are moved on past the game frame; never while a menu is up (the laser
    /// pointer is the line then, see <see cref="Laser"/>). The dot is where the builder's own ray cast hits (MyCubeBuilder.GetCurrentRayIntersection),
    /// the same ray the builder and the use detectors are given. Drawn for the empty hand and for placing blocks (the block
    /// placer, the cube builder, a paste), where it shows where a block goes; never while a hand tool or weapon is held
    /// (<see cref="HoldsToolOrWeapon"/>): the tool itself shows where it points, and a line off it breaks the immersion.
    /// </summary>
    internal static class HandRay
    {
        private const float LineThickness = 0.0025f;
        private const float DotRadius = 0.012f;

        /// <summary>How far the dot is moved back toward the hand so it is not buried in the surface it marks.</summary>
        private const double DotLift = 0.01;

        /// <summary>The use reach, when not building (MyConstants.DEFAULT_INTERACTIVE_DISTANCE), and the ray's length when it hits nothing.</summary>
        private const double UseReach = 5.0;

        private static readonly Vector4 LineColor = new Vector4(1f, 0.35f, 0.25f, 0.55f);
        private static readonly Vector4 DotColor = new Vector4(1f, 1f, 1f, 1f);
        private static readonly MyStringId LineMaterial = MyStringId.GetOrCompute("SquareIgnoreDepth");
        private static readonly MyStringId DotMaterial = MyStringId.GetOrCompute("RedDot");

        private static int errors;

        public static void Patch(Harmony harmony)
        {
            // The gameplay screen's draw (only when there is a scene), like the laser's line. Billboards added up to the
            // end of the game's frame are drawn in it.
            harmony.Patch(AccessTools.Method(typeof(MySession), nameof(MySession.DrawSync)),
                postfix: new HarmonyMethod(typeof(HandRay), nameof(Draw)));
            Log.Info("Hand tools: hand ray (line and dot) hooked");
        }

        private static void Draw()
        {
            try
            {
                HandPlacement.Hook();
                // After three failures it stays off for the run: an exception every frame would cost more than the line is worth.
                if (errors >= 3 || HandTools.Off || !VRInput.Active || !(HandUse.Hooked || HandPlacement.Hooked) || !MyTransparentGeometry.HasCamera)
                    return;
                // A menu is up (the game shows its cursor): the pointer is the laser's, not a tool's.
                if (MySandboxGame.Static == null || MySandboxGame.Static.IsCursorVisible)
                    return;
                if (HoldsToolOrWeapon())
                    return;
                if (!HandPlacement.TryRay(out Vector3D origin, out Vector3D direction))
                    return;

                // With a block in hand the ray ends where the block goes: on what it hits, or at the builder's distance out
                // in the open (A and B move it), with the dot on that point either way.
                bool block = BuildMode.Active;
                double reach = block ? MyBlockBuilderBase.IntersectionDistance : Reach();
                double? hit = MyCubeBuilder.Static != null ? MyCubeBuilder.GetCurrentRayIntersection() : null;
                bool onHit = hit.HasValue && hit.Value <= reach;
                bool dot = onHit || block;
                double length = onHit ? hit.Value : reach;
                if (length < 0.02)
                    return;

                MotionExtrapolation.AddLine(LineMaterial, LineColor, origin, (Vector3)direction, (float)length, LineThickness);
                if (dot)
                    MotionExtrapolation.AddPoint(DotMaterial, DotColor, origin + direction * Math.Max(length - DotLift, 0.0), DotRadius);
            }
            catch (Exception e)
            {
                if (errors++ < 3)
                    Log.Error(e, "Hand tools could not draw the hand ray");
            }
        }

        /// <summary>How far the ray is drawn: the builder's reach while it builds or pastes, the use reach otherwise.</summary>
        private static double Reach() => Building() ? Math.Max(MyBlockBuilderBase.IntersectionDistance, (float)UseReach) : UseReach;

        /// <summary>The cube builder is on, or a paste is being placed.</summary>
        private static bool Building() => (MyCubeBuilder.Static != null && MyCubeBuilder.Static.IsActivated)
                                          || (MyClipboardComponent.Static != null && MyClipboardComponent.Static.IsActive);

        /// <summary>
        /// The local character has a hand tool or weapon in hand (a grinder, welder or drill, a rifle, pistol or launcher):
        /// anything in hand but the block placer, while no block or paste is being placed.
        /// </summary>
        private static bool HoldsToolOrWeapon()
        {
            var held = MySession.Static?.LocalCharacter?.CurrentWeapon;
            return held != null && !(held is MyBlockPlacerBase) && !BuildMode.Active && !Building();
        }
    }
}
