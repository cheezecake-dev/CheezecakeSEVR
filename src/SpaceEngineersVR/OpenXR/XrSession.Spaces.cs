// XrSession, reference spaces: the STAGE space and the floor height taken from it.

using System;
using System.Diagnostics;

namespace SpaceEngineersVR.OpenXR
{
    internal static unsafe partial class XrSession
    {
        /// <summary>
        /// The STAGE space, whose origin is on the floor, when the runtime offers one: the floor for height calibration.
        /// Never throws; without it the floor is simply not known.
        /// </summary>
        private static void CreateStageSpace()
        {
            stageSpace = 0;
            try
            {
                Xr.Check(Xr.xrEnumerateReferenceSpaces(session, 0, out uint count, null), "xrEnumerateReferenceSpaces", XrRuntime.Instance);
                int[] types = new int[count];
                if (count > 0)
                    fixed (int* ptr = types)
                        Xr.Check(Xr.xrEnumerateReferenceSpaces(session, count, out count, ptr), "xrEnumerateReferenceSpaces", XrRuntime.Instance);
                if (Array.IndexOf(types, (int)XrReferenceSpaceType.Stage) < 0)
                {
                    Log.Info("OpenXR STAGE space: not offered by the runtime; the floor is not known, so height cannot be calibrated " +
                             $"(it offers {string.Join(", ", types)})");
                    return;
                }
                var info = new XrReferenceSpaceCreateInfo
                {
                    Type = XrStructureType.ReferenceSpaceCreateInfo,
                    ReferenceSpaceType = XrReferenceSpaceType.Stage,
                    PoseInReferenceSpace = XrPosef.Identity,
                };
                Xr.Check(Xr.xrCreateReferenceSpace(session, ref info, out stageSpace), "xrCreateReferenceSpace (STAGE)", XrRuntime.Instance);
                Log.Info("OpenXR STAGE space: available; its origin is the floor for height calibration");
            }
            catch (Exception e)
            {
                stageSpace = 0;
                Log.Error(e, "OpenXR STAGE space could not be set up; the floor is not known");
            }
        }

        /// <summary>
        /// Where the LOCAL space's origin is above the floor, at the display time the eyes were located for. A failure to
        /// locate it makes the floor unknown, never the headset fail.
        /// </summary>
        private static void LocateFloor()
        {
            if (stageSpace == 0)
                return;
            var location = new XrSpaceLocation { Type = XrStructureType.SpaceLocation };
            int result = Xr.xrLocateSpace(space, stageSpace, frame.PredictedDisplayTime, ref location);
            long now = Stopwatch.GetTimestamp();
            if (result >= 0 && (location.LocationFlags & XrConst.SpaceLocationPositionValid) != 0)
            {
                // LOCAL's position in STAGE: STAGE's up is the vertical and its origin is on the floor, so Y is the height.
                OriginAboveFloor = location.Pose.Position.Y;
                floorSeenAt = now;
                if (!floorLogged)
                {
                    floorLogged = true;
                    Log.Info($"OpenXR floor located: the LOCAL origin is {location.Pose.Position.Y:0.00} m above it");
                }
            }
            else if (OriginAboveFloor.HasValue && now - floorSeenAt > Stopwatch.Frequency)
            {
                OriginAboveFloor = null;
                floorLogged = false;
                Log.Warn($"OpenXR floor lost (xrLocateSpace {result}, flags {location.LocationFlags:X}); height cannot be corrected until it is back");
            }
        }
    }
}
