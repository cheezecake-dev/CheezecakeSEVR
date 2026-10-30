namespace SpaceEngineersVR.Multiplayer
{
    /// <summary>
    /// The peer messages' layout (<see cref="PeerChannel"/>), kept apart because a second reader holds a copy: the dedicated
    /// server relay mod (mods/SevrRelay, a mod cannot reference the plugin). tests/RelayTests compiles both and fails when
    /// they differ.
    /// </summary>
    internal static class PeerWire
    {
        /// <summary>The bug swarm's is 39317.</summary>
        public const ushort Channel = 39318;

        /// <summary>Raised when a message's layout changes; players on different versions do not pair up.</summary>
        public const byte Version = 1;

        /// <summary>The first byte of each message.</summary>
        public const byte Hello = 1, Welcome = 2, PoseToHost = 3, PoseFromHost = 4;

        /// <summary>
        /// Welcome's third byte, what answered: a host playing with the plugin (which runs scenarios and draws poses), or a
        /// dedicated server's relay mod (which only passes poses on). A Welcome of two bytes, from a plugin before the
        /// relay, is a host with the plugin.
        /// </summary>
        public const byte FromPlugin = 0, FromRelay = 1;

        /// <summary>A pose to the host: the kind, then the pose.</summary>
        public const int ToHostSize = 1 + PeerPose.Size;

        /// <summary>A pose from the host: the kind, the sender's Steam id (8, little-endian), then the pose.</summary>
        public const int FromHostSize = 1 + 8 + PeerPose.Size;
    }
}
