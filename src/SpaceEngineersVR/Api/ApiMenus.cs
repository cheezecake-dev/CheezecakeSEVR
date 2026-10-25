// ApiMenus: items other plugins and mods put on the plugin's menus (the actions wheel, the wrist panel), with pages of their own (no game types; tests/ApiTests runs it).
using System;
using System.Collections.Generic;

namespace SpaceEngineersVR.Api
{
    /// <summary>What a registered menu item does besides running (<see cref="ApiMenus"/>); several together.</summary>
    [Flags]
    internal enum MenuItemOptions
    {
        None = 0,

        /// <summary>
        /// "StaysOpen": using the item with the trigger leaves the menu up for the next use, as the plugin's own switches do (on
        /// the wheel, letting the grip go on it still closes the wheel). For an item that runs, not a page.
        /// </summary>
        StaysOpen = 1,
    }

    /// <summary>
    /// Menu items registered through SevrApi version 2 (<c>RegisterMenuItem</c>, entry point 4). Each is a real entry of the
    /// menu it names, drawn as the plugin's own are (the game's icon and words, greyed out with "Unavailable now" while it
    /// cannot be used, its state under its name) and used the same way; using it runs the caller's code. Fairness: an item
    /// can only do what its caller's code could do from a key or a button of its own.
    /// <list type="bullet">
    /// <item>An item with no <c>run</c> is a page: choosing it opens a ring (on the wrist panel, a page of tiles) of its own
    /// with Back first, and the items whose <c>parent</c> is its id. A page's parent must be registered before its items (so there are no loops), and taking a
    /// page away takes away what is under it.</item>
    /// <item>Items come after the plugin's own, owner by owner in the order of their ids (so plugin load order never decides
    /// it), each owner's in the order it first registered them. Registering the same owner, menu and id again replaces the
    /// item in its place.</item>
    /// <item>The owner's <c>run</c>, <c>canUse</c> and <c>state</c> run under its guard: one that throws is logged with the
    /// owner's name, and a burst switches the owner off, its items then greyed out.</item>
    /// </list>
    /// </summary>
    internal sealed class ApiMenus : IApiRegistry
    {
        /// <summary>The options' public names, as a registration gives them, in the order of their flags (<see cref="MenuItemOptions"/>).</summary>
        public static readonly string[] OptionNames = { "StaysOpen" };

        /// <summary>The actions wheel (the left stick click on the tools wheel): items on its top ring, and pages under them.</summary>
        public const string ActionsWheel = "ActionsWheel";

        /// <summary>
        /// The wrist panel (Y on foot and in the jetpack; in a seat, the actions wheel's "Quick actions" entry): its "Add-ons"
        /// tile, in the row across its top, opens a page of the items' tiles. The tile is there only while an item is registered.
        /// </summary>
        public const string WristPanel = "WristPanel";

        /// <summary>The menus items can be put on, by their stable public names.</summary>
        public static readonly string[] Names = { ActionsWheel, WristPanel };

        /// <summary>The most items one owner may register, on every menu together.</summary>
        public const int MaxPerOwner = 64;

        /// <summary>The most items one owner may put on a menu's top ring: more go on a page of its own.</summary>
        public const int MaxTopPerOwner = 4;

        /// <summary>The most items a page holds (Back comes with them; past 12 the ring is drawn bigger, and the wrist panel goes on to a "More" page).</summary>
        public const int MaxPerPage = 15;

        /// <summary>How many rings deep a menu goes: the top ring (the wrist panel's Add-ons page), a page, and a page on it (an item on that last one is at depth 2).</summary>
        public const int MaxDepth = 3;

        /// <summary>The longest label: the name under the ring, one line.</summary>
        public const int MaxLabel = 40;

        /// <summary>The longest state shown under the label; a longer one is cut.</summary>
        public const int MaxState = 60;

        /// <summary>The longest icon path.</summary>
        public const int MaxIconPath = 260;

        /// <summary>One item, as registered.</summary>
        internal sealed class Item
        {
            public readonly string Menu, Id, Parent, Label, Icon;
            public readonly Action Run;
            public readonly Func<bool> CanUse;
            public readonly Func<string> State;
            public readonly MenuItemOptions Options;

            /// <summary>When it was first registered: items are shown in that order within their owner.</summary>
            public readonly long Order;

            /// <summary>Its key in the registry (<see cref="Key"/>), and how the log names its callbacks: made once here, so a menu drawn each frame builds no strings.</summary>
            public readonly string RegistryKey, RunWhat, CanUseWhat, StateWhat;

