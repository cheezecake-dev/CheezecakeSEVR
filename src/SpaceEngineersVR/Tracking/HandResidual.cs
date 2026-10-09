using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Sandbox;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using VRage.Game;
using VRage.Render.Scene;
using VRage.Utils;
using VRageMath;
using VRageRender;

namespace SpaceEngineersVR.Tracking
{
    /// <summary>
    /// Debug aid for the arms (<see cref="HandIK"/>): how far each drawn hand is from its controller, in the frame the
    /// player sees. RenderDebug HandResidual=1 logs it once a second; HandGhost=1 draws each controller's grip pose as a
    /// small axis triad (X red, Y green, Z blue) with a yellow dot where the palm bone is meant to be.
    /// </summary>
    /// <remarks>
    /// Measured on the render thread, just before the eyes are drawn: the palm bone as the renderer has it (the bones the
    /// game sent, times the character's world matrix as the renderer has it) against where the palm belongs on the
    /// controller as this frame shows it (the controller's grip pose now, placed in the world as the eyes are:
    /// eye pose now * inverse(head in camera) * camera). The game thread's record of what it solved travels with the
    /// camera (<see cref="CameraHeadLink"/>), so the frame measured is the frame drawn. Also logged: the solver's own
    /// miss in model space, the speed of the body, and how old the head and hands the bones were placed from are when
    /// the frame is drawn.
    /// </remarks>
    internal static class HandResidual
    {
        /// <summary>RenderDebug HandResidual=1: measure and log.</summary>
        public static bool On;

        /// <summary>RenderDebug HandGhost=1: draw the controllers.</summary>
        public static bool Ghost;

        /// <summary>RenderDebug HandGhost=2: also the handle, a 4 cm bar along the grip's Z through its origin, to see whether the closed hand wraps it.</summary>
        public static bool Handle;

        /// <summary>What the game thread solved in one frame, for the renderer.</summary>
        internal sealed class Frame
        {
            public uint RenderId = uint.MaxValue;
            public readonly int[] Palm = { -1, -1 };
            public readonly bool[] Solved = new bool[2];
            public readonly float[] SolveMiss = new float[2];
            public float Speed;
            public long HeadSampledAt;
            public HandWorld.BodySource Source;

            /// <summary>Where the simulation put the dominant hand's grip (a tool in the hand, the use and builder rays) and each hand's fingertip, in the world.</summary>
            public bool HasTool;
            public Vector3D ToolAt;
            public readonly bool[] HasPress = new bool[2];
            public readonly Vector3D[] PressAt = new Vector3D[2];
        }

        private static bool pendingTool;
        private static Vector3D pendingToolAt;
        private static readonly bool[] pendingPress = new bool[2];
        private static readonly Vector3D[] pendingPressAt = new Vector3D[2];

        /// <summary>HandTools, as it places the dominant hand for a tool or a ray (game thread, in the simulation).</summary>
        public static void NoteTool(in MatrixD grip)
        {
            if (!On)
                return;
            pendingTool = true;
            pendingToolAt = grip.Translation;
        }

        /// <summary>FingerPress, as it places a fingertip (game thread, in the simulation).</summary>
        public static void NotePress(Hand hand, in Vector3D tip)
        {
            if (!On)
                return;
            pendingPress[(int)hand] = true;
            pendingPressAt[(int)hand] = tip;
        }

        private static Frame building;

        /// <summary>Render thread: the record of the frame being drawn (set as its camera is taken on).</summary>
        public static Frame Drawing { get; set; }

        // ---- game thread ----

        /// <summary>HandIK, before it solves the arms of a frame.</summary>
        public static void Begin(MyCharacter body, HandWorld.BodySource source)
        {
            if (!On)
            {
                building = null;
                return;
            }
            building = new Frame
            {
                RenderId = body.Render?.GetRenderObjectID() ?? uint.MaxValue,
                Speed = body.GetTopMostParent()?.Physics?.LinearVelocity.Length() ?? 0f,
                HeadSampledAt = GameHead.SampledAt,
                Source = source,
            };
        }

