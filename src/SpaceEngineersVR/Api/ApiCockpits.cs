// ApiCockpits: mods' cockpit blocks that are a game cockpit inside, joining its VR controls (no game types; tests/GameModelTests runs it).
using System;
using System.Collections.Generic;

namespace SpaceEngineersVR.Api
{
    /// <summary>
    /// The cockpits registered through SevrApi version 2 (<c>RegisterCockpit</c>), by the block's subtype: a mod's cockpit
    /// block built on one of the game's cockpits (its model, or its interior model, is that cockpit's) gets that cockpit's VR
    /// controls, ColbyJack1134's: the sticks, switches, levers, handles and buttons where the model has them. The controls are
    /// measured on the game's own meshes, so a block whose model is not the named cockpit's is not given them (it is checked
    /// when the player sits, as for the game's cockpits).
    /// <list type="bullet">
    /// <item>The game's cockpits that have the controls cannot be registered: theirs are the plugin's, the same for every
    /// player.</item>
    /// <item><c>like</c> names one of the game's cockpits that has the controls; a registered one cannot be named.</item>
    /// <item>Two owners registering one subtype: the first by owner id (ordinal) is used, so load order never decides it.</item>
    /// <item>Registering the same owner and subtype again replaces what it is like.</item>
    /// </list>
    /// </summary>
    internal sealed class ApiCockpits : IApiRegistry
    {
        /// <summary>The most cockpit subtypes one owner may register.</summary>
        public const int MaxPerOwner = 64;

        /// <summary>The game cockpit a registration is like (the list holds reference types).</summary>
        private sealed class Registration
        {
            public readonly string Like;

            public Registration(string like) => Like = like;
        }

        private readonly ApiCore core;
        private readonly Func<string, bool> hasControls;
        private readonly ApiList<Registration> list = new ApiList<Registration>();
        private Dictionary<string, string> bySubtype = new Dictionary<string, string>(StringComparer.Ordinal);
        private int builtFor = -1;

        /// <param name="hasControls">The game's cockpit with this block subtype has the plugin's VR controls (asked at registration).</param>
        public ApiCockpits(ApiCore core, Func<string, bool> hasControls)
        {
            this.core = core ?? throw new ArgumentNullException(nameof(core));
            this.hasControls = hasControls ?? throw new ArgumentNullException(nameof(hasControls));
            core.Add(this);
        }

        /// <summary>Changes each time a cockpit is registered, replaced or taken away.</summary>
        public int Version { get; private set; }

        /// <summary>
        /// Registers (or replaces) <paramref name="owner"/>'s cockpit block <paramref name="subtype"/> as built on the game's
        /// cockpit <paramref name="like"/> (both block subtype ids). False, refused and logged once, on invalid input.
        /// </summary>
        public bool Register(string owner, string subtype, string like, ApiScope scope, string member)
        {
            if (!core.Admit(owner, member))
                return false;
            if (!ApiIds.IsSubtype(subtype))
                return Refuse(owner, member, SubtypeRule("cockpit"));
            if (!ApiIds.IsSubtype(like))
                return Refuse(owner, member, SubtypeRule("cockpit it is like"));
            if (hasControls(subtype))
                return Refuse(owner, member, $"\"{subtype}\" is the game's cockpit and has its VR controls already");
            if (!hasControls(like))
                return Refuse(owner, member, $"\"{like}\" is not one of the game's cockpits with VR controls (docs/MOD_API.md, Cockpits, lists them)");
            if (list.Find(owner, subtype) == null && list.CountOf(owner) >= MaxPerOwner)
                return Refuse(owner, member, $"the owner has {MaxPerOwner} cockpits registered already");
            list.Set(new ApiList<Registration>.Entry(owner, subtype, scope, core.World, new Registration(like)));
            Version++;
            return true;
        }

        /// <summary>Takes away <paramref name="owner"/>'s registration of <paramref name="subtype"/>; false when it had none.</summary>
        public bool Unregister(string owner, string subtype, string member)
        {
            if (!core.Admit(owner, member))
                return false;
            if (!ApiIds.IsSubtype(subtype))
                return Refuse(owner, member, SubtypeRule("cockpit"));
            return Drop(list.Remove(e => e.Owner == owner && e.Key == subtype).Count) > 0;
        }

        /// <inheritdoc/>
        /// <remarks>An id alone names an action, never a cockpit (<see cref="Unregister"/> takes one away), so only a whole owner's go here.</remarks>
        public int Remove(string owner, string id) => id != null ? 0 : Drop(list.Remove(ApiList<Registration>.Picks(owner, null)).Count);

        /// <inheritdoc/>
        public int RemoveOtherWorlds(object world) => Drop(list.Remove(ApiList<Registration>.OtherWorlds(world)).Count);

        /// <summary>The game cockpit the block <paramref name="subtype"/> is registered as built on; null when it is not registered.</summary>
        public string Like(string subtype)
        {
            if (subtype == null)
                return null;
            if (builtFor != Version)
                Rebuild();
            return bySubtype.TryGetValue(subtype, out string like) ? like : null;
        }

        private static string SubtypeRule(string what) =>
            $"the {what} is not a block's subtype id (1 to {ApiIds.MaxSubtype} characters, no control characters, no space at either end)";

        private void Rebuild()
        {
            int version = Version;
            var sorted = new List<ApiList<Registration>.Entry>(list.Snapshot);
            sorted.Sort((a, b) => string.CompareOrdinal(a.Owner, b.Owner));
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (ApiList<Registration>.Entry e in sorted)
                if (!map.ContainsKey(e.Key))
                    map[e.Key] = e.Value.Like;
            bySubtype = map;
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
