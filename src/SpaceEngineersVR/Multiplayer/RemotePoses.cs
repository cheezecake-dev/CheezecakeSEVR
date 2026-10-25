using System;
using System.Collections.Generic;
using HarmonyLib;
using Sandbox.Game.Components;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using SpaceEngineersVR.Tracking;
using VRageMath;
using VRageRender.Animations;

namespace SpaceEngineersVR.Multiplayer
{
    /// <summary>
    /// Other VR players' heads and hands, drawn on their characters in this game, in VR or flat (<see cref="PeerChannel"/>
    /// brings the poses). Poses arrive about every <see cref="PeerChannel.Interval"/> frames; each character is drawn
    /// <see cref="Delay"/> frames behind the newest, between the two poses either side, so the arms move smoothly through
    /// late and lost messages. Visual only: the bones are posed just for the character's draw and put back straight after
    /// (<see cref="HandIK.DrawRemote"/>), so the game's hits, aim and everything else it reads from the bones never see it.
    /// </summary>
    internal static partial class RemotePoses
    {
        /// <summary>How far behind the newest pose a character is drawn, simulation frames: one and a half send intervals, room for one late or lost message.</summary>
        private const double Delay = PeerChannel.Interval * 1.5;

        /// <summary>A pose this long unanswered (simulation frames, 1 s) and the arms go back to the game's animation.</summary>
        private const int Stale = 60;

        /// <summary>How fast the estimate of a player's message delay creeps up after it was last lowered, of the difference per pose.</summary>
        private const double OffsetRise = 0.1;

        private const int Kept = 6;

        private struct Sample
        {
            public double Time;
            public PeerPose Pose;
        }

        private sealed class Peer
        {
            public ulong SteamId;
            public long LastSequence = -1;
            public double Offset;
            public int Arrived;
            public readonly Sample[] Samples = new Sample[Kept];
            public int Count;
            public readonly HandIK.RemoteRig Rig = new HandIK.RemoteRig();
            public MyCharacter Character;
        }

        private static readonly Dictionary<ulong, Peer> peers = new Dictionary<ulong, Peer>();
        private static readonly Dictionary<MyCharacter, Peer> byCharacter = new Dictionary<MyCharacter, Peer>();
        private static readonly FeatureGuard guard = new FeatureGuard(nameof(RemotePoses));
        private static int drawFrame;

        /// <summary>Poses taken in and characters drawn since the session began, for the log (<see cref="PeerChannel"/>).</summary>
        internal static int Received { get; private set; }
        internal static int Drawn { get; private set; }

        // A test aid stands a puppet in for a player it loops the local poses back from; without the test aids this is compiled away.
        static partial void TestAidsCharacter(ulong steamId, ref MyCharacter character);

        public static void Patch(Harmony harmony)
        {
            PatchRegistry.Hook(harmony, typeof(RemotePoses), "Other VR players' arms", "other VR players are drawn with the game's animation",
                "MyRenderComponentSkinnedEntity.Draw", AccessTools.Method(typeof(MyRenderComponentSkinnedEntity), nameof(MyRenderComponentSkinnedEntity.Draw)),
                prefix: nameof(BeforeDraw), postfix: nameof(AfterDraw));
        }

        /// <summary>Game thread: a pose from <paramref name="steamId"/>'s game.</summary>
        internal static void Receive(ulong steamId, in PeerPose pose)
        {
            if (!peers.TryGetValue(steamId, out Peer peer))
                peers[steamId] = peer = new Peer { SteamId = steamId };
            int now = Now;
            // The sequence is 16 bits; it is unwrapped against the last one, and a pose older than that one is late and dropped.
            long sequence = peer.LastSequence < 0 ? pose.Sequence : peer.LastSequence + (short)(ushort)(pose.Sequence - (ushort)peer.LastSequence);
            if (peer.LastSequence >= 0 && sequence <= peer.LastSequence)
                return;
            bool restart = peer.LastSequence < 0 || now - peer.Arrived > Stale || sequence - peer.LastSequence > 1000;
            peer.LastSequence = sequence;
            peer.Arrived = now;
            Received++;

            // The sender's clock in our frames: its sequence times the interval, plus the smallest delay seen, which creeps up
            // slowly in case the route got longer. The jitter above that is what Delay absorbs.
            double sent = sequence * (double)PeerChannel.Interval;
            double offset = now - sent;
            if (restart)
            {
                peer.Count = 0;
                peer.Offset = offset;
            }
            else if (offset < peer.Offset)
                peer.Offset = offset;
            else
                peer.Offset += (offset - peer.Offset) * OffsetRise;
            if (peer.Count == Kept)
            {
                Array.Copy(peer.Samples, 1, peer.Samples, 0, Kept - 1);
                peer.Count--;
            }
            peer.Samples[peer.Count++] = new Sample { Time = sent, Pose = pose };

            MyCharacter character = Find(steamId);
            if (character != peer.Character)
            {
                if (peer.Character != null)
                    byCharacter.Remove(peer.Character);
                peer.Character = character;
            }
            if (character != null)
                byCharacter[character] = peer;
        }