            public Item(string menu, string id, string parent, string label, string icon, Action run, Func<bool> canUse, Func<string> state,
                        MenuItemOptions options, long order)
            {
                Menu = menu;
                Id = id;
                Parent = parent;
                Label = label;
                Icon = icon;
                Run = run;
                CanUse = canUse;
                State = state;
                Options = options;
                Order = order;
                RegistryKey = Key(menu, id);
                RunWhat = "run for menu item " + id;
                CanUseWhat = "canUse for menu item " + id;
                StateWhat = "state for menu item " + id;
            }

            /// <summary>Choosing it opens a page of the items under it.</summary>
            public bool IsPage => Run == null;

            /// <summary>Using it with the trigger leaves the menu up (<see cref="MenuItemOptions.StaysOpen"/>).</summary>
            public bool StaysOpen => (Options & MenuItemOptions.StaysOpen) != 0;
        }

        /// <summary>An item and the items under it (a page's), as a menu shows them now.</summary>
        internal sealed class Node
        {
            public readonly string Owner;
            public readonly Item Item;
            public readonly List<Node> Children = new List<Node>();

            public Node(string owner, Item item)
            {
                Owner = owner;
                Item = item;
            }
        }

        private readonly ApiCore core;
        private readonly Func<string, bool> iconExists;
        private readonly ApiList<Item> list = new ApiList<Item>();
        private long order;

        /// <param name="iconExists">
        /// Whether a texture file is there for an icon path as given (the plugin: a path under the game's Content folder, or a full
        /// path, through the game's file system so a mod's packed files count).
        /// </param>
        public ApiMenus(ApiCore core, Func<string, bool> iconExists)
        {
            this.core = core ?? throw new ArgumentNullException(nameof(core));
            this.iconExists = iconExists ?? throw new ArgumentNullException(nameof(iconExists));
            core.Add(this);
        }

        /// <summary>Changes each time an item is registered, replaced or taken away.</summary>
        public int Version { get; private set; }

        /// <summary>The key an item is listed under: its menu and id, so an id alone (an action's) never names one.</summary>
        private static string Key(string menu, string id) => menu + "/" + id;

        /// <summary>
        /// Registers (or replaces) <paramref name="owner"/>'s item <paramref name="id"/> on <paramref name="menu"/>. False, refused
        /// and logged once, on invalid input; see the member's documentation in <see cref="SevrApi"/>.
        /// </summary>
        public bool Register(string owner, string menu, string id, string parent, string label, string icon, Action run, Func<bool> canUse,
                             Func<string> state, string options, ApiScope scope, string member)
        {
            if (!core.Admit(owner, member))
                return false;
            if (Array.IndexOf(Names, menu) < 0)
                return Refuse(owner, member, $"\"{menu}\" is not a menu (known: {string.Join(", ", Names)})");
            if (!ApiIds.IsId(id))
                return Refuse(owner, member, $"the item id is not 1 to {ApiIds.MaxLength} letters, digits, '_' or '-'");
            string under = string.IsNullOrEmpty(parent) ? null : parent;
            if (under != null && !ApiIds.IsId(under))
                return Refuse(owner, member, $"the parent is not empty (the top) or an item id of 1 to {ApiIds.MaxLength} letters, digits, '_' or '-'");
            if (under == id)
                return Refuse(owner, member, "an item cannot be its own parent");
            string name = label?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > MaxLabel || ApiCore.HasControlCharacters(name))
                return Refuse(owner, member, $"the label is empty, over {MaxLabel} characters, or has line breaks or other control characters");
            if (!IconOk(icon, out string whyIcon))
                return Refuse(owner, member, whyIcon);
            if (!ApiOptions.TryParse(options, OptionNames, out int bits, out string whyOptions))
                return Refuse(owner, member, whyOptions);
            var parsed = (MenuItemOptions)bits;
            if (run == null && (parsed & MenuItemOptions.StaysOpen) != 0)
                return Refuse(owner, member, "StaysOpen is for an item that runs; a page opens its items and never closes the menu");

            ApiList<Item>.Entry[] all = list.Snapshot;
            ApiList<Item>.Entry existing = list.Find(owner, Key(menu, id));
            if (existing != null && existing.Value.IsPage && run != null && HasChildren(all, owner, menu, id))
                return Refuse(owner, member, $"the page \"{id}\" has items under it; take them away before making it an item that runs");

