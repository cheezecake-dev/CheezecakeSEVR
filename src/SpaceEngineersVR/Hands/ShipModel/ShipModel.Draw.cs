using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using SpaceEngineersVR.Rendering;
using SpaceEngineersVR.Tracking;
using VRage.Game;
using VRage.Generics;
using VRage.Utils;
using VRageMath;
using VRageRender;
using MyTransparentGeometry = VRage.Game.MyTransparentGeometry;

namespace SpaceEngineersVR.Hands
{
    internal static partial class ShipModel
    {
        // ---- Drawing ----
        // Billboards, as the game draws its own lines and markers, filled in directly (not through MyTransparentGeometry.AddQuad,
        // which looks up the material and converts the colour for each one) and handed to the renderer in one batch. They are
        // given in the local character's space (MotionExtrapolation), so the model stays on the hand when the body and the view
        // are moved on past the game frame.

        /// <summary>A plain white square, depth tested: the model's faces and lines.</summary>
        private static readonly MyStringId FaceMaterial = MyStringId.GetOrCompute("Square");

        /// <summary>The real block's outline shows through what is in front of it.</summary>
        private static readonly MyStringId OutlineMaterial = MyStringId.GetOrCompute("SquareIgnoreDepth");

        private static readonly MyStringId DotMaterial = MyStringId.GetOrCompute("WhiteDot");

        private const float ModelLine = 0.0015f, OutlineLine = 0.012f, DotRadius = 0.004f;

        // The hologram's colours, in the game's sRGB with the alpha the renderer blends by: a low alpha adds light more than it
        // covers, so the faces glow and the ones behind still show.
        private static readonly Vector4 HullColor = new Vector4(0.10f, 0.42f, 0.55f, 0.08f);
        private static readonly Vector4 LightDamageColor = new Vector4(0.45f, 0.09f, 0.05f, 0.12f);
        private static readonly Vector4 HeavyDamageColor = new Vector4(0.95f, 0.06f, 0.03f, 0.2f);
        private static readonly Vector4 GoneColor = new Vector4(0.01f, 0.015f, 0.02f, 0.6f);
        private static readonly Vector4 ThrusterColor = new Vector4(0.75f, 0.38f, 0.05f, 0.1f);
        private static readonly Vector4 PowerColor = new Vector4(0.70f, 0.62f, 0.08f, 0.1f);
        private static readonly Vector4 DoorColor = new Vector4(0.10f, 0.65f, 0.20f, 0.1f);
        private static readonly Vector4 WeaponColor = new Vector4(0.95f, 0.12f, 0.10f, 0.6f);
        private static readonly Vector4 PointedColor = new Vector4(0.55f, 0.85f, 1f, 0.15f);
        private static readonly Vector4 PointedEdgeColor = new Vector4(0.8f, 0.95f, 1f, 0.8f);
        private static readonly Vector4 ForwardColor = new Vector4(0.6f, 0.9f, 1f, 0.8f);
        private static readonly Vector4 OutlineColor = new Vector4(0.55f, 0.9f, 1f, 0.9f);

        /// <summary>The forward mark: an arrow off the model's front, metres in the tracking space.</summary>
        private const float ArrowLength = 0.035f, ArrowHead = 0.015f;

        private static Vector4[] looks;
        private static Vector4 pointedLinear;
        private static readonly List<MyBillboard> batch = new List<MyBillboard>(QuadBudget + 16);
        private static int drawErrors;

        // The cost, for the Perf line.
        private static long costTicks, costMaxTicks;
        private static int costFrames, costQuads;

        /// <summary>The faces' colours, as the renderer takes them (linear, and the material's tint), by look.</summary>
        private static Vector4[] Looks()
        {
            if (looks != null)
                return looks;
            Vector4 tint = MyTransparentMaterials.GetMaterial(FaceMaterial).Color;
            var colours = new Vector4[ShipMesh.LookCount];
            colours[ShipMesh.LookHull] = HullColor;
            for (int step = 1; step <= ShipMesh.DamageSteps; step++)
                colours[step] = Vector4.Lerp(LightDamageColor, HeavyDamageColor, (step - 1) / (float)(ShipMesh.DamageSteps - 1));
            colours[ShipMesh.LookGone] = GoneColor;
            colours[ShipMesh.LookOf(ShipPart.Hull)] = HullColor;
            colours[ShipMesh.LookOf(ShipPart.Thruster)] = ThrusterColor;
            colours[ShipMesh.LookOf(ShipPart.Power)] = PowerColor;
            colours[ShipMesh.LookOf(ShipPart.Door)] = DoorColor;
            colours[ShipMesh.LookOf(ShipPart.Weapon)] = WeaponColor;
            for (int i = 0; i < colours.Length; i++)
                colours[i] = colours[i].ToLinearRGB() * tint;
            pointedLinear = PointedColor.ToLinearRGB() * tint;
            return looks = colours;
        }

