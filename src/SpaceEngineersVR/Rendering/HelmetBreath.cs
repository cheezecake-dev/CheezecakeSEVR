// HelmetBreath: hearing yourself breathe inside the closed helmet, calm at rest and harder after a sprint, from the game's own breathing sounds.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using VRage.Audio;
using VRage.Data.Audio;
using VRage.Game;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// With the local character's helmet closed (<see cref="HelmetState"/>), the player hears their own breathing, flat inside the
    /// head on an emitter of its own: calm at rest, heavy while sprinting and for <see cref="HeavyAfter"/> seconds after, and
    /// heavy and louder as the suit's air runs low (<see cref="Common.SuitAir"/>, from the game's own low mark, as its HUD warns),
    /// silent once the suit is empty, where the game's own suffocation and the heartbeat in the hands take over. Loud as
    /// <see cref="VRSettings.HelmetBreathing"/> says (<see cref="LevelNames"/>), off with it at Off, with the helmet open, the
    /// character dead or gone. Only this machine plays it, so it tells no one anything; it changes nothing in the game.
    /// <para>
    /// The sounds are the game's own, by cue id: the first of <see cref="CalmCues"/> and of <see cref="HeavyCues"/> the game has
    /// loaded. A looped cue plays until the breathing changes; a one-shot is played again each time it ends. When the game has none
    /// of them, nothing is played, and the log names each cue the game has loaded whose name has "breath" in it, to choose from.
    /// RenderDebug HelmetBreath=0 turns it off whatever the setting.
    /// </para>
    /// </summary>
    internal static class HelmetBreath
    {
        /// <summary>The levels, in the options screen's order; <see cref="VRSettings.HelmetBreathing"/> is an index into it.</summary>
        public static readonly string[] LevelNames = { "Off", "Quiet", "Normal" };

        /// <summary>Each level's share of the cue's own volume.</summary>
        private static readonly float[] LevelVolumes = { 0f, 0.45f, 1f };

        /// <summary>The game's breathing cues, at rest and heavy, the first one loaded of each used.</summary>
        private static readonly string[] CalmCues = { "PlayVocBreath1L", "PlayBreathCalm", "PlayVocBreathCalm" };
        private static readonly string[] HeavyCues = { "PlayVocBreath2L", "PlayBreathHeavy", "PlayVocBreathHeavy" };

        /// <summary>Seconds the breathing stays heavy after a sprint ends.</summary>
        private const double HeavyAfter = 6.0;

        /// <summary>The strain as the suit's air runs low is taken in this many steps, so the sound is not started again every frame.</summary>
        private const int StrainSteps = 4;

        /// <summary>How much louder the breathing is with the suit all but empty: this much more than its level.</summary>
        private const float StrainLouder = 0.6f;

        /// <summary>Debug: no breathing, whatever the setting (<see cref="RenderDebug"/> HelmetBreath=0).</summary>
        public static bool Off { get; set; }

        private static readonly FeatureGuard guard = new FeatureGuard(nameof(HelmetBreath));

        // Game thread: the emitter and its character, the cue on it and at what volume, and when the last sprint was.
        private static MyEntity3DSoundEmitter emitter;
        private static MyCharacter emitterFor;
        private static MyCueId playing;
        private static float playingVolume;
        private static long sprintAt;

        // The cues found, once: null for a breath the game has none of.
        private static bool looked;
        private static MyCueId? calm, heavy;
        private static string calmName, heavyName;

        /// <summary>
        /// Game thread, every frame the game draws (<see cref="HelmetState"/>): breathes for <paramref name="character"/> while its
        /// helmet is <paramref name="closed"/>, stops otherwise (a null character: none).
        /// </summary>
        public static void Update(MyCharacter character, bool closed)
        {
            if (!guard.On)
                return;
            try
            {
                int level = VRSettings.HelmetBreathing;
                if (Off || character == null || !closed || level <= 0 || level >= LevelNames.Length || MyAudio.Static == null)
                {
                    Stop();
                    return;
                }
                Find();
                long now = Stopwatch.GetTimestamp();
                if (character.CurrentMovementState == MyCharacterMovementEnum.Sprinting)
                    sprintAt = now;
                // Short of air: harder and louder as the suit runs low; with the suit empty, the game's own suffocation is heard instead.
                float shortOfAir = Common.SuitAir.Shortness(character, out bool empty);
                if (empty)
                {
                    Stop();
                    return;
                }
                float strain = (float)Math.Ceiling(shortOfAir * StrainSteps) / StrainSteps;
                bool hard = strain > 0f || (sprintAt != 0 && now - sprintAt < (long)(HeavyAfter * Stopwatch.Frequency));
                MyCueId? wanted = hard && heavy.HasValue ? heavy : calm;
                if (!wanted.HasValue)
                {
                    Stop();
                    return;
                }
                MySoundData data = MyAudio.Static.GetCue(wanted.Value);
                if (data == null)
                    return;
                float volume = data.Volume * LevelVolumes[level] * (1f + StrainLouder * strain);
                MyEntity3DSoundEmitter voice = EmitterFor(character);
                if (playing.Equals(wanted.Value) && volume == playingVolume && voice.IsPlaying)
                    return;
                voice.CustomVolume = volume;
                voice.PlaySoundWithDistance(wanted.Value, stopPrevious: true, skipIntro: false, force2D: true, useDistanceCheck: true,
                    alwaysHearOnRealistic: false, skipToEnd: false, force3D: true);
                playing = wanted.Value;
                playingVolume = volume;
            }
            catch (Exception e)
            {
                guard.Failed(e, "Helmet breathing failed");
            }
        }

        private static void Stop()
        {
            if (emitter != null && !playing.IsNull)
                emitter.StopSound(forced: false);
            playing = default;
        }

        /// <summary>The emitter for this character: one of its own, made again for a new character and the old one stopped.</summary>
        private static MyEntity3DSoundEmitter EmitterFor(MyCharacter character)
        {
            if (emitter != null && ReferenceEquals(emitterFor, character))
                return emitter;
            emitter?.StopSound(forced: true, cleanUp: true, cleanupSound: true);
            emitter = new MyEntity3DSoundEmitter(character);
            emitterFor = character;
            playing = default;
            return emitter;
        }

        /// <summary>Finds the breathing cues the game has loaded, once, and logs what was found.</summary>
        private static void Find()
        {
            if (looked)
                return;
            looked = true;
            calm = First(CalmCues, out calmName);
            heavy = First(HeavyCues, out heavyName);
            if (calm.HasValue)
            {
                Log.Info($"Helmet breathing: at rest {calmName}, sprinting {heavyName ?? "the same"}; " +
                         "flat inside the helmet while it is closed (Options > VR > Display > Breathing in the helmet; RenderDebug HelmetBreath=0)");
                return;
            }
            Log.Warn($"Helmet breathing: the game has none of the breathing cues {string.Join(", ", CalmCues)} loaded, so there is no breathing; " +
                     $"the cues it has with \"breath\" in their name: {string.Join(", ", LoadedBreathCues())}");
        }

        private static MyCueId? First(string[] names, out string found)
        {
            foreach (string name in names)
            {
                MyCueId id = MyAudio.Static.GetCueId(name);
                if (!id.IsNull && MyAudio.Static.GetCue(id) != null)
                {
                    found = name;
                    return id;
                }
            }
            found = null;
            return null;
        }

        /// <summary>The ids of the loaded cues with "breath" in their name, for the log (the sound engine's CueDefinitions); none when they cannot be read.</summary>
        private static IEnumerable<string> LoadedBreathCues()
        {
            var found = new List<string>();
            try
            {
                PropertyInfo cues = MyAudio.Static.GetType().GetProperty("CueDefinitions", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (cues?.GetValue(MyAudio.Static) is IEnumerable all)
                {
                    foreach (object cue in all)
                    {
                        // Its id by name through reflection: this list is for the log, and must not tie the build to the member.
                        Type type = cue?.GetType();
                        object id = type?.GetProperty("SubtypeId")?.GetValue(cue) ?? type?.GetField("SubtypeId")?.GetValue(cue);
                        string name = id?.ToString();
                        if (name != null && name.IndexOf("breath", StringComparison.OrdinalIgnoreCase) >= 0)
                            found.Add(name);
                    }
                }
            }
            catch (Exception e)
            {
                found.Add($"(could not be read: {e.Message})");
            }
            if (found.Count == 0)
                found.Add("(none found)");
            return found;
        }
    }
}
