// GripTable: who holds each hand's grip, from the presses alone (no game types; the tests use it). GripClaim is the game's one.
using System;
using System.Collections.Generic;
using SpaceEngineersVR.Input;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// The order a grip that closes in the world is offered in, lowest first: the one place each feature's turn is set. Equal
    /// places are asked in the order they registered.
    /// </summary>
    internal enum GripPriority
    {
        /// <summary>The off hand on the held tool's own second grip (TwoHand): it reaches for the tool, not the helmet or holster behind it.</summary>
        TwoHand = 5,

        /// <summary>The off hand at the gun's magazine (Reload), just after the two-handed hold: the zones tell the front grip and the magazine apart.</summary>
        Reload = 6,

        /// <summary>The off hand at the magazine pouch on the hip, and while it holds a magazine (Reload.Magazine), after the reload at the magazine.</summary>
        ReloadPouch = 7,

        /// <summary>The tool hand at a holster (Holsters), before the helmet. Level with the pouch, which only the off hand takes.</summary>
        Holsters = 7,

        /// <summary>The helmet, its visor and the lamp (HeadGestures): first of the physical interactions after the held tool and its holsters.</summary>
        HeadGestures = 10,

        /// <summary>The ship model held in the hand (ShipModel), before the external view screen and the rest.</summary>
        ShipModel = 12,

        /// <summary>The external view screen moved by its edge or face (ViewScreenGrab), before floating items, grab-to-use and the hull.</summary>
        ViewScreenGrab = 15,

        /// <summary>A floating item picked up (ItemGrab), after the helmet, before grab-to-use and the hull.</summary>
        ItemGrab = 20,

        /// <summary>One of ColbyJack1134's cockpit switches under the fingertip (CockpitTouch), ahead of his sticks.</summary>
        CockpitTouch = 28,

        /// <summary>One of ColbyJack1134's cockpit sticks (CockpitControls), ahead of grab-to-use.</summary>
        CockpitControls = 29,

        /// <summary>A use object the hand closes on (GrabUse): a door, a seat, a terminal. Before the hull, so a grip on a door stays the door's.</summary>
        GrabUse = 30,

        /// <summary>
        /// Other plugins' and mods' grip claims (SevrApi <c>RegisterGripClaim</c>, Api.ApiGrips): after everything of the game's the
        /// hand can take or use, before the hull and ladders, so what they add to a ship can be taken by hand.
        /// </summary>
        Outside = 35,

        /// <summary>The hull or a ladder pulled along (HullGrab), after everything the hand can take off or work on a ship.</summary>
        HullGrab = 40,

        /// <summary>The colour tool's skin cycle (PaintMode): the grip in empty air with the tool in hand, so last.</summary>
        PaintMode = 51,
    }

    /// <summary>
    /// Who holds each hand's grip. In the world the takers are asked once a press, in <see cref="GripPriority"/> order; a menu
    /// feature claims the grip outright as it decides at the press. The claim also carries whether the press's Use waits for
    /// the grip to be let go, and whether that Use was dropped meanwhile.
    /// </summary>
    /// <remarks>
    /// <para>World: on a new press, the first caller of <see cref="Owner"/> asks the takers in order, so it does not matter which
    /// feature looks first as long as each taker decides from the hand's pose at call time. A taker must be cheap; one that
    /// throws is logged once and skipped from then on. The owner lasts until the grip is let go; <see cref="Release"/> ends it
    /// sooner, and the grip then means nothing until pressed again.</para>
    /// <para>Menu: <see cref="ClaimInMenu"/> holds the grip until the claimant lets it go (<see cref="Release"/>), since it
    /// follows its own press (an inventory pull ends as its grip opens).</para>
    /// <para>Use: <see cref="HoldUse"/> makes the press's Use wait until the grip is let go (<see cref="UseWaits"/>);
    /// <see cref="DropUse"/> drops it, which <see cref="UseDropped"/> tells until the next press.</para>
    /// </remarks>
    internal sealed class GripTable
    {
        /// <summary>True: this feature takes the grip that just started on <paramref name="hand"/>.</summary>
        public delegate bool Taker(Hand hand);

        /// <summary>The owner of a world grip let go of early (<see cref="Release"/>): the grip means nothing until pressed again.</summary>
        public const string Released = "released";

        private sealed class Entry
        {
            public string Name;
            public GripPriority Priority;
            public Taker Take;
            public bool Failed;
        }

        private readonly Func<Hand, bool> pressed, newPressed;
        private readonly List<Entry> takers = new List<Entry>();
        private readonly string[] owner = new string[2], menu = new string[2];
        private readonly bool[] down = new bool[2], fresh = new bool[2], waits = new bool[2], dropped = new bool[2];

        /// <param name="pressed">The grip is held on the hand now.</param>
        /// <param name="newPressed">The grip went down on the hand this frame.</param>
        public GripTable(Func<Hand, bool> pressed, Func<Hand, bool> newPressed)
        {
            this.pressed = pressed ?? throw new ArgumentNullException(nameof(pressed));
            this.newPressed = newPressed ?? throw new ArgumentNullException(nameof(newPressed));
        }

        /// <summary>Adds a world taker; a second call with the same name replaces it, and goes after the others at its place.</summary>
        public void Register(string name, GripPriority priority, Taker take)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("A grip taker needs a name", nameof(name));
            if (take == null)
                throw new ArgumentNullException(nameof(take));
            takers.RemoveAll(e => e.Name == name);
            int at = takers.FindIndex(e => e.Priority > priority);
            takers.Insert(at < 0 ? takers.Count : at, new Entry { Name = name, Priority = priority, Take = take });
        }

        /// <summary>The world feature holding this hand's grip, <see cref="Released"/>, or null: the grip means what it always has.</summary>
        public string Owner(Hand hand)
        {
            int i = (int)hand;
            if (!pressed(hand))
            {
                down[i] = false;
                fresh[i] = false;
                waits[i] = false;
                owner[i] = null;
                return null;
            }
            // A new press asks again even when nobody looked while the grip was up (VRControls only asks while it is down,
            // the wheel only on a press); asked once per press, however many callers that frame.
            bool isNew = newPressed(hand);
            if (!isNew)
                fresh[i] = false;
            if (!down[i] || (isNew && !fresh[i]))
            {
                down[i] = true;
                fresh[i] = isNew;
                waits[i] = false;
                dropped[i] = false;
                owner[i] = null;
                foreach (Entry e in takers)
                {
                    if (e.Failed)
                        continue;
                    bool took;
                    try
                    {
                        took = e.Take(hand);
                    }
                    catch (Exception ex)
                    {
                        e.Failed = true;
                        Log.Error(ex, $"Grip: {e.Name} failed deciding a grab and is skipped from now on");
                        continue;
                    }
                    if (took)
                    {
                        owner[i] = e.Name;
                        Log.Info($"Grip: {hand} hand taken by {e.Name}");
                        break;
                    }
                }
            }
            return owner[i];
        }

        /// <summary>This world feature holds the hand's grip now.</summary>
        public bool Holds(Hand hand, string name) => Owner(hand) == name;

        /// <summary>
        /// A menu feature takes the grip of <paramref name="hand"/>, held now, as it decides at the press; false when the grip
        /// is open or another menu feature has it.
        /// </summary>
        public bool ClaimInMenu(Hand hand, string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("A grip claim needs a name", nameof(name));
            int i = (int)hand;
            if (!pressed(hand) || (menu[i] != null && menu[i] != name))
                return false;
            menu[i] = name;
            return true;
        }

        /// <summary>The menu feature holding this hand's grip, or null: on a panel the grip is the right mouse button.</summary>
        public string MenuOwner(Hand hand) => menu[(int)hand];

        /// <summary>
        /// Lets go before the button is released (the thing held went away, the pull ended): a world grip then means nothing
        /// until pressed again, a menu grip is the button again.
        /// </summary>
        public void Release(Hand hand, string name)
        {
            int i = (int)hand;
            if (owner[i] == name)
                owner[i] = Released;
            if (menu[i] == name)
                menu[i] = null;
        }

        /// <summary>The Use of the grip held on <paramref name="hand"/> waits for the grip to be let go.</summary>
        public void HoldUse(Hand hand)
        {
            Owner(hand);
            int i = (int)hand;
            if (down[i])
                waits[i] = true;
        }

        /// <summary>The grip is held and its Use waits for it to be let go (<see cref="HoldUse"/>).</summary>
        public bool UseWaits(Hand hand)
        {
            Owner(hand);
            return waits[(int)hand];
        }

        /// <summary>Drops the Use that waits, so it is not done when the grip is let go; nothing when no Use waits.</summary>
        public void DropUse(Hand hand)
        {
            int i = (int)hand;
            if (UseWaits(hand))
                dropped[i] = true;
        }

        /// <summary>The Use that waited on this hand's grip was dropped (<see cref="DropUse"/>); told until the grip is pressed again.</summary>
        public bool UseDropped(Hand hand)
        {
            Owner(hand);
            return dropped[(int)hand];
        }
    }
}
