// SuitAir: how low the air in the suit is, for what the player feels and hears of it.
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.World;

namespace SpaceEngineersVR.Common
{
    /// <summary>
    /// The air the local character breathes from its suit, as the game's own HUD warns of it: oxygen on in the world, the helmet
    /// closed, the air outside too thin to breathe (below the character's PressureLevelForLowDamage), and the suit's oxygen at or
    /// under the game's low mark (MyCharacterOxygenComponent.LOW_OXYGEN_RATIO). For the heartbeat in the hands
    /// (<see cref="Hands.Haptics"/>) and the breathing in the helmet (<see cref="Rendering.HelmetBreath"/>), so both tell the same
    /// thing the game's HUD does, no more.
    /// </summary>
    internal static class SuitAir
    {
        /// <summary>
        /// How short of air <paramref name="character"/> is, 0 (none short, or not breathing from the suit) to 1 (the suit empty),
        /// rising from the game's low mark; and whether the suit is empty.
        /// </summary>
        public static float Shortness(MyCharacter character, out bool empty)
        {
            empty = false;
            MyCharacterOxygenComponent oxygen = character?.OxygenComponent;
            if (oxygen == null || MySession.Static?.Settings == null || !MySession.Static.Settings.EnableOxygen || !oxygen.HelmetEnabled
                || character.EnvironmentOxygenLevel >= character.Definition.PressureLevelForLowDamage)
                return 0f;
            float low = MyCharacterOxygenComponent.LOW_OXYGEN_RATIO;
            if (oxygen.SuitOxygenLevel > low)
                return 0f;
            empty = oxygen.SuitOxygenLevel <= 0f;
            return System.Math.Max(0.01f, 1f - oxygen.SuitOxygenLevel / low);
        }
    }
}
