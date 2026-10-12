using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.Utils;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// Use and interact from the hand's ray instead of the head's. The game finds what the character can use in two
    /// detectors: MyCharacterRaycastDetectorComponent casts a line from the head, and MyCharacterClosestDetectorComponent
    /// (the one that is active, because area interactions are on) runs that one first and then sweeps a sphere along the
    /// same line when it found nothing. Both build the line from the character's head matrix and the camera's world
    /// matrix; inside these two methods only, those two reads give the hand's pose (hand position, hand forward), and
    /// everything after them (what the line hit, the use objects, their highlight, what Use does) is the game's own. A
    /// button on a button panel is a use object of the panel, found by <c>RaycastDetectors</c> along this line, so it
    /// works by pointing at it.
    /// <para>The line is no longer from the hand than to where the game's own ends: the game's reach is measured from the head
    /// (<see cref="MyConstants.DEFAULT_INTERACTIVE_DISTANCE"/>, 5 m, from 0.3 m behind it: 4.7 m ahead of it for the line; the
    /// sphere sweep's 5 m from the camera), and each detector's read of that constant gives the length along the hand's ray
    /// that keeps its end within the same distance of the character's head (<see cref="LineLength"/>, <see cref="SweepLength"/>).</para>
    /// </summary>
    /// <remarks>
    /// The game runs the detection every tenth frame (MyCharacterDetectorComponent.UpdateAfterSimulation10), so what the
    /// hand points at is picked up with up to about a sixth of a second of delay, as it is for the head.
    /// </remarks>
    internal static class HandUse
    {
        private static int errors;

        public static bool Hooked { get; private set; }

        public static void Patch(Harmony harmony)
        {
            // DoDetection is the only place either detector reads the head matrix and the camera matrix; the transpiler swaps
            // those reads for the hand's pose, and the read of the use distance for the length that keeps the end within the
            // game's reach of the head (see TranspileDetection). Without it the use ray stays on the head.
            Type closest = AccessTools.TypeByName("Sandbox.Game.Entities.Character.Components.MyCharacterClosestDetectorComponent")
                           ?? throw new MissingMemberException("MyCharacterClosestDetectorComponent");
            castRadius = AccessTools.Field(closest, "m_castRadius") ?? throw new MissingMemberException(closest.Name, "m_castRadius");
            harmony.Patch(DoDetection(typeof(MyCharacterRaycastDetectorComponent)), transpiler: new HarmonyMethod(typeof(HandUse), nameof(TranspileLine)));
            harmony.Patch(DoDetection(closest), transpiler: new HarmonyMethod(typeof(HandUse), nameof(TranspileSweep)));
            Hooked = true;
            Log.Info($"Hand tools: use from the hand hooked (2 detectors; head reads rewritten: {headReads}, camera matrix reads: {cameraReads}, use distance reads: {distanceReads})");
        }

        private static MethodInfo DoDetection(Type detector) =>
            AccessTools.DeclaredMethod(detector, "DoDetection", new[] { typeof(bool) }) ?? throw new MissingMemberException(detector.Name, "DoDetection");

        private static int headReads, cameraReads, distanceReads;

        /// <summary>The closest detector's sphere radius (MyCharacterClosestDetectorComponent.m_castRadius, 0.5 m).</summary>
        private static FieldInfo castRadius;

        private static IEnumerable<CodeInstruction> TranspileLine(IEnumerable<CodeInstruction> instructions) =>
            TranspileDetection(instructions, AccessTools.Method(typeof(HandUse), nameof(LineLength)), false);

        private static IEnumerable<CodeInstruction> TranspileSweep(IEnumerable<CodeInstruction> instructions) =>
            TranspileDetection(instructions, AccessTools.Method(typeof(HandUse), nameof(SweepLength)), true);

        private static IEnumerable<CodeInstruction> TranspileDetection(IEnumerable<CodeInstruction> instructions, MethodInfo length, bool withDetector)
        {
            int heads = 0, cameras = 0;
            IEnumerable<CodeInstruction> result = HandTools.Redirect(instructions, HandTools.IsCharacterHeadMatrix,
                AccessTools.Method(typeof(HandUse), nameof(HeadMatrix)), 1, ref heads);
            result = HandTools.RedirectField(result, AccessTools.Field(typeof(MyCamera), nameof(MyCamera.WorldMatrix)),
                AccessTools.Method(typeof(HandUse), nameof(CameraMatrix)), AccessTools.Method(typeof(HandUse), nameof(CameraMatrixRef)), 1, ref cameras);
            result = RedirectDistance(result, length, withDetector);
            headReads += heads;
            cameraReads += cameras;
            return result;
        }

        /// <summary>
        /// Rewrites the read of <see cref="MyConstants.DEFAULT_INTERACTIVE_DISTANCE"/> into a call of <paramref name="length"/>,
        /// given the detector first when <paramref name="withDetector"/>. Throws when the method has no such read.
        /// </summary>
        private static IEnumerable<CodeInstruction> RedirectDistance(IEnumerable<CodeInstruction> instructions, MethodInfo length, bool withDetector)
        {
            FieldInfo distance = AccessTools.Field(typeof(MyConstants), nameof(MyConstants.DEFAULT_INTERACTIVE_DISTANCE));
            var result = new List<CodeInstruction>();
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.opcode != OpCodes.Ldsfld || !Equals(instruction.operand, distance))
                {
                    result.Add(instruction);
                    continue;
                }
                var first = withDetector ? new CodeInstruction(OpCodes.Ldarg_0) : new CodeInstruction(OpCodes.Call, length);
                first.labels.AddRange(instruction.labels);
                first.blocks.AddRange(instruction.blocks);
                result.Add(first);
                if (withDetector)
                    result.Add(new CodeInstruction(OpCodes.Call, length));
                replaced++;
            }
            if (replaced == 0)
                throw new InvalidOperationException($"Expected a read of {distance.Name} to replace with {length.Name}, found none");
            distanceReads += replaced;
            return result;
        }

        /// <summary>
        /// Replaces <see cref="MyConstants.DEFAULT_INTERACTIVE_DISTANCE"/> in MyCharacterRaycastDetectorComponent.DoDetection,
        /// whose line runs that far from 0.3 m behind the head (<see cref="HandTools.GameUseRayBehind"/>): the length along the
        /// hand's ray that ends no farther than <see cref="LineReach"/> from the head, as the game's line does, and no longer than the game's.
        /// </summary>
        public static float LineLength()
        {
            if (!TryReach(out MyCharacter character, out Vector3D origin, out Vector3D forward))
                return MyConstants.DEFAULT_INTERACTIVE_DISTANCE;
            return (float)UseLength(character, origin, forward);
        }

        /// <summary>How far the use line runs from <paramref name="origin"/> along the unit <paramref name="forward"/>: to <see cref="LineReach"/> from the head, and no farther than the game's line runs.</summary>
        internal static double UseLength(MyCharacter character, Vector3D origin, Vector3D forward) =>
            Math.Min(Along(origin, forward, Head(character), LineReach), MyConstants.DEFAULT_INTERACTIVE_DISTANCE);

        /// <summary>
        /// Replaces <see cref="MyConstants.DEFAULT_INTERACTIVE_DISTANCE"/> in MyCharacterClosestDetectorComponent.DoDetection,
        /// whose sphere is swept from the camera that far less its radius, so it reaches that far from the eye: the length that
        /// keeps all of the sphere, at the end of its sweep along the hand's ray, as near the head, and no longer than the game's.
        /// </summary>
        public static float SweepLength(MyCharacterDetectorComponent detector)
        {
            if (!TryReach(out MyCharacter character, out Vector3D origin, out Vector3D forward))
                return MyConstants.DEFAULT_INTERACTIVE_DISTANCE;
            float radius = castRadius.GetValue(detector) is float r ? r : 0f;
            double reach = MyConstants.DEFAULT_INTERACTIVE_DISTANCE - radius;
            return (float)(Math.Min(Along(origin, forward, Head(character), reach), reach) + radius);
        }

        /// <summary>How far from the head the game's use line ends, metres: 4.7.</summary>
        internal static double LineReach => MyConstants.DEFAULT_INTERACTIVE_DISTANCE - HandTools.GameUseRayBehind;

        /// <summary>The character's head as the game measures its use reach from (its head matrix's position, not the tracked eye's).</summary>
        internal static Vector3D Head(MyCharacter character) => character.GetHeadMatrix(false).Translation;

        /// <summary>
        /// How far along the unit <paramref name="forward"/> from <paramref name="origin"/> a point stays within
        /// <paramref name="reach"/> of <paramref name="head"/>; 0 when the origin itself is not.
        /// </summary>
        internal static double Along(Vector3D origin, Vector3D forward, Vector3D head, double reach)
        {
            Vector3D w = origin - head;
            double b = Vector3D.Dot(forward, w);
            double c = w.LengthSquared() - reach * reach;
            double d = b * b - c;
            return c > 0.0 || d < 0.0 ? 0.0 : Math.Max(-b + Math.Sqrt(d), 0.0);
        }

        /// <summary>The local character and the hand's use ray, when the use ray is the hand's.</summary>
        private static bool TryReach(out MyCharacter character, out Vector3D origin, out Vector3D forward)
        {
            character = MySession.Static?.LocalCharacter;
            return TryHand(character, out origin, out forward, out _);
        }

        /// <summary>Replaces <c>Character.GetHeadMatrix(includeY: false)</c>: forward is the hand's, and the game's start (translation minus 0.3 m) is the hand.</summary>
        public static MatrixD HeadMatrix(MyCharacter character, bool includeY, bool includeX, bool forceHeadAnim, bool forceHeadBone, bool preferLocalOverSync)
        {
            if (TryHand(character, out Vector3D origin, out Vector3D forward, out Vector3D up))
                return HandTools.HeadLike(origin, forward, up);
            return character.GetHeadMatrix(includeY, includeX, forceHeadAnim, forceHeadBone, preferLocalOverSync);
        }

        /// <summary>Replaces <c>MySector.MainCamera.WorldMatrix</c>: at the hand, facing where it points.</summary>
        public static MatrixD CameraMatrix(MyCamera camera)
        {
            if (TryHand(MySession.Static?.LocalCharacter, out Vector3D origin, out Vector3D forward, out Vector3D up))
                return HandTools.CameraLike(origin, forward, up);
            return camera.WorldMatrix;
        }

        private static MatrixD handCamera;

        /// <summary>The same for a read of the camera matrix's address (a struct's property, as <c>.Translation</c>, is read through it).</summary>
        public static ref MatrixD CameraMatrixRef(MyCamera camera)
        {
            if (TryHand(MySession.Static?.LocalCharacter, out Vector3D origin, out Vector3D forward, out Vector3D up))
            {
                handCamera = HandTools.CameraLike(origin, forward, up);
                return ref handCamera;
            }
            return ref camera.WorldMatrix;
        }

        private static bool TryHand(MyCharacter character, out Vector3D origin, out Vector3D forward, out Vector3D up)
        {
            origin = forward = up = Vector3D.Zero;
            try
            {
                // The detectors run for the controlled character only (UpdateAfterSimulation10 returns otherwise). Once the hands'
                // line checks have given up (Reach.GivenUp) the detectors keep the head's own pose, and use is what the game allows from it.
                if (Reach.GivenUp || !HandTools.IsLocalOnFoot(character) || !HandTools.TryGetHand(out _, out MatrixD aim) || !HandTools.ReachRay(character, aim, out origin, out forward))
                    return false;
                up = aim.Up;
                return true;
            }
            catch (Exception e)
            {
                if (errors++ < 3)
                    Log.Error(e, "Hand tools could not take the use ray from the hand");
                return false;
            }
        }
    }
}
