// ComfortGrid: a faint grid at the player's real floor while the jetpack flies in zero g, a still frame against motion sickness.
using System;
using System.Diagnostics;
using HarmonyLib;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SpaceEngineersVR.Tracking;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The comfort grid: on the jetpack in zero g, a faint grid of thin lines at the player's real floor, level with the body (the
    /// floor's plane in the tracking space). It stays put around the player while the jetpack flies, pitches and rolls the body and
    /// the world turns past, so the eyes have something still to rest on, as a cockpit is to a pilot; that eases motion sickness
    /// for some people. Setting: Comfort grid (off by default). RenderDebug ComfortGrid=0 turns it off.
    /// </summary>
    /// <remarks>
    /// The lines are <see cref="Spacing"/> apart and fixed in the tracking space, and only those within <see cref="Radius"/> of the
    /// point under the head are drawn, each in short pieces that fade towards the edge: walking about the room moves the lit patch,
    /// never the lines. The floor is the headset's (its STAGE space, <see cref="GameHead.OriginAboveFloor"/>), else the eye height
    /// (the calibrated one, or the character's) below where the head is at the character's eye (<see cref="GameHead.HeightReference"/>).
    /// It fades in and out over <see cref="FadeSeconds"/> as the jetpack, zero g or the setting come and go, and is not drawn at all
    /// while the view is not the character's own eyes with the head tracked (a seat, third person, the spectator, a camera block,
    /// the forced view). Drawn as the laser's line is: billboards from the gameplay screen's draw, placed from the tracking space
    /// into the world (<see cref="HandWorld.TryGetTrackingToWorld"/>) and given in the character's space
    /// (<see cref="MotionExtrapolation.TryRide"/>), so they move with the view when it is moved on past the game frame; both eyes
    /// draw them.
    /// </remarks>
    internal static class ComfortGrid
    {
        /// <summary>Debug: no grid, whatever the setting (<see cref="RenderDebug"/> ComfortGrid=0).</summary>
        public static bool Off { get; set; }

        /// <summary>How far apart the lines are, metres.</summary>
        private const float Spacing = 1f;

        /// <summary>How far from the point under the head the lines reach, metres.</summary>
        private const float Radius = 3.5f;

        /// <summary>The outer share of <see cref="Radius"/> over which the lines fade out; inside it they are at full strength.</summary>
        private const float FadeShare = 0.6f;

        /// <summary>Each line is drawn in pieces this long (metres, on fixed marks along it), each as strong as its middle's distance from the head gives.</summary>
        private const float Piece = 0.25f;

        /// <summary>The lines' width, metres: about two pixels at the floor straight below.</summary>
        private const float Thickness = 0.005f;

        /// <summary>The lines' colour, and how opaque they are at full strength.</summary>
        private static readonly Vector3 Colour = new Vector3(0.6f, 0.85f, 1f);

        private const float Alpha = 0.25f;

        /// <summary>Fading in, or out, takes this long, seconds.</summary>
        public const float FadeSeconds = 0.3f;

        /// <summary>The eye's height above the floor with no floor from the headset and no calibration: the character model's, metres (as <see cref="ZeroGPivot"/> has it).</summary>
        private const float DefaultEyeHeight = 1.6f;

        /// <summary>The longest time one frame's fade is worked out for, seconds, so a stall does not finish a fade in one step.</summary>
        private const double MaxStep = 0.1;

        // Not ignoring depth: the body, the hands and a hull in the way hide it, as they would a floor.
        private static readonly MyStringId LineMaterial = MyStringId.GetOrCompute("Square");

        private static readonly FeatureGuard guard = new FeatureGuard(nameof(ComfortGrid));
        private static float level;
        private static long lastDraw;
        private static bool shownLogged;

        public static void Patch(Harmony harmony)
        {
            // The gameplay screen's draw (only when there is a scene), as the laser's line: billboards added up to the end of the
            // game's frame are drawn in it, in both eyes.
            harmony.Patch(AccessTools.Method(typeof(MySession), nameof(MySession.DrawSync)),
                postfix: new HarmonyMethod(typeof(ComfortGrid), nameof(Draw)));
        }

        /// <summary>Game thread, in the gameplay screen's draw: moves the fade on and draws the grid as strong as it has got.</summary>
        private static void Draw()
        {
            long now = Stopwatch.GetTimestamp();
            float seconds = lastDraw != 0 ? (float)Math.Min((now - lastDraw) / (double)Stopwatch.Frequency, MaxStep) : 0f;
            lastDraw = now;
            if (!guard.On)
                return;
            try
            {
                MyCharacter character = Viewer();
                if (character == null)
                {
                    level = 0f;
                    return;
                }
                bool wanted = !Off && VRSettings.ComfortGrid && character.JetpackRunning
                              && character.Gravity.LengthSquared() <= Input.VRControls.ZeroGSquared;
                level = Common.Ease.Approach(level, wanted ? 1f : 0f, seconds / FadeSeconds);
                if (level <= 0f || !HandWorld.TryGetTrackingToWorld(out MatrixD toWorld))
                    return;
                Lines(toWorld, (float)Common.Ease.Smooth01(level));
            }
            catch (Exception e)
            {
                guard.Failed(e, "Comfort grid could not be drawn");
            }
        }

        /// <summary>The local character, alive, while the view is its own eyes with the head tracked: the one view the grid belongs in. Null otherwise.</summary>
        private static MyCharacter Viewer()
        {
            MySession session = MySession.Static;
            MyCharacter character = session?.LocalCharacter;
            if (character == null || character.IsDead || character.Closed || !ReferenceEquals(session.CameraController, character)
                || !CharacterHead.IsLocalFirstPerson(character) || ForcedView.Showing || !MyTransparentGeometry.HasCamera || !GameHead.Take())
                return null;
            return character;
        }

        /// <summary>
        /// The lines x = n and z = n metres of the tracking space, at the floor, within <see cref="Radius"/> of the point under the head:
        /// each one cut to the circle, in pieces on fixed marks <see cref="Piece"/> apart, each faded by its distance from the head.
        /// </summary>
        /// <param name="toWorld">The tracking space to the world.</param>
        /// <param name="strength">The fade in or out, 0 to 1.</param>
        private static void Lines(in MatrixD toWorld, float strength)
        {
            float floor = Floor();
            Vector3 head = GameHead.TrackingPose.Translation;
            bool ride = MotionExtrapolation.TryRide(out uint parent, out MatrixD worldToLocal);
            // The tracking space's x and z in the world. toWorld is a turn and a move (HandWorld.Compose), so a length along them
            // in the tracking space is the same length in the world.
            var worldX = (Vector3)Vector3D.Normalize(toWorld.Right);
            var worldZ = (Vector3)Vector3D.Normalize(toWorld.Backward);
            int drawn = 0;
            for (int axis = 0; axis < 2; axis++)
            {
                // axis 0: lines at x = n running along z; axis 1: lines at z = n running along x.
                float across = axis == 0 ? head.X : head.Z, along = axis == 0 ? head.Z : head.X;
                Vector3 direction = axis == 0 ? worldZ : worldX;
                int first = (int)Math.Ceiling((across - Radius) / Spacing), last = (int)Math.Floor((across + Radius) / Spacing);
                for (int n = first; n <= last; n++)
                {
                    float at = n * Spacing, off = at - across;
                    float half = (float)Math.Sqrt(Math.Max(Radius * Radius - off * off, 0f));
                    float start = along - half, end = along + half;
                    for (int p = (int)Math.Floor(start / Piece), stop = (int)Math.Ceiling(end / Piece); p < stop; p++)
                    {
                        float a = Math.Max(p * Piece, start), b = Math.Min((p + 1) * Piece, end);
                        if (b - a < 0.01f)
                            continue;
                        float mid = (a + b) * 0.5f - along;
                        float fade = Edge((float)Math.Sqrt(off * off + mid * mid) / Radius) * strength;
                        if (fade <= 0.002f)
                            continue;
                        Vector3D origin = Vector3D.Transform(axis == 0 ? new Vector3D(at, floor, a) : new Vector3D(a, floor, at), toWorld);
                        var color = new Vector4(Colour, 1f) * (Alpha * fade);
                        if (ride)
                            MyTransparentGeometry.AddLineBillboard(LineMaterial, color, origin, parent, ref worldToLocal, direction, b - a, Thickness);
                        else
                            MyTransparentGeometry.AddLineBillboard(LineMaterial, color, origin, direction, b - a, Thickness);
                        drawn++;
                    }
                }
            }
            if (shownLogged || drawn == 0)
                return;
            shownLogged = true;
            Log.Info($"Comfort grid: shown on the jetpack in zero g, lines {Spacing:F0} m apart out to {Radius:F1} m at the floor {head.Y - floor:F2} m below the eyes " +
                     $"({FloorSource()}), {drawn} pieces; RenderDebug ComfortGrid=0 hides it");
        }

        /// <summary>How strong a piece this far out is (a share of <see cref="Radius"/>): full inside the fade, easing to nothing at the edge.</summary>
        private static float Edge(float distance)
        {
            float inner = 1f - FadeShare;
            return distance <= inner ? 1f : (float)Common.Ease.Smooth01((1f - distance) / FadeShare);
        }

        /// <summary>The floor's height in the tracking space: the headset's floor, else the eye height below where the head is at the character's eye.</summary>
        private static float Floor()
        {
            float? originAboveFloor = GameHead.OriginAboveFloor;
            if (originAboveFloor.HasValue)
                return -originAboveFloor.Value;
            return GameHead.HeightReference - (VRSettings.EyeHeight ?? DefaultEyeHeight);
        }

        /// <summary>Where <see cref="Floor"/> found the floor, for the log.</summary>
        private static string FloorSource()
        {
            if (GameHead.OriginAboveFloor.HasValue)
                return "the headset's floor";
            float? eyeHeight = VRSettings.EyeHeight;
            return eyeHeight.HasValue
                ? $"no floor from the headset: the calibrated eye height, {eyeHeight.Value:F2} m, below the character's eye"
                : $"no floor from the headset and no calibration: {DefaultEyeHeight:F1} m below the character's eye";
        }
    }
}
