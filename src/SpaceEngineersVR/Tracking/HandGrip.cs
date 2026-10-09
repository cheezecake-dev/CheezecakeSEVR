using VRageMath;

namespace SpaceEngineersVR.Tracking
{
    /// <summary>
    /// Where the astronaut's palm bone goes for a controller's grip pose: the hand closed round the controller's handle.
    /// </summary>
    /// <remarks>
    /// The grip pose (OpenXR 1.1 spec, standard pose identifiers): origin in the middle of the handle, level with the
    /// palm's centre; -Z through the tube the closed fingers make, from the little finger toward the thumb; +X the
    /// palm's normal, into the palm on the right hand and out of it on the left (so +X is the user's right on both
    /// hands held palms in); +Y = Z x X, which on a closed hand runs back toward the wrist.
    /// The palm bone (SE_Rig?Palm, at the wrist; read from SE_astronaut.mwm): X back toward the forearm, Y the back of
    /// the hand, Z toward the thumb on the right hand and away from it on the left (the left skeleton is the right one
    /// mirrored). The glove (RightGlove in SE_astronaut_LOD0.mwm, palm-bone frame): knuckles 0.12 m out from the
    /// wrist, the palm's surface 0.044 m below the bone's line, the hand's middle 0.005 m from it toward the little finger.
    /// The handle's middle is put 0.085 m out (the palm's centre, toward the knuckles) and 0.024 m below the bone's line,
    /// and the handle crosses the palm on a slant of 15 degrees, the little-finger end nearer the wrist (a power grip).
    /// The 0.024 m was fitted by eye (RenderDebug HandGhost=2, which draws the handle over the drawn hand, from the back
    /// and from above): with the palm's surface and a 2 cm handle radius (0.064 m) the closed fingers stopped 3 to 4 cm
    /// short of the handle, which stood out beyond them; 0.04 m nearer, the handle lies in the tube the curled fingers make.
    /// </remarks>
    internal static class HandGrip
    {
        /// <summary>The handle's middle in the right palm bone's frame, metres (the left: Z mirrored).</summary>
        private static readonly Vector3 HandleInRightPalm = new Vector3(-0.085f, -0.024f, -0.005f);

        /// <summary>The handle's slant across the palm, degrees from square across it, toward the fingers at the thumb's end.</summary>
        private const float Slant = 15f;

        /// <summary>
        /// The palm bone's pose in the grip pose's frame (row vectors: palm-local times this is grip-local), for one hand,
        /// turned (degrees about the grip's X, Y, Z: in a hand held forward, pitch, roll, yaw) and shifted (metres along
        /// the grip's X, Y, Z), both mirrored for the left hand so one setting suits both.
        /// </summary>
        public static Matrix PalmFromGrip(bool left, Vector3 turn, Vector3 shift)
        {
            float c = (float)System.Math.Cos(MathHelper.ToRadians(Slant)), s = (float)System.Math.Sin(MathHelper.ToRadians(Slant));
            // Rows: the palm bone's X, Y, Z in grip coordinates.
            Vector3 x = new Vector3(0f, c, s);
            Vector3 y = left ? new Vector3(-1f, 0f, 0f) : new Vector3(1f, 0f, 0f);
            Vector3 z = left ? new Vector3(0f, -s, c) : new Vector3(0f, s, -c);
            Vector3 handle = left ? new Vector3(HandleInRightPalm.X, HandleInRightPalm.Y, -HandleInRightPalm.Z) : HandleInRightPalm;
            Vector3 at = -(handle.X * x + handle.Y * y + handle.Z * z);
            var palm = new Matrix(x.X, x.Y, x.Z, 0f, y.X, y.Y, y.Z, 0f, z.X, z.Y, z.Z, 0f, at.X, at.Y, at.Z, 1f);

            float mirror = left ? -1f : 1f;
            Matrix tune = Matrix.CreateRotationX(MathHelper.ToRadians(turn.X))
                          * Matrix.CreateRotationY(MathHelper.ToRadians(turn.Y * mirror))
                          * Matrix.CreateRotationZ(MathHelper.ToRadians(turn.Z * mirror));
            tune.Translation = new Vector3(shift.X * mirror, shift.Y, shift.Z);
            return palm * tune;
        }

        // ---- From the runtime's palm pose (XR_EXT_palm_pose; OpenXR 1.1 grip_surface), when it has one ----
        // The palm pose (XR_EXT_palm_pose, "the definitions of both poses are identical" to 1.1's grip_surface): origin
        // the palm's centroid at the surface of the palm; -Z parallel to the index finger straightened; +X the palm's
        // normal, into the palm on the right hand and away from it on the left; +Y = Z x X, which is toward the thumb on
        // both hands. Nothing about the controller's handle is guessed: the glove's palm surface goes on the pose.

        /// <summary>
        /// The middle of the glove's palm surface in the right palm bone's frame, metres (the left: Z mirrored): 0.085 m out
        /// from the wrist toward the knuckles (the palm's centre, as the handle is placed above), 0.044 m below the bone's
        /// line (the palm's surface), 0.005 m toward the little finger (the hand's middle).
        /// </summary>
        private static readonly Vector3 PalmSurfaceInRightPalm = new Vector3(-0.085f, -0.044f, -0.005f);

        /// <summary>
        /// The palm bone's pose in the palm pose's frame (row vectors: palm-bone-local times this is palm-pose-local), for
        /// one hand, with the same turn and shift as <see cref="PalmFromGrip"/>, here about and along the palm pose's axes.
        /// </summary>
        public static Matrix PalmFromPalmPose(bool left, Vector3 turn, Vector3 shift)
        {
            // Rows: the palm bone's X (toward the forearm), Y (the back of the hand), Z (toward the thumb on the right
            // hand, away from it on the left) in palm pose coordinates. The forearm is +Z (the fingers point -Z); the back
            // of the hand is +X on the right hand (+X goes into the palm) and -X on the left; the thumb is +Y.
            Vector3 x = new Vector3(0f, 0f, 1f);
            Vector3 y = left ? new Vector3(-1f, 0f, 0f) : new Vector3(1f, 0f, 0f);
            Vector3 z = left ? new Vector3(0f, -1f, 0f) : new Vector3(0f, 1f, 0f);
            Vector3 surface = left ? new Vector3(PalmSurfaceInRightPalm.X, PalmSurfaceInRightPalm.Y, -PalmSurfaceInRightPalm.Z) : PalmSurfaceInRightPalm;
            Vector3 at = -(surface.X * x + surface.Y * y + surface.Z * z);
            var palm = new Matrix(x.X, x.Y, x.Z, 0f, y.X, y.Y, y.Z, 0f, z.X, z.Y, z.Z, 0f, at.X, at.Y, at.Z, 1f);

            float mirror = left ? -1f : 1f;
            Matrix tune = Matrix.CreateRotationX(MathHelper.ToRadians(turn.X))
                          * Matrix.CreateRotationY(MathHelper.ToRadians(turn.Y * mirror))
                          * Matrix.CreateRotationZ(MathHelper.ToRadians(turn.Z * mirror));
            tune.Translation = new Vector3(shift.X * mirror, shift.Y, shift.Z);
            return palm * tune;
        }
    }
}
