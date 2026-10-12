using System;
using System.Diagnostics;
using System.Globalization;
using SpaceEngineersVR.Input;
using VRageMath;

namespace SpaceEngineersVR.Tracking
{
    /// <summary>
    /// Height calibration: the player stands (or sits) as they will play, and their eye height above the floor
    /// becomes <see cref="VRSettings.EyeHeight"/>, where the character's eyes are. Game thread.
    /// </summary>
    /// <remarks>
    /// The floor comes from the headset's STAGE space (<see cref="OpenXR.XrSession.OriginAboveFloor"/>) or the desktop
    /// fake head's height (<see cref="Rendering.FakeHead"/>), and reaches here with the head through <see cref="HeadTracker"/>.
    /// Standing play puts the game head's height from it (<see cref="HeadCentre.HeightReference"/>); seated play does not use it.
    /// With <see cref="VRSettings.ArmSpanScaling"/> on, the calibration goes on to the arm span once the height is taken: the
    /// player holds both arms straight out to the sides, and the distance between the palms (where the character's wrists go,
    /// <see cref="HandIK.PalmTarget"/>) becomes <see cref="VRSettings.ArmSpan"/>, which sets how far the character's arms
    /// stretch (<see cref="Tracking.ArmSpan"/>). A span that is not plausible is not taken, and the one before stays.
    /// </remarks>
    internal static class HeightCalibration
    {
        /// <summary>Seconds between <see cref="Begin"/> and the measurement, to stand straight after clicking.</summary>
        public const float CountdownSeconds = 3f;

        /// <summary>How long the head's height is averaged over, seconds.</summary>
        public const float MeasureSeconds = 0.5f;

        /// <summary>A measured eye height outside this range (metres) is not taken: the head was not where the player's is.</summary>
        public const float MinEyeHeight = 0.6f, MaxEyeHeight = 2.3f;

        /// <summary>Seconds between the height being taken and the arm span's measurement, to raise the arms.</summary>
        public const float ArmCountdownSeconds = 3f;

        /// <summary>How long the arms are held out and the distance between the hands averaged over, seconds.</summary>
        public const float ArmMeasureSeconds = 2f;

        /// <summary>
        /// The hands must be about level (their height apart, averaged, metres at most) and steady (the distance between them may
        /// vary this much, metres at most) for the arm span to be taken.
        /// </summary>
        private const float MaxUnlevel = 0.15f, MaxSpread = 0.12f;

        private const double SampleInterval = 0.01; // seconds between samples; the game thread asks several times a frame
        private const string NoFloor = "No floor known: set up the play area or boundary in the headset";

        private enum Phase { Idle, Countdown, Measuring, ArmCountdown, ArmMeasuring }

        private static readonly object gate = new object();
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static Phase phase;
        private static double phaseEnd, lastSample, sum, unlevelSum, spanLow, spanHigh;
        private static int samples;
        private static string problem;

        private static double Now => clock.Elapsed.TotalSeconds;

        /// <summary>The head's height above the floor now, metres; null when the floor is not known.</summary>
        public static float? HeadAboveFloor
        {
            get
            {
                // Straight from the tracker rather than the game's per-frame snapshot, so it is the head as it is now.
                if (!HeadTracker.TryGet(out Matrix head, out float? originAboveFloor) || !originAboveFloor.HasValue)
                    return null;
                return originAboveFloor.Value + head.Translation.Y;
            }
        }

