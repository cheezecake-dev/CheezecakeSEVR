// PoseNumbers: a pose as the seven numbers a setting keeps it in.
using VRageMath;

namespace SpaceEngineersVR.Common
{
    /// <summary>
    /// A pose (a position and a turn, no scale) as seven numbers, the position x, y, z in metres and the turn as a quaternion
    /// x, y, z, w: how the hidden settings keep a place something was moved to by hand (the external view's screen).
    /// </summary>
    internal static class PoseNumbers
    {
        public const int Count = 7;

        public static float[] Of(in Matrix pose)
        {
            Quaternion turn = Quaternion.CreateFromRotationMatrix(pose);
            Vector3 at = pose.Translation;
            return new[] { at.X, at.Y, at.Z, turn.X, turn.Y, turn.Z, turn.W };
        }

        /// <summary>
        /// The pose of <paramref name="numbers"/>; false when they are not one: not <see cref="Count"/> of them, a position
        /// further than <paramref name="maxDistance"/> metres, or no turn.
        /// </summary>
        public static bool TryPose(float[] numbers, float maxDistance, out Matrix pose)
        {
            pose = Matrix.Identity;
            if (numbers == null || numbers.Length != Count)
                return false;
            var at = new Vector3(numbers[0], numbers[1], numbers[2]);
            var turn = new Quaternion(numbers[3], numbers[4], numbers[5], numbers[6]);
            if (!(at.Length() <= maxDistance) || turn.LengthSquared() < 0.5f)
                return false;
            turn.Normalize();
            pose = Matrix.CreateFromQuaternion(turn);
            pose.Translation = at;
            return true;
        }
    }
}
