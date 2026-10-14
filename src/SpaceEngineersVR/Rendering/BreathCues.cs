// BreathCues: the breathing sounds the game asks for and no longer defines, made from its own choking sound and its own recordings.
using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Sandbox.Definitions;
using SpaceEngineersVR.Common;
using VRage;
using VRage.Collections;
using VRage.Data.Audio;
using VRage.FileSystem;
using VRage.Game;
using VRage.Utils;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The game's breathing (MyCharacterBreath) asks for its sounds by name, PlayVocBreath1L at rest and PlayVocBreath2L heavy (or
    /// what the character's definition names, BreathCalmSoundName and BreathHeavySoundName), but the game's sound definitions no
    /// longer have them, so it breathes in silence. Its recordings are still in its content, beside the choking it does play:
    /// Audio\REAL\PLAYER\RealPlayVocBreath1L2d and RealPlayVocBreath2L2d by RealPlayChokeALoop2d, and the female voice's
    /// (RealFemPlay...). Each one missing is made here from the character's own choking sound (its OxygenChokeNormalSoundName,
    /// else PlayChokeA): a copy of it, so its category, volume, loop, 2D and helmet filters are the game's, with the choking
    /// recording's name changed to the breathing recording's in the same folder (<see cref="ChokeTake"/> to <see cref="CalmTake"/>
    /// or <see cref="HeavyTake"/>).
    /// <para>
    /// Under the plain name, so the game's sound pair (MySoundPair) finds it whatever the world's sound setting: a world with arcade
    /// sounds loads no sound named "Real..." (MyAudioExtensions.GetArcadeSoundDataFromDefinitions). A character whose definition
    /// names its own choking but no breathing (the female voice, if hers is named apart) is given breathing named after its
    /// choking ("PlayFemChokeA" to "PlayFemVocBreath1L"), so it breathes in its own voice.
    /// </para>
    /// <para>
    /// They go into the lists the game loads its sounds from (MyAudioExtensions.GetSoundDataFromDefinitions and the arcade one, at
    /// start-up and at each world's load and unload), not into its sound definitions: nothing is saved or sent, and only this
    /// machine hears them. A sound the game or a mod defines is never replaced; a recording missing from the content (a game
    /// update moved it) is left out, and logged.
    /// </para>
    /// </summary>
    internal static class BreathCues
    {
        // MyCharacterBreath's own names, for a character whose definition names none (its BREATH_CALM, BREATH_HEAVY, OXYGEN_CHOKE_NORMAL).
        private const string GameCalm = "PlayVocBreath1L", GameHeavy = "PlayVocBreath2L", GameChoke = "PlayChokeA";

        /// <summary>The part of the choking recording's name that names the take, and the breathing recordings' in its place.</summary>
        private const string ChokeTake = "ChokeALoop", CalmTake = "VocBreath1L", HeavyTake = "VocBreath2L";

        /// <summary>The part of a choking sound's name that a breathing sound named after it changes (<see cref="CalmTake"/>, <see cref="HeavyTake"/>).</summary>
        private const string ChokeName = "ChokeA";

        private static readonly FeatureGuard guard = new FeatureGuard(nameof(BreathCues));
        private static readonly HashSet<string> logged = new HashSet<string>();

        /// <summary>Hooks the game's sound lists (from <see cref="HelmetBreath.Patch"/>).</summary>
        public static void Patch(Harmony harmony)
        {
            var postfix = new HarmonyMethod(typeof(BreathCues), nameof(WithBreathing));
            foreach (string list in new[] { "GetSoundDataFromDefinitions", "GetArcadeSoundDataFromDefinitions" })
                harmony.Patch(AccessTools.Method(typeof(VRage.Audio.MyAudioExtensions), list)
                        ?? throw new MissingMethodException("MyAudioExtensions", list),
                    postfix: postfix);
        }

        /// <summary>Postfix of the game's sound lists: its sounds, and the breathing sounds made here that it does not have.</summary>
        private static void WithBreathing(ref ListReader<MySoundData> __result)
        {
            if (!guard.On)
                return;
            try
            {
                List<MySoundData> breathing = Make();
                if (breathing.Count == 0)
                    return;
                var all = new List<MySoundData>(__result.Count + breathing.Count);
                foreach (MySoundData data in __result)
                    all.Add(data);
                all.AddRange(breathing);
                __result = all;
            }
            catch (Exception e)
            {
                guard.Failed(e, "Helmet breathing: its sounds could not be made; the game breathes as it has it (in silence)");
            }
        }

        /// <summary>Each character definition's breathing sounds that no sound definition has, made from its choking sound.</summary>
        private static List<MySoundData> Make()
        {
            var made = new List<MySoundData>();
            MyDefinitionManager definitions = MyDefinitionManager.Static;
            if (definitions == null)
                return made;
            var sounds = new Dictionary<string, MySoundData>(StringComparer.OrdinalIgnoreCase);
            foreach (MyAudioDefinition definition in definitions.GetSoundDefinitions())
            {
                if (definition.Enabled && definition.SoundData != null)
                    sounds[definition.SoundData.SubtypeId.String] = definition.SoundData;
            }
            foreach (MyCharacterDefinition character in definitions.Characters)
            {
                string choke = string.IsNullOrEmpty(character.OxygenChokeNormalSoundName) ? GameChoke : character.OxygenChokeNormalSoundName;
                if (string.IsNullOrEmpty(character.BreathCalmSoundName) && string.IsNullOrEmpty(character.BreathHeavySoundName)
                    && !string.Equals(choke, GameChoke, StringComparison.OrdinalIgnoreCase) && choke.Contains(ChokeName))
                {
                    character.BreathCalmSoundName = choke.Replace(ChokeName, CalmTake);
                    character.BreathHeavySoundName = choke.Replace(ChokeName, HeavyTake);
                    Once($"Helmet breathing: {character.Id.SubtypeName} chokes in its own voice ({choke}), so it breathes in it too: " +
                         $"{character.BreathCalmSoundName} and {character.BreathHeavySoundName}");
                }
                Add(made, sounds, string.IsNullOrEmpty(character.BreathCalmSoundName) ? GameCalm : character.BreathCalmSoundName, choke, CalmTake, character);
                Add(made, sounds, string.IsNullOrEmpty(character.BreathHeavySoundName) ? GameHeavy : character.BreathHeavySoundName, choke, HeavyTake, character);
            }
            return made;
        }

        /// <summary>
        /// The breathing sound <paramref name="name"/>, made from the choking sound <paramref name="choke"/> with its recordings'
        /// <see cref="ChokeTake"/> changed to <paramref name="take"/>; nothing when it is had already, or cannot be made.
        /// </summary>
        private static void Add(List<MySoundData> made, Dictionary<string, MySoundData> sounds, string name, string choke, string take, MyCharacterDefinition character)
        {
            if (Find(sounds, name) != null || made.Exists(data => string.Equals(data.SubtypeId.String, name, StringComparison.OrdinalIgnoreCase)))
                return;
            MySoundData source = Find(sounds, choke);
            if (source?.Waves == null || source.Waves.Count == 0)
            {
                Once($"Helmet breathing: no sound {name} for {character.Id.SubtypeName}, and no choking sound {choke} to make it from; it breathes in silence");
                return;
            }
            var waves = new List<MyAudioWave>(source.Waves.Count);
            var files = new List<string>();
            foreach (MyAudioWave wave in source.Waves)
            {
                MyAudioWave copy = ShallowCopy.Of(wave);
                if (!Retake(ref copy.Start, take, files) || !Retake(ref copy.Loop, take, files) || !Retake(ref copy.End, take, files))
                {
                    Once($"Helmet breathing: no sound {name} for {character.Id.SubtypeName}, and the choking sound {source.SubtypeId} has a recording " +
                         $"that is not the choking take ({wave.Start} {wave.Loop} {wave.End}); it breathes in silence");
                    return;
                }
                waves.Add(copy);
            }
            foreach (string file in files)
            {
                if (!InContent(file))
                {
                    Once($"Helmet breathing: no sound {name} for {character.Id.SubtypeName}, and its recording {file} is not in the game's content; it breathes in silence");
                    return;
                }
            }
            MySoundData data = ShallowCopy.Of(source);
            data.SubtypeId = MyStringHash.GetOrCompute(name);
            data.Waves = waves;
            // The choking's distant takes and its own 2D twin are the choking's, not the breathing's.
            data.DistantSounds = null;
            data.Alternative2D = null;
            made.Add(data);
            Once($"Helmet breathing: {name} made from the game's choking sound {source.SubtypeId}, its recording {string.Join(", ", files)}");
        }

        /// <summary>A sound by name, as MySoundPair looks for it: plain, then its realistic and arcade names.</summary>
        private static MySoundData Find(Dictionary<string, MySoundData> sounds, string name) =>
            sounds.TryGetValue(name, out MySoundData plain) ? plain
            : sounds.TryGetValue("Real" + name, out MySoundData real) ? real
            : sounds.TryGetValue("Arc" + name, out MySoundData arcade) ? arcade
            : null;

        /// <summary>A recording's name with the choking take changed to <paramref name="take"/>; false when it names another take. Empty stays empty.</summary>
        private static bool Retake(ref string file, string take, List<string> files)
        {
            if (string.IsNullOrEmpty(file))
                return true;
            int at = file.IndexOf(ChokeTake, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                return false;
            file = file.Substring(0, at) + take + file.Substring(at + ChokeTake.Length);
            files.Add(file);
            return true;
        }

        /// <summary>The recording is in the game's content, as a sound's file names it (from the content folder, or its Audio folder).</summary>
        private static bool InContent(string file) =>
            MyFileSystem.FileExists(Path.Combine(MyFileSystem.ContentPath, file))
            || MyFileSystem.FileExists(Path.Combine(MyFileSystem.ContentPath, "Audio", file));

        private static void Once(string line)
        {
            lock (logged)
            {
                if (!logged.Add(line))
                    return;
            }
            Log.Info(line);
        }
    }
}
