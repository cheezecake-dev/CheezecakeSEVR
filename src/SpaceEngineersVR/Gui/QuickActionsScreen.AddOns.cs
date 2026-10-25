// QuickActionsScreen, the Add-ons page: the tiles other plugins and mods put on the wrist panel (SevrApi RegisterMenuItem,
// menu "WristPanel").
using System.Collections.Generic;
using System.Text;
using SpaceEngineersVR.Api;

namespace SpaceEngineersVR.Gui
{
    internal sealed partial class QuickActionsScreen
    {
        /// <summary>
        /// The cells of a page of tiles other than Back: the actions page's grid, four rows of <see cref="Columns"/>, so the tiles
        /// are as large as the plugin's own. A page with more goes on to a "More" tile (<see cref="ApiMenus.ShownBeforeMore"/>).
        /// </summary>
        internal const int AddOnCells = 11;

        /// <summary>
        /// The Add-ons tile in the menus row: there only while another plugin or mod has an item on the wrist panel
        /// (<see cref="ApiMenus.WristPanel"/>), so a player with none sees the panel as it always was.
        /// </summary>
        private static readonly QuickAction AddOnsTile = new QuickAction("Add-ons",
            "Actions other plugins and mods put on the wrist panel.", Hud + "plus.png", null, whiteIcon: true);

        private const string AddOnsHint = "Point at an action and pull the trigger; the foot names the plugin or mod it is from";

        /// <summary>One page of the Add-ons pages: the items it shows, from where (a "More" page goes on from the last shown), its caption.</summary>
        private readonly struct AddOnPage
        {
            public AddOnPage(List<ApiMenus.Node> nodes, int start, string title)
            {
                Nodes = nodes;
                Start = start;
                Title = title;
            }

            public List<ApiMenus.Node> Nodes { get; }

            public int Start { get; }

            public string Title { get; }
        }

        /// <summary>The Add-ons pages opened, the one shown last; Back goes to the one before, and from the first to the actions.</summary>
        private readonly List<AddOnPage> addOnPages = new List<AddOnPage>();

        /// <summary>The menus row as it is now: the Add-ons tile after the plugin's own while any outside item is registered.</summary>
        private static QuickAction[] MenuRowNow()
        {
            if (ApiRuntime.Menus.Tree(ApiMenus.WristPanel).Count == 0)
                return MenuRow;
            var row = new QuickAction[MenuRow.Length + 1];
            MenuRow.CopyTo(row, 0);
            row[MenuRow.Length] = AddOnsTile;
            return row;
        }

        /// <summary>Opens the first Add-ons page: every owner's top items, owners in the order of their ids.</summary>
        private void OpenAddOns()
        {
            addOnPages.Clear();
            addOnPages.Add(new AddOnPage(ApiRuntime.Menus.Tree(ApiMenus.WristPanel), 0, "Add-ons"));
            switchTo = Page.AddOns;
        }

        private void AddOnsBack()
        {
            if (addOnPages.Count > 0)
                addOnPages.RemoveAt(addOnPages.Count - 1);
            switchTo = addOnPages.Count > 0 ? Page.AddOns : Page.Actions;
        }

        private string AddOnsTitle => addOnPages.Count > 0 ? addOnPages[addOnPages.Count - 1].Title : "Add-ons";

        /// <summary>The tiles of the Add-ons page shown: Back first, then its items as the plugin's own tiles are, then "More" when they do not fit.</summary>
        private QuickAction[] AddOnTiles()
        {
            var tiles = new List<QuickAction>
            {
                new QuickAction("Back", addOnPages.Count > 1 ? "Back to the page before." : "Back to the quick actions.", GuiTextures.Back,
                    AddOnsBack, whiteIcon: true),
            };
            if (addOnPages.Count == 0)
                return tiles.ToArray();
            AddOnPage shown = addOnPages[addOnPages.Count - 1];
            ApiMenus menus = ApiRuntime.Menus;
            int count = shown.Nodes.Count - shown.Start;
            int before = ApiMenus.ShownBeforeMore(count, AddOnCells);
            for (int i = 0; i < before; i++)
                tiles.Add(AddOnTile(menus, shown.Nodes[shown.Start + i]));
            if (before < count)
            {
                tiles.Add(new QuickAction("More", "More actions from other plugins and mods.", Hud + "plus.png", () =>
                {
                    addOnPages.Add(new AddOnPage(shown.Nodes, shown.Start + before, shown.Title));
                    switchTo = Page.AddOns;
                }, whiteIcon: true));
            }
            return tiles.ToArray();
        }

        /// <summary>
        /// One outside item as a tile: its label and icon, greyed while it cannot be used, its state under its name, the foot naming
        /// its owner as the bindings page does (SetOwnerName). An item runs its owner's code and closes the panel, unless it was
        /// registered with "StaysOpen"; a page opens.
        /// </summary>
        private QuickAction AddOnTile(ApiMenus menus, ApiMenus.Node node)
        {
            string from = "From " + ApiRuntime.Core.NameOf(node.Owner) + ".";
            if (node.Item.IsPage)
            {
                return new QuickAction(node.Item.Label, from, node.Item.Icon, () =>
                {
                    addOnPages.Add(new AddOnPage(node.Children, 0, node.Item.Label));
                    switchTo = Page.AddOns;
                }, stateText: node.Item.State != null ? () => menus.State(node) : (System.Func<string>)null, canUse: () => menus.CanUse(node));
            }
            return new QuickAction(node.Item.Label, from, node.Item.Icon, () => menus.Use(node),
                stateText: node.Item.State != null ? () => menus.State(node) : (System.Func<string>)null, canUse: () => menus.CanUse(node),
                closesPanel: !node.Item.StaysOpen);
        }

        /// <summary>The Add-ons page shown, for the log: its caption and its tiles' names, a page's with "&gt;".</summary>
        private string AddOnsLogLine()
        {
            var text = new StringBuilder("Add-ons page ").Append(AddOnsTitle).Append(" (");
            QuickAction[] tiles = AddOnTiles();
            AddOnPage? shown = addOnPages.Count > 0 ? addOnPages[addOnPages.Count - 1] : (AddOnPage?)null;
            for (int i = 0; i < tiles.Length; i++)
            {
                if (i > 0)
                    text.Append(", ");
                text.Append(tiles[i].Label);
                if (i > 0 && shown.HasValue && shown.Value.Start + i - 1 < shown.Value.Nodes.Count && tiles[i].Label != "More"
                    && shown.Value.Nodes[shown.Value.Start + i - 1].Item.IsPage)
                    text.Append(" >");
            }
            return text.Append(')').ToString();
        }
    }
}
