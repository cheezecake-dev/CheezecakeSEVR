using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity.UseObject;
using VRage.Game.Models;
using VRage.Game.Utils;
using VRage.ModAPI;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// Use and interact from the hand's ray instead of the head's. The game finds what the character can use in two
    /// detectors: MyCharacterRaycastDetectorComponent casts a line from the head, and MyCharacterClosestDetectorComponent
    /// (the one that is active, because area interactions are on) runs that one first and then sweeps a sphere from the
    /// camera when it found nothing. Both build their path from the character's head matrix and the camera's world
    /// matrix; inside these two methods only, those two reads are rewritten, and everything after them (what was hit, the
    /// use objects, their highlight, what Use does) is the game's own. A button on a button panel is a use object of the
    /// panel, found by <c>RaycastDetectors</c> along the line, so it works by pointing at it.
    /// <para>The line is the hand's (hand position, hand forward), and no longer from the hand than to where the game's own
    /// ends: the game's reach is measured from the head (<see cref="MyConstants.DEFAULT_INTERACTIVE_DISTANCE"/>, 5 m, from
    /// 0.3 m behind it: 4.7 m ahead of it), and the line's read of that constant gives the length along the hand's ray that
    /// keeps its end within the same distance of the head (<see cref="LineLength"/>).</para>
    /// <para>The sphere sweep is the game's, from the eye (the camera's position), with the eye held to the head: in VR the
    /// camera is the tracked eye, which can lean away from the head bone and through a wall, so the sweep starts at the eye
    /// brought back to the near side of anything solid between the head bone and it (<see cref="SweepStart"/>), and runs no
    /// farther than keeps its sphere within the game's use distance of the head bone (<see cref="SweepDistance"/>). Its
    /// direction is the hand's: from that start towards the point the hand aims at (<see cref="SweepAim"/>). So it is a sweep
    /// a flat player at the same eye makes by looking at that point: it reaches nothing he could not, and misses nothing
    /// between the eye and an outstretched hand. What it finds is taken only if a corner of its box can be seen from where
    /// the sweep starts, as the game's is.</para>
    /// <para>What the line finds is held to the eye too (<see cref="LineSight"/>): the game's line starts at the head, so it
    /// finds nothing the eye cannot see, but the hand's can start around a corner. It is kept only if the eye sees the spot
    /// the line hit or a corner of what it found; otherwise detection goes on to the sweep, as the game's does when its line
    /// finds nothing. A check that fails drops the find.</para>
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
            // DoDetection is the only place either detector reads the head matrix and the camera matrix. In the line's, the
            // transpiler swaps those reads for the hand's pose, and the read of the use distance for the length that keeps the
            // end within the game's reach of the head (TranspileLine); in the sweep's, for the eye turned towards what the hand
            // aims at (TranspileSweep). Without them use stays on the head.
            Type closest = AccessTools.TypeByName("Sandbox.Game.Entities.Character.Components.MyCharacterClosestDetectorComponent")
                           ?? throw new MissingMemberException("MyCharacterClosestDetectorComponent");
            allCorners = AccessTools.DeclaredMethod(closest, "RaycastAllCorners");
            allCornersFloating = AccessTools.DeclaredMethod(closest, "RaycastAllCornersFloating");
            useObjectSetter = AccessTools.PropertySetter(typeof(MyCharacterDetectorComponent), nameof(MyCharacterDetectorComponent.UseObject))
                              ?? throw new MissingMemberException(nameof(MyCharacterDetectorComponent), "UseObject setter");
            harmony.Patch(DoDetection(typeof(MyCharacterRaycastDetectorComponent)), transpiler: new HarmonyMethod(typeof(HandUse), nameof(TranspileLine)),
                postfix: new HarmonyMethod(typeof(HandUse), nameof(LineSight)));
            castRadius = AccessTools.Field(closest, "m_castRadius") ?? throw new MissingMemberException(closest.Name, "m_castRadius");
            harmony.Patch(DoDetection(closest), transpiler: new HarmonyMethod(typeof(HandUse), nameof(TranspileSweep)));
            Hooked = true;
            Log.Info($"Hand tools: use from the hand hooked (2 detectors; head reads rewritten: {headReads}, camera matrix reads: {cameraReads}, use distance reads: {distanceReads}; sweep from the eye held to the head, towards the hand's aim, within the use distance of the head; line pass held to the eye)");
        }

        private static MethodInfo allCorners, allCornersFloating, useObjectSetter;
        private static FieldInfo castRadius;

        /// <summary>How much nearer than the line's hit the eye's own line may stop and still be taken to see it, metres.</summary>
        private const double SightTolerance = 0.05;

        /// <summary>
        /// Postfix of MyCharacterRaycastDetectorComponent.DoDetection (the line pass; the closest detector runs it first and
        /// sweeps only when it found nothing). The game's line starts at the head, so whatever it finds the eye can see; the
        /// hand's starts at the hand, which can reach around a corner or past an edge the eye cannot see over. What it found is
        /// kept when the eye sees the spot the line hit (its own line to it is not stopped short, passing through blocks that
        /// allow interaction through them as the game's line does) or a corner of its detector box (the game's own corner
        /// test, as the sweep's); otherwise it is dropped, and the closest detector sweeps, as when its line finds nothing.
        /// </summary>
        private static void LineSight(MyCharacterRaycastDetectorComponent __instance)
        {
            try
            {
                IMyUseObject found = __instance.UseObject;
                MyCharacter character = __instance.Character;
                if (found == null || MySector.MainCamera == null || !TryHand(character, out _, out _, out _))
                    return;
                Vector3D eye = SweepStart(character, MySector.MainCamera.WorldMatrix.Translation);
                if (SeesSpot(character, eye, __instance.HitPosition) || SeesCorner(__instance, found, eye))
                    return;
                useObjectSetter.Invoke(__instance, new object[] { null });
            }
            catch (Exception e)
            {
                if (errors++ < 3)
                    Log.Error(e, "Hand tools could not hold the use line's find to what the eye sees");
                // Unchecked is not seen: drop the find, as when the eye cannot see it.
                try
                {
                    useObjectSetter?.Invoke(__instance, new object[] { null });
                }
                catch (Exception clear)
                {
                    if (errors++ < 3)
                        Log.Error(clear, "Hand tools could not drop the use line's unchecked find");
                }
            }
        }

        /// <summary>The eye's line to <paramref name="spot"/> is not stopped short of it, past blocks that let use through them as the game's line goes past them.</summary>
        private static bool SeesSpot(MyCharacter character, Vector3D eye, Vector3D spot)
        {
            Vector3D toSpot = spot - eye;
            double distance = toSpot.Length();
            if (distance < SightTolerance)
                return true;
            Vector3D dir = toSpot / distance;
            Vector3D from = eye;
            for (int i = 0; i < 8; i++)
            {
                LineD line = new LineD(from, spot + dir * SightTolerance);
                MyIntersectionResultLineTriangleEx? hit = MyEntities.GetIntersectionWithLine(ref line, character, null, ignoreChildren: false, ignoreFloatingObjects: false);
                if (!hit.HasValue)
                    return true;
                Vector3D at = hit.Value.IntersectionPointInWorldSpace;
                if (Vector3D.Distance(eye, at) >= distance - SightTolerance)
                    return true;
                if (hit.Value.Entity is MyCubeGrid || !(hit.Value.UserObject is MyCube cube)
                    || cube.CubeBlock?.FatBlock?.BlockDefinition.AllowInteractionThroughBlock != true)
                    return false;
                from = at + dir * 0.05;
            }
            return false;
        }

        /// <summary>The game's own corner test (MyCharacterClosestDetectorComponent.RaycastAllCorners, or its floating-object one) from the eye.</summary>
        private static bool SeesCorner(MyCharacterRaycastDetectorComponent detector, IMyUseObject found, Vector3D eye)
        {
            if (allCorners == null || allCornersFloating == null || !allCorners.DeclaringType.IsInstanceOfType(detector))
                return false;
            if (found is MyFloatingObject)
                return (bool)allCornersFloating.Invoke(detector, new object[] { found, eye });
            MyUseObjectsComponentBase component = found.Owner?.Components.Get<MyUseObjectsComponentBase>();
            if (component != null)
                return (bool)allCorners.Invoke(detector, new object[] { component, found, eye });
            if (found is IMyEntity entity && entity.Model != null)
                return (bool)allCornersFloating.Invoke(detector, new object[] { found, eye });
            return false;
        }

        private static MethodInfo DoDetection(Type detector) =>
            AccessTools.DeclaredMethod(detector, "DoDetection", new[] { typeof(bool) }) ?? throw new MissingMemberException(detector.Name, "DoDetection");

        private static int headReads, cameraReads, distanceReads;

        /// <summary>The line's DoDetection: the head and camera reads give the hand's pose, the use distance <see cref="LineLength"/>.</summary>
        private static IEnumerable<CodeInstruction> TranspileLine(IEnumerable<CodeInstruction> instructions) =>
            RedirectDistance(TranspilePose(instructions, nameof(HeadMatrix), nameof(CameraMatrix), nameof(CameraMatrixRef)),
                AccessTools.Method(typeof(HandUse), nameof(LineLength)));

        /// <summary>
        /// The sweep's DoDetection: the camera reads give the eye held to the head (<see cref="SweepStart"/>), the head and
        /// camera reads face what the hand aims at (<see cref="SweepAim"/>), and the use distance read gives
        /// <see cref="SweepDistance"/>, from which the game takes the sphere's radius as before.
        /// </summary>
        private static IEnumerable<CodeInstruction> TranspileSweep(IEnumerable<CodeInstruction> instructions) =>
            RedirectDistance(TranspilePose(instructions, nameof(SweepHeadMatrix), nameof(SweepCameraMatrix), nameof(SweepCameraMatrixRef)),
                AccessTools.Method(typeof(HandUse), nameof(SweepDistance)), withThis: true);

        private static IEnumerable<CodeInstruction> TranspilePose(IEnumerable<CodeInstruction> instructions, string head, string camera, string cameraRef)
        {
            int heads = 0, cameras = 0;
            IEnumerable<CodeInstruction> result = HandTools.Redirect(instructions, HandTools.IsCharacterHeadMatrix,
                AccessTools.Method(typeof(HandUse), head), 1, ref heads);
            result = HandTools.RedirectField(result, AccessTools.Field(typeof(MyCamera), nameof(MyCamera.WorldMatrix)),
                AccessTools.Method(typeof(HandUse), camera), AccessTools.Method(typeof(HandUse), cameraRef), 1, ref cameras);
            headReads += heads;
            cameraReads += cameras;
            return result;
        }

        /// <summary>
        /// Rewrites the read of <see cref="MyConstants.DEFAULT_INTERACTIVE_DISTANCE"/> into a call of <paramref name="length"/>,
        /// given the detector (<c>this</c>) when <paramref name="withThis"/>. Throws when the method has no such read.
        /// </summary>
        private static IEnumerable<CodeInstruction> RedirectDistance(IEnumerable<CodeInstruction> instructions, MethodInfo length, bool withThis = false)
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
                var call = new CodeInstruction(OpCodes.Call, length);
                CodeInstruction first = withThis ? new CodeInstruction(OpCodes.Ldarg_0) : call;
                first.labels.AddRange(instruction.labels);
                first.blocks.AddRange(instruction.blocks);
                if (withThis)
                    result.Add(first);
                result.Add(call);
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

        /// <summary>How far from the head the game's use line ends, metres: 4.7.</summary>
        internal static double LineReach => MyConstants.DEFAULT_INTERACTIVE_DISTANCE - HandTools.GameUseRayBehind;

        /// <summary>The character's head bone, which use reach is measured from (<see cref="Reach.TryHead"/>; not the tracked eye, which can lean away from it).</summary>
        internal static Vector3D Head(MyCharacter character) =>
            Reach.TryHead(character, out Vector3D head) ? head : character.GetHeadMatrix(false).Translation;

        /// <summary>
        /// Where the sweep starts, for the eye at <paramref name="eye"/>: while VR draws the picture of the player's own
        /// character on foot, the eye brought back to the near side of anything solid on the line from the head bone to it
        /// (<see cref="Reach.Unblocked"/>; at the head bone when that line cannot be checked). Otherwise the eye.
        /// </summary>
        internal static Vector3D SweepStart(MyCharacter character, Vector3D eye) =>
            HeldToHead(character) ? Reach.Unblocked(character, eye) : eye;

        /// <summary>Whether the sweep is held to the head: the camera is the tracked eye, of the player's own character on foot.</summary>
        private static bool HeldToHead(MyCharacter character) => Rendering.StereoPatches.VRPicture && HandTools.IsLocalOnFoot(character);

        /// <summary>
        /// Replaces <see cref="MyConstants.DEFAULT_INTERACTIVE_DISTANCE"/> in MyCharacterClosestDetectorComponent.DoDetection,
        /// whose sphere runs that distance less its radius from the start: when the sweep is held to the head, the distance
        /// that ends the sphere (centre and radius) within the game's use distance of the head bone, from <see cref="SweepStart"/>
        /// along the sweep's direction. The game's otherwise.
        /// </summary>
        public static float SweepDistance(MyCharacterDetectorComponent detector)
        {
            float native = MyConstants.DEFAULT_INTERACTIVE_DISTANCE;
            try
            {
                MyCharacter character = detector?.Character;
                MyCamera camera = MySector.MainCamera;
                if (camera == null || !HeldToHead(character))
                    return native;
                float radius = (float)castRadius.GetValue(detector);
                MatrixD sweep = SweepCameraMatrix(camera);
                double along = Along(sweep.Translation, sweep.Forward, Head(character), Math.Max(native - radius, 0f));
                return (float)Math.Min(along + radius, native);
            }
            catch (Exception e)
            {
                if (errors++ < 3)
                    Log.Error(e, "Hand tools could not hold the use sweep within reach of the head");
                // Unchecked is not in reach: a sweep of no length.
                return 0f;
            }
        }

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

        /// <summary>Replaces <c>MySector.MainCamera.WorldMatrix</c> in the line's detection: at the hand, facing where it points.</summary>
        public static MatrixD CameraMatrix(MyCamera camera)
        {
            if (TryHand(MySession.Static?.LocalCharacter, out Vector3D origin, out Vector3D forward, out Vector3D up))
                return HandTools.CameraLike(origin, forward, up);
            return camera.WorldMatrix;
        }

        private static MatrixD handCamera, sweepCamera;

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

        /// <summary>
        /// Replaces <c>Character.GetHeadMatrix(includeY: false)</c> in the sweep's detection: the head's own, facing
        /// <see cref="SweepAim"/> from the sweep's start. The sweep takes only its forward, and only when the game aims it by the head (useHead).
        /// </summary>
        public static MatrixD SweepHeadMatrix(MyCharacter character, bool includeY, bool includeX, bool forceHeadAnim, bool forceHeadBone, bool preferLocalOverSync)
        {
            MatrixD head = character.GetHeadMatrix(includeY, includeX, forceHeadAnim, forceHeadBone, preferLocalOverSync);
            if (MySector.MainCamera != null && SweepAim(character, SweepStart(character, MySector.MainCamera.WorldMatrix.Translation), out Vector3D forward, out Vector3D up))
                return MatrixD.CreateWorld(head.Translation, forward, up);
            return head;
        }

        /// <summary>Replaces <c>MySector.MainCamera.WorldMatrix</c> in the sweep's detection: at <see cref="SweepStart"/>, facing <see cref="SweepAim"/>.</summary>
        public static MatrixD SweepCameraMatrix(MyCamera camera)
        {
            MatrixD eye = camera.WorldMatrix;
            MyCharacter character = MySession.Static?.LocalCharacter;
            Vector3D start = SweepStart(character, eye.Translation);
            if (SweepAim(character, start, out Vector3D forward, out Vector3D up))
                return MatrixD.CreateWorld(start, forward, up);
            eye.Translation = start;
            return eye;
        }

        /// <summary>The same for a read of the camera matrix's address.</summary>
        public static ref MatrixD SweepCameraMatrixRef(MyCamera camera)
        {
            sweepCamera = SweepCameraMatrix(camera);
            return ref sweepCamera;
        }

        /// <summary>
        /// The sweep's direction from <paramref name="eye"/>, when use is the hand's: towards the point the hand aims at, which
        /// is where its use line (<see cref="UseLength"/> along the hand's ray) first meets anything, or that line's end. The
        /// point is within the sweep's reach (the line ends within 4.7 m of the head; the sweep's sphere reaches 5 m from the
        /// eye), so what the hand points at lies on the sweep's axis, and the game's ranking of what the sweep found, by distance
        /// from that axis, favours it as a flat player's favours what is under his crosshair. The hand's own forward from the
        /// eye would not do: that axis runs parallel to the hand's ray an arm's length aside, past the sphere's 0.5 m for what is near.
        /// </summary>
        private static bool SweepAim(MyCharacter character, Vector3D eye, out Vector3D forward, out Vector3D up)
        {
            forward = Vector3D.Zero;
            if (!TryHand(character, out Vector3D origin, out Vector3D handForward, out up))
                return false;
            Vector3D aim = origin + handForward * UseLength(character, origin, handForward);
            LineD line = new LineD(origin, aim);
            if (line.Length > 1e-3)
            {
                MyIntersectionResultLineTriangleEx? hit = MyEntities.GetIntersectionWithLine(ref line, character, null, ignoreChildren: false, ignoreFloatingObjects: false);
                if (hit.HasValue)
                    aim = hit.Value.IntersectionPointInWorldSpace;
            }
            forward = aim - eye;
            double length = forward.Length();
            // An aim point at the eye itself (a hand held to the face): the hand's forward.
            forward = length > 1e-3 ? forward / length : handForward;
            if (Math.Abs(Vector3D.Dot(forward, up)) > 0.999)
                up = Vector3D.CalculatePerpendicularVector(forward);
            return true;
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
