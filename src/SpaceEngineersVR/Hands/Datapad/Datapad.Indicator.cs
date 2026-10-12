using System;
using System.Diagnostics;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Rendering;
using SpaceEngineersVR.Tracking;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>The new-message light on the forearm, and the tick that goes with it.</summary>
    internal static partial class Datapad
    {
        private static readonly MyStringId GlowMaterial = MyStringId.GetOrCompute("WhiteDot");

        /// <summary>The light's colour: the game's chat blue.</summary>
        private static readonly Vector4 GlowColor = new Vector4(0.35f, 0.75f, 1f, 1f);

        /// <summary>The light's size, metres, and how fast it breathes, Hz.</summary>
        private const float GlowRadius = 0.008f, GlowHertz = 0.8f;

        /// <summary>A line this soon after one was sent from the pad is the player's own, echoed back.</summary>
        private const double EchoSeconds = 3.0;

        private static int seenTimestamp = -1;
        private static volatile bool unread;
        private static long sentAt;

        /// <summary>A chat line came in while the pad was stowed and it has not been looked at.</summary>
        internal static bool Unread => unread;

        internal static void ClearUnread() => unread = false;

        /// <summary>From the chat page as it sends: the line that comes back is not news.</summary>
        internal static void Sent() => sentAt = Stopwatch.GetTimestamp();

        /// <summary>Game thread: a new chat line while stowed lights the forearm and ticks the hand it is on.</summary>
        private static void UpdateIndicator()
        {
            // MyHud.Chat throws until the HUD exists: at the main menu and while a world loads
            MyHudChat chat = MyHud.Static != null ? MyHud.Chat : null;
            if (chat == null || MySession.Static == null)
            {
                seenTimestamp = -1;
                unread = false;
                return;
            }
            int timestamp = chat.Timestamp;
            if (seenTimestamp < 0 || timestamp == seenTimestamp)
            {
                seenTimestamp = timestamp;
                return;
            }
            seenTimestamp = timestamp;
            if (!Enabled || phase != Phase.Stowed || unread || Seconds(sentAt) < EchoSeconds || FromMe(chat))
                return;
            unread = true;
            Haptics.Pulse(StowHand, TickAmplitude, TickSeconds);
            Log.Info("Datapad: a chat line came in while stowed; the forearm lights");
        }

        /// <summary>The newest line is the local player's own.</summary>
        private static bool FromMe(MyHudChat chat)
        {
            MyPlayer me = MySession.Static.LocalHumanPlayer;
            if (me == null || chat.Messages.Count == 0)
                return false;
            string sender = null;
            foreach (MyChatItem item in chat.Messages)
                sender = item.Sender;
            return sender == me.DisplayName || sender == me.PlatformDisplayName;
        }

        // Game thread, MySession.DrawSync postfix: the light where the stowed pad is, breathing.
        private static void DrawIndicator()
        {
            if (!unread || phase != Phase.Stowed || !Enabled || !guard.On)
                return;
            try
            {
                if (!gripsTracked[(int)StowHand] || !HandWorld.TryGetTrackingToWorld(out MatrixD toWorld))
                    return;
                Vector3D at = Vector3D.Transform((Vector3D)ForearmPoint(grips[(int)StowHand]), toWorld);
                double t = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                float breath = 0.55f + 0.45f * (float)Math.Sin(t * GlowHertz * 2.0 * Math.PI);
                MotionExtrapolation.AddPoint(GlowMaterial, GlowColor * breath, at, GlowRadius);
            }
            catch (Exception e)
            {
                guard.Failed(e, "Datapad light failed this frame");
            }
        }
    }
}
