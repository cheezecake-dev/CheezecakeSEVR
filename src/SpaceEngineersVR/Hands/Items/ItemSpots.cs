using Sandbox.Definitions;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// Where each hand goes on the held item: the palm bone spots of its hand item definition (RightHand for the hand that
    /// holds it, LeftHand for the other's grip; HandItems.sbc), in the item's own space. Right-handed they are the game's
    /// own. Left-handed the item is in the left hand, so the spots are mirrored across the item's X, through the middle of
    /// its main grip (<see cref="MirrorX"/>), and swap hands: the left palm goes where the right palm goes, mirrored, and the
    /// right palm where the left one goes. The item itself is not mirrored. The skeleton's left hand is its right one
    /// mirrored with the palm bone's Z (toward the thumb) reversed (<see cref="Tracking.HandGrip"/>), so a mirrored spot is
    /// a palm bone's frame again.
    /// </summary>
    internal static class ItemSpots
    {
        /// <summary>The hand the held item is in: the dominant one.</summary>
        public static Hand ToolHand => VRSettings.DominantHand;

        /// <summary>The spots are the game's mirrored: the item is in the left hand.</summary>
        public static bool Mirrored => ToolHand == Hand.Left;

        /// <summary>
        /// The triggers follow the hand holding the item: left-handed, with a gun or a tool in the player's hand on foot or the
        /// jetpack, the left trigger is primary and the right one secondary. Building (a block, a paste, the voxel hand, the
        /// colour tool) keeps the right trigger placing and the left removing.
        /// </summary>
        public static bool TriggersSwapped
        {
            get
            {
                if (!Mirrored || BuildMode.Active || PaintMode.Active)
                    return false;
                MyCharacter character = MySession.Static?.LocalCharacter;
                return HandTools.IsLocalOnFoot(character) && character.CurrentWeapon != null && !(character.CurrentWeapon is MyBlockPlacerBase);
            }
        }

        /// <summary>The tool hand's palm bone spot on the item (the game's RightHand, mirrored left-handed).</summary>
        public static Matrix Tool(MyHandItemDefinition item) => Mirrored ? Mirror(item.RightHand, MirrorX(item)) : item.RightHand;

        /// <summary>The other hand's palm bone spot on the item, its second grip (the game's LeftHand, mirrored left-handed).</summary>
        public static Matrix Other(MyHandItemDefinition item) => Mirrored ? Mirror(item.LeftHand, MirrorX(item)) : item.LeftHand;

        /// <summary>Where the tool hand closes on the item (<see cref="HeldItem.GraspInItem"/>): where its controller's grip goes.</summary>
        public static Vector3 ToolGrasp(MyHandItemDefinition item) => HeldItem.GraspInItem(Tool(item), ToolHand == Hand.Left);

        /// <summary>Where the other hand closes on the item's second grip.</summary>
        public static Vector3 OtherGrasp(MyHandItemDefinition item) => HeldItem.GraspInItem(Other(item), ToolHand != Hand.Left);

        /// <summary>
        /// Where the mirror plane crosses the item's X: the middle of its main grip, where the game's right hand closes, so
        /// the left hand closes on the same part. An item's own X = 0 is not always that: the angle grinder's main grip is
        /// 9 cm to the side of it, and mirrored through X = 0 the left hand would close beside the grinder, not on it.
        /// </summary>
        private static float MirrorX(MyHandItemDefinition item) => HeldItem.GraspInItem(item.RightHand, false).X;

        /// <summary>
        /// A palm bone's frame in the item's space, mirrored through the plane X = <paramref name="planeX"/> into the other
        /// hand's: every row's X reversed and the position's X reflected, then the bone's Z row reversed, as the other hand's
        /// bone has it (row vectors).
        /// </summary>
        internal static Matrix Mirror(in Matrix palm, float planeX)
        {
            Matrix m = palm;
            m.M11 = -palm.M11;
            m.M21 = -palm.M21;
            m.M41 = 2f * planeX - palm.M41;
            m.M32 = -palm.M32;
            m.M33 = -palm.M33;
            return m;
        }
    }
}
