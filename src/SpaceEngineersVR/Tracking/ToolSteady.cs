using System;
using System.Diagnostics;
using HarmonyLib;
using Sandbox;
using Sandbox.Definitions;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using VRage.Game.Entity;
using VRageMath;
using VRageRender.Animations;

namespace SpaceEngineersVR.Tracking
{
    /// <summary>
    /// Debug (<see cref="Rendering.RenderDebug"/> ToolSteady=1): once a second while the local character holds a tool on foot
    /// in first person, how far each link that places the tool moved in the view over that second, read as each camera goes to
    /// the renderer (<see cref="CameraHeadLink"/>). Spans are max - min, cm, along the view's right / up / back:
    /// the tool's hand spot (item RightHand, where the palm goes); the controller's grip it is placed from; the tool off that
    /// grip (the placement); the right palm off the tool's hand spot (the arm); the head matrix the camera should be built from,
    /// in the camera (the game's camera shake); and how far the body was drawn ahead (<see cref="BodyShift"/>).
    /// "Came back" counts the tool's hand spot turning between coming toward the eye and going away from it by 3 mm or
    /// more, so a wobble shows its rate. Still hands in a still view should show spans near zero: the one that is not names the
    /// link that moves.
    /// </summary>
    internal static class ToolSteady
    {
        public static bool On;

        private static readonly AccessTools.FieldRef<MyCharacter, int> RightPalm = AccessTools.FieldRefAccess<MyCharacter, int>("m_rightHandIKEndBone");

        /// <summary>The tool has to come back this far (metres) along the view's back axis to count as turning.</summary>
        private const double Turn = 0.003;

        private static readonly long Second = Stopwatch.Frequency;

        private struct Span
        {
            public Vector3D Min, Max;
            public int Count;

            public void Add(in Vector3D v)
            {
                if (Count++ == 0)
                {
                    Min = Max = v;
                    return;
                }
                Min = Vector3D.Min(Min, v);
                Max = Vector3D.Max(Max, v);
            }

            public override string ToString() => Count == 0
                ? "none"
                : $"{(Max.X - Min.X) * 100:0.0}/{(Max.Y - Min.Y) * 100:0.0}/{(Max.Z - Min.Z) * 100:0.0}";
        }

        private static Span tool, controller, placed, palm, shake;
        private static double toolTurn, shakeTurn;
        private static Vector3D firstForward;
        private static float aheadMin, aheadMax, stretchMin, stretchMax, swayMax;
        private static double palmFar;
        private static bool hasStretch;
        private static int frames, simFrames;
        private static ulong lastSim;
        private static double extreme, low, high;
        private static int direction, swings;
        private static long windowAt;
        private static string itemName;
        private static Vector3D otherGrip;
        private static bool hasOtherGrip;
        private static int errors;

        /// <summary>Game thread, as a camera goes to the renderer (<see cref="CameraHeadLink"/>).</summary>
        public static void Camera(in MatrixD view)
        {
            if (!On || errors >= 3)
                return;
            try
            {
                Sample(view);
            }
            catch (Exception e)
            {
                if (errors++ < 3)
                    Log.Error(e, "Tool steadiness could not be measured");
            }
        }

