using System;

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
                case "handik": Mode = value; return true;
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
                case "handikframe": HandWorld.PreviousCamera = value == 0; return true;
                case "handiklead": OpenXR.XrInput.Lead = value != 0; return true;
                case "handikheld": solveHeld = value != 0; return true;
                case "handikreach": reachLog = value != 0; return true;
                case "handikpin": pinOnTool = value != 0; return true;
                case "handiksway": steadyShoulder = value != 0; return true;
                case "handikease": easeLog = value != 0; return true;
                case "handikcap": settings.Cap = value / 100f; return true;
                case "handikjump": ArmJump.Cm = Math.Max(0, value); return true;
                default: return false;
            }
        }

        public static void ResetDebug()
        {
            Mode = 1;
            settings = ArmSolver.Settings.Default;
            solveHeld = true;
            reachLog = false;
            pinOnTool = true;
            steadyShoulder = SteadyByDefault;
            easeLog = false;
            ArmJump.Cm = 0;
            HandWorld.PreviousCamera = false;
            OpenXR.XrInput.Lead = true;
        }
    }
}
