using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using VRage.Game.Entity;
using VRage.Game.Models;
using VRageMath;
using VRageMath.PackedVector;
using VRageRender.Models;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// Every screen in the game as a pane of glass: where it is, which way it faces, and which pixel of it is where.
    /// A screen is a material of the block's model (ScreenArea, ScreenArea90/180/270, TransparentScreenArea*,
    /// CockpitScreen_01..06, VendingScreen, StoreScreen, ContractsScreen, ATM_*, a mod's own), and the game draws the
    /// surface's picture onto it through the mesh's texture coordinates. So the glass is read from the mesh itself: the
    /// triangles of that material, with their UVs, loaded once per model (on a worker thread, full detail) and kept.
    /// Which material is which surface comes from the block's own text panel components, so surface i here is the
    /// game's surface i (IMyTextSurfaceProvider.GetSurface(i)): an LCD's one panel (its selected rotation's area), or
    /// the multi-panel owners' (cockpits, button panels, consoles, stores) Panels[i].
    /// </summary>
    /// <remarks>
    /// The pixel is in the surface's sprite coordinates, what a script draws with: the render target the game draws
    /// spans the surface's SurfaceSize, offset by (TextureSize - SurfaceSize) / 2, the script viewport's corner
    /// (MyTextPanelComponent, MyRender11.DrawSpritesOffscreen). UV v runs down the picture (DirectX), so y does too.
    /// A cockpit seen from its seat in first person shows its interior model (MyCockpit.UpdateCockpitModel), whose
    /// screens are the interior's meshes; seen from outside, the block's model.
    /// </remarks>
    internal static class ScreenGlass
    {
        /// <summary>Debug: no glass, no touch (<see cref="Rendering.RenderDebug"/> ScreenGlass=0).</summary>
        public static bool Off { get; set; }

        /// <summary>A tip this far in front of the glass, over it, is coming at it from the front (and may touch).</summary>
        public const float Approach = 0.25f;

        /// <summary>Past the glass further than this at the first frame it is seen past: it came round the edge or from behind, not through.</summary>
        public const float MaxCatch = 0.12f;

        /// <summary>A point this close outside the edge still counts as over the glass.</summary>
        public const float EdgeMargin = 0.005f;

        /// <summary>A screen whose glass came from in front within this long may be touched or stopped on.</summary>
        public const double ArmSeconds = 0.15;

        /// <summary>The radius round a fingertip blocks are looked for in (a big screen's middle can be far from its block's corner).</summary>
        public const double GatherRadius = 0.75;

        // ---- The glass of a model ----

        internal struct Tri
        {
            public Vector3 A, B, C;
            public Vector2 UA, UB, UC;
            public Vector3 Normal;
        }

        /// <summary>One side of a screen material: its triangles facing one way (a transparent screen has two, a curved one more).</summary>
        internal sealed class Sheet
        {
            public string Material;
            public int Index;
            public Tri[] Tris;
            public Vector3 Normal, Centre;
            public Vector2 UVMin, UVMax;
            /// <summary>Model space, metres per whole UV along u and v (the picture's x and y).</summary>
            public Vector3 AlongU, AlongV;
            public float Flatness, Radius;
        }

        private sealed class ModelGlass
        {
            public volatile int State;   // 0 loading, 1 ready, 2 failed
            public readonly Dictionary<string, List<Sheet>> Sheets = new Dictionary<string, List<Sheet>>(StringComparer.OrdinalIgnoreCase);
            public string Report, Error;
            public bool Logged;
        }

        private static readonly Dictionary<string, ModelGlass> models = new Dictionary<string, ModelGlass>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The sheets of a model's material, or null while it loads (or if it cannot).</summary>
        /// <summary>
        /// The sheets of a model's material, or null while it loads (or if it has none). Kept per model and set of screen
        /// materials, so an LCD turned to another rotation's area (another material of the same model) loads it again.
        /// </summary>
        private static List<Sheet> SheetsOf(string asset, float scale, string material, List<string> allMaterials)
        {
            string key = asset + "|" + scale.ToString("R") + "|" + string.Join(",", allMaterials);
            if (!models.TryGetValue(key, out ModelGlass glass))
            {
                glass = new ModelGlass();
                models[key] = glass;
                var wanted = new HashSet<string>(allMaterials, StringComparer.OrdinalIgnoreCase);
                ThreadPool.QueueUserWorkItem(_ => Load(asset, scale, wanted, glass));
                return null;
            }
            if (glass.State == 0)
                return null;
            if (!glass.Logged)
            {
                glass.Logged = true;
                Log.Info(glass.State == 1 ? $"Screen glass: {Path.GetFileName(asset)}: {glass.Report}" : $"Screen glass: {asset} could not be read: {glass.Error}");
            }
            return glass.State == 1 && glass.Sheets.TryGetValue(material, out List<Sheet> sheets) ? sheets : null;
        }

        private static void Load(string asset, float scale, HashSet<string> wanted, ModelGlass into)
        {
            try
            {
                var model = new MyModel(asset, false);
                model.LoadUV = true;
                model.LoadData(true);
                List<MyMesh> meshes = model.GetMeshList();
                MyTriangleVertexIndices[] triangles = model.Triangles;
                HalfVector2[] uvs = model.TexCoords;
                if (meshes == null || triangles == null || uvs == null)
                    throw new InvalidDataException($"no {(meshes == null ? "meshes" : triangles == null ? "triangles" : "texture coordinates")}");
                var byMaterial = new Dictionary<string, List<Tri>>(StringComparer.OrdinalIgnoreCase);
                foreach (MyMesh mesh in meshes)
                {
                    string name = mesh.Material?.Name;
                    if (name == null || !wanted.Contains(name))
                        continue;
                    if (!byMaterial.TryGetValue(name, out List<Tri> list))
                        byMaterial[name] = list = new List<Tri>();
                    for (int t = mesh.TriStart; t < mesh.TriStart + mesh.TriCount && t < triangles.Length; t++)
                    {
                        MyTriangleVertexIndices i = triangles[t];
                        var tri = new Tri
                        {
                            A = model.GetVertex(i.I0) * scale, B = model.GetVertex(i.I1) * scale, C = model.GetVertex(i.I2) * scale,
                            UA = uvs[i.I0].ToVector2(), UB = uvs[i.I1].ToVector2(), UC = uvs[i.I2].ToVector2(),
                        };
                        Vector3 n = Vector3.Cross(tri.B - tri.A, tri.C - tri.A);
                        float length = n.Length();
                        if (length < 1e-8f)
                            continue;
                        n /= length;
                        // The face is the side the mesh's normals are on, whatever order the corners come in.
                        Vector3 shading = model.GetVertexNormal(i.I0) + model.GetVertexNormal(i.I1) + model.GetVertexNormal(i.I2);
                        tri.Normal = Vector3.Dot(n, shading) < 0f ? -n : n;
                        list.Add(tri);
                    }
                }
                var report = new StringBuilder();
                foreach (string name in wanted)
                {
                    if (!byMaterial.TryGetValue(name, out List<Tri> list) || list.Count == 0)
                    {
                        report.Append($"{name} none; ");
                        continue;
                    }
                    List<Sheet> sheets = Split(name, list);
                    into.Sheets[name] = sheets;
                    report.Append($"{name} {list.Count} tris");
                    foreach (Sheet sheet in sheets)
                        report.Append($" [sheet {sheet.Index}: {sheet.Tris.Length} tris, {sheet.AlongU.Length() * (sheet.UVMax.X - sheet.UVMin.X) * 100f:F1} x " +
                                      $"{sheet.AlongV.Length() * (sheet.UVMax.Y - sheet.UVMin.Y) * 100f:F1} cm, uv {sheet.UVMin.X:F2}-{sheet.UVMax.X:F2} by " +
                                      $"{sheet.UVMin.Y:F2}-{sheet.UVMax.Y:F2}, faces {Fmt(sheet.Normal)}, centre {Fmt(sheet.Centre)}, flat to {sheet.Flatness * 1000f:F1} mm]");
                    report.Append("; ");
                }
                into.Report = report.ToString().TrimEnd(' ', ';');
                into.State = 1;
            }
            catch (Exception e)
            {
                into.Error = e.GetType().Name + ": " + e.Message;
                into.State = 2;
            }
        }

        private static string Fmt(Vector3 v) => $"({v.X:F2},{v.Y:F2},{v.Z:F2})";

        /// <summary>Triangles facing roughly one way are one sheet; the far side of a two-sided screen is another.</summary>
        private static List<Sheet> Split(string material, List<Tri> tris)
        {
            var groups = new List<List<Tri>>();
            var refs = new List<Vector3>();
            foreach (Tri t in tris)
            {
                int j = refs.FindIndex(n => Vector3.Dot(n, t.Normal) > 0.2f);
                if (j < 0)
                {
                    refs.Add(t.Normal);
                    groups.Add(new List<Tri>());
                    j = groups.Count - 1;
                }
                groups[j].Add(t);
            }
            var sheets = new List<Sheet>();
            for (int g = 0; g < groups.Count; g++)
            {
                Tri[] set = groups[g].ToArray();
                var sheet = new Sheet { Material = material, Index = g, Tris = set, UVMin = new Vector2(float.MaxValue), UVMax = new Vector2(float.MinValue) };
                Vector3 normal = Vector3.Zero, centre = Vector3.Zero;
                float area = 0f, largest = -1f;
                foreach (Tri t in set)
                {
                    float a = Vector3.Cross(t.B - t.A, t.C - t.A).Length() * 0.5f;
                    normal += t.Normal * a;
                    centre += (t.A + t.B + t.C) / 3f * a;
                    area += a;
                    foreach (Vector2 uv in new[] { t.UA, t.UB, t.UC })
                    {
                        sheet.UVMin = Vector2.Min(sheet.UVMin, uv);
                        sheet.UVMax = Vector2.Max(sheet.UVMax, uv);
                    }
                    if (a > largest && Gradient(t, out Vector3 du, out Vector3 dv))
                    {
                        largest = a;
                        sheet.AlongU = du;
                        sheet.AlongV = dv;
                    }
                }
                sheet.Normal = Vector3.Normalize(normal);
                sheet.Centre = area > 0f ? centre / area : set[0].A;
                foreach (Tri t in set)
                    foreach (Vector3 p in new[] { t.A, t.B, t.C })
                    {
                        sheet.Flatness = Math.Max(sheet.Flatness, Math.Abs(Vector3.Dot(p - sheet.Centre, sheet.Normal)));
                        sheet.Radius = Math.Max(sheet.Radius, (p - sheet.Centre).Length());
                    }
                sheets.Add(sheet);
            }
            return sheets;
        }

        /// <summary>How far a triangle's surface moves per whole UV, along u and along v (model space).</summary>
        private static bool Gradient(in Tri t, out Vector3 du, out Vector3 dv)
        {
            Vector3 e1 = t.B - t.A, e2 = t.C - t.A;
            Vector2 d1 = t.UB - t.UA, d2 = t.UC - t.UA;
            float r = d1.X * d2.Y - d2.X * d1.Y;
            if (Math.Abs(r) < 1e-12f)
            {
                du = dv = Vector3.Zero;
                return false;
            }
            du = (e1 * d2.Y - e2 * d1.Y) / r;
            dv = (e2 * d1.X - e1 * d2.X) / r;
            return true;
        }

        // ---- Measuring a point against a sheet ----

        internal struct Hit
        {
            /// <summary>Signed distance from the glass, metres: positive in front, negative past it.</summary>
            public float Distance;
            /// <summary>How far outside the glass's edge the point is, along the glass (0: over it).</summary>
            public float Edge;
            public Vector2 UV;
            /// <summary>The nearest point of the glass, model space.</summary>
            public Vector3 Point;
            public Vector3 Normal;
        }

        internal static Hit Measure(Sheet sheet, Vector3 p)
        {
            var best = new Hit { Edge = float.MaxValue };
            float bestAbs = float.MaxValue;
            foreach (Tri t in sheet.Tris)
            {
                float d = Vector3.Dot(p - t.A, t.Normal);
                Vector3 q = p - t.Normal * d;
                Vector3 c = Closest(q, t.A, t.B, t.C);
                float e = (q - c).Length();
                if (e < best.Edge - 1e-5f || (e <= best.Edge + 1e-5f && Math.Abs(d) < bestAbs))
                {
                    Barycentric(c, t.A, t.B, t.C, out float w0, out float w1, out float w2);
                    best = new Hit { Distance = d, Edge = e, Point = c, Normal = t.Normal, UV = t.UA * w0 + t.UB * w1 + t.UC * w2 };
                    bestAbs = Math.Abs(d);
                }
            }
            return best;
        }

        /// <summary>The point of the glass showing a UV (model space), or false if no triangle shows it.</summary>
        internal static bool PointAt(Sheet sheet, Vector2 uv, out Vector3 point, out Vector3 normal)
        {
            float bestScore = float.MinValue;
            point = sheet.Centre;
            normal = sheet.Normal;
            foreach (Tri t in sheet.Tris)
            {
                Vector2 v0 = t.UB - t.UA, v1 = t.UC - t.UA, v2 = uv - t.UA;
                float den = v0.X * v1.Y - v1.X * v0.Y;
                if (Math.Abs(den) < 1e-12f)
                    continue;
                float w1 = (v2.X * v1.Y - v1.X * v2.Y) / den, w2 = (v0.X * v2.Y - v2.X * v0.Y) / den, w0 = 1f - w1 - w2;
                float score = Math.Min(w0, Math.Min(w1, w2));
                if (score > bestScore)
                {
                    bestScore = score;
                    point = t.A * w0 + t.B * w1 + t.C * w2;
                    normal = t.Normal;
                }
            }
            return bestScore > -0.01f;
        }

        private static void Barycentric(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out float w0, out float w1, out float w2)
        {
            Vector3 v0 = b - a, v1 = c - a, v2 = p - a;
            float d00 = Vector3.Dot(v0, v0), d01 = Vector3.Dot(v0, v1), d11 = Vector3.Dot(v1, v1);
            float d20 = Vector3.Dot(v2, v0), d21 = Vector3.Dot(v2, v1);
            float den = d00 * d11 - d01 * d01;
            if (Math.Abs(den) < 1e-20f)
            {
                w0 = 1f;
                w1 = w2 = 0f;
                return;
            }
            w1 = (d11 * d20 - d01 * d21) / den;
            w2 = (d00 * d21 - d01 * d20) / den;
            w0 = 1f - w1 - w2;
        }

        /// <summary>The point of a triangle nearest p (Ericson, Real-Time Collision Detection 5.1.5).</summary>
        private static Vector3 Closest(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f)
                return a;
            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3)
                return b;
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
                return a + ab * (d1 / (d1 - d3));
            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6)
                return c;
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
                return a + ac * (d2 / (d2 - d6));
            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
                return b + (c - b) * ((d4 - d3) / (d4 - d3 + (d5 - d6)));
            float den = 1f / (va + vb + vc);
            return a + ab * (vb * den) + ac * (vc * den);
        }

        // ---- The screens of a block ----

        /// <summary>Which sheet of which surface of which block: what a hand is on, kept frame to frame (and named in the log).</summary>
        internal sealed class ScreenKey : IEquatable<ScreenKey>
        {
            public readonly long Entity;
            public readonly int Surface, Sheet;
            public readonly string Name;

            public ScreenKey(long entity, int surface, int sheet, string name)
            {
                Entity = entity;
                Surface = surface;
                Sheet = sheet;
                Name = name;
            }

            public bool Equals(ScreenKey other) => other != null && other.Entity == Entity && other.Surface == Surface && other.Sheet == Sheet;
            public override bool Equals(object obj) => Equals(obj as ScreenKey);
            public override int GetHashCode() => Entity.GetHashCode() ^ (Surface * 397) ^ (Sheet * 7919);
            public override string ToString() => Name;
        }

        /// <summary>One sheet of one screen near a hand this frame.</summary>
        internal sealed class Screen
        {
            public MyCubeBlock Block;
            public int Surface;
            public MyTextPanelComponent Panel;
            public Sheet Sheet;
            public MatrixD ToWorld, FromWorld;
            public ScreenKey Key;

            public Vector2 Pixel(Vector2 uv) => ScreenGlass.Pixel(Panel, uv);
        }

        /// <summary>A UV of a surface's picture to its pixel in sprite coordinates (see the remarks on the class).</summary>
        internal static Vector2 Pixel(MyTextPanelComponent panel, Vector2 uv)
        {
            Vector2 surface = panel.SurfaceSize, texture = panel.TextureSize;
            return uv * surface + (texture - surface) / 2f;
        }

        /// <summary>A pixel back to the UV that shows it.</summary>
        internal static Vector2 UV(MyTextPanelComponent panel, Vector2 pixel)
        {
            Vector2 surface = panel.SurfaceSize, texture = panel.TextureSize;
            return new Vector2(surface.X > 0f ? (pixel.X - (texture.X - surface.X) / 2f) / surface.X : 0.5f,
                               surface.Y > 0f ? (pixel.Y - (texture.Y - surface.Y) / 2f) / surface.Y : 0.5f);
        }

        /// <summary>The visible area of a surface in sprite coordinates: its top-left corner and size.</summary>
        internal static RectangleF Viewport(MyTextPanelComponent panel) =>
            new RectangleF((panel.TextureSize - panel.SurfaceSize) / 2f, panel.SurfaceSize);

        private static readonly List<MyTextPanelComponent> panelScratch = new List<MyTextPanelComponent>();

        /// <summary>A block's surfaces in the game's order (null if it has none). The list is reused: read it before the next call.</summary>
        internal static List<MyTextPanelComponent> Surfaces(MyCubeBlock block)
        {
            panelScratch.Clear();
            if (block is MyTextPanel lcd)
            {
                if (lcd.PanelComponent != null)
                    panelScratch.Add(lcd.PanelComponent);
            }
            // Through object: from MyCubeBlock the compiler would look at its interfaces, some in Sandbox.Common (not referenced).
            else if ((object)block is IMyMultiTextPanelComponentOwner owner && owner.MultiTextPanel?.Panels != null)
                panelScratch.AddRange(owner.MultiTextPanel.Panels);
            return panelScratch.Count > 0 ? panelScratch : null;
        }

        /// <summary>The model the block shows now: a cockpit's interior to its pilot in first person, else its own.</summary>
        internal static string ShownModel(MyCubeBlock block)
        {
            if (block is MyCockpit cockpit && !string.IsNullOrEmpty(cockpit.BlockDefinition?.InteriorModel)
                && MySession.Static?.CameraController == cockpit && (cockpit.IsInFirstPersonView || cockpit.ForceFirstPersonCamera))
                return cockpit.BlockDefinition.InteriorModel;
            return block.Model?.AssetName;
        }

        internal static string Describe(MyCubeBlock block) =>
            block == null ? "nothing" : $"{block.BlockDefinition?.Id.TypeId.ToString().Replace("MyObjectBuilder_", "")} {block.BlockDefinition?.Id.SubtypeName}";

        private static readonly List<MyEntity> nearby = new List<MyEntity>();
        private static readonly List<string> materials = new List<string>();

        /// <summary>
        /// Every screen sheet of a block, added to <paramref name="into"/> (from <paramref name="pool"/>). Screens whose
        /// glass is still loading are left out this frame.
        /// </summary>
        internal static void AddScreens(MyCubeBlock block, List<Screen> into, List<Screen> pool, ref int used)
        {
            if (block == null || block.Closed || block.PositionComp == null)
                return;
            List<MyTextPanelComponent> panels = Surfaces(block);
            if (panels == null)
                return;
            string asset = ShownModel(block);
            if (string.IsNullOrEmpty(asset))
                return;
            materials.Clear();
            foreach (MyTextPanelComponent panel in panels)
                if (!string.IsNullOrEmpty(panel?.Name))
                    materials.Add(panel.Name);
            float scale = block.Model?.ScaleFactor ?? 1f;
            for (int surface = 0; surface < panels.Count; surface++)
            {
                MyTextPanelComponent panel = panels[surface];
                if (string.IsNullOrEmpty(panel?.Name))
                    continue;
                List<Sheet> sheets = SheetsOf(asset, scale, panel.Name, materials);
                if (sheets == null)
                    continue;
                foreach (Sheet sheet in sheets)
                {
                    if (used >= pool.Count)
                        pool.Add(new Screen());
                    Screen s = pool[used++];
                    s.Block = block;
                    s.Surface = surface;
                    s.Panel = panel;
                    s.Sheet = sheet;
                    s.ToWorld = block.PositionComp.WorldMatrixRef;
                    s.FromWorld = block.PositionComp.WorldMatrixNormalizedInv;
                    if (s.Key == null || s.Key.Entity != block.EntityId || s.Key.Surface != surface || s.Key.Sheet != sheet.Index)
                        s.Key = new ScreenKey(block.EntityId, surface, sheet.Index,
                            $"the glass of {Describe(block)} '{block.DisplayNameText}' surface {surface} ({panel.Name}{(sheets.Count > 1 ? $", side {sheet.Index}" : "")})");
                    into.Add(s);
                }
            }
        }

        private sealed class Gathered
        {
            public ulong Frame = ulong.MaxValue;
            public Vector3D Centre;
            public readonly List<Screen> Screens = new List<Screen>(), Pool = new List<Screen>();
        }

        private static readonly Gathered[] gathered = { new Gathered(), new Gathered() };

        /// <summary>The screens near a hand's fingertip this frame (worked out once per hand per frame). Game thread.</summary>
        internal static List<Screen> Near(Hand hand, Vector3D tip)
        {
            Gathered g = gathered[(int)hand];
            ulong frame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            if (g.Frame == frame && Vector3D.DistanceSquared(g.Centre, tip) < 0.05 * 0.05)
                return g.Screens;
            g.Frame = frame;
            g.Centre = tip;
            g.Screens.Clear();
            int used = 0;
            var sphere = new BoundingSphereD(tip, GatherRadius);
            nearby.Clear();
            MyGamePruningStructure.GetAllEntitiesInSphere(ref sphere, nearby);
            foreach (MyEntity entity in nearby)
                if (entity is MyCubeBlock block)
                    AddScreens(block, g.Screens, g.Pool, ref used);
            nearby.Clear();
            return g.Screens;
        }

        /// <summary>
        /// The screen whose glass a hand is at: its fingertip or palm less than <paramref name="range"/> in front of the glass
        /// and over it (or beside its edge by less than that), or through it as far as the glass catches a hand
        /// (<see cref="MaxCatch"/>); null when none. <paramref name="distance"/> is the point's from the glass (+ in front).
        /// Game thread.
        /// </summary>
        internal static Screen At(Hand hand, Vector3D tip, Vector3D palm, double range, out float distance)
        {
            distance = 0f;
            if (Off)
                return null;
            foreach (Screen s in Near(hand, tip))
                if (AtGlass(s, tip, range, out distance) || AtGlass(s, palm, range, out distance))
                    return s;
            return null;
        }

        private static bool AtGlass(Screen s, Vector3D point, double range, out float distance)
        {
            Hit hit = Measure(s.Sheet, (Vector3)Vector3D.Transform(point, s.FromWorld));
            distance = hit.Distance;
            return hit.Edge <= range && hit.Distance < range && hit.Distance > -MaxCatch;
        }

        // ---- The glass as a solid for the hands (HandStop) ----

        /// <summary>
        /// The hands stop on the glass: a fingertip or palm that came at a screen from in front and went through it is
        /// pushed back onto it. Coming at it round the edge or from behind does not catch (the hand may be reaching past).
        /// </summary>
        internal sealed class Glass : IHandContactSource
        {
            private sealed class PerHand
            {
                public readonly Dictionary<ScreenKey, long> Armed = new Dictionary<ScreenKey, long>();
                public ScreenKey Stuck;
                public long AskedAt;
            }

            private readonly PerHand[] hands = { new PerHand(), new PerHand() };
            private readonly List<ScreenKey> stale = new List<ScreenKey>();

            public bool TryContact(Hand hand, Vector3D tip, Vector3D palm, out HandContact contact)
            {
                contact = default;
                PerHand h = hands[(int)hand];
                if (Off)
                {
                    h.Stuck = null;
                    return false;
                }
                long now = Stopwatch.GetTimestamp();
                long armWindow = (long)(ArmSeconds * Stopwatch.Frequency);
                // Not asked for a while (the hand stop off, the hand not tracked): what the hand was on is stale.
                if (now - h.AskedAt > armWindow)
                {
                    h.Stuck = null;
                    h.Armed.Clear();
                }
                h.AskedAt = now;
                bool found = false, stuckSeen = false;
                foreach (Screen s in Near(hand, tip))
                {
                    Hit t = Measure(s.Sheet, (Vector3)Vector3D.Transform(tip, s.FromWorld));
                    Hit p = Measure(s.Sheet, (Vector3)Vector3D.Transform(palm, s.FromWorld));
                    bool tipOver = t.Edge <= EdgeMargin, palmOver = p.Edge <= EdgeMargin;
                    if (!tipOver && !palmOver)
                        continue;
                    bool inFront = (!tipOver || (t.Distance > 0f && t.Distance < Approach)) && (!palmOver || (p.Distance > 0f && p.Distance < Approach));
                    if (inFront)
                        h.Armed[s.Key] = now;
                    float depth = Math.Max(tipOver ? -t.Distance : float.MinValue, palmOver ? -p.Distance : float.MinValue);
                    bool stuck = s.Key.Equals(h.Stuck);
                    if (stuck && depth > -0.01f)
                        stuckSeen = true;
                    if (depth <= 0f)
                        continue;
                    bool caught = stuck || (h.Armed.TryGetValue(s.Key, out long armedAt) && now - armedAt <= armWindow && depth < MaxCatch);
                    if (!caught || (found && depth <= contact.Depth))
                        continue;
                    Hit deeper = (tipOver ? -t.Distance : float.MinValue) >= (palmOver ? -p.Distance : float.MinValue) ? t : p;
                    contact = new HandContact
                    {
                        Point = Vector3D.Transform((Vector3D)deeper.Point, s.ToWorld),
                        Normal = Vector3.Normalize((Vector3)Vector3D.TransformNormal((Vector3D)deeper.Normal, s.ToWorld)),
                        Depth = depth,
                        Source = s.Key,
                    };
                    found = true;
                }
                if (found)
                    h.Stuck = (ScreenKey)contact.Source;
                else if (!stuckSeen)
                    h.Stuck = null;

                // Forget screens not armed for a while.
                stale.Clear();
                foreach (KeyValuePair<ScreenKey, long> pair in h.Armed)
                    if (now - pair.Value > 10 * armWindow)
                        stale.Add(pair.Key);
                foreach (ScreenKey key in stale)
                    h.Armed.Remove(key);
                return found;
            }
        }

        private static int registered;

        /// <summary>Puts the glass among the hand stop's surfaces (once).</summary>
        internal static void Register()
        {
            if (Interlocked.Exchange(ref registered, 1) == 0)
                HandStop.Register(new Glass());
        }
    }
}
