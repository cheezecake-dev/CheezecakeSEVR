using System;
using System.Collections.Generic;
using VRageMath;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// One tab of the datapad (<see cref="DatapadScreen"/>). A page puts its controls into the area it is given when it is
    /// shown, and is told each frame while it is. A page that is an action rather than something to read (the terminal)
    /// does it in <see cref="Open"/> and is never shown.
    /// </summary>
    internal abstract class DatapadPage
    {
        /// <summary>
        /// The pages, in tab order. Another page is one more entry here: its tab follows from <see cref="Name"/>, and the
        /// drive's "datapad page" finds it by the same name.
        /// </summary>
        internal static readonly List<Func<DatapadPage>> All = new List<Func<DatapadPage>>
        {
            () => new DatapadChatPage(),
            () => new DatapadGpsPage(),
            () => new DatapadShipPage(),
            () => new DatapadTerminalPage(),
        };

        /// <summary>The tab's name.</summary>
        public abstract string Name { get; }

        /// <summary>The tab shows a mark: something new on the page.</summary>
        public virtual bool Marked => false;

        /// <summary>
        /// Its tab was pressed. True: the page did what it is for (opened another screen) and is not shown; false: it is shown.
        /// </summary>
        public virtual bool Open(DatapadScreen screen) => false;

        /// <summary>
        /// Puts the page's controls on <paramref name="screen"/> within <paramref name="area"/> (the screen's normalised
        /// measures, from its middle: x right, y down).
        /// </summary>
        public abstract void Build(DatapadScreen screen, RectangleF area);

        /// <summary>Each frame while shown, before the screen's controls take their input.</summary>
        public virtual void Update(DatapadScreen screen)
        {
        }

        /// <summary>The page is no longer shown (another tab, or the pad was put away).</summary>
        public virtual void Hidden()
        {
        }
    }
}
