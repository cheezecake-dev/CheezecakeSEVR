// ApiMenuEntries: the actions wheel's entries other plugins and mods registered through SevrApi (RegisterMenuItem).
using System.Collections.Generic;
using SpaceEngineersVR.Api;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// Puts the items other plugins and mods registered for the actions wheel (<see cref="ApiMenus"/>, menu
    /// <see cref="ApiMenus.ActionsWheel"/>) on it, after the plugin's own, each made an entry like the plugin's own: its icon and
    /// label, greyed out with "Unavailable now" while it cannot be used, its state under its label, a page opening a ring of its
    /// own with Back first. Using an item runs its owner's code under the owner's guard and closes the wheel, or, for one
    /// registered with "StaysOpen", leaves it up when used with the trigger, as the plugin's own switches do.
    /// <para>
    /// Up to <see cref="MaxOnTop"/> items go on the top ring; with more (several plugins at once), they all go on one page,
    /// "More", so the wheel the player knows keeps its places and its size.
    /// </para>
    /// </summary>
    internal static class ApiMenuEntries
    {
        /// <summary>The most outside items the top ring takes before they go on a page of their own.</summary>
        internal const int MaxOnTop = 6;

        /// <summary>Adds the entries to the end of the top ring. Game thread, as the wheel opens.</summary>
        internal static void AddTo(List<ActionsWheel.Entry> top)
        {
            ApiMenus menus = ApiRuntime.Menus;
            List<ApiMenus.Node> nodes = menus.Tree(ApiMenus.ActionsWheel);
            if (nodes.Count == 0)
                return;
            var entries = new List<ActionsWheel.Entry>(nodes.Count);
            foreach (ApiMenus.Node node in nodes)
                entries.Add(Entry(menus, node));
            if (entries.Count <= MaxOnTop)
            {
                top.AddRange(entries);
                return;
            }
            var page = new List<ActionsWheel.Entry> { Back() };
            page.AddRange(entries);
            top.Add(new ActionsWheel.Entry { Name = "More", Icon = entries[0].Icon, Page = page });
        }

        private static ActionsWheel.Entry Entry(ApiMenus menus, ApiMenus.Node node)
        {
            var entry = new ActionsWheel.Entry
            {
                Name = node.Item.Label,
                Icon = node.Item.Icon,
                CanUse = () => menus.CanUse(node),
            };
            if (node.Item.State != null)
                entry.StateText = () => menus.State(node);
            if (!node.Item.IsPage)
            {
                entry.Run = () => menus.Use(node);
                entry.Stays = node.Item.StaysOpen;
                return entry;
            }
            var page = new List<ActionsWheel.Entry> { Back() };
            foreach (ApiMenus.Node child in node.Children)
                page.Add(Entry(menus, child));
            entry.Page = page;
            return entry;
        }

        private static ActionsWheel.Entry Back() => new ActionsWheel.Entry { Name = "Back", Icon = GuiTextures.Back, IsBack = true };
    }
}
