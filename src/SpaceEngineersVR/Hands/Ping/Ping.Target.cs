// Ping: what the ray meets, and what it is called.
using System;
using System.Collections.Generic;
using Sandbox.Definitions;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Voxels;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    internal static partial class Ping
    {
        /// <summary>What a ping or mark is about: where its GPS goes, what it is called, and the line the team reads.</summary>
        internal struct Target
        {
            /// <summary>The ray met something; when it did not, the target is the player's own position.</summary>
            public bool Hit;

            /// <summary>Where the GPS goes: the point the ray met, or a detected ore deposit's middle, or the player.</summary>
            public Vector3D Point;

            /// <summary>Along the ray to where it met something, metres (for the laser); the range when it met nothing.</summary>
            public double Distance;

            /// <summary>The GPS's name: at most <see cref="Sandbox.Game.Screens.Helpers.MyGps.NAME_MAX_LENGTH"/> characters, no colon.</summary>
            public string Name;

            /// <summary>What the chat line and the GPS description say: "Iron ore here", "Enemy grid: Raider".</summary>
            public string Line;

            public Color Color;

            /// <summary>For the log: what kind of thing it is.</summary>
            public string What;
        }

        /// <summary>A seated player's own grid is not a target this close to the head: the cockpit around it.</summary>
        private const double SeatSkip = 4.0;

        /// <summary>An ore deposit the HUD shows counts when the ray passes within this angle of it, degrees.</summary>
        private const double OreConeDegrees = 2.0;

        /// <summary>How far past the surface the ray met a deposit may lie, metres (the detector finds ore underground).</summary>
        private const double OreDepth = 60.0;

        private static readonly Color PointColor = new Color(255, 170, 40);
        private static readonly Color OreColor = new Color(255, 215, 60);
        private static readonly Color FriendlyColor = new Color(117, 201, 241); // the game's own GPS colour
        private static readonly Color DamagedColor = new Color(255, 120, 60);
        private static readonly Color EnemyColor = new Color(255, 60, 50);
        private static readonly Color NeutralColor = new Color(235, 235, 120);

        private static readonly List<MyPhysics.HitInfo> hits = new List<MyPhysics.HitInfo>();
        private static readonly List<MyLineSegmentOverlapResult<MyVoxelBase>> voxels = new List<MyLineSegmentOverlapResult<MyVoxelBase>>();

        /// <summary>
        /// The ray a ping takes: on foot the pointing hand's, in a seat (the hands on the controls) and with no tracked hand
        /// the head's, as the game's camera has it.
        /// </summary>
        internal static bool TryRay(VRContext context, out Vector3D origin, out Vector3D direction, out bool fromHand)
        {
            origin = direction = Vector3D.Zero;
            fromHand = context != VRContext.Ship && HandTools.TryGetReachRay(out origin, out direction);
            if (fromHand)
                return true;
            var camera = MySector.MainCamera;
            if (camera == null)
                return false;
            origin = camera.Position;
            direction = camera.ForwardVector;
            return direction.Normalize() > 1e-6;
        }

        /// <summary>
        /// What the ray from <paramref name="origin"/> meets within <see cref="Range"/>. The physics first (grids, characters,
        /// floating objects, voxels near anything), as a camera block's raycast does; then, with <paramref name="voxelsToo"/>,
        /// the voxel maps' own geometry, which reaches asteroids and planets too far off to have physics (the camera block's
        /// fallback: a planet seen from outside its gravity is its sphere). Then what the HUD's ore markers show along it.
        /// </summary>
        internal static Target Cast(Vector3D origin, Vector3D direction, bool voxelsToo)
        {
            MyCharacter me = MySession.Static.LocalCharacter;
            MyCubeGrid seatGrid = (MySession.Static.ControlledEntity as MyShipController)?.CubeGrid;
            Vector3D end = origin + direction * Range;

            IMyEntity entity = null;
            double distance = Range;
            hits.Clear();
            MyPhysics.CastRay(origin, end, hits);
            foreach (MyPhysics.HitInfo hit in hits)
            {
                IMyEntity hitEntity = hit.HkHitInfo.GetHitEntity();
                if (hitEntity == null)
                    continue;
                IMyEntity top = hitEntity.GetTopMostParent();
                double along = Vector3D.Dot(hit.Position - origin, direction);
                if (top == me || along < 0d)
                    continue;
                if (seatGrid != null && top == seatGrid && along < SeatSkip)
                    continue;
                if (along < distance)
                {
                    distance = along;
                    entity = hitEntity;
                }
            }
            hits.Clear();

            if (voxelsToo && TryVoxels(origin, direction, distance, out MyVoxelBase voxel, out double voxelDistance))
            {
                entity = voxel;
                distance = voxelDistance;
            }

            Vector3D point = origin + direction * distance;
            Target target;
            if (entity is MyCubeGrid grid)
                target = Grid(grid, point, direction);
            else if (entity is MyCharacter character)
                target = Character(character, point);
            else if (entity is MyVoxelBase map)
                target = Voxel(map, point, direction);
            else if (entity != null)
                target = Spot(point, "other");
            else
                target = Own();
            if (entity != null)
                target.Distance = distance;

            // A deposit the ore detector found, which the HUD marks: pointing at its marker pings the ore, unless a grid or
            // someone stands in front of it.
            if (!(entity is MyCubeGrid) && !(entity is MyCharacter)
                && TryOre(origin, direction, entity != null ? distance + OreDepth : Range, out Target ore))
                target = ore;
            return target;
        }

        /// <summary>The nearest voxel surface along the ray, nearer than <paramref name="limit"/>.</summary>
        private static bool TryVoxels(Vector3D origin, Vector3D direction, double limit, out MyVoxelBase found, out double distance)
        {
            found = null;
            distance = limit;
            var ray = new LineD(origin, origin + direction * limit);
            voxels.Clear();
            MyGamePruningStructure.GetVoxelMapsOverlappingRay(ref ray, voxels);
            var seen = new HashSet<MyVoxelBase>();
            foreach (MyLineSegmentOverlapResult<MyVoxelBase> overlap in voxels)
            {
                MyVoxelBase root = overlap.Element?.RootVoxel;
                if (root == null || root.Storage == null || root.MarkedForClose || !seen.Add(root))
                    continue;
                Vector3D? at = null;
                if (root is MyPlanet planet && FarPlanet(planet, origin))
                {
                    double? t = new BoundingSphereD(planet.PositionComp.GetPosition(), planet.AverageRadius).Intersects(new RayD(origin, direction));
                    if (t.HasValue)
                        at = origin + direction * t.Value;
                }
                else if (root.GetIntersectionWithLine(ref ray, out Vector3D? v))
                    at = v;
                if (!at.HasValue)
                    continue;
                double along = Vector3D.Dot(at.Value - origin, direction);
                if (along >= 0d && along < distance)
                {
                    distance = along;
                    found = root;
                }
            }
            voxels.Clear();
            return found != null;
        }

        /// <summary>Outside the planet's gravity and its highest point: its surface is not worth meshing for a ping (MyCameraBlock.Raycast).</summary>
        private static bool FarPlanet(MyPlanet planet, Vector3D from)
        {
            var gravity = planet.Components.Get<MyGravityProviderComponent>();
            double maximum = planet.MaximumRadius;
            return gravity != null && !gravity.IsPositionInRange(from)
                   && Vector3D.DistanceSquared(from, planet.PositionComp.GetPosition()) > maximum * maximum;
        }

        private static Target Grid(MyCubeGrid grid, Vector3D point, Vector3D direction)
        {
            string gridName = Clean(grid.DisplayName);
            long owner = grid.BigOwners.Count > 0 ? grid.BigOwners[0] : 0L;
            MyRelationsBetweenPlayerAndBlock relation = owner == 0L
                ? MyRelationsBetweenPlayerAndBlock.NoOwnership
                : MyIDModule.GetRelationPlayerBlock(owner, MySession.Static.LocalPlayerId, MyOwnershipShareModeEnum.Faction);
            if (relation == MyRelationsBetweenPlayerAndBlock.Enemies)
                return Make(point, "Enemy " + gridName, "Enemy grid: " + gridName, EnemyColor, "enemy grid");
            if (relation == MyRelationsBetweenPlayerAndBlock.Neutral)
                return Make(point, "Neutral " + gridName, "Neutral grid: " + gridName, NeutralColor, "neutral grid");

            Vector3I? cell = grid.RayCastBlocks(point - direction * 0.5, point + direction * 1.5);
            MySlimBlock block = cell.HasValue ? grid.GetCubeBlock(cell.Value) : null;
            if (block == null)
                return Make(point, gridName, gridName, FriendlyColor, "grid");
            string name = BlockName(block);
            if (block.Integrity < block.BuildIntegrity - 0.01f)
                return Make(point, "Damaged " + name, $"Damaged: {name} ({gridName})", DamagedColor, "damaged block");
            if (!block.IsFullIntegrity)
                return Make(point, "Unfinished " + name, $"Unfinished: {name} ({gridName})", DamagedColor, "unfinished block");
            return Make(point, name, $"{name} ({gridName})", FriendlyColor, "block");
        }

        /// <summary>The name the terminal shows: the block's own if it has one, else its kind's.</summary>
        private static string BlockName(MySlimBlock block)
        {
            if (block.FatBlock is MyTerminalBlock terminal && terminal.CustomName != null && terminal.CustomName.Length > 0)
                return Clean(terminal.CustomName.ToString());
            return Clean(block.BlockDefinition?.DisplayNameText ?? "Block");
        }

        private static Target Character(MyCharacter character, Vector3D point)
        {
            string name = Clean(character.DisplayNameText);
            if (string.IsNullOrEmpty(name))
                name = "Someone";
            long identity = character.GetPlayerIdentityId();
            bool enemy = identity != 0L && MyIDModule.GetRelationPlayerBlock(identity, MySession.Static.LocalPlayerId,
                MyOwnershipShareModeEnum.Faction) == MyRelationsBetweenPlayerAndBlock.Enemies;
            return enemy
                ? Make(point, "Enemy " + name, "Enemy: " + name, EnemyColor, "enemy character")
                : Make(point, name, name + " here", FriendlyColor, "character");
        }

        /// <summary>The voxel material just under the surface the ray met: an ore by the name the HUD gives it, or a plain spot.</summary>
        private static Target Voxel(MyVoxelBase map, Vector3D point, Vector3D direction)
        {
            Vector3D inside = point + direction * 0.5;
            MyVoxelMaterialDefinition material = map.RootVoxel.GetMaterialAt(ref inside) ?? map.RootVoxel.GetMaterialAt(ref point);
            if (material == null || string.IsNullOrEmpty(material.MinedOre) || material.MinedOre == "Stone")
                return Spot(point, "voxel");
            string ore = OreName(material);
            return Make(point, ore, ore + " here", OreColor, "voxel " + material.MinedOre);
        }

        /// <summary>The detected deposit whose HUD marker is nearest the ray, within <see cref="OreConeDegrees"/> and <paramref name="reach"/>.</summary>
        private static bool TryOre(Vector3D origin, Vector3D direction, double reach, out Target target)
        {
            target = default;
            double bestCos = Math.Cos(MathHelper.ToRadians(OreConeDegrees));
            bool found = false;
            foreach (MyEntityOreDeposit deposit in MyHud.OreMarkers)
            {
                if (deposit?.VoxelMap == null || deposit.VoxelMap.Closed || deposit.VoxelMap.MarkedForClose)
                    continue;
                foreach (MyEntityOreDeposit.Data data in deposit.Materials)
                {
                    if (data.Material == null)
                        continue;
                    // MyEntityOreDeposit.Data.ComputeWorldPosition, which is internal.
                    Vector3 local = data.AverageLocalPosition;
                    MyVoxelCoordSystems.LocalPositionToWorldPosition(
                        deposit.VoxelMap.PositionComp.GetPosition() - (Vector3D)deposit.VoxelMap.StorageMin, ref local, out Vector3D at);
                    Vector3D to = at - origin;
                    double length = to.Length();
                    if (length < 1e-3 || length > reach)
                        continue;
                    double cos = Vector3D.Dot(to, direction) / length;
                    if (cos <= bestCos)
                        continue;
                    bestCos = cos;
                    string ore = OreName(data.Material);
                    target = Make(at, ore, ore + " here", OreColor, "ore marker " + data.Material.MinedOre);
                    target.Distance = length;
                    found = true;
                }
            }
            return found;
        }

        /// <summary>"Iron ore", "Ice": the HUD's own name for what the material mines to.</summary>
        private static string OreName(MyVoxelMaterialDefinition material)
        {
            string name = Clean(MyHud.OreMarkers.GetOreName(material));
            if (string.IsNullOrEmpty(name))
                name = material.MinedOre;
            return material.MinedOre == "Ice" ? name : name + " ore";
        }

        private static Target Spot(Vector3D point, string what) => Make(point, "Here", "Here", PointColor, what);

        /// <summary>The ray met nothing: where the player is (in a seat, the ship), as the game's "new from current position".</summary>
        private static Target Own()
        {
            IMyControllableEntity controlled = MySession.Static.ControlledEntity;
            Vector3D at = controlled?.Entity != null ? controlled.Entity.PositionComp.GetPosition() : MySession.Static.LocalCharacter.PositionComp.GetPosition();
            Target target = Make(at, "Here", "Here", PointColor, "own position");
            target.Hit = false;
            target.Distance = Range;
            return target;
        }

        private static Target Make(Vector3D point, string name, string line, Color color, string what) => new Target
        {
            Hit = true,
            Point = point,
            Name = GpsName(name),
            Line = line,
            Color = color,
            What = what,
        };

        /// <summary>A GPS name the game's chat scan reads back whole: no colon (its separator) and at most 32 characters.</summary>
        internal static string GpsName(string name)
        {
            string clean = (name ?? string.Empty).Replace(':', ' ').Trim();
            if (clean.Length > Sandbox.Game.Screens.Helpers.MyGps.NAME_MAX_LENGTH)
                clean = clean.Substring(0, Sandbox.Game.Screens.Helpers.MyGps.NAME_MAX_LENGTH).TrimEnd();
            return clean.Length == 0 ? "Here" : clean;
        }

        /// <summary>One line, no control characters (a name typed in a terminal can hold a newline).</summary>
        private static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            var chars = text.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (char.IsControl(chars[i]))
                    chars[i] = ' ';
            }
            return new string(chars).Trim();
        }
    }
}
