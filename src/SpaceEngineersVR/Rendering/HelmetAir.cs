// HelmetAir: the hiss of air as the helmet opens (depressurize) and closes (repressurize), from the game's own sound cues.
using System;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.World;
using VRage.Audio;
using VRage.Data.Audio;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// With the helmet view on (<see cref="HelmetView"/>, <see cref="VRSettings.HelmetView"/>), opening the local character's helmet
    /// plays a depressurize hiss and closing it plays a repressurize one, inside the helmet: the game's own air-vent sounds, which are
    /// what the game plays for exactly these two things at an air vent (MyAirVent.UpdateSound: BlockAirVentDepressurize while a vent
    /// draws a room's air out, BlockAirVentExhale while it blows air in). Which cue plays for each of the two, and how long and how
    /// loud, can be changed while the game runs, to be chosen by ear (<see cref="RenderDebug"/> HelmetAir, HelmetAirOpen,
    /// HelmetAirClose, HelmetAirMs, HelmetAirVol).
    /// </summary>
    /// <remarks>
    /// <para>
    /// When: the local character's helmet really opened or closed (<see cref="HelmetState"/>, which watches its state, so every way
    /// it changes is heard: the key, the wrist panel, the actions wheel, the grip on the helmet (<see cref="Hands.HeadGestures"/>), a
    /// seat, and on a multiplayer client the server's change arriving through the game's sync). The sound is on an emitter of its
    /// own, so it never cuts the game's own latch cue (PlayHelmetOn or PlayHelmetOff) short. Other players' helmets are left alone.
    /// </para>
    /// <para>
    /// What: the numbered cues in <see cref="Describe"/>. The two vent cues are looped sounds in the game's data (a start, a loop and
    /// an end part, all one 3D wave each, no 2D one). A looped cue is played from its start, the loop runs for
    /// <see cref="Milliseconds"/> and then the cue is played again from its end part only, which fades it away (the game's vent
    /// does the same when it stops). The other cues are one-shots and play to their end. Every cue is played flat, as if inside the
    /// head (an emitter flagged to play 2D whose 3D wave is used when the cue has no 2D one; the engine then gives it an even
    /// output to both ears and no distance or direction), at the cue's own volume scaled by <see cref="VolumePercent"/>.
    /// </para>
    /// <para>
    /// The game also plays its own air escape (PlayChokeInitiate, a short gasp) when the helmet is opened in vacuum, outside any ship
    /// or station, not in creative, with the suit at least half full. It is not a hiss and the two fit together, so the default
    /// depressurize plays over it; only when cue 4, that same cue, is chosen for opening is it left to the game in that case, so it
    /// is not played twice.
    /// </para>
    /// <para>
    /// Not done: an air sound when the helmet is already open as a world loads, or for the characters of other players. No
    /// setting yet: the cues are chosen by ear first. RenderDebug HelmetAir=0 turns it off.
    /// </para>
    /// </remarks>
    internal static class HelmetAir
    {
        /// <summary>The cues that can be chosen, by number (0 is none). Names are the game's own sound cue ids (Audio.sbc, Audio_player.sbc).</summary>
        private static readonly string[] CueNames =
        {
            null,
            "BlockAirVentDepressurize",
            "BlockAirVentExhale",
            "ArcEventAirVent",
            "PlayChokeInitiate",
            "ArcPlayHelmetOn",
            "ArcPlayHelmetOff",
            "RealPlayHelmetOn",
            "RealPlayHelmetOff",
        };

        /// <summary>What each cue is, read from its definition and its wave files, for the log and for <see cref="RenderDebug"/>'s report.</summary>
        private static readonly string[] CueNotes =
        {
            "none: nothing is played",
            "looped (start 0.6 s, loop 1.5 s, end 1.3 s), volume 0.8, mono 3D wave: the air vent drawing a room's air out, a low rushing whoosh",
            "looped (start 0.9 s, loop 1.5 s, end 1.2 s), volume 0.8, mono 3D wave: the air vent blowing air in, a low rushing whoosh",
            "one-shot 2.4 s, volume 0.8, mono 3D wave: the game's air leak, a hiss that swells for 0.4 s and dies away",
            "one-shot 0.5 s, volume 1.3, stereo 2D wave: the game's own air-escape gasp for the helmet opened in vacuum",
            "one-shot 0.7 s, volume 0.6, stereo 2D wave recorded very quietly: the game's own thin hiss for opening, Arcade sound setting",
            "one-shot 0.7 s, volume 0.6, stereo 2D wave recorded very quietly: the game's own thin hiss for closing, Arcade sound setting",
            "one-shot 0.65 s, volume 0.7, stereo 2D wave: the game's own latch for opening, Realistic sound setting (not loaded in Arcade)",
            "one-shot 0.7 s, volume 0.7, stereo 2D wave: the game's own latch for closing, Realistic sound setting (not loaded in Arcade)",
        };

        /// <summary>The cue that is the game's own air escape when the helmet opens in vacuum: not played here, in that case, as well.</summary>
        private const int AirEscapeCue = 4;

        /// <summary>Opening plays the vent drawing air out, closing the vent blowing it in: the game's own use of the two.</summary>
        public const int DefaultOpenCue = 1, DefaultCloseCue = 2;

        /// <summary>
        /// A looped cue's loop runs this long, milliseconds, before its end part fades it away: the start (0.6 and 0.9 s) plays through
        /// and the sound is about two seconds in all, the time the air takes to settle without outlasting the visor's third of a second
        /// by much.
        /// </summary>
        public const int DefaultMilliseconds = 800;

        /// <summary>
        /// The cues' own volumes (0.8) are as loud as a vent beside the player; inside a helmet the hiss is an accent on the latch
        /// (about 0.03 in the same units), not a sound of its own, so half of that.
        /// </summary>
        public const int DefaultVolumePercent = 50;

        private const int MostMilliseconds = 8000, MostVolumePercent = 200;

        /// <summary>Debug: no air sounds, whatever the helmet view setting (<see cref="RenderDebug"/> HelmetAir=0).</summary>
        public static bool Off { get; set; }

        /// <summary>Debug: the cue played as the helmet opens, by number (<see cref="RenderDebug"/> HelmetAirOpen=n).</summary>
        public static int OpenCue { get; set; } = DefaultOpenCue;

        /// <summary>Debug: the cue played as the helmet closes, by number (<see cref="RenderDebug"/> HelmetAirClose=n).</summary>
        public static int CloseCue { get; set; } = DefaultCloseCue;

        /// <summary>Debug: how long a looped cue's loop runs before it fades, milliseconds (<see cref="RenderDebug"/> HelmetAirMs=n).</summary>
        public static int Milliseconds { get; set; } = DefaultMilliseconds;

        /// <summary>Debug: the volume, percent of the cue's own (<see cref="RenderDebug"/> HelmetAirVol=n).</summary>
        public static int VolumePercent { get; set; } = DefaultVolumePercent;

        private static readonly FeatureGuard guard = new FeatureGuard(nameof(HelmetAir));

        // Game thread: the emitter the sounds play on, the character it belongs to, the cue a looped sound is on (to play its end part).
        private static MyEntity3DSoundEmitter emitter;
        private static MyCharacter emitterFor;
        private static MyCueId playing;
        private static int generation;
        private static System.Threading.Timer tailTimer;

        // What was last logged for each of the two, so a line comes at the first sound and again whenever the choice changes.
        private static string loggedOpen, loggedClose;
        private static readonly bool[] missingLogged = new bool[CueNames.Length];

        /// <summary>The control file's values back to how they are built (a name removed from it restores it).</summary>
        public static void Reset()
        {
            Off = false;
            OpenCue = DefaultOpenCue;
            CloseCue = DefaultCloseCue;
            Milliseconds = DefaultMilliseconds;
            VolumePercent = DefaultVolumePercent;
        }

        /// <summary>A cue number held to the list (0, none, for a number outside it).</summary>
        public static int ClampCue(int cue) => cue < 0 || cue >= CueNames.Length ? 0 : cue;

        public static int ClampMilliseconds(int ms) => Math.Max(0, Math.Min(MostMilliseconds, ms));

        public static int ClampVolume(int percent) => Math.Max(0, Math.Min(MostVolumePercent, percent));

        /// <summary>A cue's number, game name and what it is, in one line: "1 BlockAirVentDepressurize (looped ...)".</summary>
        public static string Describe(int cue)
        {
            cue = ClampCue(cue);
            return cue == 0 ? "0 " + CueNotes[0] : $"{cue} {CueNames[cue]} ({CueNotes[cue]})";
        }

        /// <summary>Game thread, from <see cref="HelmetState"/>: the local character's helmet <paramref name="opened"/> or closed; plays the cue chosen for it.</summary>
        public static void Switched(MyCharacter character, bool opened)
        {
            if (!guard.On || Off || !HelmetView.On)
                return;
            try
            {
                Play(character, opened ? OpenCue : CloseCue, opened, opened && GameAirEscapes(character.OxygenComponent, character));
            }
            catch (Exception e)
            {
                guard.Failed(e, "Helmet air could not play");
            }
        }

        /// <summary>The conditions under which the game's SwitchHelmet plays its own air escape as the helmet opens (MyCharacterOxygenComponent).</summary>
        private static bool GameAirEscapes(MyCharacterOxygenComponent oxygen, MyCharacter character)
        {
            MySession session = MySession.Static;
            var atmosphere = character.AtmosphereDetectorComp;
            return session != null && !session.CreativeMode && atmosphere != null && !atmosphere.InAtmosphere
                   && !atmosphere.InShipOrStation && oxygen.SuitOxygenAmount >= 0.5f;
        }

        private static void Play(MyCharacter character, int cue, bool opening, bool gameAirEscapes)
        {
            cue = ClampCue(cue);
            int percent = ClampVolume(VolumePercent);
            if (cue == 0 || percent == 0 || MyAudio.Static == null)
                return;
            if (cue == AirEscapeCue && gameAirEscapes)
                return;
            string name = CueNames[cue];
            MyCueId id = MyAudio.Static.GetCueId(name);
            MySoundData data = id.IsNull ? null : MyAudio.Static.GetCue(id);
            if (data == null)
            {
                if (!missingLogged[cue])
                {
                    missingLogged[cue] = true;
                    Log.Info($"Helmet air: the game has no sound cue {name} loaded (the Arcade sound setting leaves out the Real ones); nothing is played for it");
                }
                return;
            }

            MyEntity3DSoundEmitter voice = EmitterFor(character);
            // The cue's own volume, scaled: a custom volume replaces the cue's in the engine, which is how it is scaled.
            voice.CustomVolume = data.Volume * (percent / 100f);
            // Flat in the head: 2D, and force3D so that a cue with only a 3D wave still plays (as 2D: the engine takes the other
            // dimension's wave when the emitter asks for it and the cue has none of the one wanted). Not stopping the game's
            // own sounds: they are on the game's emitter, not this one.
            voice.PlaySoundWithDistance(id, stopPrevious: true, skipIntro: false, force2D: true, useDistanceCheck: true,
                alwaysHearOnRealistic: false, skipToEnd: false, force3D: true);

            int ms = ClampMilliseconds(Milliseconds);
            bool looped = data.Loopable;
            if (looped)
            {
                playing = id;
                ScheduleTail(ms);
            }
            else
            {
                generation++;
                tailTimer?.Dispose();
                tailTimer = null;
            }

            string summary = $"{name} {(looped ? $"looped, {ms} ms then its end" : "one-shot")} at {data.Volume * percent / 100f:0.##} (cue {data.Volume:0.##} x {percent}%)";
            if (opening ? summary != loggedOpen : summary != loggedClose)
            {
                if (opening)
                    loggedOpen = summary;
                else
                    loggedClose = summary;
                Log.Info($"Helmet air: the helmet {(opening ? "opened" : "closed")}: {(voice.IsPlaying ? "playing" : "could not play (the sound engine gave no voice)")} {summary}, flat inside the helmet; " +
                         $"RenderDebug {(opening ? "HelmetAirOpen" : "HelmetAirClose")}=n changes the cue, HelmetAirMs and HelmetAirVol its length and volume, HelmetAir=0 turns it off");
            }
        }

        /// <summary>The emitter for this character: one of its own, made again for a new character and the old one stopped.</summary>
        private static MyEntity3DSoundEmitter EmitterFor(MyCharacter character)
        {
            if (emitter != null && ReferenceEquals(emitterFor, character))
                return emitter;
            if (emitter != null)
            {
                try
                {
                    emitter.StopSound(forced: true, cleanUp: true, cleanupSound: true);
                }
                catch (Exception e)
                {
                    Log.Warn($"Helmet air: the last character's sound could not be stopped: {e.Message}");
                }
            }
            emitter = new MyEntity3DSoundEmitter(character);
            emitterFor = character;
            return emitter;
        }

        /// <summary>After <paramref name="ms"/> the loop is ended: handed to the game's thread, which is where its sound engine wants to be called.</summary>
        private static void ScheduleTail(int ms)
        {
            int mine = ++generation;
            tailTimer?.Dispose();
            tailTimer = new System.Threading.Timer(_ => MySandboxGame.Static?.Invoke(() => Tail(mine), "HelmetAir tail"), null, ms, System.Threading.Timeout.Infinite);
        }

        /// <summary>
        /// Game thread: plays the looped cue again from its end part only, which stops the loop's voice (it fades in a few frames) and
        /// plays the fade-away. The emitter's own stop would do the same, but plays the end part again without the 2D flag; this keeps
        /// the sound flat to its last moment. Nothing happens if another sound has been played since.
        /// </summary>
        private static void Tail(int mine)
        {
            if (mine != generation || emitter == null || playing.IsNull)
                return;
            generation++;
            try
            {
                if (emitterFor == null || emitterFor.Closed || !ReferenceEquals(emitterFor, MySession.Static?.LocalCharacter))
                {
                    emitter.StopSound(forced: true);
                    return;
                }
                emitter.PlaySoundWithDistance(playing, stopPrevious: true, skipIntro: true, force2D: true, useDistanceCheck: true,
                    alwaysHearOnRealistic: false, skipToEnd: true, force3D: true);
            }
            catch (Exception e)
            {
                guard.Failed(e, "Helmet air could not end its sound");
            }
        }
    }
}
