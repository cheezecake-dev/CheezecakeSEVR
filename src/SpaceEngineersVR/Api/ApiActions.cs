// ApiActions: controller actions other plugins and mods register, made real actions of the binding system, and delivered to them (no game types; tests/ApiTests runs it).
using System;
using System.Collections.Generic;
using System.Text;
using SpaceEngineersVR.Input.Bindings;

namespace SpaceEngineersVR.Api
{
    /// <summary>
    /// Actions registered through SevrApi version 2 (<c>RegisterAction</c>). Each is a real action of the binding system
    /// (<see cref="VRActions.AddOutside"/>): listed on the bindings page under its owner's name, saved as <c>owner.id</c>,
    /// clash-checked per context, given its suggested binding only where that clashes with nothing (<see cref="SuggestedBindings"/>),
    /// moved to the other hand for a left-handed player when it follows the hand (<see cref="BindingMirror"/>).
    /// <list type="bullet">
    /// <item>Delivered both ways, on the game thread: polled (<see cref="Held"/>, <see cref="Started"/>, <see cref="Ended"/>)
    /// and by the owner's callback (1 started, 0 ended) from the bindings' frame (<see cref="AfterFrame"/>). Both see the
    /// same frame. An action on when the hands stop counting (the headset off, a menu that stops input) ends then, so an owner
    /// never sees a start without its end.</item>
    /// <item>The owner's <c>available</c> answer is asked once a frame, only while the player is somewhere the action applies
    /// (<see cref="BeforeFrame"/>); while it says no, the action is not on and its controls work as if it were not bound.</item>
    /// <item>Registering the same owner and id again replaces the definition and keeps the action's number, so the player's
    /// binding and a press under way stay with it (a plugin reloaded).</item>
    /// </list>
    /// </summary>
    internal sealed class ApiActions : IApiRegistry
    {
        /// <summary>The most actions one owner may register.</summary>
        public const int MaxPerOwner = 64;

        /// <summary>The longest label an action may have.</summary>
        public const int MaxLabel = 80;

        /// <summary>What a callback is given when the action comes on.</summary>
        public const int StartedArg = 1;

        /// <summary>What a callback is given when the action goes off.</summary>
        public const int EndedArg = 0;

        private sealed class Outside
        {
            public readonly int Index;
            public readonly string FullId;
            public readonly Func<bool> Available;
            public readonly Action<int> Callback;

            /// <summary>How the log names the two callbacks, made once here so the frame builds no strings.</summary>
            public readonly string AvailableWhat, CallbackWhat;

            public Outside(int index, string id, string fullId, Func<bool> available, Action<int> callback)
            {
                Index = index;
                FullId = fullId;
                Available = available;
                Callback = callback;
                AvailableWhat = "available answer for action " + id;
                CallbackWhat = "callback for action " + id;
            }
        }

        private readonly ApiCore core;
        private readonly ApiList<Outside> list = new ApiList<Outside>();

        // By action number (VRActions): not available this frame; on this frame and the one before, as delivered.
        private bool[] refused = Array.Empty<bool>(), on = Array.Empty<bool>(), was = Array.Empty<bool>();

        public ApiActions(ApiCore core)
        {
            this.core = core ?? throw new ArgumentNullException(nameof(core));
            core.Add(this);
        }

        /// <summary>
        /// Registers (or replaces) <paramref name="owner"/>'s action <paramref name="id"/>. False, refused and logged once,
        /// on invalid input; see the member's documentation in <see cref="SevrApi"/>.
        /// </summary>
        public bool Register(string owner, string id, string label, string contexts, string suggested, bool followsHand,
                             Func<bool> available, Action<int> callback, ApiScope scope, string member)
        {
            if (!core.Admit(owner, member))
                return false;
            if (!ApiIds.IsId(id))
                return Refuse(owner, member, $"the action id is not 1 to {ApiIds.MaxLength} letters, digits, '_' or '-'");
            string name = label?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > MaxLabel || ApiCore.HasControlCharacters(name))
                return Refuse(owner, member, $"the label is empty, over {MaxLabel} characters, or has line breaks or other control characters");
            if (!ApiContexts.TryRead(contexts, out BindingContext where, out string whyContexts))
                return Refuse(owner, member, whyContexts);
            if (!SuggestedBindings.TryRead(suggested, out List<Binding> bindings, out string whyBinding))
                return Refuse(owner, member, "the suggested binding is not valid: " + whyBinding);
            ApiList<Outside>.Entry existing = list.Find(owner, id);
            if (existing == null && list.CountOf(owner) >= MaxPerOwner)
                return Refuse(owner, member, $"the owner has {MaxPerOwner} actions already");

