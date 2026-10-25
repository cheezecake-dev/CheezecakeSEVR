using System;
using System.Collections.Generic;

namespace SpaceEngineersVR.Input.Bindings
{
    /// <summary>How a control is used for a binding.</summary>
    internal enum PressKind
    {
        /// <summary>For as long as the control is down.</summary>
        Press,

        /// <summary>Let go before the hold time: one frame, on the release.</summary>
        Tap,

        /// <summary>Held for the hold time: from then until it is let go.</summary>
        Hold,

        /// <summary>For as long as a finger or thumb rests on it (a controller with touch sensors on that control).</summary>
        Touch,
    }

    /// <summary>One control on one hand.</summary>
    internal readonly struct ControlRef : IEquatable<ControlRef>
    {
        public readonly Hand Hand;
        public readonly ControllerControl Control;

        public ControlRef(Hand hand, ControllerControl control)
        {
            Hand = hand;
            Control = control;
        }

        public int Index => (int)Hand * ControlCount + (int)Control;

        public static readonly int ControlCount = Enum.GetValues(typeof(ControllerControl)).Length;

        public bool Equals(ControlRef other) => Hand == other.Hand && Control == other.Control;
        public override bool Equals(object obj) => obj is ControlRef other && Equals(other);
        public override int GetHashCode() => Index;
        public override string ToString() => $"{Hand} {Control}";
    }

    /// <summary>
    /// An action on a control used one way, alone or while a modifier (another control, held) is down: "Leave seat: left
    /// ButtonUpper, Hold", or "Game menu: right ThumbstickClick, Press, with left Grip held".
    /// </summary>
    internal sealed class Binding
    {
        public readonly int Action;
        public readonly ControlRef Control;
        public readonly PressKind Kind;
        public readonly ControlRef? Modifier;

        /// <summary>For <see cref="PressKind.Tap"/> and <see cref="PressKind.Hold"/>: the hold time, seconds.</summary>
        public readonly double HoldSeconds;

        public Binding(int action, ControlRef control, PressKind kind, ControlRef? modifier = null, double holdSeconds = BindingEngine.DefaultHoldSeconds)
        {
            Action = action;
            Control = control;
            Kind = kind;
            Modifier = modifier;
            HoldSeconds = holdSeconds;
        }

        public override string ToString() => (Modifier.HasValue ? Modifier.Value + " + " : string.Empty) + Control + " " + Kind;

        /// <summary>The same action on the same control, used the same way, with the same modifier and hold time.</summary>
        public bool SameAs(Binding other) =>
            other != null && Action == other.Action && Control.Equals(other.Control) && Kind == other.Kind && Nullable.Equals(Modifier, other.Modifier)
            && Math.Abs(HoldSeconds - other.HoldSeconds) < 1e-6;
    }

    /// <summary>The controls this frame: which are down and which are touched, both hands.</summary>
    internal sealed class ControlFrame
    {
        public readonly bool[] Down = new bool[2 * ControlRef.ControlCount];
        public readonly bool[] Touched = new bool[2 * ControlRef.ControlCount];

        public void Clear()
        {
            Array.Clear(Down, 0, Down.Length);
            Array.Clear(Touched, 0, Touched.Length);
        }

        public void Set(Hand hand, ControllerControl control, bool down, bool touched = false)
        {
            int i = new ControlRef(hand, control).Index;
            Down[i] = down;
            Touched[i] = touched || down;
        }
    }