        /// <summary>One line for the options menu: not calibrated, the countdown, the result, or why it cannot.</summary>
        public static string Status
        {
            get
            {
                Update();
                lock (gate)
                {
                    switch (phase)
                    {
                        case Phase.Countdown:
                            return "Stand (or sit) as you play... " + Math.Max(1, (int)Math.Ceiling(phaseEnd - Now));
                        case Phase.Measuring:
                            return "Measuring...";
                        case Phase.ArmCountdown:
                            return "Arms straight out to the sides... " + Math.Max(1, (int)Math.Ceiling(phaseEnd - Now));
                        case Phase.ArmMeasuring:
                            return "Hold still...";
                    }
                    if (problem != null)
                        return problem;
                    if (VRSettings.EyeHeight.HasValue)
                        return "Eye height " + Metres(VRSettings.EyeHeight.Value) + " m" +
                               (VRSettings.ArmSpanScaling && VRSettings.ArmSpan.HasValue ? ", arm span " + Metres(VRSettings.ArmSpan.Value) + " m" : string.Empty);
                    return HeadAboveFloor.HasValue ? "Not calibrated" : NoFloor;
                }
            }
        }

        /// <summary>A countdown or a measurement is running (the drive's calibrate waits for it to end).</summary>
        public static bool Busy
        {
            get
            {
                Update();
                lock (gate)
                    return phase != Phase.Idle;
            }
        }

        /// <summary>The last calibration ended on a problem (<see cref="Status"/> says which).</summary>
        public static bool Failed
        {
            get
            {
                lock (gate)
                    return problem != null;
            }
        }

        /// <summary>Starts the countdown; at its end the head height is measured, stored and saved.</summary>
        public static void Begin()
        {
            lock (gate)
            {
                if (!HeadAboveFloor.HasValue)
                {
                    Fail(NoFloor);
                    return;
                }
                problem = null;
                phase = Phase.Countdown;
                phaseEnd = Now + CountdownSeconds;
            }
            Log.Info($"Height calibration: measuring in {CountdownSeconds:0} s");
        }

        /// <summary>Forgets the calibration (EyeHeight and ArmSpan = null) and saves.</summary>
        public static void Clear()
        {
            lock (gate)
            {
                phase = Phase.Idle;
                problem = null;
                VRSettings.EyeHeight = null;
                VRSettings.ArmSpan = null;
            }
            Save();
            Log.Info("Height calibration cleared");
        }

        /// <summary>
        /// Advances the countdown and the measurement by the clock. Called on the game's frames (<see cref="GameHead.Take"/>,
        /// which every head matrix built asks for) and whenever <see cref="Status"/> is read, so it runs with or without a
        /// screen open. Any thread.
        /// </summary>
        internal static void Update()
        {
            float? spanned = null;
            bool arms;
            lock (gate)
            {
                arms = phase == Phase.ArmCountdown || phase == Phase.ArmMeasuring;
                if (arms)
                    spanned = UpdateArmSpan(Now);
            }
            if (arms)
            {
                if (spanned.HasValue)
                {
                    Save();
                    Log.Info($"Arm span calibrated: {Metres(spanned.Value)} m between the hands" +
                             (ArmSpan.Needed(spanned.Value) > 0f ? $", the character's arms need to stretch x{ArmSpan.Needed(spanned.Value):0.00} to reach them" : string.Empty));
                }
                return;
            }

            float? calibrated = null;
            lock (gate)
            {
                if (phase == Phase.Idle || phase == Phase.ArmCountdown || phase == Phase.ArmMeasuring)
                    return;
                double now = Now;
                if (phase == Phase.Countdown)
                {
                    if (now < phaseEnd)
                        return;
                    phase = Phase.Measuring;
                    phaseEnd = now + MeasureSeconds;
                    lastSample = double.NegativeInfinity;
                    sum = 0.0;
                    samples = 0;
                }

                float? height = HeadAboveFloor;
                if (!height.HasValue)
                {
                    Fail(NoFloor);
                    return;
                }
                if (now - lastSample >= SampleInterval)
                {
                    sum += height.Value;
                    samples++;
                    lastSample = now;
                }
                if (now < phaseEnd)
                    return;

                phase = Phase.Idle;
                float mean = (float)(sum / samples); // the first call of the measurement always samples
                if (mean < MinEyeHeight || mean > MaxEyeHeight)
                {
                    Fail($"Measured {Metres(mean)} m, outside {MinEyeHeight:0.0} to {MaxEyeHeight:0.0} m: try again");
                    return;
                }
                problem = null;
                VRSettings.EyeHeight = mean;
                calibrated = mean;
                if (VRSettings.ArmSpanScaling)
                {
                    phase = Phase.ArmCountdown;
                    phaseEnd = now + ArmCountdownSeconds;
                }
            }

            if (calibrated.HasValue)
            {
                Save();
                Log.Info($"Height calibrated: eye height {Metres(calibrated.Value)} m above the floor");
            }
        }

