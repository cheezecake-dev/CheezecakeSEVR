using System.Text;
using Sandbox.Game.Gui;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// Chat: the game's recent chat lines (the HUD's own, MyHud.Chat.Messages), scrolled with a swipe of the fingertip or
    /// the stick; a row of phrases that send at a touch; and the line to type into, which opens the VR keyboard
    /// (<see cref="VRKeyboardHook"/>), with the channel it goes to beside it, touched to change it.
    /// </summary>
    internal sealed class DatapadChatPage : DatapadPage
    {
        /// <summary>The phrases that send at a touch.</summary>
        internal static readonly string[] Phrases = { "On my way", "Need power", "Follow me", "Wait", "Thanks" };

        private const float HistoryScale = 1.15f, InputScale = 1.15f, RowHeight = 0.09f, ChannelWidth = 0.32f, SendWidth = 0.17f;

        /// <summary>The chat's count of lines as the page last showed it.</summary>
        private static int seen = -1;

        private MyGuiControlMultilineText history;
        private MyGuiControlTextbox input;
        private MyGuiControlButton channel;
        private int shownStamp = -1;
        private bool showing;
        private string channelShown;

        public override string Name => "Chat";

        public override bool Marked => !showing && MyHud.Chat != null && seen >= 0 && MyHud.Chat.Timestamp != seen;

        public override void Build(DatapadScreen screen, RectangleF area)
        {
            showing = true;
            Hands.Datapad.ClearUnread();
            float gap = DatapadScreen.Gap;
            float historyHeight = area.Height - 2f * RowHeight - 2f * gap;

            history = new MyGuiControlMultilineText(new Vector2(area.X, area.Y), new Vector2(area.Width, historyHeight), new Vector4(0f, 0f, 0f, 0.35f),
                "White", HistoryScale, MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP, null, drawScrollbarV: true, drawScrollbarH: false,
                MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP)
            {
                Name = "DatapadChatHistory",
            };
            screen.Controls.Add(history);
            shownStamp = -1;
            Refresh();

            // The phrases.
            float y = area.Y + historyHeight + gap;
            float width = (area.Width - gap * (Phrases.Length - 1)) / Phrases.Length;
            bool can = DatapadChat.Available;
            for (int i = 0; i < Phrases.Length; i++)
            {
                string phrase = Phrases[i];
                MyGuiControlButton button = screen.Button(new Vector2(area.X + i * (width + gap), y), new Vector2(width, RowHeight), phrase,
                    DatapadScreen.ButtonScale, () => DatapadChat.Send(phrase, "phrase button"));
                button.Name = "DatapadPhrase" + i;
                button.Enabled = can;
            }

            // The channel, the line to type into, and Send.
            y += RowHeight + gap;
            channelShown = DatapadChat.ChannelName();
            channel = screen.Button(new Vector2(area.X, y), new Vector2(ChannelWidth, RowHeight), channelShown, DatapadScreen.ButtonScale,
                DatapadChat.NextChannel);
            channel.Name = "DatapadChannel";
            channel.ColorMask = DatapadChat.ChannelColor();

            float inputWidth = area.Width - ChannelWidth - SendWidth - 2f * gap;
            input = new MyGuiControlTextbox(new Vector2(area.X + ChannelWidth + gap, y), null, DatapadChat.MaxLength, null, InputScale)
            {
                Name = "DatapadChatInput",
                OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
                Size = new Vector2(inputWidth, RowHeight),
                Enabled = can,
            };
            // The keyboard's Done (and Enter on a real one) sends.
            input.EnterPressed += box => DatapadScreen.Guard("send", () => SendInput("keyboard"));
            screen.Controls.Add(input);

            MyGuiControlButton send = screen.Button(new Vector2(area.X + area.Width - SendWidth, y), new Vector2(SendWidth, RowHeight), "Send",
                DatapadScreen.ButtonScale, () => SendInput("Send button"));
            send.Name = "DatapadSend";
            send.Enabled = can;

            if (!can)
            {
                screen.Controls.Add(new MyGuiControlLabel(new Vector2(area.X, y - gap / 2f), null, "Chat is restricted on this platform", Color.Red.ToVector4(),
                    0.9f, "White", MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_BOTTOM));
            }
        }

        private void SendInput(string how)
        {
            if (input == null || !DatapadChat.Send(input.Text, how))
                return;
            input.Text = string.Empty;
        }

        public override void Update(DatapadScreen screen)
        {
            Refresh();
            string name = DatapadChat.ChannelName();
            if (channel != null && name != channelShown)
            {
                channelShown = name;
                channel.Text = name;
                channel.ColorMask = DatapadChat.ChannelColor();
            }
        }

        public override void Hidden()
        {
            showing = false;
            history = null;
            input = null;
            channel = null;
        }

        /// <summary>The history again when the chat has a new line: it stays at the bottom if it was there.</summary>
        private void Refresh()
        {
            MyHudChat chat = MyHud.Chat;
            if (history == null || chat == null || chat.Timestamp == shownStamp)
                return;
            shownStamp = seen = chat.Timestamp;
            MyVScrollbar bar = history.ScrollbarV;
            bool atBottom = bar == null || !bar.Visible || bar.Value >= bar.MaxSize - bar.PageSize - 0.01f;

            history.Clear();
            if (chat.Messages.Count == 0)
                history.AppendText("No messages yet.", "White", HistoryScale, DatapadScreen.DimColor);
            bool first = true;
            var line = new StringBuilder();
            foreach (MyChatItem item in chat.Messages)
            {
                if (!first)
                    history.AppendLine();
                first = false;
                if (!string.IsNullOrEmpty(item.Sender))
                    history.AppendText(line.Clear().Append(item.Sender).Append(": "), "White", HistoryScale, item.SenderColor.ToVector4());
                history.AppendText(line.Clear().Append(item.Message), "White", HistoryScale, item.MessageColor.ToVector4());
            }
            history.RecalculateScrollBar();
            if (atBottom && history.ScrollbarV != null)
                history.ScrollbarOffsetV = history.ScrollbarV.MaxSize;
        }
    }
}
