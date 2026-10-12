// Ping: what the ray meets, and what it is called.
using System;
using System.Collections.Generic;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GUI.HudViewers;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using VRage.Game;
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
            /// <summary>The ray met something within <see cref="Range"/>. When it did not there is nothing to ping or mark.</summary>
            public bool Hit;

            /// <summary>Where the GPS goes: the point the ray met, or the middle of an ore deposit the HUD marks.</summary>
            public Vector3D Point;

            /// <summary>Along the ray to where it met something, metres (for the laser); the range when it met nothing.</summary>
            public double Distance;

            /// <summary>The GPS's name: at most <see cref="Sandbox.Game.Screens.Helpers.MyGps.NAME_MAX_LENGTH"/> characters, no colon. Empty when the ray met nothing.</summary>
            public string Name;

            /// <summary>What the chat line and the GPS description say: "Iron ore here", "Large grid".</summary>
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

        private static readonly List<MyPhysics.HitInfo> hits = new List<MyPhysics.HitInfo>();
        private static readonly List<MyLineSegmentOverlapResult<MyVoxelBase>> voxels = new List<MyLineSegmentOverlapResult<MyVoxelBase>>();

        /// <summary>The ore the HUD draws a marker for, one per material (the nearest), as <see cref="HudOres"/> finds them.</summary>
        private static readonly Dictionary<byte, Marker> markers = new Dictionary<byte, Marker>();

        private struct Marker
        {
            public MyVoxelMaterialDefinition Material;
            public Vector3D At;
            public double DistanceSquared;
        }

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
        /// What the ray from <paramref name="origin"/> meets within <see cref="Range"/> of the character. The physics first
        /// (grids, characters, floating objects, voxels near anything), as a camera block's raycast does; then, with
        /// <paramref name="voxelsToo"/>, the voxel maps' own geometry, which reaches asteroids and planets that have no physics
        /// yet (the camera block's fallback: a planet seen from outside its gravity is its sphere). Then what the HUD's ore
        /// markers show along it. Past the range the ray finds nothing: <see cref="Target.Hit"/> is false.
        /// </summary>
        internal static Target Cast(Vector3D origin, Vector3D direction, bool voxelsToo)
        {
            MyCharacter me = MySession.Static.LocalCharacter;
            if (me == null)
                return Nothing();
            MyCubeGrid seatGrid = (MySession.Static.ControlledEntity as MyShipController)?.CubeGrid;
            double range = Range;
            Vector3D centre = me.PositionComp.GetPosition();
            Vector3D end = origin + direction * range;

            IMyEntity entity = null;
            double distance = range;
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

            // The ray starts at the hand, which is not quite where the suit's antenna is.
            Vector3D point = origin + direction * distance;
            if (entity != null && Vector3D.DistanceSquared(point, centre) > range * range)
                entity = null;

            Target target;
            if (entity is MyCubeGrid grid)
                target = Grid(grid, point, direction);
            else if (entity is MyCharacter character)
                target = Character(character, point);
            else if (entity is MyVoxelBase map)
                target = Voxel(map, point);
            else if (entity != null)
                target = Spot(point, "Here", "other");
            else
                target = Nothing();
            if (entity != null)
                target.Distance = distance;

            // A deposit the HUD marks: pointing at its marker pings the ore, unless a grid or someone stands in front of it.
            if (!(entity is MyCubeGrid) && !(entity is MyCharacter)
                && TryOre(origin, direction, entity != null ? distance + OreDepth : range, centre, range, out Target ore))
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

        /// <summary>
        /// A grid the player owns or shares with their faction is named, and its block (and the block's damage) too. Any other is
        /// only a grid: no name, no owner, no friend or foe.
        /// </summary>
        private static Target Grid(MyCubeGrid grid, Vector3D point, Vector3D direction)
        {
            if (!Shared(grid))
            {
                string size = grid.GridSizeEnum == MyCubeSize.Large ? "Large grid" : "Small grid";
                return Make(point, size, size, PointColor, "grid");
            }

            string gridName = Clean(grid.DisplayName);
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

        /// <summary>
        /// The grid is the local player's own or their faction's: it has big owners, and every one of them is
        /// (<see cref="Shared(long)"/>), as the ship model asks of a grid too. Only its name and the pointed-at block are told
        /// here, not its insides, so a faction member's grid counts without its blocks being shared with the faction.
        /// </summary>
        private static bool Shared(MyCubeGrid grid)
        {
            if (grid.BigOwners.Count == 0)
                return false;
            foreach (long owner in grid.BigOwners)
            {
                if (!Shared(owner))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// The identity is the local player's own or in their faction, as the game's own relation says
        /// (<see cref="MyIDModule.GetRelationPlayerBlock"/>: Owner, or FactionShare). Anyone else, and nobody, is not.
        /// </summary>
        private static bool Shared(long identity)
        {
            if (identity == 0L)
                return false;
            MyRelationsBetweenPlayerAndBlock relation = MyIDModule.GetRelationPlayerBlock(identity, MySession.Static.LocalPlayerId, MyOwnershipShareModeEnum.Faction);
            return relation == MyRelationsBetweenPlayerAndBlock.Owner || relation == MyRelationsBetweenPlayerAndBlock.FactionShare;
        }

        /// <summary>The name the terminal shows: the block's own if it has one and the player may see it in the terminal, else its kind's.</summary>
        private static string BlockName(MySlimBlock block)
        {
            if (block.FatBlock is MyTerminalBlock terminal && terminal.CustomName != null && terminal.CustomName.Length > 0 && terminal.HasLocalPlayerAccess())
                return Clean(terminal.CustomName.ToString());
            return Clean(block.BlockDefinition?.DisplayNameText ?? "Block");
        }

        /// <summary>A player of the player's own faction (or themselves) is named; any other is only a player: no name, no friend or foe.</summary>
        private static Target Character(MyCharacter character, Vector3D point)
        {
            if (!Shared(character.GetPlayerIdentityId()))
                return Make(point, "Player", "Player", PointColor, "character");
            string name = Clean(character.DisplayNameText);
            if (string.IsNullOrEmpty(name))
                name = "Someone";
            return Make(point, name, name + " here", FriendlyColor, "character");
        }

        /// <summary>A voxel surface is only an asteroid or a planet: the material under it is not read, so no ore is told apart without a marker.</summary>
        private static Target Voxel(MyVoxelBase map, Vector3D point) =>
            Spot(point, map.RootVoxel is MyPlanet ? "Planet" : "Asteroid", "voxel");

        /// <summary>
        /// The ore the HUD draws a marker for, where it draws it: for each material the deposit nearest the controlled entity
        /// (<c>MyGuiScreenHudSpace.DrawOreMarkers</c>), and none when the HUD does not draw them: ore markers off, the HUD
        /// hidden (dead, the medical screen), minimal or in a cutscene, or the signal display mode off
        /// (<c>MyHudMarkerRender.AddOre</c>).
        /// </summary>
        private static void HudOres(Vector3D fallback)
        {
            markers.Clear();
            MyHudOreMarkers oreMarkers = MyHud.OreMarkers;
            if (!oreMarkers.Visible || !MyHud.IsVisible || MyHud.IsHudMinimal || MyHud.MinimalHud || MyHud.CutsceneHud
                || MyHudMarkerRender.SignalDisplayMode == MyHudMarkerRender.SignalMode.Off)
                return;
            IMyControllableEntity controlled = MySession.Static.ControlledEntity;
            Vector3D from = controlled?.Entity != null ? controlled.Entity.PositionComp.GetPosition() : fallback;
            foreach (MyEntityOreDeposit deposit in oreMarkers)
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
                    double squared = Vector3D.DistanceSquared(from, at);
                    if (!markers.TryGetValue(data.Material.Index, out Marker nearest) || squared < nearest.DistanceSquared)
                        markers[data.Material.Index] = new Marker { Material = data.Material, At = at, DistanceSquared = squared };
                }
            }
        }

        /// <summary>
        /// The ore marker the HUD is drawing that is nearest the ray, within <see cref="OreConeDegrees"/> and
        /// <paramref name="reach"/>, and within <paramref name="range"/> of <paramref name="centre"/>.
        /// </summary>
        private static bool TryOre(Vector3D origin, Vector3D direction, double reach, Vector3D centre, double range, out Target target)
        {
            target = default;
            HudOres(centre);
            double bestCos = Math.Cos(MathHelper.ToRadians(OreConeDegrees));
            bool found = false;
            foreach (Marker marker in markers.Values)
            {
                if (Vector3D.DistanceSquared(marker.At, centre) > range * range)
                    continue;
                Vector3D to = marker.At - origin;
                double length = to.Length();
                if (length < 1e-3 || length > reach)
                    continue;
                double cos = Vector3D.Dot(to, direction) / length;
                if (cos <= bestCos)
                    continue;
                bestCos = cos;
                string ore = OreName(marker.Material);
                target = Make(marker.At, ore, ore + " here", OreColor, "ore marker " + marker.Material.MinedOre);
                target.Distance = length;
                found = true;
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

        private static Target Spot(Vector3D point, string name, string what) => Make(point, name, name, PointColor, what);

        /// <summary>The ray met nothing within range: no point, no name; the caller makes no GPS and sends no chat line.</summary>
        private static Target Nothing() => new Target
        {
            Hit = false,
            Name = string.Empty,
            Line = "Nothing in range",
            Color = PointColor,
            What = "nothing in range",
            Distance = Range,
        };

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
