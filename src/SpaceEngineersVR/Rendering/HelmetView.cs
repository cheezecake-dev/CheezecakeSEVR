// HelmetView: the view from inside the helmet: its inside, its visor's glass and the HUD on it; the helmet's model is left hidden, as the game has it.
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SpaceEngineersVR.Tracking;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The helmet view (Options > VR > Display > Helmet, <see cref="VRSettings.HelmetView"/>): the eyes look out from inside the
    /// astronaut's helmet, on foot and in a seat. The helmet's inside round the view at a helmet's depth
    /// (<see cref="HelmetInterior"/>), the visor's glass filtering the light seen through it (<see cref="VisorGlass"/>) and
    /// swinging up as the helmet opens, the HUD projected onto the glass (<see cref="HudVisor"/>), the glass and HUD gone while the
    /// helmet is open (<see cref="HelmetHud"/>), and the air as it opens and closes (<see cref="HelmetAir"/>). On foot the helmet
    /// also turns with the head up to the neck's limits (<see cref="HelmetHead"/>) and is where the walls
    /// (<see cref="HelmetWalls"/>) and the hands (<see cref="Hands.HelmetStop"/>) stop.
    /// RenderDebug HelmetView=0 turns the whole helmet view off.
    /// </summary>
    /// <remarks>
    /// The helmet's model is not drawn round the eyes (<see cref="HelmetInterior"/> draws the inside instead): in first person the game hides the head's materials (the definition's
    /// MaterialsDisabledIn1st, "Head", the helmet's shell and visor, and "Spacesuit_hood" with the face and the lamps:
    /// MyCharacter.EnableHead, from MyCharacter and MyCockpit as the view switches), and they stay hidden. Drawn again from
    /// inside, the visor's faces point outward and are culled, so only the shell's edge showed round a view seen through
    /// the rest of it.
    /// </remarks>
    internal static class HelmetView
    {
        /// <summary>Debug: no helmet view at all, whatever the setting (<see cref="RenderDebug"/> HelmetView=0): no visor, no head turn, the HUD as without.</summary>
        public static bool Off { get; set; }

        /// <summary>The helmet view is on: the setting, and not switched off for debugging. Any thread.</summary>
        public static bool On => VRSettings.HelmetView && !Off;

        /// <summary>
        /// Game thread: the helmet is on this character's eyes on foot now, for <see cref="Tracking.HelmetHead"/> to turn it with the
        /// head and for the walls and the hands to stop on it: the helmet view on, the local character, alive, on foot, seen from
        /// its own eyes, with the head hidden as the game hides it in first person.
        /// </summary>
        internal static bool Worn(MyCharacter character)
        {
            return On && character == MySession.Static?.LocalCharacter && !character.IsDead
                   && CharacterHead.IsLocalFirstPerson(character) && !character.HeadRenderingEnabled;
        }
    }
}