        /// <summary>The session ended: nobody is drawn any more.</summary>
        internal static void Clear()
        {
            peers.Clear();
            byCharacter.Clear();
            Received = Drawn = 0;
        }

        private static MyCharacter Find(ulong steamId)
        {
            MyCharacter character = null;
            TestAidsCharacter(steamId, ref character);
            if (character != null)
                return character;
            return Sync.Players != null && Sync.Players.TryGetPlayerBySteamId(steamId, out MyPlayer player) ? player.Character : null;
        }

        /// <summary>Where the character is to be drawn now: between the two poses either side of the draw time, or the newest held.</summary>
        private static bool TryPose(Peer peer, out PeerPose pose)
        {
            pose = default;
            if (peer.Count == 0 || Now - peer.Arrived > Stale)
                return false;
            double at = Now - peer.Offset - Delay;
            Sample[] s = peer.Samples;
            if (at <= s[0].Time)
            {
                pose = s[0].Pose;
                return true;
            }
            for (int i = 1; i < peer.Count; i++)
            {
                if (at <= s[i].Time)
                {
                    double span = s[i].Time - s[i - 1].Time;
                    float t = span > 0 ? (float)((at - s[i - 1].Time) / span) : 1f;
                    pose = PeerPose.Between(s[i - 1].Pose, s[i].Pose, MathHelper.Clamp(t, 0f, 1f));
                    return true;
                }
            }
            pose = s[peer.Count - 1].Pose;
            return true;
        }

        private static int Now => MySession.Static?.GameplayFrameCounter ?? 0;

        /// <summary>Before a character is drawn: another VR player's pose goes on its bones.</summary>
        private static void BeforeDraw(MyRenderComponentSkinnedEntity __instance, out bool __state)
        {
            __state = false;
            if (byCharacter.Count == 0 || !guard.On)
                return;
            try
            {
                if (!(__instance.Entity is MyCharacter character) || !byCharacter.TryGetValue(character, out Peer peer))
                    return;
                if (character == MySession.Static?.LocalCharacter || character.IsDead || character.Closed || character.IsOnLadder)
                    return;
                MyCharacterBone[] bones = character.AnimationController?.CharacterBones;
                Matrix[] absolutes = character.AnimationController?.BoneAbsoluteTransforms;
                if (bones == null || absolutes == null || character.PositionComp == null || !TryPose(peer, out PeerPose pose))
                    return;
                peer.Rig.Save(bones, absolutes);
                __state = true; // put back whatever the drawing touched, even if it stopped part way
                if (HandIK.DrawRemote(character, bones, peer.Rig, pose, ++drawFrame))
                    Drawn++;
            }
            catch (Exception e)
            {
                guard.Failed(e, "Drawing another VR player's arms failed");
            }
        }

        /// <summary>After the draw sent the bones to the renderer: the animation's bones back, as the game left them.</summary>
        private static void AfterDraw(MyRenderComponentSkinnedEntity __instance, bool __state)
        {
            if (!__state)
                return;
            try
            {
                if (__instance.Entity is MyCharacter character && byCharacter.TryGetValue(character, out Peer peer))
                    peer.Rig.Restore(character.AnimationController.CharacterBones, character.AnimationController.BoneAbsoluteTransforms);
            }
            catch (Exception e)
            {
                guard.Failed(e, "Putting another VR player's bones back failed");
            }
        }
    }
}