    /// <summary>
    /// Turns the controls into actions by a set of <see cref="Binding"/>s, once a game frame (<see cref="Update"/>).
    /// <list type="bullet">
    /// <item>Where the player is (<see cref="BindingContext"/>) is given each frame. An action is on only where it applies, and
    /// the places are stacked in <see cref="Layers"/>: a menu over a wheel over a ping being aimed over building, painting or a
    /// container over the place itself. A press belongs to the highest layer that has a binding for it as it begins (so the
    /// trigger places a block while building and fires otherwise), and to that layer only for the whole press: a press that
    /// closes a menu does nothing in the game under it, and one that opens a wheel goes on holding the wheel open.</item>
    /// <item>A modifier is any control some binding names as its <see cref="Binding.Modifier"/>. A press of another control
    /// that begins while a modifier it has a binding with is down belongs to that modifier for the whole press: only the
    /// bindings with that modifier see it, the control's plain bindings do not, even if the modifier is let go first.</item>
    /// <item>A modifier keeps its own bindings: its tap, and its hold before the hold time, are given only when no other
    /// control was pressed with it, so holding it to reach a second layer never also does its own thing; a hold already
    /// given goes on (holding X aims a ping, and the trigger pulled with it marks a GPS without ending the aim). A Press
    /// binding on a modifier would act at once; <see cref="Conflicts"/> names it.</item>
    /// <item>Tap is given on the release of a press shorter than the hold time, for one frame; Hold from the hold time on
    /// while it stays down, for one press. A control with both is a tap or a hold, never both.</item>
    /// <item>A press can be taken by something that is not a binding (a hand working a cockpit switch with the trigger):
    /// <see cref="Consume"/> takes it from every binding until it is let go.</item>
    /// </list>
    /// </summary>
    internal sealed class BindingEngine
    {
        public const double DefaultHoldSeconds = 0.5;

        /// <summary>The places that stack, highest first: a press belongs to the highest that binds it (<see cref="BindingEngine"/>).</summary>
        public static readonly BindingContext[] Layers =
        {
            BindingContext.Menus | BindingContext.WristPanel,
            BindingContext.ToolsWheel | BindingContext.ActionsWheel,
            BindingContext.PingAim,
            BindingContext.Build | BindingContext.Placing | BindingContext.Paint | BindingContext.Container,
            BindingContext.Gameplay,
        };

        private int actionCount;
        private readonly Func<int, BindingContext> contextsOf;
        private Binding[] bindings = Array.Empty<Binding>();
        private bool[] bindingOn = Array.Empty<bool>();

        // Per control (ControlRef.Index): when the current press began (or -1), the modifier it belongs to (or -1), the layer
        // it belongs to, when it was first used as a modifier during its own press (or -1), and whether it has been taken.
        private readonly double[] downAt;
        private readonly int[] ownerModifier;
        private readonly BindingContext[] pressLayer;
        private readonly double[] usedAsModifierAt;
        private readonly bool[] consumed;
        private readonly bool[] wasDown;

        // Per control: down already on the first frame after a Reset (held through the gap), so its press is no new press.
        private readonly bool[] carried;
        private bool resuming;
        private bool[] isModifier;
        private BindingContext where = BindingContext.Any;
        private double lastSeconds;

        private bool[] active, activeBefore;

        /// <param name="actionCount">How many actions there are.</param>
        /// <param name="contextsOf">Where each action applies; null: everywhere, and the places are not stacked.</param>
        public BindingEngine(int actionCount, Func<int, BindingContext> contextsOf = null)
        {
            this.actionCount = actionCount;
            this.contextsOf = contextsOf;
            int controls = 2 * ControlRef.ControlCount;
            downAt = new double[controls];
            ownerModifier = new int[controls];
            pressLayer = new BindingContext[controls];
            usedAsModifierAt = new double[controls];
            consumed = new bool[controls];
            wasDown = new bool[controls];
            carried = new bool[controls];
            isModifier = new bool[controls];
            for (int i = 0; i < controls; i++)
            {
                downAt[i] = -1d;
                ownerModifier[i] = -1;
                usedAsModifierAt[i] = -1d;
            }
            active = new bool[actionCount];
            activeBefore = new bool[actionCount];
        }

        public IReadOnlyList<Binding> Bindings => bindings;

        /// <summary>How many actions the engine answers for (numbers 0 to this, less one).</summary>
        public int ActionCount => actionCount;

