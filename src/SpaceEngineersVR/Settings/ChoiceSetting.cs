// ChoiceSetting: a setting that is one of a few named choices.
using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace SpaceEngineersVR
{
    /// <summary>One of a few named choices, stored in the file by its name.</summary>
    internal sealed class ChoiceSetting : Setting
    {
        private readonly string[] choices;
        private volatile int effective, pending;

        public ChoiceSetting(string key, string label, string section, string[] choices, int defaultValue, string tooltip = null,
            bool restart = false, bool hidden = false)
            : base(key, label, section, tooltip, restart, hidden)
        {
            this.choices = (string[])choices.Clone();
            DefaultValue = defaultValue;
            Clear();
        }

        /// <summary>The choices' names, in the order the options screen lists them; a value is an index into it.</summary>
        public string[] Choices => (string[])choices.Clone();

        public int DefaultValue { get; }

        /// <summary>What the plugin uses.</summary>
        public int Value => effective;

        /// <summary>What the options screen shows and the file holds.</summary>
        public int Pending => pending;

        /// <summary>The new choice; one that is not in the list is ignored.</summary>
        public void Set(int value)
        {
            if (value < 0 || value >= choices.Length)
                return;
            pending = value;
            if (!Restart)
                effective = value;
        }

        private void Force(int value)
        {
            pending = value;
            effective = value;
        }

        public override void ResetToDefault() => Set(DefaultValue);

        public override object Snapshot() => pending;

        public override void Restore(object snapshot) => Set((int)snapshot);

        internal override void Clear() => Force(DefaultValue);

        internal override string Serialize() => choices[pending];

        internal override bool TryLoad(string text, out string problem)
        {
            problem = null;
            string name = (text ?? string.Empty).Trim();
            for (int i = 0; i < choices.Length; i++)
            {
                if (string.Equals(choices[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    Force(i);
                    return true;
                }
            }
            problem = $"'{text}' is not one of {string.Join(", ", choices)}";
            return false;
        }

        internal override string Describe() =>
            $"{Label}: {string.Join(" or ", choices)}, default {choices[DefaultValue]}{(Restart ? ", takes effect on restart" : string.Empty)}";
    }
}