            string fullId = ApiIds.Full(owner, id);
            int index = VRActions.AddOutside(owner, fullId, name, core.NameOf(owner), where, followsHand, bindings);
            if (index < 0)
                return Refuse(owner, member, $"\"{fullId}\" is the name of one of the plugin's own actions");
            Grow(index + 1);
            list.Set(new ApiList<Outside>.Entry(owner, id, scope, core.World, new Outside(index, id, fullId, available, callback)));
            return true;
        }

        /// <inheritdoc/>
        public int Remove(string owner, string id) => Drop(list.Remove(ApiList<Outside>.Picks(owner, id)));

        /// <inheritdoc/>
        public int RemoveOtherWorlds(object world) => Drop(list.Remove(ApiList<Outside>.OtherWorlds(world)));

        private int Drop(List<ApiList<Outside>.Entry> gone)
        {
            foreach (ApiList<Outside>.Entry e in gone)
            {
                VRActions.RemoveOutside(e.Value.Index);
                // A press under way is the owner's no more: no end is sent, and polling it says off.
                on[e.Value.Index] = was[e.Value.Index] = refused[e.Value.Index] = false;
            }
            return gone.Count;
        }

        /// <summary>The owner was given a name: its actions are listed under it.</summary>
        public void Renamed(string owner) => VRActions.SetOutsideGroup(owner, core.NameOf(owner));

        /// <summary>The action's number (<see cref="VRActions"/>) for a full id, or -1 when it is not registered now.</summary>
        public int IndexOf(string fullId) => VRActions.FromSaveName(fullId) is int i && VRActions.Info(i)?.IsOutside == true ? i : -1;

        /// <summary>
        /// Game thread, each bindings frame before the engine runs: asks each outside action whose places include where the player
        /// is (<paramref name="where"/>) whether it is available, once, under its owner's guard (an owner switched off, or one that
        /// throws, is not available).
        /// </summary>
        public void BeforeFrame(BindingContext where)
        {
            foreach (ApiList<Outside>.Entry e in list.Snapshot)
            {
                Outside a = e.Value;
                if (a.Available == null || (VRActions.ContextsOf(a.Index) & where) == 0)
                {
                    refused[a.Index] = false;
                    continue;
                }
                refused[a.Index] = !core.Ask(e.Owner, a.AvailableWhat, a.Available, fallback: false);
            }
        }

        /// <summary>The engine's question (<see cref="BindingEngine.Refuses"/>): the outside action said it is not available this frame.</summary>
        public bool Refuses(int action) => action >= 0 && action < refused.Length && refused[action];

        /// <summary>
        /// The outside action is offered to the player now, so the labels by the hands may name it: registered, its owner's
        /// callbacks not switched off, and not refused this frame by its <c>available</c> answer (asked only where it applies).
        /// </summary>
        public bool Offered(int action)
        {
            VRActionInfo info = VRActions.Info(action);
            return info != null && info.IsOutside && core.OwnerEnabled(info.Owner) && !Refuses(action);
        }

        /// <summary>
        /// Game thread, each bindings frame after the engine ran: takes which actions are on (<paramref name="isActive"/>, as the
        /// binding layer answers for the player's hand), and calls each owner's callback for each that came on or went off.
        /// </summary>
        public void AfterFrame(Func<int, bool> isActive)
        {
            ApiList<Outside>.Entry[] snapshot = list.Snapshot;
            foreach (ApiList<Outside>.Entry e in snapshot)
            {
                int i = e.Value.Index;
                was[i] = on[i];
                on[i] = isActive(i);
            }
            Dispatch(snapshot);
        }

        /// <summary>Game thread: the hands stopped counting (the headset off, input stopped): every action on goes off, its end sent.</summary>
        public void EndAll()
        {
            ApiList<Outside>.Entry[] snapshot = list.Snapshot;
            bool any = false;
            foreach (ApiList<Outside>.Entry e in snapshot)
            {
                int i = e.Value.Index;
                any |= on[i] || was[i];
                was[i] = on[i];
                on[i] = false;
                refused[i] = false;
            }
            if (any)
                Dispatch(snapshot);
        }

