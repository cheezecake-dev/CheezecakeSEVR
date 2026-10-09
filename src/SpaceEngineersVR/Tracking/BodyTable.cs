namespace SpaceEngineersVR.Tracking
{
    /// <summary>
    /// Where the astronaut's own body is, in front of the first-person camera: the most forward the eye can be, for each
    /// height and each distance from the middle, and still be 7 cm from every surface the game draws in first person.
    /// Generated from the astronaut's meshes (SE_astronaut_LOD0.mwm and the female's, Default_Astronaut in Characters.sbc), all the
    /// materials that stay drawn (the suit, the cloth, the gear, the backpack, the boots; not the head, the hood, the glass, the lights, the
    /// arms and the gloves, which follow the hands) and the smaller of the two models' values at each cell. Head frame: origin at the
    /// HeadDummy bone (where the camera is), x right, y up, z BACK (the body's forward is -z).
    /// For each cell: the smallest, over every surface point within 7 cm of the cell's centre, of that point's z minus the rest of
    /// the distance to the margin, so that an eye at or in front of the value is at least the margin from the point.
    /// </summary>
    internal static class BodyTable
    {
        /// <summary>The margin the table was built with, metres.</summary>
        public const float Margin = 0.07f;

        /// <summary>No body here: any z is clear.</summary>
        public const float None = 9f;

        /// <summary>The rows' heights (y), from the top down.</summary>
        public static readonly float[] RowY = { +0.16f, +0.14f, +0.12f, +0.10f, +0.08f, +0.06f, +0.04f, +0.02f, +0.00f, -0.02f, -0.04f, -0.06f, -0.08f, -0.10f, -0.12f, -0.14f, -0.16f, -0.18f, -0.20f, -0.25f, -0.30f, -0.35f, -0.40f, -0.45f, -0.50f, -0.55f, -0.60f, -0.70f, -0.80f, -0.95f, -1.20f, -1.70f };

        /// <summary>The columns' distance from the middle (|x|).</summary>
        public static readonly float[] ColX = { 0.00f, 0.06f, 0.12f, 0.18f, 0.24f, 0.30f };

        /// <summary>[row][column]: the most forward (smallest) z the eye can be at, or <see cref="None"/>.</summary>
        public static readonly float[][] StopZ =
        {
            new[] { None, None, None, None, None, None }, // y +0.16
            new[] { None, None, None, None, None, None }, // y +0.14
            new[] { +0.195f, +0.197f, +0.196f, None, None, None }, // y +0.12
            new[] { +0.174f, +0.175f, +0.172f, None, None, None }, // y +0.10
            new[] { +0.164f, +0.165f, +0.128f, +0.382f, None, None }, // y +0.08
            new[] { +0.161f, +0.144f, +0.112f, +0.139f, None, None }, // y +0.06
            new[] { +0.164f, +0.132f, +0.105f, +0.128f, None, None }, // y +0.04
            new[] { +0.173f, +0.105f, +0.095f, +0.127f, None, None }, // y +0.02
            new[] { +0.195f, +0.075f, +0.081f, +0.127f, None, None }, // y +0.00
            new[] { +0.058f, +0.052f, +0.069f, +0.129f, None, None }, // y -0.02
            new[] { +0.033f, +0.036f, +0.062f, +0.112f, +0.194f, None }, // y -0.04
            new[] { +0.022f, +0.026f, +0.060f, +0.095f, +0.140f, None }, // y -0.06
            new[] { +0.018f, +0.022f, +0.046f, +0.073f, +0.099f, None }, // y -0.08
            new[] { +0.020f, +0.019f, +0.032f, +0.054f, +0.075f, +0.154f }, // y -0.10
            new[] { +0.001f, +0.001f, +0.018f, +0.042f, +0.058f, +0.128f }, // y -0.12
            new[] { -0.011f, -0.011f, -0.005f, +0.021f, +0.050f, +0.116f }, // y -0.14
            new[] { -0.016f, -0.015f, -0.021f, -0.003f, +0.045f, +0.106f }, // y -0.16
            new[] { -0.019f, -0.018f, -0.028f, -0.016f, +0.030f, +0.099f }, // y -0.18
            new[] { -0.046f, -0.055f, -0.039f, -0.021f, +0.017f, +0.095f }, // y -0.20
            new[] { -0.099f, -0.102f, -0.060f, -0.037f, +0.016f, +0.104f }, // y -0.25
            new[] { -0.111f, -0.108f, -0.098f, -0.035f, +0.043f, None }, // y -0.30
            new[] { -0.111f, -0.111f, -0.099f, -0.043f, +0.048f, None }, // y -0.35
            new[] { -0.111f, -0.109f, -0.100f, -0.032f, +0.123f, None }, // y -0.40
            new[] { -0.109f, -0.107f, -0.095f, -0.026f, +0.150f, None }, // y -0.45
            new[] { -0.095f, -0.099f, -0.057f, -0.023f, +0.068f, None }, // y -0.50
            new[] { -0.053f, -0.049f, -0.051f, -0.012f, +0.059f, None }, // y -0.55
            new[] { -0.051f, -0.044f, -0.023f, +0.005f, +0.026f, None }, // y -0.60
            new[] { -0.063f, -0.060f, -0.045f, -0.014f, +0.025f, +0.112f }, // y -0.70
            new[] { -0.045f, -0.052f, -0.043f, -0.023f, +0.015f, +0.134f }, // y -0.80
            new[] { -0.008f, -0.034f, -0.038f, -0.026f, +0.009f, +0.128f }, // y -0.95
            new[] { +0.077f, +0.038f, +0.034f, +0.044f, +0.075f, None }, // y -1.20
            new[] { None, +0.037f, +0.024f, +0.033f, +0.078f, None }, // y -1.70
        };
    }
}