        /// <summary>The gameplay screen's draw: the model, its marks, and the real block's outline.</summary>
        private static void Draw()
        {
            if (!Shown || fade <= 0f || drawErrors >= 3 || !MyTransparentGeometry.HasCamera)
                return;
            long start = Stopwatch.GetTimestamp();
            try
            {
                // On the pad, where the pad is drawn this frame (DatapadModel takes it the same way).
                if (onPad && !hiding)
                    RidePad();
                MyObjectsPool<MyBillboard> pool = MyRenderProxy.BillboardsPoolWrite;
                if (pool == null || !HandWorld.TryGetTrackingToWorld(out MatrixD toWorld))
                    return;
                bool ride = MotionExtrapolation.TryRide(out uint parent, out MatrixD worldToLocal);
                if (!ride)
                    parent = uint.MaxValue;
                MatrixD toDraw = ride ? toWorld * worldToLocal : toWorld;
                Vector4[] colours = Looks();
                Matrix modelToTracking = ModelToTracking;
                float distance = (float)Vector3D.DistanceSquared(MyTransparentGeometry.Camera.Translation, Vector3D.Transform((Vector3D)pose.Translation, toWorld));

                batch.Clear();
                foreach (Part part in parts)
                {
                    ShipShape shape = part.Shape;
                    if (shape == null || part.Grid.Closed)
                        continue;
                    MatrixD m = (MatrixD)(GridToModel(part.Grid) * modelToTracking) * toDraw;
                    Faces(pool, shape.Hull, colours, m, parent, distance);
                    Faces(pool, shape.Overlay, colours, m, parent, distance);
                }
                if (TryPointed(out Part pointedOn, out ShipBlock block, out _))
                {
                    BoundingBox box = ShipMesh.BoxOf(block, pointedOn.Shape.CellSize);
                    box.Inflate(pointedOn.Shape.CellSize * 0.03f);
                    MatrixD m = (MatrixD)(GridToModel(pointedOn.Grid) * modelToTracking) * toDraw;
                    Box(pool, box, pointedLinear * fade, m, parent, distance);
                }
                int quads = batch.Count;
                MyRenderProxy.AddBillboards(batch);
                batch.Clear();

                Lines(toWorld, ride, parent, ref worldToLocal, modelToTracking);
                Outline();

                long ticks = Stopwatch.GetTimestamp() - start;
                Interlocked.Add(ref costTicks, ticks);
                Interlocked.Increment(ref costFrames);
                Interlocked.Add(ref costQuads, quads);
                if (ticks > Interlocked.Read(ref costMaxTicks))
                    Interlocked.Exchange(ref costMaxTicks, ticks);
            }
            catch (Exception e)
            {
                batch.Clear();
                if (drawErrors++ < 3)
                    Log.Error(e, "Ship model could not be drawn" + (drawErrors >= 3 ? "; it is not drawn for the rest of this run" : string.Empty));
            }
        }

        private static void Faces(MyObjectsPool<MyBillboard> pool, ShipFace[] faces, Vector4[] colours, in MatrixD m, uint parent, float distance)
        {
            if (faces == null)
                return;
            for (int i = 0; i < faces.Length && batch.Count < QuadBudget; i++)
            {
                ShipFace f = faces[i];
                Quad(pool, f.P0, f.P1, f.P2, f.P3, colours[f.Look] * fade, m, parent, distance);
            }
        }

        private static void Quad(MyObjectsPool<MyBillboard> pool, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector4 color, in MatrixD m, uint parent, float distance)
        {
            pool.AllocateOrCreate(out MyBillboard b);
            b.Material = FaceMaterial;
            b.BlendType = MyBillboard.BlendTypeEnum.Standard;
            b.Position0 = Vector3D.Transform((Vector3D)p0, m);
            b.Position1 = Vector3D.Transform((Vector3D)p1, m);
            b.Position2 = Vector3D.Transform((Vector3D)p2, m);
            b.Position3 = Vector3D.Transform((Vector3D)p3, m);
            b.Color = color;
            b.ColorIntensity = 1f;
            b.SoftParticleDistanceScale = 1f;
            b.UVOffset = Vector2.Zero;
            b.UVSize = Vector2.One;
            b.LocalType = MyBillboard.LocalTypeEnum.Custom;
            b.ParentID = parent;
            b.DistanceSquared = distance;
            b.Reflectivity = 0f;
            b.AlphaCutout = 0f;
            b.CustomViewProjection = -1;
            batch.Add(b);
        }

