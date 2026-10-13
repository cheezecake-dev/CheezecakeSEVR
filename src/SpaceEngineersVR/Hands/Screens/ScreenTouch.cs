using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Gui;
using Sandbox.Game.SessionComponents.Clipboard;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>A touch on a screen: the fingertip reached the glass, moved on it, or lifted off.</summary>
    internal enum TouchPhase
    {
        Down,
        Move,
        Up,
    }

    /// <summary>
    /// The index fingertip touching a screen's glass (<see cref="ScreenGlass"/>) is a touch on that screen, reported in
    /// the screen's own pixels (sprite coordinates, what a script draws with) on <see cref="Touched"/>:
    /// <list type="bullet">
    /// <item><see cref="TouchPhase.Down"/> when the tip, coming from in front, reaches the glass over the picture
    /// (a tick on the controller);</item>
    /// <item><see cref="TouchPhase.Move"/> while it stays on it, each time the pixel changes (the real tip may go on
    /// past the glass, where the drawn hand stops: <see cref="HandStop"/>);</item>
    /// <item><see cref="TouchPhase.Up"/> when it lifts more than <see cref="LiftOff"/> off the glass, or slides off its
    /// edge (with the last pixel on it).</item>
    /// </list>
    /// Seated or on foot; not up a ladder, not while a menu has the focus, not with the hand that holds a tool or the
    /// builder. The surface index is the game's (IMyTextSurfaceProvider.GetSurface). One log line per Down and Up;
    /// RenderDebug ScreenGlass=0 turns it off with the glass.
    /// </summary>
    internal static partial class ScreenTouch
    {
        internal delegate void TouchHandler(MyCubeBlock block, int surfaceIndex, Vector2 pixel, TouchPhase phase, Hand hand);

        /// <summary>A touch on a screen. Game thread, after the character's simulation.</summary>
        internal static event TouchHandler Touched;

        /// <summary>Lifted further than this off the glass: the touch is up.</summary>
        public const float LiftOff = 0.01f;

        /// <summary>Slid further than this off the glass's edge: the touch is up.</summary>
        public const float SlideOff = 0.01f;

        private sealed class HandTouch
        {
            public readonly Dictionary<ScreenGlass.ScreenKey, long> Armed = new Dictionary<ScreenGlass.ScreenKey, long>();
            public ScreenGlass.ScreenKey Key;
            public MyCubeBlock Block;
            public int Surface;
            public Vector2 Pixel;
            public long DownAt;
            public float Deepest;
            public int Moves;
            public long TraceFrameTime;
        }

        /// <summary>
        /// RenderDebug TouchTrace=1: a log line per hand per frame while it is down on a screen, the drawn hand is stopped
        /// on something, or the tip is within <see cref="TraceBand"/> of a screen's glass (<see cref="TraceFrame"/>). Off by default.
        /// </summary>
        public static bool Trace { get; set; }

        /// <summary>The hand's fingertip is down on a screen now (the cockpit's switches leave it alone meanwhile).</summary>
        internal static bool Down(Hand hand) => hands[(int)hand].Key != null;

        /// <summary>The tip this near a screen's glass (either side) is traced.</summary>
        private const float TraceBand = 0.03f;

        /// <summary>Trace lines in a run (about five minutes of one hand at the glass); the rest are dropped, with a line saying so.</summary>
        private const int TraceCap = 18000;

        private static int traceLines;

        private static readonly HandTouch[] hands = { new HandTouch(), new HandTouch() };
        private static readonly Hand[] bothHands = { Hand.Left, Hand.Right };
        private static readonly List<ScreenGlass.ScreenKey> stale = new List<ScreenGlass.ScreenKey>();
        private static int errors;

        public static void Patch(Harmony harmony)
        {
            try
            {
                ScreenGlass.Register();
                harmony.Patch(AccessTools.Method(typeof(MyCharacter), nameof(MyCharacter.UpdateAfterSimulation)),
                    postfix: new HarmonyMethod(typeof(ScreenTouch), nameof(AfterSimulation)));
                Log.Info("Screen touch: screens' glass is solid to the hands, and the fingertip touches them (hooked)");
            }
            catch (Exception e)
            {
                PatchRegistry.Lost(e, "Screen touch could not be hooked; screens cannot be touched");
                throw;
            }
        }

        /// <summary>Raises <see cref="Touched"/>; one handler failing does not keep the touch from the others.</summary>
        internal static void Raise(MyCubeBlock block, int surfaceIndex, Vector2 pixel, TouchPhase phase, Hand hand)
        {
            TouchHandler handlers = Touched;
            if (handlers == null)
                return;
            foreach (Delegate d in handlers.GetInvocationList())
            {
                try
                {
                    ((TouchHandler)d)(block, surfaceIndex, pixel, phase, hand);
                }
                catch (Exception e)
                {
                    if (errors++ < 6)
                        Log.Error(e, $"Screen touch: a handler of the {phase} on {ScreenGlass.Describe(block)} failed");
                }
            }
        }

        /// <summary>A test aid placing a fake fingertip on a screen (ScreenTest), before the touch reads the hands.</summary>
        static partial void TestAidsFrame(MyCharacter character);

        private static void AfterSimulation(MyCharacter __instance)
        {
            if (__instance != MySession.Static?.LocalCharacter)
                return;
            try
            {
                TestAidsFrame(__instance);
                bool live = !ScreenGlass.Off && VRInput.Active && !__instance.IsDead && !__instance.Closed && __instance.PositionComp != null;
                object focused = live ? MyScreenManager.GetScreenWithFocus() : null;
                bool building = (MyCubeBuilder.Static != null && MyCubeBuilder.Static.IsActivated)
                                || (MyClipboardComponent.Static != null && MyClipboardComponent.Static.IsActive);
                bool holdsTool = (__instance.CurrentWeapon != null && !__instance.IsSitting) || building;
                foreach (Hand hand in bothHands)
                {
                    // A seated hand holding its stick or throttle is drawn on it, not where the controller is: it touches nothing.
                    bool onControl = __instance.IsSitting && (ControlGrip.Holds(hand) || Cockpits.CockpitControls.HandBusy(hand));
                    bool may = live && !__instance.IsOnLadder && focused is MyGuiScreenGamePlay && !(holdsTool && hand == VRSettings.DominantHand) && !onControl;
                    Step(hand, __instance, may);
                }
            }
            catch (Exception e)
            {
                if (errors++ < 3)
                    Log.Error(e, "Screen touch failed");
            }
        }

        private static void Step(Hand hand, MyCharacter character, bool may)
        {
            HandTouch h = hands[(int)hand];
            if (!may || !HandWorld.TryGetNow(character, hand, out MatrixD grip, out _, out _))
            {
                Lift(hand, h, "the hand is not touching now (tracking lost, a menu, a tool, the ladder or a held stick or throttle)");
                h.Armed.Clear();
                return;
            }
            Vector3D tip = Reach.Limit(character, FingerPress.Fingertip(hand, grip));
            long now = Stopwatch.GetTimestamp();
            long armWindow = (long)(ScreenGlass.ArmSeconds * Stopwatch.Frequency);

            ScreenGlass.Screen downOn = null;
            ScreenGlass.Hit downHit = default;
            ScreenGlass.Screen candidate = null;
            ScreenGlass.Hit candidateHit = default;
            ScreenGlass.Screen nearest = null;
            ScreenGlass.Hit nearestHit = default;
            foreach (ScreenGlass.Screen s in ScreenGlass.Near(hand, tip))
            {
                ScreenGlass.Hit hit = ScreenGlass.Measure(s.Sheet, (Vector3)Vector3D.Transform(tip, s.FromWorld));
                if (h.Key != null && s.Key.Equals(h.Key))
                {
                    downOn = s;
                    downHit = hit;
                }
                if (Trace && hit.Edge <= SlideOff && (nearest == null || Math.Abs(hit.Distance) < Math.Abs(nearestHit.Distance)))
                {
                    nearest = s;
                    nearestHit = hit;
                }
                if (hit.Edge > ScreenGlass.EdgeMargin)
                    continue;
                if (hit.Distance > 0f && hit.Distance < ScreenGlass.Approach)
                    h.Armed[s.Key] = now;
                else if (hit.Distance <= 0f && hit.Distance > -ScreenGlass.MaxCatch && h.Armed.TryGetValue(s.Key, out long armedAt) && now - armedAt <= armWindow
                         && (candidate == null || hit.Distance > candidateHit.Distance))
                {
                    candidate = s;
                    candidateHit = hit;
                }
            }

            if (h.Armed.Count > 16)
            {
                stale.Clear();
                foreach (KeyValuePair<ScreenGlass.ScreenKey, long> pair in h.Armed)
                    if (now - pair.Value > 10 * armWindow)
                        stale.Add(pair.Key);
                foreach (ScreenGlass.ScreenKey key in stale)
                    h.Armed.Remove(key);
            }

            string phase = null;
            if (h.Key != null)
            {
                if (downOn == null || downHit.Edge > SlideOff || downHit.Distance > LiftOff)
                {
                    Lift(hand, h, downOn == null ? "the screen is gone" : downHit.Distance > LiftOff ? "lifted" : "slid off the edge");
                    phase = "Up";
                }
                else
                {
                    h.Deepest = Math.Max(h.Deepest, -downHit.Distance);
                    Vector2 pixel = Clamp(downOn, downOn.Pixel(downHit.UV));
                    if (Vector2.DistanceSquared(pixel, h.Pixel) >= 0.25f)
                    {
                        h.Pixel = pixel;
                        h.Moves++;
                        Raise(h.Block, h.Surface, pixel, TouchPhase.Move, hand);
                    }
                }
            }
            else if (candidate != null)
            {
                Press(hand, h, candidate, candidateHit, now);
                phase = "Down";
            }

            if (Trace)
            {
                ScreenGlass.Screen traced = downOn ?? candidate ?? nearest;
                TraceFrame(hand, h, character, tip, traced, downOn != null ? downHit : candidate != null ? candidateHit : nearestHit, phase);
            }
        }

        /// <summary>A Down on <paramref name="candidate"/>: the touch starts there, with a tick on the controller.</summary>
        private static void Press(Hand hand, HandTouch h, ScreenGlass.Screen candidate, in ScreenGlass.Hit candidateHit, long now)
        {
            h.Key = candidate.Key;
            h.Block = candidate.Block;
            h.Surface = candidate.Surface;
            h.Pixel = Clamp(candidate, candidate.Pixel(candidateHit.UV));
            h.DownAt = now;
            h.Deepest = -candidateHit.Distance;
            h.Moves = 0;
            HandStop.Tick(hand);
            Vector2 size = candidate.Panel.SurfaceSize;
            Log.Info($"Screen touch: {hand} Down on {ScreenGlass.Describe(candidate.Block)} '{candidate.Block.DisplayNameText}' ({candidate.Block.EntityId}) surface {candidate.Surface} " +
                     $"'{candidate.Panel.Name}' at pixel ({h.Pixel.X:F0},{h.Pixel.Y:F0}) of a {size.X:F0}x{size.Y:F0} surface (texture {candidate.Panel.TextureSize.X:F0}x{candidate.Panel.TextureSize.Y:F0}), " +
                     $"uv ({candidateHit.UV.X:F3},{candidateHit.UV.Y:F3}), tip {-candidateHit.Distance * 1000f:F1} mm past the glass");
            Raise(h.Block, h.Surface, h.Pixel, TouchPhase.Down, hand);
        }

        /// <summary>
        /// RenderDebug TouchTrace=1: one line for this hand this frame, when it is down on a screen, Down or Up happened, the
        /// drawn hand is stopped, or the tip is within <see cref="TraceBand"/> of the glass. The tip (the drawn glove's index
        /// fingertip, <see cref="FingerPress.Fingertip(Hand, in MatrixD)"/>, before the stop holds the drawn hand back) and
        /// its distance from the glass (+ in front, mm) three ways: as the touch reads it ("tip", the controller located for
        /// the frame drawn from it),
        /// "now" (the same placement, the controller at the display time of the headset frame being composed: what the
        /// prediction adds), and "drawn" (the tip moved by the hand stop's push). Then the stop's state; the head: the tracked
        /// head in the room ("raw"), the seat's offset, where the seat held it and the seat's eye from the pilot's head bone
        /// (CockpitHead, cm, x right y up z back; "old" when not from this frame); the eye, the grip and the tip as placed, in
        /// the screen block's own space (cm), and the grip against the tracked head (cm, the controller's own part); the headset
        /// frame the hands came from (ms after the last line's, and how far behind the frame being composed); where the
        /// placement came from (HandWorld.LastBodySource). Each term on its own line from frame to frame shows which one
        /// moves when the touch flickers.
        /// </summary>
        private static void TraceFrame(Hand hand, HandTouch h, MyCharacter character, Vector3D tip, ScreenGlass.Screen s, in ScreenGlass.Hit hit, string phase)
        {
            bool stopped = HandStop.Touching(hand);
            if (h.Key == null && phase == null && !stopped && (s == null || Math.Abs(hit.Distance) > TraceBand))
                return;
            if (traceLines >= TraceCap)
            {
                if (traceLines++ == TraceCap)
                    Log.Info($"TouchTrace: {TraceCap} lines this run, no more");
                return;
            }
            traceLines++;

            ulong frame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            HandWorld.BodySource source = HandWorld.LastBodySource;
            bool seatThisFrame = CockpitHead.LastFrame == frame;
            Vector3 offset = CockpitHead.LastOffset, held = CockpitHead.LastHeld, fromHead = CockpitHead.LastFromHead;
            Matrix headPose = GameHead.TrackingPose;
            HandState state = VRInput.Get(hand);
            long sinceLast = h.TraceFrameTime != 0 && state.FrameTime != 0 ? state.FrameTime - h.TraceFrameTime : 0;
            h.TraceFrameTime = state.FrameTime;

            var line = new System.Text.StringBuilder(400);
            line.Append("TouchTrace ").Append(frame).Append(' ').Append(hand).Append(h.Key != null ? " down" : " up");
            if (phase != null)
                line.Append(" (").Append(phase).Append(')');

            MatrixD toWorld = MatrixD.Identity;
            bool placed = HandWorld.TryGetTrackingToBody(character, out MatrixD trackingToModel, out _);
            if (placed)
                toWorld = trackingToModel * MatrixD.Invert(character.PositionComp.WorldMatrixNormalizedInv);

            if (s == null)
                line.Append(", no screen");
            else
            {
                line.Append($", tip {hit.Distance * 1000f:F1} mm (edge {hit.Edge * 1000f:F1})");
                long nowFrameTime = 0;
                IVRInputSource headset = VRInput.Headset;
                if (placed && headset != null && headset.Active)
                {
                    headset.ReadNow(out HandState left, out HandState right);
                    HandState now = hand == Hand.Left ? left : right;
                    nowFrameTime = now.FrameTime;
                    if (now.Tracked)
                    {
                        Vector3D nowTip = FingerPress.Fingertip(hand, now, toWorld);
                        line.Append($", now {ScreenGlass.Measure(s.Sheet, (Vector3)Vector3D.Transform(nowTip, s.FromWorld)).Distance * 1000f:F1}");
                    }
                }
                Vector3D drawnTip = tip + Vector3D.TransformNormal((Vector3D)HandStop.Offset(hand), character.PositionComp.WorldMatrixRef);
                line.Append($", drawn {ScreenGlass.Measure(s.Sheet, (Vector3)Vector3D.Transform(drawnTip, s.FromWorld)).Distance * 1000f:F1}");
                line.Append("; ").Append(HandStop.TraceState(hand));
                line.Append($"; head raw {Cm(headPose.Translation)}, seat {(seatThisFrame ? string.Empty : "old ")}offset {Cm(offset)} held {Cm(held)} eye-from-pilot-head {Cm(fromHead)}");
                if (placed)
                {
                    Vector3D eye = Vector3D.Transform((Vector3D)headPose.Translation, toWorld);
                    Vector3D gripAt = Vector3D.Transform((Vector3D)state.Grip.Translation, toWorld);
                    line.Append($"; in the block: eye {Cm((Vector3)Vector3D.Transform(eye, s.FromWorld))} grip {Cm((Vector3)Vector3D.Transform(gripAt, s.FromWorld))}" +
                                $" tip {Cm((Vector3)Vector3D.Transform(tip, s.FromWorld))}");
                }
                line.Append($"; grip from head {Cm(Vector3.Transform(state.Grip.Translation, Matrix.Invert(headPose)))}");
                line.Append($"; hands' frame +{sinceLast / 1e6:F1} ms, {(nowFrameTime != 0 && state.FrameTime != 0 ? (nowFrameTime - state.FrameTime) / 1e6 : 0.0):F1} ms behind");
            }
            if (s == null)
                line.Append("; ").Append(HandStop.TraceState(hand));
            line.Append("; placed by ").Append(placed ? source.ToString() : "nothing");
            Log.Info(line.ToString());
        }

        private static string Cm(Vector3 v) => $"({v.X * 100f:F1}, {v.Y * 100f:F1}, {v.Z * 100f:F1})";

        private static Vector2 Clamp(ScreenGlass.Screen s, Vector2 pixel)
        {
            RectangleF view = ScreenGlass.Viewport(s.Panel);
            return Vector2.Clamp(pixel, view.Position, view.Position + view.Size);
        }

        private static void Lift(Hand hand, HandTouch h, string why)
        {
            if (h.Key == null)
                return;
            MyCubeBlock block = h.Block;
            int surface = h.Surface;
            Vector2 pixel = h.Pixel;
            double held = (Stopwatch.GetTimestamp() - h.DownAt) / (double)Stopwatch.Frequency;
            Log.Info($"Screen touch: {hand} Up from {ScreenGlass.Describe(block)} surface {surface} at pixel ({pixel.X:F0},{pixel.Y:F0}), {why}, " +
                     $"after {held:F2} s, {h.Moves} moves, the real tip {h.Deepest * 100f:F1} cm past the glass at most");
            h.Key = null;
            h.Block = null;
            Raise(block, surface, pixel, TouchPhase.Up, hand);
        }
    }
}
