// TextSetting: a one-line text setting, escaped in the settings file.
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SpaceEngineersVR
{
    /// <summary>
    /// A line of text. Written to the file with the characters that would break a line or start a comment escaped
    /// (<c>%</c>, <c>#</c>, line breaks), so any name survives the round trip.
    /// </summary>
    internal sealed class TextSetting : Setting
    {
        private volatile string effective = string.Empty, pending = string.Empty;

        public TextSetting(string key, string label, string section, string tooltip = null, bool restart = false, bool hidden = false)
            : base(key, label, section, tooltip, restart, hidden)
        {
            Clear();
        }

        /// <summary>What the plugin uses; never null.</summary>
        public string Value => effective;

        /// <summary>What the options screen shows and the file holds; never null.</summary>
        public string Pending => pending;

        public void Set(string value)
        {
            pending = value ?? string.Empty;
            if (!Restart)
                effective = pending;
        }

        public override void ResetToDefault() => Set(string.Empty);

        public override object Snapshot() => pending;

        public override void Restore(object snapshot) => Set((string)snapshot);

        internal override void Clear()
        {
            pending = string.Empty;
            effective = string.Empty;
        }

        internal override string Serialize() => Escape(pending);

        internal override bool TryLoad(string text, out string problem)
        {
            problem = null;
            string value = Unescape(text ?? string.Empty);
            pending = value;
            effective = value;
            return true;
        }

        internal override string Describe() => $"{Label}: text, default none{(Restart ? ", takes effect on restart" : string.Empty)}";

        internal static string Escape(string text) =>
            text.Replace("%", "%25").Replace("#", "%23").Replace("\r", "%0D").Replace("\n", "%0A");

        internal static string Unescape(string text) =>
            text.Replace("%0A", "\n").Replace("%0D", "\r").Replace("%23", "#").Replace("%25", "%");
    }
}
