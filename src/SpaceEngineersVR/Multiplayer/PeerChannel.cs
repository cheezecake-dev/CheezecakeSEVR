using System;
using System.Collections.Generic;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game.Components;

namespace SpaceEngineersVR.Multiplayer
{
    /// <summary>
    /// The plugin's own messages between players who have it, in a game one of them hosts: each VR player's head and hands
    /// (<see cref="PeerPose"/>), drawn on their character in everyone else's game (<see cref="RemotePoses"/>). Players
    /// without the plugin are never sent anything and see the game's own animation.
    /// </summary>
    /// <remarks>
    /// The game passes a client's mod messages only through the host: a message one client addresses to another goes to
    /// the host and no further (MyModAPIHelper.MyMultiplayer: ModMessageClientUnreliable is a [Server][Client] event the
    /// host's replication sends back to where it came from). So it runs through the host:
    /// <list type="bullet">
    /// <item>On joining, a client says <see cref="Hello"/> to the host. A host with the plugin keeps it as a peer and
    /// answers <see cref="Welcome"/>; a host without it drops the message and nothing more is sent.</item>
    /// <item>A VR client sends its pose to the host (<see cref="PoseToHost"/>); the host draws it and passes it on, with the
    /// sender's Steam id, to every other peer (<see cref="PoseFromHost"/>). A VR host sends its own the same way.</item>
    /// </list>
    /// The host takes a pose's sender from the game's own record of who sent it, never from the message; a client takes
    /// <see cref="PoseFromHost"/> only from the host. Poses go unreliably (a lost one is replaced by the next) at
    /// <see cref="Interval"/>: 15 a second, 48 bytes each from the host and 40 to it.
    /// No dedicated server and no host-side plugin: a game hosted without the plugin shows nobody's VR arms.
    /// <para>The handshake is also how the plugin's scenarios know who has the plugin (<see cref="HasPlugin"/>,
    /// <see cref="HostHasPlugin"/>, <see cref="PlayerWithPlugin"/>); they send their own messages on channels of their own.</para>
    /// </remarks>
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public partial class PeerChannel : MySessionComponentBase
    {
        /// <summary>The bug swarm's is 39317.</summary>
        internal const ushort Channel = 39318;

        /// <summary>Raised when a message's layout changes; players on different versions do not pair up.</summary>
        private const byte Version = 1;

        private const byte Hello = 1, Welcome = 2, PoseToHost = 3, PoseFromHost = 4;

        /// <summary>
        /// Simulation frames between poses: 15 a second at 60. Arms and heads interpolated at 15 Hz with a 100 ms buffer
        /// (<see cref="RemotePoses"/>) move smoothly at a glance; 20 Hz would cost a third more for a quicker reaction,
        /// and the game itself syncs characters at about this rate.
        /// </summary>
        internal const int Interval = 4;

        /// <summary>Simulation frames until a client says hello, and when it asks again if the host has not answered (a host loading slowly).</summary>
        private const int HelloAt = 60, HelloAgainAt = 600;

        /// <summary>Simulation frames after which a client takes a host that has not answered either hello to be without the plugin.</summary>
        private const int NoAnswerAt = HelloAgainAt + 300;

        private const int LogEvery = 60 * 30;

        private static readonly FeatureGuard guard = new FeatureGuard(nameof(PeerChannel));

        /// <summary>On the host: the clients with the plugin, by Steam id.</summary>
        private static readonly HashSet<ulong> peers = new HashSet<ulong>();
        private static readonly List<ulong> gone = new List<ulong>();

        /// <summary>On a client: the host has the plugin and passes poses on.</summary>
        private static bool welcomed;

        private static bool registered;
        private static int frame, sent, relayed, refused;
        private static int loggedSent, loggedReceived, loggedDrawn;
        private static readonly byte[] toHost = new byte[1 + PeerPose.Size];
        private static readonly byte[] fromHost = new byte[1 + 8 + PeerPose.Size];

        /// <summary>
        /// On the host: a player with the plugin said hello (on joining, or again). Raised for each hello, so a scenario can
        /// send that player its state each time.
        /// </summary>
        internal static event Action<ulong> PlayerWithPlugin;

        /// <summary>On the host: whether the player with this Steam id has the plugin (the host itself has).</summary>
        internal static bool HasPlugin(ulong steamId) => Sync.IsServer && (steamId == Sync.MyId || peers.Contains(steamId));

        /// <summary>On the host: the players with the plugin besides the host.</summary>
        internal static IReadOnlyCollection<ulong> Peers => peers;

        /// <summary>
        /// Whether the game's host has the plugin: true when hosting (or in single player), or once the host has answered;
        /// false once it has not answered either hello; null while asking.
        /// </summary>
        internal static bool? HostHasPlugin => !Sync.MultiplayerActive || Sync.IsServer || welcomed ? true
            : frame >= NoAnswerAt ? false : (bool?)null;

        // A test aid loops the poses this game sends back into its own drawing (onto a puppet); compiled away without it.
        static partial void TestAidsLoopback(ref bool on);
        static partial void TestAidsSent(in PeerPose pose);

        public override void BeforeStart()
        {
            peers.Clear();
            welcomed = false;
            frame = sent = relayed = refused = loggedSent = loggedReceived = loggedDrawn = 0;
            RemotePoses.Clear();
            if (MyAPIGateway.Utilities.IsDedicated)
                return;
            MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(Channel, Received);
            registered = true;
        }

        protected override void UnloadData()
        {
            if (registered)
                MyAPIGateway.Multiplayer?.UnregisterSecureMessageHandler(Channel, Received);
            registered = false;
            peers.Clear();
            welcomed = false;
            RemotePoses.Clear();
        }

        public override void UpdateAfterSimulation()
        {
            if (!registered || !guard.On)
                return;
            try
            {
                frame++;
                if (Sync.MultiplayerActive && !Sync.IsServer && !welcomed && (frame == HelloAt || frame == HelloAgainAt))
                {
                    MyAPIGateway.Multiplayer.SendMessageToServer(Channel, new[] { Hello, Version }, true);
                    Log.Info($"Peers: said hello to the host ({(frame == HelloAt ? "joining" : "no answer yet, again")})");
                }
                int now = MySession.Static.GameplayFrameCounter;
                if (now % Interval == 0)
                    Send(now);
                if (frame % LogEvery == 0)
                    LogCounts();
            }
            catch (Exception e)
            {
                guard.Failed(e, "Sending the VR pose to other players failed");
            }
        }

        private static void Send(int now)
        {
            bool loopback = false;
            TestAidsLoopback(ref loopback);
            bool anyone = Sync.MultiplayerActive && (Sync.IsServer ? peers.Count > 0 : welcomed);
            if (!anyone && !loopback)
                return;
            if (!PoseCapture.TryTake(out PeerPose pose))
                return;
            // The sequence counts send intervals, not messages, so a pause in sending does not slow the receiver's clock.
            pose.Sequence = (ushort)(now / Interval);
            if (loopback)
                TestAidsSent(pose);
            if (!anyone)
                return;
            if (Sync.IsServer)
            {
                FromHost(Sync.MyId, pose);
                SendToPeers(Sync.MyId);
            }
            else
            {
                toHost[0] = PoseToHost;
                pose.Write(toHost, 1);
                MyAPIGateway.Multiplayer.SendMessageToServer(Channel, toHost, false);
            }
            sent++;
        }

        private static void FromHost(ulong steamId, in PeerPose pose)
        {
            fromHost[0] = PoseFromHost;
            BitConverter.GetBytes(steamId).CopyTo(fromHost, 1);
            pose.Write(fromHost, 9);
        }

        /// <summary>On the host: <see cref="fromHost"/> to every peer but <paramref name="except"/>; peers who left are forgotten.</summary>
        private static void SendToPeers(ulong except)
        {
            foreach (ulong peer in peers)
            {
                if (!Sync.Clients.HasClient(peer))
                {
                    gone.Add(peer);
                    continue;
                }
                if (peer != except)
                    MyAPIGateway.Multiplayer.SendMessageTo(Channel, fromHost, peer, false);
            }
            foreach (ulong peer in gone)
            {
                peers.Remove(peer);
                Log.Info($"Peers: {peer} left");
            }
            gone.Clear();
        }

        private static void Received(ushort channel, byte[] message, ulong sender, bool fromServer)
        {
            if (!guard.On || message == null || message.Length < 2)
                return;
            try
            {
                switch (message[0])
                {
                    case Hello:
                        if (!Sync.IsServer || message[1] != Version || sender == 0 || sender == Sync.MyId)
                            return;
                        if (peers.Add(sender))
                            Log.Info($"Peers: {sender} has the plugin; {peers.Count} with it besides the host");
                        MyAPIGateway.Multiplayer.SendMessageTo(Channel, new[] { Welcome, Version }, sender, true);
                        try
                        {
                            PlayerWithPlugin?.Invoke(sender);
                        }
                        catch (Exception e)
                        {
                            // a scenario's failure is its own, never the poses'
                            Log.Error(e, "Peers: a scenario could not greet a player with the plugin");
                        }
                        return;
                    case Welcome:
                        if (Sync.IsServer || !fromServer || message[1] != Version || welcomed)
                            return;
                        welcomed = true;
                        Log.Info("Peers: the host has the plugin; VR heads and hands go both ways");
                        return;
                    case PoseToHost:
                        if (!Sync.IsServer || !peers.Contains(sender) || !PeerPose.TryRead(message, 1, out PeerPose pose))
                        {
                            refused++;
                            return;
                        }
                        RemotePoses.Receive(sender, pose);
                        if (peers.Count > 1)
                        {
                            FromHost(sender, pose);
                            SendToPeers(sender);
                            relayed++;
                        }
                        return;
                    case PoseFromHost:
                        if (Sync.IsServer || !fromServer || message.Length < 9 || !PeerPose.TryRead(message, 9, out PeerPose passed))
                        {
                            refused++;
                            return;
                        }
                        ulong from = BitConverter.ToUInt64(message, 1);
                        if (from != Sync.MyId)
                            RemotePoses.Receive(from, passed);
                        return;
                }
            }
            catch (Exception e)
            {
                guard.Failed(e, "A VR pose from another player could not be read");
            }
        }

        /// <summary>Every 30 s while anything moved: poses sent, taken in and drawn since the last line.</summary>
        private static void LogCounts()
        {
            int received = RemotePoses.Received, drawn = RemotePoses.Drawn;
            if (sent == loggedSent && received == loggedReceived && drawn == loggedDrawn)
                return;
            Log.Info($"Peers: {sent - loggedSent} poses sent, {received - loggedReceived} received, {drawn - loggedDrawn} draws of other VR players " +
                     $"in the last {LogEvery / 60} s; {relayed} passed on, {refused} refused in all; " +
                     (Sync.IsServer ? $"hosting, {peers.Count} peers" : welcomed ? "the host has the plugin" : "the host has not answered"));
            loggedSent = sent;
            loggedReceived = received;
            loggedDrawn = drawn;
        }
    }
}
