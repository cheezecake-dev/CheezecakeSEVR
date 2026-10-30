// ApiGuns: the sights mods give their hand guns, which a model does not carry (VRageMath only; tests/GameModelTests runs it).
using System;
using System.Collections.Generic;
using VRageMath;

namespace SpaceEngineersVR.Api
{
    /// <summary>
    /// The gun sights registered through SevrApi version 2 (<c>RegisterGunSight</c>), by the gun's physical item subtype. A
    /// hand gun's kind, muzzle and magazine come from its own definition and model (<see cref="Hands.GunModel"/>); its sight is
    /// not in either, so a mod gives it here, and with one the gun is aimed down its sights as the game's are.
    /// <list type="bullet">
    /// <item>The game's own guns keep ColbyJack1134's measured sights: a registration for a game item is used only while that
    /// item has another model (a mod that reskins it).</item>
    /// <item>Two owners registering one item: the first by owner id (ordinal) is used, so load order never decides it.</item>
    /// <item>Registering the same owner and item again replaces its sight.</item>
    /// </list>
    /// </summary>
    internal sealed class ApiGuns : IApiRegistry
    {
        /// <summary>The most gun items one owner may register.</summary>
        public const int MaxPerOwner = 64;

        /// <summary>The farthest a sight's aim point may be from the gun model's origin, metres.</summary>
        public const double MaxDistance = 2.0;

        /// <summary>The shortest sight direction taken that is not zero (zero means along the bore).</summary>
        public const double MinDirection = 1e-3;

        /// <summary>A gun's sight, in the gun model's space: the aim point, and the sight line's unit direction (forward, -Z, for along the bore).</summary>
        internal readonly struct Sight
        {
            public readonly Vector3 Point, Direction;

            public Sight(Vector3 point, Vector3 direction)
            {
                Point = point;
                Direction = direction;
            }
        }

        /// <summary>One registration (the list holds reference types).</summary>
        private sealed class Registration
        {
            public readonly Sight Sight;

            public Registration(Sight sight) => Sight = sight;
        }

        private readonly ApiCore core;
        private readonly ApiList<Registration> list = new ApiList<Registration>();
        private Dictionary<string, Sight> byItem = new Dictionary<string, Sight>(StringComparer.Ordinal);
        private int builtFor = -1;

        public ApiGuns(ApiCore core)
        {
            this.core = core ?? throw new ArgumentNullException(nameof(core));
            core.Add(this);
        }

        /// <summary>Changes each time a sight is registered, replaced or taken away.</summary>
        public int Version { get; private set; }

        /// <summary>
        /// Registers (or replaces) <paramref name="owner"/>'s sight for the gun whose physical item subtype is
        /// <paramref name="item"/>: the aim point <paramref name="sight"/> and the sight line's <paramref name="direction"/>, in
        /// the gun model's space (metres; forward is -Z); a zero direction is along the bore. False, refused and logged once, on
        /// invalid input.
        /// </summary>
        public bool Register(string owner, string item, Vector3D sight, Vector3D direction, ApiScope scope, string member)
        {
            if (!core.Admit(owner, member))
                return false;
            if (!ApiIds.IsSubtype(item))
                return Refuse(owner, member, ItemRule);
            if (!IsFinite(sight) || sight.Length() > MaxDistance)
                return Refuse(owner, member, $"the sight's aim point is not a point within {MaxDistance:0.#} m of the gun model's origin");
            if (!IsFinite(direction))
                return Refuse(owner, member, "the sight's direction is not a number");
            double length = direction.Length();
            if (length > 0 && length < MinDirection)
                return Refuse(owner, member, "the sight's direction is too short to have one; give zero for along the bore");
            Vector3D unit = length > 0 ? direction / length : Vector3D.Forward;
            if (Vector3D.Dot(unit, Vector3D.Forward) <= 0)
                return Refuse(owner, member, "the sight's direction does not point forward of the gun (-Z, where it fires)");
            if (list.Find(owner, item) == null && list.CountOf(owner) >= MaxPerOwner)
                return Refuse(owner, member, $"the owner has {MaxPerOwner} gun sights registered already");
            list.Set(new ApiList<Registration>.Entry(owner, item, scope, core.World, new Registration(new Sight((Vector3)sight, (Vector3)unit))));
            Version++;
            return true;
        }

        /// <summary>Takes away <paramref name="owner"/>'s sight for <paramref name="item"/>; false when it had none.</summary>
        public bool Unregister(string owner, string item, string member)
        {
            if (!core.Admit(owner, member))
                return false;
            if (!ApiIds.IsSubtype(item))
                return Refuse(owner, member, ItemRule);
            return Drop(list.Remove(e => e.Owner == owner && e.Key == item).Count) > 0;
        }

        /// <inheritdoc/>
        /// <remarks>An id alone names an action, never a gun (<see cref="Unregister"/> takes one away), so only a whole owner's go here.</remarks>
        public int Remove(string owner, string id) => id != null ? 0 : Drop(list.Remove(ApiList<Registration>.Picks(owner, null)).Count);

        /// <inheritdoc/>
        public int RemoveOtherWorlds(object world) => Drop(list.Remove(ApiList<Registration>.OtherWorlds(world)).Count);

        /// <summary>The sight registered for the gun item <paramref name="item"/>; null when none is.</summary>
        public Sight? Of(string item)
        {
            if (item == null)
                return null;
            if (builtFor != Version)
                Rebuild();
            return byItem.TryGetValue(item, out Sight sight) ? sight : (Sight?)null;
        }

        private static readonly string ItemRule =
            $"the gun is not a physical item's subtype id (1 to {ApiIds.MaxSubtype} characters, no control characters, no space at either end)";

        private static bool IsFinite(Vector3D v) => IsFinite(v.X) && IsFinite(v.Y) && IsFinite(v.Z);

        private static bool IsFinite(double d) => !double.IsNaN(d) && !double.IsInfinity(d);

        private void Rebuild()
        {
            int version = Version;
            var sorted = new List<ApiList<Registration>.Entry>(list.Snapshot);
            sorted.Sort((a, b) => string.CompareOrdinal(a.Owner, b.Owner));
            var map = new Dictionary<string, Sight>(StringComparer.Ordinal);
            foreach (ApiList<Registration>.Entry e in sorted)
                if (!map.ContainsKey(e.Key))
                    map[e.Key] = e.Value.Sight;
            byItem = map;
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
