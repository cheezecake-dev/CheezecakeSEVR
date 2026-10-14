// HelmetState: the local character's helmet, watched on the game thread, for the sounds of opening and closing it and of breathing inside it, and its turn.
using System;
using HarmonyLib;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// Watches the local character's helmet (MyCharacterOxygenComponent.HelmetEnabled: true closed) once a frame the game draws, on
    /// the game thread, and tells the helmet's sounds when it really opens or closes (<see cref="HelmetAir"/>) and, every frame, how it
    /// is (<see cref="HelmetBreath"/>). A new character starts with the sun visor up, and opening the helmet raises it with the glass
    /// (<see cref="SunVisor"/>). It also has the visor's glare look for the sun (<see cref="VisorGlare.Sample"/>), and the
    /// helmet follow the head round (<see cref="HelmetTurn.Update"/>; a new character's starts in its place), and the helmet's inside
    /// in the scene get its picture to the renderer (<see cref="HelmetInterior.Update"/>).
    /// <para>
    /// By watching the state, not a method that changes it, every way the helmet changes is heard: the key, the wrist panel, the
    /// actions wheel, the grip on the helmet, a seat, and on a multiplayer client the server's change arriving through the game's
    /// sync (where the client's own SwitchHelmet does not change it). A new character (a respawn, a world loading) takes the helmet
    /// as it is, with no sound.
    /// </para>
    /// </summary>
    internal static class HelmetState
    {
        private static readonly FeatureGuard guard = new FeatureGuard(nameof(HelmetState));

        // Game thread: the character watched and its helmet when last seen.
        private static MyCharacter seen;
        private static bool wasClosed;

        /// <summary>The local character's helmet is closed, its glass down, as last watched. Game thread.</summary>
        public static bool Closed => seen != null && wasClosed;

        public static void Patch(Harmony harmony)
        {
            // The gameplay screen's draw (only when there is a scene), once a frame the game draws, on the game thread.
            harmony.Patch(AccessTools.Method(typeof(MySession), nameof(MySession.DrawSync)),
                postfix: new HarmonyMethod(typeof(HelmetState), nameof(AfterDraw)));
        }

        private static void AfterDraw()
        {
            if (!guard.On)
                return;
            try
            {
                Watch();
            }
            catch (Exception e)
            {
                guard.Failed(e, "Helmet state failed");
            }
        }

        /// <summary>Game thread: the helmet as it is now, against how it was; a change of the same character is a switch.</summary>
        private static void Watch()
        {
            MyCharacter character = MySession.Static?.LocalCharacter;
            bool alive = character != null && !character.IsDead && !character.Closed && character.OxygenComponent != null;
            bool closed = alive && character.OxygenComponent.HelmetEnabled;
            if (!alive || !ReferenceEquals(character, seen))
            {
                if (!ReferenceEquals(character, seen))
                {
                    SunVisor.Reset();
                    HelmetTurn.Reset();
                }
                seen = alive ? character : null;
                wasClosed = closed;
            }
            else if (closed != wasClosed)
            {
                wasClosed = closed;
                HelmetAir.Switched(character, opened: !closed);
                // The sun visor rides on the glass: it goes up with it, and stays up until it is drawn down again.
                if (!closed)
                    SunVisor.Move(false);
            }
            HelmetBreath.Update(alive ? character : null);
            VisorGlare.Sample();
            HelmetTurn.Update();
            HelmetInterior.Update();
        }
    }
}
