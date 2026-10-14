using System.Diagnostics;
using Sandbox.Game.Gui;
using Sandbox.Game.World;

namespace SpaceEngineersVR.Hands
{
    /// <summary>The new-message mark on the wrist panel's Datapad tile, and the tick that goes with it.</summary>
    internal static partial class Datapad
    {
        /// <summary>A line this soon after one was sent from the pad is the player's own, echoed back.</summary>
        private const double EchoSeconds = 3.0;

        private static int seenTimestamp = -1;
        private static volatile bool unread;
        private static long sentAt;

        /// <summary>A chat line came in while the pad was away and it has not been looked at.</summary>
        internal static bool Unread => unread;

        internal static void ClearUnread() => unread = false;

        /// <summary>From the chat page as it sends: the line that comes back is not news.</summary>
        internal static void Sent() => sentAt = Stopwatch.GetTimestamp();

        /// <summary>Game thread: a new chat line while the pad is away marks the tile and ticks the hand it comes out into.</summary>
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
            if (Off || phase != Phase.Away || unread || Seconds(sentAt) < EchoSeconds || FromMe(chat))
                return;
            unread = true;
            Haptics.Pulse(OutHand, TickAmplitude, TickSeconds);
            Log.Info("Datapad: a chat line came in while it was away; the wrist panel's Datapad tile shows it");
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
    }
}