        /// <summary>
        /// The engine answers for <paramref name="count"/> actions from now on (actions registered while the game runs are
        /// numbered after the others). It only grows: what is on, and was on the frame before, stays as it was.
        /// </summary>
        public void EnsureActions(int count)
        {
            if (count <= actionCount)
                return;
            Array.Resize(ref active, count);
            Array.Resize(ref activeBefore, count);
            actionCount = count;
        }

        /// <summary>Replaces the bindings; presses under way keep going under the new ones from the next frame.</summary>
        public void SetBindings(IEnumerable<Binding> list)
        {
            bindings = new List<Binding>(list).ToArray();
            bindingOn = new bool[bindings.Length];
            var modifiers = new bool[isModifier.Length];
            foreach (Binding b in bindings)
                if (b.Modifier.HasValue)
                    modifiers[b.Modifier.Value.Index] = true;
            isModifier = modifiers;
        }

        /// <summary>The action is on this frame (false for a number the engine does not answer for).</summary>
        public bool IsActive(int action) => Known(action) && active[action];

        /// <summary>The action came on this frame.</summary>
        public bool Started(int action) => Known(action) && active[action] && !activeBefore[action];

        /// <summary>The action went off this frame.</summary>
        public bool Ended(int action) => Known(action) && !active[action] && activeBefore[action];

        private bool Known(int action) => action >= 0 && action < actionCount;

        /// <summary>
        /// How far a hold of <paramref name="action"/> under way has got, 0 to 1 (1 once it is on), or -1 with none under way that
        /// can still become it: for a feel that builds while a control is held.
        /// </summary>
        public double HoldProgress(int action)
        {
            double best = -1d;
            if (!Enabled(action))
                return best;
            foreach (Binding b in bindings)
            {
                if (b.Action != action || b.Kind != PressKind.Hold)
                    continue;
                int i = b.Control.Index;
                if (!wasDown[i] || consumed[i] || downAt[i] < 0d || (ContextsOf(action) & pressLayer[i]) == 0
                    || ownerModifier[i] != (b.Modifier.HasValue ? b.Modifier.Value.Index : -1))
                    continue;
                if (isModifier[i] && usedAsModifierAt[i] >= 0d && usedAsModifierAt[i] - downAt[i] < b.HoldSeconds)
                    continue;
                best = Math.Max(best, Math.Min(1d, (lastSeconds - downAt[i]) / b.HoldSeconds));
            }
            return best;
        }

        /// <summary>The control is down, and its press has not been taken (<see cref="Consume"/>).</summary>
        public bool IsDown(ControlRef control) => wasDown[control.Index] && !consumed[control.Index];

        /// <summary>The control's press under way is taken from every binding until it is let go, from this frame on. False when it is not down or already taken.</summary>
        public bool Consume(ControlRef control)
        {
            int i = control.Index;
            if (!wasDown[i] || consumed[i])
                return false;
            consumed[i] = true;
            // This frame's answers from it go too.
            Array.Clear(active, 0, actionCount);
            for (int k = 0; k < bindings.Length; k++)
            {
                if (bindingOn[k] && consumed[bindings[k].Control.Index])
                    bindingOn[k] = false;
                if (bindingOn[k])
                    active[bindings[k].Action] = true;
            }
            return true;
        }

        /// <summary>
        /// Everything is let go: presses under way end, and nothing is on. A control still down on the next frame was held
        /// through the gap: it starts no Press or Tap until let go (a trigger held as the hands come back does not fire), while
        /// its Hold and Touch work (a Menu held then still recentres).
        /// </summary>
        public void Reset()
        {
            resuming = true;
            for (int i = 0; i < downAt.Length; i++)
            {
                downAt[i] = -1d;
                ownerModifier[i] = -1;
                pressLayer[i] = BindingContext.None;
                usedAsModifierAt[i] = -1d;
                consumed[i] = false;
                wasDown[i] = false;
                carried[i] = false;
            }
            Array.Clear(active, 0, actionCount);
            Array.Clear(activeBefore, 0, actionCount);
            Array.Clear(bindingOn, 0, bindingOn.Length);
        }