        /// <summary>HandIK, after an arm was solved: the palm bone and where it was asked to be (model space).</summary>
        public static void Solved(Hand hand, int palm, in Matrix target, in Matrix bone)
        {
            Frame frame = building;
            if (frame == null)
                return;
            int i = (int)hand;
            frame.Palm[i] = palm;
            frame.Solved[i] = true;
            frame.SolveMiss[i] = (target.Translation - bone.Translation).Length();
        }

        /// <summary>As the camera goes to the renderer: this frame's record, to travel with it.</summary>
        public static Frame Take()
        {
            Frame frame = building;
            building = null;
            if (frame != null)
            {
                frame.HasTool = pendingTool;
                frame.ToolAt = pendingToolAt;
                for (int i = 0; i < 2; i++)
                {
                    frame.HasPress[i] = pendingPress[i];
                    frame.PressAt[i] = pendingPressAt[i];
                }
            }
            pendingTool = false;
            pendingPress[0] = pendingPress[1] = false;
            return frame;
        }

        // ---- render thread ----

        private static MethodInfo getSkinning;
        private static FieldInfo absoluteTransforms;
        private static bool reflectionFailed;

        private struct Stats
        {
            public int Frames, Missing;
            public double Sum, Max, AngleSum, AngleMax, SolveSum, SolveMax;
            public int Count;

            /// <summary>The drawn palm's offset from where it belongs, in the target's own axes (X back toward the forearm, Y the back of the hand, Z toward the thumb on a right hand), summed.</summary>
            public Vector3D Offset;
        }

        private static readonly Stats[] stats = new Stats[2];
        private static Stats toolStats;
        private static readonly Stats[] pressStats = new Stats[2];
        private static double ageSum, ageMax, speedMax;
        private static int ageCount, frames;
        private static long logAt;
        private static HandWorld.BodySource lastSource;

        /// <summary>
        /// Render thread, before the eyes are drawn. <paramref name="fromCamera"/> and <paramref name="cameraWorld"/> place
        /// the tracking space as the eyes are placed (inverse of the head in the camera, and the camera sent).
        /// </summary>
        public static void Measure(in Matrix fromCamera, in MatrixD cameraWorld)
        {
            if (!On || reflectionFailed)
                return;
            try
            {
                Frame frame = Drawing;
                if (frame == null || frame.RenderId == uint.MaxValue)
                    return;
                MyActor actor = MyIDTracker<MyActor>.FindByID(frame.RenderId);
                Matrix[] bones = Bones(actor);
                if (bones == null)
                    return;
                MatrixD actorWorld = actor.WorldMatrix;
                frames++;
                lastSource = frame.Source;
                speedMax = Math.Max(speedMax, frame.Speed);
                if (frame.HeadSampledAt != 0)
                {
                    double age = (Stopwatch.GetTimestamp() - frame.HeadSampledAt) * 1000.0 / Stopwatch.Frequency;
                    ageSum += age;
                    ageMax = Math.Max(ageMax, age);
                    ageCount++;
                }
                for (int i = 0; i < 2; i++)
                {
                    Hand hand = (Hand)i;
                    if (!frame.Solved[i])
                        continue;
                    int palm = frame.Palm[i];
                    HandState state = RenderTimeHand(hand);
                    if (palm < 0 || palm >= bones.Length || !state.Tracked)
                    {
                        stats[i].Missing++;
                        continue;
                    }
                    MatrixD want = (MatrixD)(HandIK.PalmTarget(hand, state) * fromCamera) * cameraWorld;
                    MatrixD drawn = (MatrixD)bones[palm] * actorWorld;
                    double distance = (want.Translation - drawn.Translation).Length();
                    double angle = AngleDegrees(want, drawn);
                    ref Stats s = ref stats[i];
                    s.Count++;
                    s.Sum += distance;
                    s.Max = Math.Max(s.Max, distance);
                    s.Offset += Vector3D.TransformNormal(drawn.Translation - want.Translation, MatrixD.Transpose(MatrixD.Normalize(want)));
                    s.AngleSum += angle;
                    s.AngleMax = Math.Max(s.AngleMax, angle);
                    s.SolveSum += frame.SolveMiss[i];
                    s.SolveMax = Math.Max(s.SolveMax, frame.SolveMiss[i]);
                }
                if (frame.HasTool)
                {
                    HandState tool = RenderTimeHand(VRSettings.DominantHand);
                    if (tool.Tracked)
                        Add(ref toolStats, (((MatrixD)tool.Grip * fromCamera) * cameraWorld).Translation - frame.ToolAt);
                }
                for (int i = 0; i < 2; i++)
                {
                    HandState press = RenderTimeHand((Hand)i);
                    if (frame.HasPress[i] && press.Tracked)
                    {
                        MatrixD grip = ((MatrixD)press.Grip * fromCamera) * cameraWorld, aim = ((MatrixD)press.Aim * fromCamera) * cameraWorld;
                        Add(ref pressStats[i], Hands.FingerPress.Fingertip(grip, aim) - frame.PressAt[i]);
                    }
                }
            }
            catch (Exception e)
            {
                reflectionFailed = true;
                Log.Error(e, "Hand residual could not be measured; switched off");
            }
            finally
            {
                Report();
            }
        }

