// ExternalView, game thread: moving the headset screen by hand and remembering where it was put.

using System;
using System.Globalization;
using System.Linq;
using SpaceEngineersVR.Common;
using VRageMath;

namespace SpaceEngineersVR.Rendering
{
    internal static partial class ExternalView
    {
        private static readonly object screenGate = new object();
        private static bool shown, placeAgain;
        private static Matrix shownPose, shownAnchor, anchor;
        private static Matrix? movedTo;

        /// <summary>Any thread: the screen is in the headset now, and where (in the tracking space, facing +z), with its width and height in metres.</summary>
        public static bool TryGetScreen(out Matrix pose, out float width, out float height)
        {
            width = VRSettings.ExternalViewSize;
            height = width * TextureHeight / TextureWidth;
            lock (screenGate)
            {
                pose = shownPose;
                return shown;
            }
        }

        /// <summary>Game thread, while a hand holds the screen: it stands here from the next headset frame.</summary>
        public static void MoveScreen(in Matrix pose)
        {
            lock (screenGate)
                movedTo = pose;
        }

        /// <summary>
        /// Game thread, as the hand lets go at <paramref name="pose"/>: keeps where it stands, from the head's heading when the
        /// screen appeared, in the settings file, so it appears there again (turned off and on, or in the next session).
        /// </summary>
        public static void KeepScreen(in Matrix pose)
        {
            Matrix from;
            lock (screenGate)
            {
                movedTo = pose;
                from = shownAnchor;
            }
            Matrix local = pose * Matrix.Invert(from);
            VRSettings.ExternalViewPlace = string.Join(" ", PoseNumbers.Of(local).Select(v => v.ToString("0.####", CultureInfo.InvariantCulture)));
        }

        /// <summary>Any thread: forgets where the screen was moved by hand; it goes back on the dash from the next headset frame.</summary>
        public static void PutScreenBack()
        {
            VRSettings.ExternalViewPlace = string.Empty;
            lock (screenGate)
            {
                placeAgain = true;
                movedTo = null;
            }
        }

        /// <summary>Where the screen was moved by hand, from <paramref name="heading"/> (<see cref="Heading"/>); false for the dash (none kept, or one that does not read).</summary>
        private static bool TryKeptPlace(in Matrix heading, out Matrix pose)
        {
            pose = Matrix.Identity;
            string kept = VRSettings.ExternalViewPlace;
            if (string.IsNullOrWhiteSpace(kept))
                return false;
            string[] parts = kept.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != PoseNumbers.Count)
                return false;
            var v = new float[PoseNumbers.Count];
            for (int i = 0; i < v.Length; i++)
                if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]) || float.IsNaN(v[i]) || float.IsInfinity(v[i]))
                    return false;
            if (!PoseNumbers.TryPose(v, MaxPlaceDistance, out Matrix local))
                return false;
            pose = local * heading;
            return true;
        }
    }
}
