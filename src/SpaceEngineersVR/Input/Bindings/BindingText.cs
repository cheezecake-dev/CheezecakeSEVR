using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SpaceEngineersVR.Input.Bindings
{
    /// <summary>
    /// Bindings as one line of the settings file: <c>Action=Hand.Control.Kind[+Hand.Control][@seconds]</c>, separated by
    /// <c>;</c>, e.g. <c>LeaveSeat=Left.ButtonUpper.Hold;GameMenu=Right.ThumbstickClick.Press+Left.Grip</c>. Actions are
    /// written by name, so a new action or a reordered list never shifts a player's bindings; an entry naming an action,
    /// control or kind this version does not know is skipped (and reported), never fatal.
    /// </summary>
    internal static class BindingText
    {
        public static string Write(IEnumerable<Binding> bindings, Func<int, string> actionName)
        {
            var text = new StringBuilder();
            foreach (Binding b in bindings)
            {
                if (text.Length > 0)
                    text.Append(';');
                text.Append(actionName(b.Action)).Append('=').Append(b.Control.Hand).Append('.').Append(b.Control.Control).Append('.').Append(b.Kind);
                if (b.Modifier.HasValue)
                    text.Append('+').Append(b.Modifier.Value.Hand).Append('.').Append(b.Modifier.Value.Control);
                if (b.Kind != PressKind.Press && b.Kind != PressKind.Touch && Math.Abs(b.HoldSeconds - BindingEngine.DefaultHoldSeconds) > 1e-6)
                    text.Append('@').Append(b.HoldSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }

        /// <summary>
        /// A player's changes to a set of defaults, as one line: the bindings added as <see cref="Write"/> writes them, and each
        /// default taken away with a <c>-</c> before it (<c>-Jump=Right.ButtonLower.Press;Jump=Right.Trigger.Press</c>).
        /// </summary>
        public static string WriteChanges(IReadOnlyList<Binding> defaults, IReadOnlyList<Binding> bindings, Func<int, string> actionName)
        {
            var removed = new List<Binding>();
            var added = new List<Binding>();
            foreach (Binding d in defaults)
                if (!Contains(bindings, d))
                    removed.Add(d);
            foreach (Binding b in bindings)
                if (!Contains(defaults, b) && !Contains(added, b))
                    added.Add(b);
            var text = new StringBuilder();
            foreach (Binding b in removed)
            {
                if (text.Length > 0)
                    text.Append(';');
                text.Append('-').Append(Write(new[] { b }, actionName));
            }
            if (added.Count > 0)
            {
                if (text.Length > 0)
                    text.Append(';');
                text.Append(Write(added, actionName));
            }
            return text.ToString();
        }

        /// <summary>
        /// <paramref name="defaults"/> with the changes in <paramref name="text"/> made (<see cref="WriteChanges"/>): a default
        /// added since the changes were saved is kept, one taken away stays away. <paramref name="skipped"/> gets each entry that
        /// could not be read.
        /// </summary>
        public static List<Binding> ReadChanges(string text, IReadOnlyList<Binding> defaults, Func<string, int?> actionId, List<string> skipped = null)
        {
            var list = new List<Binding>(defaults);
            if (string.IsNullOrWhiteSpace(text))
                return list;
            var added = new List<Binding>();
            foreach (string raw in text.Split(';'))
            {
                string entry = raw.Trim();
                if (entry.Length == 0)
                    continue;
                bool remove = entry[0] == '-';
                if (!TryReadEntry(remove ? entry.Substring(1) : entry, actionId, out Binding binding))
                {
                    skipped?.Add(entry);
                    continue;
                }
                if (remove)
                    list.RemoveAll(b => b.SameAs(binding));
                else
                    added.Add(binding);
            }
            foreach (Binding b in added)
                if (!Contains(list, b))
                    list.Add(b);
            return list;
        }

        private static bool Contains(IReadOnlyList<Binding> list, Binding binding)
        {
            foreach (Binding b in list)
                if (b.SameAs(binding))
                    return true;
            return false;
        }

        /// <summary>The bindings in <paramref name="text"/>; <paramref name="skipped"/> gets each entry that could not be read.</summary>
        public static List<Binding> Read(string text, Func<string, int?> actionId, List<string> skipped = null)
        {
            var list = new List<Binding>();
            if (string.IsNullOrWhiteSpace(text))
                return list;
            foreach (string raw in text.Split(';'))
            {
                string entry = raw.Trim();
                if (entry.Length == 0)
                    continue;
                if (TryReadEntry(entry, actionId, out Binding binding))
                    list.Add(binding);
                else
                    skipped?.Add(entry);
            }
            return list;
        }

        /// <summary>
        /// The defaults <paramref name="text"/> takes away (its <c>-</c> entries, <see cref="WriteChanges"/>), for the actions
        /// <paramref name="actionId"/> knows.
        /// </summary>
        public static List<Binding> ReadRemoved(string text, Func<string, int?> actionId)
        {
            var list = new List<Binding>();
            if (string.IsNullOrWhiteSpace(text))
                return list;
            foreach (string raw in text.Split(';'))
            {
                string entry = raw.Trim();
                if (entry.Length > 1 && entry[0] == '-' && TryReadEntry(entry.Substring(1), actionId, out Binding binding))
                    list.Add(binding);
            }
            return list;
        }

        /// <summary>
        /// The entries of <paramref name="text"/> (added or taken away) for actions <paramref name="actionId"/> does not know, as
        /// written: an outside action whose plugin or mod is not here this run. They are written back as they are
        /// (<see cref="PlayerBindings.Write"/>), so the player's bindings for it are there when it comes back.
        /// </summary>
        public static List<string> Unknown(string text, Func<string, int?> actionId)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
                return list;
            foreach (string raw in text.Split(';'))
            {
                string entry = raw.Trim();
                string body = entry.Length > 0 && entry[0] == '-' ? entry.Substring(1) : entry;
                int eq = body.IndexOf('=');
                if (eq > 0 && !actionId(body.Substring(0, eq).Trim()).HasValue)
                    list.Add(entry);
            }
            return list;
        }

        private static bool TryReadEntry(string entry, Func<string, int?> actionId, out Binding binding)
        {
            binding = null;
            int eq = entry.IndexOf('=');
            if (eq <= 0)
                return false;
            int? action = actionId(entry.Substring(0, eq).Trim());
            if (!action.HasValue)
                return false;
            return TryReadBinding(entry.Substring(eq + 1), action.Value, out binding);
        }

        /// <summary>
        /// One binding as written after an action's <c>=</c>: <c>Hand.Control.Kind[+Hand.Control][@seconds]</c>, for
        /// <paramref name="action"/>. False when it is not that (an unknown control or kind, a hold time not over 0 and up to
        /// 10 s, a control held with itself).
        /// </summary>
        public static bool TryReadBinding(string text, int action, out Binding binding)
        {
            binding = null;
            if (text == null)
                return false;
            string rest = text.Trim();

            double hold = BindingEngine.DefaultHoldSeconds;
            int at = rest.IndexOf('@');
            if (at >= 0)
            {
                if (!double.TryParse(rest.Substring(at + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out hold) || hold <= 0d || hold > 10d)
                    return false;
                rest = rest.Substring(0, at);
            }

            ControlRef? modifier = null;
            int plus = rest.IndexOf('+');
            if (plus >= 0)
            {
                if (!TryReadControl(rest.Substring(plus + 1), out ControlRef m))
                    return false;
                modifier = m;
                rest = rest.Substring(0, plus);
            }

            string[] parts = rest.Split('.');
            if (parts.Length != 3 || !TryReadControl(parts[0] + "." + parts[1], out ControlRef control)
                || !Enum.TryParse(parts[2], false, out PressKind kind) || !Enum.IsDefined(typeof(PressKind), kind))
                return false;
            if (modifier.HasValue && modifier.Value.Equals(control))
                return false;
            binding = new Binding(action, control, kind, modifier, hold);
            return true;
        }

        private static bool TryReadControl(string text, out ControlRef control)
        {
            control = default;
            string[] parts = text.Trim().Split('.');
            if (parts.Length != 2
                || !Enum.TryParse(parts[0], false, out Hand hand) || !Enum.IsDefined(typeof(Hand), hand)
                || !Enum.TryParse(parts[1], false, out ControllerControl c) || !Enum.IsDefined(typeof(ControllerControl), c))
                return false;
            control = new ControlRef(hand, c);
            return true;
        }
    }
}
