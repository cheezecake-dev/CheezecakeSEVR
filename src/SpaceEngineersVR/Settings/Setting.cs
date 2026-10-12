// Setting: the base class of a declared setting - key, label, tooltip and the pending/effective values.
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SpaceEngineersVR
{
    /// <summary>
    /// One setting. Declared once, in <see cref="VRSettings"/>; the settings file and the options screen
    /// (Gui/VROptionsScreen.cs) are both generated from the list, so a new setting is one declaration there, and one line
    /// in Gui/VROptionsTabs.cs for the tab it is shown on.
    /// A setting has two values. The effective one is what the rest of the plugin reads. The pending one is what is
    /// shown, edited and saved. For a setting that applies live they are always the same. A setting that applies on
    /// restart (<see cref="Restart"/>) keeps its effective value as it was at start-up, so changing it in the menu
    /// cannot disturb the run in progress (the mirror window's size, for one, is compared against all the time).
    /// </summary>
    internal abstract class Setting
    {
        protected Setting(string key, string label, string section, string tooltip, bool restart, bool hidden)
        {
            Key = key;
            Label = label;
            Section = section;
            Tooltip = tooltip ?? string.Empty;
            Restart = restart;
            Hidden = hidden;
        }

        /// <summary>The name in the settings file.</summary>
        public string Key { get; }

        /// <summary>The name in the options screen.</summary>
        public string Label { get; }

        /// <summary>
        /// The settings file groups settings under this caption, in the order the sections first appear. The options screen
        /// places them by Gui/VROptionsTabs.cs, and uses this only for one that has no line there.
        /// </summary>
        public string Section { get; }

        public string Tooltip { get; }

        /// <summary>The change only takes effect the next time the game starts.</summary>
        public bool Restart { get; }

        /// <summary>Kept in the file but not shown in the options screen (the height calibration sets it).</summary>
        public bool Hidden { get; }

        /// <summary>Sets the pending value (and the effective one, unless <see cref="Restart"/>) to the default.</summary>
        public abstract void ResetToDefault();

        /// <summary>The pending value, to hand back to <see cref="Restore"/>.</summary>
        public abstract object Snapshot();

        public abstract void Restore(object snapshot);

        /// <summary>Both values back to the default. Used when the settings are loaded.</summary>
        internal abstract void Clear();

        /// <summary>The pending value as it is written to the file; empty for none.</summary>
        internal abstract string Serialize();

        /// <summary>Reads a value from the file into both values. False, with the reason, when the text is not a valid value.</summary>
        internal abstract bool TryLoad(string text, out string problem);

        /// <summary>One line for the comment above the setting in the file.</summary>
        internal abstract string Describe();
    }
}
