// ApiHints: which outside actions the labels beside the hands name, on which control, in what words (no game types; tests/ApiTests runs it).
using System;
using System.Collections.Generic;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Input.Bindings;

namespace SpaceEngineersVR.Api
{
    /// <summary>
    /// The lines the labels beside the hands (<see cref="Rendering.HandHints"/>) give the actions other plugins and mods register
    /// (SevrApi version 2, entry point 3): an outside action gets a line where it applies (its contexts meet where the player is)
    /// and wherever it is bound on the controller in hand, one line per hand it is bound on, worded as the plugin's own lines are
    /// ("Hold: boost", "Left grip held: boost"). They come after the plugin's own lines, in save-name order, so which plugin
    /// loaded first never decides the order; the plate's row limit cuts the last ones. Whether each is offered this frame
    /// (its <c>available</c> answer, its owner's guard) is asked as the plate is drawn (<see cref="ApiActions.Offered"/>).
    /// </summary>
    internal static class ApiHints
    {
        /// <summary>One outside action's line: the hand and control it is bound to, what it does, and its action number.</summary>
        internal readonly struct HintLine
        {
            public HintLine(Hand hand, ControllerControl control, string what, int action)
            {
                Hand = hand;
                Control = control;
                What = what;
                Action = action;
            }

            public Hand Hand { get; }

            public ControllerControl Control { get; }

            public string What { get; }

            public int Action { get; }
        }

        /// <summary>
        /// The outside actions' lines for a player in <paramref name="where"/>, from the bindings in use (<paramref name="names"/>),
        /// each action as it works for the player's hand (<paramref name="mirrors"/>: <see cref="ControllerBindings.Mirrors"/>).
        /// Left hand's first, then the right's, each in save-name order. Empty where the player is nowhere.
        /// </summary>
        public static List<HintLine> For(BindingNames names, BindingContext where, Func<int, bool> mirrors)
        {
            var left = new List<HintLine>();
            var right = new List<HintLine>();
            if (where == BindingContext.None)
                return left;
            foreach (VRActionInfo info in VRActions.OutsideByName)
            {
                if ((info.Contexts & where) == 0)
                    continue;
                bool mirrored = mirrors != null && mirrors(info.Index);
                for (int h = 0; h < 2; h++)
                {
                    var hand = (Hand)h;
                    Binding b = names.Find((VRAction)info.Index, mirrored, hand);
                    if (b == null)
                        continue;
                    (hand == Hand.Left ? left : right).Add(new HintLine(hand, b.Control.Control, What(names, b, info.Name), info.Index));
                }
            }
            left.AddRange(right);
            return left;
        }

        /// <summary>
        /// The action's label as a line says it: a hold says so ("Hold: boost"), and a control held with it is named ("Left grip
        /// held: boost"), as the plugin's own lines are (<see cref="ControlGuide"/>).
        /// </summary>
        internal static string What(BindingNames names, Binding b, string label)
        {
            string what = label;
            if (b.Kind == PressKind.Hold)
                what = "Hold: " + Lower(what);
            if (b.Modifier.HasValue && names.Qualified(b.Modifier.Value.Hand, b.Modifier.Value.Control) is string held)
                what = held + " held: " + Lower(what);
            return what;
        }

        /// <summary>The first letter lower case, mid-sentence, unless the word is an initialism ("GPS", "HUD").</summary>
        private static string Lower(string s)
        {
            if (string.IsNullOrEmpty(s) || !char.IsUpper(s[0]) || s.Length > 1 && char.IsUpper(s[1]))
                return s;
            return char.ToLowerInvariant(s[0]) + s.Substring(1);
        }
    }
}
