// PlayerBindings: a controller's bindings from its defaults, the outside actions' suggestions and the player's saved changes, and back (no game types; the tests use it).
using System.Collections.Generic;

namespace SpaceEngineersVR.Input.Bindings
{
    /// <summary>
    /// One controller's bindings as the player has them, from the text saved for it (<see cref="BindingText.WriteChanges"/>),
    /// and the text to save for a list the player has set. <see cref="ControllerBindings"/> keeps the text per controller in the
    /// settings; this is the part that needs no game.
    /// <list type="bullet">
    /// <item>The defaults are the plugin's for the controller (<see cref="DefaultBindings.For"/>) and, for each outside action
    /// registered now, its suggestions (<see cref="SuggestedBindings"/>) that clash with nothing: taken in save-name order, so
    /// which of two outside actions asking for one control gets it does not depend on which plugin loaded first.</item>
    /// <item>A suggestion the player took away stays away (a <c>-</c> entry is written for it while its action is registered).
    /// A suggestion that clashes with something the player bound gives way: the player's choice stands, and the action is
    /// unbound there until the player binds it.</item>
    /// <item>Entries for actions not registered this run (a plugin or mod not installed now, or not loaded yet) are written
    /// back as they were, so the player's bindings for it are there when it is.</item>
    /// </list>
    /// </summary>
    internal static class PlayerBindings
    {
        /// <summary>A controller's bindings composed, and what composing them found.</summary>
        internal sealed class Composed
        {
            /// <summary>The bindings in force.</summary>
            public readonly List<Binding> Bindings;

            /// <summary>What the player's changes are measured against: the plugin's defaults, the suggestions given, and the ones the player took away.</summary>
            public readonly List<Binding> Defaults;

            /// <summary>The suggestions the player took away (also in <see cref="Defaults"/>).</summary>
            public readonly List<Binding> Declined;

            /// <summary>Saved entries that could not be read (a control or kind this version does not have): left out.</summary>
            public readonly List<string> Unreadable;

            /// <summary>Saved entries for actions not registered now: kept as written.</summary>
            public readonly List<string> Unknown;

            public Composed(List<Binding> bindings, List<Binding> defaults, List<Binding> declined, List<string> unreadable, List<string> unknown)
            {
                Bindings = bindings;
                Defaults = defaults;
                Declined = declined;
                Unreadable = unreadable;
                Unknown = unknown;
            }
        }

        /// <summary><paramref name="profile"/>'s bindings with the player's changes <paramref name="saved"/> (null or empty: none).</summary>
        public static Composed Compose(string profile, string saved)
        {
            List<Binding> builtIn = DefaultBindings.For(profile);
            var skipped = new List<string>();
            List<Binding> list = BindingText.ReadChanges(saved, builtIn, VRActions.FromSaveName, skipped);
            List<Binding> removed = BindingText.ReadRemoved(saved, VRActions.FromSaveName);
            List<string> unknown = BindingText.Unknown(saved, VRActions.FromSaveName);
            var unreadable = new List<string>();
            foreach (string entry in skipped)
                if (!unknown.Contains(entry))
                    unreadable.Add(entry);

            var defaults = new List<Binding>(builtIn);
            var declined = new List<Binding>();
            foreach (VRActionInfo info in VRActions.OutsideByName)
            {
                foreach (Binding s in SuggestedBindings.ForProfile(profile, info.Suggested))
                {
                    if (Contains(removed, s))
                    {
                        declined.Add(s);
                        defaults.Add(s);
                        continue;
                    }
                    // The player has bound it so already: theirs, written as theirs.
                    if (Contains(list, s))
                        continue;
                    if (SuggestedBindings.UsesDirection(s) || !SuggestedBindings.ClashesWithNothing(profile, list, s))
                        continue;
                    list.Add(s);
                    defaults.Add(s);
                }
            }
            return new Composed(list, defaults, declined, unreadable, unknown);
        }

        /// <summary>
        /// The text to save for <paramref name="profile"/> when the player has set <paramref name="bindings"/>, the text saved
        /// before being <paramref name="saved"/>: what differs from the defaults, then the entries for actions not registered
        /// now, as they were. Empty when the list is the defaults and nothing else is kept.
        /// </summary>
        public static string Write(string profile, string saved, IReadOnlyList<Binding> bindings)
        {
            Composed before = Compose(profile, saved);
            // A suggestion taken away and now bound again by the player is the player's: written as an added binding, so it
            // stays when its suggestion would give way.
            var defaults = new List<Binding>(before.Defaults.Count);
            foreach (Binding d in before.Defaults)
                if (!(Contains(before.Declined, d) && Contains(bindings, d)))
                    defaults.Add(d);
            // A binding for an action taken away since the list was made (a page open while a plugin unregisters) is already
            // kept, as saved, with the entries for actions not registered now.
            var registered = new List<Binding>(bindings.Count);
            foreach (Binding b in bindings)
                if (VRActions.Info(b.Action) != null)
                    registered.Add(b);
            string text = BindingText.WriteChanges(defaults, registered, VRActions.SaveName);
            foreach (string entry in before.Unknown)
                text = text.Length == 0 ? entry : text + ";" + entry;
            return text;
        }

        /// <summary>The text for <paramref name="saved"/> (a profile's saved bindings) when the player puts it back to its defaults: only the entries for actions not registered now.</summary>
        public static string Reset(string saved) => string.Join(";", BindingText.Unknown(saved, VRActions.FromSaveName));

        /// <summary><paramref name="saved"/> changes an action registered now (entries kept only for absent actions are no change the player sees).</summary>
        public static bool HasChanges(string saved)
        {
            if (string.IsNullOrWhiteSpace(saved))
                return false;
            int entries = 0;
            foreach (string raw in saved.Split(';'))
                if (raw.Trim().Length > 0)
                    entries++;
            return entries > BindingText.Unknown(saved, VRActions.FromSaveName).Count;
        }

        private static bool Contains(IReadOnlyList<Binding> list, Binding binding)
        {
            foreach (Binding b in list)
                if (b.SameAs(binding))
                    return true;
            return false;
        }
    }
}