        private static void Add(ref Stats s, Vector3D difference)
        {
            double distance = difference.Length();
            s.Count++;
            s.Sum += distance;
            s.Max = Math.Max(s.Max, distance);
        }

        private static string DescribeAt(in Stats s) => s.Count == 0 ? "not placed" : $"{s.Sum / s.Count * 100:F1} cm (max {s.Max * 100:F1})";

        /// <summary>The controllers as this frame shows them: the headset's, read for this frame; the desktop hands as the game has them.</summary>
        private static HandState RenderTimeHand(Hand hand)
        {
            IVRInputSource headset = VRInput.Headset;
            if (headset?.Active == true)
            {
                headset.ReadNow(out HandState left, out HandState right);
                return hand == Hand.Left ? left : right;
            }
            return VRInput.Get(hand);
        }

        /// <summary>The bones the renderer skins the character with (its MySkinningComponent's absolute transforms, model space).</summary>
        private static Matrix[] Bones(MyActor actor)
        {
            if (actor == null)
                return null;
            if (getSkinning == null)
            {
                Type extensions = typeof(MyDX11Render).Assembly.GetType("VRage.Render11.Scene.ActorExtensions", throwOnError: true);
                getSkinning = AccessTools.Method(extensions, "GetSkinning");
                absoluteTransforms = AccessTools.Field(getSkinning.ReturnType, "m_absoluteTransforms");
            }
            object skinning = getSkinning.Invoke(null, new object[] { actor });
            return skinning == null ? null : absoluteTransforms.GetValue(skinning) as Matrix[];
        }

        /// <summary>The angle between two poses' orientations, degrees.</summary>
        private static double AngleDegrees(MatrixD a, MatrixD b)
        {
            a.Translation = Vector3D.Zero;
            b.Translation = Vector3D.Zero;
            Matrix ra = Matrix.Normalize((Matrix)a), rb = Matrix.Normalize((Matrix)b);
            Matrix relative = ra * Matrix.Transpose(rb);
            double cos = (relative.M11 + relative.M22 + relative.M33 - 1.0) / 2.0;
            return MathHelper.ToDegrees(Math.Acos(Math.Max(-1.0, Math.Min(1.0, cos))));
        }

        private static void Report()
        {
            long now = Stopwatch.GetTimestamp();
            if (logAt == 0)
                logAt = now;
            if (now - logAt < Stopwatch.Frequency)
                return;
            logAt = now;
            if (frames == 0)
                return;
            string age = ageCount > 0 ? $"; head and hands {ageSum / ageCount:F0} ms old at the draw (max {ageMax:F0})" : string.Empty;
            Log.Info($"Hand residual over {frames} frames, body up to {speedMax:F1} m/s, placed by {lastSource}: " +
                     $"left {Describe(stats[0])}; right {Describe(stats[1])}{age}. " +
                     $"Hand tools' grip {DescribeAt(toolStats)}; fingertips left {DescribeAt(pressStats[0])}, right {DescribeAt(pressStats[1])}");
            stats[0] = stats[1] = default;
            toolStats = default;
            pressStats[0] = pressStats[1] = default;
            ageSum = ageMax = speedMax = 0;
            ageCount = frames = 0;
        }