        /// <summary>
        /// The arm span step (caller holds the lock): the countdown to raise the arms, then the distance between the palms
        /// averaged over <see cref="ArmMeasureSeconds"/>. Returns the span once it is taken and stored; null while it goes on,
        /// and when it fails (<see cref="Fail"/>: the span before stays).
        /// </summary>
        private static float? UpdateArmSpan(double now)
        {
            if (phase == Phase.ArmCountdown)
            {
                if (now < phaseEnd)
                    return null;
                phase = Phase.ArmMeasuring;
                phaseEnd = now + ArmMeasureSeconds;
                lastSample = double.NegativeInfinity;
                sum = unlevelSum = 0.0;
                spanLow = double.MaxValue;
                spanHigh = 0.0;
                samples = 0;
            }

            if (!TryPalms(out Vector3 left, out Vector3 right))
            {
                FailArms("a controller is not tracked");
                return null;
            }
            if (now - lastSample >= SampleInterval)
            {
                double apart = Vector3.Distance(left, right);
                sum += apart;
                unlevelSum += Math.Abs(left.Y - right.Y);
                spanLow = Math.Min(spanLow, apart);
                spanHigh = Math.Max(spanHigh, apart);
                samples++;
                lastSample = now;
            }
            if (now < phaseEnd)
                return null;

            phase = Phase.Idle;
            float span = (float)(sum / samples); // the first call of the measurement always samples
            float unlevel = (float)(unlevelSum / samples);
            if (spanHigh - spanLow > MaxSpread)
            {
                FailArms("hands moved while measuring");
                return null;
            }
            if (unlevel > MaxUnlevel)
            {
                FailArms($"hands {unlevel * 100f:0} cm off level");
                return null;
            }
            if (span < ArmSpan.Min || span > ArmSpan.Max)
            {
                FailArms($"hands {Metres(span)} m apart, need {ArmSpan.Min:0.0} to {ArmSpan.Max:0.0} m");
                return null;
            }
            problem = null;
            VRSettings.ArmSpan = span;
            return span;
        }

        /// <summary>The controllers' palms now, in the tracking space, where the character's wrists go; false when either is not tracked.</summary>
        private static bool TryPalms(out Vector3 left, out Vector3 right)
        {
            left = right = Vector3.Zero;
            if (!VRInput.TryReadLive(out HandState leftHand, out HandState rightHand) || !leftHand.PositionTracked || !rightHand.PositionTracked)
                return false;
            left = HandIK.PalmTarget(Hand.Left, leftHand).Translation;
            right = HandIK.PalmTarget(Hand.Right, rightHand).Translation;
            return true;
        }

        /// <summary>Caller holds the lock. The arm span was not taken; the one before stays.</summary>
        private static void FailArms(string reason)
        {
            float? kept = VRSettings.ArmSpan;
            Fail("Arm span: " + reason + (kept.HasValue ? $"; kept {Metres(kept.Value)} m" : "; none saved"));
        }

        /// <summary>Caller holds the lock.</summary>
        private static void Fail(string reason)
        {
            phase = Phase.Idle;
            problem = reason;
            Log.Warn("Height calibration: " + reason);
        }

        private static void Save()
        {
            try
            {
                VRSettings.Save();
            }
            catch (Exception e)
            {
                Log.Error(e, "Height calibration could not be saved");
            }
        }

        private static string Metres(float value) => value.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
