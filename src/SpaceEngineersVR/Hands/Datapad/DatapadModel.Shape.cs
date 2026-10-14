// DatapadModel, its shape: the game's datapad model read from its mesh, the screen found on it, and the picture fitted to the screen.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Sandbox.Definitions;
using VRageMath;
using Sheet = SpaceEngineersVR.Hands.ScreenGlass.Sheet;

namespace SpaceEngineersVR.Hands
{
    internal static partial class DatapadModel
    {
        /// <summary>A material named with one of these is a screen (an LCD's ScreenArea, a cockpit's CockpitScreen_01, a mod's Display).</summary>
        private static readonly string[] ScreenWords = { "screen", "display", "lcd", "monitor" };

        /// <summary>A sheet bowed less than this share of its radius is flat (and never less than a millimetre): a screen, or the face it is on.</summary>
        private const float FlatShare = 0.03f, FlatLeast = 0.001f;

        /// <summary>On a face that is not a screen material of its own, the picture leaves this share of the face's shorter side as a bezel all round.</summary>
        private const float BezelShare = 0.08f;

        /// <summary>The model's mesh as read, in the order the log numbers its sheets (by material name, then by the way they face).</summary>
        private sealed class ModelRead
        {
            public string Asset;
            public List<Sheet> Sheets;
            public string Error;
        }

        /// <summary>The picture on the model's screen.</summary>
        private sealed class Shape
        {
            public string Asset;

            /// <summary>The model's space to the picture's: its middle on the screen's face, x right, y up, +Z out of the screen; model units.</summary>
            public Matrix FromModel;

            /// <summary>The picture's width on the model, model units; its height is the GUI's shape of it.</summary>
            public float Width;

            /// <summary>How far the body reaches left and right of the picture's middle, as shares of the picture's width.</summary>
            public float LeftEdge, RightEdge;

            /// <summary>The sheet it is on, as numbered in the log.</summary>
            public int OnSheet;

            public string Report;
        }

        /// <summary>The game's datapad's model (the first datapad definition that has one): null while the definitions are not loaded, or none has one.</summary>
        private static string DatapadAsset()
        {
            MyDefinitionManager definitions = MyDefinitionManager.Static;
            if (definitions == null)
                return null;
            foreach (MyDatapadDefinition definition in definitions.GetDefinitionsOfType<MyDatapadDefinition>())
            {
                if (!string.IsNullOrEmpty(definition.Model))
                    return definition.Model;
            }
            return null;
        }

        /// <summary>Worker thread: every material's sheets of the model, as the screens' glass is read (<see cref="ScreenGlass.ReadSheets"/>).</summary>
        private static ModelRead Read(string asset)
        {
            var read = new ModelRead { Asset = asset };
            try
            {
                Dictionary<string, List<Sheet>> byMaterial = ScreenGlass.ReadSheets(asset, 1f, _ => true);
                read.Sheets = byMaterial.OrderBy(material => material.Key, StringComparer.OrdinalIgnoreCase)
                    .SelectMany(material => material.Value).ToList();
                if (read.Sheets.Count == 0)
                    read.Error = "no triangles";
            }
            catch (Exception e)
            {
                read.Error = e.GetType().Name + ": " + e.Message;
            }
            return read;
        }

        private static bool Named(Sheet sheet) =>
            ScreenWords.Any(word => sheet.Material.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);

        private static bool Flat(Sheet sheet) => sheet.Flatness <= Math.Max(FlatLeast, FlatShare * sheet.Radius);

        /// <summary>
        /// The sheet the screen is: a flat one of a material named as a screen, else the largest flat face (the front of a pad
        /// whose screen is part of its one material), else the largest; the more triangles between two the same size.
        /// </summary>
        private static int Choose(List<Sheet> sheets, out string why)
        {
            int best = -1;
            for (int i = 0; i < sheets.Count; i++)
            {
                if (best < 0 || Better(sheets[i], sheets[best]))
                    best = i;
            }
            Sheet chosen = sheets[best];
            why = Named(chosen) && Flat(chosen) ? "the largest flat sheet of a screen material"
                : Flat(chosen) ? "the largest flat sheet; no screen material"
                : "the largest sheet; none is flat";
            return best;
        }

        private static bool Better(Sheet a, Sheet b)
        {
            int rankA = (Named(a) && Flat(a) ? 2 : 0) + (Flat(a) ? 1 : 0), rankB = (Named(b) && Flat(b) ? 2 : 0) + (Flat(b) ? 1 : 0);
            if (rankA != rankB)
                return rankA > rankB;
            if (Math.Abs(a.Area - b.Area) > 0.01f * Math.Max(a.Area, b.Area))
                return a.Area > b.Area;
            return a.Tris.Length > b.Tris.Length;
        }