        private static string Describe(in Stats s)
        {
            if (s.Count == 0)
                return s.Missing > 0 ? $"not measured ({s.Missing} frames)" : "not solved";
            Vector3D offset = s.Offset / s.Count * 100.0;
            return $"{s.Sum / s.Count * 100:F1} cm (max {s.Max * 100:F1}) [palm axes {offset.X:F1}, {offset.Y:F1}, {offset.Z:F1}], {s.AngleSum / s.Count:F1} deg (max {s.AngleMax:F1}), " +
                   $"solver miss {s.SolveSum / s.Count * 100:F1} cm (max {s.SolveMax * 100:F1})";
        }

        // ---- the ghost ----

        private static readonly MyStringId LineMaterial = MyStringId.GetOrCompute("SquareIgnoreDepth");
        private static readonly Vector4 Red = new Vector4(1f, 0.15f, 0.15f, 1f), Green = new Vector4(0.15f, 1f, 0.15f, 1f),
            Blue = new Vector4(0.2f, 0.4f, 1f, 1f), Yellow = new Vector4(1f, 0.9f, 0.1f, 1f), Cyan = new Vector4(0.1f, 1f, 1f, 1f);
        private const float AxisLength = 0.08f, AxisThickness = 0.003f;
        private const float HandleLength = 0.14f, HandleThickness = 0.04f;
        private static readonly Vector4 HandleColor = new Vector4(1f, 0.55f, 0.1f, 0.75f);
        private static int ghostErrors;

        public static void Patch(Harmony harmony)
        {
            try
            {
                // The gameplay screen's draw, after the camera of this frame was built (as the laser's line is drawn).
                harmony.Patch(AccessTools.Method(typeof(MySession), nameof(MySession.DrawSync)),
                    postfix: new HarmonyMethod(typeof(HandResidual), nameof(DrawGhost)));
            }
            catch (Exception e)
            {
                Log.Error(e, "Hand ghost could not be hooked");
            }
        }

        /// <summary>Game thread, in the draw: each controller's grip pose where this frame's camera shows it.</summary>
        private static void DrawGhost()
        {
            if (!Ghost || ghostErrors >= 3 || !VRInput.Active)
                return;
            try
            {
                if (!MyTransparentGeometry.HasCamera || !HandWorld.TryGetTrackingToWorld(out MatrixD toWorld))
                    return;
                for (int i = 0; i < 2; i++)
                {
                    Hand hand = (Hand)i;
                    HandState state = VRInput.Get(hand);
                    if (!state.Tracked)
                        continue;
                    MatrixD grip = (MatrixD)state.Grip * toWorld;
                    Axis(grip.Translation, grip.Right, Red);
                    Axis(grip.Translation, grip.Up, Green);
                    Axis(grip.Translation, grip.Backward, Blue);
                    if (Handle)
                    {
                        double length = HandleLength, forwardLength = grip.Forward.Length();
                        if (forwardLength > 1e-6)
                        {
                            Vector3D along = grip.Forward / forwardLength;
                            MyTransparentGeometry.AddLineBillboard(LineMaterial, HandleColor, grip.Translation - along * (length / 2), (Vector3)along, (float)length, HandleThickness);
                        }
                    }
                    MatrixD palm = (MatrixD)HandIK.PalmTarget(hand, state) * toWorld;
                    MyTransparentGeometry.AddPointBillboard(LineMaterial, Yellow, palm.Translation, 0.008f, 0f);
                    if (Handle)
                    {
                        // Where the finger press takes the index fingertip to be (HandGhost=2): it should meet the glove's tip.
                        MatrixD aim = (MatrixD)state.Aim * toWorld;
                        MyTransparentGeometry.AddPointBillboard(LineMaterial, Cyan, Hands.FingerPress.Fingertip(grip, aim), 0.008f, 0f);
                    }
                }
            }
            catch (Exception e)
            {
                if (ghostErrors++ < 3)
                    Log.Error(e, "Hand ghost could not draw");
            }
        }

        private static void Axis(Vector3D from, Vector3D along, Vector4 color)
        {
            double length = along.Length();
            if (length < 1e-6)
                return;
            MyTransparentGeometry.AddLineBillboard(LineMaterial, color, from, (Vector3)(along / length), AxisLength, AxisThickness);
        }
    }
}
