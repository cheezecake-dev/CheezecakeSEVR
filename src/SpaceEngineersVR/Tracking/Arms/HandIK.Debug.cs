using System;
using Sandbox.Game.Entities.Character;
using VRageMath;
using VRageRender.Animations;

namespace SpaceEngineersVR.Tracking
{
    // HandIK's RenderDebug switches: SetDebug and ResetDebug.
    internal static partial class HandIK
    {
        /// <summary>RenderDebug: a "HandIK..." key. False if it is not one of these.</summary>
        public static bool SetDebug(string name, int value)
        {
            switch (name.ToLowerInvariant())
            {
                case "handik":
                    if (value < 0 || value > 2)
                    {
                        // Past the player's modes: a test build's (the game's own solver), else not a switch.
                        bool taken = false;
                        TestAidsMode(value, ref taken);
                        return taken;
                    }
                    Mode = value;
                    return true;
                // The grip fit: of the controllers in use, kept and saved (GripFit); ResetDebug leaves it, HandIKFitReset=1 clears it.
                case "handikpitch": GripFit.Set(GripFit.Axis.Pitch, value); return true;
                case "handikroll": GripFit.Set(GripFit.Axis.Roll, value); return true;
                case "handikyaw": GripFit.Set(GripFit.Axis.Yaw, value); return true;
                case "handikx": GripFit.Set(GripFit.Axis.X, value); return true;
                case "handiky": GripFit.Set(GripFit.Axis.Y, value); return true;
                case "handikz": GripFit.Set(GripFit.Axis.Z, value); return true;
                case "handikfitreset": if (value != 0) GripFit.Reset(); return true;
                case "handikstretch": settings.Stretch = value / 100f; return true;
                case "handikshrug": settings.Shrug = value; return true;
                case "handiktwist": settings.TwistShare = value / 100f; return true;
                case "handikswing": settings.SwivelStep = value; return true;
                case "handikreach": reachLog = value != 0; return true;
                case "handiksway": steadyShoulder = value != 0; return true;
                case "handikease": easeLog = value != 0; return true;
                case "handikcap": settings.Cap = value / 100f; return true;
                case "handikjump": ArmJump.Cm = Math.Max(0, value); return true;
                default: return false;
            }
        }

        /// <summary>A test build's HandIK=n for n past the player's modes (<c>taken</c> set when it is one).</summary>
        static partial void TestAidsMode(int value, ref bool taken);

        /// <summary>A test build's solver choice is back to the player's.</summary>
        static partial void TestAidsReset();

        /// <summary>Whether a test build has the game's own solver (instead of ours) on the arms; false in a player's build.</summary>
        private static bool TestAidsGameSolver()
        {
            bool on = false;
            TestAidsGameSolver(ref on);
            return on;
        }

        static partial void TestAidsGameSolver(ref bool on);

        /// <summary>A test build's game solver putting the arm's hand on <c>target</c>.</summary>
        static partial void TestAidsSolveGame(MyCharacterBone[] bones, ArmSolver.Chain chain, Matrix target);

        public static void ResetDebug()
        {
            Mode = 1;
            TestAidsReset();
            settings = ArmSolver.Settings.Default;
            reachLog = false;
            steadyShoulder = SteadyByDefault;
            easeLog = false;
            ArmJump.Cm = 0;
        }
    }
}
