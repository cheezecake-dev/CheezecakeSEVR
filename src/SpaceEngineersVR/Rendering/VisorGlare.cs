// VisorGlare: sunlight scattered in the visor's glass when you look toward the sun, which the sun visor cuts.
using System;
using System.Diagnostics;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using SpaceEngineersVR.Gui;
using VRageMath;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// Looking toward the sun through the closed visor, its light scatters in the glass: a soft bloom round the sun and a veil of
    /// haze over the whole view, strongest looking straight at it and gone well away from it. Lowering the sun visor
    /// (<see cref="SunVisor"/>) cuts it, as a sun visor is for; opening the glass takes it away with the glass
    /// (<see cref="HelmetHud.GlassCover"/>). It is what gives the sun visor its use.
    /// <para>
    /// Only where the sun is really seen: the game thread (<see cref="Sample"/>, ten times a second) looks along the direction to
    /// the sun (MySector.DirectionToSunNormalized) from the eyes: a physics ray <see cref="RayLength"/> out for ships, stations,
    /// asteroids and terrain, and the nearest planet as a sphere of its least radius for night. Drawn in each eye after its scene
    /// and before the glass's own tint (<see cref="EyeRenderer"/>): the bloom from a texture of our own at the sun's place in that
    /// eye, scaled down in a small image and added, and the haze added over the whole eye (<see cref="GameSprites.Add"/>), so the
    /// glass and the sun visor then filter it as they filter the world.
    /// </para>
    /// <para>
    /// Fairness (docs/DEVELOPMENT.md): it only comes looking near the sun, where the game already blooms and flares for every
    /// player; the sun visor takes it away, and <see cref="VRSettings.VisorGlare"/> sets how strong it is, down to none. It never
    /// shows anything; the sun's place is where the game draws the sun. RenderDebug VisorGlare=0 turns it off.
    /// </para>
    /// </summary>
    internal static class VisorGlare
    {
        /// <summary>Debug: no glare (<see cref="RenderDebug"/> VisorGlare=0).</summary>
        public static bool Off { get; set; }

        /// <summary>How far the ray toward the sun looks for something in the way, metres.</summary>
        private const double RayLength = 3000.0;

        /// <summary>The ray starts this far out, past the character's own body.</summary>
        private const double RayStart = 1.0;

        /// <summary>The sun's look is taken again this often, seconds.</summary>
        private const double SampleEvery = 0.1;

        /// <summary>
        /// The glare's strength by the sun's distance from the middle of the eye, in the eye's normalized screen units (1 at its
        /// edge): full within the first, gone past the second.
        /// </summary>
        private const float FullWithin = 0.15f, GoneAt = 1.8f;

        /// <summary>The strongest bloom and haze, as shares of full white added at the brightest point and over the view.</summary>
        private const float Bloom = 0.55f, Haze = 0.1f;

        /// <summary>How much of the glare the sun visor takes when down, on top of its tint.</summary>
        private const float SunVisorCuts = 0.9f;

        /// <summary>The bloom's size, as a share of the eye's width, and the small image it is scaled in, as a share of the eye.</summary>
        private const float BloomSize = 1.1f, Small = 0.25f;

        /// <summary>Sunlight's colour through the glass: a little warm.</summary>
        private static readonly Vector3 Sunlight = new Vector3(1f, 0.95f, 0.85f);

        private const int GlowSize = 256;
        private static readonly GeneratedTexture glow = new GeneratedTexture("VR visor glare", GlowSize, GlowPixels);
        private static readonly FeatureGuard guard = new FeatureGuard(nameof(VisorGlare));

        // Game thread to render thread: the sun's direction in the world and whether it is seen from the eyes.
        private static volatile bool seen;
        private static Vector3 toSun;
        private static readonly object gate = new object();
        private static long sampledAt;
        private static bool logged;

        /// <summary>Game thread, every frame the game draws (<see cref="HelmetState"/>): where the sun is and whether anything hides it, ten times a second.</summary>
        public static void Sample()
        {
            long now = Stopwatch.GetTimestamp();
            if (now - sampledAt < (long)(SampleEvery * Stopwatch.Frequency))
                return;
            sampledAt = now;
            if (Off || !HelmetView.On || VRSettings.VisorGlare <= 0f || !HelmetHud.Worn || !guard.On)
            {
                seen = false;
                return;
            }
            try
            {
                var camera = MySector.MainCamera;
                if (camera == null)
                {
                    seen = false;
                    return;
                }
                Vector3 sun = MySector.DirectionToSunNormalized;
                Vector3D eyes = camera.Position, way = sun;
                bool visible = sun.LengthSquared() > 0.5f && !Planet(eyes, way)
                               && !MyPhysics.CastRay(eyes + way * RayStart, eyes + way * RayLength).HasValue;
                lock (gate)
                    toSun = sun;
                seen = visible;
            }
            catch (Exception e)
            {
                seen = false;
                guard.Failed(e, "Visor glare could not find the sun");
            }
        }

        /// <summary>
        /// The nearest planet is between the eyes and the sun: the ray toward the sun meets its least radius. A planet whose shape
        /// is not there yet (MinimumRadius 0 until its shape provider is) counts as in the way, so no glare shows through a planet
        /// still loading; it lasts only while it loads.
        /// </summary>
        private static bool Planet(Vector3D eyes, Vector3D sun)
        {
            MyPlanet planet = MyGamePruningStructure.GetClosestPlanet(eyes);
            if (planet == null)
                return false;
            double radius = planet.MinimumRadius;
            if (radius <= 0.0)
                return true;
            Vector3D toCentre = planet.PositionComp.GetPosition() - eyes;
            double along = Vector3D.Dot(toCentre, sun);
            if (along <= 0.0)
                return false;
            return toCentre.LengthSquared() - along * along < radius * radius;
        }

        /// <summary>
        /// Render thread, in an eye's pass, on the world's picture before the glass (<see cref="VisorFilters"/>, <see cref="VisorGlass"/>): adds the glare to
        /// <paramref name="eyeTarget"/> (an eye image <paramref name="eye"/> pixels) seen through <paramref name="viewProjection"/>
        /// (the eye's, at the camera). Never throws.
        /// </summary>
        public static void DrawEye(object eyeTarget, Vector2I eye, Matrix viewProjection)
        {
            if (Off || !seen || !HelmetView.On || !guard.On || !GameSprites.Ready || GameSprites.Add == null || GameSprites.Multiply == null)
                return;
            try
            {
                Vector3 sun;
                lock (gate)
                    sun = toSun;
                Vector4 clip = Vector4.Transform(new Vector4(sun, 0f), viewProjection);
                if (clip.W <= 1e-4f)
                    return;
                var at = new Vector2(clip.X / clip.W, clip.Y / clip.W);
                float strength = VRSettings.VisorGlare * HelmetHud.GlassCover * (1f - SunVisorCuts * SunVisor.Cover)
                                 * (1f - Smooth((at.Length() - FullWithin) / (GoneAt - FullWithin)));
                if (strength <= 0.003f)
                    return;

                // The bloom, in a small image: the glow at the sun's place there, times its strength, then added over the eye.
                var small = new Vector2I(Math.Max(16, (int)(eye.X * Small)), Math.Max(16, (int)(eye.Y * Small)));
                float px = (at.X * 0.5f + 0.5f) * small.X, py = (0.5f - at.Y * 0.5f) * small.Y, side = BloomSize * small.X;
                object made = glow.Get();
                if (made != null)
                {
                    object image = EyeRenderer.Borrow("VR visor glare", small);
                    try
                    {
                        EyeRenderer.Clear(image);
                        EyeRenderer.Blend(image, made, new MyViewport(px - side / 2f, py - side / 2f, side, side));
                        float bloom = Math.Min(1f, strength * Bloom);
                        GameSprites.DrawTextures(image, small, new Color(bloom, bloom, bloom, 1f), GameSprites.Multiply,
                            (GuiTextures.Blank, new RectangleF(0f, 0f, small.X, small.Y)));
                        // Its alpha is none, so drawn premultiplied over the eye it is added.
                        EyeRenderer.Blend(eyeTarget, image, new MyViewport(0f, 0f, eye.X, eye.Y));
                    }
                    finally
                    {
                        EyeRenderer.Release(image);
                    }
                }

                // The haze, over the whole eye.
                GameSprites.DrawTextures(eyeTarget, eye, new Color(Sunlight * (strength * Haze)), GameSprites.Add,
                    (GuiTextures.Blank, new RectangleF(0f, 0f, eye.X, eye.Y)));
            }
            catch (Exception e)
            {
                guard.Failed(e, "Visor glare could not be drawn");
                return;
            }
            if (!logged)
            {
                logged = true;
                Log.Info("Visor glare: the sun is in view through the closed visor; its light scatters in the glass until the sun visor is lowered " +
                         "(Options > VR > Display > Visor glare; RenderDebug VisorGlare=0)");
            }
        }

        /// <summary>The glow's texels: sunlight, premultiplied with no alpha (so it adds), a bright core and a wide soft halo.</summary>
        private static byte[] GlowPixels(int size)
        {
            var texels = new byte[size * size * 4];
            for (int j = 0; j < size; j++)
            {
                float y = 2f * (j + 0.5f) / size - 1f;
                for (int i = 0; i < size; i++)
                {
                    float x = 2f * (i + 0.5f) / size - 1f;
                    float r = (float)Math.Sqrt(x * x + y * y);
                    float light = r >= 1f ? 0f : ((float)Math.Exp(-r * r * 40f) * 0.8f + (float)Math.Exp(-r * 5f) * 0.35f) * (1f - Smooth((r - 0.8f) / 0.2f));
                    int at = (j * size + i) * 4;
                    texels[at] = (byte)Math.Round(Math.Min(1f, light * Sunlight.X) * 255f);
                    texels[at + 1] = (byte)Math.Round(Math.Min(1f, light * Sunlight.Y) * 255f);
                    texels[at + 2] = (byte)Math.Round(Math.Min(1f, light * Sunlight.Z) * 255f);
                }
            }
            return texels;
        }

        private static float Smooth(float t)
        {
            t = t < 0f ? 0f : t > 1f ? 1f : t;
            return t * t * (3f - 2f * t);
        }
    }
}