        /// <summary>
        /// Game thread, once a frame: the controls as they are now, at <paramref name="seconds"/> (only differences count), with
        /// the player at <paramref name="now"/>.
        /// </summary>
        public void Update(ControlFrame frame, double seconds, BindingContext now = BindingContext.Any)
        {
            where = now;
            lastSeconds = seconds;
            Array.Copy(active, activeBefore, actionCount);
            Array.Clear(active, 0, actionCount);

            int controls = downAt.Length;
            // New presses first take their owner (a modifier down now, with a binding on this control), then their layer.
            for (int i = 0; i < controls; i++)
            {
                if (frame.Down[i] && !wasDown[i])
                {
                    carried[i] = resuming;
                    downAt[i] = seconds;
                    usedAsModifierAt[i] = -1d;
                    consumed[i] = false;
                    ownerModifier[i] = OwnerFor(i, frame);
                    if (ownerModifier[i] >= 0 && usedAsModifierAt[ownerModifier[i]] < 0d)
                        usedAsModifierAt[ownerModifier[i]] = seconds;
                    pressLayer[i] = LayerFor(i, ownerModifier[i]);
                }
                // A press that began with the player nowhere (the first frame the hands are back, before anything says where
                // they are) belongs to the first place they are in, so a button already held then still works.
                else if (frame.Down[i] && pressLayer[i] == BindingContext.None && where != BindingContext.None)
                    pressLayer[i] = LayerFor(i, ownerModifier[i]);
            }

            for (int k = 0; k < bindings.Length; k++)
            {
                Binding b = bindings[k];
                bindingOn[k] = b.Action >= 0 && b.Action < actionCount && Enabled(b.Action) && Evaluate(b, frame, seconds);
                if (bindingOn[k])
                    active[b.Action] = true;
            }

            for (int i = 0; i < controls; i++)
            {
                if (!frame.Down[i])
                {
                    downAt[i] = -1d;
                    ownerModifier[i] = -1;
                    pressLayer[i] = BindingContext.None;
                    usedAsModifierAt[i] = -1d;
                    consumed[i] = false;
                    carried[i] = false;
                }
                wasDown[i] = frame.Down[i];
            }
            resuming = false;
        }

        private BindingContext ContextsOf(int action) => contextsOf == null ? BindingContext.Any : contextsOf(action);

        /// <summary>
        /// An action the game would refuse here this frame, though the place has it (the container's production with a hand
        /// weapon that reloads): it is not on, and its controls belong to the layer under it, as if it had no binding.
        /// </summary>
        public Func<int, bool> Refuses { get; set; }

        private bool Enabled(int action) => (ContextsOf(action) & where) != 0 && !(Refuses?.Invoke(action) ?? false);

        private int OwnerFor(int control, ControlFrame frame)
        {
            foreach (Binding b in bindings)
            {
                if (!b.Modifier.HasValue || b.Control.Index != control || !Enabled(b.Action))
                    continue;
                int m = b.Modifier.Value.Index;
                if (m != control && frame.Down[m])
                    return m;
            }
            return -1;
        }

        /// <summary>The highest layer the player is in with a binding for this press; with none, the highest the player is in.</summary>
        private BindingContext LayerFor(int control, int modifier)
        {
            if (contextsOf == null)
                return BindingContext.Any;
            foreach (BindingContext layer in Layers)
            {
                if ((layer & where) == 0)
                    continue;
                foreach (Binding b in bindings)
                {
                    if (b.Control.Index != control || b.Kind == PressKind.Touch || (b.Modifier.HasValue ? b.Modifier.Value.Index : -1) != modifier)
                        continue;
                    if ((ContextsOf(b.Action) & where & layer) != 0 && Enabled(b.Action))
                        return layer;
                }
            }
            foreach (BindingContext layer in Layers)
                if ((layer & where) != 0)
                    return layer;
            return where;
        }