        /// <summary>A box's six faces.</summary>
        private static void Box(MyObjectsPool<MyBillboard> pool, BoundingBox box, Vector4 color, in MatrixD m, uint parent, float distance)
        {
            Vector3 a = box.Min, z = box.Max;
            Quad(pool, new Vector3(a.X, a.Y, a.Z), new Vector3(a.X, z.Y, a.Z), new Vector3(a.X, z.Y, z.Z), new Vector3(a.X, a.Y, z.Z), color, m, parent, distance);
            Quad(pool, new Vector3(z.X, a.Y, a.Z), new Vector3(z.X, z.Y, a.Z), new Vector3(z.X, z.Y, z.Z), new Vector3(z.X, a.Y, z.Z), color, m, parent, distance);
            Quad(pool, new Vector3(a.X, a.Y, a.Z), new Vector3(z.X, a.Y, a.Z), new Vector3(z.X, a.Y, z.Z), new Vector3(a.X, a.Y, z.Z), color, m, parent, distance);
            Quad(pool, new Vector3(a.X, z.Y, a.Z), new Vector3(z.X, z.Y, a.Z), new Vector3(z.X, z.Y, z.Z), new Vector3(a.X, z.Y, z.Z), color, m, parent, distance);
            Quad(pool, new Vector3(a.X, a.Y, a.Z), new Vector3(z.X, a.Y, a.Z), new Vector3(z.X, z.Y, a.Z), new Vector3(a.X, z.Y, a.Z), color, m, parent, distance);
            Quad(pool, new Vector3(a.X, a.Y, z.Z), new Vector3(z.X, a.Y, z.Z), new Vector3(z.X, z.Y, z.Z), new Vector3(a.X, z.Y, z.Z), color, m, parent, distance);
        }

        // ---- Lines: the weapons' outlines, the pointed block's edges, the forward arrow ----

        private static readonly Vector3[] corners = new Vector3[8];
        private static readonly Vector3D[] worldCorners = new Vector3D[8];

        /// <summary>A box's twelve edges, by corner (BoundingBox.GetCorners' order).</summary>
        private static readonly int[] Edges = { 0, 1, 1, 2, 2, 3, 3, 0, 4, 5, 5, 6, 6, 7, 7, 4, 0, 4, 1, 5, 2, 6, 3, 7 };

        /// <summary>The most weapons outlined; the rest still have their place in the hull.</summary>
        private const int MostWeapons = 150;

        private static void Lines(in MatrixD toWorld, bool ride, uint parent, ref MatrixD worldToLocal, in Matrix modelToTracking)
        {
            float thickness = ModelLine;
            int weapons = 0;
            foreach (Part part in parts)
            {
                ShipShape shape = part.Shape;
                if (shape == null || part.Grid.Closed)
                    continue;
                MatrixD m = (MatrixD)(GridToModel(part.Grid) * modelToTracking) * toWorld;
                foreach (BoundingBox weapon in shape.Weapons)
                {
                    if (weapons++ >= MostWeapons)
                        break;
                    BoxEdges(weapon, m, WeaponColor * fade, FaceMaterial, thickness, ride, parent, ref worldToLocal);
                }
            }
            if (TryPointed(out Part pointedOn, out ShipBlock block, out _))
            {
                BoundingBox box = ShipMesh.BoxOf(block, pointedOn.Shape.CellSize);
                box.Inflate(pointedOn.Shape.CellSize * 0.03f);
                MatrixD m = (MatrixD)(GridToModel(pointedOn.Grid) * modelToTracking) * toWorld;
                BoxEdges(box, m, PointedEdgeColor * fade, FaceMaterial, thickness, ride, parent, ref worldToLocal);
                if (!pointedByTip)
                {
                    // A dot where the ray meets the model.
                    Vector3D at = Vector3D.Transform((Vector3D)pointedAt, toWorld);
                    if (ride)
                        MyTransparentGeometry.AddPointBillboard(DotMaterial, PointedEdgeColor * fade, at, parent, ref worldToLocal, DotRadius, 0f);
                    else
                        MyTransparentGeometry.AddPointBillboard(DotMaterial, PointedEdgeColor * fade, at, DotRadius, 0f);
                }
            }

            // The forward arrow, off the middle of the model's front face.
            Vector3 front = Vector3.Transform(new Vector3(0f, 0f, -halfExtent.Z), modelToTracking);
            Vector3 forward = Vector3.Normalize(pose.Forward), side = Vector3.Normalize(pose.Right);
            Vector3 tip = front + forward * ArrowLength;
            Vector3D w0 = Vector3D.Transform((Vector3D)front, toWorld), w1 = Vector3D.Transform((Vector3D)tip, toWorld);
            Vector3D left = Vector3D.Transform((Vector3D)(tip - forward * ArrowHead - side * ArrowHead * 0.7f), toWorld);
            Vector3D right = Vector3D.Transform((Vector3D)(tip - forward * ArrowHead + side * ArrowHead * 0.7f), toWorld);
            Vector4 arrow = ForwardColor * fade;
            Line(w0, w1, arrow, FaceMaterial, thickness * 1.5f, ride, parent, ref worldToLocal);
            Line(w1, left, arrow, FaceMaterial, thickness * 1.5f, ride, parent, ref worldToLocal);
            Line(w1, right, arrow, FaceMaterial, thickness * 1.5f, ride, parent, ref worldToLocal);
        }

