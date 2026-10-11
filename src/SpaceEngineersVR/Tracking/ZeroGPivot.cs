using System;
using System.Diagnostics;
using HarmonyLib;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using VRageMath;

namespace SpaceEngineersVR.Tracking
{
    /// <summary>
    /// Zero-g pitch and roll from the hands (<see cref="VRControls"/>) turn the body about the eyes, so the view turns in place
    /// by the turn asked for and is not also carried round a circle.
    /// </summary>
    /// <remarks>
    /// What the game does (decompiled): the jetpack turns the character in <c>MyCharacterJetpackComponent.MoveAndRotate</c>
    /// about the middle of its collision box, the physics position + up * <c>ModelCollision.BoundingBoxSizeHalf.Y</c>, and
    /// places it there with <c>Character.WorldMatrix</c>. The eyes are some 0.7 m above that middle, so a 30 deg pitch would also
    /// move the view about 36 cm, forward and down, in the same frame. The tracked head already sits on the turned body as it
    /// sat on the upright one (<see cref="CharacterHead"/> builds it on the body's forward and up), so only the place is wrong.
    ///
    /// Here, on a frame the hands pitched or rolled the local character: the turn the game made is kept, and the body is put
    /// where the eyes (the head bone, with the tracked head's offset from it) stay where they would have been without the
    /// turn. Yaw alone (the snap and smooth turn, the body following the head) is the game's as before: the eyes are nearly on
    /// its axis. RenderDebug ZeroGPivot=0 leaves the pivot to the game.
    /// </remarks>
    internal static class ZeroGPivot
    {
        /// <summary>RenderDebug ZeroGPivot=0: the jetpack turns the body about its middle, as the game does.</summary>
        public static bool Off { get; set; }

        private static readonly AccessTools.FieldRef<MyCharacter, int> HeadBoneIndex = AccessTools.FieldRefAccess<MyCharacter, int>("m_headBoneIndex");

        /// <summary>Where the eyes are without a head bone (metres up from the feet): the game's character model's eye height.</summary>
        private const float EyeHeight = 1.6f;

        // The turns not reported yet (Report, at most a line a second).
        private static readonly long NoteEvery = Stopwatch.Frequency;
        private static long lastNote;
        private static int heldTurns, errors;
        private static double heldMoved;

        /// <summary>The character's place before the game turned it, and its eyes in its own frame.</summary>
        internal struct Before
        {
            public bool Ours;
            public MatrixD World;
            public Vector3 Eyes;
        }

        public static void Patch(Harmony harmony)
        {
            try
            {
                harmony.Patch(AccessTools.Method(typeof(MyCharacterJetpackComponent), nameof(MyCharacterJetpackComponent.MoveAndRotate)),
                    prefix: new HarmonyMethod(typeof(ZeroGPivot), nameof(Remember)),
                    postfix: new HarmonyMethod(typeof(ZeroGPivot), nameof(Place)));
            }
            catch (Exception e)
            {
                Log.Error(e, "Zero-g pivot: not hooked; zero-g pitch and roll turn the body about its middle, as the game does");
            }
        }

        private static void Remember(MyCharacterJetpackComponent __instance, bool canRotate, out Before __state)
        {
            __state = default;
            if (Off || !canRotate || !VRControls.ZeroGTurning)
                return;
            MyCharacter character = __instance.Character;
            if (character == null || character != MySession.Static?.LocalCharacter || character.Physics == null)
                return;
            __state.Ours = true;
            __state.World = character.WorldMatrix;
            __state.Eyes = Eyes(character);
        }

        /// <summary>
        /// The game has turned and placed the body: the same turn, placed about the eyes. The game's own place is the physics
        /// position, with the turn about the middle added; without a turn it is the physics position itself.
        /// </summary>
        private static void Place(MyCharacterJetpackComponent __instance, Before __state)
        {
            if (!__state.Ours)
                return;
            try
            {
                MyCharacter character = __instance.Character;
                MatrixD turned = character.WorldMatrix;
                if (turned.Forward == __state.World.Forward && turned.Up == __state.World.Up)
                    return;
                Vector3D at = character.Physics.GetWorldMatrix().Translation
                              + Vector3D.TransformNormal(__state.Eyes, __state.World) - Vector3D.TransformNormal(__state.Eyes, turned);
                heldMoved += (at - turned.Translation).Length();
                heldTurns++;
                turned.Translation = at;
                character.WorldMatrix = turned;
                Report();
            }
            catch (Exception e)
            {
                if (errors++ < 3)
                    Log.Error(e, "Zero-g pivot: the body could not be placed about the eyes; the game's place is kept");
            }
        }

        /// <summary>The eyes in the character's own frame: the head bone, with the tracked head's offset from it when the head is the view.</summary>
        private static Vector3 Eyes(MyCharacter character)
        {
            int bone = HeadBoneIndex(character);
            Matrix[] bones = character.BoneAbsoluteTransforms;
            Vector3 eyes = bone >= 0 && bones != null && bone < bones.Length ? bones[bone].Translation : new Vector3(0f, EyeHeight, 0f);
            if (CharacterHead.IsLocalFirstPerson(character) && GameHead.Take())
                eyes += GameHead.Head.Translation;
            return eyes;
        }

        private static void Report()
        {
            long now = Stopwatch.GetTimestamp();
            if (lastNote != 0 && now - lastNote < NoteEvery)
                return;
            lastNote = now;
            Log.Info($"Zero-g pivot: the body turned about the eyes {heldTurns} time{(heldTurns == 1 ? "" : "s")}, moved {heldMoved:0.000} m in all from the game's place about its middle");
            heldTurns = 0;
            heldMoved = 0d;
        }
    }
}