        private void Dispatch(ApiList<Outside>.Entry[] snapshot)
        {
            foreach (ApiList<Outside>.Entry e in snapshot)
            {
                Outside a = e.Value;
                int i = a.Index;
                if (a.Callback == null || on[i] == was[i])
                    continue;
                core.Invoke(e.Owner, a.CallbackWhat, a.Callback, on[i] ? StartedArg : EndedArg);
            }
        }

        /// <summary>The action is on this frame (false for one not registered).</summary>
        public bool Held(string fullId) { int i = IndexOf(fullId); return i >= 0 && on[i]; }

        /// <summary>The action came on this frame.</summary>
        public bool Started(string fullId) { int i = IndexOf(fullId); return i >= 0 && on[i] && !was[i]; }

        /// <summary>The action went off this frame.</summary>
        public bool Ended(string fullId) { int i = IndexOf(fullId); return i >= 0 && !on[i] && was[i]; }

        private void Grow(int count)
        {
            if (count <= on.Length)
                return;
            Array.Resize(ref refused, count);
            Array.Resize(ref on, count);
            Array.Resize(ref was, count);
        }

        private bool Refuse(string owner, string member, string why)
        {
            core.Refuse(owner, member, why);
            return false;
        }
    }

    /// <summary>The binding system's places (<see cref="BindingContext"/>) by their stable public names, as integrators write them.</summary>
    internal static class ApiContexts
    {
        /// <summary>
        /// The names, as an action's contexts are given and as <c>Context</c> reports where the player is. Each is a place the
        /// binding system already tells apart. Build, Paint, Placing, Container and PingAim are reported with the place under
        /// them ("OnFoot,Build"); the wheels, the wrist panel and menus are places of their own, reported alone.
        /// </summary>
        public static readonly (string Name, BindingContext Context)[] Names =
        {
            ("OnFoot", BindingContext.OnFoot),
            ("Jetpack", BindingContext.Jetpack),
            ("Seat", BindingContext.Seat),
            ("Turret", BindingContext.Turret),
            ("CameraView", BindingContext.CameraView),
            ("Spectator", BindingContext.Spectator),
            ("Build", BindingContext.Build),
            ("Paint", BindingContext.Paint),
            ("Placing", BindingContext.Placing),
            ("Container", BindingContext.Container),
            ("PingAim", BindingContext.PingAim),
            ("ToolsWheel", BindingContext.ToolsWheel),
            ("ActionsWheel", BindingContext.ActionsWheel),
            ("WristPanel", BindingContext.WristPanel),
            ("Menus", BindingContext.Menus),
        };

        /// <summary>Names that stand for several places: accepted in an action's contexts, never reported.</summary>
        public static readonly (string Name, BindingContext Context)[] Groups =
        {
            ("Gameplay", BindingContext.Gameplay),
        };

        /// <summary>
        /// Reads a comma-separated list of names (<see cref="Names"/>, <see cref="Groups"/>), case as written, spaces around
        /// names ignored. False with <paramref name="why"/> for none, or a name not known.
        /// </summary>
        public static bool TryRead(string text, out BindingContext contexts, out string why)
        {
            contexts = BindingContext.None;
            why = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                why = "no contexts are given (say where the action applies: " + Known() + ")";
                return false;
            }
            foreach (string raw in text.Split(','))
            {
                string name = raw.Trim();
                BindingContext found = Find(name);
                if (found == BindingContext.None)
                {
                    why = $"\"{name}\" is not a context (known: {Known()})";
                    return false;
                }
                contexts |= found;
            }
            return true;
        }

        /// <summary>Where the player is, by name, comma-separated in the order of <see cref="Names"/>; empty for nowhere.</summary>
        public static string Write(BindingContext contexts)
        {
            var text = new StringBuilder();
            foreach ((string name, BindingContext c) in Names)
            {
                if ((contexts & c) == 0)
                    continue;
                if (text.Length > 0)
                    text.Append(',');
                text.Append(name);
            }
            return text.ToString();
        }

        private static BindingContext Find(string name)
        {
            foreach ((string n, BindingContext c) in Names)
                if (n == name)
                    return c;
            foreach ((string n, BindingContext c) in Groups)
                if (n == name)
                    return c;
            return BindingContext.None;
        }

        private static string Known()
        {
            var names = new List<string>();
            foreach ((string n, BindingContext _) in Names)
                names.Add(n);
            foreach ((string n, BindingContext _) in Groups)
                names.Add(n);
            return string.Join(", ", names);
        }
    }
}
