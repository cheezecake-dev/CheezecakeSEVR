// HelmetBreath: the game's own breathing in the closed helmet, as loud as the setting says, and straining as the suit's air runs low.
using System;
using System.Collections.Generic;
using HarmonyLib;
using Sandbox.Game.Components;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using VRage.Audio;
using VRage.Data.Audio;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The breathing heard inside the closed helmet is the game's own (MyCharacterBreath, for the local character only): calm at
    /// rest, harder running and sprinting and for a while after, choking when the suit is empty, and silent with the helmet open in
    /// air (MyCharacterSoundComponent.UpdateBreath and SelectSound set its state each frame; MyCharacterBreath.Update plays it,
    /// flat). It is adapted, not played again beside it:
    /// <list type="bullet">
    /// <item>How loud, by <see cref="VRSettings.HelmetBreathing"/> (<see cref="LevelNames"/>): Normal is the game's own volume, Quiet
    /// less (its breathing cues' volumes, MySoundData.Volume, scaled on this machine), and Off stops the breathing (its state to
    /// NoBreath) but never the choking, which is the game's own warning that the suit is out of air.</item>
    /// <item>Short of air (<see cref="Common.SuitAir"/>, from the game's own low mark, as its HUD warns): the breathing strains as the
    /// suit runs low, as it does after a sprint (its state raised to Heated, then to VeryHeated from <see cref="StrainedFrom"/>).</item>
    /// <item>Its sounds: the game asks for its breathing sounds but no longer defines them, so they are made from its own
    /// recordings (<see cref="BreathCues"/>), recorded as heard inside the helmet, as its choking is, in any world's sound setting.</item>
    /// </list>
    /// Only this machine hears it, so it tells no one anything; it changes nothing in the game. RenderDebug HelmetBreath=0 leaves the
    /// game's breathing as the game has it.
    /// </summary>
    internal static class HelmetBreath
    {
        /// <summary>The levels, in the options screen's order; <see cref="VRSettings.HelmetBreathing"/> is an index into it.</summary>
        public static readonly string[] LevelNames = { "Off", "Quiet", "Normal" };

        /// <summary>Each level's share of the game's own volume for its breathing (Off silences it by its state, so its share is unused).</summary>
        private static readonly float[] LevelVolumes = { 1f, 0.45f, 1f };

        /// <summary>How short of air the suit is (<see cref="Common.SuitAir.Shortness"/>) from which the breathing is at its hardest.</summary>
        private const float StrainedFrom = 0.5f;


        /// <summary>Debug: the game's breathing as the game has it, whatever the setting (<see cref="RenderDebug"/> HelmetBreath=0).</summary>
        public static bool Off { get; set; }

        private static readonly FeatureGuard guard = new FeatureGuard(nameof(HelmetBreath));

        // The game's breathing (MyCharacterBreath): the voice playing, and its sounds at rest, heavy, and choking as the suit's air
        // runs out.
        private static readonly AccessTools.FieldRef<MyCharacterBreath, IMySourceVoice> Voice =
            AccessTools.FieldRefAccess<MyCharacterBreath, IMySourceVoice>("m_sound");

        private static readonly AccessTools.FieldRef<MyCharacterBreath, MySoundPair> BreathCalm = PairField("m_breathCalm"),
            BreathHeavy = PairField("m_breathHeavy"), Choking = PairField("m_oxygenChokeNormal");

        private static AccessTools.FieldRef<MyCharacterBreath, MySoundPair> PairField(string name) => AccessTools.FieldRefAccess<MyCharacterBreath, MySoundPair>(name);

        // Game thread: each sound's own volume; the breathing given its share, and that share.
        private static readonly Dictionary<MyCueId, float> ownVolumes = new Dictionary<MyCueId, float>();
        private static MyCharacterBreath givenFor;
        private static float givenShare = 1f;
        private static bool logged;

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(MyCharacterSoundComponent), "UpdateBreath")
                    ?? throw new MissingMethodException(nameof(MyCharacterSoundComponent), "UpdateBreath"),
                postfix: new HarmonyMethod(typeof(HelmetBreath), nameof(AfterUpdateBreath)));
            BreathCues.Patch(harmony);
            Log.Info("Helmet breathing: the game's own breathing, adapted (Options > VR > Display > Breathing in the helmet: Off, Quiet, Normal; it strains as the suit's air runs low; RenderDebug HelmetBreath=0 leaves it as the game has it)");
        }

        /// <summary>
        /// Postfix of MyCharacterSoundComponent.UpdateBreath, after the game set the local character's breathing for this frame (on
        /// the game's parallel update): Off silences it, and short of air it strains. Choking and silence are left as they are.
        /// </summary>
        private static void AfterUpdateBreath(MyCharacterSoundComponent __instance)
        {
            if (Off || !guard.On)
                return;
            try
            {
                if (!(__instance.Entity is MyCharacter character) || !ReferenceEquals(character, MySession.Static?.LocalCharacter))
                    return;
                MyCharacterBreath breath = character.Breath;
                MyCharacterBreath.State state = breath?.CurrentState ?? MyCharacterBreath.State.NoBreath;
                if (state != MyCharacterBreath.State.Calm && state != MyCharacterBreath.State.Heated && state != MyCharacterBreath.State.VeryHeated)
                    return;
                if (VRSettings.HelmetBreathing <= 0)
                {
                    breath.CurrentState = MyCharacterBreath.State.NoBreath;
                    return;
                }
                float shortOfAir = Common.SuitAir.Shortness(character, out bool empty);
                if (empty || shortOfAir <= 0f)
                    return;
                MyCharacterBreath.State strained = shortOfAir >= StrainedFrom ? MyCharacterBreath.State.VeryHeated : MyCharacterBreath.State.Heated;
                if (strained > state)
                    breath.CurrentState = strained;
            }
            catch (Exception e)
            {
                guard.Failed(e, "Helmet breathing failed; the game's breathing is as the game has it");
            }
        }

        /// <summary>
        /// Game thread, every frame the game draws (<see cref="HelmetState"/>): <paramref name="character"/>'s breathing (null: none)
        /// at the setting's share of its sounds' own volume, with the breath playing now given it too; with RenderDebug
        /// HelmetBreath=0, as the game has it.
        /// </summary>
        public static void Update(MyCharacter character)
        {
            MyCharacterBreath breath = character?.Breath;
            if (!guard.On || breath == null || MyAudio.Static == null)
                return;
            try
            {
                int level = VRSettings.HelmetBreathing;
                float share = Off || level < 0 || level >= LevelVolumes.Length ? 1f : LevelVolumes[level];
                if (ReferenceEquals(breath, givenFor) && share == givenShare)
                    return;
                givenFor = breath;
                givenShare = share;
                Scale(BreathCalm(breath), share);
                Scale(BreathHeavy(breath), share);
                // The breath playing now keeps the sound and volume it started with until it is played again: given the volume.
                IMySourceVoice voice = Voice(breath);
                if (voice != null && voice.IsPlaying)
                {
                    MySoundData data = MyAudio.Static.GetCue(voice.CueEnum);
                    if (data != null)
                        voice.SetVolume(data.Volume);
                }
                if (!logged)
                {
                    logged = true;
                    string mode = MySession.Static?.Settings == null ? "unknown" : MySession.Static.Settings.RealisticSound ? "realistic" : "arcade";
                    Log.Info($"Helmet breathing: the game's own, in a world with {mode} sounds: at rest {Named(BreathCalm(breath))}, " +
                             $"heavy {Named(BreathHeavy(breath))}, choking {Named(Choking(breath))}; {LevelNames[Math.Max(0, Math.Min(level, LevelNames.Length - 1))]} ({share * 100f:0} % of its volume)");
                }
            }
            catch (Exception e)
            {
                guard.Failed(e, "Helmet breathing: its sounds could not be set; the game's breathing is as the game has it");
            }
        }

        /// <summary>The sound the pair plays now, at <paramref name="share"/> of its own volume (its MySoundData, on this machine).</summary>
        private static void Scale(MySoundPair pair, float share)
        {
            MyCueId cue = pair?.SoundId ?? default;
            if (cue.IsNull)
                return;
            MySoundData data = MyAudio.Static.GetCue(cue);
            if (data == null)
                return;
            if (!ownVolumes.TryGetValue(cue, out float own))
                ownVolumes[cue] = own = data.Volume;
            data.Volume = own * share;
        }

        /// <summary>For the log: the pair's cue and the sound it plays now, or that the game has none.</summary>
        private static string Named(MySoundPair pair)
        {
            MyCueId cue = pair?.SoundId ?? default;
            return cue.IsNull || MyAudio.Static.GetCue(cue) == null ? $"{pair?.GetCueName() ?? "none"} (no sound loaded)" : $"{pair.GetCueName()} ({cue})";
        }
    }
}
