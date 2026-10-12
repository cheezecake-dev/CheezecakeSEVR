using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage.Game.Utils;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// Building and pasting from the hand's ray instead of the head's. Everything the block builder and the clipboard
    /// place, show and test comes from one ray, MyBlockBuilderBase.PlacementProvider's RayStart and RayDirection (the
    /// head's position and forward), so the two getters of the one provider the game has (MyDefaultPlacementProvider)
    /// answer with the hand's ray instead: what the block ghost sits on, the block's reach, the grid it points at, and
    /// the survival "close enough" test all follow the hand without any of the builder's own code changing. Beside it the
    /// builder still reads the main camera in a few places that are the same ray in other words; those read the hand too.
    /// </summary>
    /// <remarks>
    /// The provider is the game's own and stays in place (the game puts a new one back in whenever it deactivates the
    /// builder, so assigning one of our own would be undone). The one read of the camera left as it was is the builder's
    /// test that the block does not sit inside the player's head (MyCubeBuilder.IntersectsCharacterOrCamera): that is
    /// about the head, not the ray. Seated in a ship or a remote the ray stays the game's.
    /// </remarks>
    internal static class HandPlacement
    {
        public static bool Hooked { get; private set; }

        private static int errors;
        private static Harmony pending;

        /// <summary>
        /// Called with the other hooks while the game is still starting. Nothing is patched yet: Harmony compiles a patched
        /// method on the spot, which runs the static constructor of its class, and MyCubeBuilder's reads a definition id
        /// that does not exist until the game has registered its object builders, a little after this (MySandboxGame's
        /// constructor: InitializeRender, then RegisterAssemblies). A class whose constructor throws stays broken for the
        /// whole run, so the builder is patched from <see cref="Hook"/>, on the first frame of a session.
        /// </summary>
        public static void Patch(Harmony harmony)
        {
            pending = harmony;
            Log.Info("Hand tools: building from the hand will be hooked on the first frame of a session");
        }

        /// <summary>Once, on the game thread, from the first frame the gameplay screen draws: by then the game has registered everything.</summary>
        public static void Hook()
        {
            Harmony harmony = pending;
            if (harmony == null)
                return;
            pending = null;
            // Through the registry: a hook that fails takes the ones before it off again, so the ray is all the hand's or all the head's.
            PatchRegistry.Apply(nameof(HandPlacement), () => HookNow(harmony),
                "Building from the hand could not be hooked; the builder keeps the head's ray");
        }

        internal static void HookNow(Harmony harmony)
        {
            // The ray itself.
            harmony.Patch(AccessTools.PropertyGetter(typeof(MyDefaultPlacementProvider), nameof(MyDefaultPlacementProvider.RayStart)),
                postfix: new HarmonyMethod(typeof(HandPlacement), nameof(AfterRayStart)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(MyDefaultPlacementProvider), nameof(MyDefaultPlacementProvider.RayDirection)),
                postfix: new HarmonyMethod(typeof(HandPlacement), nameof(AfterRayDirection)));

            // The builder's own reads of the camera that are the ray again (see the transpilers below).
            harmony.Patch(AccessTools.Method(typeof(MyCubeBuilder), nameof(MyCubeBuilder.Draw)),
                transpiler: new HarmonyMethod(typeof(HandPlacement), nameof(TranspileBuilderDraw)));
            harmony.Patch(AccessTools.Method(typeof(MyCubeBuilder), nameof(MyCubeBuilder.AlignToGravity)),
                transpiler: new HarmonyMethod(typeof(HandPlacement), nameof(TranspileAlignToGravity)));
            harmony.Patch(AccessTools.Method(typeof(MyCubeBuilderGizmo), nameof(MyCubeBuilderGizmo.DefaultGizmoCloseEnough)),
                transpiler: new HarmonyMethod(typeof(HandPlacement), nameof(TranspileCloseEnough)));
            int redirected = builderDrawReads + alignReads + closeEnoughReads;

            // The clipboard's paste ray (a protected static of MyGridClipboard).
            MethodInfo pasteMatrix = AccessTools.DeclaredMethod(typeof(MyGridClipboard), "GetPasteMatrix", Type.EmptyTypes)
                                     ?? throw new MissingMemberException(nameof(MyGridClipboard), "GetPasteMatrix");
            harmony.Patch(pasteMatrix, postfix: new HarmonyMethod(typeof(HandPlacement), nameof(AfterPasteMatrix)));

            Hooked = true;
            BuildMode.HookBuilder(harmony);
            Log.Info($"Hand tools: building from the hand hooked (placement ray, paste ray, {redirected} camera reads rewritten)");
        }

        private static int builderDrawReads, alignReads, closeEnoughReads;

        private static IEnumerable<CodeInstruction> TranspileBuilderDraw(IEnumerable<CodeInstruction> instructions)
        {
            int count = 0;
            IEnumerable<CodeInstruction> result = HandTools.Redirect(instructions, IsCameraPosition, AccessTools.Method(typeof(HandPlacement), nameof(CameraPosition)), 1, ref count);
            builderDrawReads = count;
            return result;
        }

        private static IEnumerable<CodeInstruction> TranspileAlignToGravity(IEnumerable<CodeInstruction> instructions)
        {
            int count = 0;
            IEnumerable<CodeInstruction> result = HandTools.Redirect(instructions, IsCameraForward, AccessTools.Method(typeof(HandPlacement), nameof(CameraForward)), 1, ref count);
            alignReads = count;
            return result;
        }

        private static IEnumerable<CodeInstruction> TranspileCloseEnough(IEnumerable<CodeInstruction> instructions)
        {
            int count = 0;
            IEnumerable<CodeInstruction> result = HandTools.Redirect(instructions, IsCameraPosition, AccessTools.Method(typeof(HandPlacement), nameof(CameraPosition)), 2, ref count);
            result = HandTools.Redirect(result, IsCameraForward, AccessTools.Method(typeof(HandPlacement), nameof(CameraForward)), 1, ref count);
            closeEnoughReads = count;
            return result;
        }

        private static bool IsCameraPosition(MethodBase method) =>
            method.DeclaringType == typeof(MyCamera) && method.Name == "get_" + nameof(MyCamera.Position);

        private static bool IsCameraForward(MethodBase method) =>
            method.DeclaringType == typeof(MyCamera) && method.Name == "get_" + nameof(MyCamera.ForwardVector);

        /// <summary>Replaces <c>MySector.MainCamera.Position</c> where the builder means the start of its ray.</summary>
        public static Vector3D CameraPosition(MyCamera camera) =>
            TryRay(out Vector3D origin, out _) ? origin : camera.Position;

        /// <summary>Replaces <c>MySector.MainCamera.ForwardVector</c> where the builder means the direction of its ray.</summary>
        public static Vector3 CameraForward(MyCamera camera) =>
            TryRay(out _, out Vector3D direction) ? (Vector3)direction : camera.ForwardVector;

        private static void AfterRayStart(ref Vector3D __result)
        {
            if (TryRay(out Vector3D origin, out _))
                __result = origin;
        }

        private static void AfterRayDirection(ref Vector3D __result)
        {
            if (TryRay(out _, out Vector3D direction))
                __result = direction;
        }

        /// <summary>
        /// Postfix of <c>MyGridClipboard.GetPasteMatrix()</c>: a pose at the hand, facing where it points (the game's is the
        /// head's). While the left stick click turns the paste, the ray it had when the click went down (<see cref="BuildMode"/>).
        /// </summary>
        private static void AfterPasteMatrix(ref MatrixD __result)
        {
            try
            {
                Sandbox.Game.Entities.Character.MyCharacter character = MySession.Static?.LocalCharacter;
                if (!HandTools.IsLocalOnFoot(character))
                    return;
                if (BuildMode.TryFrozenRay(character, out Vector3D heldOrigin, out Vector3D heldForward))
                {
                    Vector3D up = Math.Abs(Vector3D.Dot(heldForward, character.WorldMatrix.Up)) > 0.99 ? character.WorldMatrix.Backward : character.WorldMatrix.Up;
                    __result = HandTools.CameraLike(heldOrigin, heldForward, up);
                    return;
                }
                if (!HandTools.TryGetHand(out _, out MatrixD aim) || !HandTools.ReachRay(character, aim, out Vector3D origin, out Vector3D forward))
                    return;
                __result = HandTools.CameraLike(origin, forward, aim.Up);
            }
            catch (Exception e)
            {
                if (errors++ < 3)
                    Log.Error(e, "Hand tools could not take the paste ray from the hand");
            }
        }

        /// <summary>The hand's ray for the builder: only for the local character on foot, with the dominant hand tracked.</summary>
        internal static bool TryRay(out Vector3D origin, out Vector3D direction)
        {
            origin = direction = Vector3D.Zero;
            try
            {
                Sandbox.Game.Entities.Character.MyCharacter character = MySession.Static?.LocalCharacter;
                if (!HandTools.IsLocalOnFoot(character))
                    return false;
                // While the left stick click turns the block the ray stays where it was when the click went down (BuildMode).
                return BuildMode.TryFrozenRay(character, out origin, out direction) || HandTools.TryGetReachRay(out origin, out direction);
            }
            catch (Exception e)
            {
                if (errors++ < 3)
                    Log.Error(e, "Hand tools could not take the building ray from the hand");
                return false;
            }
        }
    }
}
