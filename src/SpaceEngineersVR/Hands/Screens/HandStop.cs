using System;
using System.Collections.Generic;
using System.Diagnostics;
using Sandbox.Game.Entities.Character;
using SpaceEngineersVR.Input;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// The drawn hand stops on solid things while the real controller goes on past: every frame, each registered
    /// <see cref="IHandContactSource"/> is asked where the real fingertip and palm are, the deepest contact wins, and
    /// the palm's target (<see cref="Tracking.HandIK"/>, before the arm is solved) is pushed back out along its normal
    /// by its depth, so the hand rests on the surface. The first touch stops the hand where it meets the surface, as a
    /// real hand would; leaving a surface, or moving from one to another, eases the difference out over
    /// <see cref="EaseSeconds"/>, so the hand never jumps, and never into the surface it is on. A short tick on the
    /// controller on first contact with each surface. RenderDebug HandStop=0 turns all of it off.
    /// </summary>
    internal static class HandStop
    {
        /// <summary>Debug: no hand stop (<see cref="Rendering.RenderDebug"/> HandStop=0).</summary>
        public static bool Off { get; set; }

        /// <summary>The time constant the hand eases back to the controller, or from one surface to the next, with.</summary>
        public const double EaseSeconds = 0.06;

        /// <summary>The tick on first contact: light and short, a tap.</summary>
        public const float TickAmplitude = 0.4f, TickSeconds = 0.02f;

        /// <summary>Two ticks on the same hand closer than this are one (a screen's touch and its glass are the same tap).</summary>
        public const double TickGapSeconds = 0.12;

        /// <summary>A surface left and met again within this does not tick again (the hand resting on it, trembling).</summary>
        public const double RetouchSeconds = 0.25;

        /// <summary>No frame for this long: the hand starts over (no easing from a stale offset).</summary>
        private const double GapSeconds = 0.1;

        private static readonly List<IHandContactSource> sources = new List<IHandContactSource>();

        private sealed class HandStopState
        {
            public long LastAt, LeftAt, ContactSince, NotedAt;
            public object Source, LeftSource;
            public Vector3D Carry, Applied;
            public float DeepestReal;
            public string Name;
        }

        private static readonly HandStopState[] states = { new HandStopState(), new HandStopState() };
        private static int errors;

        /// <summary>Adds a source; the same source twice is one.</summary>
        public static void Register(IHandContactSource source)
        {
            if (source == null)
                return;
            lock (sources)
            {
                if (!sources.Contains(source))
                    sources.Add(source);
            }
        }

        /// <summary>The offset the drawn palm of a hand was pushed this frame, model space (zero: not stopped).</summary>
        internal static Vector3 Offset(Hand hand) => (Vector3)states[(int)hand].Applied;

        /// <summary>Whether a hand rests on something now.</summary>
        internal static bool Touching(Hand hand) => states[(int)hand].Source != null;

        /// <summary>
        /// RenderDebug TouchTrace: the stop's state for a hand as it stands, for the screen touch's per-frame line: on or off
        /// a surface, the push it gives the drawn hand and what of it is still easing out, and how long ago it last ran
        /// (under a frame: this frame, before the touch; a frame or more: the touch runs first).
        /// </summary>
        internal static string TraceState(Hand hand)
        {
            HandStopState s = states[(int)hand];
            double ago = s.LastAt == 0 ? -1.0 : (Stopwatch.GetTimestamp() - s.LastAt) * 1000.0 / Stopwatch.Frequency;
            return $"stop {(s.Source != null ? "on" : "off")} push {s.Applied.Length() * 1000.0:F1} mm (easing {s.Carry.Length() * 1000.0:F1}), ran {ago:F1} ms ago";
        }

        /// <summary>
        /// Called by the arm solver with the palm's target in the body's model space, before the arm is solved: moves the
        /// target out of whatever the hand went into. <paramref name="trackingToModel"/> is this frame's tracking space to
        /// the model space of <paramref name="body"/>.
        /// </summary>
        internal static void Apply(Hand hand, MyCharacter body, in HandState state, in MatrixD trackingToModel, ref Matrix target)
        {
            HandStopState s = states[(int)hand];
            if (Off || errors >= 3 || body?.PositionComp == null)
            {
                s.Source = null;
                s.Applied = s.Carry = Vector3D.Zero;
                return;
            }
            try
            {
                long now = Stopwatch.GetTimestamp();
                if (s.LastAt == 0 || now - s.LastAt > GapSeconds * Stopwatch.Frequency)
                {
                    s.Carry = s.Applied = Vector3D.Zero;
                    s.Source = null;
                }
                double dt = s.LastAt == 0 ? 0.0 : Math.Min(GapSeconds, (now - s.LastAt) / (double)Stopwatch.Frequency);
                s.LastAt = now;

                MatrixD modelFromWorld = body.PositionComp.WorldMatrixNormalizedInv;
                MatrixD toWorld = trackingToModel * MatrixD.Invert(modelFromWorld);
                MatrixD grip = (MatrixD)state.Grip * toWorld;
                Vector3D tip = FingerPress.Fingertip(hand, state, toWorld), palm = grip.Translation;

                bool found = false;
                HandContact best = default;
                lock (sources)
                {
                    foreach (IHandContactSource source in sources)
                    {
                        HandContact c;
                        bool hit;
                        try
                        {
                            hit = source.TryContact(hand, tip, palm, out c);
                        }
                        catch (Exception e)
                        {
                            if (errors++ < 3)
                                Log.Error(e, $"Hand stop: {source.GetType().Name} failed" + (errors >= 3 ? "; the hand stop is off for this run" : string.Empty));
                            continue;
                        }
                        if (hit && c.Depth > 0f && c.Source != null && (!found || c.Depth > best.Depth))
                        {
                            best = c;
                            found = true;
                        }
                    }
                }

                Vector3D normalModel = Vector3D.Zero, wanted = Vector3D.Zero;
                if (found)
                {
                    normalModel = Vector3D.Normalize(Vector3D.TransformNormal((Vector3D)best.Normal, modelFromWorld));
                    wanted = normalModel * best.Depth;
                }

                bool changed = found ? !Equals(best.Source, s.Source) : s.Source != null;
                if (changed && found && s.Source == null)
                    s.Carry = Vector3D.Zero;              // first touch: the hand stops where it meets the surface
                else if (changed)
                    s.Carry = s.Applied - wanted;         // off a surface, or onto another: ease the difference out
                else
                    s.Carry *= Math.Exp(-dt / EaseSeconds);
                Vector3D applied = wanted + s.Carry;
                if (found)
                {
                    // Never into the surface the hand is on, whatever is still being eased out.
                    double along = Vector3D.Dot(applied, normalModel);
                    if (along < best.Depth)
                        applied += normalModel * (best.Depth - along);
                }
                if (applied.LengthSquared() < 1e-10)
                    applied = s.Carry = Vector3D.Zero;
                s.Applied = applied;

                Note(hand, s, found, best, now);
                target.Translation += (Vector3)applied;
            }
            catch (Exception e)
            {
                if (errors++ < 3)
                    Log.Error(e, "Hand stop failed" + (errors >= 3 ? "; it is off for this run" : string.Empty));
            }
        }

        /// <summary>Contact begun and ended, and once a second while it lasts: the log lines, and the tick.</summary>
        private static void Note(Hand hand, HandStopState s, bool found, in HandContact best, long now)
        {
            long second = Stopwatch.Frequency;
            if (found && !Equals(best.Source, s.Source))
            {
                if (s.Source != null)
                    Log.Info($"Hand stop: {hand} hand moved from {s.Name} to {best.Source}");
                bool retouch = Equals(best.Source, s.LeftSource) && now - s.LeftAt < RetouchSeconds * second;
                if (!retouch)
                    Tick(hand);
                s.Source = best.Source;
                s.Name = best.Source.ToString();
                s.ContactSince = s.NotedAt = now;
                s.DeepestReal = best.Depth;
                Log.Info($"Hand stop: {hand} hand on {s.Name}, the real hand {best.Depth * 100f:F1} cm through; the drawn hand stops on it" +
                         (retouch ? " (touched again, no tick)" : string.Empty));
                return;
            }
            if (found)
            {
                s.DeepestReal = Math.Max(s.DeepestReal, best.Depth);
                if (now - s.NotedAt >= second)
                {
                    s.NotedAt = now;
                    Log.Info($"Hand stop: {hand} hand held on {s.Name}: the real hand {best.Depth * 100f:F1} cm through, " +
                             $"the drawn palm pushed back {s.Applied.Length() * 100.0:F1} cm");
                }
                return;
            }
            if (s.Source != null)
            {
                Log.Info($"Hand stop: {hand} hand off {s.Name} after {(now - s.ContactSince) / (double)second:F2} s; the real hand went " +
                         $"{s.DeepestReal * 100f:F1} cm through at most; easing back to the controller from {s.Applied.Length() * 100.0:F1} cm");
                s.LeftSource = s.Source;
                s.LeftAt = now;
                s.Source = null;
            }
        }

        private static readonly long[] tickedAt = new long[2];

        /// <summary>
        /// A light tap on a hand's controller (contact with a surface, a touch on a screen); one tick for things that happen
        /// within <see cref="TickGapSeconds"/> of each other on the same hand. Game thread.
        /// </summary>
        internal static void Tick(Hand hand)
        {
            int i = (int)hand;
            long now = Stopwatch.GetTimestamp();
            if (tickedAt[i] != 0 && now - tickedAt[i] < TickGapSeconds * Stopwatch.Frequency)
                return;
            tickedAt[i] = now;
            Haptics.Pulse(hand, TickAmplitude, TickSeconds);
        }
    }
}
