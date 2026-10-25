// SuitPower: the suit's own battery failing, shown and felt instead of only read on a bar.
using System;
using System.Diagnostics;
using System.Threading;
using HarmonyLib;
using Sandbox.Game.Components;
using Sandbox.Game.Definitions.Components;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.GUI;
using Sandbox.Game.World;
using VRageMath;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// As the suit's battery (<see cref="MyCharacter.SuitEnergyLevel"/>) runs low, the HUD on the visor (<see cref="HudVisor"/>)
    /// flickers, more often the lower it gets, and dims; at zero it goes out, as the game's own HUD would with no power
    /// (checked: the flat 2D HUD itself is never gated by suit energy in the base game - MyHudWarnings only plays a sound and a
    /// notification at the thresholds below - so this is the plugin's own immersive read of "no power", built on the game's own
    /// thresholds), and the wrist screen (<see cref="WristStatus"/>) goes with it; recharging brings both back with the same
    /// power-on flourish the visor already has (<see cref="HudVisor.PowerOnLevel"/>). The suit lamp sputters the same way before
    /// it dies (it already does die at zero: <c>MyCharacter.UpdateLightPower</c> fades <c>m_currentLightPower</c> to 0 once the
    /// resource sink can no longer feed it, independent of this class).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The thresholds are the game's own: MyHudWarnings.EnergyLowWarningMethod/EnergyCritWarningMethod call
    /// <c>IsEnergyUnderTreshold(0.25f)</c> and <c>(0.1f)</c> for the suit's own low and critical marks (and the same two numbers
    /// for the jetpack's hydrogen, <c>IsFuelUnderThreshold</c>); <c>IsEnergyUnderTreshold(0f)</c> is the game's own "no power" mark.
    /// Creative mode and a dead character are excluded there, and here.
    /// </para>
    /// <para>
    /// Low stat warnings (<see cref="VRSettings.LowStatWarnings"/>): small icons at the HUD's edge, one per stat at its own
    /// critical mark - health (<c>MyCharacterStatComponent.HEALTH_RATIO_CRITICAL</c>, 0.2), food (its own
    /// <c>MyFoodStatDefinition.CriticalLevel</c>, data from the stat's definition), energy and hydrogen (0.1 above), and oxygen at a third of
    /// <c>MyCharacterOxygenComponent.LOW_OXYGEN_RATIO</c> (0.2, so about 0.067): the mark below which the game itself switches the
    /// suit's breathing sound to its gasping one (<c>MyCharacterSoundComponent</c>), the nearest thing the game has to a critical
    /// oxygen mark - MyHudWarnings has none of its own. This game version has no water/thirst stat, so only these five.
    /// </para>
    /// <para>
    /// The icon for each is the game's own: <see cref="MyStatControlImage"/> is the HUD's own per-stat icon control (one
    /// instance for each of health/food/energy/oxygen/hydrogen, drawn with <c>MyRenderProxy.DrawSprite(Texture.Path, ...)</c>,
    /// found in MyStatControls' own build, not redrawn); it carries no name, so each is told apart by matching its drawn
    /// <c>StatCurrent/StatMaxValue</c> against the character's own ratio for that stat, sampled the same frame (a close-enough
    /// match, not a lock: a stale comparison for one frame at worst finds no match that frame).
    /// </para>
    /// </remarks>
    internal static class SuitPower
    {
        /// <summary>The game's own suit energy marks (MyHudWarnings.cs: EnergyLowWarningMethod, EnergyCritWarningMethod).</summary>
        private const float LowRatio = 0.25f, CriticalRatio = 0.1f;

        /// <summary>The suit oxygen mark the game's own breathing sound turns to gasping at: a third of its low-oxygen ratio.</summary>
        private static readonly float OxygenCriticalRatio = MyCharacterOxygenComponent.LOW_OXYGEN_RATIO / 3f;

        /// <summary>How dark the HUD and lamp get as energy nears empty, before the cut at zero.</summary>
        private const float DimFloor = 0.3f;

        // ---- Game thread: sampled once a drawn frame (MySession.DrawSync). ----
        private static volatile bool tracked;
        private static volatile float ratio = 1f;
        private static int state; // 0 normal, 1 low, 2 critical, 3 out
        private static bool wasOut;
        private static long restoredAt;

        private static readonly string[] StateNames = { "restored", "low", "critical", "out - the HUD and the suit lamp go dark, and the wrist screen with them" };

        // The five tracked stats: health, food, energy, oxygen, hydrogen (in the HUD's own order).
        private const int Health = 0, Food = 1, Energy = 2, Oxygen = 3, Hydrogen = 4, StatCount = 5;
        private static readonly string[] StatNames = { "health", "food", "energy", "oxygen", "hydrogen" };
        private static readonly float[] statRatio = new float[StatCount];
        private static readonly bool[] statTracked = new bool[StatCount];
        private static readonly bool[] statCritical = new bool[StatCount];

        /// <summary>The icon file each stat's own HUD control last drew (<see cref="WatchIcon"/>); null until seen once.</summary>
        private static readonly string[] statIcon = new string[StatCount];

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(MySession), nameof(MySession.DrawSync)),
                postfix: new HarmonyMethod(typeof(SuitPower), nameof(Sample)));
            harmony.Patch(AccessTools.Method(typeof(MyStatControlImage), nameof(MyStatControlImage.Draw)),
                prefix: new HarmonyMethod(typeof(SuitPower), nameof(WatchIcon)));
            harmony.Patch(AccessTools.Method(typeof(MyRenderComponentCharacter), nameof(MyRenderComponentCharacter.UpdateLightProperties)),
                prefix: new HarmonyMethod(typeof(SuitPower), nameof(FlickerLamp)));
            Log.Info("Suit power: hooked (MySession.DrawSync, MyStatControlImage.Draw, MyRenderComponentCharacter.UpdateLightProperties)");
        }

        // ---- Game thread ----

        private static void Sample()
        {
            long now = Stopwatch.GetTimestamp();
            MyCharacter character = MySession.Static?.LocalCharacter;
            bool creative = MySession.Static?.CreativeMode ?? true;
            bool on = VRSettings.SuitPowerFailing && character != null && !character.IsDead && !creative;
            tracked = on;
            ratio = on ? character.SuitEnergyLevel : 1f;
            bool outNow = on && ratio <= 0f;
            int newState = !on ? 0 : outNow ? 3 : ratio <= CriticalRatio ? 2 : ratio <= LowRatio ? 1 : 0;
            if (newState != state)
            {
                state = newState;
                Log.Info("Suit power: " + StateNames[newState]);
            }
            if (wasOut && !outNow)
                Interlocked.Exchange(ref restoredAt, now);
            wasOut = outNow;
            if (on)
                WristStatus.Brightness = Level;

            SampleStats(character, creative);
        }

        private static void SampleStats(MyCharacter character, bool creative)
        {
            bool baseOn = VRSettings.LowStatWarnings && character != null && !character.IsDead && !creative;
            for (int i = 0; i < StatCount; i++)
            {
                bool on = baseOn;
                float r = 1f;
                switch (i)
                {
                    case Health:
                        MyCharacterStatComponent stat = on ? character.StatComp : null;
                        on &= stat != null;
                        if (on) r = stat.HealthRatio;
                        break;
                    case Food:
                        MyCharacterStatComponent foodStat = on ? character.StatComp : null;
                        on &= foodStat?.Food != null;
                        if (on) r = foodStat.FoodRatio;
                        break;
                    case Energy:
                        if (on) r = character.SuitEnergyLevel;
                        break;
                    case Oxygen:
                        // The suit's tank is breathed from, and drains, with the helmet closed (HelmetEnabled: the game's gas sink asks for
                        // oxygen only then, and NeedsOxygenFromSuit is the opposite, the helmet open); an open helmet breathes the air.
                        on &= on && character.OxygenComponent != null && character.OxygenComponent.HelmetEnabled && MySession.Static.Settings.EnableOxygen;
                        if (on) r = character.OxygenComponent.GetGasFillLevel(MyCharacterOxygenComponent.OxygenId);
                        break;
                    case Hydrogen:
                        on &= on && character.OxygenComponent != null && character.JetpackComp != null && MySession.Static.Settings.EnableOxygen;
                        if (on) r = character.OxygenComponent.GetGasFillLevel(MyCharacterOxygenComponent.HydrogenId);
                        break;
                }
                bool crit = on && i == Food && character.StatComp.Food.StatDefinition is MyFoodStatDefinition food
                    ? r <= food.CriticalLevel
                    : on && r <= (i == Oxygen ? OxygenCriticalRatio : CriticalRatio);
                // Health's own mark (0.2) differs from the shared 0.1 used by the rest.
                if (on && i == Health)
                    crit = r <= MyCharacterStatComponent.HEALTH_RATIO_CRITICAL;
                statRatio[i] = r;
                statTracked[i] = on;
                if (crit != statCritical[i])
                {
                    statCritical[i] = crit;
                    Log.Info($"Low stat warning: {StatNames[i]} {(crit ? "critical - flashing at the HUD's edge" : "back above critical")}");
                }
            }
        }

        /// <summary>MyStatControlImage.Draw: which stat this icon belongs to, by matching its drawn ratio against the character's own (no name on the control itself).</summary>
        private static void WatchIcon(MyStatControlImage __instance)
        {
            if (__instance.StatMaxValue <= 0f || __instance.Texture == null || string.IsNullOrEmpty(__instance.Texture.Path))
                return;
            float r = __instance.StatCurrent / __instance.StatMaxValue;
            for (int i = 0; i < StatCount; i++)
            {
                if (statTracked[i] && Math.Abs(r - statRatio[i]) < 0.0005f)
                {
                    statIcon[i] = __instance.Texture.Path;
                    break;
                }
            }
        }

        /// <summary>MyRenderComponentCharacter.UpdateLightProperties: the local character's lamp sputters the same way the HUD flickers, before the game's own fade turns it off at zero.</summary>
        private static void FlickerLamp(MyRenderComponentCharacter __instance, ref float currentLightPower)
        {
            if (!tracked || currentLightPower <= 0f || !StereoPatches.VRPicture)
                return;
            MyCharacter character = __instance.Container?.Entity as MyCharacter;
            if (character == null || character != MySession.Static?.LocalCharacter)
                return;
            float r = ratio;
            if (r >= LowRatio)
                return;
            float severity = 1f - MathHelper.Clamp(r / LowRatio, 0f, 1f);
            if (Flicker(Stopwatch.GetTimestamp(), severity))
                currentLightPower *= 0.3f;
        }

        // ---- Any thread: the HUD's and the lamp's level, and the indicators' icons ----

        /// <summary>
        /// How strongly the HUD (and, through <see cref="WristStatus.Brightness"/>, the wrist screen) shows: 1 full, down to
        /// <see cref="DimFloor"/> near empty with brief flickers below the game's own low mark, 0 at the game's own "no power"
        /// mark, and the visor's own power-on ramp (<see cref="HudVisor.PowerOnLevel"/>) for a few seconds after it recharges
        /// from empty. 1 when the setting is off or nothing is tracked.
        /// </summary>
        public static float Level
        {
            get
            {
                if (!tracked)
                    return 1f;
                long now = Stopwatch.GetTimestamp();
                long restored = Interlocked.Read(ref restoredAt);
                if (restored != 0)
                {
                    double t = (now - restored) / (HudVisor.PowerOnSeconds * Stopwatch.Frequency);
                    if (t < 1.0)
                        return MathHelper.Clamp(HudVisor.PowerOnLevel(t), 0f, 1f);
                }
                float r = ratio;
                if (r <= 0f)
                    return 0f;
                float level = r < CriticalRatio ? MathHelper.Lerp(DimFloor, 1f, r / CriticalRatio) : 1f;
                if (r < LowRatio)
                {
                    float severity = 1f - MathHelper.Clamp(r / LowRatio, 0f, 1f);
                    if (Flicker(now, severity))
                        level *= 0.35f;
                }
                return MathHelper.Clamp(level, 0f, 1f);
            }
        }

        /// <summary>The suit's battery is empty and the HUD is dark for it (the game's own "no power" mark, Suit power failing on). Any thread.</summary>
        public static bool Out => tracked && ratio <= 0f;

        /// <summary>A short electrical dip, not continuous noise: more often the higher <paramref name="severity"/> (0 none, 1 worst), each cycle's dip placed a little differently so it does not read as a metronome.</summary>
        private static bool Flicker(long now, float severity)
        {
            if (severity <= 0f)
                return false;
            double seconds = now / (double)Stopwatch.Frequency;
            double period = MathHelper.Lerp(3.5, 0.3, MathHelper.Clamp(severity, 0f, 1f));
            long cycle = (long)(seconds / period);
            double phase = seconds - cycle * period;
            double start = period * (0.25 + 0.5 * Hash01(cycle));
            const double dip = 0.07;
            return phase >= start && phase < start + dip;
        }

        private static double Hash01(long x)
        {
            unchecked
            {
                ulong h = (ulong)x * 2654435761UL;
                h ^= h >> 15;
                return (h & 0xFFFFFF) / (double)0x1000000;
            }
        }

        // ---- Render thread: the low-stat indicators, drawn into the visor's one display (HudVisor.Show) ----

        private const float IndicatorSize = 64f, IndicatorGap = 12f, IndicatorMargin = 28f;

        /// <summary>Every critical stat but energy, stacked down the HUD's right edge, each flashing at a calm, shared rate. Drawn before the suit-power fade, so they dim and flicker with it.</summary>
        public static void DrawIndicators(object target, Vector2I size)
        {
            if (!Blink(Stopwatch.GetTimestamp()))
                return;
            int shown = 0;
            for (int i = 0; i < StatCount; i++)
            {
                if (i == Energy || !statCritical[i] || statIcon[i] == null)
                    continue;
                DrawOne(target, size, i, shown);
                shown++;
            }
        }

        /// <summary>
        /// The energy indicator alone, drawn after the suit-power fade so it is not dimmed by its own effect: the last thing
        /// visible as the HUD goes dark, cut only at the same instant everything else is (<see cref="Level"/> reaching 0).
        /// </summary>
        public static void DrawEnergyIndicator(object target, Vector2I size)
        {
            if (!statCritical[Energy] || statIcon[Energy] == null || !Blink(Stopwatch.GetTimestamp()))
                return;
            DrawOne(target, size, Energy, StatCount - 1);
        }

        private static void DrawOne(object target, Vector2I size, int stat, int row)
        {
            float top = size.Y * 0.5f - (StatCount * (IndicatorSize + IndicatorGap)) * 0.5f + row * (IndicatorSize + IndicatorGap);
            var dest = new RectangleF(size.X - IndicatorSize - IndicatorMargin, top, IndicatorSize, IndicatorSize);
            GameSprites.DrawTextures(target, size, Color.White, null, (statIcon[stat], dest));
        }

        /// <summary>A calm, steady on/off: about 0.6 s on, 0.6 s off - a warning light, not a strobe.</summary>
        private static bool Blink(long now) => (now / (Stopwatch.Frequency * 6 / 10)) % 2 == 0;
    }
}
