using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;
using VRage.Game.Entity;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// Test aids for the screens' glass, run from the drive file (<see cref="Drive"/>):
    /// <list type="bullet">
    /// <item><c>touch &lt;entityId&gt; &lt;surface&gt; &lt;x&gt; &lt;y&gt; [down|up|tap]</c> raises <see cref="ScreenTouch.Touched"/> as a finger would;</item>
    /// <item><c>reach &lt;block&gt; &lt;surface&gt; &lt;tl|tr|bl|br|centre|x,y&gt; [past cm] [left|right]</c> moves the desktop hand's
    /// fingertip (<see cref="DesktopHands.PoseOverride"/>) to that pixel of the screen, from 8 cm in front of the glass to
    /// <c>past</c> cm through it (default 5) along its normal in half a second, and holds it there; <c>reach off</c> takes it
    /// back out and lets the hands file have it again. The block is an entity id, <c>seat</c> (the cockpit the character
    /// sits in), or the nearest block with screens whose subtype or name has the text in it;</item>
    /// <item><c>screens [radius m]</c> logs every screen within reach of the eyes (default 2.5 m): its size and how far its
    /// middle is from the eyes and from the dominant shoulder.</item>
    /// </list>
    /// </summary>
    internal static class ScreenTest
    {
        /// <summary>The corners are this far in from the picture's edges (of its size), so the tip lands on the glass.</summary>
        private const float CornerInset = 0.04f;

        private const double ApproachSeconds = 0.5, RetreatSeconds = 0.3;
        private const double FrontMetres = 0.08;

        private sealed class Reach
        {
            public Hand Hand;
            public long BlockId;
            public Vector3 FromModel, ToModel, NormalModel;
            public long Start;
            public double Seconds;
            public bool Retreat;
        }

        private static Reach reach;

        // ---- Drive commands. Each returns true when it has ended, with ok and detail for the drive's line. ----

        internal static bool Step(string verb, string args, int frames, out bool ok, out string detail)
        {
            ok = false;
            detail = null;
            MyCharacter character = MySession.Static?.LocalCharacter;
            if (character == null || character.IsDead)
            {
                detail = "no living character";
                return true;
            }
            switch (verb)
            {
                case "touch": return Touch(args, frames, out ok, out detail);
                case "reach": return ReachCommand(character, args, frames, out ok, out detail);
                case "screens":
                    ok = Table(character, args, out detail);
                    return true;
                default:
                    detail = $"not a screen test command: {verb}";
                    return true;
            }
        }

        private static string touchPhase;
        private static MyCubeBlock touchBlock;
        private static int touchSurface;
        private static Vector2 touchPixel;

        private static bool Touch(string args, int frames, out bool ok, out string detail)
        {
            ok = false;
            detail = null;
            if (frames == 0)
            {
                string[] words = args.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length < 4 || !long.TryParse(words[0], out long id) || !int.TryParse(words[1], out touchSurface)
                    || !float.TryParse(words[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                    || !float.TryParse(words[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
                {
                    detail = "usage: touch <entityId> <surface> <x> <y> [down|up|tap]";
                    return true;
                }
                touchPhase = words.Length > 4 ? words[4].ToLowerInvariant() : "tap";
                if (!MyEntities.TryGetEntityById(id, out MyEntity entity) || !(entity is MyCubeBlock block))
                {
                    detail = $"no block {id}";
                    return true;
                }
                List<MyTextPanelComponent> panels = ScreenGlass.Surfaces(block);
                if (panels == null || touchSurface < 0 || touchSurface >= panels.Count)
                {
                    detail = $"{ScreenGlass.Describe(block)} has {(panels?.Count ?? 0)} surfaces";
                    return true;
                }
                touchBlock = block;
                touchPixel = new Vector2(x, y);
                if (touchPhase != "up")
                    Raise(TouchPhase.Down);
                if (touchPhase == "down" || touchPhase == "up")
                {
                    if (touchPhase == "up")
                        Raise(TouchPhase.Up);
                    ok = true;
                    detail = $"{touchPhase} on {ScreenGlass.Describe(touchBlock)} surface {touchSurface} at ({x:F0},{y:F0})";
                    return true;
                }
                if (touchPhase != "tap")
                {
                    detail = $"'{touchPhase}' is not down, up or tap";
                    return true;
                }
                return false;
            }
            if (frames < 2)
                return false;
            Raise(TouchPhase.Up);
            ok = true;
            detail = $"tap on {ScreenGlass.Describe(touchBlock)} surface {touchSurface} at ({touchPixel.X:F0},{touchPixel.Y:F0})";
            return true;
        }

        private static void Raise(TouchPhase phase)
        {
            Log.Info($"Screen touch (drive): {VRSettings.DominantHand} {phase} on {ScreenGlass.Describe(touchBlock)} ({touchBlock.EntityId}) surface {touchSurface} at pixel ({touchPixel.X:F0},{touchPixel.Y:F0})");
            ScreenTouch.Raise(touchBlock, touchSurface, touchPixel, phase, VRSettings.DominantHand);
        }

        private static bool ReachCommand(MyCharacter character, string args, int frames, out bool ok, out string detail)
        {
            ok = false;
            detail = null;
            string[] words = args.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (frames == 0)
            {
                if (words.Length >= 1 && words[0].Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    if (reach == null)
                    {
                        ok = true;
                        detail = "no reach to take back";
                        return true;
                    }
                    reach.Retreat = true;
                    reach.Start = Stopwatch.GetTimestamp();
                    reach.Seconds = RetreatSeconds;
                    return false;
                }
                reach = null;
                if (words.Length < 3 || !int.TryParse(words[1], out int surface))
                {
                    detail = "usage: reach <entityId|subtype or name text> <surface> <tl|tr|bl|br|centre|x,y> [past cm] [left|right] | reach off";
                    return true;
                }
                float past = 5f;
                Hand hand = VRSettings.DominantHand;
                for (int i = 3; i < words.Length; i++)
                {
                    if (words[i].Equals("left", StringComparison.OrdinalIgnoreCase))
                        hand = Hand.Left;
                    else if (words[i].Equals("right", StringComparison.OrdinalIgnoreCase))
                        hand = Hand.Right;
                    else if (!float.TryParse(words[i], NumberStyles.Float, CultureInfo.InvariantCulture, out past))
                    {
                        detail = $"'{words[i]}' is not a distance in cm or a hand";
                        return true;
                    }
                }
                MyCubeBlock block = Find(character, words[0]);
                if (block == null)
                {
                    detail = $"no block with screens matching '{words[0]}'";
                    return true;
                }
                var screens = new List<ScreenGlass.Screen>();
                int used = 0;
                ScreenGlass.AddScreens(block, screens, new List<ScreenGlass.Screen>(), ref used);
                List<MyTextPanelComponent> panels = ScreenGlass.Surfaces(block);
                if (surface < 0 || surface >= (panels?.Count ?? 0))
                {
                    detail = $"{ScreenGlass.Describe(block)} has {(panels?.Count ?? 0)} surfaces";
                    return true;
                }
                // The side facing the eyes (a transparent screen has two).
                Vector3D eyes = TryEyes(character, out Vector3D e) ? e : character.PositionComp.GetPosition();
                ScreenGlass.Screen screen = screens.Where(s => s.Surface == surface)
                    .OrderByDescending(s => Vector3D.Dot(Vector3D.TransformNormal((Vector3D)s.Sheet.Normal, s.ToWorld),
                        eyes - Vector3D.Transform((Vector3D)s.Sheet.Centre, s.ToWorld)))
                    .FirstOrDefault();
                if (screen == null)
                {
                    // The glass may still be loading: try again for 3 s.
                    if (frames == 0)
                        reachWaitStart = Stopwatch.GetTimestamp();
                    detail = $"the glass of {ScreenGlass.Describe(block)} surface {surface} is not loaded (or has no triangles; see the Screen glass lines)";
                    return false;
                }
                return Begin(character, block, screen, hand, words[2], past, eyes, out ok, out detail);
            }
            if (reach == null)
            {
                // Waiting for the glass to load (see above), at most 3 s.
                if ((Stopwatch.GetTimestamp() - reachWaitStart) / (double)Stopwatch.Frequency > 3.0)
                {
                    detail = "the glass did not load in 3 s";
                    return true;
                }
                return ReachRetry(character, words, out ok, out detail);
            }
            double elapsed = (Stopwatch.GetTimestamp() - reach.Start) / (double)Stopwatch.Frequency;
            if (elapsed < reach.Seconds + 0.15)
                return false;
            ok = true;
            if (reach.Retreat)
            {
                Hand hand = reach.Hand;
                reach = null;
                DesktopHands.SetPoseOverride(hand, null);
                detail = $"{hand} hand back to the hands file";
                return true;
            }
            Vector3 offset = HandStop.Offset(reach.Hand);
            detail = $"{reach.Hand} fingertip held; the drawn palm pushed back {offset.Length() * 100f:F1} cm by the hand stop" +
                     (HandStop.Touching(reach.Hand) ? "" : " (the hand stop has no contact)");
            return true;
        }

        private static long reachWaitStart;

        private static bool ReachRetry(MyCharacter character, string[] words, out bool ok, out string detail)
        {
            ok = false;
            detail = null;
            MyCubeBlock block = Find(character, words[0]);
            int surface = int.Parse(words[1]);
            float past = 5f;
            Hand hand = VRSettings.DominantHand;
            for (int i = 3; i < words.Length; i++)
            {
                if (words[i].Equals("left", StringComparison.OrdinalIgnoreCase))
                    hand = Hand.Left;
                else if (words[i].Equals("right", StringComparison.OrdinalIgnoreCase))
                    hand = Hand.Right;
                else
                    float.TryParse(words[i], NumberStyles.Float, CultureInfo.InvariantCulture, out past);
            }
            var screens = new List<ScreenGlass.Screen>();
            int used = 0;
            ScreenGlass.AddScreens(block, screens, new List<ScreenGlass.Screen>(), ref used);
            Vector3D eyes = TryEyes(character, out Vector3D e) ? e : character.PositionComp.GetPosition();
            ScreenGlass.Screen screen = screens.Where(s => s.Surface == surface)
                .OrderByDescending(s => Vector3D.Dot(Vector3D.TransformNormal((Vector3D)s.Sheet.Normal, s.ToWorld),
                    eyes - Vector3D.Transform((Vector3D)s.Sheet.Centre, s.ToWorld)))
                .FirstOrDefault();
            if (screen == null)
                return false;
            return Begin(character, block, screen, hand, words[2], past, eyes, out ok, out detail);
        }

        private static bool Begin(MyCharacter character, MyCubeBlock block, ScreenGlass.Screen screen, Hand hand, string where, float pastCm, Vector3D eyes,
                                  out bool ok, out string detail)
        {
            ok = false;
            detail = null;
            RectangleF view = ScreenGlass.Viewport(screen.Panel);
            Vector2 a = view.Position, size = view.Size, inset = size * CornerInset;
            Vector2 pixel;
            switch (where.ToLowerInvariant())
            {
                case "tl": pixel = a + inset; break;
                case "tr": pixel = new Vector2(a.X + size.X - inset.X, a.Y + inset.Y); break;
                case "bl": pixel = new Vector2(a.X + inset.X, a.Y + size.Y - inset.Y); break;
                case "br": pixel = a + size - inset; break;
                case "centre":
                case "center": pixel = a + size / 2f; break;
                default:
                    string[] xy = where.Split(',');
                    if (xy.Length != 2 || !float.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out pixel.X)
                        || !float.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out pixel.Y))
                    {
                        detail = $"'{where}' is not tl, tr, bl, br, centre or x,y";
                        return true;
                    }
                    break;
            }
            Vector2 uv = ScreenGlass.UV(screen.Panel, pixel);
            if (!ScreenGlass.PointAt(screen.Sheet, uv, out Vector3 point, out Vector3 normal))
            {
                detail = $"pixel ({pixel.X:F0},{pixel.Y:F0}) (uv {uv.X:F3},{uv.Y:F3}) is not on the glass of {screen.Key}";
                return true;
            }
            reach = new Reach
            {
                Hand = hand,
                BlockId = block.EntityId,
                FromModel = point + normal * (float)FrontMetres,
                ToModel = point - normal * (pastCm / 100f),
                NormalModel = normal,
                Start = Stopwatch.GetTimestamp(),
                Seconds = ApproachSeconds,
            };

            // Where the pixel is on the glass as seen from in front, the character's up being up: a check, from the
            // geometry alone, that x runs right and y runs down the picture.
            MatrixD toWorld = screen.ToWorld;
            Vector3D n = Vector3D.Normalize(Vector3D.TransformNormal((Vector3D)normal, toWorld));
            Vector3D up = character.PositionComp.WorldMatrixRef.Up;
            up = Vector3D.Normalize(up - n * Vector3D.Dot(up, n));
            Vector3D right = Vector3D.Cross(up, n);
            Vector3D centre = Vector3D.Transform((Vector3D)Centre(screen), toWorld);
            Vector3D at = Vector3D.Transform((Vector3D)point, toWorld);
            Vector3D du = Vector3D.TransformNormal((Vector3D)screen.Sheet.AlongU, toWorld), dv = Vector3D.TransformNormal((Vector3D)screen.Sheet.AlongV, toWorld);
            Log.Info($"Screen test: reach {hand} fingertip to {screen.Key} pixel ({pixel.X:F0},{pixel.Y:F0}) of the {view.Position.X:F0},{view.Position.Y:F0} " +
                     $"{size.X:F0}x{size.Y:F0} viewport (uv {uv.X:F3},{uv.Y:F3}), {pastCm:F1} cm through: seen from in front it is " +
                     $"{Vector3D.Dot(at - centre, right) * 100.0:F1} cm right and {Vector3D.Dot(at - centre, up) * 100.0:F1} cm up of the picture's middle; " +
                     $"x grows to the {(Vector3D.Dot(du, right) >= 0 ? "right" : "left")}{(Math.Abs(Vector3D.Dot(Vector3D.Normalize(du), right)) < 0.7 ? " (mostly not)" : "")}, " +
                     $"y grows {(Vector3D.Dot(dv, up) <= 0 ? "down" : "up")}{(Math.Abs(Vector3D.Dot(Vector3D.Normalize(dv), up)) < 0.7 ? " (mostly not)" : "")}; " +
                     $"the glass is {Vector3D.Distance(eyes, at):F2} m from the eyes");
            return false;
        }

        /// <summary>The middle of a screen's picture (model space).</summary>
        private static Vector3 Centre(ScreenGlass.Screen screen) =>
            ScreenGlass.PointAt(screen.Sheet, (screen.Sheet.UVMin + screen.Sheet.UVMax) / 2f, out Vector3 p, out _) ? p : screen.Sheet.Centre;

        private static MyCubeBlock Find(MyCharacter character, string text)
        {
            if (long.TryParse(text, out long id))
                return MyEntities.TryGetEntityById(id, out MyEntity entity) ? entity as MyCubeBlock : null;
            if (text.Equals("seat", StringComparison.OrdinalIgnoreCase))
                return MySession.Static?.ControlledEntity as MyCockpit;
            Vector3D at = character.PositionComp.GetPosition();
            return MyEntities.GetEntities().OfType<MyCubeGrid>()
                .SelectMany(grid => grid.GetFatBlocks())
                .Where(b => ScreenGlass.Surfaces(b) != null
                            && ((b.BlockDefinition?.Id.SubtypeName?.IndexOf(text, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                                || (b.DisplayNameText?.IndexOf(text, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0))
                .OrderBy(b => Vector3D.DistanceSquared(b.PositionComp.GetPosition(), at))
                .FirstOrDefault();
        }

        /// <summary>
        /// Each frame, after the character's simulation: puts the desktop hand where the reach has it, in the tracking
        /// space, from this frame's head (so the fingertip lands on the glass however the body sits).
        /// </summary>
        internal static void Frame(MyCharacter character)
        {
            Reach r = reach;
            if (r == null)
                return;
            if (!MyEntities.TryGetEntityById(r.BlockId, out MyEntity entity) || !(entity is MyCubeBlock block)
                || !HandWorld.TryGetTrackingToBody(character, out MatrixD trackingToModel, out _))
                return;
            double t = Math.Min(1.0, (Stopwatch.GetTimestamp() - r.Start) / (double)Stopwatch.Frequency / r.Seconds);
            double s = t * t * (3.0 - 2.0 * t);
            Vector3 tipModel = r.Retreat ? Vector3.Lerp(r.ToModel, r.FromModel, (float)s) : Vector3.Lerp(r.FromModel, r.ToModel, (float)s);
            MatrixD toWorld = block.PositionComp.WorldMatrixRef;
            Vector3D tip = Vector3D.Transform((Vector3D)tipModel, toWorld);
            Vector3D forward = -Vector3D.Normalize(Vector3D.TransformNormal((Vector3D)r.NormalModel, toWorld));
            Vector3D up = character.PositionComp.WorldMatrixRef.Up;
            up -= forward * Vector3D.Dot(up, forward);
            if (up.LengthSquared() < 1e-6)
                up = Vector3D.CalculatePerpendicularVector(forward);
            up.Normalize();
            // The desktop hand (one pose for grip and aim, no palm pose) turned so that the drawn hand reaches in along
            // forward, wrist to fingertip, the back of the hand up, and placed so that the drawn fingertip is on the point.
            Matrix palmInGrip = HandIK.PalmTarget(r.Hand, new HandState { Tracked = true, Grip = Matrix.Identity, Aim = Matrix.Identity });
            Vector3D tipInGrip = Vector3.Transform(FingerPress.TipInPalm(r.Hand), palmInGrip);
            MatrixD handInGrip = MatrixD.CreateWorld(Vector3D.Zero, tipInGrip - palmInGrip.Translation, (Vector3D)palmInGrip.Up);
            MatrixD world = MatrixD.Transpose(handInGrip) * MatrixD.CreateWorld(Vector3D.Zero, forward, up);
            world.Translation = tip - Vector3D.TransformNormal(tipInGrip, world);
            MatrixD trackingToWorld = trackingToModel * MatrixD.Invert(character.PositionComp.WorldMatrixNormalizedInv);
            DesktopHands.SetPoseOverride(r.Hand, (Matrix)(world * MatrixD.Invert(trackingToWorld)));
        }

        /// <summary>Where the eyes are (world), from this frame's tracked head.</summary>
        private static bool TryEyes(MyCharacter character, out Vector3D eyes)
        {
            eyes = Vector3D.Zero;
            if (!HandWorld.TryGetTrackingToBody(character, out MatrixD trackingToModel, out _) || !GameHead.Take())
                return false;
            MatrixD toWorld = trackingToModel * MatrixD.Invert(character.PositionComp.WorldMatrixNormalizedInv);
            eyes = Vector3D.Transform((Vector3D)GameHead.TrackingPose.Translation, toWorld);
            return true;
        }

        /// <summary>The reach table: each screen near the eyes, how big, and how far from the eyes and the dominant shoulder.</summary>
        private static bool Table(MyCharacter character, string args, out string detail)
        {
            double radius = 2.5;
            if (!string.IsNullOrWhiteSpace(args))
                double.TryParse(args.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out radius);
            if (!TryEyes(character, out Vector3D eyes))
            {
                detail = "no tracked head to measure from";
                return false;
            }
            Hand dominant = VRSettings.DominantHand;
            bool shoulderKnown = HandIK.TryShoulder(dominant, character, out Vector3D shoulder);
            var sphere = new BoundingSphereD(eyes, radius);
            var entities = new List<MyEntity>();
            MyGamePruningStructure.GetAllEntitiesInSphere(ref sphere, entities);
            var screens = new List<ScreenGlass.Screen>();
            var pool = new List<ScreenGlass.Screen>();
            int used = 0, loading = 0;
            foreach (MyEntity entity in entities)
                if (entity is MyCubeBlock block)
                {
                    int before = screens.Count;
                    ScreenGlass.AddScreens(block, screens, pool, ref used);
                    List<MyTextPanelComponent> panels = ScreenGlass.Surfaces(block);
                    if (panels != null && screens.Count == before)
                        loading++;
                }
            string seat = character.IsSitting && MySession.Static?.ControlledEntity is MyCockpit cockpit
                ? $"seated in {ScreenGlass.Describe(cockpit)} '{cockpit.DisplayNameText}' on '{cockpit.CubeGrid.DisplayName}'"
                : "on foot";
            var text = new StringBuilder();
            text.Append($"Screen reach: {seat}, {dominant} shoulder {(shoulderKnown ? $"{Vector3D.Distance(eyes, shoulder):F2} m from the eyes" : "unknown")}; " +
                        $"{screens.Count} screen sheets within {radius:F1} m" + (loading > 0 ? $" ({loading} blocks' glass still loading or none)" : ""));
            foreach (ScreenGlass.Screen s in screens.Where(x => Vector3D.Distance(eyes, Vector3D.Transform((Vector3D)Centre(x), x.ToWorld)) <= radius
                                                                && Vector3D.Dot(Vector3D.TransformNormal((Vector3D)x.Sheet.Normal, x.ToWorld),
                                                                    eyes - Vector3D.Transform((Vector3D)x.Sheet.Centre, x.ToWorld)) > 0)
                                                   .OrderBy(x => Vector3D.DistanceSquared(eyes, Vector3D.Transform((Vector3D)Centre(x), x.ToWorld))))
            {
                Vector3D middle = Vector3D.Transform((Vector3D)Centre(s), s.ToWorld);
                Vector3 du = s.Sheet.AlongU * (s.Sheet.UVMax.X - s.Sheet.UVMin.X), dv = s.Sheet.AlongV * (s.Sheet.UVMax.Y - s.Sheet.UVMin.Y);
                text.Append($"\n  {ScreenGlass.Describe(s.Block)} '{s.Block.DisplayNameText}' ({s.Block.EntityId}) surface {s.Surface} {s.Panel.Name}: " +
                            $"{du.Length() * 100f:F0}x{dv.Length() * 100f:F0} cm, {s.Panel.SurfaceSize.X:F0}x{s.Panel.SurfaceSize.Y:F0} px; middle {Vector3D.Distance(eyes, middle):F2} m from the eyes" +
                            (shoulderKnown ? $", {Vector3D.Distance(shoulder, middle):F2} m from the {dominant} shoulder" : ""));
            }
            Log.Info(text.ToString());
            detail = $"{screens.Count} screen sheets logged ({seat})";
            return true;
        }
    }
}
