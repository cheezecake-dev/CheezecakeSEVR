using System;

namespace SpaceEngineersVR.Scenarios.BugSwarm
{
    /// <summary>What a message on the swarm's channel is: a request to the host, or the host's word to a player.</summary>
    internal enum SwarmKind : byte
    {
        /// <summary>To the host: wake the swarm at the panel, saving the game first.</summary>
        Wake = 1,
        /// <summary>To the host: wake the swarm at the panel without saving.</summary>
        WakeUnsaved = 2,
        /// <summary>To the host: end the run at the panel.</summary>
        End = 3,
        /// <summary>From the host: the player pressed the panel; ask them whether to save and start.</summary>
        AskFirst = 4,
        /// <summary>From the host: the player pressed the panel in its run; ask them whether to end it.</summary>
        AskEnd = 5,
        /// <summary>From the host: whether swarm blocks work in its game, and whether a run is on (<see cref="SwarmState"/>).</summary>
        State = 6,
    }

    /// <summary>The host's state, in a <see cref="SwarmKind.State"/> message.</summary>
    [Flags]
    internal enum SwarmState : byte
    {
        None = 0,
        /// <summary>The host's game runs swarm blocks (Bug Swarm on button panels is on there).</summary>
        SwarmOn = 1,
        /// <summary>A run is on.</summary>
        RunOn = 2,
        /// <summary>The run brings players who die back at the base, so the respawn screen waits.</summary>
        BringsBack = 4,
        /// <summary>The host's world is the VR Bug Swarm scenario: swarm blocks work in it whatever the host's option says, and
        /// each run puts the base back.</summary>
        Scenario = 8,
    }

    /// <summary>
    /// A message on the swarm's channel, between players who have the plugin (<see cref="BugSwarmSession"/>). 11 bytes:
    /// the layout's version, the kind, the swarm block's entity id (0 for <see cref="SwarmKind.State"/>) and the state.
    /// A message of another version, length or kind is refused whole.
    /// </summary>
    internal struct SwarmMessage
    {
        /// <summary>Raised when the layout or the meaning of a kind changes; games on different versions ignore each other.</summary>
        internal const byte Version = 2;

        internal const int Size = 11;

        public SwarmKind Kind;
        public long Panel;
        public SwarmState State;

        public SwarmMessage(SwarmKind kind, long panel, SwarmState state = SwarmState.None)
        {
            Kind = kind;
            Panel = panel;
            State = state;
        }

        /// <summary>Requests go from a player to the host; the rest come from the host.</summary>
        internal static bool ToHost(SwarmKind kind) => kind == SwarmKind.Wake || kind == SwarmKind.WakeUnsaved || kind == SwarmKind.End;

        public byte[] Write()
        {
            var bytes = new byte[Size];
            bytes[0] = Version;
            bytes[1] = (byte)Kind;
            BitConverter.GetBytes(Panel).CopyTo(bytes, 2);
            bytes[10] = (byte)State;
            return bytes;
        }

        public static bool TryRead(byte[] bytes, out SwarmMessage message)
        {
            message = default(SwarmMessage);
            if (bytes == null || bytes.Length != Size || bytes[0] != Version)
                return false;
            var kind = (SwarmKind)bytes[1];
            if (kind < SwarmKind.Wake || kind > SwarmKind.State)
                return false;
            var state = (SwarmState)bytes[10];
            if ((state & ~(SwarmState.SwarmOn | SwarmState.RunOn | SwarmState.BringsBack | SwarmState.Scenario)) != 0)
                return false;
            long panel = BitConverter.ToInt64(bytes, 2);
            // a request names a panel; the host's state names none
            if ((kind == SwarmKind.State) != (panel == 0))
                return false;
            message = new SwarmMessage(kind, panel, state);
            return true;
        }
    }
}
