using System;
using HarmonyLib;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.SessionComponents.Clipboard;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// A thin line from the dominant hand along its ray, and a dot where the ray meets something, drawn while the ray is
    /// what the game uses for interacting and building (<see cref="HandUse"/>, <see cref="HandPlacement"/>), so the
    /// player can see what the hand is pointing at. Drawn as the game draws its own lines, as billboards in the world,
    /// from the gameplay screen's draw; never while a menu is up (the laser pointer is the line then, see
    /// <see cref="Laser"/>). The dot is where the builder's own ray cast hits (MyCubeBuilder.GetCurrentRayIntersection),
    /// the same ray the builder and the use detectors are given.
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
                if (!HandPlacement.TryRay(out Vector3D origin, out Vector3D direction))
                    return;

                double reach = Reach();
                double? hit = MyCubeBuilder.Static != null ? MyCubeBuilder.GetCurrentRayIntersection() : null;
                bool dot = hit.HasValue && hit.Value <= reach;
                double length = dot ? hit.Value : reach;
                if (length < 0.02)
                    return;

                MyTransparentGeometry.AddLineBillboard(LineMaterial, LineColor, origin, (Vector3)direction, (float)length, LineThickness);
                if (dot)
                    MyTransparentGeometry.AddPointBillboard(DotMaterial, DotColor, origin + direction * Math.Max(length - DotLift, 0.0), DotRadius, 0f);
            }
            catch (Exception e)
            {
                if (errors++ < 3)
                    Log.Error(e, "Hand tools could not draw the hand ray");
            }
        }

        /// <summary>How far the ray is drawn: the builder's reach while it builds or pastes, the use reach otherwise.</summary>
        private static double Reach()
        {
            bool building = (MyCubeBuilder.Static != null && MyCubeBuilder.Static.IsActivated)
                            || (MyClipboardComponent.Static != null && MyClipboardComponent.Static.IsActive);
            return building ? Math.Max(MyBlockBuilderBase.IntersectionDistance, (float)UseReach) : UseReach;
        }
    }
}
