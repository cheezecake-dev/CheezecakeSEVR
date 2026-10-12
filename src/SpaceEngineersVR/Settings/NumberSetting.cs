// NumberSetting: a numeric setting in a range, moved in steps.
using System;
using System.Globalization;
using System.Text;

namespace SpaceEngineersVR
{
    /// <summary>A number, in a range, moved in steps. Stored as a float; <see cref="Integer"/> ones are whole.</summary>
    internal sealed class NumberSetting : Setting
    {
        private float effective, pending;
        private bool effectiveSet, pendingSet;

        /// <param name="optional">May be unset (null): <see cref="DefaultValue"/> is then null too.</param>
        /// <param name="displayScale">What the options screen multiplies the value by to show it (metres as millimetres).</param>
        public NumberSetting(string key, string label, string section, float? defaultValue, float min, float max, float step,
            string unit = "", int decimals = 2, float displayScale = 1f, string tooltip = null,
            bool restart = false, bool hidden = false, bool optional = false, bool integer = false)
            : base(key, label, section, tooltip, restart, hidden)
        {
            DefaultValue = defaultValue;
            Min = min;
            Max = max;
            Step = step;
            Unit = unit ?? string.Empty;
            Decimals = decimals;
            DisplayScale = displayScale;
            Optional = optional;
            Integer = integer;
            Clear();
        }

        public float? DefaultValue { get; }

        public float Min { get; }

        public float Max { get; }

        public float Step { get; }

        /// <summary>Shown after the value in the options screen ("m", "deg", "px").</summary>
        public string Unit { get; }

        public int Decimals { get; }

        public float DisplayScale { get; }

        public bool Optional { get; }

        public bool Integer { get; }

        /// <summary>What the plugin uses. For a setting that is <see cref="Optional"/> and unset: the default, or the minimum.</summary>
        public float Value => effectiveSet ? effective : DefaultValue ?? Min;

        /// <summary>What the plugin uses; null when unset.</summary>
        public float? Effective => effectiveSet ? effective : (float?)null;

        /// <summary>What the options screen shows and the file holds; null when unset.</summary>
        public float? Pending => pendingSet ? pending : (float?)null;

        /// <summary>
        /// The new value, from the options screen or from code: not range-checked (the screen's slider keeps within
        /// the range, and code knows what it measured). Null unsets an optional setting; a number that is not a
        /// number is ignored.
        /// </summary>
        public void Set(float? value)
        {
            if (!Accept(ref value))
                return;
            Assign(ref pending, ref pendingSet, value);
            if (!Restart)
                Assign(ref effective, ref effectiveSet, value);
        }

        /// <summary>Sets both values, for the command line.</summary>
        public void Force(float? value)
        {
            if (!Accept(ref value))
                return;
            Assign(ref pending, ref pendingSet, value);
            Assign(ref effective, ref effectiveSet, value);
        }

        private bool Accept(ref float? value)
        {
            if (!value.HasValue)
                return Optional;
            if (float.IsNaN(value.Value) || float.IsInfinity(value.Value))
                return false;
            if (Integer)
                value = (float)Math.Round(value.Value);
            return true;
        }

        private static void Assign(ref float target, ref bool targetSet, float? value)
        {
            // The value first, then whether there is one: another thread that sees "set" sees a value that goes with it.
            if (value.HasValue)
                target = value.Value;
            targetSet = value.HasValue;
        }

        public override void ResetToDefault() => Set(DefaultValue);

        public override object Snapshot() => Pending;

        public override void Restore(object snapshot) => Set((float?)snapshot);

        internal override void Clear() => Force(DefaultValue);

        internal override string Serialize()
        {
            if (!pendingSet)
                return string.Empty;
            return Integer
                ? ((long)Math.Round(pending)).ToString(CultureInfo.InvariantCulture)
                : pending.ToString("R", CultureInfo.InvariantCulture);
        }

        internal override bool TryLoad(string text, out string problem)
        {
            problem = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                if (Optional)
                {
                    Force(null);
                    return true;
                }
                problem = "no value";
                return false;
            }
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || float.IsNaN(value) || float.IsInfinity(value))
            {
                problem = $"'{text}' is not a number";
                return false;
            }
            if (value < Min || value > Max)
            {
                problem = $"{Format(value)} is outside {Format(Min)} to {Format(Max)}";
                return false;
            }
            if (Integer && value != Math.Round(value))
            {
                problem = $"'{text}' is not a whole number";
                return false;
            }
            Force(value);
            return true;
        }

        internal override string Describe()
        {
            string unit = Unit.Length > 0 ? " (" + Unit + ")" : string.Empty;
            string restart = Restart ? ", takes effect on restart" : string.Empty;
            string def = DefaultValue.HasValue ? Format(DefaultValue.Value) : "none";
            return $"{Label}{unit}: {Format(Min)} to {Format(Max)}, default {def}{restart}";
        }

        private static string Format(float value) => value.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
