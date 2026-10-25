using System;
using System.Diagnostics;
using HarmonyLib;
using Sandbox;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Gui;
using Sandbox.Game.GUI;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using Sandbox.Graphics;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Rendering;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Input;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// The toolbar on the inventory screen in VR: the game's own toolbar control (the one the G menu shows, in the HUD's
    /// toolbar style) along the bottom of the inventory page, below the two inventories, which are made that much shorter.
    /// A hand tool or a gun dragged from the inventory onto one of its nine slots is put there as the G menu puts it, and a
    /// tool or gun dropped on the selected slot (the one in the hand) comes into the hand.
    /// </summary>
    /// <remarks>
    /// <para>What the game does, not a copy of it. The control is the G menu's (MyPerGameSettings.GUI.ToolbarControl, made
    /// as MyGuiScreenToolbarConfigBase makes it, with MyHud.HudDefinition.Toolbar), so its slots, icons, page numbers and its
    /// own clicks are the game's: a click on a slot uses it, a right click (the grip) takes it off the toolbar. The drop is
    /// the inventory's own drag (MyTerminalInventoryController: the laser's trigger held on an item, then moved), whose end,
    /// dragDrop_OnItemDropped, finds the grid under the cursor (MyGuiControlGridDragAndDrop.Drop) and does nothing when that
    /// is not an inventory; here, when it is this toolbar's grid, the item goes through the G menu's own drop
    /// (MyGuiScreenToolbarConfigBase.DropGridItemToToolbar) as the toolbar item the G menu builds for that weapon or tool
    /// (a MyObjectBuilder_ToolbarItemWeapon with the item's definition, AddSimpleItemDefinition). Only what the G menu lists
    /// as tools and weapons (MyDefinitionManager.GetWeaponDefinitions), and only what it would let you pick: in survival, one
    /// you carry (inventory.ContainItems). Anything else dropped there does nothing. A double click keeps its meaning (the
    /// item to the other inventory).</para>
    /// <para>While the picture is VR's (<see cref="StereoPatches.VRPicture"/>), and without a headset too unless that is off
    /// (<see cref="VRSettings.InventoryToolbarFlat"/>): the control and the drop are the game's, so the mouse works them.
    /// The control and its hooks go when the inventory page closes (MyTerminalInventoryController.Close).</para>
    /// </remarks>
    internal static class InventoryToolbar
    {
        /// <summary>Debug: no toolbar on the inventory screen (<see cref="RenderDebug"/> InventoryToolbar=0).</summary>
        public static bool Off { get; set; }

        /// <summary>Between the inventories and the toolbar, in the game's screen units.</summary>
        private const float Gap = 0.01f;

        /// <summary>Between the slots and the row of page numbers above them, in the game's screen units.</summary>
        private const float PageGap = 0.004f;

        private static readonly FeatureGuard guard = new FeatureGuard(nameof(InventoryToolbar));
        private static Harmony pending;
        private static MyGuiControlToolbar bar;
        private static IMyGuiControlsParent barParent;
        /// <summary>When the page just shown starts to say where its slots are (<see cref="LogPlaces"/>); 0: said, or nothing to say.</summary>
        private static long placesAt;

        /// <summary>MyTerminalInventoryController is internal to the game: its clean-up and its drag's field, reached by name.</summary>
        private static System.Reflection.MethodInfo clearDisabled;
        private static System.Reflection.FieldInfo dragInfo;

        /// <summary>
        /// Hooking MyTerminalInventoryController before the game has made its GUI could run class constructors that need the
        /// renderer, as the double-click tolerance found for the controls; so it waits for the game's first update.
        /// </summary>
        public static void Patch(Harmony harmony)
        {
            if (MySandboxGame.Static?.IsFirstUpdateDone != true)
            {
                pending = harmony;
                Log.Info("Inventory toolbar: waits for the game's first update to hook the inventory");
                return;
            }
            Hook(harmony);
        }

        /// <summary>Game thread, once a frame: hooks once the game has run its first update; the trace.</summary>
        public static void Update()
        {
            if (pending != null && MySandboxGame.Static?.IsFirstUpdateDone == true)
            {
                Harmony harmony = pending;
                pending = null;
                PatchRegistry.Apply(nameof(InventoryToolbar), () => Hook(harmony),
                    "The toolbar on the inventory screen could not be hooked; tools and guns go on the toolbar from the G menu");
            }
            if (placesAt != 0 && bar != null && guard.On && Stopwatch.GetTimestamp() >= placesAt)
            {
                try
                {
                    // tried until the panel is placed and the inventory listed, for five seconds at most
                    bool last = Stopwatch.GetTimestamp() - placesAt > 5 * Stopwatch.Frequency;
                    if (LogPlaces(last) || last)
                        placesAt = 0;
                }
                catch (Exception e)
                {
                    placesAt = 0;
                    guard.Failed(e, "Inventory toolbar: saying where its slots are failed");
                }
            }
        }

        private static void Hook(Harmony harmony)
        {
            Type controller = AccessTools.TypeByName("Sandbox.Game.Gui.MyTerminalInventoryController")
                              ?? throw new TypeLoadException("Sandbox.Game.Gui.MyTerminalInventoryController");
            clearDisabled = AccessTools.Method(controller, "ClearDisabledControls") ?? throw new MissingMethodException(controller.Name, "ClearDisabledControls");
            dragInfo = AccessTools.Field(controller, "m_dragAndDropInfo") ?? throw new MissingFieldException(controller.Name, "m_dragAndDropInfo");
            harmony.Patch(AccessTools.Method(controller, "Init"),
                postfix: new HarmonyMethod(typeof(InventoryToolbar), nameof(AfterInit)));
            harmony.Patch(AccessTools.Method(controller, "Close"),
                prefix: new HarmonyMethod(typeof(InventoryToolbar), nameof(BeforeClose)));
            harmony.Patch(AccessTools.Method(typeof(MyGuiControlToolbar), "RefreshSelectedItem"),
                postfix: new HarmonyMethod(typeof(InventoryToolbar), nameof(AfterRefreshSelected)));
            harmony.Patch(AccessTools.Method(typeof(MyGuiControlToolbar), "RepositionControls")
                          ?? throw new MissingMethodException(nameof(MyGuiControlToolbar), "RepositionControls"),
                postfix: new HarmonyMethod(typeof(InventoryToolbar), nameof(AfterReposition)));
            harmony.Patch(AccessTools.Method(typeof(MyGuiControlToolbar), nameof(MyGuiControlToolbar.HandleInput)),
                postfix: new HarmonyMethod(typeof(InventoryToolbar), nameof(AfterHandleInput)));
            harmony.Patch(AccessTools.Method(controller, "dragDrop_OnItemDropped"),
                prefix: new HarmonyMethod(typeof(InventoryToolbar), nameof(BeforeDropped)));
            Log.Info("Inventory toolbar: hooked (the inventory page shows the toolbar; a tool or gun dropped on a slot goes there)");
        }

        // ---- The control ----

        /// <summary>MyTerminalInventoryController.Init: the page is built; the toolbar goes along its bottom.</summary>
        private static void AfterInit(IMyGuiControlsParent controlsParent)
        {
            if (Off || !guard.On || !(StereoPatches.VRPicture || VRSettings.InventoryToolbarFlat) || controlsParent == null)
                return;
            try
            {
                Remove();
                MyGuiControlBase left = controlsParent.Controls.GetControlByName("LeftInventory");
                MyGuiControlBase right = controlsParent.Controls.GetControlByName("RightInventory");
                if (left == null || right == null)
                {
                    Log.Info("Inventory toolbar: the inventory page has no LeftInventory or RightInventory list; no toolbar on it");
                    return;
                }
                var made = (MyGuiControlToolbar)Activator.CreateInstance(MyPerGameSettings.GUI.ToolbarControl, MyHud.HudDefinition.Toolbar, false);
                // The colour and skin swatches and the selected item's name are the HUD's, placed for the HUD; here the slots and the page numbers.
                HideExtras(made);
                float pagesHigh = PlacePages(made);
                float height = Math.Max(made.Size.Y > 0f ? made.Size.Y : made.ToolbarGrid.Size.Y,
                                        pagesHigh > 0f ? made.ToolbarGrid.Size.Y + PageGap + pagesHigh : 0f);
                float bottom = Math.Max(Bottom(left), Bottom(right));
                float shorter = height + Gap;
                left.Size = new Vector2(left.Size.X, left.Size.Y - shorter);
                right.Size = new Vector2(right.Size.X, right.Size.Y - shorter);
                made.OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_BOTTOM;
                made.Position = new Vector2(0f, bottom);
                made.Name = "VRInventoryToolbar";
                controlsParent.Controls.Add(made);
                bar = made;
                barParent = controlsParent;
                // where its slots are in the headset's space; nowhere without one
                placesAt = StereoPatches.VRPicture ? Stopwatch.GetTimestamp() + Stopwatch.Frequency : 0;
                Log.Info($"Inventory toolbar: shown on the inventory page, {made.Size.X:0.000} x {height:0.000} at the bottom ({bottom:0.000}), " +
                         $"slots {made.ToolbarGrid.Size.Y:0.000} high" + (pagesHigh > 0f ? $" with the page numbers {pagesHigh:0.000} high above them" : ", no page numbers") +
                         $"; the inventories {shorter:0.000} shorter, now {left.Size.Y:0.000} high");
            }
            catch (Exception e)
            {
                Remove();
                guard.Failed(e, "Inventory toolbar: putting the toolbar on the inventory page failed");
            }
        }

        /// <summary>The lowest edge of a control in its parent's units, whatever its alignment.</summary>
        private static float Bottom(MyGuiControlBase control) =>
            MyUtils.GetCoordTopLeftFromAligned(control.Position, control.Size, control.OriginAlign).Y + control.Size.Y;

        /// <summary>The colour and skin swatches and the selected item's name: the HUD's, placed for the HUD above the slots.</summary>
        private static void HideExtras(MyGuiControlToolbar made)
        {
            foreach (string field in new[] { "m_colorVariantPanel", "m_skinVariantPanel", "m_selectedItemLabel" })
            {
                if (AccessTools.Field(typeof(MyGuiControlToolbar), field)?.GetValue(made) is MyGuiControlBase part)
                    part.Visible = false;
            }
        }

        /// <summary>
        /// The game's row of page numbers (m_pageLabelList, the page in use lit, made again each time the control shows a toolbar),
        /// which the game puts under the slots (RepositionControls), where it hangs below the inventories and reads as a second
        /// toolbar: moved above the slots, into the room the hidden name and swatches leave, along the slots' left edge as the
        /// game has it. Returns the row's height, 0 with no page numbers (a toolbar of one page).
        /// </summary>
        private static float PlacePages(MyGuiControlToolbar made)
        {
            if (!(PageLabels?.GetValue(made) is System.Collections.Generic.List<MyGuiControlLabel> pages) || pages.Count == 0)
                return 0f;
            // Positions are from the control's middle; the slots' bottom is the control's.
            float slotsTop = made.Size.Y / 2f - made.ToolbarGrid.Size.Y;
            float high = 0f;
            foreach (MyGuiControlLabel page in pages)
            {
                page.Position = new Vector2(page.Position.X, slotsTop - PageGap - page.Size.Y / 2f);
                high = Math.Max(high, page.Size.Y);
            }
            return high;
        }

        private static readonly System.Reflection.FieldInfo PageLabels = AccessTools.Field(typeof(MyGuiControlToolbar), "m_pageLabelList");

        /// <summary>MyGuiControlToolbar.RepositionControls (a toolbar shown, a size changed) puts the page numbers back under the slots; on this toolbar they go above.</summary>
        private static void AfterReposition(MyGuiControlToolbar __instance)
        {
            if (ReferenceEquals(__instance, bar))
                PlacePages(__instance);
        }

        /// <summary>MyGuiControlToolbar.RefreshSelectedItem shows the swatches again for a block; on this toolbar they stay hidden.</summary>
        private static void AfterRefreshSelected(MyGuiControlToolbar __instance)
        {
            if (ReferenceEquals(__instance, bar))
                HideExtras(__instance);
        }

        /// <summary>
        /// MyGuiControlToolbar.HandleInput: a click on one of this toolbar's page numbers turns to that page
        /// (MyToolbar.SwitchToPage, as Ctrl and the number do). The game's own labels only show the page; this one is clickable.
        /// </summary>
        private static void AfterHandleInput(MyGuiControlToolbar __instance, ref MyGuiControlBase __result)
        {
            if (!ReferenceEquals(__instance, bar) || !guard.On || !MyInput.Static.IsNewLeftMousePressed()
                || !(PageLabels?.GetValue(__instance) is System.Collections.Generic.List<MyGuiControlLabel> pages))
                return;
            try
            {
                Vector2 cursor = MyGuiManager.MouseCursorPosition;
                for (int i = 0; i < pages.Count; i++)
                {
                    if (!pages[i].Visible || !pages[i].Rectangle.Contains(cursor))
                        continue;
                    MyToolbar toolbar = __instance.ShownToolbar ?? MyToolbarComponent.CurrentToolbar;
                    if (toolbar == null)
                        return;
                    toolbar.SwitchToPage(i);
                    __result = __instance;
                    Log.Info($"Inventory toolbar: page {i + 1} clicked; the toolbar is on page {toolbar.CurrentPage + 1}");
                    return;
                }
            }
            catch (Exception e)
            {
                guard.Failed(e, "Inventory toolbar: turning the page on a click failed");
            }
        }

        /// <summary>MyTerminalInventoryController.Close: the page goes; the toolbar control with it (its OnRemoving lets go of the game's toolbar).</summary>
        private static void BeforeClose() => Remove();

        private static void Remove()
        {
            MyGuiControlToolbar old = bar;
            IMyGuiControlsParent parent = barParent;
            bar = null;
            barParent = null;
            if (old == null)
                return;
            try
            {
                parent?.Controls.Remove(old);
            }
            catch (Exception e)
            {
                Log.Error(e, "Inventory toolbar: taking the toolbar off the inventory page failed");
            }
        }

        // ---- The drop ----

        /// <summary>
        /// MyTerminalInventoryController.dragDrop_OnItemDropped: a drop on this toolbar is a tool or gun going on the toolbar.
        /// The game's own handler would return early there and leave the inventories greyed out, so its clean-up is done here.
        /// </summary>
        private static bool BeforeDropped(object __instance, MyDragAndDropEventArgs eventArgs)
        {
            MyGuiControlToolbar shown = bar;
            if (shown == null || eventArgs?.DropTo?.Grid == null || !shown.IsToolbarGrid(eventArgs.DropTo.Grid) || !guard.On)
                return true;
            try
            {
                Drop(eventArgs);
            }
            catch (Exception e)
            {
                guard.Failed(e, "Inventory toolbar: the drop on the toolbar failed");
            }
            try
            {
                clearDisabled?.Invoke(__instance, null);
                dragInfo?.SetValue(__instance, null);
            }
            catch (Exception e)
            {
                Log.Error(e, "Inventory toolbar: the inventory's drag could not be ended");
            }
            return false;
        }

        private static void Drop(MyDragAndDropEventArgs eventArgs)
        {
            int slot = eventArgs.DropTo.ItemIndex;
            MyToolbar toolbar = MyToolbarComponent.CurrentToolbar;
            MyCharacter character = MySession.Static?.LocalCharacter;
            if (toolbar == null || character == null || !(eventArgs.Item?.UserData is MyPhysicalInventoryItem item) || item.Content == null)
                return;
            MyDefinitionId id = item.Content.GetId();
            // As the G menu's drop: one of the nine slots, not the empty hand's after them.
            if (slot < 0 || slot >= 9 || slot >= toolbar.SlotCount)
            {
                Log.Info($"Inventory toolbar: {id.SubtypeName} dropped on slot {slot + 1}, which takes nothing");
                return;
            }
            if (!TryHandItem(character, id, out MyPhysicalItemDefinition definition, out string why))
            {
                Log.Info($"Inventory toolbar: {id.SubtypeName} dropped on slot {slot + 1} stays off the toolbar: {why}");
                return;
            }
            var data = MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_ToolbarItemWeapon>();
            data.DefinitionId = definition.Id;
            MyToolbarItem toolbarItem = MyToolbarItemFactory.CreateToolbarItem(data);
            if (toolbarItem == null)
            {
                Log.Info($"Inventory toolbar: the game made no toolbar item for {definition.Id}");
                return;
            }
            int? selected = toolbar.SelectedSlot;
            MyGuiScreenToolbarConfigBase.DropGridItemToToolbar(toolbarItem, slot);
            bool placed = ReferenceEquals(toolbar.GetSlotItem(slot), toolbarItem);
            Log.Info($"Inventory toolbar: {definition.DisplayNameText} ({definition.Id.SubtypeName}) {(placed ? "put" : "not put")} in slot {slot + 1}");
            if (placed && selected == slot)
            {
                // The slot in the hand: what is now in it comes into the hand, as the toolbar's own use of the slot.
                toolbar.ActivateItemAtSlot(slot, checkIfWantsToBeActivated: false, playActivationSound: false, userActivated: false);
                Log.Info($"Inventory toolbar: slot {slot + 1} is the one in the hand, so the {definition.DisplayNameText} comes into the hand");
            }
        }

        // ---- Shared with the grip pull ----

        /// <summary>
        /// The item is a hand tool or a gun the G menu would let you pick: one of its tools and weapons
        /// (MyDefinitionManager.GetWeaponDefinitions), public (the G menu also shows non-public ones in a debug build) and, in survival, available there, and carried (or creative).
        /// </summary>
        internal static bool TryHandItem(MyCharacter character, MyDefinitionId id, out MyPhysicalItemDefinition definition, out string why)
        {
            definition = null;
            foreach (MyPhysicalItemDefinition weapon in MyDefinitionManager.Static.GetWeaponDefinitions())
            {
                if (weapon.Id == id)
                {
                    definition = weapon;
                    break;
                }
            }
            if (definition == null)
            {
                why = "not a hand tool or a gun";
                return false;
            }
            if (!definition.Public || !(definition.AvailableInSurvival || !MySession.Static.SurvivalMode))
            {
                why = "the game does not offer it here";
                return false;
            }
            if (!MySession.Static.CreativeMode && !(character.GetInventory()?.ContainItems(1, id) ?? false))
            {
                why = "not in your inventory";
                return false;
            }
            why = null;
            return true;
        }

        // ---- Where things are on the panel ----

        /// <summary>
        /// Once a showing, a second after it: where the toolbar's slots and the first tool or gun you carry are, in the STAGE
        /// space (the floor's), on the panel the page is drawn on. False, saying nothing, until the panel is placed and (unless
        /// this is the <paramref name="last"/> try) your own inventory's list shows a tool or gun.
        /// </summary>
        private static bool LogPlaces(bool last)
        {
            MyGuiControlGrid slots = bar?.ToolbarGrid;
            if (slots == null || !HudPanel.TryGetPlacement(out Matrix pose, out float halfWidth, out float halfHeight)
                || !(OpenXR.XrSession.LocalInStage is Matrix stage))
                return false;
            var text = new System.Text.StringBuilder("Inventory toolbar: on the panel, its slots at stage");
            for (int i = 0; i < slots.GetItemsCount(); i++)
                text.Append(i == 0 ? " " : ", ").Append(i + 1).Append(' ').Append(Format(StageOf(slots, i, pose, halfWidth, halfHeight, stage)));
            text.Append(" m; ");
            MyCharacter character = MySession.Static?.LocalCharacter;
            MyInventory own = character?.GetInventory();
            string tool = null;
            if (own != null && MyScreenManager.GetScreenWithFocus() is MyGuiScreenBase screen)
            {
                var grids = new System.Collections.Generic.List<MyGuiControlBase>();
                Collect(screen.Controls, grids);
                foreach (MyGuiControlBase control in grids)
                {
                    if (!(control is MyGuiControlGrid grid) || !ReferenceEquals(grid.UserData, own))
                        continue;
                    for (int i = 0; i < grid.GetItemsCount() && tool == null; i++)
                    {
                        if (grid.TryGetItemAt(i)?.UserData is MyPhysicalInventoryItem stack && stack.Content != null
                            && TryHandItem(character, stack.Content.GetId(), out _, out _))
                            tool = $"the first tool or gun you carry, {stack.Content.SubtypeName}, at stage {Format(StageOf(grid, i, pose, halfWidth, halfHeight, stage))} m";
                    }
                }
            }
            if (tool == null && !last)
                return false;
            Log.Info(text.Append(tool ?? "you carry no tool or gun").ToString());
            return true;
        }

        private static void Collect(MyGuiControls controls, System.Collections.Generic.List<MyGuiControlBase> into)
        {
            foreach (MyGuiControlBase control in controls)
            {
                into.Add(control);
                if (control is MyGuiControlInventoryOwner owner)
                    into.AddRange(owner.ContentGrids);
                else if (control is IMyGuiControlsParent parent)
                    Collect(parent.Controls, into);
            }
        }

        /// <summary>The middle of a grid's cell on the panel, in the STAGE space.</summary>
        private static Vector3 StageOf(MyGuiControlGrid grid, int index, in Matrix pose, float halfWidth, float halfHeight, in Matrix stage)
        {
            int columns = Math.Max(1, grid.ColumnsCount);
            Vector2 middle = grid.GetDrawPositionTopLeft(index % columns, index / columns) + grid.ItemSize / 2f;
            Vector2 screenAt = MyGuiManager.GetScreenCoordinateFromNormalizedCoordinate(middle);
            Rectangle full = MyGuiManager.GetFullscreenRectangle();
            float u = screenAt.X / full.Width * GuiLayout.Size.X / (GuiLayout.Size.X - 1f);
            float v = screenAt.Y / full.Height * GuiLayout.Size.Y / (GuiLayout.Size.Y - 1f);
            Vector3 local = new Vector3(u * 2f * halfWidth - halfWidth, halfHeight - v * 2f * halfHeight, 0f);
            return Vector3.Transform(Vector3.Transform(local, pose), stage);
        }

        private static string Format(Vector3 v) => $"({v.X:F3}, {v.Y:F3}, {v.Z:F3})";
    }
}
