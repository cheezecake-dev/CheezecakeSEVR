using System;
using System.Collections.Generic;
using HarmonyLib;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Gui;
using Sandbox.Game.GameSystems.Chat;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.GameServices;
using Sandbox.Engine.Networking;
using VRageMath;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// Sending chat and choosing its channel as the game's chat line does (MyGuiScreenChat.OnInputFieldActivated and
    /// SendChatMessage, which are the game's own and not reachable from here): a chat command is run, anything else goes to
    /// the mods' message handlers and then to the channel the game has chosen. The channel is the game's
    /// (MyChatSystem.CurrentChannel), so a change here is the desktop chat's too.
    /// </summary>
    internal static class DatapadChat
    {
        /// <summary>Text chat is allowed (the platform can restrict it, as the game's chat line shows).</summary>
        internal static bool Available
        {
            get
            {
                MyMultiplayerBase multiplayer = MyMultiplayer.Static;
                return MySession.Static?.ChatSystem != null && (multiplayer == null || multiplayer.IsTextChatAvailable);
            }
        }

        /// <summary>
        /// The longest message the platform takes: the game's own question (MyGameService.GetChatMaxMessageSize, internal), or
        /// its fallback when that cannot be asked.
        /// </summary>
        internal static int MaxLength
        {
            get
            {
                if (maxLength == null)
                {
                    try
                    {
                        maxLength = AccessTools.MethodDelegate<Func<int>>(AccessTools.Method(typeof(MyGameService), "GetChatMaxMessageSize"));
                    }
                    catch (Exception e)
                    {
                        Log.Error(e, "Datapad: the chat's length limit could not be asked; using the game's fallback");
                        maxLength = () => MyGameServiceConstants.MAX_CHAT_MESSAGE_SIZE;
                    }
                }
                return maxLength();
            }
        }

        private static Func<int> maxLength;

        /// <summary>Sends <paramref name="text"/> as the game's chat line would. False when there was nothing to send.</summary>
        internal static bool Send(string text, string how)
        {
            MySession session = MySession.Static;
            if (session?.ChatSystem == null || string.IsNullOrWhiteSpace(text) || !Available)
                return false;
            Hands.Datapad.Sent();
            if (session.ChatSystem.CommandSystem.CanHandle(text))
            {
                MyHud.Chat.ShowMessage(session.LocalHumanPlayer?.PlatformDisplayName ?? string.Empty, text);
                session.ChatSystem.CommandSystem.Handle(text);
                Log.Info($"Datapad: chat command from the {how}");
                return true;
            }
            bool toOthers = true;
            MyAPIUtilities.Static.EnterMessage(session.LocalHumanPlayer?.Id.SteamId ?? 0, text, ref toOthers);
            if (toOthers)
            {
                ChatChannel channel = session.ChatSystem.CurrentChannel;
                long target = session.ChatSystem.CurrentTarget;
                if (MyMultiplayer.Static != null)
                    MyMultiplayer.Static.SendChatMessage(text, channel, target);
                else if (MyGameService.IsActive)
                    MyHud.Chat.ShowMessageColoredSP(text, channel, target);
                else
                    MyHud.Chat.ShowMessage(session.LocalHumanPlayer?.PlatformDisplayName ?? string.Empty, text);
            }
            Log.Info($"Datapad: sent {text.Length} characters to {ChannelName()} from the {how}{(toOthers ? string.Empty : " (a mod kept it)")}");
            return true;
        }

        /// <summary>The channel chat goes to now, in a few words.</summary>
        internal static string ChannelName()
        {
            MySession session = MySession.Static;
            if (session?.ChatSystem == null)
                return "Chat";
            switch (session.ChatSystem.CurrentChannel)
            {
                case ChatChannel.Global:
                    return "Global";
                case ChatChannel.Faction:
                    IMyFaction faction = session.Factions.TryGetPlayerFaction(session.LocalPlayerId);
                    return faction != null ? "Faction [" + faction.Tag + "]" : "Faction";
                case ChatChannel.Private:
                    MyIdentity identity = session.Players.TryGetIdentity(session.ChatSystem.CurrentTarget);
                    return "To " + (identity?.DisplayName ?? "player");
                default:
                    return session.ChatSystem.CurrentChannel.ToString();
            }
        }

        internal static Vector4 ChannelColor()
        {
            MySession session = MySession.Static;
            return session?.ChatSystem == null ? Vector4.One : MyChatSystem.GetChannelColor(session.ChatSystem.CurrentChannel).ToVector4();
        }

        /// <summary>The next channel along: global, the faction (if the player has one), then each other player online, privately.</summary>
        internal static void NextChannel()
        {
            MySession session = MySession.Static;
            if (session?.ChatSystem == null)
                return;
            var stops = new List<long>();
            bool faction = session.Factions.TryGetPlayerFaction(session.LocalPlayerId) != null;
            foreach (MyPlayer player in session.Players.GetOnlinePlayers())
            {
                long id = player.Identity?.IdentityId ?? 0;
                if (player.Id.SerialId == 0 && id != 0 && id != session.LocalPlayerId && !stops.Contains(id))
                    stops.Add(id);
            }
            stops.Sort();

            MyChatSystem chat = session.ChatSystem;
            switch (chat.CurrentChannel)
            {
                case ChatChannel.Global:
                    if (faction)
                        chat.ChangeChatChannel_Faction();
                    else if (stops.Count > 0)
                        chat.ChangeChatChannel_Whisper(stops[0]);
                    break;
                case ChatChannel.Faction:
                    if (stops.Count > 0)
                        chat.ChangeChatChannel_Whisper(stops[0]);
                    else
                        chat.ChangeChatChannel_Global();
                    break;
                case ChatChannel.Private:
                    int at = stops.IndexOf(chat.CurrentTarget);
                    if (at >= 0 && at + 1 < stops.Count)
                        chat.ChangeChatChannel_Whisper(stops[at + 1]);
                    else
                        chat.ChangeChatChannel_Global();
                    break;
                default:
                    chat.ChangeChatChannel_Global();
                    break;
            }
            Log.Info($"Datapad: chat channel now {ChannelName()}");
        }
    }
}
