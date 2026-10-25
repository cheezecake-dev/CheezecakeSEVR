using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game.ModAPI.Ingame.Utilities;

namespace SpaceEngineersVR.Scenarios.BugSwarm
{
    /// <summary>
    /// A swarm block's settings: the [Bug Swarm] section of a button panel's Custom Data. Kept there they travel with the
    /// world, a blueprint and the Workshop, reach everyone in a multiplayer game, and can be edited by hand.
    /// </summary>
    internal sealed class SwarmSettings
    {
        internal const string Section = "Bug Swarm";

        internal sealed class Number
        {
            public readonly string Key, Label, Unit, Tooltip;
            public readonly int Default, Min, Max;

            public Number(string key, string label, int value, int min, int max, string unit = "", string tooltip = null)
            {
                Key = key; Label = label; Default = value; Min = min; Max = max; Unit = unit; Tooltip = tooltip;
            }
        }

        internal const string StartWave = "StartWave", FirstWaveBugs = "FirstWaveBugs", BugsPerWave = "BugsPerWave",
            MostAtOnce = "MostAtOnce", DamagePerWave = "DamagePerWave", BiteDamage = "BiteDamage", PlayerReach = "PlayerReach",
            StingersFrom = "StingersFrom", BrutesFrom = "BrutesFrom", FirstWaveDelay = "FirstWaveDelay", Break = "Break",
            StragglerWait = "StragglerWait", LastWave = "LastWave";
        private const string EnabledKey = "Enabled", DefendKey = "Defend", SuppliesKey = "Supplies",
            RestockKey = "Restock";

        internal static readonly Number[] Numbers =
        {
            new Number(StartWave, "Start at wave", 1, 1, 50, tooltip: "The wave a run starts at, as big and as hard as that wave is in a run from the first."),
            new Number(LastWave, "Last wave", 10, 0, 50, tooltip: "The run is won when this wave is beaten. 0: the waves never end."),
            new Number(FirstWaveBugs, "Bugs in the first wave", 4, 1, 40),
            new Number(BugsPerWave, "Bugs added each wave", 3, 0, 20),
            new Number(MostAtOnce, "Most bugs at once", 0, 0, 128, tooltip: "0: as many as the world's bot limit (32 unless the world is experimental). More than that, the swarm fields while it runs, which a slower PC may not keep up with."),
            new Number(DamagePerWave, "Damage added each wave", 10, 0, 100, "%", "How much harder each wave bites than the one before, players and blocks alike."),
            new Number(BiteDamage, "Bite damage to blocks", 120, 10, 1000, tooltip: "What a bug's bite does to a block in the first wave. A brute's does three times as much."),
            new Number(PlayerReach, "Bugs go for players within", 10, 0, 50, "m", "A bug or brute leaves the block it is after for a player this close. Stingers hunt players wherever they are."),
            new Number(StingersFrom, "Stingers from wave", 2, 1, 50),
            new Number(BrutesFrom, "Brutes from wave", 3, 1, 50),
            new Number(FirstWaveDelay, "Before the first wave", 10, 0, 120, "s"),
            new Number(Break, "Between waves", 45, 0, 300, "s", "The time to rebuild and restock before the next wave."),
            new Number(StragglerWait, "Wait for stragglers", 0, 0, 600, "s", "0: a wave lasts until every bug in it is dead. Otherwise, how long after its last bug comes it ends anyway."),
        };

        internal sealed class Switch
        {
            public readonly string Key, Label, Tooltip;

            public Switch(string key, string label, string tooltip)
            {
                Key = key; Label = label; Tooltip = tooltip;
            }
        }

        internal const string PutBack = "PutBack", BringBack = "BringBack", HeadShots = "HeadShots", Loot = "Loot",
            Pistols = "Pistols", PlaceSupplies = "PlaceSupplies", Climb = "Climb", WayIn = "WayIn", InReach = "InReach";

