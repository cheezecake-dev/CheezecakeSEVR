using System;
using System.Collections.Generic;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>What a block is on the ship model, for its overlay.</summary>
    internal enum ShipPart : byte
    {
        Hull,
        Thruster,
        Power,
        Door,
        Weapon,
    }

    /// <summary>One block of a grid as the ship model sees it: the cells it fills, what it is, and how damaged.</summary>
    internal struct ShipBlock
    {
        /// <summary>The block's first and last cell on its grid (MySlimBlock.Min and Max).</summary>
        public Vector3I Min, Max;

        public ShipPart Part;

        /// <summary>0 whole, 1 to <see cref="ShipMesh.DamageSteps"/> more and more damaged (<see cref="ShipMesh.DamageStep"/>).</summary>
        public byte Damage;

        /// <summary>The block was there when the model was shown and has since been destroyed or taken off.</summary>
        public bool Gone;
    }

    /// <summary>A quad of the model, in its grid's own space (metres), and how it is coloured (<see cref="ShipMesh"/>'s looks).</summary>
    internal struct ShipFace
    {
        public Vector3 P0, P1, P2, P3;
        public byte Look;
    }

    /// <summary>One grid's model: its quads, the boxes outlined for its weapons, and which block fills each cell.</summary>
    internal sealed class ShipShape
    {
        public long GridId;
        public float CellSize;
        public ShipBlock[] Blocks;

        /// <summary>The block in each cell (an index into <see cref="Blocks"/>), at full detail whatever <see cref="Level"/> the hull was drawn at.</summary>
        public Dictionary<Vector3I, int> Cells;

        public ShipFace[] Hull;
        public ShipFace[] Overlay;

        /// <summary>The weapons' boxes, grid metres, outlined when the weapons overlay is on.</summary>
        public BoundingBox[] Weapons;

        /// <summary>The hull's cells merged 2^Level to a side, so a large ship stays within the quad budget.</summary>
        public int Level;

        /// <summary>Every block's box together, grid metres.</summary>
        public BoundingBox Bounds;
    }

    /// <summary>
    /// The ship model's geometry, apart from the game so it can be checked without one: each block as the box of its cells,
    /// only the faces nothing covers, merged into the largest rectangles of one look (greedy meshing), and the hull made
    /// coarser (2, 4, 8 cells to a side) until it fits the quad budget, so a 20,000-block ship costs what a small one does.
    /// Overlays (thrusters, power, doors) are their blocks' own boxes, a little proud of the hull, so they show through it.
    /// </summary>
    internal static class ShipMesh
    {
        public const int DamageSteps = 4;

        // Looks: 0 whole hull, 1-4 damaged, Gone, then one per overlay part.
        public const byte LookHull = 0, LookGone = DamageSteps + 1, LookOverlay = DamageSteps + 2;

        public static byte LookOf(ShipPart part) => (byte)(LookOverlay + (int)part);

        public const int LookCount = LookOverlay + 5;

        /// <summary>The coarsest the hull is made: 32 cells to a side.</summary>
        public const int MaxLevel = 5;

        /// <summary>How far an overlay's box stands out of the hull, in cells: enough not to share a plane with it.</summary>
        private const float OverlayProud = 0.04f;

        /// <summary>A block's damage step from its integrity: 0 for whole, then 1 (a scratch) to <see cref="DamageSteps"/> (nearly gone).</summary>
        public static byte DamageStep(float integrity, float maxIntegrity)
        {
            if (!(maxIntegrity > 0f))
                return 0;
            float ratio = integrity / maxIntegrity;
            if (ratio >= 0.999f)
                return 0;
            return (byte)MathHelper.Clamp(1 + (int)((1f - ratio) * DamageSteps), 1, DamageSteps);
        }

        /// <summary>A grid's blocks, as read from the game, for <see cref="Build"/>.</summary>
        internal sealed class Input
        {
            public long GridId;
            public float CellSize;
            public ShipBlock[] Blocks;
        }

        /// <summary>
        /// The grids' shapes, the hull at the finest level whose quads (over all the grids) fit
        /// <paramref name="budget"/> less the overlays'. <paramref name="overlays"/> is indexed by <see cref="ShipPart"/>.
        /// </summary>
        public static ShipShape[] Build(Input[] grids, bool[] overlays, int budget, out int level, out int quads)
        {
            var shapes = new ShipShape[grids.Length];
            int overlayQuads = 0;
            for (int g = 0; g < grids.Length; g++)
            {
                Input input = grids[g];
                var shape = new ShipShape
                {
                    GridId = input.GridId,
                    CellSize = input.CellSize,
                    Blocks = input.Blocks,
                    Cells = new Dictionary<Vector3I, int>(input.Blocks.Length * 2, Vector3I.Comparer),
                };
                var weapons = new List<BoundingBox>();
                BoundingBox bounds = BoundingBox.CreateInvalid();
                for (int i = 0; i < input.Blocks.Length; i++)
                {
                    ShipBlock b = input.Blocks[i];
                    BoundingBox box = BoxOf(b, input.CellSize);
                    bounds = bounds.Include(ref box);
                    if (b.Part == ShipPart.Weapon && !b.Gone && Overlay(overlays, ShipPart.Weapon))
                        weapons.Add(box);
                    for (int x = b.Min.X; x <= b.Max.X; x++)
                        for (int y = b.Min.Y; y <= b.Max.Y; y++)
                            for (int z = b.Min.Z; z <= b.Max.Z; z++)
                            {
                                var cell = new Vector3I(x, y, z);
                                // A live block wins its cells over one that was there before.
                                if (!shape.Cells.TryGetValue(cell, out int had) || (input.Blocks[had].Gone && !b.Gone))
                                    shape.Cells[cell] = i;
                            }
                }
                shape.Bounds = bounds;
                shape.Weapons = weapons.ToArray();
                shape.Overlay = OverlayFaces(shape, overlays);
                overlayQuads += shape.Overlay.Length;
                shapes[g] = shape;
            }

            int hullBudget = Math.Max(budget - overlayQuads, budget / 2);
            var faces = new List<ShipFace>[grids.Length];
            for (level = 0; ; level++)
            {
                quads = 0;
                for (int g = 0; g < shapes.Length; g++)
                {
                    faces[g] = HullFaces(shapes[g], level);
                    quads += faces[g].Count;
                }
                if (quads <= hullBudget || level >= MaxLevel)
                    break;
            }
            for (int g = 0; g < shapes.Length; g++)
            {
                shapes[g].Hull = faces[g].ToArray();
                shapes[g].Level = level;
            }
            quads += overlayQuads;
            return shapes;
        }

        private static bool Overlay(bool[] overlays, ShipPart part) => overlays != null && (int)part < overlays.Length && overlays[(int)part];

        /// <summary>A block's box in its grid's space, metres: its cells' centres are whole multiples of the cell size.</summary>
        public static BoundingBox BoxOf(in ShipBlock b, float cellSize) =>
            new BoundingBox(((Vector3)b.Min - 0.5f) * cellSize, ((Vector3)b.Max + 0.5f) * cellSize);

        /// <summary>The rank of a look when cells are merged into a coarser one: damage shows over whole hull, which shows over what is gone.</summary>
        private static int Rank(byte look) => look == LookGone ? 0 : look == LookHull ? 1 : 1 + look;

        private static byte HullLook(in ShipBlock b) => b.Gone ? LookGone : b.Damage;

        /// <summary>The hull with cells merged 2^level to a side: a coarse cell is there when any of its cells is, and takes the worst look among them.</summary>
        private static List<ShipFace> HullFaces(ShipShape shape, int level)
        {
            var voxels = new Dictionary<Vector3I, byte>(level == 0 ? shape.Cells.Count : shape.Cells.Count / 2 + 1, Vector3I.Comparer);
            foreach (KeyValuePair<Vector3I, int> cell in shape.Cells)
            {
                byte look = HullLook(shape.Blocks[cell.Value]);
                Vector3I v = level == 0 ? cell.Key : new Vector3I(cell.Key.X >> level, cell.Key.Y >> level, cell.Key.Z >> level);
                if (!voxels.TryGetValue(v, out byte had) || Rank(look) > Rank(had))
                    voxels[v] = look;
            }
            var faces = new List<ShipFace>();
            Surface(voxels, 1 << level, shape.CellSize, 0f, faces);
            return faces;
        }

        /// <summary>Each overlay that is on: its blocks' cells, faces only where a cell of the same overlay does not cover them.</summary>
        private static ShipFace[] OverlayFaces(ShipShape shape, bool[] overlays)
        {
            var faces = new List<ShipFace>();
            var voxels = new Dictionary<Vector3I, byte>(Vector3I.Comparer);
            for (ShipPart part = ShipPart.Thruster; part <= ShipPart.Door; part++)
            {
                if (!Overlay(overlays, part))
                    continue;
                voxels.Clear();
                byte look = LookOf(part);
                foreach (KeyValuePair<Vector3I, int> cell in shape.Cells)
                {
                    ShipBlock b = shape.Blocks[cell.Value];
                    if (b.Part == part && !b.Gone)
                        voxels[cell.Key] = look;
                }
                Surface(voxels, 1, shape.CellSize, OverlayProud, faces);
            }
            return faces.ToArray();
        }

        // ---- The surface of a set of cells, merged ----

        private struct SliceCell
        {
            public int U, W;
            public byte Look;
        }

        private static readonly Vector3I[] Directions =
        {
            new Vector3I(-1, 0, 0), new Vector3I(1, 0, 0),
            new Vector3I(0, -1, 0), new Vector3I(0, 1, 0),
            new Vector3I(0, 0, -1), new Vector3I(0, 0, 1),
        };

        /// <summary>
        /// The faces of <paramref name="voxels"/> (cells of <paramref name="size"/> grid cells to a side) that no other voxel covers,
        /// merged per plane into rectangles of one look, pushed out by <paramref name="proud"/> cells.
        /// </summary>
        private static void Surface(Dictionary<Vector3I, byte> voxels, int size, float cellSize, float proud, List<ShipFace> faces)
        {
            // One slice per plane: axis, side, and the voxel layer it bounds.
            var slices = new Dictionary<long, List<SliceCell>>();
            foreach (KeyValuePair<Vector3I, byte> voxel in voxels)
            {
                Vector3I v = voxel.Key;
                for (int d = 0; d < 6; d++)
                {
                    if (voxels.ContainsKey(v + Directions[d]))
                        continue;
                    int axis = d >> 1;
                    Axes(axis, out int ua, out int wa);
                    long key = ((long)Get(v, axis) << 3) | (long)d;
                    if (!slices.TryGetValue(key, out List<SliceCell> slice))
                        slices[key] = slice = new List<SliceCell>();
                    slice.Add(new SliceCell { U = Get(v, ua), W = Get(v, wa), Look = voxel.Value });
                }
            }

            var at = new Dictionary<long, int>();
            foreach (KeyValuePair<long, List<SliceCell>> pair in slices)
            {
                int d = (int)(pair.Key & 7);
                int layer = (int)(pair.Key >> 3);
                int axis = d >> 1;
                bool positive = (d & 1) != 0;
                List<SliceCell> slice = pair.Value;
                slice.Sort((a, b) => a.W != b.W ? a.W.CompareTo(b.W) : a.U.CompareTo(b.U));
                at.Clear();
                for (int i = 0; i < slice.Count; i++)
                    at[Pack(slice[i].U, slice[i].W)] = i;
                var used = new bool[slice.Count];

                float plane = (positive ? (layer + 1) * size : layer * size) - 0.5f + (positive ? proud : -proud);
                for (int i = 0; i < slice.Count; i++)
                {
                    if (used[i])
                        continue;
                    SliceCell first = slice[i];
                    int u1 = first.U + 1;
                    while (Free(at, used, slice, u1, first.W, first.Look))
                        u1++;
                    int w1 = first.W + 1;
                    while (Row(at, used, slice, first.U, u1, w1, first.Look))
                        w1++;
                    for (int w = first.W; w < w1; w++)
                        for (int u = first.U; u < u1; u++)
                            used[at[Pack(u, w)]] = true;
                    faces.Add(Face(axis, plane, first.U * size - 0.5f - proud, u1 * size - 0.5f + proud,
                        first.W * size - 0.5f - proud, w1 * size - 0.5f + proud, cellSize, first.Look));
                }
            }
        }

        private static bool Free(Dictionary<long, int> at, bool[] used, List<SliceCell> slice, int u, int w, byte look) =>
            at.TryGetValue(Pack(u, w), out int i) && !used[i] && slice[i].Look == look;

        private static bool Row(Dictionary<long, int> at, bool[] used, List<SliceCell> slice, int u0, int u1, int w, byte look)
        {
            for (int u = u0; u < u1; u++)
                if (!Free(at, used, slice, u, w, look))
                    return false;
            return true;
        }

        private static long Pack(int u, int w) => ((long)u << 32) | (uint)w;

        /// <summary>The two axes across a face of <paramref name="axis"/>, in turn after it (x: y, z; y: z, x; z: x, y).</summary>
        private static void Axes(int axis, out int ua, out int wa)
        {
            ua = (axis + 1) % 3;
            wa = (axis + 2) % 3;
        }

        private static int Get(in Vector3I v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

        private static Vector3 Point(int axis, float a, int ua, float u, int wa, float w)
        {
            var p = new Vector3();
            p.SetDim(axis, a);
            p.SetDim(ua, u);
            p.SetDim(wa, w);
            return p;
        }

        private static ShipFace Face(int axis, float plane, float u0, float u1, float w0, float w1, float cellSize, byte look)
        {
            Axes(axis, out int ua, out int wa);
            return new ShipFace
            {
                P0 = Point(axis, plane, ua, u0, wa, w0) * cellSize,
                P1 = Point(axis, plane, ua, u1, wa, w0) * cellSize,
                P2 = Point(axis, plane, ua, u1, wa, w1) * cellSize,
                P3 = Point(axis, plane, ua, u0, wa, w1) * cellSize,
                Look = look,
            };
        }

        // ---- Pointing into the model ----

        /// <summary>The block in the cell holding <paramref name="point"/> (grid metres), or -1.</summary>
        public static int BlockAt(ShipShape shape, Vector3 point)
        {
            Vector3 q = point / shape.CellSize;
            var cell = new Vector3I((int)Math.Round(q.X), (int)Math.Round(q.Y), (int)Math.Round(q.Z));
            return shape.Cells.TryGetValue(cell, out int i) ? i : -1;
        }

        /// <summary>
        /// The first block along a ray (grid metres; <paramref name="direction"/> a unit vector) within <paramref name="reach"/>
        /// metres, walking the cells it passes through in order; -1 for none. <paramref name="distance"/> is where it enters that block's cell.
        /// </summary>
        public static int BlockAlong(ShipShape shape, Vector3 origin, Vector3 direction, float reach, out float distance)
        {
            distance = 0f;
            if (shape.Cells.Count == 0 || !(shape.CellSize > 0f))
                return -1;
            // In cell units, shifted so cell i spans [i, i + 1).
            float size = shape.CellSize;
            Vector3 o = origin / size + 0.5f;
            Vector3 lo = shape.Bounds.Min / size + 0.5f, hi = shape.Bounds.Max / size + 0.5f;
            if (!Clip(o, direction, lo, hi, reach / size, out float enter, out float leave))
                return -1;
            Vector3 p = o + direction * (enter + 1e-4f);
            var cell = new Vector3I((int)Math.Floor(p.X), (int)Math.Floor(p.Y), (int)Math.Floor(p.Z));
            var step = new Vector3I(Math.Sign(direction.X), Math.Sign(direction.Y), Math.Sign(direction.Z));
            Vector3 next = new Vector3(Boundary(p.X, direction.X, cell.X), Boundary(p.Y, direction.Y, cell.Y), Boundary(p.Z, direction.Z, cell.Z));
            Vector3 delta = new Vector3(Delta(direction.X), Delta(direction.Y), Delta(direction.Z));
            float t = enter;
            next += new Vector3(enter);
            int steps = (int)(Math.Abs(hi.X - lo.X) + Math.Abs(hi.Y - lo.Y) + Math.Abs(hi.Z - lo.Z)) + 3;
            for (int s = 0; s < steps && t <= leave; s++)
            {
                if (shape.Cells.TryGetValue(cell, out int i))
                {
                    distance = t * size;
                    return i;
                }
                if (next.X <= next.Y && next.X <= next.Z)
                {
                    t = next.X;
                    next.X += delta.X;
                    cell.X += step.X;
                }
                else if (next.Y <= next.Z)
                {
                    t = next.Y;
                    next.Y += delta.Y;
                    cell.Y += step.Y;
                }
                else
                {
                    t = next.Z;
                    next.Z += delta.Z;
                    cell.Z += step.Z;
                }
            }
            return -1;
        }

        /// <summary>How far along the ray, from the point it is at, the next cell boundary on one axis is.</summary>
        private static float Boundary(float at, float direction, int cell)
        {
            if (direction > 0f)
                return (cell + 1 - at) / direction;
            if (direction < 0f)
                return (cell - at) / direction;
            return float.PositiveInfinity;
        }

        private static float Delta(float direction) => direction == 0f ? float.PositiveInfinity : Math.Abs(1f / direction);

        /// <summary>Where a ray is inside a box (slab test), from 0 to <paramref name="reach"/>.</summary>
        private static bool Clip(Vector3 o, Vector3 d, Vector3 lo, Vector3 hi, float reach, out float enter, out float leave)
        {
            enter = 0f;
            leave = reach;
            for (int a = 0; a < 3; a++)
            {
                float oa = o.GetDim(a), da = d.GetDim(a), la = lo.GetDim(a), ha = hi.GetDim(a);
                if (Math.Abs(da) < 1e-9f)
                {
                    if (oa < la || oa > ha)
                        return false;
                    continue;
                }
                float t0 = (la - oa) / da, t1 = (ha - oa) / da;
                if (t0 > t1)
                {
                    float swap = t0;
                    t0 = t1;
                    t1 = swap;
                }
                enter = Math.Max(enter, t0);
                leave = Math.Min(leave, t1);
                if (enter > leave)
                    return false;
            }
            return true;
        }
    }
}
