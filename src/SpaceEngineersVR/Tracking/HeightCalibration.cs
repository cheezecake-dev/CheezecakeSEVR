using System;
using System.Diagnostics;
using System.Globalization;
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
    /// </remarks>
    internal static class HeightCalibration
    {
        /// <summary>Seconds between <see cref="Begin"/> and the measurement, to stand straight after clicking.</summary>
        public const float CountdownSeconds = 3f;

        /// <summary>How long the head's height is averaged over, seconds.</summary>
        public const float MeasureSeconds = 0.5f;

        /// <summary>A measured eye height outside this range (metres) is not taken: the head was not where the player's is.</summary>
        public const float MinEyeHeight = 0.6f, MaxEyeHeight = 2.3f;

        private const double SampleInterval = 0.01; // seconds between samples; the game thread asks several times a frame
        private const string NoFloor = "No floor known: set up the play area or boundary in the headset";

        private enum Phase { Idle, Countdown, Measuring }

        private static readonly object gate = new object();
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static Phase phase;
        private static double phaseEnd, lastSample, sum;
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
                    }
                    if (problem != null)
                        return problem;
                    if (VRSettings.EyeHeight.HasValue)
                        return "Eye height " + Metres(VRSettings.EyeHeight.Value) + " m";
                    return HeadAboveFloor.HasValue ? "Not calibrated" : NoFloor;
                }
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

        /// <summary>Forgets the calibration (EyeHeight = null) and saves.</summary>
        public static void Clear()
        {
            lock (gate)
            {
                phase = Phase.Idle;
                problem = null;
                VRSettings.EyeHeight = null;
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
            float? calibrated = null;
            lock (gate)
            {
                if (phase == Phase.Idle)
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
            }

            if (calibrated.HasValue)
            {
                Save();
                Log.Info($"Height calibrated: eye height {Metres(calibrated.Value)} m above the floor");
            }
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