        /// <summary>
        /// The picture fitted to the screen on sheet <paramref name="choice"/> (the chosen one when it is not a sheet's number),
        /// turned <paramref name="turns"/> quarter turns: as large as the screen takes at the GUI's shape (<paramref name="aspect"/>,
        /// height over width), along the screen's long side, upright as the screen's own picture is (against its v, which runs
        /// down a picture). Null when the sheet has no room for it.
        /// </summary>
        private static Shape Fit(ModelRead read, int choice, int turns, float aspect)
        {
            List<Sheet> sheets = read.Sheets;
            string why = "chosen by DatapadSheet";
            int index = choice >= 0 && choice < sheets.Count ? choice : Choose(sheets, out why);
            Sheet sheet = sheets[index];

            Vector3 normal = Vector3.Normalize(sheet.Normal);
            Vector3 right = sheet.AlongU - normal * Vector3.Dot(sheet.AlongU, normal);
            right = right.LengthSquared() > 1e-12f ? Vector3.Normalize(right) : Vector3.CalculatePerpendicularVector(normal);
            Vector3 up = Vector3.Cross(normal, right);
            // A mapping that is mirrored turns the picture's right round with its up.
            if (Vector3.Dot(up, sheet.AlongV) > 0f)
            {
                right = -right;
                up = -up;
            }
            Extent(sheet, sheet.Centre, right, up, normal, out float minRight, out float maxRight, out float minUp, out float maxUp, out _);
            // The long side across, as the GUI is.
            if (maxUp - minUp > maxRight - minRight)
                QuarterTurn(ref right, ref up);
            for (int i = 0; i < (turns % 4 + 4) % 4; i++)
                QuarterTurn(ref right, ref up);

            Extent(sheet, sheet.Centre, right, up, normal, out minRight, out maxRight, out minUp, out maxUp, out float front);
            float faceWidth = maxRight - minRight, faceHeight = maxUp - minUp;
            float inset = Named(sheet) ? 0f : BezelShare * Math.Min(faceWidth, faceHeight);
            float width = Math.Min(faceWidth - 2f * inset, (faceHeight - 2f * inset) / aspect);
            if (!(width > 0f))
                return null;
            Vector3 middle = sheet.Centre + right * (minRight + maxRight) / 2f + up * (minUp + maxUp) / 2f + normal * front;

            // The body beside the picture: every sheet's corners along the picture's x.
            float left = width / 2f, reach = width / 2f;
            foreach (Sheet part in sheets)
            {
                Extent(part, middle, right, up, normal, out float partLeft, out float partRight, out _, out _, out _);
                left = Math.Max(left, -partLeft);
                reach = Math.Max(reach, partRight);
            }

            var shape = new Shape
            {
                Asset = read.Asset,
                FromModel = Matrix.Invert(Matrix.CreateWorld(middle, -normal, up)),
                Width = width,
                LeftEdge = left / width,
                RightEdge = reach / width,
                OnSheet = index,
            };
            shape.Report = Describe(read, shape, why, faceWidth, faceHeight, inset, aspect);
            return shape;
        }

        /// <summary>A quarter turn of the picture on the screen, its face the same way.</summary>
        private static void QuarterTurn(ref Vector3 right, ref Vector3 up)
        {
            Vector3 was = right;
            right = up;
            up = -was;
        }

        /// <summary>How far a sheet's corners reach from <paramref name="from"/> along right and up, and out along the normal.</summary>
        private static void Extent(Sheet sheet, Vector3 from, Vector3 right, Vector3 up, Vector3 normal,
            out float minRight, out float maxRight, out float minUp, out float maxUp, out float front)
        {
            minRight = minUp = float.MaxValue;
            maxRight = maxUp = front = float.MinValue;
            foreach (ScreenGlass.Tri tri in sheet.Tris)
            {
                foreach (Vector3 corner in new[] { tri.A, tri.B, tri.C })
                {
                    Vector3 d = corner - from;
                    float x = Vector3.Dot(d, right), y = Vector3.Dot(d, up), z = Vector3.Dot(d, normal);
                    minRight = Math.Min(minRight, x);
                    maxRight = Math.Max(maxRight, x);
                    minUp = Math.Min(minUp, y);
                    maxUp = Math.Max(maxUp, y);
                    front = Math.Max(front, z);
                }
            }
        }

        private static string Describe(ModelRead read, Shape shape, string why, float faceWidth, float faceHeight, float inset, float aspect)
        {
            var text = new StringBuilder();
            text.Append($"Datapad model: {read.Asset}, {read.Sheets.Count} sheet(s):");
            for (int i = 0; i < read.Sheets.Count; i++)
            {
                Sheet sheet = read.Sheets[i];
                text.Append($" [{i}] {sheet.Material} {ScreenGlass.DescribeSheet(sheet)}, {sheet.Area * 1e4f:F1} cm²{(Flat(sheet) ? ", flat" : string.Empty)};");
            }
            text.Append($" the screen is sheet {shape.OnSheet} ({why}), its face {faceWidth * 100f:F1} x {faceHeight * 100f:F1} cm");
            if (inset > 0f)
                text.Append($" less a bezel of {inset * 100f:F1} cm");
            text.Append($"; the datapad's picture is {shape.Width * 100f:F1} x {shape.Width * aspect * 100f:F1} cm on it, the body reaching " +
                        $"{shape.LeftEdge * shape.Width * 100f:F1} cm left and {shape.RightEdge * shape.Width * 100f:F1} cm right of its middle. " +
                        "RenderDebug DatapadSheet=N puts the picture on sheet N, DatapadTurn=N turns it N quarter turns, DatapadModel=0 shows the flat pad");
            return text.ToString();
        }
    }
}
