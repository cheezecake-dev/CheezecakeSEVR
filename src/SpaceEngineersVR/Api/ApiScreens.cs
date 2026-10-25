// ApiScreens: how other plugins' GUI screens are shown on the VR panel when the game's rule alone does not do (no game types; tests/ApiTests runs it).
using System;
using System.Collections.Generic;

namespace SpaceEngineersVR.Api
{
    /// <summary>What a registered screen needs on the VR panel (<see cref="ApiScreens"/>); several together.</summary>
    [Flags]
    internal enum PanelScreenOptions
    {
        None = 0,

        /// <summary>
        /// "Cursor": the screen hides the game's mouse cursor (MyGuiScreenBase.DrawMouseCursor false) yet is used by pointing:
        /// while it has the focus in VR the cursor is shown, so the laser points and clicks on it.
        /// </summary>
        Cursor = 1,

        /// <summary>
        /// "AtHand": a short-lived selector (a wheel, a picker): the panel stands at the off hand as the screen opens, fixed there
        /// facing the head until the screen loses the focus, as the plugin's own wheels do, instead of ahead of the player.
        /// </summary>
        AtHand = 2,

        /// <summary>
        /// "WalkBeside": the player keeps walking, turning and jumping on the sticks while the screen has the focus, as on the
        /// wrist panel; the trigger, grips, B and Menu stay the screen's. On foot and in the jetpack only.
        /// </summary>
        WalkBeside = 4,
    }

    /// <summary>
    /// The panel screens registered through SevrApi version 2 (<c>RegisterPanelScreen</c>, entry point 5), by the screen type's
    /// full name. Every GUI screen that takes the focus is already on the VR panel, and one that shows the game's cursor is
    /// already used with the laser; a registration is for what that rule does not cover (<see cref="PanelScreenOptions"/>).
    /// <list type="bullet">
    /// <item>A type matches exactly (not its subclasses): register each screen type that needs it.</item>
    /// <item>The game's screens and the plugin's own cannot be registered: how they are shown is the plugin's to decide, the same
    /// for every player.</item>
    /// <item>Two owners registering one type: the first by owner id (ordinal) is used, so load order never decides it.</item>
    /// <item>Registering the same owner and type again replaces its options.</item>
    /// </list>
    /// </summary>
    internal sealed class ApiScreens : IApiRegistry
    {
        /// <summary>The longest type name taken.</summary>
        public const int MaxTypeName = 512;

        /// <summary>The most screen types one owner may register.</summary>
        public const int MaxPerOwner = 32;

        /// <summary>The options' public names, as a registration gives them, in the order of their flags.</summary>
        public static readonly string[] OptionNames = { "Cursor", "AtHand", "WalkBeside" };

        /// <summary>Namespaces whose screens cannot be registered: the game's and the plugin's own.</summary>
        public static readonly string[] Reserved = { "Sandbox.", "SpaceEngineers.Game.", "VRage.", "SpaceEngineersVR." };

        /// <summary>One registration's options (the list holds reference types).</summary>
        private sealed class Registration
        {
            public readonly PanelScreenOptions Options;

            public Registration(PanelScreenOptions options) => Options = options;
        }

        private readonly ApiCore core;
        private readonly ApiList<Registration> list = new ApiList<Registration>();
        private Dictionary<string, PanelScreenOptions> byType = new Dictionary<string, PanelScreenOptions>(StringComparer.Ordinal);
        private int builtFor = -1;

        public ApiScreens(ApiCore core)
        {
            this.core = core ?? throw new ArgumentNullException(nameof(core));
            core.Add(this);
        }

        /// <summary>Changes each time a screen is registered, replaced or taken away.</summary>
        public int Version { get; private set; }

