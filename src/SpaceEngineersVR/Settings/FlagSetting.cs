// FlagSetting: an on/off setting.
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SpaceEngineersVR
{
    /// <summary>An on/off switch.</summary>
    internal sealed class FlagSetting : Setting
    {
        private volatile bool effective, pending;

        public FlagSetting(string key, string label, string section, bool defaultValue, string tooltip = null, bool restart = false, bool hidden = false)
            : base(key, label, section, tooltip, restart, hidden)
        {
            DefaultValue = defaultValue;
            Clear();
        }

        public bool DefaultValue { get; }

        /// <summary>What the plugin uses.</summary>
        public bool Value => effective;

        /// <summary>What the options screen shows and the file holds.</summary>
        public bool Pending => pending;

        public void Set(bool value)
        {
            pending = value;
            if (!Restart)
                effective = value;
        }

        /// <summary>Sets both values, for the command line.</summary>
        public void Force(bool value)
        {
            pending = value;
            effective = value;
        }

        public override void ResetToDefault() => Set(DefaultValue);

        public override object Snapshot() => pending;

        public override void Restore(object snapshot) => Set((bool)snapshot);

        internal override void Clear() => Force(DefaultValue);

        internal override string Serialize() => pending ? "true" : "false";

        internal override bool TryLoad(string text, out string problem)
        {
            problem = null;
            switch ((text ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "true":
                case "1":
                case "yes":
                case "on":
                    Force(true);
                    return true;
                case "false":
                case "0":
                case "no":
                case "off":
                    Force(false);
                    return true;
                default:
                    problem = $"'{text}' is not true or false";
                    return false;
            }
        }

        internal override string Describe() =>
            $"{Label}: true or false, default {(DefaultValue ? "true" : "false")}{(Restart ? ", takes effect on restart" : string.Empty)}";
    }
}
