using System;
using HarmonyLib;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.SessionComponents.Clipboard;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using Sandbox.Engine.Physics;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
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
    /// placer, the cube builder, a paste), where it shows where a block goes. With a welder, grinder or drill in hand
    /// (<see cref="VRSettings.ToolLaser"/>) it is the tool's own laser instead: from the tool's tip over the reach its own
    /// sensor works (the welder's or grinder's caster, the drill's sensor: <see cref="ToolAim.TrySensor"/>), with the dot where
    /// that reach meets something the sensor would (cast in the sensor's own collision layer), so the player sees where it
    /// will act and when it is in reach. A gun has none: its aim is the crosshair's (<see cref="Rendering.HandCrosshair"/>).
    /// </summary>
    internal static class HandRay
    {
        private const float LineThickness = 0.0025f;
        private const float DotRadius = 0.012f;

        /// <summary>How far the dot is moved back toward the hand so it is not buried in the surface it marks.</summary>
        private const double DotLift = 0.01;

        private static readonly Vector4 LineColor = new Vector4(1f, 0.35f, 0.25f, 0.55f);
        private static readonly Vector4 DotColor = new Vector4(1f, 1f, 1f, 1f);
        private static readonly MyStringId LineMaterial = MyStringId.GetOrCompute("SquareIgnoreDepth");
        private static readonly MyStringId DotMaterial = MyStringId.GetOrCompute("RedDot");

        private static readonly FeatureGuard guard = new FeatureGuard(nameof(HandRay));

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
                // The guard switches the ray off while it keeps failing: an exception every frame would cost more than the line is worth.
                if (!guard.On || HandTools.Off || !VRInput.Active || !(HandUse.Hooked || HandPlacement.Hooked) || !MyTransparentGeometry.HasCamera)
                    return;
                // A menu is up (the game shows its cursor): the pointer is the laser's, not a tool's.
                if (MySandboxGame.Static == null || MySandboxGame.Static.IsCursorVisible)
                    return;
                if (Ping.Aiming)
                    return; // a ping's aim draws its own, longer laser
                if (HoldsToolOrWeapon())
                {
                    if (VRSettings.ToolLaser)
                        DrawToolLaser();
                    return;
                }
                if (!HandPlacement.TryRay(out Vector3D origin, out Vector3D direction))
                    return;

                // With a block in hand the ray ends where the block goes: on what it hits, or at the builder's distance out
                // in the open (A and B move it), with the dot on that point either way.
                bool block = BuildMode.Active;
                double reach = block ? MyBlockBuilderBase.IntersectionDistance : Reach(origin, direction);
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
                guard.Failed(e, "Hand tools could not draw the hand ray");
            }
        }

        /// <summary>
        /// The held tool's laser: a line from its tip along its sensor's line to where that reach ends or first meets something, and the dot
        /// there when it meets something. Nothing for a gun, or an item the game holds its own way (<see cref="ToolAim.TryGet"/>).
        /// </summary>
        private static void DrawToolLaser()
        {
            if (!ToolAim.TryGet(out MyCharacter character, out IMyEntity weapon, out _, out _)
                || !ToolAim.TrySensor(weapon, out Vector3D from, out Vector3D to))
                return;
            Vector3D direction = Vector3D.Normalize(to - from);
            bool hit = ToolAim.FirstHit(from, to, character, weapon, MyPhysics.CollisionLayers.ObjectDetectionCollisionLayer, out double along);
            Vector3D start = weapon is IMyHandheldGunObject<MyToolBase> tool && tool.GunBase != null
                ? Vector3D.Transform(tool.GunBase.GetMuzzleLocalPosition(), weapon.WorldMatrix)
                : from;
            // Out of the tip along the sensor's line: to what the sensor meets, or as far past the tip as its reach goes.
            Vector3D end = hit ? from + direction * along : start + direction * Math.Max(0d, Vector3D.Dot(to - start, direction));
            Vector3D line = end - start;
            double length = line.Length();
            if (length > 0.02 && Vector3D.Dot(line, direction) > 0d)
                MotionExtrapolation.AddLine(LineMaterial, LineColor, start, (Vector3)(line / length), (float)length, LineThickness);
            if (hit)
                MotionExtrapolation.AddPoint(DotMaterial, DotColor, end - direction * DotLift, DotRadius);
            NoteToolLaser(weapon, hit, from, to, along);
        }

        /// <summary>
        /// One line each time the held tool changes or its laser starts or stops meeting something (not more than twice a second):
        /// what a test, or a player reading the log, can tell the laser by without seeing it.
        /// </summary>
        private static void NoteToolLaser(IMyEntity weapon, bool hit, Vector3D from, Vector3D to, double along)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            bool newTool = !ReferenceEquals(weapon, laserLoggedTool);
            if (!newTool && (hit == laserLoggedHit || now - laserLoggedAt < System.Diagnostics.Stopwatch.Frequency / 2))
                return;
            laserLoggedTool = weapon;
            laserLoggedHit = hit;
            laserLoggedAt = now;
            Log.Info($"Hand tools: the {weapon.GetType().Name}'s laser drawn from its tip over its sensor's reach ({Vector3D.Distance(from, to):0.00} m), " +
                     $"{(hit ? $"meeting something {along:0.00} m along" : "meeting nothing")}");
        }

        private static IMyEntity laserLoggedTool;
        private static bool laserLoggedHit;
        private static long laserLoggedAt;

        /// <summary>
        /// How far the ray is drawn: the builder's reach while it builds or pastes (or the game's use distance, if that is
        /// farther), otherwise as far as the use line from the hand reaches.
        /// </summary>
        private static double Reach(Vector3D origin, Vector3D direction) => Building()
            ? Math.Max(MyBlockBuilderBase.IntersectionDistance, MyConstants.DEFAULT_INTERACTIVE_DISTANCE)
            : UseReach(origin, direction);

        /// <summary>The use line's reach from <paramref name="origin"/> (<see cref="HandUse.UseLength"/>).</summary>
        private static double UseReach(Vector3D origin, Vector3D direction)
        {
            MyCharacter character = MySession.Static?.LocalCharacter;
            return character != null ? HandUse.UseLength(character, origin, direction) : MyConstants.DEFAULT_INTERACTIVE_DISTANCE;
        }

        /// <summary>The cube builder is on, or a paste is being placed.</summary>
        private static bool Building() => (MyCubeBuilder.Static != null && MyCubeBuilder.Static.IsActivated)
                                          || (MyClipboardComponent.Static != null && MyClipboardComponent.Static.IsActive);

        /// <summary>
        /// The local character has a hand tool or weapon in hand (a grinder, welder or drill, a rifle, pistol or launcher):
        /// anything in hand but the block placer, while no block or paste is being placed.
        /// </summary>
        internal static bool HoldsToolOrWeapon()
        {
            var held = MySession.Static?.LocalCharacter?.CurrentWeapon;
            return held != null && !(held is MyBlockPlacerBase) && !BuildMode.Active && !Building();
        }
    }
}
