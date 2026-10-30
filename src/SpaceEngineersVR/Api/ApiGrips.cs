// ApiGrips: grips other plugins and mods take through SevrApi version 2, asked in the plugin's grip order (no game types; tests/ApiTests runs it).
using System;
using System.Collections.Generic;

namespace SpaceEngineersVR.Api
{
    /// <summary>
    /// The grip claims registered through SevrApi version 2 (<c>RegisterGripClaim</c>). As a grip closes in the world, after the
    /// plugin's own takers ahead of them (the held tool, the helmet, items, use objects: the grip table's order), each claim is
    /// asked whether the press is its, until one says yes; it then holds that grip until the grip opens, and is told so once.
    /// <list type="bullet">
    /// <item>Claims are asked by owner id, then id (ordinal), so load order never decides it.</item>
    /// <item>Registering the same owner and id again replaces its callbacks.</item>
    /// <item>A claim taken away while it holds a grip is not told; the grip then does nothing until it opens.</item>
    /// <item>Callbacks run under the owner's guard: one switched off, or one that throws, does not take the grip.</item>
    /// </list>
    /// What a claim gets is the hand's number and the press, the player's own grip: nothing a flat player could not know.
    /// </summary>
    internal sealed class ApiGrips : IApiRegistry
    {
        /// <summary>The most grip claims one owner may register.</summary>
        public const int MaxPerOwner = 16;

        /// <summary>The name outside claims hold a grip under in the plugin's grip table.</summary>
        public const string ClaimName = "SEVR API";

        /// <summary>One registration's callbacks, with the delegates its guarded calls use made once (a press allocates nothing).</summary>
        private sealed class Claim
        {
            public readonly Func<bool>[] Take;
            public readonly Action<int> Released;
            public readonly string TakeWhat, ReleasedWhat;

            public Claim(string id, Func<int, bool> take, Action<int> released)
            {
                Take = new Func<bool>[] { () => take(0), () => take(1) };
                Released = released;
                TakeWhat = "grip claim " + id + " take";
                ReleasedWhat = "grip claim " + id + " released";
            }
        }

        private static readonly string[] HandNames = { "left", "right" };

        private readonly ApiCore core;
        private readonly ApiList<Claim> list = new ApiList<Claim>();
        private readonly ApiList<Claim>.Entry[] holder = new ApiList<Claim>.Entry[2];
        private ApiList<Claim>.Entry[] ordered = new ApiList<Claim>.Entry[0];
        private int version, builtFor;

        public ApiGrips(ApiCore core)
        {
            this.core = core ?? throw new ArgumentNullException(nameof(core));
            core.Add(this);
        }

        /// <summary>
        /// Registers (or replaces) <paramref name="owner"/>'s claim <paramref name="id"/>. <paramref name="take"/>: asked with the
        /// hand (0 left, 1 right) as its grip closes; true takes that press. <paramref name="released"/>: told the hand once the
        /// grip it took opens (or VR stops); null for no word. False, refused and logged once, on invalid input.
        /// </summary>
        public bool Register(string owner, string id, Func<int, bool> take, Action<int> released, ApiScope scope, string member)
        {
            if (!core.Admit(owner, member))
                return false;
            if (!ApiIds.IsId(id))
                return Refuse(owner, member, $"the id is not 1 to {ApiIds.MaxLength} letters, digits, '_' or '-'");
            if (take == null)
                return Refuse(owner, member, "take is null: it is what says whether a grip is the claim's");
            if (list.Find(owner, id) == null && list.CountOf(owner) >= MaxPerOwner)
                return Refuse(owner, member, $"the owner has {MaxPerOwner} grip claims registered already");
            list.Set(new ApiList<Claim>.Entry(owner, id, scope, core.World, new Claim(id, take, released)));
            version++;
            return true;
        }

        /// <inheritdoc/>
        /// <remarks>An id names the owner's grip claim as it names its action: <c>UnregisterId</c> takes away both.</remarks>
        public int Remove(string owner, string id) => Drop(list.Remove(ApiList<Claim>.Picks(owner, id)));

        /// <inheritdoc/>
        public int RemoveOtherWorlds(object world) => Drop(list.Remove(ApiList<Claim>.OtherWorlds(world)));

        /// <summary>
        /// Game thread, from the grip table as the grip of <paramref name="hand"/> (0 or 1) closes in the world: true when a claim
        /// takes it. A claim still holding the hand's last press is told first that it ended.
        /// </summary>
        public bool Take(int hand)
        {
            End(hand);
            if (builtFor != version)
                Rebuild();
            foreach (ApiList<Claim>.Entry e in ordered)
            {
                if (!core.Ask(e.Owner, e.Value.TakeWhat, e.Value.Take[hand], fallback: false))
                    continue;
                holder[hand] = e;
                Log.Info($"SEVR API: {e.Owner}'s grip claim {e.Key} took the {HandNames[hand]} grip");
                return true;
            }
            return false;
        }

        /// <summary>
        /// Game thread, once a frame: a claim whose grip no longer is its (<paramref name="holds"/> false: the grip opened, the plugin
        /// let it go, VR stopped) is told, once.
        /// </summary>
        public void Frame(Func<int, bool> holds)
        {
            for (int hand = 0; hand < holder.Length; hand++)
                if (holder[hand] != null && !holds(hand))
                    End(hand);
        }

        private void End(int hand)
        {
            ApiList<Claim>.Entry held = holder[hand];
            if (held == null)
                return;
            holder[hand] = null;
            // Registered again meanwhile: the callbacks told are the ones registered now.
            ApiList<Claim>.Entry e = list.Find(held.Owner, held.Key) ?? held;
            if (e.Value.Released != null)
                core.Invoke(e.Owner, e.Value.ReleasedWhat, e.Value.Released, hand);
        }

        private void Rebuild()
        {
            var sorted = new List<ApiList<Claim>.Entry>(list.Snapshot);
            sorted.Sort((a, b) =>
            {
                int byOwner = string.CompareOrdinal(a.Owner, b.Owner);
                return byOwner != 0 ? byOwner : string.CompareOrdinal(a.Key, b.Key);
            });
            ordered = sorted.ToArray();
            builtFor = version;
        }

        private int Drop(List<ApiList<Claim>.Entry> gone)
        {
            if (gone.Count == 0)
                return 0;
            version++;
            // A claim taken away holds nothing: it is not told, and the grip it took does nothing until it opens.
            for (int hand = 0; hand < holder.Length; hand++)
                if (holder[hand] != null && gone.Contains(holder[hand]))
                    holder[hand] = null;
            return gone.Count;
        }

        private bool Refuse(string owner, string member, string why)
        {
            core.Refuse(owner, member, why);
            return false;
        }
    }
}
