// GripSurface: the outside of a cockpit control the hand closes round, measured from the cockpit's own model.
using System;
using VRageMath;
using VRageRender.Messages;

namespace SpaceEngineersVR.Cockpits
{
    /// <summary>
    /// The outside of a control the hand closes round (a stick's grip, a handle's bar), measured once from the installed cockpit
    /// model's mesh of it (<see cref="CockpitRig.Geometry"/>): how far its surface lies from the grip's line, at each turn round
    /// the line (<see cref="Sectors"/>) and each step along it (<see cref="Rows"/> over <see cref="Reach"/> each way from where the
    /// hand holds it). Its triangles are sampled, not only their corners, so a long flat face counts along its length. Turns
    /// with no surface found take their nearest neighbours' in the same step. In the cockpit's rest space, as its mesh and its
    /// rig's contacts are; the control's pose moves both (<see cref="CockpitGrip"/>).
    /// </summary>
    internal sealed class GripSurface
    {
        /// <summary>How far along the line, each way from the contact, the surface is measured, metres: a hand's width and more.</summary>
        internal const float Reach = .07f;

        /// <summary>Steps along the line, and turns round it.</summary>
        internal const int Rows = 28, Sectors = 24;

        /// <summary>Points sampled on each triangle, per side (a triangular grid of them).</summary>
        private const int SamplesPerSide = 4;

        private readonly Vector3 contact, line, across, across2;
        private readonly float[] radius;

        /// <summary>The widest the surface is anywhere measured, metres (for the log).</summary>
        internal float Widest { get; private set; }

        private GripSurface(Vector3 contact, Vector3 line)
        {
            this.contact = contact;
            this.line = Vector3.Normalize(line);
            Vector3 seed = Math.Abs(this.line.Y) < .9f ? Vector3.Up : Vector3.Right;
            across = Vector3.Normalize(Vector3.Cross(this.line, seed));
            across2 = Vector3.Cross(this.line, across);
            radius = new float[Rows * Sectors];
        }

        /// <summary>
        /// Measures <paramref name="mesh"/> (a control's triangles, in the cockpit's rest space) round the line through
        /// <paramref name="contact"/> along <paramref name="line"/>. Null when nothing of it is near enough to the hand's hold.
        /// </summary>
        internal static GripSurface Measure(MyModelData mesh, Vector3 contact, Vector3 line)
        {
            if (mesh == null || mesh.Positions.Count < 3 || !line.IsValid() || line.LengthSquared() < 1e-8f)
                return null;
            var surface = new GripSurface(contact, line);
            var positions = mesh.Positions;
            var indices = mesh.Indices;
            int triangles = (indices.Count > 0 ? indices.Count : positions.Count) / 3;
            for (int t = 0; t < triangles; t++)
            {
                Vector3 a = positions[indices.Count > 0 ? indices[t * 3] : t * 3];
                Vector3 b = positions[indices.Count > 0 ? indices[t * 3 + 1] : t * 3 + 1];
                Vector3 c = positions[indices.Count > 0 ? indices[t * 3 + 2] : t * 3 + 2];
                for (int i = 0; i <= SamplesPerSide; i++)
                    for (int j = 0; i + j <= SamplesPerSide; j++)
                    {
                        float u = i / (float)SamplesPerSide, v = j / (float)SamplesPerSide;
                        surface.Include(a + (b - a) * u + (c - a) * v);
                    }
            }
            return surface.Fill() ? surface : null;
        }

        private void Include(Vector3 point)
        {
            if (!Cell(point, out int row, out int sector, out float distance))
                return;
            int at = row * Sectors + sector;
            if (distance > radius[at])
                radius[at] = distance;
        }

        /// <summary>Each step's turns with nothing found take the nearest found turn's on either side; false when no step has any.</summary>
        private bool Fill()
        {
            bool any = false;
            var row = new float[Sectors];
            for (int r = 0; r < Rows; r++)
            {
                Array.Copy(radius, r * Sectors, row, 0, Sectors);
                for (int s = 0; s < Sectors; s++)
                {
                    if (row[s] > 0f)
                        continue;
                    for (int step = 1; step <= Sectors / 2; step++)
                    {
                        float near = Math.Max(row[(s + step) % Sectors], row[(s - step + Sectors) % Sectors]);
                        if (near > 0f)
                        {
                            radius[r * Sectors + s] = near;
                            break;
                        }
                    }
                }
                for (int s = 0; s < Sectors; s++)
                    any |= radius[r * Sectors + s] > 0f;
            }
            foreach (float value in radius)
                Widest = Math.Max(Widest, value);
            return any;
        }

        /// <summary>The step and turn a point is in, and how far it is from the line; false outside the measured length.</summary>
        private bool Cell(Vector3 point, out int row, out int sector, out float distance)
        {
            Vector3 offset = point - contact;
            float along = Vector3.Dot(offset, line);
            Vector3 radial = offset - line * along;
            distance = radial.Length();
            row = (int)Math.Floor((along + Reach) / (2f * Reach) * Rows);
            float turn = (float)Math.Atan2(Vector3.Dot(radial, across2), Vector3.Dot(radial, across));
            sector = (int)Math.Floor((turn + MathHelper.Pi) / MathHelper.TwoPi * Sectors) % Sectors;
            if (sector < 0)
                sector += Sectors;
            return row >= 0 && row < Rows;
        }

        /// <summary>
        /// How far <paramref name="point"/> (rest space) is outside the surface, metres: less than 0 inside it. Positive infinity
        /// past the measured length, or where no surface was found at that step.
        /// </summary>
        internal float Clearance(Vector3 point)
        {
            if (!Cell(point, out int row, out int sector, out float distance))
                return float.PositiveInfinity;
            float surface = radius[row * Sectors + sector];
            return surface > 0f ? distance - surface : float.PositiveInfinity;
        }

        /// <summary>The way out from the line to <paramref name="point"/>, square to the line (rest space); zero on the line.</summary>
        internal Vector3 Outward(Vector3 point)
        {
            Vector3 offset = point - contact;
            Vector3 radial = offset - line * Vector3.Dot(offset, line);
            float length = radial.Length();
            return length > 1e-6f ? radial / length : Vector3.Zero;
        }
    }
}