            int depth = 0;
            if (under != null)
            {
                ApiList<Item>.Entry page = list.Find(owner, Key(menu, under));
                if (page == null)
                    return Refuse(owner, member, $"the parent \"{under}\" is not one of the owner's items on {menu}; register the page first");
                if (!page.Value.IsPage)
                    return Refuse(owner, member, $"the parent \"{under}\" is not a page (a page is registered with no run)");
                for (string up = under; up != null; up = list.Find(owner, Key(menu, up))?.Value.Parent)
                {
                    if (up == id)
                        return Refuse(owner, member, $"\"{under}\" is under \"{id}\": an item cannot go under its own page");
                    depth++;
                }
            }
            // A page needs a ring under it, and what is under a page being moved must still sit within the limit.
            int below = Math.Max(run == null ? 1 : 0, existing == null ? 0 : Height(all, owner, menu, id));
            if (depth + below > MaxDepth - 1)
                return Refuse(owner, member, $"pages go at most {MaxDepth} rings deep (the top ring and two pages under it)");

            bool moved = existing == null || existing.Value.Parent != under;
            if (existing == null && list.CountOf(owner) >= MaxPerOwner)
                return Refuse(owner, member, $"the owner has {MaxPerOwner} menu items already");
            if (moved && under == null && CountUnder(all, owner, menu, null) >= MaxTopPerOwner)
                return Refuse(owner, member, $"the owner has {MaxTopPerOwner} items on the top of {menu} already; put more on a page of its own");
            if (moved && under != null && CountUnder(all, owner, menu, under) >= MaxPerPage)
                return Refuse(owner, member, $"the page \"{under}\" holds {MaxPerPage} items already");

            long first = existing?.Value.Order ?? ++order;
            list.Set(new ApiList<Item>.Entry(owner, Key(menu, id), scope, core.World,
                new Item(menu, id, under, name, icon, run, canUse, state, parsed, first)));
            Version++;
            return true;
        }

        /// <summary>
        /// Takes away <paramref name="owner"/>'s item <paramref name="id"/> on <paramref name="menu"/>, and, for a page, the items
        /// under it. False when it had none (or the call is refused).
        /// </summary>
        public bool Unregister(string owner, string menu, string id, string member)
        {
            if (!core.Admit(owner, member))
                return false;
            if (Array.IndexOf(Names, menu) < 0)
                return Refuse(owner, member, $"\"{menu}\" is not a menu (known: {string.Join(", ", Names)})");
            if (!ApiIds.IsId(id))
                return Refuse(owner, member, $"the item id is not 1 to {ApiIds.MaxLength} letters, digits, '_' or '-'");
            string key = Key(menu, id);
            return Drop(list.Remove(e => e.Owner == owner && e.Key == key)) > 0;
        }

        /// <inheritdoc/>
        /// <remarks>An id alone names an action, never a menu item (<see cref="Unregister"/> takes one away), so only a whole owner's go here.</remarks>
        public int Remove(string owner, string id) => id != null ? 0 : Drop(list.Remove(ApiList<Item>.Picks(owner, null)));

        /// <inheritdoc/>
        public int RemoveOtherWorlds(object world) => Drop(list.Remove(ApiList<Item>.OtherWorlds(world)));

        /// <summary>
        /// Items went: the ones under a page that went go too (however they were made, so a page made over the mod channel never
        /// leaves items with nothing to stand under). How many went in all.
        /// </summary>
        private int Drop(List<ApiList<Item>.Entry> gone)
        {
            int count = gone.Count;
            while (true)
            {
                ApiList<Item>.Entry[] now = list.Snapshot;
                var orphans = new HashSet<ApiList<Item>.Entry>();
                foreach (ApiList<Item>.Entry e in now)
                    if (e.Value.Parent != null && list.Find(e.Owner, Key(e.Value.Menu, e.Value.Parent)) == null)
                        orphans.Add(e);
                if (orphans.Count == 0)
                    break;
                count += list.Remove(orphans.Contains).Count;
            }
            if (count > 0)
                Version++;
            return count;
        }