        private bool Evaluate(Binding b, ControlFrame frame, double seconds)
        {
            int i = b.Control.Index;
            int wanted = b.Modifier.HasValue ? b.Modifier.Value.Index : -1;

            if (b.Kind == PressKind.Touch)
            {
                if (!frame.Touched[i])
                    return false;
                return wanted < 0 ? true : frame.Down[wanted];
            }

            bool down = frame.Down[i];
            bool released = !down && wasDown[i];
            if (!down && !released)
                return false;
            if (consumed[i])
                return false;
            // The press belongs to one layer of places, and to one layer of modifiers: the modifier it began under, or none.
            if ((ContextsOf(b.Action) & pressLayer[i]) == 0)
                return false;
            if (ownerModifier[i] != wanted)
                return false;

            double held = downAt[i] < 0d ? 0d : seconds - downAt[i];
            // A modifier's own tap, and its hold not yet given, stand down once it has been used to reach another control.
            if (wanted < 0 && isModifier[i] && usedAsModifierAt[i] >= 0d)
            {
                if (b.Kind == PressKind.Tap)
                    return false;
                if (b.Kind == PressKind.Hold && usedAsModifierAt[i] - downAt[i] < b.HoldSeconds)
                    return false;
            }

            // Held through a Reset: no press or tap of it until it is let go.
            if (carried[i] && (b.Kind == PressKind.Press || b.Kind == PressKind.Tap))
                return false;

            switch (b.Kind)
            {
                case PressKind.Press:
                    return down;
                case PressKind.Tap:
                    return released && held < b.HoldSeconds;
                case PressKind.Hold:
                    return down && held >= b.HoldSeconds;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Bindings that cannot both work as set: two actions on the same control, kind and modifier in the same context, or a
        /// Press binding on a control that is also a modifier (it would act every time the modifier is held). <paramref name="sameContext"/>
        /// says whether two actions can be wanted at the same moment; a modifier's press counts only against the layers it
        /// shares a context with. <paramref name="heldMode"/>, when given, names the actions that are themselves the held layer
        /// (holding the control is the mode, and the controls pressed with it are its own), whose press on a modifier is wanted.
        /// </summary>
        public static List<(Binding A, Binding B, string Why)> Conflicts(IReadOnlyList<Binding> list, Func<int, int, bool> sameContext, Func<int, bool> heldMode = null)
        {
            var found = new List<(Binding, Binding, string)>();
            for (int x = 0; x < list.Count; x++)
            {
                Binding a = list[x];
                if (a.Kind == PressKind.Press && !a.Modifier.HasValue && (heldMode == null || !heldMode(a.Action)))
                {
                    foreach (Binding layer in list)
                    {
                        if (layer.Modifier.HasValue && layer.Modifier.Value.Equals(a.Control) && sameContext(a.Action, layer.Action))
                        {
                            found.Add((a, layer, "a modifier's press acts every time the modifier is held: use its tap or hold"));
                            break;
                        }
                    }
                }
                for (int y = x + 1; y < list.Count; y++)
                {
                    Binding b = list[y];
                    if (a.Action == b.Action || !a.Control.Equals(b.Control) || !Nullable.Equals(a.Modifier, b.Modifier))
                        continue;
                    if (!sameContext(a.Action, b.Action))
                        continue;
                    if (a.Kind == b.Kind)
                        found.Add((a, b, "the same control, used the same way"));
                    else if ((a.Kind == PressKind.Press && b.Kind != PressKind.Touch) || (b.Kind == PressKind.Press && a.Kind != PressKind.Touch))
                        found.Add((a, b, "a press acts at once, so its tap or hold would do both"));
                }
            }
            return found;
        }
    }
}
