// ControlsLayout: how many rows the wrist panel's Controls page gives each hand, how tall, and when it says "more" (no game types; tests/ApiTests runs it).
using System;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// The rule behind the Controls page (<see cref="QuickActionsScreen"/>) once other plugins' and mods' actions are listed
    /// after the plugin's own (SevrApi, <see cref="Input.ControlGuide.Outside"/>): every row is one height, the same on both
    /// hands, and it shrinks evenly to fit everything, down to a floor that is still readable
    /// (<see cref="FloorRow"/>). When even the floor does not fit, a hand's last row says how many more there are and that they
    /// are on the bindings page; the plugin's own rows are never cut, and no row is cut part way. With no outside actions the
    /// rows are the full height they always were.
    /// </summary>
    internal static class ControlsLayout
    {
        /// <summary>A hand's heading ("Left hand  -  Quest 3") takes one row, as a line does.</summary>
        public const int HeadingRows = 1;

        /// <summary>What <see cref="Fit"/> decided: one row height for the page, and for each hand how many outside rows show and how many are left for the bindings page.</summary>
        internal readonly struct Plan
        {
            public Plan(float rowHeight, float textRatio, int[] shown, int[] hidden)
            {
                RowHeight = rowHeight;
                TextRatio = textRatio;
                Shown = shown;
                Hidden = hidden;
            }

            /// <summary>The height of a row, in the GUI's measures.</summary>
            public float RowHeight { get; }

            /// <summary>The row's height over the full one: the text of every row is drawn at this share of its usual size (1: as it always was).</summary>
            public float TextRatio { get; }

            /// <summary>Per hand (left, right): the outside rows shown.</summary>
            public int[] Shown { get; }

            /// <summary>Per hand: the outside rows not shown, which the hand's "more" row names; 0 when none (no "more" row).</summary>
            public int[] Hidden { get; }

            /// <summary>The rows are shorter than the full height, so their text is drawn smaller; false is the page as it was.</summary>
            public bool Shrunk => TextRatio < 1f;

            /// <summary>Rows a hand uses: its heading, its own lines, the outside rows shown, and the "more" row when it has one.</summary>
            public int RowsOf(int hand, int own) => HeadingRows + own + Shown[hand] + (Hidden[hand] > 0 ? 1 : 0);
        }

        /// <summary>
        /// The plan for a page <paramref name="available"/> tall, with rows <paramref name="fullRow"/> tall when there is room and never
        /// less than <paramref name="floorRow"/> (clamped to the full row): per hand, the plugin's own lines (<paramref name="own"/>)
        /// and the outside ones (<paramref name="outside"/>). Rows shrink evenly while all fit at or above the floor; past that the
        /// rows stay at the floor (as many as there are, of that page height) and each hand that does not fit shows as many outside
        /// rows as leave room for its "more" row. The plugin's own rows and a "more" row always get their room: if the floor leaves
        /// less than that, the rows go below the floor for them (they are the plugin's own text, as large as the player has had it
        /// on a page that fits).
        /// </summary>
        public static Plan Fit(float available, float fullRow, float floorRow, int[] own, int[] outside)
        {
            if (own == null || outside == null || own.Length != 2 || outside.Length != 2)
                throw new ArgumentException("one count for each hand");
            if (!(fullRow > 0f) || float.IsInfinity(fullRow))
                throw new ArgumentOutOfRangeException(nameof(fullRow));
            if (!(available > 0f))
                available = 0f;
            floorRow = float.IsNaN(floorRow) || floorRow <= 0f ? fullRow : Math.Min(floorRow, fullRow);
            int[] none = new int[2];

            if (outside[0] <= 0 && outside[1] <= 0)
                return new Plan(fullRow, 1f, none, none);

            int need = 0, ownRows = 0;
            for (int h = 0; h < 2; h++)
            {
                need = Math.Max(need, HeadingRows + Math.Max(0, own[h]) + Math.Max(0, outside[h]));
                ownRows = Math.Max(ownRows, HeadingRows + Math.Max(0, own[h]));
            }
            var all = new[] { Math.Max(0, outside[0]), Math.Max(0, outside[1]) };

            if (need * fullRow <= available)
                return new Plan(fullRow, 1f, all, none);

            float fit = available / need;
            if (fit >= floorRow)
                return new Plan(fit, fit / fullRow, all, none);

            // Past the floor: the rows the floor gives, at least the plugin's own and a "more" row.
            int capacity = Math.Max((int)Math.Floor(available / floorRow), ownRows + 1);
            float row = Math.Min(fullRow, available / capacity);
            var shown = new int[2];
            var hidden = new int[2];
            for (int h = 0; h < 2; h++)
            {
                int total = HeadingRows + Math.Max(0, own[h]) + all[h];
                if (total <= capacity)
                {
                    shown[h] = all[h];
                    continue;
                }
                shown[h] = Math.Max(0, capacity - HeadingRows - Math.Max(0, own[h]) - 1);
                hidden[h] = all[h] - shown[h];
            }
            return new Plan(row, row / fullRow, shown, hidden);
        }

        /// <summary>
        /// The least a row can be, in the GUI's measures, for its text to be as large to the eye as the plugin's smallest text in the
        /// world: <paramref name="minTextRadians"/> is the angle the text's line (its box, <paramref name="textShare"/> of a row) has to
        /// subtend, <paramref name="readingMetres"/> the farthest the page is read from, <paramref name="panelWidthMetres"/> how wide
        /// the page is in the world for <paramref name="guiWidth"/> of the GUI's width. 0 when any input is not a usable size (then
        /// <see cref="Fit"/> keeps the full row: nothing shrinks).
        /// </summary>
        public static float FloorRow(float minTextRadians, float readingMetres, float panelWidthMetres, float guiWidth, float textShare)
        {
            if (!(minTextRadians > 0f) || !(readingMetres > 0f) || !(panelWidthMetres > 0f) || !(guiWidth > 0f) || !(textShare > 0f)
                || float.IsInfinity(minTextRadians) || float.IsInfinity(readingMetres) || float.IsInfinity(panelWidthMetres)
                || float.IsInfinity(guiWidth) || float.IsInfinity(textShare))
                return 0f;
            float metresPerUnit = panelWidthMetres / guiWidth;
            return minTextRadians * readingMetres / metresPerUnit / textShare;
        }

        /// <summary>The "more" row's words: how many outside actions the page leaves out and where they are.</summary>
        public static string MoreText(int hidden) => hidden + " more on the bindings page (Options > VR > Controller)";
    }
}
