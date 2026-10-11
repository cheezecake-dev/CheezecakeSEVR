using System;
using System.Collections.Generic;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// Where each setting is in Options > VR (<see cref="VROptionsScreen"/>): its tab, and the group it sits under in that tab.
    /// One line a setting, in <see cref="Places"/>. Tabs come in the order of their first line, the groups in a tab likewise,
    /// and the settings in a group in the order of their lines. A new setting on the screen is its declaration in
    /// <see cref="VRSettings"/> and its line here.
    /// </summary>
    /// <remarks>
    /// A shown setting with no line here still appears: on the tab its section goes to (<see cref="SectionTabs"/>, or a tab
    /// of the section's name), under its section's name, after the settings placed here; the log names it the first time
    /// the screen is made. A line whose key no setting has is logged and skipped, and a second line for a setting is ignored.
    /// Hidden settings are never shown. Where a setting is shown has nothing to do with the settings file: its key, and the
    /// file's own grouping by section, stay as they are.
    /// </remarks>
    internal static class VROptionsTabs
    {
        private const string Display = "Display", Comfort = "Comfort", Controls = "Controls", Hands = "Hands", Interface = "Interface";

        /// <summary>The tab, the group in it, and the setting's key (its name in VRSettings and the settings file).</summary>
        private static readonly (string Tab, string Group, string Key)[] Places =
        {
            (Display,   "Play position",  "playPosition"),
            (Display,   "Play position",  "heightOffset"),
            (Display,   "Play position",  "crouchWithHead"),
            (Display,   "External view",  "externalViewSize"),
            (Display,   "External view",  "externalViewHalfRate"),
            (Display,   "External view",  "moveViewByHand"),
            (Display,   "Headset",        "dynamicResolution"),
            (Display,   "Headset",        "headsetAudio"),
            (Display,   "Headset",        "ipd"),
            (Display,   "Third person",   "thirdPerson"),
            (Display,   "Third person",   "levelChaseCamera"),
            (Display,   "Desktop window", "mirrorWidth"),
            (Display,   "Desktop window", "mirrorHeight"),

            (Comfort,   "Turning",        "snapTurn"),
            (Comfort,   "Turning",        "smoothTurn"),
            (Comfort,   "Turning",        "smoothTurnSpeed"),
            (Comfort,   "Turning",        "zeroGTurn"),
            (Comfort,   "Motion",         "comfortVignette"),
            (Comfort,   "Motion",         "cameraShake"),
            (Comfort,   "Head",           "headLock"),
            (Comfort,   "Head",           "headReach"),
            (Comfort,   "Head",           "bodyClearance"),
            (Comfort,   "Head",           "seatReach"),

            (Controls,  "Controllers",    "leftHanded"),
            (Controls,  "Controllers",    "stickDeadzone"),
            (Controls,  "Controllers",    "shipStickSensitivity"),
            (Controls,  "Piloting",       "handsOnControls"),
            (Controls,  "Hand flying",    "handFlying"),
            (Controls,  "Hand flying",    "stickMaxAngle"),
            (Controls,  "Hand flying",    "throttleTravel"),
            (Controls,  "Hand flying",    "handFlyingSensitivity"),

            (Hands,     "Grab",           "grabToUse"),
            (Hands,     "Grab",           "grabItems"),
            (Hands,     "Grab",           "grabHulls"),
            (Hands,     "Grab",           "grabLadders"),
            (Hands,     "Touch",          "fingerPress"),
            (Hands,     "Touch",          "helmetAndLamp"),
            (Hands,     "Tools",          "twoHandedTools"),
            (Hands,     "Tools",          "twoHandGripStays"),
            (Hands,     "Tools",          "kickback"),
            (Hands,     "Haptics",        "hapticStrength"),

            (Interface, "Interface parts", "wristStatus"),
            (Interface, "Interface parts", "showToolbarOnFoot"),
            (Interface, "Interface parts", "controlHints"),
            (Interface, "Interface parts", "externalView"),
            (Interface, "HUD",            "hudDistance"),
            (Interface, "HUD",            "hudWidth"),
            (Interface, "Menus",          "menuDistance"),
            (Interface, "Menus",          "menuWidth"),
            (Interface, "Menus",          "mainMenuWidth"),
            (Interface, "Menus",          "vrKeyboard"),
            (Interface, "Wrist panel",    "wristPanel"),
            (Interface, "Prompts",        "vrButtonNames"),
            (Interface, "Wheels",         "vrRadialMenu"),
            (Interface, "Wheels",         "vrActionsWheel"),
            (Interface, "Cockpit",        "touchToolbar"),
        };

        /// <summary>The tab for a setting with no line in <see cref="Places"/>, by its section.</summary>
        private static readonly Dictionary<string, string> SectionTabs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Display"] = Display,
            ["Height"] = Display,
            ["Sound"] = Display,
            ["Advanced"] = Display,
            ["Comfort"] = Comfort,
            ["Controls"] = Controls,
            ["Hands"] = Hands,
            ["Menus"] = Interface,
            ["Interface"] = Interface,
        };

        private static bool reported;

        /// <summary>A caption in a tab, and the settings under it.</summary>
        internal sealed class Group
        {
            public Group(string name) => Name = name;

            public string Name { get; }

            public List<Setting> Rows { get; } = new List<Setting>();
        }

        /// <summary>A tab, and its groups.</summary>
        internal sealed class Tab
        {
            public Tab(string name) => Name = name;

            public string Name { get; }

            public List<Group> Groups { get; } = new List<Group>();

            public Group GetGroup(string name)
            {
                foreach (Group group in Groups)
                {
                    if (string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase))
                        return group;
                }
                var added = new Group(name);
                Groups.Add(added);
                return added;
            }
        }

        /// <summary>The shown settings in their tabs and groups, in screen order. Never empty while a setting is shown.</summary>
        public static List<Tab> Arrange(IReadOnlyList<Setting> settings)
        {
            var byKey = new Dictionary<string, Setting>(StringComparer.OrdinalIgnoreCase);
            foreach (Setting setting in settings)
            {
                if (!byKey.ContainsKey(setting.Key))
                    byKey.Add(setting.Key, setting);
            }

            var tabs = new List<Tab>();
            var placed = new HashSet<Setting>();
            var unknown = new List<string>();
            var twice = new List<string>();
            foreach ((string tab, string group, string key) in Places)
            {
                if (!byKey.TryGetValue(key, out Setting setting))
                {
                    unknown.Add(key);
                    continue;
                }
                if (!placed.Add(setting))
                {
                    twice.Add(key);
                    continue;
                }
                if (!setting.Hidden)
                    GetTab(tabs, tab).GetGroup(group).Rows.Add(setting);
            }

            var unplaced = new List<string>();
            foreach (Setting setting in settings)
            {
                if (setting.Hidden || placed.Contains(setting))
                    continue;
                string tab = SectionTabs.TryGetValue(setting.Section, out string mapped) ? mapped : setting.Section;
                GetTab(tabs, tab).GetGroup(setting.Section).Rows.Add(setting);
                unplaced.Add(setting.Key);
            }

            if (!reported)
            {
                reported = true;
                if (unplaced.Count > 0)
                    Log.Warn("VR options: no tab line in Gui/VROptionsTabs.cs for " + string.Join(", ", unplaced) + "; shown on their section's tab");
                if (unknown.Count > 0)
                    Log.Warn("VR options: Gui/VROptionsTabs.cs places " + string.Join(", ", unknown) + ", which no setting has; skipped");
                if (twice.Count > 0)
                    Log.Warn("VR options: Gui/VROptionsTabs.cs places " + string.Join(", ", twice) + " more than once; the first line wins");
            }
            return tabs;
        }

        private static Tab GetTab(List<Tab> tabs, string name)
        {
            foreach (Tab tab in tabs)
            {
                if (string.Equals(tab.Name, name, StringComparison.OrdinalIgnoreCase))
                    return tab;
            }
            var added = new Tab(name);
            tabs.Add(added);
            return added;
        }
    }
}
