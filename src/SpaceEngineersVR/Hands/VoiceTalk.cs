using System;
using System.Diagnostics;
using Sandbox.Engine.Networking;
using Sandbox.Game;
using Sandbox.Game.Gui;
using Sandbox.Game.VoiceChat;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;
using VRage.Audio;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// Push to talk: the left grip held with the hand at the helmet's mouth, where its microphone is, holds the game's voice
    /// chat control (VOICE_CHAT, U on the keyboard) for as long as the grip is held, in every context: on foot, on the jetpack,
    /// in a seat and in a turret. A light tick as the talking starts, and another as it stops.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The game reads the control in its gameplay pass, whatever the player controls (MyGuiScreenGamePlay.cs:845): a new press
    /// starts recording (MyVoiceChatSessionComponent.StartRecording, which also puts up its voice chat indicator), and a pass
    /// in which it is no longer held stops it. It ignores the control when its voice chat is set to open by itself
    /// (MyPlatformGameSettings.VOICE_CHAT_AUTOMATIC_ACTIVATION), and records nothing with voice chat off in its audio options
    /// or without the game service (StartRecording); the log says which.
    /// </para>
    /// <para>
    /// The mouth is a point in the head's own frame (<see cref="Mouth"/>, turning with the head), from the tracked head and the
    /// hand's grip pose in the same tracking space, so it is the same in a seat as on foot. Only a grip that <b>starts</b> there
    /// counts, claimed through <see cref="GripClaim"/> at priority 8: after the held weapon's own grips (two hands at 5, the
    /// magazine at 7) and before the helmet (10), so the left grip's other meanings that start at the mouth (the helmet, the
    /// toolbar wheel on foot, a seat's roll and its controls) are skipped for that press. Once started it talks until the grip
    /// is let go, wherever the hand goes.
    /// </para>
    /// <para>
    /// Where it sits against <see cref="HeadGestures"/>: the mouth is 9 cm below the eyes and 10 cm in front of them, a fist
    /// held to the helmet's chin, within <see cref="MouthReach"/>. The temples (the lamp's tap) are about 19 cm from it, so the
    /// two zones are 6 cm apart at their nearest, and the lamp is a fingertip tap with no grip. The mouth is inside the helmet's
    /// reach (the helmet is gripped anywhere within 26 cm of the middle of the head): for the left hand that part of the helmet,
    /// the chin, is the mouth, and the helmet's tick does not mark it either; the rest of the helmet is as it was, and the right
    /// hand's helmet is all of it.
    /// </para>
    /// </remarks>
    internal static class VoiceTalk
    {
        /// <summary>The name the grip is claimed under (<see cref="GripClaim"/>).</summary>
        public const string ClaimName = "Voice";

        /// <summary>After the held weapon's grips (TwoHand 5, Reload 7), before the helmet (HeadGestures 10).</summary>
        public const int ClaimPriority = 8;

        /// <summary>The hand that talks: the left, whose grip is free of the tool hand's use.</summary>
        public const Hand TalkHand = Hand.Left;

        /// <summary>The mouth, in the head's own frame from the eyes (X right, Y up, -Z ahead): 9 cm below the eyes, 10 cm in front.</summary>
        public static readonly Vector3 Mouth = new Vector3(0f, -0.09f, -0.10f);

        /// <summary>A grip this near the mouth is at it, metres.</summary>
        public const float MouthReach = 0.07f;

        /// <summary>What the game is doing is logged this long after the talking starts or stops, seconds.</summary>
        private const double VerifyAfterSeconds = 0.3;

        private static bool talking;
        private static long startedAt, verifyAt;
        private static int errors;

        /// <summary>Registers the grip claim. Once, as the plugin's hooks are made.</summary>
        public static void Register()
        {
            GripClaim.Register(ClaimName, ClaimPriority, Take);
            Log.Info($"Push to talk: the {TalkHand.ToString().ToLowerInvariant()} grip at the helmet's mouth (grip claim at priority {ClaimPriority}; within {MouthReach * 100:0} cm of a point {-Mouth.Y * 100:0} cm below and {-Mouth.Z * 100:0} cm in front of the eyes)");
        }

        /// <summary>A grip has just started: it is push to talk when it is the left hand's, at the mouth, with the game in play.</summary>
        private static bool Take(Hand hand)
        {
            if (hand != TalkHand || errors >= 3 || !VRInput.Active || ToolbarWheel.Owns)
                return false;
            // The game reads the control only in its gameplay pass; in a menu the left grip is the terminal's amount modifier.
            if (!(MyScreenManager.GetScreenWithFocus() is MyGuiScreenGamePlay))
                return false;
            return TryDistance(hand, out float distance) && distance <= MouthReach;
        }

        /// <summary>How far the hand's grip is from the mouth, metres; false when the head or the hand is not tracked.</summary>
        internal static bool TryDistance(Hand hand, out float distance)
        {
            distance = float.MaxValue;
            HandState state = VRInput.Get(hand);
            if (!state.Tracked || !GameHead.Take())
                return false;
            Vector3 mouth = Vector3.Transform(Mouth, GameHead.TrackingPose);
            distance = Vector3.Distance(state.Grip.Translation, mouth);
            return true;
        }

        /// <summary>The left hand is at the mouth now (for the helmet's tick, which leaves the mouth to this).</summary>
        internal static bool AtMouth(Hand hand) => hand == TalkHand && TryDistance(hand, out float distance) && distance <= MouthReach;

        /// <summary>
        /// Game thread, once a frame from <see cref="VRControls"/> while the hands are live: true while push to talk is held,
        /// which the game is told as VOICE_CHAT held.
        /// </summary>
        public static bool Frame()
        {
            try
            {
                bool held = GripClaim.Holds(TalkHand, ClaimName);
                long now = Stopwatch.GetTimestamp();
                if (held != talking)
                {
                    talking = held;
                    Haptics.Pulse(TalkHand, HeadGestures.TickAmplitude, HeadGestures.TickSeconds);
                    if (held)
                    {
                        startedAt = now;
                        Log.Info($"Push to talk: on (left grip at the mouth); {Expect()}");
                    }
                    else
                        Log.Info($"Push to talk: off after {(now - startedAt) / (double)Stopwatch.Frequency:0.0} s (left grip let go)");
                    verifyAt = now + (long)(VerifyAfterSeconds * Stopwatch.Frequency);
                }
                else if (verifyAt != 0 && now >= verifyAt)
                {
                    verifyAt = 0;
                    Log.Info($"Push to talk: {(talking ? "held" : "let go")} {VerifyAfterSeconds:0.0} s ago; the game is {(Recording() ? "recording" : "not recording")}");
                }
                return held;
            }
            catch (Exception e)
            {
                if (errors++ < 3)
                    Log.Error(e, "Push to talk failed this frame" + (errors >= 3 ? "; it stays off for this run" : string.Empty));
                talking = false;
                return false;
            }
        }

        private static bool Recording() => MyVoiceChatSessionComponent.Static?.IsRecording == true;

        /// <summary>What the game will make of the control, from its own conditions (MyGuiScreenGamePlay.cs:845, StartRecording).</summary>
        private static string Expect()
        {
            if (MyPlatformGameSettings.VOICE_CHAT_AUTOMATIC_ACTIVATION)
                return "the game's voice chat opens by itself here (automatic activation) and ignores the control";
            if (!MyPerGameSettings.VoiceChatEnabled || MyVoiceChatSessionComponent.Static == null)
                return "the game has no voice chat in this session";
            if (MyAudio.Static == null || !MyAudio.Static.EnableVoiceChat)
                return "voice chat is off in the game's audio options, so it records nothing";
            if (!MyGameService.IsActive)
                return "the game service is not running, so it records nothing";
            return "the game records while it is held";
        }
    }
}
