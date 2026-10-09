using System;
using System.Collections.Generic;
using Sandbox.Game;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Graphics;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Input;
using VRage.Game;
using VRage.Game.ObjectBuilders;
using VRage.Input;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// The toolbar's slots as a wheel: the game's own radial menu screen (<see cref="MyGuiControlRadialMenuBase"/>, the
    /// screen the system menu and the block palette are made from) filled with the current toolbar instead of menu
    /// actions. It has the game's background, rings, tabs, highlight, tooltips and sounds; what it changes is
    /// <list type="bullet">
    /// <item>The items: a section for each page of the toolbar, nine items for a page's nine slots, each reading the
    /// toolbar's item live (<see cref="SlotWheelItem"/>). The tabs along the top are the pages.</item>
    /// <item>Nine sectors where the base has eight (<see cref="GenerateIcons"/>, <see cref="UpdateHighlight"/>): the base
    /// builds its sectors in its constructor and works out which one a stick points at from how many it built, so
    /// swapping them for nine of 40 degrees makes the stock selection code pick the slots.</item>
    /// <item>The page follows the toolbar (<see cref="Update"/>): <see cref="ToolbarWheel"/> pages the toolbar with the
    /// game's own <c>SwitchToPage</c> and this screen shows whatever page the toolbar is on, so the HUD's toolbar and the
    /// wheel always agree. The carousel's own paging controls are not answered.</item>
    /// <item>It does not hide the HUD (CanHideOthers off), so the toolbar bar stays on screen behind it.</item>
    /// </list>
    /// What it does not do is act: <see cref="ToolbarWheel"/> reads the hands, and activates the slot with
    /// <c>ActivateItemAtSlot</c> and closes the screen with the game's CANCEL. <see cref="SlotWheelItem.Activate"/> does the
    /// same call for a gamepad's confirm, which the base screen turns into an item activation.
    /// </summary>
    internal sealed class SlotWheelScreen : MyGuiControlRadialMenuBase
    {
        private const string SectorTexture = "Textures\\GUI\\Controls\\RadialSectorUnSelected.dds";
        private const string CentreTexture = "Textures\\GUI\\Controls\\RadialCentralCircle.dds";
        private const string CentreSelectedTexture = "Textures\\GUI\\Controls\\RadialCentralCircleSelected.dds";
        private const string HolsterIcon = "Textures\\GUI\\Icons\\HideWeapon.dds";

        /// <summary>
        /// The base's sector is a 45 degree wedge in a 288 pixel square; a 40 degree one is the same wedge squeezed across
        /// by tan(20) / tan(22.5), which keeps its apex on the middle line and its edges where the neighbours' meet.
        /// </summary>
        private static readonly float SectorWidth = (float)(Math.Tan(Math.PI / SlotWheel.Positions) / Math.Tan(Math.PI / 8d));

        /// <summary>A slot with nothing in it: its sector is drawn fainter.</summary>
        private static readonly Vector4 EmptyTint = new Vector4(1f, 1f, 1f, 0.35f);

        private readonly MyToolbar toolbar;

        // Set before the base constructor runs (field initialisers come first), because it asks for the icons.
        private readonly string[][] shownIcons = new string[SlotWheel.Positions][];
        private readonly MyGuiControlLabel[] numbers = new MyGuiControlLabel[SlotWheel.Positions];
        private MyGuiControlImageRotatable highlight;

        public SlotWheelScreen(MyToolbar toolbar)
            : base(BuildData(toolbar), MyControlsSpace.SYSTEM_RADIAL_MENU, null)
        {
            this.toolbar = toolbar;
            CanHideOthers = false;
            HideHolster();
            SwitchSection(toolbar.CurrentPage, forceStop: true);
            VRRadialMenus.Relabel(this, tools: true);
        }

        public override string GetFriendlyName() => "SlotWheel";

        /// <summary>The radial menu's data: one section per page, a <see cref="SlotWheelItem"/> for each of the nine positions.</summary>
        internal static MyRadialMenu BuildData(MyToolbar toolbar)
        {
            var sections = new List<MyRadialMenuSection>();
            for (int page = 0; page < toolbar.PageCount; page++)
            {
                var items = new List<MyRadialMenuItem>(SlotWheel.Positions);
                for (int slot = 0; slot < SlotWheel.Positions; slot++)
                    items.Add(new SlotWheelItem(toolbar, page, slot));
                // A string the language files do not know shows as itself.
                sections.Add(new MyRadialMenuSection(items, MyStringId.GetOrCompute("Page " + (page + 1)))
                {
                    IsEnabledCreative = true,
                    IsEnabledSurvival = true,
                });
            }
            // The base keeps the last section shown under the menu's id, so it needs one of its own.
            return new MyRadialMenu(sections) { Id = new MyDefinitionId(typeof(MyObjectBuilder_RadialMenu), "VRSlotWheel") };
        }

        /// <summary>The unit vector from the middle of the wheel to sector <paramref name="index"/>: sector 0 straight up (GUI y is down), the rest clockwise.</summary>
        internal static Vector2 Direction(int index)
        {
            float a = SlotWheel.AngleOf(index) - (float)Math.PI / 2f;
            return new Vector2((float)Math.Cos(a), (float)Math.Sin(a));
        }

        /// <summary>The centre of sector <paramref name="index"/> from the middle of the wheel, GUI units (as the base places its eight).</summary>
        internal static Vector2 SectorCentre(int index) => Direction(index) * 144f / MyGuiConstants.GUI_OPTIMAL_SIZE;

        /// <summary>Where the icon of sector <paramref name="index"/> sits (the base's own rule, for nine).</summary>
        internal static Vector2 IconCentre(int index) => new Vector2(1f, 1.3333334f) * Direction(index) * RADIUS;

        /// <summary>
        /// Called by the base constructor right after it has built its eight sectors: those go and nine take their place,
        /// then the icons and the slot numbers, in the order the base draws things (sectors, icons, highlight).
        /// </summary>
        protected override void GenerateIcons(int maxSize)
        {
            foreach (MyGuiControlImageRotatable eight in m_buttons)
                Controls.Remove(eight);
            m_buttons.Clear();

            Vector2 size = new Vector2(288f * SectorWidth, 288f) / MyGuiConstants.GUI_OPTIMAL_SIZE;
            for (int i = 0; i < SlotWheel.Positions; i++)
            {
                var sector = new MyGuiControlImageRotatable();
                sector.SetTexture(SectorTexture);
                sector.Rotation = SlotWheel.AngleOf(i);
                sector.Size = size;
                sector.Position = SectorCentre(i);
                m_buttons.Add(sector);
                AddControl(sector);
            }
            for (int i = 0; i < SlotWheel.Positions; i++)
            {
                var icon = new MyGuiControlImage();
                icon.Size = new Vector2(65f) / MyGuiConstants.GUI_OPTIMAL_SIZE;
                icon.Position = IconCentre(i);
                m_icons.Add(icon);
                AddControl(icon);
            }
            for (int i = 0; i < SlotWheel.Positions; i++)
            {
                numbers[i] = new MyGuiControlLabel(IconCentre(i) * 1.38f, null, (i + 1).ToString(), new Vector4(1f, 1f, 1f, 0.75f), 0.7f, "Blue",
                    MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER);
                AddControl(numbers[i]);
            }
            highlight = new MyGuiControlImageRotatable { Size = size, Visible = false };
            highlight.SetTexture("Textures\\GUI\\Controls\\RadialSectorSelected.dds");
            AddControl(highlight);
        }

        /// <summary>The centre of the base's wheel has the put-the-weapon-away icon (the centred stick); here it only closes the wheel.</summary>
        private void HideHolster()
        {
            foreach (MyGuiControlBase control in Controls)
            {
                if (control is MyGuiControlImage image && image.Textures != null && image.Textures.Length > 0 && image.Textures[0].Texture == HolsterIcon)
                    image.Visible = false;
            }
        }

        protected override void UpdateHighlight(int oldIndex, int newIndex)
        {
            if (oldIndex == -1)
                m_cancelButton.SetTexture(CentreTexture);
            if (newIndex == -1)
            {
                m_cancelButton.SetTexture(CentreSelectedTexture);
                if (highlight != null)
                    highlight.Visible = false;
                return;
            }
            if (highlight == null)
                return;
            highlight.Rotation = SlotWheel.AngleOf(newIndex);
            highlight.Position = SectorCentre(newIndex);
            highlight.Visible = true;
        }

        protected override void SetIconTextures(MyRadialMenuSection selectedSection) => Refresh(selectedSection);

        protected override void UpdateIcon() => Refresh(m_data.CurrentSections[m_currentSection]);

        /// <summary>Each position shows its slot as the toolbar draws it: the item's icon layers, dimmed when it cannot be used, and the number.</summary>
        private void Refresh(MyRadialMenuSection section)
        {
            for (int i = 0; i < SlotWheel.Positions && i < section.Items.Count; i++)
            {
                var item = (SlotWheelItem)section.Items[i];
                MyToolbarItem filled = item.ToolbarItem;
                MyGuiControlImage icon = m_icons[i];
                icon.Visible = filled != null;
                if (filled != null)
                {
                    string[] layers = filled.Icons;
                    if (!ReferenceEquals(layers, shownIcons[i]) && layers != null && layers.Length > 0)
                    {
                        shownIcons[i] = layers;
                        icon.SetTextures(layers);
                    }
                    icon.ColorMask = item.Enabled() ? Color.White : Color.Gray;
                }
                else
                    shownIcons[i] = null;
                m_buttons[i].Visible = true;
                m_buttons[i].ColorMask = filled != null ? Vector4.One : EmptyTint;
                if (numbers[i] != null)
                    numbers[i].Visible = item.Exists;
            }
        }

        public override bool Update(bool hasFocus)
        {
            // The toolbar's page is the wheel's page: show whichever the toolbar is on, and keep the icons live.
            if (toolbar != null)
            {
                if (toolbar.CurrentPage != m_currentSection)
                    SwitchSection(toolbar.CurrentPage, forceStop: true);
                else
                    UpdateIcon();
            }
            return base.Update(hasFocus);
        }

        /// <summary>The chosen slot's name, its number and whether it can be used, outside the ring on the side it is on.</summary>
        protected override void UpdateTooltip()
        {
            int slot = m_selectedButton;
            if (toolbar == null || slot < 0 || slot >= SlotWheel.Positions || m_currentSection < 0 || m_currentSection >= m_data.CurrentSections.Count)
            {
                m_tooltipName.Visible = m_tooltipState.Visible = m_tooltipShortcut.Visible = false;
                return;
            }
            var item = (SlotWheelItem)m_data.CurrentSections[m_currentSection].Items[slot];
            MyToolbarItem filled = item.ToolbarItem;
            string name = filled == null ? "Empty" : filled.DisplayName.Length > 0 ? filled.DisplayName.ToString() : "Item";
            m_tooltipName.Text = name;
            m_tooltipState.Text = "Slot " + (slot + 1) + (filled != null && !item.Enabled() ? " (unavailable)" : string.Empty);
            m_tooltipState.ColorMask = filled != null && !item.Enabled() ? Color.Red : Color.White;
            m_tooltipName.RecalculateSize();
            m_tooltipState.RecalculateSize();

            // Which side of the wheel the slot is on decides which way the text grows away from the ring.
            Vector2 at = m_icons[slot].Position * 1.92f;
            int side = Math.Abs(at.X) < 0.05f ? 0 : Math.Sign(at.X);
            int level = Math.Abs(at.Y) < 0.05f ? 0 : Math.Sign(at.Y);
            var align = (MyGuiDrawAlignEnum)(3 * (1 - side) + (1 - level));
            const float Line = 0.025f;
            float nameY = level < 0 ? -Line : level == 0 ? -Line / 2f : 0f;
            float stateY = level < 0 ? 0f : level == 0 ? Line / 2f : Line;
            m_tooltipName.Position = at + new Vector2(0f, nameY);
            m_tooltipState.Position = at + new Vector2(0f, stateY);
            m_tooltipName.OriginAlign = align;
            m_tooltipState.OriginAlign = align;
            m_tooltipName.Visible = true;
            m_tooltipState.Visible = true;
            m_tooltipShortcut.Visible = false;
        }
    }

    /// <summary>
    /// One position of the wheel: slot <see cref="Slot"/> of page <see cref="Page"/> of a toolbar, reading the toolbar's
    /// item whenever it is asked, so the wheel follows the toolbar being edited, used and updated.
    /// </summary>
    internal sealed class SlotWheelItem : MyRadialMenuItem
    {
        public SlotWheelItem(MyToolbar toolbar, int page, int slot)
        {
            Toolbar = toolbar;
            Page = page;
            Slot = slot;
            CloseMenu = true;
        }

        public MyToolbar Toolbar { get; }

        public int Page { get; }

        public int Slot { get; }

        /// <summary>The toolbar has a slot here (a toolbar can have fewer than the wheel's nine).</summary>
        public bool Exists => Slot < Toolbar.SlotCount;

        private int Index => Page * Toolbar.SlotCount + Slot;

        /// <summary>The item in the slot, or null when it is empty or the slot is not there.</summary>
        public MyToolbarItem ToolbarItem => Exists ? Toolbar.GetItemAtIndex(Index) : null;

        public override bool Enabled() => ToolbarItem != null && Toolbar.IsEnabled(Index);

        public override string GetIcon()
        {
            string[] layers = ToolbarItem?.Icons;
            return layers != null && layers.Length > 0 ? layers[0] : string.Empty;
        }

        /// <summary>A gamepad's confirm on the wheel (the hands do the same in <see cref="ToolbarWheel"/>): the slot's page, then the slot.</summary>
        public override void Activate(params object[] parameters)
        {
            if (!Exists || !Toolbar.CanPlayerActivateItems)
                return;
            Toolbar.SwitchToPage(Page);
            Toolbar.ActivateItemAtSlot(Slot);
        }
    }
}
