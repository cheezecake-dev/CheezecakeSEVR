using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.World;
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
            int count = 0;
            Type closest = AccessTools.TypeByName("Sandbox.Game.Entities.Character.Components.MyCharacterClosestDetectorComponent")
                           ?? throw new MissingMemberException("MyCharacterClosestDetectorComponent");
            foreach (Type detector in new[] { typeof(MyCharacterRaycastDetectorComponent), closest })
            {
                MethodInfo doDetection = AccessTools.DeclaredMethod(detector, "DoDetection", new[] { typeof(bool) })
                                         ?? throw new MissingMemberException(detector.Name, "DoDetection");
                harmony.Patch(doDetection, transpiler: new HarmonyMethod(typeof(HandUse), nameof(TranspileDetection)));
                count++;
            }
            Hooked = true;
            Log.Info($"Hand tools: use from the hand hooked ({count} detectors; head reads rewritten: {headReads}, camera matrix reads: {cameraReads})");
        }

        private static int headReads, cameraReads;

        private static IEnumerable<CodeInstruction> TranspileDetection(IEnumerable<CodeInstruction> instructions)
        {
            int heads = 0, cameras = 0;
            IEnumerable<CodeInstruction> result = HandTools.Redirect(instructions, HandTools.IsCharacterHeadMatrix,
                AccessTools.Method(typeof(HandUse), nameof(HeadMatrix)), 1, ref heads);
            result = HandTools.RedirectField(result, AccessTools.Field(typeof(MyCamera), nameof(MyCamera.WorldMatrix)),
                AccessTools.Method(typeof(HandUse), nameof(CameraMatrix)), AccessTools.Method(typeof(HandUse), nameof(CameraMatrixRef)), 1, ref cameras);
            headReads += heads;
            cameraReads += cameras;
            return result;
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
                // The detectors run for the controlled character only (UpdateAfterSimulation10 returns otherwise).
                if (!HandTools.IsLocalOnFoot(character) || !HandTools.TryGetHand(out _, out MatrixD aim) || !HandTools.Ray(aim, out origin, out forward))
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
