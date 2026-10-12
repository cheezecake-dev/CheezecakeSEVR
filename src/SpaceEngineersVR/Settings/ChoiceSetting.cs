// ChoiceSetting: a setting that is one of a few named choices.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SpaceEngineersVR
{
    /// <summary>
    /// One of a few named choices, stored in the file by its name. An <c>open</c> one has choices that are only known while the
    /// game runs (the refresh rates a headset offers): <see cref="Offer"/> sets them, and a name read from the file that is
    /// not among them is kept, so a saved value is neither lost nor changed.
    /// </summary>
    internal sealed class ChoiceSetting : Setting
    {
        private volatile string[] choices;
        private readonly bool open;
        private volatile int effective, pending;

        public ChoiceSetting(string key, string label, string section, string[] choices, int defaultValue, string tooltip = null,
            bool restart = false, bool hidden = false, bool open = false)
            : base(key, label, section, tooltip, restart, hidden)
        {
            this.open = open;
            this.choices = (string[])choices.Clone();
            DefaultValue = defaultValue;
            Clear();
        }

        /// <summary>The choices' names, in the order the options screen lists them; a value is an index into it.</summary>
        public string[] Choices => (string[])choices.Clone();

        /// <summary>The name of the choice the plugin uses.</summary>
        public string Name => choices[effective];

        /// <summary>
        /// An open setting's choices: the default's name first, then <paramref name="names"/> in the order given, then the
        /// current value if it is none of them. The value stays the same name.
        /// </summary>
        public void Offer(IEnumerable<string> names)
        {
            string[] old = choices;
            string pendingName = old[pending], effectiveName = old[effective];
            var list = new List<string> { old[DefaultValue] };
            foreach (string name in names)
                if (!list.Contains(name))
                    list.Add(name);
            if (!list.Contains(pendingName))
                list.Add(pendingName);
            if (!list.Contains(effectiveName))
                list.Add(effectiveName);
            choices = list.ToArray();
            pending = list.IndexOf(pendingName);
            effective = list.IndexOf(effectiveName);
        }

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
            if (open && name.Length > 0 && Array.FindIndex(choices, n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) < 0)
                choices = new List<string>(choices) { name }.ToArray();
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