        /// <summary>
        /// Registers (or replaces) <paramref name="owner"/>'s options for the screen type <paramref name="typeFullName"/> (its
        /// <c>Type.FullName</c>). <paramref name="options"/>: the option names, separated by commas ("Cursor, AtHand"); case does not
        /// matter. False, refused and logged once, on invalid input.
        /// </summary>
        public bool Register(string owner, string typeFullName, string options, ApiScope scope, string member)
        {
            if (!core.Admit(owner, member))
                return false;
            if (!IsTypeName(typeFullName))
                return Refuse(owner, member, $"the screen type is not a type's full name (Type.FullName: letters, digits, '_', '.', '+', '`'; at most {MaxTypeName})");
            foreach (string reserved in Reserved)
                if (typeFullName.StartsWith(reserved, StringComparison.Ordinal))
                    return Refuse(owner, member, $"\"{typeFullName}\" is the game's or this plugin's screen; register your own screen types");
            if (!TryParse(options, out PanelScreenOptions parsed, out string why))
                return Refuse(owner, member, why);
            if (parsed == PanelScreenOptions.None)
                return Refuse(owner, member, "no options are given: a screen that takes the focus and shows the cursor is on the panel and "
                                             + "works with the laser already, with no registration");
            if (list.Find(owner, typeFullName) == null && list.CountOf(owner) >= MaxPerOwner)
                return Refuse(owner, member, $"the owner has {MaxPerOwner} screens registered already");
            list.Set(new ApiList<Registration>.Entry(owner, typeFullName, scope, core.World, new Registration(parsed)));
            Version++;
            return true;
        }

        /// <summary>Takes away <paramref name="owner"/>'s registration of <paramref name="typeFullName"/>; false when it had none.</summary>
        public bool Unregister(string owner, string typeFullName, string member)
        {
            if (!core.Admit(owner, member))
                return false;
            if (!IsTypeName(typeFullName))
                return Refuse(owner, member, "the screen type is not a type's full name");
            return Drop(list.Remove(e => e.Owner == owner && e.Key == typeFullName).Count) > 0;
        }

        /// <inheritdoc/>
        /// <remarks>An id alone names an action, never a screen (<see cref="Unregister"/> takes one away), so only a whole owner's go here.</remarks>
        public int Remove(string owner, string id) => id != null ? 0 : Drop(list.Remove(ApiList<Registration>.Picks(owner, null)).Count);

        /// <inheritdoc/>
        public int RemoveOtherWorlds(object world) => Drop(list.Remove(ApiList<Registration>.OtherWorlds(world)).Count);

        /// <summary>The options registered for the screen type <paramref name="typeFullName"/>; none when it is not registered.</summary>
        public PanelScreenOptions Of(string typeFullName)
        {
            if (typeFullName == null)
                return PanelScreenOptions.None;
            if (builtFor != Version)
                Rebuild();
            return byType.TryGetValue(typeFullName, out PanelScreenOptions options) ? options : PanelScreenOptions.None;
        }

        /// <summary>Parses the options' names (comma-separated, any case, spaces ignored); empty or null is none.</summary>
        internal static bool TryParse(string text, out PanelScreenOptions options, out string why)
        {
            bool ok = ApiOptions.TryParse(text, OptionNames, out int bits, out why);
            options = (PanelScreenOptions)bits;
            return ok;
        }

        private static bool IsTypeName(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > MaxTypeName || text[0] == '.' || text[text.Length - 1] == '.')
                return false;
            foreach (char c in text)
                if (!(c < 128 && char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '+' || c == '`'))
                    return false;
            return true;
        }

        private void Rebuild()
        {
            int version = Version;
            var sorted = new List<ApiList<Registration>.Entry>(list.Snapshot);
            sorted.Sort((a, b) => string.CompareOrdinal(a.Owner, b.Owner));
            var map = new Dictionary<string, PanelScreenOptions>(StringComparer.Ordinal);
            foreach (ApiList<Registration>.Entry e in sorted)
                if (!map.ContainsKey(e.Key))
                    map[e.Key] = e.Value.Options;
            byType = map;
            builtFor = version;
        }

        private int Drop(int count)
        {
            if (count > 0)
                Version++;
            return count;
        }

        private bool Refuse(string owner, string member, string why)
        {
            core.Refuse(owner, member, why);
            return false;
        }
    }
}
