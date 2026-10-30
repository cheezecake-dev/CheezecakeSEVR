using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using HarmonyLib;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Rendering;
using SpaceEngineersVR.Tracking;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// The ship in your hand: a small hologram of a grid and the grids joined to it by rotors, pistons, hinges and connectors,
    /// held above the off hand, about <see cref="ModelSize"/> across whatever the ship's size, in their current pose.
    /// <list type="bullet">
    /// <item>Shown and dismissed from the actions wheel ("Ship model", <see cref="Gui.ShipModelEntries"/>). Seated, it is the
    /// ship you are in; on foot, the grid under the dominant hand's ray, else the nearest within <see cref="NearestReach"/>,
    /// else the one shown last. Only a grid that is yours or shared with your faction (<see cref="Permitted"/>): another's
    /// ship, a neutral or unowned one is not shown, however close, and one that stops being yours while shown is put away.</item>
    /// <item>It rides above the off hand, the ship's forward (the cockpit's) pointing where you look, until either hand grips
    /// it: then it turns with that hand, two hands scale it, and let go it stays where it was left.</item>
    /// <item>Damaged blocks are red, deeper the more damaged; blocks destroyed or removed while it is out are dark. The overlays
    /// (thrusters orange, power yellow, doors green, weapons outlined red) are switched on the wheel's Model overlays page.</item>
    /// <item>The dominant hand's fingertip in a block of the model, or its ray on one, lights that block on the model and
    /// outlines the real one in the world, with its name and integrity as a marker beside it.</item>
    /// </list>
    /// Built from the game's own grid data (each block's cells, integrity and kind), once when shown and again as blocks are
    /// added, removed or damaged, on a worker thread, and drawn as billboards (<see cref="ShipMesh"/> keeps it within a quad
    /// budget). Off with the setting (<see cref="VRSettings.ShipModel"/>) or RenderDebug ShipModel=0.
    /// </summary>
    internal static partial class ShipModel
    {
        /// <summary>Debug: no ship model (<see cref="RenderDebug"/> ShipModel=0).</summary>
        public static bool Off { get; set; }

        public static bool Enabled => !Off && VRSettings.ShipModel && guard.On;

        /// <summary>The model's longest side when it appears, metres.</summary>
        public const float ModelSize = 0.28f;

        /// <summary>On foot with nothing under the ray, the nearest grid this close is shown, metres.</summary>
        public const double NearestReach = 50.0;

        /// <summary>How far the dominant hand's ray looks for a grid to show, metres.</summary>
        private const double LaserReach = 200.0;

        /// <summary>The most quads drawn a frame; the hull is made coarser to fit (<see cref="ShipMesh.Build"/>).</summary>
        public const int QuadBudget = 3000;

        /// <summary>The soonest a changed ship is built again after the last build began, seconds.</summary>
        private const double RebuildSeconds = 0.25;

        /// <summary>Blocks whose damage is checked a frame, round the ship: damage reaches the model within a second or two on large ships.</summary>
        private const int ScanPerFrame = 600;

        /// <summary>How often the joined grids are looked at again for one added or taken off, frames.</summary>
        private const int GroupCheckFrames = 30;

        /// <summary>The model fades in and out over this long, seconds, rather than popping.</summary>
        private const float FadeSeconds = 0.2f;

        private static readonly FeatureGuard guard = new FeatureGuard(nameof(ShipModel));

        /// <summary>One grid of the model on the game thread: the entity, and its blocks as last built.</summary>
        private sealed class Part
        {
            public MyCubeGrid Grid;

            /// <summary>Blocks gone since the model was shown: kept dark where they were.</summary>
            public readonly List<ShipBlock> Gone = new List<ShipBlock>();
            public readonly List<string> GoneNames = new List<string>();

            /// <summary>As last built: the block behind each of the shape's blocks (null for a gone one), and the gone ones' names.</summary>
            public MySlimBlock[] Slims = Array.Empty<MySlimBlock>();
            public string[] Names = Array.Empty<string>();
            public ShipShape Shape;
        }

        /// <summary>A build on its way from the worker: the parts it was taken for, with what it made.</summary>
        private sealed class Build
        {
            public int Generation;
            public Part[] Parts;
            public MySlimBlock[][] Slims;
            public string[][] Names;
            public ShipMesh.Input[] Inputs;
            public bool[] Overlays;
            public ShipShape[] Shapes;
            public int Level, Quads, Blocks;
            public double Ms;
            public Exception Error;
        }

        // ---- What is shown (game thread) ----

        private static MyCubeGrid main;
        private static readonly List<Part> parts = new List<Part>();
        private static Matrix mainToModel = Matrix.Identity;

        /// <summary>Half the model's size along its own axes, metres of ship.</summary>
        private static Vector3 halfExtent = Vector3.One;
        private static float longest = 1f;

        private static bool dirty, hiding;
        private static float fade;
        private static long lastFrame, buildStarted;
        private static int frame, scanGrid, scanBlock;
        private static int generation;
        private static Build pending, done;
        private static long lastGridId;
        private static string shownName;
        private static readonly List<MyCubeGrid> groupNodes = new List<MyCubeGrid>();
        private static readonly List<MyEntity> nearby = new List<MyEntity>();
        private static readonly List<MyPhysics.HitInfo> hits = new List<MyPhysics.HitInfo>();

        /// <summary>The grids <see cref="Target"/> has turned down this time as not yours or your faction's, by entity id.</summary>
        private static readonly HashSet<long> refused = new HashSet<long>();

        /// <summary>The model is out (or fading out).</summary>
        public static bool Shown => main != null;

        /// <summary>The grid the model shows, for the wheel's state line.</summary>
        public static string ShownName => main != null && !hiding ? shownName : null;

        public static void Patch(Harmony harmony)
        {
            // Drawn with the game's other billboards, in the gameplay screen's draw, as the hand ray is.
            harmony.Patch(AccessTools.Method(typeof(MySession), nameof(MySession.DrawSync)),
                postfix: new HarmonyMethod(typeof(ShipModel), nameof(Draw)));
            Log.Info($"Ship model: hooked (about {ModelSize * 100:0} cm across, at most {QuadBudget} quads a frame)");
        }

        /// <summary>Shows the model, or dismisses it when it is out: the actions wheel's entry.</summary>
        public static void Toggle()
        {
            if (Shown && !hiding)
                Hide("dismissed");
            else
                Show();
        }

        /// <summary>Shows the model of the ship of yours in reach (see the class remarks); false with a notice when there is none.</summary>
        public static bool Show()
        {
            if (!Enabled)
                return false;
            try
            {
                MyCubeGrid grid = Target(out string how);
                if (grid == null)
                {
                    HudToolbar.Notice("No ship of yours in reach");
                    Log.Info($"Ship model: no grid of yours or your faction's to show (seated, under the ray, within {NearestReach:0} m, shown before: none; " +
                             $"{refused.Count} other grid(s) seen and refused)");
                    return false;
                }
                Clear();
                main = grid;
                lastGridId = grid.EntityId;
                shownName = grid.DisplayName;
                hiding = false;
                Join();
                Measure(Reference(grid));
                StartHolding();
                dirty = true;
                Log.Info($"Ship model: showing '{shownName}' ({how}; {parts.Count} grid(s), {CountBlocks()} blocks, {longest:0.0} m long, scale 1:{1f / scale:0})");
                HudToolbar.Notice(shownName);
                return true;
            }
            catch (Exception e)
            {
                Fail(e, "could not be shown");
                return false;
            }
        }

        /// <summary>Dismisses the model; it fades out.</summary>
        public static void Hide(string why)
        {
            if (!Shown || hiding)
                return;
            hiding = true;
            LetGoAll();
            Unpoint();
            Log.Info($"Ship model: hidden ({why})");
        }

        /// <summary>Game thread, once a frame after the hands are read (<see cref="InputPatches"/>).</summary>
        internal static void Frame()
        {
            RegisterClaim();
            if (!Shown)
                return;
            try
            {
                long now = Stopwatch.GetTimestamp();
                float dt = lastFrame == 0 ? 0f : (float)Math.Min((now - lastFrame) / (double)Stopwatch.Frequency, 0.1);
                lastFrame = now;
                frame++;

                if (!hiding && (!Enabled || MySession.Static == null || main.Closed || main.MarkedForClose))
                    Hide(main.Closed || main.MarkedForClose ? "the grid is gone" : "switched off");
                else if (!hiding && !Permitted(main))
                    Hide("the grid is no longer yours or your faction's");
                fade = MathHelper.Clamp(fade + (hiding ? -dt : dt) / FadeSeconds, 0f, 1f);
                if (hiding)
                {
                    if (fade <= 0f)
                        Clear();
                    return;
                }

                Vet();
                if (frame % GroupCheckFrames == 0)
                    Join();
                Take();
                Scan();
                if (dirty && pending == null && (now - buildStarted) / (double)Stopwatch.Frequency >= RebuildSeconds)
                    Rebuild(now);

                Hold(dt);
                Point();
            }
            catch (Exception e)
            {
                Fail(e, "failed");
            }
        }

        private static void Fail(Exception e, string what)
        {
            // Switched off, everything is let go; it comes back when the guard does, the next time it is asked for.
            if (guard.Failed(e, "Ship model " + what))
                Clear();
        }

        /// <summary>Everything let go and forgotten: the events unhooked, the marker taken down.</summary>
        private static void Clear()
        {
            LetGoAll();
            Unpoint();
            foreach (Part part in parts)
                Unhook(part.Grid);
            parts.Clear();
            main = null;
            hiding = false;
            fade = 0f;
            lastFrame = 0;
            pending = null;
            Interlocked.Exchange(ref done, null);
            generation++;
        }

        // ---- Which grid ----

        /// <summary>
        /// The grid to show: the one controlled, else under the ray, else the nearest, else the last; each only if it is yours or
        /// your faction's (<see cref="Showable"/>). Seated in a ship that is not, there is none: the ship you are in is the one asked for.
        /// </summary>
        private static MyCubeGrid Target(out string how)
        {
            refused.Clear();
            if (MySession.Static?.ControlledEntity is MyCubeBlock seat && seat.CubeGrid != null)
            {
                how = "seated in " + seat.DisplayNameText;
                return Showable(seat.CubeGrid);
            }
            if (HandWorld.TryGet(VRSettings.DominantHand, out _, out MatrixD aim) && HandTools.Ray(aim, out Vector3D origin, out Vector3D direction))
            {
                hits.Clear();
                MyPhysics.CastRay(origin, origin + direction * LaserReach, hits);
                foreach (MyPhysics.HitInfo hit in hits)
                {
                    if (Showable(((IHitInfo)hit).HitEntity?.GetTopMostParent() as MyCubeGrid) is MyCubeGrid under)
                    {
                        how = $"under the ray, {Vector3D.Distance(origin, hit.Position):0} m";
                        return Largest(under);
                    }
                }
            }
            Vector3D at = MySession.Static?.LocalCharacter?.PositionComp.GetPosition() ?? Vector3D.Zero;
            MyCubeGrid nearest = null;
            double best = NearestReach;
            var box = new BoundingBoxD(at - NearestReach, at + NearestReach);
            nearby.Clear();
            MyEntities.GetTopMostEntitiesInBox(ref box, nearby);
            foreach (MyEntity entity in nearby)
            {
                if (!(Showable(entity as MyCubeGrid) is MyCubeGrid grid))
                    continue;
                double distance = grid.PositionComp.WorldAABB.Distance(at);
                if (distance < best)
                {
                    best = distance;
                    nearest = grid;
                }
            }
            nearby.Clear();
            if (nearest != null)
            {
                how = $"nearest, {best:0} m";
                return Largest(nearest);
            }
            how = "shown last";
            return lastGridId != 0 && MyEntities.TryGetEntityById(lastGridId, out MyEntity last) ? Showable(last as MyCubeGrid) : null;
        }

        /// <summary>The grid if it can be shown (a real grid with blocks) and is yours or your faction's; else null, with the others noted in <see cref="refused"/>.</summary>
        private static MyCubeGrid Showable(MyCubeGrid grid)
        {
            if (grid == null || grid.Closed || grid.MarkedForClose || grid.Projector != null || grid.IsPreview || grid.BlocksCount == 0)
                return null;
            if (Permitted(grid))
                return grid;
            refused.Add(grid.EntityId);
            return null;
        }

        /// <summary>
        /// Whether the grid is yours or shared with your faction, the way the game's terminal reads it: the owners of the grid
        /// (<see cref="MyCubeGrid.BigOwners"/>, the players with the most working blocks; while none works, those with any block)
        /// must all be you, or in your faction with their blocks shared with it (<see cref="MyIDModule.GetRelationPlayerBlock"/>
        /// answering owner or faction share). Unowned, neutral, allied-faction and enemy grids are not, a block shared with
        /// everyone does not make its grid yours, and a grid captured by another player stops being. Nothing is shown of a
        /// grid the terminal would refuse you, nor more than it would show.
        /// </summary>
        private static bool Permitted(MyCubeGrid grid)
        {
            long me = MySession.Static?.LocalPlayerId ?? 0L;
            if (grid == null || me == 0L)
                return false;
            List<long> owners = grid.BigOwners;
            if (owners == null || owners.Count == 0)
                owners = grid.SmallOwners;
            if (owners == null || owners.Count == 0)
                return false;
            foreach (long owner in owners)
            {
                MyRelationsBetweenPlayerAndBlock relation = owner == me ? MyRelationsBetweenPlayerAndBlock.Owner : RelationTo(grid, owner, me);
                if (relation != MyRelationsBetweenPlayerAndBlock.Owner && relation != MyRelationsBetweenPlayerAndBlock.FactionShare)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// How <paramref name="me"/> stands to a block of <paramref name="owner"/> on the grid, as the faction sees it: the share
        /// mode set on that block counts when it is faction or nobody's, but a block shared with everyone (a public medical room,
        /// a door) is read as faction, so it is the owner's faction that decides. On a grid with none of the owner's own blocks
        /// (a rotor head takes its owners from the ship), the faction share mode is taken as set.
        /// </summary>
        private static MyRelationsBetweenPlayerAndBlock RelationTo(MyCubeGrid grid, long owner, long me)
        {
            foreach (MyCubeBlock block in grid.GetFatBlocks())
            {
                if (block.OwnerId != owner || block.IDModule == null)
                    continue;
                MyOwnershipShareModeEnum share = block.IDModule.ShareMode == MyOwnershipShareModeEnum.None
                    ? MyOwnershipShareModeEnum.None
                    : MyOwnershipShareModeEnum.Faction;
                return MyIDModule.GetRelationPlayerBlock(owner, me, share);
            }
            return MyIDModule.GetRelationPlayerBlock(owner, me, MyOwnershipShareModeEnum.Faction);
        }

        /// <summary>A grid on foot is shown from the largest of the grids joined to it (a ship, not the rotor head pointed at).</summary>
        private static MyCubeGrid Largest(MyCubeGrid grid)
        {
            groupNodes.Clear();
            MyCubeGridGroups.Static?.Logical.GetGroupNodes(grid, groupNodes);
            MyCubeGrid largest = grid;
            foreach (MyCubeGrid node in groupNodes)
                if (Showable(node) != null && node.BlocksCount > largest.BlocksCount)
                    largest = node;
            groupNodes.Clear();
            return largest;
        }

        /// <summary>
        /// The block whose facing is the model's forward: the seat controlled, else the grid's main cockpit, else any ship
        /// controller on it; null leaves the grid's own axes.
        /// </summary>
        private static MyCubeBlock Reference(MyCubeGrid grid)
        {
            if (MySession.Static?.ControlledEntity is MyShipController seat && seat.CubeGrid != null)
                return seat;
            if (grid.MainCockpit != null)
                return grid.MainCockpit;
            foreach (MyCubeBlock block in grid.GetFatBlocks())
                if (block is MyShipController controller && !(controller is MyCryoChamber))
                    return controller;
            return null;
        }

        // ---- The grids joined to it, and their events ----

        /// <summary>
        /// Takes in the grids joined to the main one (rotors, pistons, hinges, connectors; not landing gear), and lets go of any no
        /// longer joined. A joined grid that is not yours or your faction's, a ship docked at a connector, is left out.
        /// </summary>
        private static void Join()
        {
            groupNodes.Clear();
            MyCubeGridGroups.Static?.Logical.GetGroupNodes(main, groupNodes);
            if (!groupNodes.Contains(main))
                groupNodes.Add(main);
            bool changed = false;
            for (int i = parts.Count - 1; i >= 0; i--)
            {
                MyCubeGrid grid = parts[i].Grid;
                if (groupNodes.Contains(grid) && !grid.Closed)
                    continue;
                Unhook(grid);
                parts.RemoveAt(i);
                changed = true;
            }
            foreach (MyCubeGrid grid in groupNodes)
            {
                if (grid.Closed || grid.Projector != null || parts.Exists(p => p.Grid == grid) || !Permitted(grid))
                    continue;
                parts.Add(new Part { Grid = grid });
                grid.OnBlockAdded += BlockAdded;
                grid.OnBlockRemoved += BlockRemoved;
                grid.OnBlockIntegrityChanged += BlockChanged;
                changed = true;
            }
            groupNodes.Clear();
            if (changed)
                dirty = true;
        }

        /// <summary>Every frame: a grid of the model that is no longer yours or your faction's (sold, captured, the faction changed) is taken out of it.</summary>
        private static void Vet()
        {
            for (int i = parts.Count - 1; i >= 0; i--)
            {
                MyCubeGrid grid = parts[i].Grid;
                if (Permitted(grid))
                    continue;
                Log.Info($"Ship model: '{grid.DisplayName}' is no longer yours or your faction's, left out");
                Unhook(grid);
                parts.RemoveAt(i);
                dirty = true;
            }
        }

        private static void Unhook(MyCubeGrid grid)
        {
            grid.OnBlockAdded -= BlockAdded;
            grid.OnBlockRemoved -= BlockRemoved;
            grid.OnBlockIntegrityChanged -= BlockChanged;
        }

        private static void BlockAdded(MySlimBlock block) => dirty = true;

        private static void BlockChanged(MySlimBlock block) => dirty = true;

        /// <summary>A block destroyed, ground down or cut off: kept on the model, dark, where it was.</summary>
        private static void BlockRemoved(MySlimBlock block)
        {
            dirty = true;
            Part part = parts.Find(p => p.Grid == block.CubeGrid);
            if (part == null)
                return;
            part.Gone.Add(new ShipBlock { Min = block.Min, Max = block.Max, Part = PartOf(block), Gone = true });
            part.GoneNames.Add(NameOf(block));
        }

        private static int CountBlocks()
        {
            int count = 0;
            foreach (Part part in parts)
                count += part.Grid.BlocksCount;
            return count;
        }

        // ---- Building ----

        /// <summary>What a block is, for its overlay: anything that makes electricity is power (reactors, batteries, solar, wind, engines).</summary>
        private static ShipPart PartOf(MySlimBlock slim)
        {
            MyCubeBlock fat = slim.FatBlock;
            if (fat == null)
                return ShipPart.Hull;
            if (fat is MyThrust)
                return ShipPart.Thruster;
            if (fat is MyUserControllableGun)
                return ShipPart.Weapon;
            if (fat is MyDoorBase)
                return ShipPart.Door;
            if (fat.Components.TryGet(out MyResourceSourceComponent source))
                foreach (var type in source.ResourceTypes)
                    if (type == MyResourceDistributorComponent.ElectricityId)
                        return ShipPart.Power;
            return ShipPart.Hull;
        }

        /// <summary>The name the terminal shows: the block's own if it has one and the terminal is open to you, else its kind's.</summary>
        private static string NameOf(MySlimBlock slim) =>
            slim.FatBlock is MyTerminalBlock terminal && terminal.CustomName != null && terminal.CustomName.Length > 0 && terminal.HasLocalPlayerAccess()
                ? terminal.CustomName.ToString()
                : slim.BlockDefinition?.DisplayNameText ?? "Block";

        /// <summary>Reads the grids on the game thread and leaves the geometry to a worker; the result is taken up by <see cref="Take"/>.</summary>
        private static void Rebuild(long now)
        {
            dirty = false;
            buildStarted = now;
            var build = new Build
            {
                Generation = generation,
                Parts = parts.ToArray(),
                Inputs = new ShipMesh.Input[parts.Count],
                Slims = new MySlimBlock[parts.Count][],
                Names = new string[parts.Count][],
                Overlays = Overlays(),
            };
            for (int g = 0; g < build.Parts.Length; g++)
            {
                Part part = build.Parts[g];
                HashSet<MySlimBlock> live = part.Grid.GetBlocks();
                int count = live.Count + part.Gone.Count;
                var blocks = new ShipBlock[count];
                var slims = new MySlimBlock[count];
                var names = new string[count];
                int i = 0;
                foreach (MySlimBlock slim in live)
                {
                    blocks[i] = new ShipBlock { Min = slim.Min, Max = slim.Max, Part = PartOf(slim), Damage = ShipMesh.DamageStep(slim.Integrity, slim.MaxIntegrity) };
                    slims[i++] = slim;
                }
                for (int k = 0; k < part.Gone.Count; k++, i++)
                {
                    blocks[i] = part.Gone[k];
                    names[i] = part.GoneNames[k];
                }
                build.Inputs[g] = new ShipMesh.Input { GridId = part.Grid.EntityId, CellSize = part.Grid.GridSize, Blocks = blocks };
                build.Slims[g] = slims;
                build.Names[g] = names;
                build.Blocks += live.Count;
            }
            pending = build;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                long start = Stopwatch.GetTimestamp();
                try
                {
                    build.Shapes = ShipMesh.Build(build.Inputs, build.Overlays, QuadBudget, out build.Level, out build.Quads);
                }
                catch (Exception e)
                {
                    build.Error = e;
                }
                build.Ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                Interlocked.Exchange(ref done, build);
            });
        }

        /// <summary>Game thread: a finished build of what is shown now takes the place of the last.</summary>
        private static void Take()
        {
            Build build = Interlocked.Exchange(ref done, null);
            if (build == null)
                return;
            if (ReferenceEquals(build, pending))
                pending = null;
            if (build.Generation != generation)
                return;
            if (build.Error != null)
            {
                Fail(build.Error, "could not be built");
                return;
            }
            for (int g = 0; g < build.Parts.Length; g++)
            {
                Part part = build.Parts[g];
                part.Shape = build.Shapes[g];
                part.Slims = build.Slims[g];
                part.Names = build.Names[g];
            }
            int gone = 0;
            foreach (Part part in build.Parts)
                gone += part.Gone.Count;
            builds.Info($"Ship model: built '{shownName}': {build.Parts.Length} grid(s), {build.Blocks} blocks{(gone > 0 ? $" and {gone} gone" : string.Empty)}, " +
                        $"{build.Quads} quads at level {build.Level} ({1 << build.Level} cells to a side) in {build.Ms:0.0} ms on a worker");
            lastLevel = build.Level;
            lastQuads = build.Quads;
        }

        private static readonly Common.LineThrottle builds = new Common.LineThrottle(1);
        private static int lastLevel, lastQuads;

        /// <summary>A little of the ship's damage checked each frame, round the ship: a change since the last build builds it again.</summary>
        private static void Scan()
        {
            if (dirty || parts.Count == 0)
                return;
            for (int n = 0; n < ScanPerFrame; n++)
            {
                if (scanGrid >= parts.Count)
                    scanGrid = 0;
                Part part = parts[scanGrid];
                if (part.Shape == null || scanBlock >= part.Slims.Length)
                {
                    scanBlock = 0;
                    scanGrid++;
                    if (part.Shape == null || part.Slims.Length == 0)
                        return;
                    continue;
                }
                MySlimBlock slim = part.Slims[scanBlock];
                if (slim != null && ShipMesh.DamageStep(slim.Integrity, slim.MaxIntegrity) != part.Shape.Blocks[scanBlock].Damage)
                {
                    dirty = true;
                    return;
                }
                scanBlock++;
            }
        }

        // ---- Where the model is ----

        /// <summary>
        /// The model's own space from the main grid's: centred on the ship, the reference block's forward along -Z and its up
        /// along +Y, and the size it is drawn at to start with.
        /// </summary>
        private static void Measure(MyCubeBlock reference)
        {
            Matrix rotation = Matrix.Identity;
            if (reference != null && !reference.Closed)
            {
                MatrixD inMain = reference.WorldMatrix * main.PositionComp.WorldMatrixNormalizedInv;
                rotation = Matrix.CreateWorld(Vector3.Zero, Vector3.Normalize((Vector3)inMain.Forward), Vector3.Normalize((Vector3)inMain.Up));
            }
            Matrix toModel = Matrix.Transpose(rotation);
            BoundingBox box = BoundingBox.CreateInvalid();
            foreach (Part part in parts)
            {
                Matrix gridToMain = (Matrix)(part.Grid.WorldMatrix * main.PositionComp.WorldMatrixNormalizedInv);
                BoundingBox local = (BoundingBox)part.Grid.PositionComp.LocalAABB;
                box = box.Include(local.Transform(gridToMain * toModel));
            }
            if (!(box.Min.X <= box.Max.X))
                box = new BoundingBox(-Vector3.One, Vector3.One);
            Vector3 centre = box.Center;
            halfExtent = Vector3.Max(box.HalfExtents, new Vector3(0.05f));
            longest = 2f * Math.Max(halfExtent.X, Math.Max(halfExtent.Y, halfExtent.Z));
            mainToModel = toModel * Matrix.CreateTranslation(-centre);
            scale = ModelSize / longest;
        }

        /// <summary>A grid's own space (metres) to the model's (metres of ship), in the grids' pose now.</summary>
        private static Matrix GridToModel(MyCubeGrid grid) =>
            ReferenceEquals(grid, main) ? mainToModel : (Matrix)(grid.WorldMatrix * main.PositionComp.WorldMatrixNormalizedInv) * mainToModel;

        /// <summary>The model's space to the tracking space: its size, then where it is held.</summary>
        private static Matrix ModelToTracking => Matrix.CreateScale(scale) * pose;

        /// <summary>The overlays switched on, by <see cref="ShipPart"/>.</summary>
        private static bool[] Overlays() => new[]
        {
            false, VRSettings.ShipModelThrusters, VRSettings.ShipModelPower, VRSettings.ShipModelDoors, VRSettings.ShipModelWeapons,
        };

        /// <summary>An overlay was switched: the model is built again with it.</summary>
        internal static void OverlaysChanged()
        {
            if (Shown)
                dirty = true;
        }
    }
}
