using System;
using System.Diagnostics;
using HarmonyLib;
using Sandbox.Game.Components;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The blood overlay's strength over time (Textures\Gui\Blood.dds, dark red at the edges of the view and clear in the
    /// middle, laid over each whole eye by <see cref="FullView"/>): a slow, smooth pulse while the character is at low
    /// health and a short swell on a hit, in place of the game's flashes.
    /// </summary>
    /// <remarks>
    /// The game (MyRenderComponentCharacter.GetHUDBloodAlpha) holds the overlay steady while health is at or below
    /// MyCharacterStatComponent.HEALTH_RATIO_CRITICAL (20%): 0.3 at 20%, rising in a straight line to full at 6% and
    /// below. On every loss of health (MyCharacterStatComponent.OnDamage, MyRenderComponentCharacter.Damage) it jumps to
    /// full in one frame, falls in a straight line by 1/60 of 0.8 every drawn frame (0.8 s at 60 fps, less at a
    /// headset's rate), and then jumps back up to the low-health level. Suffocating, a loss every 100 simulation frames
    /// (1.7 s), or under fire, that is a full-strength red flash at the edges of the view every second or so: a strobe in
    /// the periphery, where the eye is most sensitive to flicker.
    /// <para>
    /// Here, while the character is alive, the alpha is one swell a beat at 0.5 Hz at the threshold, up to 0.7 Hz at no
    /// health, eased in and out (smoothstep: no step and no corner in it), between a trough that never reaches clear and
    /// a peak well short of full; it fades in and out over a second when health crosses the threshold. A hit adds a
    /// swell that eases up over 0.2 s and down over 0.8 s, from wherever the last one had got to, so a stream of hits
    /// holds it up rather than strobing it. The sum never goes above <see cref="Most"/>. The game's texture and its
    /// threshold are kept; death (the cause of death screen and the respawn wait) is left to the game.
    /// </para>
    /// <para>
    /// Blood.dds alone is faint: 0.48 alpha at most, at the very edge, and its fall to clear is spread over the whole width
    /// of the view, so even the game's full alpha covers under half of the edge. <see cref="FullView"/> therefore lays the
    /// sprite <see cref="Layers"/> deep and squeezes its fall into the outer <see cref="Reach"/> of the view; the middle
    /// of the view stays clear. Not done for the game's own alpha (BloodPulse=0, death): <see cref="Reach"/> is 0 then.
    /// </para>
    /// Debug (<see cref="RenderDebug"/>): BloodPulse=0 gives the game's own alpha back; BloodTest=N draws it as if
    /// health were N percent; BloodPhase=N holds the beat N percent of the way through (0 the trough, 40 the peak);
    /// BloodHit=N adds a hit every N ms.
    /// </remarks>
    internal static class BloodPulse
    {
        /// <summary>Debug: the game's own alpha, flashes and all (<see cref="RenderDebug"/> BloodPulse=0).</summary>
        public static bool Off { get; set; }

        /// <summary>Debug: the health, 0 to 1, the overlay is drawn for in place of the character's (<see cref="RenderDebug"/> BloodTest=N, percent).</summary>
        public static float? Test { get; set; }

        /// <summary>Debug: the beat held at this share of the way through, 0 to 1 (<see cref="RenderDebug"/> BloodPhase=N, percent).</summary>
        public static float? TestPhase { get; set; }

        /// <summary>Debug: a hit every this many seconds while the overlay is drawn (<see cref="RenderDebug"/> BloodHit=N, ms).</summary>
        public static float? TestHits { get; set; }

        /// <summary>
        /// Main thread: the share of the view's width and height the tint reaches in from each edge, for the sprite
        /// <see cref="Alpha"/> has just given the game, or 0 when it is the game's own alpha (the sprite is then drawn whole).
        /// </summary>
        public static float Reach { get; private set; }

        // ---- Tuning ----

        /// <summary>Beats a second at the threshold and at no health: a resting heart is about 1, flicker starts at about 3.</summary>
        public const float SlowRate = 0.5f, FastRate = 0.7f;

        /// <summary>The share of a beat spent rising; the rest is the slower fall.</summary>
        public const float RiseShare = 0.4f;

        /// <summary>
        /// The sprite's alpha at the bottom and the top of a beat, at the threshold and at no health (the game: a steady
        /// 0.3 at the threshold, full from 6% down). Blood.dds is 0.48 alpha at its very edge and <see cref="Layers"/> are
        /// laid, so the edge is covered 1 - (1 - 0.48 alpha)^2: 0.07 to 0.46 at the threshold, 0.15 to 0.66 at no health.
        /// </summary>
        public const float Trough = 0.08f, Peak = 0.55f, TroughZero = 0.16f, PeakZero = 0.86f;

        /// <summary>How many times <see cref="FullView"/> lays the sprite over itself.</summary>
        public const int Layers = 2;

        /// <summary>
        /// How far in from each edge the tint reaches, as a share of the view's width and height, at the threshold and at
        /// no health. The tint is Blood.dds's whole fall from its edge to its middle squeezed into this much of the view.
        /// </summary>
        public const float ReachSlow = 0.22f, ReachFast = 0.30f;

        /// <summary>A hit's swell: added alpha at its top, and its rise and fall, seconds (the game: full alpha, 0.8 s straight down).</summary>
        public const float HitSwell = 0.35f, HitRise = 0.2f, HitFall = 0.8f;

        /// <summary>The most the sprite's alpha is ever drawn at, pulse and hits together (the game: 1); the edge is then covered 0.70.</summary>
        public const float Most = 0.95f;

        /// <summary>The pulse fades in, or out, over this long when health crosses the threshold, seconds.</summary>
        public const float FadeSeconds = 1f;

        /// <summary>A change in health moves the pulse's rate and strength 90% of the way in this long, seconds.</summary>
        public const float SeveritySeconds = 0.5f;

        /// <summary>Below this the overlay is not drawn at all.</summary>
        public const float Visible = 0.005f;

        /// <summary>Hit lines in the log, at most, a run (only hits that start a swell from rest are logged).</summary>
        private const int MaxHitLines = 100;

        // ---- What the alpha does: pure, so it can be checked without a game. ----

        /// <summary>Where the pulse and the last hit have got to.</summary>
        internal struct State
        {
            public double Time, Phase, HitStart, TestHitAt;
            public float Severity, Envelope, HitFrom, Reach;
            public bool Started, Pulsing;
        }

        /// <summary>How far into the low-health range <paramref name="health"/> is: 0 at the threshold, 1 at no health.</summary>
        public static float Severity(float health, float critical) =>
            critical > 0f ? Clamp01((critical - health) / critical) : 0f;

        public static float Rate(float severity) => Lerp(SlowRate, FastRate, severity);

        public static float TroughAt(float severity) => Lerp(Trough, TroughZero, severity);

        public static float PeakAt(float severity) => Lerp(Peak, PeakZero, severity);

        public static float ReachAt(float severity) => Lerp(ReachSlow, ReachFast, severity);

        /// <summary>One beat's shape, 0 to 1 at <paramref name="phase"/> (0 to 1): eased up over <see cref="RiseShare"/>, eased down over the rest.</summary>
        public static float Beat(double phase)
        {
            float p = (float)(phase - Math.Floor(phase));
            return p < RiseShare ? Ease(p / RiseShare) : 1f - Ease((p - RiseShare) / (1f - RiseShare));
        }

        /// <summary>A hit's swell, 0 to 1, <paramref name="since"/> seconds after it, starting from <paramref name="from"/>.</summary>
        public static float Swell(double since, float from)
        {
            if (!(since >= 0d) || since >= HitRise + HitFall)
                return 0f;
            if (since < HitRise)
                return from + (1f - from) * Ease((float)(since / HitRise));
            return 1f - Ease((float)((since - HitRise) / HitFall));
        }

        /// <summary>A hit at <paramref name="now"/>: a new swell, from where the last one has got to.</summary>
        public static void Hit(ref State state, double now)
        {
            state.HitFrom = Swell(now - state.HitStart, state.HitFrom);
            state.HitStart = now;
        }

        /// <summary>
        /// One step at <paramref name="now"/> seconds for a live character at <paramref name="health"/> (0 to 1), with the
        /// low-health threshold <paramref name="critical"/>. Returns the overlay's alpha.
        /// </summary>
        public static float Advance(ref State state, double now, float health, float critical, float? heldPhase)
        {
            if (!state.Started)
            {
                state.Started = true;
                state.Time = now;
                state.HitStart = double.NegativeInfinity;
                state.Reach = ReachSlow;
            }
            float seconds = (float)Math.Max(0d, Math.Min(now - state.Time, 0.1d)); // a stall must not jump the beat
            state.Time = now;

            bool low = health <= critical;
            if (low && state.Envelope <= 0f)
            {
                state.Severity = Severity(health, critical); // a new pulse starts at its own strength, at the bottom of a beat
                state.Phase = 0d;
            }
            else if (low)
            {
                state.Severity = Follow(state.Severity, Severity(health, critical), seconds);
            }
            state.Envelope = Clamp01(state.Envelope + (low ? seconds : -seconds) / FadeSeconds);
            state.Reach = Follow(state.Reach, ReachAt(state.Envelope > 0f ? state.Severity : 0f), seconds); // eased: a new pulse's severity is a step
            state.Phase = (state.Phase + seconds * Rate(state.Severity)) % 1d;

            float beat = Beat(heldPhase ?? state.Phase);
            float pulse = Ease(state.Envelope) * Lerp(TroughAt(state.Severity), PeakAt(state.Severity), beat);
            float alpha = Math.Min(Most, pulse + HitSwell * Swell(now - state.HitStart, state.HitFrom));
            return alpha < Visible ? 0f : alpha;
        }

        /// <summary>Moves <paramref name="level"/> toward <paramref name="target"/>, 90% of the way in <see cref="SeveritySeconds"/>.</summary>
        private static float Follow(float level, float target, float seconds) =>
            level + (target - level) * (1f - (float)Math.Exp(-seconds * Math.Log(10d) / SeveritySeconds));

        /// <summary>Smoothstep: no slope at either end, so a beat or a swell has no corner where it turns.</summary>
        private static float Ease(float t)
        {
            t = Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        private static float Lerp(float from, float to, float t) => from + (to - from) * t;

        private static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;

        // ---- The game's side ----

        private static State state;
        private static MyCharacter character;
        private static bool failed;
        private static int hitLines;

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(MyRenderComponentCharacter), nameof(MyRenderComponentCharacter.GetHUDBloodAlpha)),
                prefix: new HarmonyMethod(typeof(BloodPulse), nameof(Alpha)));
            harmony.Patch(AccessTools.Method(typeof(MyRenderComponentCharacter), nameof(MyRenderComponentCharacter.Damage)),
                postfix: new HarmonyMethod(typeof(BloodPulse), nameof(Damaged)));
            Log.Info($"Blood overlay: pulse hooked (at or below {MyCharacterStatComponent.HEALTH_RATIO_CRITICAL * 100:0}% health one swell a beat, " +
                $"{SlowRate:0.00} Hz and alpha {Trough:0.00}-{Peak:0.00} at the threshold to {FastRate:0.00} Hz and {TroughZero:0.00}-{PeakZero:0.00} at none, " +
                $"{RiseShare * 100:0}% of a beat up; the sprite {Layers} deep, its fall squeezed into the outer {ReachSlow * 100:0}-{ReachFast * 100:0}% of the view; " +
                $"a hit +{HitSwell:0.00} over {HitRise:0.0} s up and {HitFall:0.0} s down; never above {Most:0.00}; " +
                "the game's: steady 0.30-1.00, a hit full and 0.8 s straight down; BloodPulse=0 gives it back)");
        }

        private static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        /// <summary>
        /// Main thread, MyGuiControlBloodOverlay.Draw, for the local character: the alpha while it is alive. A dead
        /// character's (the cause of death screen, the respawn wait) is left to the game, and so is everything after a failure
        /// and before the picture is VR's (<see cref="StereoPatches.VRPicture"/>).
        /// </summary>
        private static bool Alpha(MyRenderComponentCharacter __instance, ref float __result)
        {
            if (failed || !StereoPatches.VRPicture)
                return true;
            try
            {
                if (Off || !(__instance.Entity is MyCharacter current) || current.IsDead)
                {
                    Stop(Off ? "BloodPulse=0" : "dead");
                    return true;
                }
                double now = Now();
                if (!ReferenceEquals(current, character))
                {
                    Stop("another character");
                    character = current;
                }
                if (TestHits is float every && now - state.TestHitAt >= every)
                {
                    state.TestHitAt = now;
                    Damage(now, "BloodHit");
                }
                float critical = MyCharacterStatComponent.HEALTH_RATIO_CRITICAL;
                float health = Test ?? current.StatComp?.HealthRatio ?? 1f;
                __result = Advance(ref state, now, health, critical, TestPhase);
                Reach = state.Reach;

                bool low = health <= critical;
                if (low != state.Pulsing)
                {
                    state.Pulsing = low;
                    float severity = low ? Severity(health, critical) : state.Severity;
                    string test = Test.HasValue ? " (BloodTest)" : "";
                    string pulse = $"{Rate(severity):0.00} Hz, alpha {TroughAt(severity):0.00}-{PeakAt(severity):0.00} " +
                        $"(edge covered {FullView.Coverage(0f, TroughAt(severity), ReachAt(severity)):0.00}-{FullView.Coverage(0f, PeakAt(severity), ReachAt(severity)):0.00}, " +
                        $"reaching {ReachAt(severity) * 100:0}% in)";
                    Log.Info(low
                        ? $"Blood pulse: on at health {health * 100:0}%{test}: {pulse}, fading in over {FadeSeconds:0.0} s" +
                            (TestPhase is float held ? $", beat held {held * 100:0}% through" : "")
                        : $"Blood pulse: off at health {health * 100:0}%{test}, fading out over {FadeSeconds:0.0} s (was {pulse})");
                }
                return false;
            }
            catch (Exception e)
            {
                failed = true;
                Reach = 0f;
                Log.Error(e, "Blood overlay pulse failed; the game's own overlay is back for the rest of the run");
                return true;
            }
        }

        /// <summary>Main thread, any character losing health: a swell, when it is the local character.</summary>
        private static void Damaged(MyRenderComponentCharacter __instance)
        {
            if (Off || failed || !ReferenceEquals(__instance.Entity, character) || !ReferenceEquals(character, MySession.Static?.LocalCharacter))
                return;
            Damage(Now(), "hit");
        }

        private static void Damage(double now, string what)
        {
            if (!state.Started)
                return; // nothing drawn yet: Advance sets the swell's start
            bool fromRest = Swell(now - state.HitStart, state.HitFrom) <= 0f;
            Hit(ref state, now);
            if (fromRest && hitLines < MaxHitLines)
            {
                hitLines++;
                Log.Info($"Blood pulse: {what}, a swell of +{HitSwell:0.00} over {HitRise + HitFall:0.0} s" +
                    (hitLines == MaxHitLines ? " (no more of these lines this run)" : ""));
            }
        }

        /// <summary>Forgets the pulse and any swell, so a new character or the game's own alpha does not start mid-beat.</summary>
        private static void Stop(string why)
        {
            if (state.Pulsing)
                Log.Info($"Blood pulse: off ({why})");
            state = default;
            character = null;
            Reach = 0f;
        }
    }
}