        /// <summary>
        /// What <paramref name="menu"/> shows from outside now: the top items, owner by owner in the ordinal order of their ids, each
        /// owner's in the order first registered, with each page's items under it in the same order.
        /// </summary>
        public List<Node> Tree(string menu)
        {
            ApiList<Item>.Entry[] all = list.Snapshot;
            var sorted = new List<ApiList<Item>.Entry>();
            foreach (ApiList<Item>.Entry e in all)
                if (e.Value.Menu == menu)
                    sorted.Add(e);
            sorted.Sort((a, b) =>
            {
                int byOwner = string.CompareOrdinal(a.Owner, b.Owner);
                return byOwner != 0 ? byOwner : a.Value.Order.CompareTo(b.Value.Order);
            });
            var nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
            foreach (ApiList<Item>.Entry e in sorted)
                nodes[e.Owner + "|" + e.Value.Id] = new Node(e.Owner, e.Value);
            var top = new List<Node>();
            foreach (ApiList<Item>.Entry e in sorted)
            {
                Node node = nodes[e.Owner + "|" + e.Value.Id];
                if (e.Value.Parent == null)
                    top.Add(node);
                else if (nodes.TryGetValue(e.Owner + "|" + e.Value.Parent, out Node page))
                    page.Children.Add(node);
            }
            return top;
        }

        /// <summary>
        /// The item can be used now: still registered as it was shown, its owner's callbacks on, and its <c>canUse</c> answer yes
        /// (null is always), asked under the owner's guard.
        /// </summary>
        public bool CanUse(Node node)
        {
            if (!Current(node) || !core.OwnerEnabled(node.Owner))
                return false;
            Func<bool> canUse = node.Item.CanUse;
            return canUse == null || core.Ask(node.Owner, node.Item.CanUseWhat, canUse, fallback: false);
        }

        /// <summary>
        /// How many of a page's <paramref name="count"/> items one screen of <paramref name="cells"/> cells shows: all when they fit,
        /// else one fewer, so the last cell is "More", which opens the rest the same way.
        /// </summary>
        public static int ShownBeforeMore(int count, int cells) => count <= cells ? Math.Max(0, count) : Math.Max(1, cells - 1);

        /// <summary>The item's state, one line of at most <see cref="MaxState"/> characters; empty with none, or when it throws.</summary>
        public string State(Node node)
        {
            Func<string> state = node.Item.State;
            if (state == null || !Current(node))
                return string.Empty;
            return ApiCore.OneLine(core.Ask(node.Owner, node.Item.StateWhat, state, fallback: string.Empty), MaxState);
        }

        /// <summary>Uses the item: its <c>run</c>, under its owner's guard, if it can be used now. True when it ran without throwing.</summary>
        public bool Use(Node node)
        {
            if (node.Item.IsPage || !CanUse(node))
                return false;
            return core.Invoke(node.Owner, node.Item.RunWhat, node.Item.Run);
        }

        /// <summary>The item shown is the one registered now (not taken away or replaced since the menu opened).</summary>
        private bool Current(Node node) => ReferenceEquals(list.Find(node.Owner, node.Item.RegistryKey)?.Value, node.Item);

        private bool IconOk(string icon, out string why)
        {
            why = null;
            if (string.IsNullOrWhiteSpace(icon))
                why = "no icon is given (a .dds or .png texture: a path under the game's Content folder, or a full path such as a mod's "
                      + "ModContext.ModPath joined with its file)";
            else if (icon.Length > MaxIconPath || ApiCore.HasControlCharacters(icon))
                why = $"the icon path is over {MaxIconPath} characters or has control characters";
            else if (!icon.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) && !icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                why = $"the icon \"{icon}\" is not a .dds or .png texture";
            else if (!Exists(icon))
                why = $"the icon \"{icon}\" is not found (a path under the game's Content folder, or a full path)";
            return why == null;
        }

        private bool Exists(string icon)
        {
            try
            {
                return iconExists(icon);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool HasChildren(ApiList<Item>.Entry[] all, string owner, string menu, string id) => CountUnder(all, owner, menu, id) > 0;

        private static int CountUnder(ApiList<Item>.Entry[] all, string owner, string menu, string parent)
        {
            int n = 0;
            foreach (ApiList<Item>.Entry e in all)
                if (e.Owner == owner && e.Value.Menu == menu && e.Value.Parent == parent)
                    n++;
            return n;
        }

        /// <summary>How many levels of pages are under the item (0 for an item, or a page with nothing under it).</summary>
        private static int Height(ApiList<Item>.Entry[] all, string owner, string menu, string id)
        {
            int most = 0;
            foreach (ApiList<Item>.Entry e in all)
                if (e.Owner == owner && e.Value.Menu == menu && e.Value.Parent == id)
                    most = Math.Max(most, 1 + Height(all, owner, menu, e.Value.Id));
            return most;
        }

        private bool Refuse(string owner, string member, string why)
        {
            core.Refuse(owner, member, why);
            return false;
        }
    }
}
