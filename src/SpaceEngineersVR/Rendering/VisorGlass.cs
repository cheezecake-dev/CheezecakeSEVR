// VisorGlass: the visor's glass as tinted glass, filtering the light of the world seen through it, in each eye.
using System;
using SpaceEngineersVR.Gui;
using VRageMath;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The helmet's visor glass in the headset: tinted glass between the eyes and the world while the helmet is on and seen from
    /// inside (<see cref="HelmetHud.GlassCover"/>, with the helmet view on, <see cref="HelmetView.On"/>). It covers the view and
    /// swings up with the glass as the helmet opens: the glass's lower edge rises across the view as the glass bone turns, so the
    /// world past it is seen untinted, and comes down again as it closes.
    /// <para>
    /// Physically: tinted glass passes a share of each colour's light (its transmission) and takes the rest. So the glass is
    /// a multiply, drawn over each eye after its scene (<see cref="EyeRenderer"/>, before the vignette and the fades): the eye's
    /// pixels times the transmission, channel by channel, through the game's sprite renderer with a multiply blend state
    /// (<see cref="GameSprites.Multiply"/>). It only takes light away, never adds any, so it can never show more than the game
    /// shows a player without a headset (fairness, docs/DEVELOPMENT.md); and the HUD, projected onto the glass from inside
    /// (<see cref="HudVisor"/>), is a layer of its own over the eyes, and the helmet's inside (<see cref="HelmetInterior"/>) is
    /// drawn after the glass goes on (<see cref="VisorFilters"/>): neither is behind the glass, so neither is tinted.
    /// </para>
    /// <para>
    /// What is outside the helmet but goes to the headset as a layer of its own (the external view's screen, a menu or the wrist
    /// panel standing in the world, the laser's beam) is seen through the visor too (<see cref="Behind"/>, <see cref="CopyThrough"/>),
    /// so nothing outside the helmet shows in front of its glass.
    /// </para>
    /// <para>
    /// The sun visor (<see cref="SunVisor"/>), lowered by hand over the outside of the helmet, is a second pane the same way,
    /// in its own colour (<see cref="VRSettings.SunVisorColour"/>), as dark as <see cref="VRSettings.SunVisorTint"/>, sliding down from the top; it is the helmet's, so it shows with the
    /// glass open too.
    /// </para>
    /// <para>
    /// The settings: <see cref="VRSettings.VisorTintColour"/> (one of <see cref="ColourNames"/>) and <see cref="VRSettings.VisorTint"/>,
    /// how much light it takes on average (by brightness: Rec. 709 luminance), <see cref="DefaultDarkness"/> about the game's own
    /// visor glass (HelmetVisorGlass.png, 13% darker in the middle, the picture the game's HUD definition draws over a flat screen
    /// with the helmet on, commented out in Default.sbc). A colour takes more of some channels and less of others for the same
    /// average (<see cref="Transmission"/>). The desktop window, which shows the left eye, shows the glass too.
    /// </para>
    /// <para>
    /// RenderDebug VisorGlass=0: no glass. Without a multiply blend state (the game's blend state manager not as it was) there
    /// is no glass, logged once; an exception switches it off for a while (<see cref="FeatureGuard"/>).
    /// </para>
    /// </summary>
    internal static class VisorGlass
    {
        /// <summary>Debug: no glass (<see cref="RenderDebug"/> VisorGlass=0).</summary>
        public static bool Off { get; set; }

        /// <summary>The tint's default darkness: about the game's own visor glass.</summary>
        public const float DefaultDarkness = 0.13f;

        /// <summary>The most light the glass may take on average: past this the world is too dark to play in.</summary>
        public const float MostDarkness = 0.6f;

        /// <summary>The colours, in the options screen's order; <see cref="VRSettings.VisorTintColour"/> is an index into it.</summary>
        public static readonly string[] ColourNames = { "Neutral", "Gold", "Blue", "Green", "Rose" };

        /// <summary>
        /// How much of each channel (red, green, blue) a colour takes for each unit of average darkness, before
        /// <see cref="Transmission"/> scales them so their brightness-weighted average is 1: more on a channel, less of it passes.
        /// Gold is the sun visor of a spacesuit, its gold film holding back blue the most.
        /// </summary>
        private static readonly Vector3[] Absorption =
        {
            new Vector3(1f, 1f, 1f),
            new Vector3(0.55f, 1f, 2.6f),
            new Vector3(1.8f, 0.95f, 0.4f),
            new Vector3(1.6f, 0.8f, 1.6f),
            new Vector3(0.4f, 1.2f, 1f),
        };

        /// <summary>Rec. 709 luminance weights: how much each channel counts toward brightness.</summary>
        private static readonly Vector3 Luminance = new Vector3(0.2126f, 0.7152f, 0.0722f);

        /// <summary>No channel passes less than this: a strong colour still shows something in every channel.</summary>
        private const float LeastTransmission = 0.05f;

        /// <summary>
        /// Where the glass's lower edge is down the eye, as a share of its height, closed (past the bottom) and swung fully open
        /// (past the top): it rises across the view between the two as the glass swings.
        /// </summary>
        private const float EdgeClosed = 1.1f, EdgeOpen = -0.1f;

        /// <summary>The glass's lower edge, seen edge on, as a share of the eye's height: a band where the light goes through it twice over.</summary>
        private const float EdgeBand = 0.012f;

        private static readonly FeatureGuard guard = new FeatureGuard(nameof(VisorGlass));
        private static bool missingLogged, logged;

        /// <summary>
        /// The glass's transmission, red, green and blue, for colour <paramref name="colour"/> taking <paramref name="darkness"/> of
        /// the light on average: 1 less darkness times the colour's absorption, the absorption scaled so its brightness-weighted
        /// average is 1, each channel kept between <see cref="LeastTransmission"/> and 1; the darkness no more than
        /// <paramref name="most"/> (the glass's <see cref="MostDarkness"/>, or the sun visor's). Pure.
        /// </summary>
        internal static Vector3 Transmission(int colour, float darkness, float most = MostDarkness)
        {
            Vector3 absorption = Absorption[colour >= 0 && colour < Absorption.Length ? colour : 0];
            absorption /= Vector3.Dot(absorption, Luminance);
            float d = MathHelper.Clamp(darkness, 0f, most);
            return new Vector3(
                MathHelper.Clamp(1f - d * absorption.X, LeastTransmission, 1f),
                MathHelper.Clamp(1f - d * absorption.Y, LeastTransmission, 1f),
                MathHelper.Clamp(1f - d * absorption.Z, LeastTransmission, 1f));
        }

        /// <summary>
        /// Render thread, in an eye's pass, on the world's picture (<see cref="VisorFilters"/>): multiplies the part of <paramref name="eyeTarget"/> (an eye image
        /// <paramref name="eye"/> pixels) the glass covers by its transmission, and then the part the sun visor covers by its
        /// own (<see cref="SunVisor"/>). Never throws.
        /// </summary>
        public static void DrawEye(object eyeTarget, Vector2I eye)
        {
            if (Off || !HelmetView.On || !guard.On || !GameSprites.Ready)
                return;
            float glassCover = HelmetHud.GlassCover, glassDarkness = VRSettings.VisorTint;
            float sunCover = HelmetHud.Worn ? SunVisor.Cover : 0f, sunDarkness = VRSettings.SunVisorTint;
            bool glass = glassCover > 0f && glassDarkness > 0f, sun = sunCover > 0f && sunDarkness > 0f;
            if (!glass && !sun)
                return;
            object multiply = GameSprites.Multiply;
            if (multiply == null)
            {
                if (!missingLogged)
                {
                    missingLogged = true;
                    Log.Warn("Visor glass: the game's renderer gave no multiply blend state, so the visor's glass and sun visor are not tinted");
                }
                return;
            }
            try
            {
                if (glass)
                    Filter(eyeTarget, eye, multiply, Transmission(VRSettings.VisorTintColour, glassDarkness), glassCover);
                if (sun)
                    Filter(eyeTarget, eye, multiply, Transmission(SunVisor.Colour, sunDarkness, SunVisor.MostDarkness), sunCover);
            }
            catch (Exception e)
            {
                guard.Failed(e, "Visor glass could not be drawn");
                return;
            }

            if (!logged && glass)
            {
                logged = true;
                Vector3 pass = Transmission(VRSettings.VisorTintColour, glassDarkness);
                Log.Info($"Visor glass: {ColourNames[Math.Max(0, Math.Min(ColourNames.Length - 1, VRSettings.VisorTintColour))]} at {glassDarkness:P0}, passing " +
                         $"red {pass.X:P0}, green {pass.Y:P0}, blue {pass.Z:P0}, multiplied into each eye while the helmet is on and closed, swinging away with the glass; " +
                         $"the sun visor, when down, {ColourNames[Math.Max(0, Math.Min(ColourNames.Length - 1, SunVisor.Colour))]} at {sunDarkness:P0} over it (RenderDebug VisorGlass=0 turns both off)");
            }
        }

        /// <summary>
        /// The light that comes through the visor for what is outside the helmet but goes to the headset as a layer of its own, not
        /// in the eyes' images (the external view's screen, a menu or the wrist panel standing in the world, the laser's beam): the
        /// glass's and the sun visor's transmissions, each as much as it covers the view, multiplied. White without the helmet view,
        /// the helmet off or both panes up. What is inside the helmet (the HUD on the glass, the helmet's inside) is not behind it.
        /// Any thread.
        /// </summary>
        public static Vector3 Behind
        {
            get
            {
                Vector3 pass = Vector3.One;
                if (Off || !HelmetView.On || !HelmetHud.Worn || GameSprites.Multiply == null)
                    return pass;
                float glassCover = HelmetHud.GlassCover;
                if (glassCover > 0f && VRSettings.VisorTint > 0f)
                    pass *= Vector3.Lerp(Vector3.One, Transmission(VRSettings.VisorTintColour, VRSettings.VisorTint), glassCover);
                float sunCover = SunVisor.Cover;
                if (sunCover > 0f && VRSettings.SunVisorTint > 0f)
                    pass *= Vector3.Lerp(Vector3.One, Transmission(SunVisor.Colour, VRSettings.SunVisorTint, SunVisor.MostDarkness), sunCover);
                return pass;
            }
        }

        /// <summary>
        /// Render thread: copies <paramref name="source"/> (an image <paramref name="size"/> pixels, premultiplied) into the
        /// headset's <paramref name="swapImage"/> as seen through the visor (<see cref="Behind"/>): through a borrowed copy multiplied
        /// by it, so the source itself is never changed (a picture sent again, as the external view's at half rate, is not darkened
        /// twice). <paramref name="light"/> dims it further, as the view's fades dim the world behind it (the external view's screen:
        /// the camera cut and the vignette). Straight across when nothing is in the way. Never throws: on a failure the plain picture goes.
        /// </summary>
        public static void CopyThrough(object source, Vector2I size, SharpDX.Direct3D11.Texture2D swapImage, float light = 1f)
        {
            Vector3 pass = Behind * MathHelper.Clamp(light, 0f, 1f);
            if (pass.X >= 0.999f && pass.Y >= 0.999f && pass.Z >= 0.999f)
            {
                EyeRenderer.CopyToImage(source, swapImage);
                return;
            }
            object copy = null;
            try
            {
                copy = EyeRenderer.Borrow("VR through the visor", size);
                EyeRenderer.CopyOpaque(copy, source, new MyViewport(0f, 0f, size.X, size.Y));
                GameSprites.DrawTextures(copy, size, new Color(pass), GameSprites.Multiply, (GuiTextures.Blank, new RectangleF(0f, 0f, size.X, size.Y)));
                EyeRenderer.CopyToImage(copy, swapImage);
            }
            catch (Exception e)
            {
                guard.Failed(e, "A layer could not be seen through the visor; it goes as it is");
                EyeRenderer.CopyToImage(source, swapImage);
            }
            finally
            {
                if (copy != null)
                    EyeRenderer.Release(copy);
            }
        }

        /// <summary>
        /// One pane over the eye: from the top down to its lower edge, which lies <paramref name="cover"/> of the way from above the
        /// view (0) to below it (1), the eye's pixels times <paramref name="pass"/>; and the edge itself, seen edge on, a band where
        /// the light goes through the pane twice over.
        /// </summary>
        private static void Filter(object eyeTarget, Vector2I eye, object multiply, Vector3 pass, float cover)
        {
            float edge = eye.Y * MathHelper.Lerp(EdgeOpen, EdgeClosed, cover);
            if (edge <= 0f)
                return;
            float band = Math.Min(edge, eye.Y * EdgeBand);
            GameSprites.DrawTextures(eyeTarget, eye, new Color(pass), multiply,
                (GuiTextures.Blank, new RectangleF(0f, 0f, eye.X, Math.Min(edge, eye.Y))));
            if (edge - band < eye.Y)
                GameSprites.DrawTextures(eyeTarget, eye, new Color(pass * pass), multiply,
                    (GuiTextures.Blank, new RectangleF(0f, edge - band, eye.X, band)));
        }
    }
}
