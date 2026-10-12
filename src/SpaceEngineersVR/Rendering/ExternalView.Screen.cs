// ExternalView, game thread: moving the headset screen by hand and remembering where it was put.

using System;
using System.Globalization;
using System.Linq;
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
            Quaternion turn = Quaternion.CreateFromRotationMatrix(local);
            Vector3 at = local.Translation;
            VRSettings.ExternalViewPlace = string.Join(" ", new[] { at.X, at.Y, at.Z, turn.X, turn.Y, turn.Z, turn.W }
                .Select(v => v.ToString("0.####", CultureInfo.InvariantCulture)));
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
            if (parts.Length != 7)
                return false;
            var v = new float[7];
            for (int i = 0; i < 7; i++)
                if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]) || float.IsNaN(v[i]) || float.IsInfinity(v[i]))
                    return false;
            var at = new Vector3(v[0], v[1], v[2]);
            var turn = new Quaternion(v[3], v[4], v[5], v[6]);
            if (at.Length() > MaxPlaceDistance || turn.LengthSquared() < 0.5f)
                return false;
            turn.Normalize();
            Matrix local = Matrix.CreateFromQuaternion(turn);
            local.Translation = at;
            pose = local * heading;
            return true;
        }
    }
}