        /// <summary>What a run adds to the game, each on unless the level's maker turns it off; off, that part plays as the game does.</summary>
        internal static readonly Switch[] Switches =
        {
            new Switch(Climb, "Bugs climb walls", "Bugs walk up walls and over the top of what is in their way, as spiders do. Off: they stay on the ground, as the game's spiders do."),
            new Switch(WayIn, "Bugs look for a way in", "Bugs head for gaps and open doors in a wall rather than chewing through it. Off: the game's own pathing."),
            new Switch(InReach, "Bugs bite only what they can reach", "A bug bites only a block it can see from where it stands, so the target cannot be bitten through a wall. Off: bites reach through."),
            new Switch(PutBack, "Put the base back after a run", "When a run is lost, won or ended, everything the bugs could reach is put back as it was when the run started, and each player's health, food, energy, oxygen and hydrogen with it. Off: what they broke stays broken."),
            new Switch(BringBack, "Bring players back at the base", "A player who dies in a run comes back at the base a moment later, away from the bugs, at full health, food and suit levels, with no respawn screen. Off: the game's own respawn."),
            new Switch(HeadShots, "Headshots on bugs", "In a run a shot to a bug's head does the round's headshot damage, as on an astronaut. Off: bugs take the same damage anywhere."),
            new Switch(Loot, "Bugs drop guns and ammo", "A bug killed by a player now and then drops ammo for the gun that hurt it most, or a better gun; brutes always drop something."),
            new Switch(Pistols, "Unlimited pistols in the supplies", "In a run the supplies container always holds pistols and pistol magazines."),
            new Switch(PlaceSupplies, "Supplies box if none is chosen", "With no supplies container chosen, a run puts a small cargo container on the floor beside this panel, gone again when the base is put back."),
        };

        // Parsing Custom Data on every look from the terminal would be wasteful; a block's settings are parsed again only
        // when its Custom Data has changed.
        private static readonly Dictionary<long, SwarmSettings> cache = new Dictionary<long, SwarmSettings>();

        private readonly IMyTerminalBlock block;
        private readonly MyIni ini = new MyIni();
        private string parsed;

        private SwarmSettings(IMyTerminalBlock block)
        {
            this.block = block;
        }

        public static SwarmSettings Of(IMyTerminalBlock block)
        {
            SwarmSettings settings;
            if (!cache.TryGetValue(block.EntityId, out settings))
            {
                if (cache.Count > 500)
                    cache.Clear();
                cache[block.EntityId] = settings = new SwarmSettings(block);
            }
            if (!ReferenceEquals(settings.parsed, block.CustomData))
                settings.Parse();
            return settings;
        }

        internal static void Forget() => cache.Clear();

        private void Parse()
        {
            parsed = block.CustomData;
            ini.Clear();
            // Custom Data that is not in sections (a note, a script's own text) is kept, after the settings.
            if (!ini.TryParse(parsed ?? ""))
            {
                ini.Clear();
                ini.EndContent = parsed;
            }
        }

        private void Save()
        {
            string data = ini.ToString();
            block.CustomData = data;
            parsed = block.CustomData;
        }

        public bool Enabled
        {
            get { return ini.Get(Section, EnabledKey).ToBoolean(false); }
            set { ini.Set(Section, EnabledKey, value); Save(); }
        }

        /// <summary>The name of the block the swarm is after; empty for the swarm block itself.</summary>
        public string Defend
        {
            get { return ini.Get(Section, DefendKey).ToString(""); }
            set { ini.Set(Section, DefendKey, value); Save(); }
        }

        /// <summary>The name of the container restocked between waves; empty for none.</summary>
        public string Supplies
        {
            get { return ini.Get(Section, SuppliesKey).ToString(""); }
            set { ini.Set(Section, SuppliesKey, value); Save(); }
        }

        /// <summary>
        /// What the supplies container is filled back up to, as "Type/Subtype=count" lines: what it held the first time
        /// the swarm was woken, unless the level's maker wrote their own.
        /// </summary>
        public string Restock
        {
            get { return ini.Get(Section, RestockKey).ToString(""); }
            set { ini.Set(Section, RestockKey, value); Save(); }
        }

        public bool On(string key) => ini.Get(Section, key).ToBoolean(true);

        public void Set(string key, bool on)
        {
            ini.Set(Section, key, on);
            Save();
        }

        public int this[string key]
        {
            get
            {
                Number number = Find(key);
                return Math.Max(number.Min, Math.Min(number.Max, ini.Get(Section, key).ToInt32(number.Default)));
            }
            set
            {
                Number number = Find(key);
                ini.Set(Section, key, Math.Max(number.Min, Math.Min(number.Max, value)));
                Save();
            }
        }

        private static Number Find(string key)
        {
            foreach (Number number in Numbers)
                if (number.Key == key)
                    return number;
            throw new ArgumentException("No swarm setting " + key, nameof(key));
        }
    }
}