        /// <summary>The real block pointed at, outlined in the world, through whatever is in front of it.</summary>
        private static void Outline()
        {
            if (!TryPointedWorld(out MatrixD box))
                return;
            float thickness = Math.Max(OutlineLine, (float)box.Right.Length() * 0.01f);
            var local = new BoundingBox(new Vector3(-0.5f), new Vector3(0.5f));
            local.GetCorners(corners);
            for (int i = 0; i < 8; i++)
                worldCorners[i] = Vector3D.Transform((Vector3D)corners[i], box);
            for (int e = 0; e < Edges.Length; e += 2)
            {
                Vector3D a = worldCorners[Edges[e]], b = worldCorners[Edges[e + 1]];
                Vector3D along = b - a;
                double length = along.Length();
                if (length > 1e-4)
                    MotionExtrapolation.AddLine(OutlineMaterial, OutlineColor * fade, a, (Vector3)(along / length), (float)length, thickness);
            }
        }

        private static void BoxEdges(BoundingBox box, in MatrixD m, Vector4 color, MyStringId material, float thickness, bool ride, uint parent, ref MatrixD worldToLocal)
        {
            box.GetCorners(corners);
            for (int i = 0; i < 8; i++)
                worldCorners[i] = Vector3D.Transform((Vector3D)corners[i], m);
            for (int e = 0; e < Edges.Length; e += 2)
                Line(worldCorners[Edges[e]], worldCorners[Edges[e + 1]], color, material, thickness, ride, parent, ref worldToLocal);
        }

        private static void Line(Vector3D a, Vector3D b, Vector4 color, MyStringId material, float thickness, bool ride, uint parent, ref MatrixD worldToLocal)
        {
            Vector3D along = b - a;
            double length = along.Length();
            if (!(length > 1e-5))
                return;
            var direction = (Vector3)(along / length);
            if (ride)
                MyTransparentGeometry.AddLineBillboard(material, color, a, parent, ref worldToLocal, direction, (float)length, thickness);
            else
                MyTransparentGeometry.AddLineBillboard(material, color, a, direction, (float)length, thickness);
        }

        /// <summary>The model's part of the Perf line: its draw cost a frame and its quads, over the frames it was drawn; empty when it was not.</summary>
        internal static string PerfText()
        {
            int frames = Interlocked.Exchange(ref costFrames, 0);
            long ticks = Interlocked.Exchange(ref costTicks, 0);
            long max = Interlocked.Exchange(ref costMaxTicks, 0);
            int quads = Interlocked.Exchange(ref costQuads, 0);
            if (frames == 0)
                return string.Empty;
            double ms = 1000.0 / Stopwatch.Frequency;
            return string.Format(CultureInfo.InvariantCulture, "; ship model {0:0.000} ms/frame avg / {1:0.000} max, {2:0} quads at level {3} ({4} built)",
                ticks * ms / frames, max * ms, (double)quads / frames, lastLevel, lastQuads);
        }
    }
}
