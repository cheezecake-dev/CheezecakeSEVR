using HarmonyLib;
using Sandbox.Game.Entities.Character;

namespace SpaceEngineersVR.Common
{
    /// <summary>
    /// The game's private MyCharacter fields that several tracking features read. Declared once so a game update that renames
    /// one is fixed in one place. Resolved on first use; a field the game no longer has throws from this type's initialiser.
    /// </summary>
    internal static class CharacterFields
    {
        /// <summary>Index of the head bone in the character's skeleton (MyCharacter.m_headBoneIndex).</summary>
        public static readonly AccessTools.FieldRef<MyCharacter, int> HeadBoneIndex =
            AccessTools.FieldRefAccess<MyCharacter, int>("m_headBoneIndex");
    }
}