        private static void Sample(in MatrixD view)
        {
            MyCharacter character = MySession.Static?.LocalCharacter;
            MyEntity weapon = character?.CurrentWeapon as MyEntity;
            MyHandItemDefinition item = character?.HandItemDefinition;
            if (character == null || character.Closed || character.PositionComp == null || weapon == null || weapon.Closed || item == null
                || !CharacterHead.IsLocalFirstPerson(character))
            {
                Clear();
                return;
            }

            long now = Stopwatch.GetTimestamp();
            if (frames == 0)
                windowAt = now;
            string name = item.Id.SubtypeName;
            if (itemName != name)
            {
                Clear();
                windowAt = now;
                itemName = name;
            }

            MatrixD toolWorld = weapon.WorldMatrix;
            Vector3D spot = Vector3D.Transform((Vector3D)item.RightHand.Translation, toolWorld);
            Vector3D spotInView = Vector3D.Transform(spot, view);
            tool.Add(spotInView);
            Vector3D forward = Vector3D.Normalize(Vector3D.TransformNormal(toolWorld.Forward, view));
            if (frames == 0)
                firstForward = forward;
            else
                toolTurn = Math.Max(toolTurn, Degrees(firstForward, forward));
            Swing(spotInView.Z);

            if (HandWorld.TryGetNow(character, VRSettings.DominantHand, out MatrixD grip, out _, out _))
            {
                Vector3D gripInView = Vector3D.Transform(grip.Translation, view);
                controller.Add(gripInView);
                placed.Add(spotInView - gripInView);
                // The tool's other grip in the tracking space, for placing a fake other hand on it: the world is the tracking
                // space turned and moved, so the tool hand's controller carries the one into the other.
                HandState main = VRInput.Get(VRSettings.DominantHand);
                if (main.Tracked)
                {
                    Vector3D left = Vector3D.Transform((Vector3D)item.LeftHand.Translation, toolWorld);
                    otherGrip = Vector3D.Transform(left, MatrixD.Invert(grip) * (MatrixD)main.Grip);
                    hasOtherGrip = true;
                }
            }

            int palmBone = RightPalm(character);
            MyCharacterBone[] bones = character.AnimationController?.CharacterBones;
            if (bones != null && palmBone >= 0 && palmBone < bones.Length)
            {
                Vector3D palmWorld = Vector3D.Transform((Vector3D)bones[palmBone].AbsoluteTransform.Translation, character.PositionComp.WorldMatrixRef);
                Vector3D palmOff = Vector3D.Transform(palmWorld, view) - spotInView;
                palm.Add(palmOff);
                palmFar = Math.Max(palmFar, palmOff.Length());
            }

            // The camera is built from this head matrix (MyCharacter.GetViewMatrix); anything between is the camera's own shake.
            MatrixD head = character.GetHeadMatrix(true, true, false, character.ForceFirstPersonCamera, true);
            shake.Add(Vector3D.Transform(head.Translation, view));
            MatrixD camera = MatrixD.Invert(view);
            shakeTurn = Math.Max(shakeTurn, Degrees(Vector3D.Normalize(head.Forward), Vector3D.Normalize(camera.Forward)));

            float stretch = HandIK.HeldStretch;
            if (!float.IsNaN(stretch))
            {
                stretchMin = hasStretch ? Math.Min(stretchMin, stretch) : stretch;
                stretchMax = hasStretch ? Math.Max(stretchMax, stretch) : stretch;
                hasStretch = true;
            }
            swayMax = Math.Max(swayMax, HandIK.HeldSway);
            float ahead = BodyShift.Applied;
            aheadMin = frames == 0 ? ahead : Math.Min(aheadMin, ahead);
            aheadMax = frames == 0 ? ahead : Math.Max(aheadMax, ahead);
            ulong sim = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            if (frames == 0 || sim != lastSim)
                simFrames++;
            lastSim = sim;
            frames++;

            if (now - windowAt < Second)
                return;
            Log.Info($"Tool steady: {itemName} over {frames} cameras ({simFrames} simulation frames), spans cm right/up/back: " +
                     $"tool {tool} (turned up to {toolTurn:0.0} deg, came back {swings} times), controller {controller}, " +
                     $"tool off the controller {placed}, palm off the tool {palm} (up to {palmFar * 100:0.0} cm), " +
                     (hasStretch ? $"arm stretched x{stretchMin:F3}..x{stretchMax:F3}, shoulder held up to {swayMax * 100:0.0} cm off its sway, " : string.Empty) +
                     $"camera off the head {shake} ({shakeTurn:0.0} deg), " +
                     $"body ahead {aheadMin * 100:0.0}..{aheadMax * 100:0.0} cm" +
                     (hasOtherGrip ? $"; the tool's other grip at tracking ({otherGrip.X:F3}, {otherGrip.Y:F3}, {otherGrip.Z:F3}) m" : string.Empty));
            Clear();
        }

        /// <summary>Counts the tool's hand spot turning along the view's back axis, out of a 3 mm dead band.</summary>
        private static void Swing(double back)
        {
            if (frames == 0)
            {
                low = high = back;
                direction = 0;
                return;
            }
            if (direction == 0)
            {
                // Which way it goes first.
                low = Math.Min(low, back);
                high = Math.Max(high, back);
                if (back - low >= Turn || high - back >= Turn)
                {
                    direction = back - low >= Turn ? 1 : -1;
                    extreme = back;
                }
                return;
            }
            if (direction > 0 ? back > extreme : back < extreme)
            {
                extreme = back;
                return;
            }
            if (Math.Abs(back - extreme) < Turn)
                return;
            swings++;
            direction = -direction;
            extreme = back;
        }

        private static double Degrees(in Vector3D a, in Vector3D b) =>
            MathHelper.ToDegrees(Math.Acos(MathHelper.Clamp(Vector3D.Dot(a, b), -1.0, 1.0)));

        private static void Clear()
        {
            tool = controller = placed = palm = shake = default;
            toolTurn = shakeTurn = palmFar = 0.0;
            hasStretch = false;
            swayMax = 0f;
            frames = simFrames = 0;
            swings = 0;
            direction = 0;
            hasOtherGrip = false;
        }
    }
}
