// VisorWeather: the world on the helmet's visor: frost at its rim in the cold, breath on its inside, rain, snow and dust from the planet's
// weather, mist coming in from the cold, and a fingertip's wipe through all of it.
using System;
using System.Diagnostics;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using SpaceEngineersVR.Hands;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.OpenXR;
using VRage.Game;
using VRage.Utils;
using VRageMath;
using VRageRender;
using VRageRender.Messages;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// What the world leaves on the closed visor, from the game's own temperature and weather, each with its setting:
    /// <list type="bullet">
    /// <item>Frost (<see cref="VRSettings.VisorFrost"/>): grows in from the opening's edge while the game's temperature outside the
    /// character (MyCharacter.GetOutsideTemperature, as MySectorWeatherComponent.TemperatureToLevel reads it) is Freeze or
    /// ExtremeFreeze, further and faster in ExtremeFreeze, and melts back in the warm. Frost needs air to form from: the game reads
    /// ExtremeFreeze in space too (MySectorWeatherComponent.GetTemperatureInPoint: no air, no warmth), so in vacuum the visor
    /// stays clear (<see cref="InAir"/>).</item>
    /// <item>Breath (<see cref="VRSettings.VisorBreath"/>): on the inside, at the lower edge, a faint mist with each breath out while
    /// it is freezing or the character breathes hard (MyCharacterBreath.CurrentState Heated or VeryHeated, as the game sets it
    /// after running), fading within a second. Never wiped.</item>
    /// <item>Weather (<see cref="VRSettings.VisorWeather"/>): out in the weather at the character's place
    /// (MySectorWeatherComponent.GetWeather and GetWeatherIntensity), by its particle effect (MyWeatherEffectDefinition.EffectName):
    /// rain (and hail) spatters and runs, snow settles at the edges, dust and sand coat it in the weather's fog colour
    /// (MySector.FogProperties.FogColor). Not under cover: the game's own test for its weather particles (MyEntityReverbDetectorComponent:
    /// more than 20 grid or voxel hits round the character), or in a pressurised cockpit.</item>
    /// <item>Mist (<see cref="VRSettings.VisorMisting"/>): the glass keeps the cold it has been in, warming over seconds; in air
    /// warmer than Freeze while the glass is still colder (from Freeze or below), it mists over, clearing as the glass warms.</item>
    /// <item>Wipe (<see cref="VRSettings.VisorWipe"/>): the drawn index fingertip resting on the helmet's front
    /// (<see cref="HelmetStop.TipOnHelmet"/>), where it lies over the glass seen from the eyes, clears all of the outside along its
    /// path (<see cref="VisorCoat.Wipe"/>), with a soft buzz in that hand. While a cause lasts, it grows back slowly.</item>
    /// </list>
    /// <para>
    /// Drawn on the glass itself: a part of a sphere <see cref="CoatRadius"/> round the eyes over the helmet's opening, just inside the
    /// helmet's inside (<see cref="HelmetInterior"/>, <see cref="HelmetShell.Radius"/>), as quads of the game's billboard renderer after its
    /// processing (MyBillboard.BlendTypeEnum.PostPP), placed as the helmet's inside is (<see cref="HelmetTurn"/>, from the head the eyes
    /// are drawn from), so it is where the glass is in both eyes and turns with the helmet; it swings up with the glass as the helmet
    /// opens (<see cref="HelmetHud.GlassCover"/>) and is gone with it. The breath is a second such layer nearer the eyes
    /// (<see cref="BreathRadius"/>). What lies outside is seen through the glass and the sun visor (<see cref="VisorGlass.Behind"/>);
    /// both are lit by an estimate of the light on the glass (day or night on a planet, MySectorWeatherComponent.NightValue; under
    /// cover; the suit lamp), as the post-processing billboards are not lit by the scene. The HUD on the glass is a layer of its own
    /// over the eyes (<see cref="HudVisor"/>), so nothing here covers it.
    /// </para>
    /// <para>
    /// Each layer is one texture given to the renderer by name (MyRenderProxy.CreateGeneratedTexture): the outside
    /// <see cref="VisorCoat.PictureWidth"/> by <see cref="VisorCoat.PictureHeight"/> (about 0.22 deg a texel, so drops are round
    /// and frost's crystals fine in the headset), the breath <see cref="VisorCoat.Width"/> by <see cref="VisorCoat.Height"/> (it is
    /// soft). The outside is worked out on the game thread <see cref="StepsPerSecond"/> times a second (<see cref="VisorCoat"/>),
    /// its time logged once a character (<see cref="Timing"/>), and its texels copied into the renderer's texture on the render
    /// thread when they change; the breath's picture never changes, only how much of it shows. Nothing is allocated per frame. One log line for each change:
    /// the game's temperature level, frost coming and going, the mist, the weather on the glass, each wipe, the breath. A character
    /// new in the world is left <see cref="TemperatureTicks"/> before any of it, until the game has read its temperature.
    /// </para>
    /// <para>
    /// The fairness rule (docs/DEVELOPMENT.md) has a recorded exception for this (2026-10-17): the effects may hinder the view when
    /// the game's own state justifies them, each has its setting, and the HUD is never covered. RenderDebug VisorWeather=0 turns it
    /// all off.
    /// </para>
    /// </summary>
    internal static class VisorWeather
    {
        /// <summary>Debug: nothing on the visor (<see cref="RenderDebug"/> VisorWeather=0).</summary>
        public static bool Off { get; set; }

        /// <summary>The layers' radii round the eyes, metres: the outside of the glass, and its inside nearer the eyes; both inside the helmet's inside.</summary>
        public const float CoatRadius = 0.136f, BreathRadius = 0.132f;

        /// <summary>How often the outside of the glass is worked out, per second.</summary>
        public const int StepsPerSecond = 30;

        /// <summary>
        /// Simulation ticks a new character is left before its temperature is read: the game reads it once every 100 ticks
        /// (MyCharacter.UpdateAfterSimulation100, UpdateOutsideTemperature), and until its first reading it is 0, ExtremeFreeze.
        /// </summary>
        private const int TemperatureTicks = 120;

        private const string CoatTexture = "SEVRVisorCoat", BreathTexture = "SEVRVisorBreath";
        private static readonly MyStringId CoatMaterial = MyStringId.GetOrCompute("SEVRVisorCoat"), BreathMaterial = MyStringId.GetOrCompute("SEVRVisorBreath");

        /// <summary>The layers' quads: this many across the opening and down it.</summary>
        private const int Columns = 16, Rows = 12;

        /// <summary>How far in from the opening's edge the frost reaches at the most, in Freeze and ExtremeFreeze (a share of the way to the middle).</summary>
        private const float FrostReachFreeze = 0.2f, FrostReachExtreme = 0.36f;

        /// <summary>How long the frost takes to grow most of the way to its reach, in Freeze and ExtremeFreeze, and to melt back, seconds.</summary>
        private const float FrostGrowFreeze = 40f, FrostGrowExtreme = 22f, FrostMelt = 10f;

        /// <summary>How long the glass takes to come to the temperature round it, cooling and warming, seconds.</summary>
        private const float GlassCools = 20f, GlassWarms = 4f;

        /// <summary>How long a breath's mist takes to fade, seconds (most of it gone within a second).</summary>
        private const float BreathFades = 0.35f;

        /// <summary>The dust's colour when the weather's fog has none, and how much of it is kept over the fog's.</summary>
        private static readonly Vector3 Dust = new Vector3(0.62f, 0.5f, 0.37f);

        private static readonly FeatureGuard guard = new FeatureGuard(nameof(VisorWeather));

        internal enum Fall { None, Rain, Snow, Dust }

        private static readonly string[] FallNames = { "none", "rain", "snow", "dust" };

        // ---- Shared between the threads ----

        private static readonly object gate = new object();
        // Three pictures in turn: the game thread composes one, the newest waits ready, and the render thread uploads its own
        // outside the lock (UploadCoat), so the game thread never waits on the upload.
        private static byte[] composing = new byte[VisorCoat.PictureWidth * VisorCoat.PictureHeight * 4], ready = new byte[VisorCoat.PictureWidth * VisorCoat.PictureHeight * 4],
            uploading = new byte[VisorCoat.PictureWidth * VisorCoat.PictureHeight * 4];
        private static bool fresh;

        /// <summary>The texture is new (given again, or the renderer's own changed): the newest picture goes into it again, new or not.</summary>
        private static bool reupload;
        private static MatrixD helmetDrawn;
        private static bool helmetKnown;
        private static volatile bool coatShows, materialAdded, texturesSent, texturesLost;
        private static volatile float breathShows, light = 0.6f;

        // ---- Game thread ----

        private static VisorCoat coat;
        private static MyCharacter seenFor, arrived;
        private static int arrivedAt;
        private static MyTemperatureLevel? levelLogged;
        private static long steppedAt, frameAt, sentAt;
        private static float frostReach, glassTemperature = 0.5f, breathPhase, lightTarget = 0.6f;
        private static string weatherName;
        private static Fall weatherFall;
        private static readonly bool[] touching = new bool[2];
        private static readonly Vector2[] touchedAt = new Vector2[2];
        private static readonly float[] wiped = new float[2];

        /// <summary>Per hand: why the fingertip on the helmet does not wipe has been logged for this touch.</summary>
        private static readonly bool[] missNoted = new bool[2];

        /// <summary>Per hand: the touch it is on (each new touch a new smear's streaks).</summary>
        private static readonly int[] strokes = new int[2];

        /// <summary>Steps with something on the glass whose time is logged, once a character: how long working out the glass takes.</summary>
        private const int Timing = 300;

        private static int timedSteps;
        private static double timedTotal, timedMost;

        // What was last logged, so each change is logged once.
        private static bool frostLogged, mistLogged, breathLogged;

        /// <summary>The glass has swung clear of the opening since the helmet last closed, and was cleaned as it did.</summary>
        private static bool glassUp;
        private static Fall fallLogged;

        // ---- Render thread ----

        private struct Quad
        {
            public int TopLeft;
            public Vector2 UVOffset, UVSize;
        }

        private static Quad[] quads;
        private static Vector3[] gridLocal;
        private static Vector3D[] coatWorld, breathWorld;
        private static bool prepared, drawCoat, drawBreath, drawnLogged, roomLogged;
        private static int eyesThisFrame;
        private static object coatTexture, breathTexture;

        /// <summary>How far below level the breath's texture has the mouth (radians), as it was last given or uploaded.</summary>
        private static volatile float breathMouth;
        private static long checkedAt;
        private static Vector4 coatColour, breathColour;

        /// <summary>This eye's pass has the visor's layers: the visor's filters go on before them (<see cref="VisorFilters"/>). Render thread.</summary>
        public static bool InScene { get; private set; }

        /// <summary>Any of it is to be worked out and shown: not off for debugging, the helmet view and at least one of the settings on, nothing failed. Any thread.</summary>
        private static bool Wanted => !Off && HelmetView.On && guard.On && materialAdded
                                      && (VRSettings.VisorFrost || VRSettings.VisorBreath || VRSettings.VisorWeather || VRSettings.VisorMisting);

        /// <summary>At start-up, on the game thread with no world drawn: the layers' materials, as the helmet's inside registers its own (<see cref="PictureMaterial"/>).</summary>
        public static void Register()
        {
            PictureMaterial.Add(CoatMaterial, CoatTexture, softMetres: 0.001f);
            PictureMaterial.Add(BreathMaterial, BreathTexture, softMetres: 0.001f);
            VisorCoat.Prepare();
            materialAdded = true;
            Log.Info($"Visor weather: transparent materials \"{CoatMaterial}\" and \"{BreathMaterial}\" registered");
        }

        // ---- Game thread ----

        /// <summary>
        /// Game thread, once a frame the game draws (<see cref="HelmetState"/>): <paramref name="character"/> (null: none alive) and
        /// what the world does to its visor now; the outside of the glass worked out <see cref="StepsPerSecond"/> times a second.
        /// </summary>
        public static void Update(MyCharacter character)
        {
            if (!guard.On)
                return;
            try
            {
                UpdateNow(character);
            }
            catch (Exception e)
            {
                coatShows = false;
                breathShows = 0f;
                guard.Failed(e, "Visor weather failed; the visor stays clear");
            }
        }

        private static void UpdateNow(MyCharacter character)
        {
            long now = Stopwatch.GetTimestamp();
            float frameSeconds = frameAt == 0 ? 0f : (float)Math.Min(0.25, (now - frameAt) / (double)Stopwatch.Frequency);
            frameAt = now;
            // A character that has just come into the world (a world loading, a respawn): its temperature reads 0 until the game
            // first reads it, so the visor waits for that, rather than frost, breath and mist all coming and going at once.
            int tick = MySession.Static?.GameplayFrameCounter ?? 0;
            if (!ReferenceEquals(character, arrived))
            {
                arrived = character;
                arrivedAt = tick;
            }
            if (!Wanted || character?.OxygenComponent == null || tick - arrivedAt < TemperatureTicks)
            {
                if (seenFor != null || coatShows || breathShows > 0f)
                    Forget();
                return;
            }
            if (!ReferenceEquals(character, seenFor))
            {
                // A new character (a respawn, a world loading): a clean visor, at the temperature it is in.
                Forget();
                seenFor = character;
                glassTemperature = character.GetOutsideTemperature();
            }
            SendTextures();

            bool closed = character.OxygenComponent.HelmetEnabled;
            float temperature = character.GetOutsideTemperature();
            MyTemperatureLevel level = MySectorWeatherComponent.TemperatureToLevel(temperature);
            Breathe(character, closed, level, frameSeconds);

            double sinceStep = (now - steppedAt) / (double)Stopwatch.Frequency;
            if (steppedAt != 0 && sinceStep < 1.0 / StepsPerSecond)
                return;
            float seconds = steppedAt == 0 ? 0f : (float)Math.Min(0.25, sinceStep);
            if (coat == null)
            {
                if (!VisorCoat.Ready)
                    return;
                coat = new VisorCoat();
            }
            steppedAt = now;

            // The helmet opened and its glass swung clear: it closes again clean, with nothing of the weather left on it.
            if (closed)
                glassUp = false;
            else if (!glassUp && HelmetHud.GlassCover <= 0.01f)
            {
                glassUp = true;
                coat.Clear();
                frostReach = 0f;
                Log.Info("Visor weather: the helmet is open; the glass is clean when it closes");
            }

            Vector3D place = character.PositionComp.GetPosition();
            bool covered = Covered(character);

            // Frost: toward the reach the cold allows, growing only with the glass closed; melting back in the warm.
            float reach = !VRSettings.VisorFrost || !InAir(character, place) ? 0f : level == MyTemperatureLevel.ExtremeFreeze ? FrostReachExtreme : level == MyTemperatureLevel.Freeze ? FrostReachFreeze : 0f;
            if (reach > frostReach && closed)
                frostReach += (reach - frostReach) * (1f - (float)Math.Exp(-seconds / (level == MyTemperatureLevel.ExtremeFreeze ? FrostGrowExtreme : FrostGrowFreeze)));
            else if (reach < frostReach)
                frostReach = Math.Max(0f, frostReach - Math.Max(0.004f, frostReach) * (1f - (float)Math.Exp(-seconds / FrostMelt)));
            if (frostReach < 0.002f && reach <= 0f)
                frostReach = 0f;

            // Mist: the glass's own temperature follows the air's; in warm air, glass that is still cold mists over.
            float glassTau = temperature > glassTemperature ? GlassWarms : GlassCools;
            glassTemperature += (temperature - glassTemperature) * (1f - (float)Math.Exp(-seconds / glassTau));
            bool warmAir = level >= MyTemperatureLevel.Cozy && character.EnvironmentOxygenLevel > 0.1f;
            float mist = VRSettings.VisorMisting && closed && warmAir ? Smooth(0.08f, 0.25f, temperature - glassTemperature) : 0f;

            // Weather: what falls here, out from under cover, with the glass closed.
            Fall fall = Fall.None;
            float strength = 0f;
            if (VRSettings.VisorWeather && closed && !covered)
                fall = Weather(place, out strength);
            float rain = fall == Fall.Rain ? strength : 0f, snowfall = fall == Fall.Snow ? strength : 0f, storm = fall == Fall.Dust ? strength : 0f;

            // The glass as it was last drawn, for which way is down on it and where a fingertip touches it.
            bool knowHelmet;
            MatrixD helmet;
            lock (gate)
            {
                knowHelmet = helmetKnown;
                helmet = helmetDrawn;
            }
            MatrixD toHelmet = knowHelmet ? MatrixD.Invert(helmet) : MatrixD.Identity;
            Vector2 down = Vector2.Zero;
            Vector3 gravity = character.Gravity;
            if (knowHelmet && gravity.LengthSquared() > 0.25f)
            {
                Vector3 local = Vector3.TransformNormal(Vector3.Normalize(gravity), (Matrix)toHelmet);
                down = new Vector2(local.X, -local.Y);
            }

            // A clean glass with nothing coming onto it (the helmet open, or clear weather): nothing to step or draw again.
            bool idle = !coatShows && frostReach <= 0f && mist <= 0f && fall == Fall.None;
            long workFrom = Stopwatch.GetTimestamp();
            if (!idle)
                coat.Step(seconds, frostReach, mist, rain, snowfall, storm, down, level <= MyTemperatureLevel.Freeze);
            if (VRSettings.VisorWipe && closed && knowHelmet && HelmetHud.GlassCover >= 0.99f)
            {
                Wipe(Hand.Left, toHelmet);
                Wipe(Hand.Right, toHelmet);
            }
            else
            {
                touching[0] = touching[1] = false;
                string why = !VRSettings.VisorWipe ? "Wipe the visor is off"
                    : !closed ? "the helmet is open"
                    : !knowHelmet ? "the glass has not been drawn yet"
                    : $"the glass covers {HelmetHud.GlassCover:0.00} of the opening, not all of it";
                NoteMiss(Hand.Left, why);
                NoteMiss(Hand.Right, why);
            }

            if (idle)
            {
                Light(character, place, covered, seconds);
                Report(level, temperature, mist, fall, strength);
                return;
            }

            Vector3 fog = MySector.FogProperties.FogColor;
            Vector3 dust = fog.LengthSquared() > 0.001f ? Vector3.Lerp(Vector3.Clamp(fog, Vector3.Zero, Vector3.One), Dust, 0.4f) : Dust;
            bool shows = coat.Compose(composing, dust);
            Time(shows, workFrom);
            lock (gate)
            {
                byte[] swap = ready;
                ready = composing;
                composing = swap;
                fresh = true;
            }
            coatShows = shows;

            Light(character, place, covered, seconds);
            Report(level, temperature, mist, fall, strength);
        }

        /// <summary>A clean visor and nothing shown: no character, or none of it wanted.</summary>
        private static void Forget()
        {
            seenFor = null;
            coat?.Clear();
            coatShows = false;
            breathShows = 0f;
            frostReach = 0f;
            breathPhase = 0f;
            steppedAt = 0;
            touching[0] = touching[1] = false;
            weatherName = null;
            weatherFall = Fall.None;
            frostLogged = mistLogged = breathLogged = false;
            glassUp = false;
            fallLogged = Fall.None;
            levelLogged = null;
            timedSteps = 0;
            timedTotal = timedMost = 0.0;
        }

        /// <summary>Once a character, after <see cref="Timing"/> steps with something on the glass: how long working out the glass took.</summary>
        private static void Time(bool shows, long from)
        {
            if (!shows || timedSteps >= Timing)
                return;
            double ms = (Stopwatch.GetTimestamp() - from) * 1000.0 / Stopwatch.Frequency;
            timedTotal += ms;
            timedMost = Math.Max(timedMost, ms);
            if (++timedSteps == Timing)
                Log.Info($"Visor weather: working out the glass took {timedTotal / Timing:0.00} ms a step on average over {Timing} steps, {timedMost:0.00} ms at most ({StepsPerSecond} steps a second, on the game thread)");
        }

        /// <summary>Under cover from the weather: a pressurised cockpit, or the game's own test for its weather particles (more than 20 grid or voxel hits round the character).</summary>
        /// <summary>Whether there is air outside the glass for frost to form from: a planet's atmosphere (MyPlanet.GetAirDensity) or
        /// air the game gives oxygen to, such as a pressurised room. Space has none.</summary>
        private static bool InAir(MyCharacter character, Vector3D place)
        {
            if (character.EnvironmentOxygenLevel > 0f)
                return true;
            MyPlanet planet = MyGamePruningStructure.GetClosestPlanet(place);
            return planet != null && planet.HasAtmosphere && planet.GetAirDensity(place) > 0f;
        }

        private static bool Covered(MyCharacter character)
        {
            if (character.Parent is MyCockpit cockpit && cockpit.BlockDefinition.IsPressurized)
                return true;
            var reverb = character.ReverbDetectorComp;
            return reverb != null && (reverb.Grids > 20 || reverb.Voxels > 20);
        }

        /// <summary>The weather at <paramref name="place"/> as it falls on glass, by its particle effect, and how hard (the game's intensity there, 0 to 1).</summary>
        private static Fall Weather(Vector3D place, out float strength)
        {
            strength = 0f;
            var weather = MySession.Static?.GetComponent<MySectorWeatherComponent>();
            if (weather == null || !weather.GetWeather(place, out MyObjectBuilder_WeatherEffect effect) || effect == null)
                return Fall.None;
            if (!string.Equals(effect.Weather, weatherName, StringComparison.Ordinal))
            {
                weatherName = effect.Weather;
                weatherFall = Classify(MyDefinitionManager.Static.GetWeatherEffect(effect.Weather)?.EffectName);
            }
            if (weatherFall == Fall.None)
                return Fall.None;
            strength = MathHelper.Clamp(weather.GetWeatherIntensity(place, effect), 0f, 1f);
            return strength > 0.01f ? weatherFall : Fall.None;
        }

        /// <summary>What a weather's particle effect drops on glass, by its name (the game's own: WM_Rain, WM_RainHeavy, WM_Hailstorm, WM_SnowSlow, WM_Blizzard, WM_Dust, WM_Sandstorm, WM_SandStormHeavy, WM_MarsStorm...). Pure.</summary>
        internal static Fall Classify(string effect)
        {
            if (string.IsNullOrEmpty(effect))
                return Fall.None;
            if (Has(effect, "Rain") || Has(effect, "Hail"))
                return Fall.Rain;
            if (Has(effect, "Snow") || Has(effect, "Blizzard"))
                return Fall.Snow;
            if (Has(effect, "Dust") || Has(effect, "Sand") || Has(effect, "MarsStorm"))
                return Fall.Dust;
            return Fall.None;
        }

        private static bool Has(string text, string part) => text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// A fingertip on the helmet's front, where it lies over the glass seen from the eyes, wipes from where it was the last step;
        /// the hand buzzes softly as much as it takes off. A touch outside the glass, or the first of a stroke, only marks the place.
        /// </summary>
        private static void Wipe(Hand hand, in MatrixD toHelmet)
        {
            int h = (int)hand;
            if (!HelmetStop.TipOnHelmet(hand, out Vector3D point))
            {
                touching[h] = false;
                missNoted[h] = false;
                return;
            }
            Vector3 local = (Vector3)Vector3D.Transform(point, toHelmet);
            if (local.LengthSquared() < 1e-6f)
            {
                touching[h] = false;
                return;
            }
            Vector3 way = Vector3.Normalize(local);
            float round = (float)Math.Atan2(way.X, -way.Z), up = (float)Math.Asin(MathHelper.Clamp(way.Y, -1f, 1f));
            float radius = HelmetShell.OpeningRadius(round, up);
            if (radius > 1f)
            {
                touching[h] = false;
                NoteMiss(hand, $"it is outside the glass ({MathHelper.ToDegrees(round):0} deg round, {MathHelper.ToDegrees(up):0} up, {radius:0.00} of the way to the opening's edge)");
                return;
            }
            missNoted[h] = false;
            Vector2 at = VisorCoat.Place(round, up);
            if (!touching[h])
            {
                touching[h] = true;
                touchedAt[h] = at;
                wiped[h] = 0f;
                strokes[h]++;
                Log.Info($"Visor weather: the {hand.ToString().ToLowerInvariant()} fingertip wipes the visor ({at.X:0.00}, {at.Y:0.00} across and down the glass)");
            }
            float taken = coat.Wipe(touchedAt[h].X, touchedAt[h].Y, at.X, at.Y, strokes[h] * 2 + h);
            touchedAt[h] = at;
            wiped[h] += taken;
            if (taken > 0.5f)
                Haptics.Pulse(hand, Math.Min(0.35f, 0.08f + taken * 0.004f), 0.03f);
        }

        /// <summary>
        /// The fingertip of <paramref name="hand"/> rests on the helmet and does not wipe: says why, once a touch.
        /// </summary>
        private static void NoteMiss(Hand hand, string why)
        {
            int h = (int)hand;
            if (!HelmetStop.TipOnHelmet(hand, out _))
            {
                missNoted[h] = false;
                return;
            }
            if (missNoted[h])
                return;
            missNoted[h] = true;
            Log.Info($"Visor weather: the {hand.ToString().ToLowerInvariant()} fingertip is on the helmet but does not wipe: {why}");
        }

        /// <summary>
        /// Every frame: the breath's rhythm, quicker as the game's breathing is harder, and each breath out misting the inside of the
        /// glass as much as the cold or the effort calls for, the mist fading after it.
        /// </summary>
        private static void Breathe(MyCharacter character, bool closed, MyTemperatureLevel level, float seconds)
        {
            MyCharacterBreath.State state = character.Breath?.CurrentState ?? MyCharacterBreath.State.NoBreath;
            float period = state == MyCharacterBreath.State.VeryHeated ? 1.5f : state == MyCharacterBreath.State.Heated ? 2.2f : 4f;
            breathPhase += seconds / period;
            if (breathPhase >= 1f)
                breathPhase -= (float)Math.Floor(breathPhase);
            float cold = level == MyTemperatureLevel.ExtremeFreeze ? 0.8f : level == MyTemperatureLevel.Freeze ? 0.6f : 0f;
            float hard = state == MyCharacterBreath.State.VeryHeated ? 0.75f : state == MyCharacterBreath.State.Heated ? 0.5f : 0f;
            float strength = VRSettings.VisorBreath && closed && !character.IsDead ? Math.Max(cold, hard) : 0f;
            float shows = breathShows;
            if (strength > 0f && breathPhase > 0.45f && breathPhase < 0.62f)
                shows = Math.Min(strength, shows + seconds / 0.3f * strength);
            else
                shows *= (float)Math.Exp(-seconds / BreathFades);
            breathShows = shows < 0.004f ? 0f : shows;

            bool breathing = strength > 0f;
            if (breathing != breathLogged)
            {
                breathLogged = breathing;
                Log.Info(breathing
                    ? $"Visor weather: each breath mists the inside of the visor ({(cold >= hard ? $"the cold, {level}" : $"breathing hard, {state}")})"
                    : "Visor weather: the breath no longer mists the visor");
            }
        }

        /// <summary>The light on the glass, eased: daylight to night on a planet, a little under cover, more with the suit lamp on.</summary>
        private static void Light(MyCharacter character, Vector3D place, bool covered, float seconds)
        {
            float target = 0.6f;
            if (!covered)
            {
                MyPlanet planet = MyGamePruningStructure.GetClosestPlanet(place);
                if (planet != null)
                    target = MathHelper.Lerp(0.8f, 0.15f, MathHelper.Clamp(MySectorWeatherComponent.NightValue(planet, place), 0f, 1f));
            }
            if (HelmetHud.LampOn)
                target = Math.Min(1f, target + 0.3f);
            lightTarget = target;
            light += (lightTarget - light) * (1f - (float)Math.Exp(-seconds / 1f));
        }

        /// <summary>One log line for each change: frost coming and going, the mist, the weather on the glass.</summary>
        private static void Report(MyTemperatureLevel level, float temperature, float mist, Fall fall, float strength)
        {
            if (level != levelLogged)
            {
                levelLogged = level;
                Log.Info($"Visor weather: the game's temperature at the character reads {level} ({temperature:0.00}), the air outside {seenFor.EnvironmentOxygenLevel:0.00}" +
                         (seenFor.OxygenComponent.HelmetEnabled ? "" : ", the helmet open"));
            }
            bool frost = frostReach > 0f;
            if (frost != frostLogged)
            {
                frostLogged = frost;
                Log.Info(frost
                    ? $"Visor weather: frost grows in from the visor's edge (the game's temperature reads {level}, {temperature:0.00})"
                    : "Visor weather: the frost has melted from the visor");
            }
            bool misted = mist > 0.05f;
            if (misted != mistLogged)
            {
                mistLogged = misted;
                Log.Info(misted
                    ? $"Visor weather: the visor mists over, cold from outside in warm air (the glass at {glassTemperature:0.00}, the air at {temperature:0.00})"
                    : "Visor weather: the visor's mist clears as the glass warms");
            }
            if (fall != fallLogged)
            {
                fallLogged = fall;
                Log.Info(fall == Fall.None
                    ? "Visor weather: no weather falls on the visor now"
                    : $"Visor weather: {FallNames[(int)fall]} on the visor ({weatherName} at {strength:P0})");
            }
        }

        /// <summary>The renderer has the layers' textures: given once, and again after a device reset lost them.</summary>
        private static void SendTextures()
        {
            if (texturesSent && !texturesLost)
                return;
            MyRenderProxy.CreateGeneratedTexture(CoatTexture, VisorCoat.PictureWidth, VisorCoat.PictureHeight, MyGeneratedTextureType.RGBA, 1,
                new byte[VisorCoat.PictureWidth * VisorCoat.PictureHeight * 4], generateMipmaps: false, immediatelyReady: true);
            float mouth = Mouth();
            MyRenderProxy.CreateGeneratedTexture(BreathTexture, VisorCoat.Width, VisorCoat.Height, MyGeneratedTextureType.RGBA, 1,
                VisorCoat.BreathTexels(mouth), generateMipmaps: false, immediatelyReady: true);
            breathMouth = mouth;
            LogMouth(mouth);
            lock (gate)
                reupload = true;
            Log.Info(texturesSent
                ? "Visor weather: the renderer had lost the visor's textures (a device reset); given again"
                : $"Visor weather: the visor's textures given to the renderer (\"{CoatTexture}\" {VisorCoat.PictureWidth}x{VisorCoat.PictureHeight}, \"{BreathTexture}\" {VisorCoat.Width}x{VisorCoat.Height})");
            sentAt = Stopwatch.GetTimestamp();
            texturesLost = false;
            texturesSent = true;
        }

        // ---- Render thread ----

        /// <summary>Render thread, as an eye frame begins: no eye has the layers yet.</summary>
        public static void BeginFrame()
        {
            eyesThisFrame = 0;
            prepared = false;
            InScene = false;
        }

        /// <summary>
        /// Render thread, before an eye frame's passes: where the glass is this frame, from the head the eyes are drawn from
        /// (<paramref name="headWorld"/>), swung up as far as the glass is open; the outside's new texels into its texture.
        /// </summary>
        public static void Prepare(in MatrixD headWorld)
        {
            if (!Wanted || !texturesSent || texturesLost || !HelmetHud.Worn)
                return;
            try
            {
                MatrixD helmetWorld = (MatrixD)HelmetTurn.FromHead() * headWorld;
                lock (gate)
                {
                    helmetDrawn = helmetWorld;
                    helmetKnown = true;
                }
                float cover = HelmetHud.GlassCover;
                float breath = breathShows;
                drawCoat = coatShows;
                drawBreath = breath > 0f;
                if (cover <= 0f || (!drawCoat && !drawBreath) || !Textures())
                    return;
                UploadCoat();
                PlaceBreath();
                if (quads == null)
                    Build();
                // The breath goes where the mouth is, at the foot of the view: placed by the head, not by the helmet turned about it.
                MatrixD swing = MatrixD.CreateRotationX((1f - cover) * HelmetHud.GlassSwingAngle);
                MatrixD glassWorld = swing * helmetWorld, mouthWorld = swing * headWorld;
                for (int i = 0; i < gridLocal.Length; i++)
                {
                    coatWorld[i] = Vector3D.Transform((Vector3D)(gridLocal[i] * CoatRadius), glassWorld);
                    breathWorld[i] = Vector3D.Transform((Vector3D)(gridLocal[i] * BreathRadius), mouthWorld);
                }
                float lit = light;
                Vector3 through = VisorGlass.Behind * lit;
                coatColour = new Vector4(through, 1f);
                breathColour = new Vector4(lit * breath, lit * breath, lit * breath, breath);
                prepared = true;
            }
            catch (Exception e)
            {
                guard.Failed(e, "Visor weather could not be placed; the visor stays clear");
            }
        }

        /// <summary>
        /// The renderer has both textures; checked once a second (asking allocates). Gone a while after they were given: lost with
        /// the device, given again by the game thread.
        /// </summary>
        private static bool Textures()
        {
            long now = Stopwatch.GetTimestamp();
            if (coatTexture != null && now - checkedAt < Stopwatch.Frequency)
                return true;
            checkedAt = now;
            if (RendererTextures.TryGet(CoatTexture, out object made) && RendererTextures.TryGet(BreathTexture, out object breathMade))
            {
                breathTexture = breathMade;
                if (!ReferenceEquals(made, coatTexture))
                {
                    coatTexture = made;
                    lock (gate)
                        reupload = true;
                }
                return true;
            }
            coatTexture = null;
            if ((now - sentAt) / (double)Stopwatch.Frequency > 2.0)
                texturesLost = true;
            return false;
        }

        /// <summary>
        /// How far below level the mouth is in the view (radians): the foot of the headset's view, the lower of the eyes', within the
        /// opening; the opening's lower edge until the runtime gives the views.
        /// </summary>
        private static float Mouth()
        {
            float down = 0f;
            for (int eye = 0; eye < 2; eye++)
                down = Math.Max(down, -XrSession.View(eye).Fov.AngleDown);
            return down > 0f ? Math.Min(down, HelmetShell.Down) : HelmetShell.Down;
        }

        /// <summary>The breath's texture made again for the mouth, when the headset's view has a different foot from the one it was made for.</summary>
        private static unsafe void PlaceBreath()
        {
            float mouth = Mouth();
            if (breathTexture == null || Math.Abs(mouth - breathMouth) < MathHelper.ToRadians(0.5f))
                return;
            byte[] texels = VisorCoat.BreathTexels(mouth);
            fixed (byte* data = texels)
                EyeRenderer.D3DDevice.ImmediateContext.UpdateSubresource(new SharpDX.DataBox((IntPtr)data, VisorCoat.Width * 4, 0), EyeRenderer.ResourceOf(breathTexture), 0);
            breathMouth = mouth;
            LogMouth(mouth);
        }

        private static void LogMouth(float mouth) =>
            Log.Info($"Visor weather: the breath mists the glass from {MathHelper.ToDegrees(mouth):0.0} deg below level, the foot of the headset's view");

        /// <summary>The outside's newest texels into its texture, when the game thread has new ones.</summary>
        private static unsafe void UploadCoat()
        {
            if (coatTexture == null)
                return;
            lock (gate)
            {
                if (fresh)
                {
                    byte[] swap = uploading;
                    uploading = ready;
                    ready = swap;
                    fresh = false;
                }
                else if (!reupload)
                    return;
                reupload = false;
            }
            SharpDX.Direct3D11.Resource resource = EyeRenderer.ResourceOf(coatTexture);
            fixed (byte* data = uploading)
                EyeRenderer.D3DDevice.ImmediateContext.UpdateSubresource(new SharpDX.DataBox((IntPtr)data, VisorCoat.PictureWidth * 4, 0), resource, 0);
        }

        /// <summary>
        /// Render thread, as an eye's pass begins, after the one-frame billboards were rewound to the frame's mark: the layers' quads
        /// for this pass. Not when the pool has no room for them.
        /// </summary>
        public static void AddToEye()
        {
            InScene = false;
            if (!prepared)
                return;
            try
            {
                if (OnceBillboards.Shared && eyesThisFrame > 0)
                {
                    InScene = true;
                    eyesThisFrame++;
                    return;
                }
                int needed = (drawCoat ? quads.Length : 0) + (drawBreath ? quads.Length : 0);
                if (OnceBillboards.Room < needed)
                {
                    if (!roomLogged)
                    {
                        roomLogged = true;
                        Log.Warn($"Visor weather: the renderer's one-frame billboards have no room for the visor's {needed} quads ({OnceBillboards.Room} left); not drawn this frame");
                    }
                    return;
                }
                if (drawCoat)
                    Add(CoatMaterial, coatWorld, coatColour, CoatRadius);
                if (drawBreath)
                    Add(BreathMaterial, breathWorld, breathColour, BreathRadius);
                InScene = true;
                eyesThisFrame++;
                if (!drawnLogged)
                {
                    drawnLogged = true;
                    Log.Info($"Visor weather: the visor's layers drawn on the glass, {quads.Length} quads each ({Columns} across by {Rows} down the opening, those wholly past its edge left out), " +
                             $"{CoatRadius * 100f:0.0} cm (outside) and {BreathRadius * 100f:0.0} cm (inside) from the eyes, by the game's billboard renderer after its processing; " +
                             "RenderDebug VisorWeather=0 turns it off");
                }
            }
            catch (Exception e)
            {
                guard.Failed(e, "Visor weather could not be drawn; the visor stays clear");
            }
        }

        private static void Add(MyStringId material, Vector3D[] world, Vector4 colour, float radius)
        {
            foreach (Quad quad in quads)
            {
                MyBillboard b = OnceBillboards.Add();
                if (b == null)
                    return;
                int at = quad.TopLeft;
                b.Material = material;
                b.BlendType = MyBillboard.BlendTypeEnum.PostPP;
                // The renderer takes the texture's corners in this order: top left, top right, bottom right, bottom left.
                b.Position0 = world[at];
                b.Position1 = world[at + 1];
                b.Position2 = world[at + Columns + 2];
                b.Position3 = world[at + Columns + 1];
                b.UVOffset = quad.UVOffset;
                b.UVSize = quad.UVSize;
                b.Color = colour;
                b.ColorIntensity = 1f;
                b.SoftParticleDistanceScale = 1f;
                b.LocalType = MyBillboard.LocalTypeEnum.Custom;
                b.ParentID = uint.MaxValue;
                b.DistanceSquared = radius * radius;
                b.Reflectivity = 0f;
                b.AlphaCutout = 0f;
                b.CustomViewProjection = -1;
            }
        }

        /// <summary>The grid of unit directions over the picture (<see cref="VisorCoat.Angles"/>), row by row from the top, and the quads between them that reach the glass.</summary>
        private static void Build()
        {
            gridLocal = new Vector3[(Columns + 1) * (Rows + 1)];
            coatWorld = new Vector3D[gridLocal.Length];
            breathWorld = new Vector3D[gridLocal.Length];
            for (int row = 0; row <= Rows; row++)
            {
                for (int column = 0; column <= Columns; column++)
                {
                    VisorCoat.Angles(column / (float)Columns, row / (float)Rows, out float round, out float up);
                    gridLocal[row * (Columns + 1) + column] = HelmetShell.Direction(round, up);
                }
            }
            var kept = new System.Collections.Generic.List<Quad>(Columns * Rows);
            var size = new Vector2(1f / Columns, 1f / Rows);
            for (int row = 0; row < Rows; row++)
            {
                for (int column = 0; column < Columns; column++)
                {
                    if (!ReachesGlass(column, row))
                        continue;
                    kept.Add(new Quad
                    {
                        TopLeft = row * (Columns + 1) + column,
                        UVOffset = new Vector2(column * size.X, row * size.Y),
                        UVSize = size,
                    });
                }
            }
            quads = kept.ToArray();
        }

        /// <summary>Some of a quad is on the glass (in the opening), by a grid of points over it, its edges included.</summary>
        private static bool ReachesGlass(int column, int row)
        {
            const int Samples = 6;
            for (int j = 0; j <= Samples; j++)
            {
                for (int i = 0; i <= Samples; i++)
                {
                    VisorCoat.Angles((column + i / (float)Samples) / Columns, (row + j / (float)Samples) / Rows, out float round, out float up);
                    if (HelmetShell.OpeningRadius(round, up) < 1f)
                        return true;
                }
            }
            return false;
        }

        private static float Smooth(float from, float to, float x)
        {
            float t = MathHelper.Clamp((x - from) / (to - from), 0f, 1f);
            return t * t * (3f - 2f * t);
        }
    }
}
