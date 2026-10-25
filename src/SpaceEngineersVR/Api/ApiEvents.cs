// ApiEvents: named events other plugins and mods subscribe to, raised on the game thread when a change is seen (no game types; tests/ApiTests runs it).
using System;
using SpaceEngineersVR.Input.Bindings;

namespace SpaceEngineersVR.Api
{
    /// <summary>
    /// Events through SevrApi version 2 (<c>Subscribe</c>): a plain <see cref="Action"/> per owner and event name, called on
    /// the game thread under the owner's guard when the plugin sees the change (<see cref="Observe"/>, once a frame), so at most
    /// a frame after it happened. The handler reads what it needs through the other members. Subscribing the same owner to the
    /// same event again replaces its handler.
    /// </summary>
    internal sealed class ApiEvents : IApiRegistry
    {
        /// <summary>VR came on or went off (the member IsActive changed).</summary>
        public const string ActiveChanged = "ActiveChanged";

        /// <summary>The player switched between right- and left-handed (DominantHand changed).</summary>
        public const string DominantHandChanged = "DominantHandChanged";

        /// <summary>Where the player is, for the bindings, changed (Context changed).</summary>
        public const string ContextChanged = "ContextChanged";

        /// <summary>The HUD was shown or hidden, or moved or resized (HudVisible, HudDepth or HudTangents changed).</summary>
        public const string HudChanged = "HudChanged";

        /// <summary>The bindings in use changed: the player rebound something, the controller changed, or an action was registered or taken away.</summary>
        public const string BindingsChanged = "BindingsChanged";

        /// <summary>Every event name.</summary>
        public static readonly string[] Names = { ActiveChanged, DominantHandChanged, ContextChanged, HudChanged, BindingsChanged };

        /// <summary>The state the events are about, as seen in one frame.</summary>
        internal struct State : IEquatable<State>
        {
            public bool Active;
            public int DominantHand;
            public BindingContext Context;
            public bool HudVisible;
            public double HudDepth, HudTangentX, HudTangentY;
            public int Bindings;

            public bool Equals(State o) => Active == o.Active && DominantHand == o.DominantHand && Context == o.Context && SameHud(o)
                                           && Bindings == o.Bindings;

            public bool SameHud(State o) => HudVisible == o.HudVisible && HudDepth.Equals(o.HudDepth) && HudTangentX.Equals(o.HudTangentX)
                                            && HudTangentY.Equals(o.HudTangentY);

            public override bool Equals(object obj) => obj is State s && Equals(s);

            public override int GetHashCode() => (Active ? 1 : 0) ^ DominantHand << 1 ^ (int)Context << 4 ^ Bindings << 8;
        }

        private readonly ApiCore core;
        private readonly ApiList<Action> list = new ApiList<Action>();
        private State last;
        private bool seen;

        public ApiEvents(ApiCore core)
        {
            this.core = core ?? throw new ArgumentNullException(nameof(core));
            core.Add(this);
        }

        /// <summary>The name is one of <see cref="Names"/>.</summary>
        public static bool IsEvent(string name) => Array.IndexOf(Names, name) >= 0;

        /// <summary>Subscribes (or replaces) <paramref name="owner"/>'s handler for <paramref name="name"/>. False, refused and logged once, on invalid input.</summary>
        public bool Subscribe(string owner, string name, Action handler, ApiScope scope, string member)
        {
            if (!core.Admit(owner, member))
                return false;
            if (!IsEvent(name))
            {
                core.Refuse(owner, member, $"\"{name}\" is not an event (known: {string.Join(", ", Names)})");
                return false;
            }
            if (handler == null)
            {
                core.Refuse(owner, member, "the handler is null");
                return false;
            }
            list.Set(new ApiList<Action>.Entry(owner, name, scope, core.World, handler));
            return true;
        }

        /// <summary>Takes away <paramref name="owner"/>'s handler for <paramref name="name"/>. False when it had none (or the call is refused).</summary>
        public bool Unsubscribe(string owner, string name, string member)
        {
            if (!core.Admit(owner, member))
                return false;
            if (!IsEvent(name))
            {
                core.Refuse(owner, member, $"\"{name}\" is not an event (known: {string.Join(", ", Names)})");
                return false;
            }
            return list.Remove(ApiList<Action>.Picks(owner, name)).Count > 0;
        }

        /// <inheritdoc/>
        /// <remarks>An id names an action or other registration, never a subscription (<see cref="Unsubscribe"/> takes those away), so only a whole owner's or a scope's go here.</remarks>
        public int Remove(string owner, string id) => id != null ? 0 : list.Remove(ApiList<Action>.Picks(owner, null)).Count;

        /// <inheritdoc/>
        public int RemoveOtherWorlds(object world) => list.Remove(ApiList<Action>.OtherWorlds(world)).Count;

        /// <summary>
        /// Game thread, once a frame: the state now. Raises an event for each part that differs from the frame before (the first
        /// call only takes the state).
        /// </summary>
        public void Observe(State now)
        {
            State before = last;
            bool first = !seen;
            last = now;
            seen = true;
            if (first || now.Equals(before))
                return;
            if (now.Active != before.Active)
                Raise(ActiveChanged);
            if (now.DominantHand != before.DominantHand)
                Raise(DominantHandChanged);
            if (now.Context != before.Context)
                Raise(ContextChanged);
            if (!now.SameHud(before))
                Raise(HudChanged);
            if (now.Bindings != before.Bindings)
                Raise(BindingsChanged);
        }

        /// <summary>Calls every handler of <paramref name="name"/>, each under its owner's guard, from the subscriptions as they are now.</summary>
        public void Raise(string name)
        {
            foreach (ApiList<Action>.Entry e in list.Snapshot)
                if (e.Key == name)
                    core.Invoke(e.Owner, name + " handler", e.Value);
        }
    }
}
