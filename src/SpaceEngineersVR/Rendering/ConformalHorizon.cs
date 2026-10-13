// ConformalHorizon: the cockpit's artificial horizon drawn on the real horizon, as an aircraft's head-up display draws it, and the altitude under the crosshair.
using System;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using Sandbox.Graphics;
using VRage.Game.Utils;
using VRage.Utils;
using VRageMath;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The game's artificial horizon and altitude (a cockpit's "show horizon and altitude", in gravity) in the headset, drawn
    /// conformal: the horizon line lies over the real horizon as seen through the HUD, turned as the horizon is turned from the
    /// eyes (so it stays on the horizon when the head turns or tilts), the altitude just under the ship's crosshair. On a flat
    /// screen the game draws the line at the middle of the screen, moved by 0.35 of the screen's height for each unit of the
    /// sine of the ship's pitch and turned by the ship's roll; with the HUD on a panel that spans
    /// <see cref="VRSettings.HudWidthDegrees"/> and turns with the head, that line would not lie on the horizon.
    /// <para>
    /// Adapted, not new: the game's own art (MyGuiConstants.TEXTURE_HUD_GRAVITY_HORIZON at its size, 0.4 at 1080 rows), its
    /// altitude (MyGuiScreenHudSpace.GetHeightAboveSurface, rounded to the metre, in the "Blue" font at 0.8) and its conditions
    /// (the controller's HorizonIndicatorEnabled, the grid's physics, natural gravity of at least 0.05 m/s²). What it shows is
    /// what the game shows (pitch, roll, altitude), only placed where it points; nothing more (fairness, docs/DEVELOPMENT.md).
    /// The layout was checked against ColbyJack1134's description of the game's (ShipCrosshair.Horizon, used as a reference
    /// only).
    /// </para>
    /// <para>
    /// Replaces MyGuiScreenHudSpace.DrawArtificialHorizonAndAltitude while the picture is VR's (<see cref="StereoPatches.VRPicture"/>)
    /// and the HUD projects through the panel (<see cref="HudCamera"/>); otherwise the game's own runs. The sprites go on the flat
    /// panel with the crosshair and the markers. RenderDebug ConformalHorizon=0: the game's own.
    /// </para>
    /// </summary>
    internal static class ConformalHorizon
    {
        /// <summary>Debug: the game's own horizon (<see cref="RenderDebug"/> ConformalHorizon=0).</summary>
        public static bool Off { get; set; }

        private const string GameMethod = "DrawArtificialHorizonAndAltitude";

        /// <summary>The game's horizon art at 1080 rows is this share of its size; its altitude text this scale, in this font.</summary>
        private const float ArtScale = 0.4f, TextScale = 0.8f;

        private const string Font = "Blue";

        /// <summary>The altitude's middle is this share of the screen's height under the crosshair, as the game puts it.</summary>
        private const float AltitudeBelow = 0.03f;

        /// <summary>Natural gravity under this (squared, m/s²) has no horizon, as the game's.</summary>
        private const float LeastGravitySquared = 0.0025f;

        /// <summary>How far a direction may be behind the eyes' plane (its depth in front, over its length) and still be drawn.</summary>
        private const double Ahead = 0.05;

        private static readonly FeatureGuard guard = new FeatureGuard(nameof(ConformalHorizon));
        private static bool logged;

        public static void Patch(Harmony harmony)
        {
            var method = AccessTools.Method(typeof(MyGuiScreenHudSpace), GameMethod)
                ?? throw new MissingMethodException(nameof(MyGuiScreenHudSpace), GameMethod);
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(ConformalHorizon), nameof(Instead)));
            Log.Info("Conformal horizon: hooked; in the headset a cockpit's artificial horizon lies on the real horizon (RenderDebug ConformalHorizon=0: the game's own)");
        }

        /// <summary>Instead of the game's draw while the picture is VR's: false when it drew, true to let the game's run.</summary>
        private static bool Instead()
        {
            if (Off || HudCamera.Off || !StereoPatches.VRPicture || !guard.On)
                return true;
            try
            {
                Draw();
                return false;
            }
            catch (Exception e)
            {
                guard.Failed(e, "Conformal horizon could not be drawn; the game's own is drawn instead");
                return true;
            }
        }

        private static void Draw()
        {
            MySession session = MySession.Static;
            MyCamera camera = MySector.MainCamera;
            if (!(session?.ControlledEntity is MyShipController ship) || camera == null || !ship.HorizonIndicatorEnabled || ship.CubeGrid?.Physics == null)
                return;
            MatrixD shipMatrix = ship.WorldMatrix;
            Vector3 gravity = MyGravityProviderSystem.CalculateNaturalGravityInPoint(shipMatrix.Translation);
            if (gravity.LengthSquared() < LeastGravitySquared)
                return;
            Rectangle screen = MyGuiManager.GetFullscreenRectangle();
            if (screen.Width <= 0 || screen.Height <= 0)
                return;

            var projection = new Projection(HudCamera.View(camera), screen);
            Vector3D up = -Vector3D.Normalize(gravity);
            Vector3D forward = shipMatrix.Forward;
            float pixel = screen.Height / 1080f;

            // The line: the horizon straight ahead of the ship (its forward laid level), turned as the horizon's own right runs.
            Vector3D level = forward - up * Vector3D.Dot(forward, up);
            if (level.LengthSquared() > 1e-6)
            {
                level.Normalize();
                Vector3D along = Vector3D.Cross(level, up);
                if (projection.TryPoint(level, out Vector2 middle) && projection.TryPoint(level + 0.05 * along, out Vector2 right))
                {
                    var horizon = Sandbox.Graphics.GUI.MyGuiConstants.TEXTURE_HUD_GRAVITY_HORIZON;
                    Vector2 size = horizon.SizePx * ArtScale * pixel;
                    var destination = new RectangleF(middle.X - size.X / 2f, middle.Y - size.Y / 2f, size.X, size.Y);
                    float turn = (float)Math.Atan2(right.Y - middle.Y, right.X - middle.X);
                    MyRenderProxy.DrawSprite(horizon.Texture, ref destination, null, Color.White, turn, true, true, null, null, 0f);
                }
            }

            // The altitude, under where the ship points.
            if (projection.TryPoint(forward, out Vector2 aim))
            {
                string text = Math.Round(MyGuiScreenHudSpace.GetHeightAboveSurface(ship.CubeGrid)).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " m";
                float scale = TextScale * pixel;
                Vector2 measured = MyGuiManager.GetFont(MyStringHash.GetOrCompute(Font)).MeasureString(text, scale);
                var at = new Vector2(aim.X - measured.X / 2f, aim.Y + AltitudeBelow * screen.Height - measured.Y / 2f);
                MyRenderProxy.DrawString((int)MyStringHash.GetOrCompute(Font), at, Color.White, text, scale, float.PositiveInfinity, true, null);
            }

            if (!logged)
            {
                logged = true;
                Log.Info($"Conformal horizon: drawn on the real horizon through the HUD panel ({VRSettings.HudWidthDegrees:0} deg wide), the altitude under the crosshair");
            }
        }

        /// <summary>Directions in the world to points of the HUD, pixels, as <see cref="HudCamera"/> projects the HUD: from the head through the panel.</summary>
        private readonly struct Projection
        {
            private readonly MatrixD view;
            private readonly double centreX, centreY, perTangent;

            public Projection(MatrixD view, Rectangle screen)
            {
                this.view = view;
                centreX = screen.X + screen.Width / 2.0;
                centreY = screen.Y + screen.Height / 2.0;
                perTangent = screen.Width / 2.0 / Math.Tan(MathHelper.ToRadians(VRSettings.HudWidthDegrees) / 2.0);
            }

            /// <summary>The HUD pixel the world direction <paramref name="direction"/> is seen at; false when it is behind the eyes.</summary>
            public bool TryPoint(Vector3D direction, out Vector2 point)
            {
                Vector3D seen = Vector3D.TransformNormal(direction, view);
                double length = seen.Length();
                if (length < 1e-9 || -seen.Z / length < Ahead)
                {
                    point = default;
                    return false;
                }
                point = new Vector2((float)(centreX + seen.X / -seen.Z * perTangent), (float)(centreY - seen.Y / -seen.Z * perTangent));
                return true;
            }
        }
    }
}
