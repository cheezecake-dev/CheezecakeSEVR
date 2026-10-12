using System;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Gui;
using Sandbox.Game.Screens.Helpers;
using SpaceEngineersVR.Input;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    internal static partial class ShipModel
    {
        // ---- Pointing at a block ----
        // The dominant hand (the other one when it holds the model) points with its fingertip in a block of the model, or
        // with its ray at one. That block is lit on the model, the real one outlined in the world, and a marker beside it
        // gives its name and integrity: one of the HUD's own world markers (the one the game puts on a button panel's buttons).

        /// <summary>How far the pointing ray reaches into the model, metres.</summary>
        private const float PointReach = 1.5f;

        private const float TickAmplitude = 0.12f, TickSeconds = 0.01f;

        /// <summary>What is pointed at: the part (grid) and block, -1 for none; and how (the fingertip or the ray).</summary>
        private static int pointedPart = -1, pointedBlock = -1;
        private static bool pointedByTip;
        private static Hand pointer;

        /// <summary>Where the ray meets the model, in the tracking space, while it points by ray.</summary>
        private static Vector3 pointedAt;

        private static MyGps marker;
        private static readonly Common.LineThrottle pointing = new Common.LineThrottle(2);

        private static void Point()
        {
            if (!PointerHand(out pointer) || fade < 1f)
            {
                Unpoint();
                return;
            }
            HandState state = VRInput.Get(pointer);
            int part = -1, block = -1;
            bool byTip = false;

            // The fingertip first: inside a block is pointing at it, whatever the ray says.
            Vector3 tip = FingerPress.FingertipInTracking(pointer, state);
            for (int g = 0; g < parts.Count && block < 0; g++)
            {
                ShipShape shape = parts[g].Shape;
                if (shape == null)
                    continue;
                Matrix toGrid = Matrix.Invert(GridToModel(parts[g].Grid) * ModelToTracking);
                int found = ShipMesh.BlockAt(shape, Vector3.Transform(tip, toGrid));
                if (found >= 0)
                {
                    part = g;
                    block = found;
                    byTip = true;
                }
            }

            if (block < 0)
            {
                Vector3 origin = state.Aim.Translation, direction = Vector3.Normalize(state.Aim.Forward);
                float nearest = PointReach;
                for (int g = 0; g < parts.Count; g++)
                {
                    ShipShape shape = parts[g].Shape;
                    if (shape == null)
                        continue;
                    Matrix gridToTracking = GridToModel(parts[g].Grid) * ModelToTracking;
                    Matrix toGrid = Matrix.Invert(gridToTracking);
                    Vector3 o = Vector3.Transform(origin, toGrid), d = Vector3.TransformNormal(direction, toGrid);
                    float perMetre = d.Length();
                    if (!(perMetre > 1e-6f))
                        continue;
                    int found = ShipMesh.BlockAlong(shape, o, d / perMetre, nearest * perMetre, out float distance);
                    if (found < 0)
                        continue;
                    nearest = distance / perMetre;
                    part = g;
                    block = found;
                }
                if (block >= 0)
                    pointedAt = origin + direction * nearest;
            }

            if (part != pointedPart || block != pointedBlock)
            {
                pointedPart = part;
                pointedBlock = block;
                pointedByTip = byTip;
                if (block >= 0)
                {
                    Haptics.Pulse(pointer, TickAmplitude, TickSeconds);
                    pointing.Info($"Ship model: {(byTip ? "touching" : "pointing at")} {Label()} with the {Name(pointer)} hand");
                }
            }
            UpdateMarker();
        }

        /// <summary>The hand that points: the dominant one, or the other while the dominant one holds the model; none while both do.</summary>
        private static bool PointerHand(out Hand hand)
        {
            hand = VRSettings.DominantHand;
            if (held[(int)hand])
                hand = hand == Hand.Left ? Hand.Right : Hand.Left;
            return !held[(int)hand] && VRInput.Active && VRInput.Get(hand).Tracked;
        }

        private static void Unpoint()
        {
            pointedPart = pointedBlock = -1;
            if (marker != null)
                MyHud.ButtonPanelMarkers.UnregisterMarker(marker);
            marker = null;
        }

        /// <summary>The block pointed at, if any: its grid's part, its block, and the real block (null when it is gone).</summary>
        private static bool TryPointed(out Part part, out ShipBlock block, out MySlimBlock slim)
        {
            part = null;
            block = default;
            slim = null;
            if (pointedPart < 0 || pointedPart >= parts.Count)
                return false;
            part = parts[pointedPart];
            if (part.Shape == null || pointedBlock < 0 || pointedBlock >= part.Shape.Blocks.Length)
                return false;
            block = part.Shape.Blocks[pointedBlock];
            slim = pointedBlock < part.Slims.Length ? part.Slims[pointedBlock] : null;
            if (slim != null && (slim.CubeGrid != part.Grid || slim.IsDestroyed))
                slim = null;
            return true;
        }

        /// <summary>The pointed block's name and integrity, as the marker shows it.</summary>
        private static string Label()
        {
            if (!TryPointed(out Part part, out ShipBlock block, out MySlimBlock slim))
                return string.Empty;
            if (slim == null)
            {
                string name = pointedBlock < part.Names.Length ? part.Names[pointedBlock] : null;
                return $"{name ?? "Block"}: {(block.Gone ? "destroyed" : "gone")}";
            }
            float integrity = slim.MaxIntegrity > 0f ? slim.Integrity / slim.MaxIntegrity : 1f;
            return $"{NameOf(slim)}: {Math.Floor(integrity * 100f):0}%";
        }

        /// <summary>The real block's box in the world: its grid's cells, a little larger so the outline stands clear of it.</summary>
        private static bool TryPointedWorld(out MatrixD box)
        {
            box = MatrixD.Identity;
            if (!TryPointed(out Part part, out ShipBlock block, out _) || part.Grid.Closed)
                return false;
            BoundingBox local = ShipMesh.BoxOf(block, part.Shape.CellSize);
            Vector3 size = local.Size + new Vector3(part.Shape.CellSize * 0.04f);
            box = MatrixD.CreateScale(size) * MatrixD.CreateTranslation(local.Center) * part.Grid.WorldMatrix;
            return true;
        }

        /// <summary>The marker beside the real block: put up, moved with the block and its label kept current; taken down when nothing is pointed at.</summary>
        private static void UpdateMarker()
        {
            if (!TryPointedWorld(out MatrixD box))
            {
                if (marker != null)
                    MyHud.ButtonPanelMarkers.UnregisterMarker(marker);
                marker = null;
                return;
            }
            if (marker == null)
                marker = new MyGps { ShowOnHud = true, AlwaysVisible = true };
            marker.Name = Label();
            marker.Coords = box.Translation;
            MyHud.ButtonPanelMarkers.RegisterMarker(marker);
        }
    }
}
