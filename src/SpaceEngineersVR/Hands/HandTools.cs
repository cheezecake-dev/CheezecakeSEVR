using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// The dominant hand as the game's tool, aim and pointer: the shared parts of <see cref="HeldItem"/> (tools and guns in
    /// the hand), <see cref="HandUse"/> (use and interact), <see cref="HandPlacement"/> (building) and <see cref="HandRay"/>
    /// (the visible ray). All of it needs a live hand source and a tracked dominant hand, and only the local character
    /// takes part; with either missing the game is untouched (its head ray, its own item position).
    /// </summary>
    internal static class HandTools
    {
        /// <summary>Debug: all hand tools off, back to the head ray and the game's item position (<see cref="Rendering.RenderDebug"/> HandTools=0).</summary>
        public static bool Off { get; set; }

        /// <summary>
        /// How far behind the ray's start the game starts its use ray: it casts from the head matrix's translation minus
        /// 0.3 m along its forward (MyCharacterRaycastDetectorComponent.DoDetection). <see cref="HeadLike"/> puts that
        /// much in front, so the use ray starts at the hand.
        /// </summary>
        public const double GameUseRayBehind = 0.3;

        /// <summary>
        /// Each hook on its own, like the hand input's: a hook that cannot be made leaves the rest working, and the
        /// rendering patches are never taken down by it.
        /// </summary>
        public static void Patch(Harmony harmony)
        {
            Hook("Tools and guns in the hand", () => HeldItem.Patch(harmony));
            Hook("Use from the hand", () => HandUse.Patch(harmony));
            Hook("Building from the hand", () => HandPlacement.Patch(harmony));
            Hook("Hand ray", () => HandRay.Patch(harmony));
        }

        private static void Hook(string what, Action patch)
        {
            try
            {
                patch();
            }
            catch (Exception e)
            {
                Log.Error(e, what + " could not be hooked; it will not reach the game");
            }
        }

        /// <summary>
        /// The dominant hand's grip and aim poses in the world, when hand tools are on and it is tracked. Placed by this
        /// frame's head (<see cref="HandWorld.TryGetNow"/>), not the previous draw's camera: the tool, the use ray and the
        /// builder ray are worked out in the simulation, and a body in motion would leave them a frame behind the hand.
        /// With the held tool in both hands the pose is turned to point along the line between them (<see cref="TwoHand"/>),
        /// so the tool, its sensors, the shot and the use ray follow both hands.
        /// </summary>
        public static bool TryGetHand(out MatrixD grip, out MatrixD aim)
        {
            grip = aim = MatrixD.Identity;
            MyCharacter character = MySession.Static?.LocalCharacter;
            if (Off || !HandWorld.TryGetNow(character, VRSettings.DominantHand, out grip, out aim, out _))
                return false;
            Tracking.HandResidual.NoteTool(grip);
            TwoHand.Apply(character, ref grip, ref aim);
            return true;
        }

        /// <summary>The dominant hand's pointing ray in the world: where it is, and the unit direction it points.</summary>
        public static bool TryGetRay(out Vector3D origin, out Vector3D direction)
        {
            origin = direction = Vector3D.Zero;
            if (!TryGetHand(out _, out MatrixD aim))
                return false;
            return Ray(aim, out origin, out direction);
        }

        /// <summary>The hand's aim pose as a ray (the pose points along -Z).</summary>
        internal static bool Ray(in MatrixD aim, out Vector3D origin, out Vector3D direction)
        {
            origin = aim.Translation;
            direction = aim.Forward;
            double length = direction.Length();
            if (!(length > 1e-6))
                return false;
            direction /= length;
            return true;
        }

        /// <summary>This is the player's own character, which the player controls directly (not through a seat or a remote).</summary>
        public static bool IsLocalOnFoot(MyCharacter character) =>
            character != null && MySession.Static != null && character == MySession.Static.LocalCharacter
            && ReferenceEquals(MySession.Static.ControlledEntity, character) && !character.IsDead;

        /// <summary>
        /// A matrix that stands in for the character's head matrix where the game builds a ray from it: forward is the
        /// hand's, and the game's "translation minus 0.3 m along forward" start is the hand's own position.
        /// </summary>
        internal static MatrixD HeadLike(Vector3D origin, Vector3D forward, Vector3D up) =>
            MatrixD.CreateWorld(origin + forward * GameUseRayBehind, forward, up);

        /// <summary>A stand-in for the camera's world matrix: at the hand, facing where it points.</summary>
        internal static MatrixD CameraLike(Vector3D origin, Vector3D forward, Vector3D up) =>
            MatrixD.CreateWorld(origin, forward, up);

        /// <summary>
        /// Rewrites the calls in a method that match <paramref name="match"/> into calls of <paramref name="replacement"/>,
        /// a static method that takes the receiver first and then the arguments, so the stack stays as it was. Nothing
        /// else in the method changes. Fewer than <paramref name="atLeast"/> matches throw, so a game update that moves
        /// things shows as a failed hook in the log rather than a hook that quietly does nothing.
        /// </summary>
        internal static IEnumerable<CodeInstruction> Redirect(IEnumerable<CodeInstruction> instructions, Func<MethodBase, bool> match,
            MethodInfo replacement, int atLeast, ref int count)
        {
            var result = new List<CodeInstruction>();
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                    && instruction.operand is MethodBase method && match(method))
                {
                    var changed = new CodeInstruction(OpCodes.Call, replacement);
                    changed.labels.AddRange(instruction.labels);
                    changed.blocks.AddRange(instruction.blocks);
                    result.Add(changed);
                    replaced++;
                    continue;
                }
                result.Add(instruction);
            }
            if (replaced < atLeast)
                throw new InvalidOperationException($"Expected at least {atLeast} call(s) to replace with {replacement.Name}, found {replaced}");
            count += replaced;
            return result;
        }

        /// <summary>
        /// Rewrites reads of an instance field into calls: a read of the value (<c>ldfld</c>) into <paramref name="byValue"/>
        /// and a read of its address (<c>ldflda</c>, as a struct's property is read) into <paramref name="byRef"/>, static
        /// methods that take the owner and return the value or a reference to one. Throws when there is none to rewrite.
        /// </summary>
        internal static IEnumerable<CodeInstruction> RedirectField(IEnumerable<CodeInstruction> instructions, FieldInfo field,
            MethodInfo byValue, MethodInfo byRef, int atLeast, ref int count)
        {
            var result = new List<CodeInstruction>();
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                MethodInfo replacement = instruction.operand is FieldInfo operand && operand == field
                    ? instruction.opcode == OpCodes.Ldfld ? byValue : instruction.opcode == OpCodes.Ldflda ? byRef : null
                    : null;
                if (replacement != null)
                {
                    var changed = new CodeInstruction(OpCodes.Call, replacement);
                    changed.labels.AddRange(instruction.labels);
                    changed.blocks.AddRange(instruction.blocks);
                    result.Add(changed);
                    replaced++;
                    continue;
                }
                result.Add(instruction);
            }
            if (replaced < atLeast)
                throw new InvalidOperationException($"Expected at least {atLeast} read(s) of {field.Name} to replace, found {replaced}");
            count += replaced;
            return result;
        }

        /// <summary>MyCharacter.GetHeadMatrix(includeY, includeX, forceHeadAnim, forceHeadBone, preferLocalOverSync).</summary>
        internal static bool IsCharacterHeadMatrix(MethodBase method) =>
            method.DeclaringType == typeof(MyCharacter) && method.Name == nameof(MyCharacter.GetHeadMatrix) && method.GetParameters().Length == 5;
    }
}
