using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using SpaceEngineersVR.Input;
using VRage.Game.Entity;
using VRage.Game.Models;
using VRageMath;
using VRageRender.Import;
using VRageRender.Models;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// The glass of windows and cockpit canopies as something the drawn hand stops on (<see cref="SurfaceStop"/>). Glass is
    /// often not in a block's collision: a sloped or round window's pane stands up to 17 cm outside it and a seat cockpit's
    /// canopy up to 10 cm, and a cockpit's interior model (what its pilot sees in first person) has no collision at all,
    /// while the seat's own block is passed so the seated body fits in it. So the glass is read from the model the block
    /// shows: the triangles whose material draws as glass (MyMeshDrawTechnique.GLASS, the test the game's projectiles make
    /// on a block's model), loaded once per model on a worker thread at full detail, as the screens are (<see cref="ScreenGlass"/>).
    ///
    /// Left out: a connected piece of glass under 400 cm² (a gauge's cover, a lamp, the small glass on a console) and glass
    /// on a screen (an LCD's cover, a cockpit screen's), so every button and screen is still in reach. A pane stops a ball
    /// coming at it from the side the eyes are on; a ball already on its far side passes, so a hand is never held behind glass.
    /// Blocks and their moving parts (a door's leaf) of real grids are looked at; projections and paste previews are not.
    /// </summary>
    internal static class GlassPanes
    {
        /// <summary>A connected piece of glass smaller than this (m²) is left out.</summary>
        private const float MinPane = 0.04f;
        /// <summary>A piece with a screen's triangle this close (m) is the screen's cover, left out.</summary>
        private const float OnScreen = 0.01f;
        /// <summary>Corners closer than 1/this (m, 2 mm) are one corner when the pieces are found.</summary>
        private const float Weld = 500f;
        /// <summary>A ball already this close to its contact depth is not caught again by the same plane (the corner sweep).</summary>
        private const float Skin = 0.0001f;
        /// <summary>Blocks are looked for this far round a sweep, so the other ball of the hand finds them already gathered.</summary>
        private const double GatherMargin = 0.5;

        private struct Tri
        {
            public Vector3 A, B, C, Normal;
        }

        private sealed class Pane
        {
            public Tri[] Tris;
            public BoundingBox Box;
        }

        private sealed class ModelPanes
        {
            public volatile int State;   // 0 loading, 1 ready, 2 failed
            public Pane[] Panes;
            public string Report, Error;
            public bool Logged;
        }

        private static readonly Dictionary<string, ModelPanes> models = new Dictionary<string, ModelPanes>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The glass of one block (or one moving part of a block): what the hand is on, one surface for all its panes.</summary>
        internal sealed class Glass
        {
            public readonly MyEntity Entity;
            public readonly string Asset;
            private readonly string name;

            public Glass(MyEntity entity, string asset, string name)
            {
                Entity = entity;
                Asset = asset;
                this.name = name;
            }

            public override bool Equals(object obj) => obj is Glass other && other.Entity == Entity && other.Asset == Asset;
            public override int GetHashCode() => Entity.GetHashCode();
            public override string ToString() => name;
        }

        private sealed class Known
        {
            public MyEntity Entity;
            public string Asset;
            public ModelPanes Model;
            public Glass Glass;
        }

        private static readonly Dictionary<MyEntity, Known> known = new Dictionary<MyEntity, Known>();

        private sealed class Gathered
        {
            public ulong Frame = ulong.MaxValue;
            public BoundingSphereD Sphere;
            public readonly List<Known> Glass = new List<Known>();
        }

        private static readonly Gathered[] gathered = { new Gathered(), new Gathered() };
        private static readonly List<MyEntity> found = new List<MyEntity>();
        private static readonly List<string> screenList = new List<string>();

        private static long tested;

        /// <summary>For the Perf line: the glass triangles tested since the last call.</summary>
        internal static long TakeTested() => Interlocked.Exchange(ref tested, 0);

        /// <summary>The first glass a ball going from <c>from</c> to <c>to</c> meets (<see cref="Sweep"/>).</summary>
        internal struct Contact
        {
            public float Fraction;
            public Vector3D Centre, Point, Normal;
            public MyEntity Owner;
            public Glass What;
            /// <summary>Set only with <see cref="SurfaceStop.Trace"/>: a pane the ball went back through from its far side (passed).</summary>
            public Glass Behind;
        }

        /// <summary>
        /// The first pane the ball meets before <paramref name="before"/> (a fraction of the way, what the hulls gave), coming
        /// from the side <paramref name="eye"/> is on. Game thread.
        /// </summary>
        internal static bool Sweep(Hand hand, ulong frame, Vector3D from, Vector3D to, float radius, Vector3D eye, float before, out Contact contact)
        {
            contact = default;
            List<Known> near = Gather(hand, frame, from, to, radius);
            if (near.Count == 0)
                return false;
            var m = new Meeting { Fraction = before, Trace = SurfaceStop.Trace };
            Known bestOf = null;
            Vector3 bestPoint = default, bestNormal = default;
            foreach (Known k in near)
            {
                if (k.Entity.MarkedForClose)
                    continue;
                MatrixD toLocal = k.Entity.PositionComp.WorldMatrixNormalizedInv;
                m.Found = m.Behind = false;
                Meet(k.Model.Panes, (Vector3)Vector3D.Transform(from, toLocal), (Vector3)Vector3D.Transform(to, toLocal),
                    (Vector3)Vector3D.Transform(eye, toLocal), radius, ref m);
                if (m.Behind && contact.Behind == null)
                    contact.Behind = k.Glass;
                if (m.Found)
                {
                    bestOf = k;
                    bestPoint = m.Point;
                    bestNormal = m.Normal;
                }
            }
            Interlocked.Add(ref tested, m.Tested);
            if (bestOf == null)
                return false;
            MatrixD toWorld = bestOf.Entity.PositionComp.WorldMatrixRef;
            contact.Fraction = m.Fraction;
            contact.Centre = from + (to - from) * m.Fraction;
            contact.Point = Vector3D.Transform((Vector3D)bestPoint, toWorld);
            contact.Normal = Vector3D.Normalize(Vector3D.TransformNormal((Vector3D)bestNormal, toWorld));
            contact.Owner = bestOf.Entity.GetTopMostParent();
            contact.What = bestOf.Glass;
            return true;
        }

        /// <summary>The first pane a ball meets, carried from model to model; the point and normal are in that model's space.</summary>
        private struct Meeting
        {
            /// <summary>Of the way from a to b: in, only a pane met sooner counts; out, the first one met.</summary>
            public float Fraction;
            public Vector3 Point, Normal;
            public bool Found, Trace, Behind;
            public long Tested;
        }

        private static void Meet(Pane[] panes, Vector3 a, Vector3 b, Vector3 eye, float radius, ref Meeting m)
        {
            Vector3 way = b - a;
            var reach = new BoundingBox(Vector3.Min(a, b) - new Vector3(radius), Vector3.Max(a, b) + new Vector3(radius));
            foreach (Pane pane in panes)
            {
                if (!pane.Box.Intersects(reach))
                    continue;
                m.Tested += pane.Tris.Length;
                foreach (Tri tri in pane.Tris)
                {
                    // The front is the side the eyes are on: the outside of a window to someone outside, the inside of a
                    // canopy to its pilot.
                    Vector3 n = Vector3.Dot(eye - tri.A, tri.Normal) >= 0f ? tri.Normal : -tri.Normal;
                    float s0 = Vector3.Dot(a - tri.A, n), s1 = Vector3.Dot(b - tri.A, n);
                    if (s0 <= 0f)
                    {
                        // Started on the far side: passed, whichever way it goes.
                        if (m.Trace && s1 > 0f && !m.Behind)
                        {
                            Vector3 cross = a + way * (s0 / (s0 - s1));
                            m.Behind = Vector3.DistanceSquared(cross, ScreenGlass.Closest(cross, tri.A, tri.B, tri.C)) <= radius * radius;
                        }
                        continue;
                    }
                    if (s1 >= radius - Skin || s1 >= s0)
                        continue;
                    float t = s0 <= radius ? 0f : (s0 - radius) / (s0 - s1);
                    if (t >= m.Fraction)
                        continue;
                    // Where the ball meets the plane: on the pane if its foot is within a radius of the triangle.
                    Vector3 centre = a + way * t;
                    Vector3 foot = centre - n * Vector3.Dot(centre - tri.A, n);
                    Vector3 on = ScreenGlass.Closest(foot, tri.A, tri.B, tri.C);
                    if (Vector3.DistanceSquared(foot, on) > radius * radius)
                        continue;
                    m.Fraction = t;
                    m.Point = on;
                    m.Normal = n;
                    m.Found = true;
                }
            }
        }

        /// <summary>The blocks with glass round a sweep, gathered once per hand per frame (and again only if a sweep leaves the sphere).</summary>
        private static List<Known> Gather(Hand hand, ulong frame, Vector3D from, Vector3D to, float radius)
        {
            Gathered g = gathered[hand == Hand.Left ? 0 : 1];
            var need = new BoundingSphereD((from + to) * 0.5, Vector3D.Distance(from, to) * 0.5 + radius);
            if (g.Frame == frame && g.Sphere.Contains(need) == ContainmentType.Contains)
                return g.Glass;
            g.Frame = frame;
            g.Sphere = new BoundingSphereD(need.Center, need.Radius + GatherMargin);
            g.Glass.Clear();
            found.Clear();
            MyGamePruningStructure.GetAllEntitiesInSphere(ref g.Sphere, found);
            foreach (MyEntity entity in found)
            {
                Known k = KnownOf(entity);
                if (k != null)
                    g.Glass.Add(k);
            }
            found.Clear();
            return g.Glass;
        }

        /// <summary>A block's or a block part's glass, if it has some ready; its model is sent to load the first time it is seen.</summary>
        private static Known KnownOf(MyEntity entity)
        {
            // Only blocks and their parts: a pilot is a child of the cockpit too, and a helmet's visor is glass.
            MyCubeBlock block = entity as MyCubeBlock;
            if (block == null && !(entity is MyEntitySubpart))
                return null;
            if (entity.MarkedForClose || entity.PositionComp == null)
                return null;
            if (!(entity.GetTopMostParent() is MyCubeGrid grid) || grid.Projector != null || grid.IsPreview || grid.Physics == null)
                return null;
            string asset = block != null ? ScreenGlass.ShownModel(block) : entity.Model?.AssetName;
            if (string.IsNullOrEmpty(asset))
                return null;
            if (!known.TryGetValue(entity, out Known k) || k.Asset != asset)
            {
                if (known.Count > 256)
                    known.Clear();
                float scale = entity.Model?.ScaleFactor ?? 1f;
                screenList.Clear();
                List<MyTextPanelComponent> panels = block != null ? ScreenGlass.Surfaces(block) : null;
                if (panels != null)
                    foreach (MyTextPanelComponent panel in panels)
                        if (!string.IsNullOrEmpty(panel?.Name))
                            screenList.Add(panel.Name);
                screenList.Sort(StringComparer.OrdinalIgnoreCase);
                string key = asset + "|" + scale.ToString("R") + "|" + string.Join(",", screenList);
                if (!models.TryGetValue(key, out ModelPanes model))
                {
                    model = new ModelPanes();
                    models[key] = model;
                    var screens = new HashSet<string>(screenList, StringComparer.OrdinalIgnoreCase);
                    ThreadPool.QueueUserWorkItem(_ => Load(asset, scale, screens, model));
                }
                k = new Known { Entity = entity, Asset = asset, Model = model, Glass = new Glass(entity, asset, "the glass of " + Name(entity, block)) };
                known[entity] = k;
            }
            ModelPanes m = k.Model;
            if (m.State == 0)
                return null;
            if (!m.Logged)
            {
                m.Logged = true;
                if (m.State == 2)
                    Log.Info($"Surface stop: the glass of {asset} could not be read: {m.Error}");
                else if (m.Report != null)
                    Log.Info($"Surface stop: glass of {Path.GetFileName(asset)}: {m.Report}");
            }
            return m.State == 1 && m.Panes.Length > 0 ? k : null;
        }

        private static string Name(MyEntity entity, MyCubeBlock block)
        {
            if (block != null)
                return SurfaceStop.Describe(block);
            for (MyEntity e = entity.Parent; e != null; e = e.Parent)
                if (e is MyCubeBlock owner)
                    return "a part of " + SurfaceStop.Describe(owner);
            return entity.DisplayName ?? entity.GetType().Name;
        }

        // ---- Loading (worker thread) ----

        private static void Load(string asset, float scale, HashSet<string> screens, ModelPanes into)
        {
            try
            {
                var model = new MyModel(asset, false);
                model.LoadData(true);
                List<MyMesh> meshes = model.GetMeshList();
                MyTriangleVertexIndices[] triangles = model.Triangles;
                if (meshes == null || triangles == null)
                    throw new InvalidDataException($"no {(meshes == null ? "meshes" : "triangles")}");
                var glass = new List<Tri>();
                var screenCentres = new List<Vector3>();
                foreach (MyMesh mesh in meshes)
                {
                    MyMeshMaterial material = mesh.Material;
                    if (material == null)
                        continue;
                    bool isGlass = material.DrawTechnique == MyMeshDrawTechnique.GLASS;
                    // A screen: one of the block's surfaces, or a material named as one (an LCD's other rotations).
                    bool isScreen = !isGlass && material.Name != null
                                    && (screens.Contains(material.Name) || material.Name.IndexOf("Screen", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!isGlass && !isScreen)
                        continue;
                    for (int t = mesh.TriStart; t < mesh.TriStart + mesh.TriCount && t < triangles.Length; t++)
                    {
                        MyTriangleVertexIndices i = triangles[t];
                        Vector3 a = model.GetVertex(i.I0) * scale, b = model.GetVertex(i.I1) * scale, c = model.GetVertex(i.I2) * scale;
                        if (isScreen)
                        {
                            screenCentres.Add((a + b + c) / 3f);
                            continue;
                        }
                        Vector3 n = Vector3.Cross(b - a, c - a);
                        float length = n.Length();
                        if (length < 1e-8f)
                            continue;
                        glass.Add(new Tri { A = a, B = b, C = c, Normal = n / length });
                    }
                }
                if (glass.Count == 0)
                {
                    into.Panes = new Pane[0];
                    into.State = 1;
                    return;
                }

                // Pieces: triangles that share a corner are one piece of glass.
                int[] parent = new int[glass.Count];
                for (int t = 0; t < parent.Length; t++)
                    parent[t] = t;
                var corners = new Dictionary<Vector3I, int>();
                for (int t = 0; t < glass.Count; t++)
                {
                    Join(corners, parent, glass[t].A, t);
                    Join(corners, parent, glass[t].B, t);
                    Join(corners, parent, glass[t].C, t);
                }
                var pieces = new Dictionary<int, List<Tri>>();
                for (int t = 0; t < glass.Count; t++)
                {
                    int root = Root(parent, t);
                    if (!pieces.TryGetValue(root, out List<Tri> piece))
                        pieces[root] = piece = new List<Tri>();
                    piece.Add(glass[t]);
                }

                var panes = new List<Pane>();
                int small = 0, onScreens = 0, kept = 0;
                float area = 0f;
                foreach (List<Tri> piece in pieces.Values)
                {
                    float pieceArea = 0f;
                    BoundingBox box = BoundingBox.CreateInvalid();
                    foreach (Tri tri in piece)
                    {
                        pieceArea += Vector3.Cross(tri.B - tri.A, tri.C - tri.A).Length() * 0.5f;
                        box.Include(tri.A);
                        box.Include(tri.B);
                        box.Include(tri.C);
                    }
                    if (pieceArea < MinPane)
                    {
                        small++;
                        continue;
                    }
                    if (CoversScreen(piece, box, screenCentres))
                    {
                        onScreens++;
                        continue;
                    }
                    panes.Add(new Pane { Tris = piece.ToArray(), Box = box });
                    kept += piece.Count;
                    area += pieceArea;
                }
                into.Panes = panes.ToArray();
                into.Report = $"{panes.Count} pane(s), {kept} triangles, {area:0.00} m²; left out {small} small piece(s) and {onScreens} on a screen";
                into.State = 1;
            }
            catch (Exception e)
            {
                into.Error = e.GetType().Name + ": " + e.Message;
                into.State = 2;
            }
        }

        private static void Join(Dictionary<Vector3I, int> corners, int[] parent, Vector3 corner, int t)
        {
            var at = new Vector3I((int)Math.Round(corner.X * Weld), (int)Math.Round(corner.Y * Weld), (int)Math.Round(corner.Z * Weld));
            if (corners.TryGetValue(at, out int other))
                parent[Root(parent, t)] = Root(parent, other);
            else
                corners[at] = t;
        }

        private static int Root(int[] parent, int t)
        {
            while (parent[t] != t)
                t = parent[t] = parent[parent[t]];
            return t;
        }

        /// <summary>Whether a screen's triangle lies on this piece (its centre within <see cref="OnScreen"/> of the glass).</summary>
        private static bool CoversScreen(List<Tri> piece, BoundingBox box, List<Vector3> screenCentres)
        {
            box.Inflate(OnScreen);
            foreach (Vector3 p in screenCentres)
            {
                if (box.Contains(p) == ContainmentType.Disjoint)
                    continue;
                foreach (Tri tri in piece)
                    if (Vector3.DistanceSquared(p, ScreenGlass.Closest(p, tri.A, tri.B, tri.C)) <= OnScreen * OnScreen)
                        return true;
            }
            return false;
        }
    }
}
