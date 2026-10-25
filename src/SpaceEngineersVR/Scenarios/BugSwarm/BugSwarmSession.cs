using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.Game;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Graphics.GUI;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using Sandbox.ModAPI;
using SpaceEngineers.Game.ModAPI;
using VRage;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Scenarios.BugSwarm
{
    /// <summary>
    /// Bug Swarm: a button panel made a swarm block (<see cref="SwarmControls"/>) wakes waves of spiders when pressed. They
    /// come up out of the ground beyond the farthest block of its grid and go for the block chosen in its control panel
    /// (<see cref="SwarmAi"/>), chewing through whatever stands in their way, until that block stops working. Between waves
    /// the supplies container is restocked and status screens on the grid keep the score.
    /// </summary>
    /// <remarks>
    /// It runs where the world is simulated, the host, so everyone in the world plays the same swarm: the waves, the bugs
    /// and what they go for, their bites, the loot, the restocks, the status screens, bringing players back and putting
    /// the base back are all the host's, and reach the other players through the game's own sync (the bugs are the game's
    /// spiders, the drops its floating items, the screens its text panels). Players without the plugin see and fight the
    /// bugs but not the swarm block's controls; a press of theirs wakes the swarm after a countdown on screen. Players with
    /// the plugin ask the host on the swarm's own channel (<see cref="SwarmMessage"/>), and the host tells them whether its
    /// game runs swarm blocks and whether a run is on. A game hosted without the plugin runs no swarm, and the control
    /// panel says so. One swarm runs at a time in a world. It needs a planet, on whose ground the bugs walk, and a grid
    /// standing on it: a ship that takes off ends the run.
    /// </remarks>
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    public class BugSwarmSession : MySessionComponentBase
    {
        private const ushort Channel = 39317;
        private const string BestKey = "BugSwarm.BestWave.";
        private const string Marked = "Bug Swarm";   // status screens and alarms on the grid have it in their names

        private const int Second = 60;
        private const int SpawnInterval = 75;
        private const int MostSides = 6;
        private const double SpawnNearest = 10, SpawnFarthest = 30;   // metres beyond the farthest block
        private const double Wandered = 300;                          // metres beyond that a bug no longer counts
        private const double Grounded = 3;                            // metres: the lowest block this near the ground
        private const double TakeOff = 5;                             // metres: a ship risen this far has taken off

        private const double BiteRadius = 1.9;   // round the bug's middle: the blocks it can reach
        private static readonly List<IMyEntity> NoEntities = new List<IMyEntity>();
        private const double PlayerNear = 3;     // with a player this close a stinger's own attack is on the player
        private const int BiteInterval = 90;
        private const double StuckWithin = 1;   // metres: a bug that has not got this far
        private const int StuckFor = 5 * Second;   // in this long is stuck, on the ground or a wall, and bites its way on
        private const string Bug = "SpaceSpider", Brute = "SpaceSpiderBrown", Stinger = "SpaceSpiderGreen";
        internal const string BruteKind = Brute, StingerKind = Stinger;

        // what bugs drop, and the pistols and pistol ammo the supplies container never runs out of while a run is on
        private readonly SwarmLoot loot = new SwarmLoot();
        private static readonly MyTuple<MyDefinitionId, int>[] Unlimited =
        {
            new MyTuple<MyDefinitionId, int>(new MyDefinitionId(typeof(MyObjectBuilder_PhysicalGunObject), "SemiAutoPistolItem"), 2),
            new MyTuple<MyDefinitionId, int>(new MyDefinitionId(typeof(MyObjectBuilder_AmmoMagazine), "SemiAutoPistolMagazine"), 20),
        };

        private enum Phase { Ready, Break, Wave, Lost }

        // The settings, read from the swarm block each time it is woken.
        private int startWave, lastWave, firstWaveBugs, bugsPerWave, mostAlive, stingersFrom, brutesFrom;
        private int firstWaveDelay, breakLength, stragglerTimeout;
        private float damagePerWave, biteDamage;

        private static readonly MyStringHash SpiderDamage = MyStringHash.GetOrCompute("Spider");

        private IMyTerminalBlock block, target, supplies;
        private readonly List<MyTuple<MyDefinitionId, int>> restock = new List<MyTuple<MyDefinitionId, int>>();
        private readonly List<IMySoundBlock> alarms = new List<IMySoundBlock>();
        private readonly List<IMyTextPanel> screens = new List<IMyTextPanel>();
        private readonly List<IMyCubeGrid> gridList = new List<IMyCubeGrid>();
        private readonly HashSet<IMyEntity> grids = new HashSet<IMyEntity>();
        private bool anchored;
        private double reach, startHeight;
        private MyPlanet planet;

        private Phase phase = Phase.Ready;
        private int tick, phaseEnds, runStarted, lastSpawn, nextSpawn;
        private int wave, toSpawn, spawned, alive, best, heldFor, bites;
        private readonly List<Vector3D> bearings = new List<Vector3D>();
        private readonly Random random = new Random();

        private readonly HashSet<IMyEntity> found = new HashSet<IMyEntity>();
        private readonly HashSet<IMyCharacter> humans = new HashSet<IMyCharacter>();
        private readonly List<IMyPlayer> players = new List<IMyPlayer>();
        private readonly List<IMyCharacter> bugs = new List<IMyCharacter>();
        private readonly Dictionary<long, IMyCharacter> bugIds = new Dictionary<long, IMyCharacter>();
        private readonly Dictionary<long, int> nextBite = new Dictionary<long, int>();
        private readonly Dictionary<long, MyTuple<Vector3D, int>> lastMoved = new Dictionary<long, MyTuple<Vector3D, int>>();
        private readonly StringBuilder text = new StringBuilder();
        private static bool damageHooked;

        private static BugSwarmSession session;

        // Everything the bugs could reach as it was when the run started, put back when the run is over: the grids there
        // (the base, vehicles and anything else, each with what is joined to it), the pieces broken off them since, and the
        // loose items on the ground then.
        private readonly List<MyObjectBuilder_CubeGrid> before = new List<MyObjectBuilder_CubeGrid>();
        private readonly HashSet<long> gridsBefore = new HashSet<long>(), pieces = new HashSet<long>(), itemsBefore = new HashSet<long>();
        private readonly List<IMyCubeGrid> watched = new List<IMyCubeGrid>();
        private BoundingSphereD baseArea;
        private const double KeptBeyond = SpawnFarthest + 20;   // metres beyond the farthest block
        private int putBackAt, rebuildAt;
        internal const int PutBackAfterSeconds = 10;
        private const int PutBackAfter = PutBackAfterSeconds * Second;

        // A press by a player without the plugin, who cannot be shown the warning before a run, wakes the swarm after a
        // countdown on screen instead (players with it are known by Multiplayer.PeerChannel's handshake).
        private IMyTerminalBlock wakeLater;
        private long wakeLaterBy;
        private int wakeAt;
        private const int Countdown = 10 * Second;

        // Players who died in the run, and when they are brought back at the base.
        private readonly Dictionary<long, int> backAt = new Dictionary<long, int>();

        /// <summary>A player's health, food and suit levels; a health or food below 0 is full.</summary>
        private struct Levels
        {
            public float Health, Food, Energy, Oxygen, Hydrogen;
        }

        // each player's levels when the run started, given back with the base; and the levels a player brought back at the
        // base is given once their new body is in the world
        private readonly Dictionary<long, Levels> levelsBefore = new Dictionary<long, Levels>();
        private readonly Dictionary<long, Levels> levelsDue = new Dictionary<long, Levels>();
        internal const int BackAfterSeconds = 3;
        private const int BackAfter = BackAfterSeconds * Second;

        /// <summary>Whether a run is on in this world, as the host has said: on every machine, for the respawn notice.</summary>
        internal static bool RunOn { get; private set; }

        /// <summary>
        /// While a run is on, a shot to a spider's head counts as a headshot, as it does on an astronaut: the game's own rule,
        /// which hurts by the round's headshot damage (three times its body damage for rifle and pistol rounds). The game's
        /// spiders have no weak point of their own; the head is the first of their hit capsules.
        /// </summary>
        private static void HeadShots(bool on)
        {
            if (headShotsOn == on)
                return;
            headShotsOn = on;
            foreach (var character in Sandbox.Definitions.MyDefinitionManager.Static.Characters)
                if (character.Id.SubtypeName.StartsWith("Space_spider", StringComparison.Ordinal))
                {
                    if (on)
                        character.WeakPointBoneIndices.Add(HeadCapsule);
                    else
                        character.WeakPointBoneIndices.Remove(HeadCapsule);
                }
        }

        private static bool headShotsOn;

        // the run's switches (SwarmSettings.Switches), read when it is woken
        private bool putBack, bringBack, headShots, lootOn, pistols, placeSupplies, inReach;

        /// <summary>Whether this run's bugs climb walls, and look for a way in (SwarmSettings.Climb, .WayIn).</summary>
        internal bool Climbs { get; private set; }
        internal bool FindsWayIn { get; private set; }

        /// <summary>Whether the host brings players who die in this run back at the base, so the respawn screen waits.</summary>
        internal static bool BringsBack { get; private set; }
        private const int HeadCapsule = 0;

        /// <summary>When the host last said the run was over, for the respawn notice.</summary>
        internal static DateTime RunOverAt { get; private set; }

        // where players who die are brought back, fixed when the run starts; what they are told when the run ended meanwhile
        private MatrixD comeBack;
        private string endNote;

        // players just brought back, and until when bugs cannot hurt them
        private readonly Dictionary<long, int> safeUntil = new Dictionary<long, int>();
        private const int SafeFor = 3 * Second;

        /// <summary>The world's swarm while a run is on, between its first wave's call and its end; otherwise null.</summary>
        internal static BugSwarmSession Active { get; private set; }

        /// <summary>
        /// Bug Swarm is shelved until its scenario's map is made: the scenario is not listed, the option is not in VR options,
        /// and this component patches nothing, adds no controls, hears no messages and never updates. False turns it all back on
        /// (with the <c>scenarios</c> asset in the data files and the export, and its test back in Tests/vr).
        /// </summary>
        internal static readonly bool Shelved = true;

        public override void BeforeStart()
        {
            if (Shelved)
            {
                SetUpdateOrder(MyUpdateOrder.NoUpdate);
                return;
            }
            session = this;
            SwarmAi.Patch();
            if (!MyAPIGateway.Utilities.IsDedicated)
                SwarmControls.Add();
            MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(Channel, Received);
            hostState = null;
            stateRunOn = false;
            clientFrames = 0;
            scenarioHere = Scenarios.ScenarioRegistry.IsRunning(SwarmScenario.Name);
            if (scenarioHere)
                Log.Info($"Bug Swarm: this world is the {SwarmScenario.Name} scenario: swarm blocks work in it, and the base is always put back");
            if (MyAPIGateway.Multiplayer.IsServer)
            {
                MyVisualScriptLogicProvider.PlayerDied += Died;
                Multiplayer.PeerChannel.PlayerWithPlugin += Greet;
                swarmOnSaid = BlocksOn;
            }
            // The game's damage handlers stay registered for the whole game, so this one is added once and finds the swarm in play.
            if (damageHooked)
                return;
            damageHooked = true;
            MyAPIGateway.Session.DamageSystem.RegisterBeforeDamageHandler(0, (object hit, ref MyDamageInformation info) =>
            {
                if (Active == null)
                    return;
                info.Amount *= Active.DamageScale(info.AttackerId, hit as IMyCharacter);
                var character = hit as IMyCharacter;
                IMyCharacter bug;
                if (character != null && Active.bugIds.TryGetValue(character.EntityId, out bug) && !bug.IsDead)
                    if (Active.lootOn)
                        Active.loot.Hurt(bug, info.AttackerId, info.Amount, Kind(bug));
            });
        }

        protected override void UnloadData()
        {
            if (Shelved)
                return;
            if (Active == this)
                Active = null;
            if (session == this)
                session = null;
            RunOn = false;
            scenarioHere = false;
            HeadShots(false);
            SwarmControls.Remove();
            SwarmSettings.Forget();
            MyAPIGateway.Multiplayer?.UnregisterSecureMessageHandler(Channel, Received);
            MyVisualScriptLogicProvider.PlayerDied -= Died;
            Multiplayer.PeerChannel.PlayerWithPlugin -= Greet;
            hostState = null;
            StopWatching();
        }

        /// <summary>Tells every machine, this one too, whether a run is on.</summary>
        private static void SayRunOn(bool on)
        {
            RunOn = on;
            if (on)
                BringsBack = Active?.bringBack ?? true;
            HeadShots(on && (Active?.headShots ?? false));
            if (!on)
                RunOverAt = DateTime.UtcNow;
            SayState();
        }

        /// <summary>On the host: its state, as players with the plugin are told it.</summary>
        private static SwarmMessage State()
        {
            SwarmState state = SwarmState.None;
            if (BlocksOn)
                state |= SwarmState.SwarmOn;
            if (scenarioHere)
                state |= SwarmState.Scenario;
            if (RunOn)
                state |= SwarmState.RunOn;
            if (BringsBack)
                state |= SwarmState.BringsBack;
            return new SwarmMessage(SwarmKind.State, 0, state);
        }

        /// <summary>On the host: its state to every player with the plugin. Players without it are sent nothing.</summary>
        private static void SayState()
        {
            if (!MyAPIGateway.Multiplayer.MultiplayerActive)
                return;
            byte[] state = State().Write();
            foreach (ulong player in Multiplayer.PeerChannel.Peers)
                if (Sandbox.Game.Multiplayer.Sync.Clients.HasClient(player))
                    MyAPIGateway.Multiplayer.SendMessageTo(Channel, state, player);
        }

        /// <summary>On the host: a player with the plugin said hello, on joining or again; they are told the host's state.</summary>
        private static void Greet(ulong player)
        {
            if (session != null)
                MyAPIGateway.Multiplayer.SendMessageTo(Channel, State().Write(), player);
        }

        // On the host: whether its players were last told that its game runs swarm blocks.
        private static bool swarmOnSaid;

        // On the host: whether the world is the VR Bug Swarm scenario, found when it loaded. It is the game's own campaign
        // state that says so (Scenarios.ScenarioRegistry.IsRunning), which only the host has.
        private static bool scenarioHere;

        /// <summary>Whether this game plays the VR Bug Swarm scenario: as the host found, or, on a player's game, as it said.</summary>
        internal static bool InScenario =>
            session != null && (MyAPIGateway.Multiplayer == null || MyAPIGateway.Multiplayer.IsServer
                ? scenarioHere
                : hostState.HasValue && (hostState.Value & SwarmState.Scenario) != 0);

        /// <summary>
        /// Whether button panels can be swarm blocks in this game: with Bug Swarm on button panels on in the options, and
        /// always in the scenario, whatever that option says (the host's, in a game it hosts).
        /// </summary>
        internal static bool BlocksOn => VRSettings.SwarmBlocks || InScenario;

        // On a player's game: the host's state as it last said, null until it has; whether that said a run was on, to note
        // when one ends; and simulation frames since loading.
        private static SwarmState? hostState;
        private static bool stateRunOn;
        private static int clientFrames;

        /// <summary>
        /// Simulation frames after loading by which a host with the plugin has said its state (it does as soon as this game's
        /// hello reaches it, a second in): with none by then the host's plugin has no swarm, or another version of it.
        /// </summary>
        private const int StateBy = 20 * Second;

        /// <summary>Why the swarm cannot be woken from this game, for the control panel and a press.</summary>
        internal enum Blocked { None, Asking, NoPlugin, HostOff }

        /// <summary>Whether this game can ask for the swarm: always where it is hosted; elsewhere as its host has said.</summary>
        internal static Blocked WhyNot()
        {
            if (MyAPIGateway.Multiplayer == null || MyAPIGateway.Multiplayer.IsServer)
                return Blocked.None;
            bool? host = Multiplayer.PeerChannel.HostHasPlugin;
            if (host == false)
                return Blocked.NoPlugin;
            if (hostState.HasValue)
                return (hostState.Value & SwarmState.SwarmOn) != 0 ? Blocked.None : Blocked.HostOff;
            return host == true && clientFrames > StateBy ? Blocked.HostOff : Blocked.Asking;
        }

        /// <summary>What a player is told when the swarm cannot be woken from their game.</summary>
        internal static string Why(Blocked why)
        {
            switch (why)
            {
                case Blocked.Asking:
                    return "Still asking this game's host whether it can run the swarm. Try again in a moment.";
                case Blocked.NoPlugin:
                    return "The swarm cannot wake in this game: its host does not have Cheezecake's SE VR.";
                case Blocked.HostOff:
                    return "The swarm cannot wake in this game: the host's game has Bug Swarm on button panels off, or another version of Cheezecake's SE VR.";
                default:
                    return null;
            }
        }

        /// <summary>
        /// A button of a swarm block was pressed, here where the world is simulated; <paramref name="by"/> is who pressed it
        /// (0 for a press by the grid itself, a timer or a sensor). A player is asked first whether to save, on their own
        /// machine.
        /// </summary>
        internal static void Pressed(IMyTerminalBlock panel, long by)
        {
            if (session == null || !BlocksOn || !SwarmSettings.Of(panel).Enabled)
                return;
            if (Active == session && session.block == panel)
            {
                // a press in the run: the player who pressed it is asked whether to end it
                if (by == 0)
                    return;
                if (!MyAPIGateway.Utilities.IsDedicated && MyAPIGateway.Session.Player?.IdentityId == by)
                    AskEndThen(panel);
                else if (Multiplayer.PeerChannel.HasPlugin(MyAPIGateway.Players.TryGetSteamId(by)))
                    MyAPIGateway.Multiplayer.SendMessageTo(Channel, new SwarmMessage(SwarmKind.AskEnd, panel.EntityId).Write(),
                        MyAPIGateway.Players.TryGetSteamId(by));
                else
                    session.End(panel, by);
            }
            else if (Active != null || by == 0)
                session.Wake(panel, by);
            else if (!MyAPIGateway.Utilities.IsDedicated && MyAPIGateway.Session.Player?.IdentityId == by)
                AskFirstThen(panel);
            else
            {
                ulong steamId = MyAPIGateway.Players.TryGetSteamId(by);
                if (Multiplayer.PeerChannel.HasPlugin(steamId))
                    MyAPIGateway.Multiplayer.SendMessageTo(Channel, new SwarmMessage(SwarmKind.AskFirst, panel.EntityId).Write(), steamId);
                else if (session.wakeLater == null)
                {
                    session.wakeLater = panel;
                    session.wakeLaterBy = by;
                    session.wakeAt = session.tick + Countdown;
                    Tell(by, $"The swarm wakes in {Countdown / Second} s and chews through anything between it and {Defended(panel)}. " +
                        "The game is saved first, and the base is put back as it was when the run ends.", "Red", 8000);
                }
            }
        }

        /// <summary>
        /// Wake or end the swarm from a swarm block's control panel; waking asks first whether to save. Where the host
        /// cannot run the swarm the player is told why instead.
        /// </summary>
        internal static void Request(IMyTerminalBlock panel, bool wake)
        {
            if (session == null)
                return;
            Blocked why = WhyNot();
            if (why != Blocked.None)
            {
                MyAPIGateway.Utilities.ShowNotification(Why(why), 6000, "Red");
                return;
            }
            if (wake)
            {
                if (!RunOn)
                    AskFirstThen(panel);
                else
                    Send(panel, SwarmKind.Wake);
            }
            else
                Send(panel, SwarmKind.End);
        }

        /// <summary>
        /// The warning before a run, on the machine of the player who wakes it: the bugs break the base, and saving first is
        /// strongly suggested even though the base is put back when the run is over.
        /// </summary>
        private static void AskFirstThen(IMyTerminalBlock panel)
        {
            // The scenario is a game of its own, saved by itself and put back whole each time, so it asks nothing first.
            if (InScenario)
            {
                Send(panel, SwarmKind.Wake);
                return;
            }
            var text = new StringBuilder("The bugs come up out of the ground and chew through anything between them and ")
                .Append(Defended(panel)).Append(".\n\n")
                .Append("We strongly suggest saving the game before you start. The base is put back as it was when the run ends, " +
                    "but if the game closes or crashes during the run, or saves itself in it, what the bugs broke stays broken.");
            MyGuiSandbox.AddScreen(MyGuiSandbox.CreateMessageBox(MyMessageBoxStyleEnum.Info, MyMessageBoxButtonsType.YES_NO_CANCEL,
                text, new StringBuilder("Wake the swarm"),
                yesButtonText: MyStringId.GetOrCompute("Save and start"), noButtonText: MyStringId.GetOrCompute("Start without saving"),
                cancelButtonText: MyStringId.GetOrCompute("Cancel"),
                focusedResult: MyGuiScreenMessageBox.ResultEnum.YES,
                callback: result =>
                {
                    if (result == MyGuiScreenMessageBox.ResultEnum.YES)
                        Send(panel, SwarmKind.Wake);
                    else if (result == MyGuiScreenMessageBox.ResultEnum.NO)
                        Send(panel, SwarmKind.WakeUnsaved);
                }));
        }

        /// <summary>Pressing the swarm block in its run: end it here, with the base put back as it was at the start?</summary>
        private static void AskEndThen(IMyTerminalBlock panel)
        {
            MyGuiSandbox.AddScreen(MyGuiSandbox.CreateMessageBox(MyMessageBoxStyleEnum.Info, MyMessageBoxButtonsType.YES_NO,
                new StringBuilder("End the run now? The bugs sink back into the ground, and the base is put back as it was when the run started."),
                new StringBuilder("End the run"),
                yesButtonText: MyStringId.GetOrCompute("End the run"), noButtonText: MyStringId.GetOrCompute("Keep fighting"),
                focusedResult: MyGuiScreenMessageBox.ResultEnum.NO,
                callback: result =>
                {
                    if (result == MyGuiScreenMessageBox.ResultEnum.YES)
                        Send(panel, SwarmKind.End);
                }));
        }

        private static string Defended(IMyTerminalBlock panel)
        {
            string defend = SwarmSettings.Of(panel).Defend;
            return defend.Length > 0 ? defend : panel.CustomName;
        }

        /// <summary>Wakes or ends the run here where the world is simulated, or asks the host to.</summary>
        private static void Send(IMyTerminalBlock panel, SwarmKind what)
        {
            if (session == null || panel.Closed)
                return;
            if (MyAPIGateway.Multiplayer.IsServer)
            {
                long by = MyAPIGateway.Session.Player?.IdentityId ?? 0;
                if (what == SwarmKind.Wake || what == SwarmKind.WakeUnsaved)
                    session.Wake(panel, by, what == SwarmKind.Wake);
                else
                    session.End(panel, by);
                return;
            }
            MyAPIGateway.Multiplayer.SendMessageToServer(Channel, new SwarmMessage(what, panel.EntityId).Write());
        }

        /// <summary>
        /// A message on the swarm's channel. The host takes requests, each from the player the game says sent it, who must
        /// have access to the panel; a player's game takes the host's questions and state, and only from the host.
        /// </summary>
        private void Received(ushort channel, byte[] bytes, ulong sender, bool fromServer)
        {
            SwarmMessage message;
            if (!SwarmMessage.TryRead(bytes, out message))
                return;
            bool server = MyAPIGateway.Multiplayer.IsServer;
            if (SwarmMessage.ToHost(message.Kind) != server || (server ? sender == 0 : !fromServer))
                return;
            if (message.Kind == SwarmKind.State)
            {
                hostState = message.State;
                RunOn = (message.State & SwarmState.RunOn) != 0;
                if (RunOn)
                    BringsBack = (message.State & SwarmState.BringsBack) != 0;
                else if (stateRunOn)
                    RunOverAt = DateTime.UtcNow;
                stateRunOn = RunOn;
                return;
            }
            var panel = MyAPIGateway.Entities.GetEntityById(message.Panel) as IMyButtonPanel;
            if (panel == null)
                return;
            if (!server)
            {
                if (message.Kind == SwarmKind.AskFirst)
                    AskFirstThen(panel);
                else if (message.Kind == SwarmKind.AskEnd)
                    AskEndThen(panel);
                return;
            }
            long by = MyAPIGateway.Players.TryGetIdentityId(sender);
            if (!SwarmSettings.Of(panel).Enabled)
                return;
            if (!BlocksOn)
            {
                Tell(by, "The swarm cannot wake in this game: the host's game has Bug Swarm on button panels off.", "Red", 6000);
                return;
            }
            if (!panel.HasPlayerAccess(by))
            {
                Tell(by, $"You have no access to {panel.CustomName}.", "Red");
                return;
            }
            if (message.Kind == SwarmKind.Wake || message.Kind == SwarmKind.WakeUnsaved)
                Wake(panel, by, message.Kind == SwarmKind.Wake);
            else
                End(panel, by);
        }

        private static void Tell(long player, string message, string font = "White", int ms = 4000)
        {
            MyVisualScriptLogicProvider.ShowNotification(message, ms, font, player);
        }

        private static void TellAll(string message, string font, int ms)
        {
            MyVisualScriptLogicProvider.ShowNotificationToAll(message, ms, font);
        }

        /// <summary>
        /// Each wave bites harder than the one before, the bugs' attacks on players and their chewing alike; a stinger's or
        /// a brute's attack on a player hurts more than a bug's (the game's spiders all hurt as a bug does).
        /// </summary>
        private float DamageScale(long attacker, IMyCharacter hit)
        {
            IMyCharacter bug;
            if (!bugIds.TryGetValue(attacker, out bug) || wave < 1)
                return 1f;
            bool onPlayer = hit != null;
            int until;
            if (onPlayer && safeUntil.TryGetValue(((Sandbox.Game.Entities.Character.MyCharacter)hit).GetPlayerIdentityId(), out until) && tick < until)
                return 0f;
            float scale = WaveScale();
            if (onPlayer)
            {
                string kind = Kind(bug);
                scale *= kind == Brute ? 60f / 35f : kind == Stinger ? 45f / 35f : 1f;
            }
            return scale;
        }

        private float WaveScale()
        {
            return 1f + damagePerWave * (wave - 1);
        }

        /// <summary>A bug's kind by its model, as its bot's subtype.</summary>
        internal static string Kind(IMyCharacter bug)
        {
            string model = bug.Definition != null ? bug.Definition.Id.SubtypeName : "";
            if (model.IndexOf("brown", StringComparison.OrdinalIgnoreCase) >= 0)
                return Brute;
            if (model.IndexOf("green", StringComparison.OrdinalIgnoreCase) >= 0)
                return Stinger;
            return Bug;
        }

        internal static bool IsStinger(IMyCharacter bug) => Kind(bug) == Stinger;

        /// <summary>The terminal blocks of the grid and of the grids on its rotors, pistons and hinges.</summary>
        internal static void TerminalBlocks(IMyCubeGrid grid, List<IMyTerminalBlock> into)
        {
            var group = new List<IMyCubeGrid>();
            var slims = new List<IMySlimBlock>();
            MyAPIGateway.GridGroups.GetGroup(grid, GridLinkTypeEnum.Mechanical, group);
            foreach (IMyCubeGrid g in group)
            {
                slims.Clear();
                g.GetBlocks(slims, b => b.FatBlock is IMyTerminalBlock);
                foreach (IMySlimBlock b in slims)
                    into.Add((IMyTerminalBlock)b.FatBlock);
            }
        }

        public override void UpdateBeforeSimulation()
        {
            if (MyAPIGateway.Session == null)
                return;
            if (!MyAPIGateway.Multiplayer.IsServer)
            {
                clientFrames++;
                return;
            }
            tick++;
            // Bug Swarm on button panels turned on or off in the host's game: its players with the plugin are told
            if (tick % Second == 0 && BlocksOn != swarmOnSaid)
            {
                swarmOnSaid = BlocksOn;
                SayState();
            }
            BringBack();
            GiveLevels();
            if (Active == this && pistols && tick % Second == 0)
                KeepPistols();
            PutBack();
            if (wakeLater != null && tick >= wakeAt)
            {
                if (!wakeLater.Closed)
                    Wake(wakeLater, wakeLaterBy);
                wakeLater = null;
            }
            if (block == null)
                return;
            if (block.Closed)
            {
                Stop();
                phase = Phase.Ready;
                block = null;
                return;
            }
            if (phase == Phase.Ready)
                return;
            if (tick % 15 == 0)
            {
                CountBugs();
                Bite();
            }
            if (tick % 30 == 0)
            {
                Advance();
                ShowStatus();
            }
            if (tick % (10 * Second) == 0 && phase == Phase.Wave)
                LogWave();
            if (phase == Phase.Wave && tick >= nextSpawn)
                SpawnNext();
        }

        private void Wake(IMyTerminalBlock panel, long by, bool save = true)
        {
            if (Active != null)
            {
                if (block != panel)
                    Tell(by, "A swarm is already on in this world.", "Red");
                return;
            }
            if (putBackAt > 0 || rebuildAt > 0)
            {
                Tell(by, "The base is still being put back from the last run.", "Red");
                return;
            }
            Vector3D at = panel.GetPosition();
            MyPlanet world = MyGamePruningStructure.GetClosestPlanet(at);
            if (world == null || MyGravityProviderSystem.CalculateNaturalGravityInPoint(at).Length() < 0.5f)
            {
                Tell(by, "The swarm comes up out of a planet's ground: build on a planet to call it.", "Red");
                return;
            }
            SwarmSettings settings = SwarmSettings.Of(panel);
            var blocks = new List<IMyTerminalBlock>();
            TerminalBlocks(panel.CubeGrid, blocks);
            IMyTerminalBlock goal = Named(blocks, settings.Defend) ?? panel;
            if (!goal.IsFunctional)
            {
                Tell(by, $"Weld {goal.CustomName} back up first.", "Red");
                return;
            }

            if (block != panel)
            {
                block = panel;
                best = 0;
                MyAPIGateway.Utilities.GetVariable(BestKey + panel.EntityId, out best);
            }
            planet = world;
            target = goal;
            Gather(blocks, settings);
            if (!anchored && LowestHeight() > Grounded)
            {
                Tell(by, "Land first: the bugs only reach a grid standing on the ground.", "Red");
                return;
            }

            startWave = settings[SwarmSettings.StartWave];
            firstWaveBugs = settings[SwarmSettings.FirstWaveBugs];
            bugsPerWave = settings[SwarmSettings.BugsPerWave];
            mostAlive = settings[SwarmSettings.MostAtOnce];
            lastWave = settings[SwarmSettings.LastWave];
            damagePerWave = settings[SwarmSettings.DamagePerWave] / 100f;
            biteDamage = settings[SwarmSettings.BiteDamage];
            stingersFrom = settings[SwarmSettings.StingersFrom];
            brutesFrom = settings[SwarmSettings.BrutesFrom];
            firstWaveDelay = settings[SwarmSettings.FirstWaveDelay] * Second;
            breakLength = settings[SwarmSettings.Break] * Second;
            stragglerTimeout = settings[SwarmSettings.StragglerWait] * Second;
            PlayerReach = settings[SwarmSettings.PlayerReach];
            // the scenario always puts the base back, whatever its Custom Data says
            putBack = scenarioHere || settings.On(SwarmSettings.PutBack);
            bringBack = settings.On(SwarmSettings.BringBack);
            headShots = settings.On(SwarmSettings.HeadShots);
            lootOn = settings.On(SwarmSettings.Loot);
            pistols = settings.On(SwarmSettings.Pistols);
            placeSupplies = settings.On(SwarmSettings.PlaceSupplies);
            Climbs = settings.On(SwarmSettings.Climb);
            FindsWayIn = settings.On(SwarmSettings.WayIn);
            inReach = settings.On(SwarmSettings.InReach);

            reach = 0;
            foreach (IMyEntity e in grids)
            {
                var grid = (IMyCubeGrid)e;
                var slims = new List<IMySlimBlock>();
                grid.GetBlocks(slims);
                foreach (IMySlimBlock b in slims)
                    reach = Math.Max(reach, Vector3D.Distance(grid.GridIntegerToWorld(b.Position), at) + grid.GridSize);
            }
            startHeight = Height(at);
            KeepBase(at);
            // a level with no supplies container gets one beside the swarm block, which goes again when the base is put back
            if (supplies == null && placeSupplies)
                supplies = PlaceSupplies();
            if (!save)
                Log.Info("Bug Swarm: started without saving");
            else if (MyAPIGateway.Utilities.IsDedicated)
                MyAPIGateway.Session.Save();
            else
                MyAsyncSaving.Start();

            wave = startWave - 1;
            bites = 0;
            runStarted = tick;
            phase = Phase.Break;
            phaseEnds = tick + firstWaveDelay;
            Active = this;
            SayRunOn(true);
            Log.Info($"Bug Swarm: woken at {block.CustomName}, after {target.CustomName}, bugs from {reach:0} m out, " +
                (anchored ? "a station" : "a landed ship"));
            TellAll($"The swarm is waking. Keep {target.CustomName} working.", "Red", 5000);
            ShowStatus();
        }

        private static IMyTerminalBlock Named(List<IMyTerminalBlock> blocks, string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;
            foreach (IMyTerminalBlock b in blocks)
                if (b.CustomName == name)
                    return b;
            return null;
        }

        /// <summary>The swarm block's grids, its screens, alarms and supplies, and what the supplies are restocked to.</summary>
        private void Gather(List<IMyTerminalBlock> blocks, SwarmSettings settings)
        {
            gridList.Clear();
            grids.Clear();
            MyAPIGateway.GridGroups.GetGroup(block.CubeGrid, GridLinkTypeEnum.Mechanical, gridList);
            anchored = false;
            foreach (IMyCubeGrid g in gridList)
            {
                grids.Add(g);
                anchored |= g.IsStatic;
            }
            screens.Clear();
            alarms.Clear();
            foreach (IMyTerminalBlock b in blocks)
            {
                if (b.CustomName.IndexOf(Marked, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (b is IMyTextPanel)
                {
                    screens.Add((IMyTextPanel)b);
                    ((IMyTextPanel)b).ContentType = ContentType.TEXT_AND_IMAGE;
                }
                else if (b is IMySoundBlock)
                    alarms.Add((IMySoundBlock)b);
            }

            supplies = Named(blocks, settings.Supplies);
            restock.Clear();
            IMyInventory inventory = supplies?.GetInventory();
            if (inventory == null)
                return;
            if (settings.Restock.Length == 0)
            {
                // the first time: what the level's maker put in the container is what it is kept at
                var counts = new Dictionary<MyDefinitionId, int>();
                var items = new List<VRage.Game.ModAPI.Ingame.MyInventoryItem>();
                inventory.GetItems(items);
                foreach (VRage.Game.ModAPI.Ingame.MyInventoryItem item in items)
                {
                    MyDefinitionId id = item.Type;
                    int count;
                    counts.TryGetValue(id, out count);
                    counts[id] = count + item.Amount.ToIntSafe();
                }
                var lines = new StringBuilder();
                foreach (KeyValuePair<MyDefinitionId, int> c in counts)
                    lines.Append(c.Key.TypeId.ToString().Replace("MyObjectBuilder_", "")).Append('/').Append(c.Key.SubtypeName)
                        .Append('=').Append(c.Value).Append('\n');
                settings.Restock = lines.ToString().TrimEnd('\n');
            }
            foreach (string line in settings.Restock.Split('\n'))
            {
                string[] parts = line.Trim().Split('=');
                MyDefinitionId id;
                int count;
                if (parts.Length == 2 && int.TryParse(parts[1], out count)
                    && MyDefinitionId.TryParse("MyObjectBuilder_" + parts[0], out id))
                    restock.Add(new MyTuple<MyDefinitionId, int>(id, count));
            }
        }

        /// <summary>Metres from the ground up to this point, along the planet's up.</summary>
        private double Height(Vector3D at)
        {
            Vector3D ground = planet.GetClosestSurfacePointGlobal(ref at);
            Vector3D centre = planet.PositionComp.GetPosition();
            return (at - centre).Length() - (ground - centre).Length();
        }

        /// <summary>How high the lowest corner of the swarm block's grids is above the ground; below it counts as on it.</summary>
        private double LowestHeight()
        {
            double lowest = double.MaxValue;
            var corners = new Vector3D[8];
            foreach (IMyEntity e in grids)
            {
                var grid = (IMyCubeGrid)e;
                var box = new BoundingBoxD(((Vector3D)grid.Min - 0.5) * grid.GridSize, ((Vector3D)grid.Max + 0.5) * grid.GridSize);
                new MyOrientedBoundingBoxD(box, grid.WorldMatrix).GetCorners(corners, 0);
                foreach (Vector3D corner in corners)
                    lowest = Math.Min(lowest, Height(corner));
            }
            return lowest;
        }

        private void Advance()
        {
            if (phase == Phase.Ready || phase == Phase.Lost)
                return;
            if (!TargetWorking())
            {
                heldFor = tick - runStarted;
                int waves = WavesHeld();
                Log.Info($"Bug Swarm: {target?.CustomName} down in wave {wave}, after {Clock(heldFor)}");
                Stop();
                phase = Phase.Lost;
                if (waves > best)
                {
                    best = waves;
                    MyAPIGateway.Utilities.SetVariable(BestKey + block.EntityId, best);
                }
                TellAll($"{target?.CustomName ?? "The block"} is down. You held {waves} wave{(waves == 1 ? "" : "s")} for {Clock(heldFor)}." +
                    PuttingBack($" The base is put back in {PutBackAfter / Second} s."), "Red", 10000);
                PutBackIn(PutBackAfter);
                endNote = $"{target?.CustomName ?? "The block"} fell while you were down." + PuttingBack(" The base is put back as it was.");
                return;
            }
            if (!anchored && Height(block.GetPosition()) > startHeight + TakeOff)
            {
                Log.Info($"Bug Swarm: the ship took off in wave {wave}");
                endNote = "The ship took off while you were down: the swarm sank back into the ground.";
                StopWatching();
                Stop();
                phase = Phase.Ready;
                TellAll("You took off: the swarm sinks back into the ground.", "White", 6000);
                return;
            }
            if (phase == Phase.Break && tick >= phaseEnds)
                StartWave();
            else if (phase == Phase.Wave && spawned >= toSpawn && (alive == 0 || stragglerTimeout > 0 && tick - lastSpawn > stragglerTimeout))
            {
                if (lastWave > 0 && wave >= lastWave)
                {
                    heldFor = tick - runStarted;
                    int waves = WavesHeld();
                    Log.Info($"Bug Swarm: won at wave {wave}, after {Clock(heldFor)}");
                    Stop();
                    phase = Phase.Lost;
                    if (waves > best)
                    {
                        best = waves;
                        MyAPIGateway.Utilities.SetVariable(BestKey + block.EntityId, best);
                    }
                    TellAll($"You held {target.CustomName} through the last wave, {wave}, in {Clock(heldFor)}." +
                        PuttingBack($" The base is put back in {PutBackAfter / Second} s."), "Green", 10000);
                    PutBackIn(PutBackAfter);
                    endNote = "The last wave was beaten while you were down." + PuttingBack(" The base is put back as it was.");
                    return;
                }
                phase = Phase.Break;
                phaseEnds = tick + breakLength;
                int added = TopUp();
                Log.Info($"Bug Swarm: wave {wave} held; break, {added} items put back");
                TellAll($"Wave {wave} beaten." + (supplies != null ? " Supplies are in." : "") +
                    $" Rebuild: the next wave comes in {breakLength / Second} s.", "Green", 6000);
            }
        }

        /// <summary>Ends the run from the swarm block's control panel: the bugs still alive die off, and nothing is counted.</summary>
        private void End(IMyTerminalBlock panel, long by)
        {
            if (block != panel || Active == null)
                return;
            Log.Info($"Bug Swarm: ended by hand in wave {wave}");
            Stop();
            phase = Phase.Lost;
            TellAll("The swarm sinks back into the ground" + PuttingBack(", and the base is put back") + ".", "White", 4000);
            PutBackIn(0);
            endNote = "The run was ended while you were down." + PuttingBack(" The base is put back as it was.");
        }

        private string PuttingBack(string text) => putBack ? text : "";

        private void PutBackIn(int ticks)
        {
            if (!putBack)
            {
                levelsBefore.Clear();
                StopWatching();
                block = null;
                phase = Phase.Ready;
                return;
            }
            putBackAt = tick + ticks;
            Log.Info($"Bug Swarm: putting the base back in {ticks / Second} s");
        }

        /// <summary>A player who dies in a run is brought back at the base a moment later, with no respawn screen.</summary>
        private void Died(long identity)
        {
            if (bringBack && (Active == this || putBackAt > 0 || rebuildAt > 0) && Human(identity))
                backAt[identity] = tick + BackAfter;
        }

        // players found by identity, reused
        private readonly List<IMyPlayer> lookedUp = new List<IMyPlayer>();

        // the game reports a bot's death, a bug's too, as a player's
        private bool Human(long identity)
        {
            lookedUp.Clear();
            MyAPIGateway.Players.GetPlayers(lookedUp, p => p.IdentityId == identity);
            return lookedUp.Count > 0 && !lookedUp[0].IsBot;
        }

        /// <summary>
        /// Brings the players who lookedUp back at the base: at a medical room, survival kit or cryo chamber of it if it had one
        /// working when the run started, otherwise in front of the swarm block, with a moment in which the bugs cannot hurt
        /// them. One who lookedUp as the run ended comes back once the base is put back, and is told how it ended.
        /// </summary>
        private void BringBack()
        {
            if (backAt.Count == 0 || (Active != this && (putBackAt > 0 || rebuildAt > 0)))
                return;
            List<long> due = null;
            foreach (KeyValuePair<long, int> player in backAt)
                if (Active != this || tick >= player.Value)
                    (due ?? (due = new List<long>())).Add(player.Key);
            if (due == null)
                return;
            MatrixD at = Active == this ? SafestSpot() : comeBack;
            Log.Info($"Bug Swarm: {due.Count} player{(due.Count == 1 ? "" : "s")} back at the base, the nearest bug " +
                (bugs.Count > 0 ? $"{Math.Sqrt(NearestBug(at.Translation)):0} m away" : "none"));
            foreach (long identity in due)
            {
                backAt.Remove(identity);
                // Until the game's death countdown on the dead body runs out, the player still controls that body, and
                // the game hands a new body control only when the player controls nothing: brought back before then,
                // they would stand at the base unable to move. So the countdown's end is played out now, as the game
                // plays it: the dead body lets go, and the game gives the player the body they are brought back in.
                Sandbox.Game.World.MyPlayer player = PlayerOf(identity);
                bool deadBodyHeld = player?.Controller?.ControlledEntity is Sandbox.Game.Entities.Character.MyCharacter held
                    && held.IsDead;
                MyVisualScriptLogicProvider.SpawnPlayer(at, Vector3D.Zero, identity);
                if (deadBodyHeld && player.Character != null && !player.Character.IsDead)
                    Sandbox.Game.Multiplayer.Sync.Players.KillPlayer(player);
                safeUntil[identity] = tick + SafeFor;
                // in the run, full; after it, the levels they had when it started
                Levels levels;
                if (Active == this || !levelsBefore.TryGetValue(identity, out levels))
                    levels = new Levels { Health = -1, Food = -1, Energy = 1, Oxygen = 1, Hydrogen = 1 };
                else
                    levelsBefore.Remove(identity);
                levelsDue[identity] = levels;
                Tell(identity, Active == this ? $"You are back at the base. Keep {target.CustomName} working." : endNote, "White", 6000);
            }
        }

        private static Sandbox.Game.World.MyPlayer PlayerOf(long identity)
        {
            Sandbox.Game.World.MyPlayer.PlayerId id;
            return Sandbox.Game.World.MySession.Static.Players.TryGetPlayerId(identity, out id)
                ? Sandbox.Game.World.MySession.Static.Players.GetPlayerById(id) : null;
        }

        /// <summary>Each player's health, food and suit levels as the run starts, to give back with the base.</summary>
        private void KeepLevels()
        {
            levelsBefore.Clear();
            levelsDue.Clear();
            lookedUp.Clear();
            MyAPIGateway.Players.GetPlayers(lookedUp, p => !p.IsBot && p.Character != null && !p.Character.IsDead);
            foreach (IMyPlayer player in lookedUp)
                levelsBefore[player.IdentityId] = new Levels
                {
                    Health = MyVisualScriptLogicProvider.GetPlayersHealth(player.IdentityId),
                    // a world with food off has no food stat
                    Food = (player.Character as Sandbox.Game.Entities.Character.MyCharacter)?.StatComp?.Food?.Value ?? -1,
                    Energy = MyVisualScriptLogicProvider.GetPlayersEnergyLevel(player.IdentityId),
                    Oxygen = MyVisualScriptLogicProvider.GetPlayersOxygenLevel(player.IdentityId),
                    Hydrogen = MyVisualScriptLogicProvider.GetPlayersHydrogenLevel(player.IdentityId),
                };
        }

        /// <summary>Gives the levels due to each player who has a living body.</summary>
        private void GiveLevels()
        {
            if (levelsDue.Count == 0)
                return;
            List<long> given = null;
            foreach (KeyValuePair<long, Levels> player in levelsDue)
            {
                lookedUp.Clear();
                MyAPIGateway.Players.GetPlayers(lookedUp, p => p.IdentityId == player.Key);
                if (lookedUp.Count == 0)
                {
                    (given ?? (given = new List<long>())).Add(player.Key);
                    continue;
                }
                var character = lookedUp[0].Character as Sandbox.Game.Entities.Character.MyCharacter;
                if (character == null || character.IsDead || character.StatComp?.Health == null)
                    continue;
                Levels levels = player.Value;
                long id = player.Key;
                MyVisualScriptLogicProvider.SetPlayersHealth(id, levels.Health < 0 ? character.StatComp.Health.MaxValue : levels.Health);
                MyVisualScriptLogicProvider.SetPlayersEnergyLevel(id, levels.Energy);
                MyVisualScriptLogicProvider.SetPlayersOxygenLevel(id, levels.Oxygen);
                MyVisualScriptLogicProvider.SetPlayersHydrogenLevel(id, levels.Hydrogen);
                // a new body starts hungry (the food stat's ReducedOnSpawnValue)
                var food = character.StatComp.Food;
                if (food != null)
                    food.Value = levels.Food < 0 ? food.MaxValue : levels.Food;
                Log.Info($"Bug Swarm: a player's levels set to health {MyVisualScriptLogicProvider.GetPlayersHealth(id):0}, food {food?.Value ?? 0:0}, energy " +
                    $"{levels.Energy:P0}, oxygen {levels.Oxygen:P0}, hydrogen {levels.Hydrogen:P0}");
                (given ?? (given = new List<long>())).Add(id);
            }
            if (given != null)
                foreach (long id in given)
                    levelsDue.Remove(id);
        }

        // cells of the base looked at for a place to stand, at most
        private const int MostCells = 30000;

        /// <summary>
        /// The place on the base's floors farthest from the nearest bug: a free cell of a large grid with a block under it
        /// and room above, so a player who died is not brought back among the bugs that killed them.
        /// </summary>
        private MatrixD SafestSpot()
        {
            CountBugs();
            if (bugs.Count == 0)
                return comeBack;
            Vector3D up = Up();
            MatrixD best = comeBack;
            double bestRoom = NearestBug(comeBack.Translation);
            int looked = 0;
            foreach (IMyEntity e in grids)
            {
                var grid = (IMyCubeGrid)e;
                if (grid.GridSizeEnum != MyCubeSize.Large)
                    continue;
                Vector3I u = Base6Directions.GetIntVector(grid.WorldMatrix.GetClosestDirection(up));
                for (int x = grid.Min.X; x <= grid.Max.X; x++)
                    for (int y = grid.Min.Y; y <= grid.Max.Y; y++)
                        for (int z = grid.Min.Z; z <= grid.Max.Z; z++)
                        {
                            if (++looked > MostCells)
                                return best;
                            var cell = new Vector3I(x, y, z);
                            if (grid.CubeExists(cell) || !grid.CubeExists(cell - u) || grid.CubeExists(cell + u))
                                continue;
                            Vector3D feet = grid.GridIntegerToWorld(cell) - up * (grid.GridSize * 0.5 - 0.1);
                            double room = NearestBug(feet);
                            if (room > bestRoom)
                            {
                                bestRoom = room;
                                best = MatrixD.CreateWorld(feet, Vector3D.Normalize(Vector3D.Reject(comeBack.Forward, up)), up);
                            }
                        }
            }
            return best;
        }

        private double NearestBug(Vector3D at)
        {
            double nearest = double.MaxValue;
            foreach (IMyCharacter bug in bugs)
                nearest = Math.Min(nearest, Vector3D.DistanceSquared(bug.GetPosition(), at));
            return nearest;
        }

        private MatrixD RespawnPoint(Vector3D up)
        {
            IMyTerminalBlock from = block;
            foreach (IMyEntity e in grids)
            {
                var spawns = new List<IMySlimBlock>();
                ((IMyCubeGrid)e).GetBlocks(spawns, b => b.FatBlock is IMyMedicalRoom || b.FatBlock?.BlockDefinition.TypeIdString == "MyObjectBuilder_SurvivalKit" || b.FatBlock is IMyCryoChamber);
                foreach (IMySlimBlock spawn in spawns)
                    if (((IMyFunctionalBlock)spawn.FatBlock).IsWorking)
                    {
                        from = (IMyTerminalBlock)spawn.FatBlock;
                        break;
                    }
                if (from != block)
                    break;
            }
            MatrixD frame = from.WorldMatrix;
            Vector3D forward = Vector3D.Reject(frame.Forward, up);
            if (forward.LengthSquared() < 0.01)
                forward = Vector3D.CalculatePerpendicularVector(up);
            forward = Vector3D.Normalize(forward);
            // in front of it, on its floor, facing away from it
            Vector3D position = frame.Translation + forward * (from.CubeGrid.GridSize * 0.5 + 1) - up * (from.CubeGrid.GridSize * 0.5 - 0.1);
            return MatrixD.CreateWorld(position, forward, up);
        }

        /// <summary>Keeps everything the bugs could reach as it is now, to put back when the run is over.</summary>
        private void KeepBase(Vector3D at)
        {
            before.Clear();
            gridsBefore.Clear();
            pieces.Clear();
            itemsBefore.Clear();
            StopWatching();
            baseArea = new BoundingSphereD(at, reach + KeptBeyond);
            var group = new List<IMyCubeGrid>();
            foreach (IMyEntity e in MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref baseArea))
            {
                if (e is Sandbox.Game.Entities.MyFloatingObject)
                    itemsBefore.Add(e.EntityId);
                var near = e as IMyCubeGrid;
                if (near == null || gridsBefore.Contains(near.EntityId))
                    continue;
                group.Clear();
                MyAPIGateway.GridGroups.GetGroup(near, GridLinkTypeEnum.Mechanical, group);
                foreach (IMyCubeGrid grid in group)
                    if (gridsBefore.Add(grid.EntityId))
                    {
                        var copy = (MyObjectBuilder_CubeGrid)grid.GetObjectBuilder(true);
                        // a player seated in it at the start is not put back with it
                        foreach (MyObjectBuilder_CubeBlock b in copy.CubeBlocks)
                            b.GetType().GetField("Pilot")?.SetValue(b, null);
                        before.Add(copy);
                        Watch(grid);
                    }
            }
            Log.Info($"Bug Swarm: keeping {before.Count} grids within {baseArea.Radius:0} m to put back");
            comeBack = RespawnPoint(Up());
            KeepLevels();
            loot.Clear();
            backAt.Clear();
            safeUntil.Clear();
            putBackAt = rebuildAt = 0;
        }

        // the game tells of each piece broken off a grid, which is followed in turn
        private void Watch(IMyCubeGrid grid)
        {
            grid.OnGridSplit += Split;
            watched.Add(grid);
        }

        private void Split(IMyCubeGrid from, IMyCubeGrid piece)
        {
            pieces.Add(piece.EntityId);
            Watch(piece);
        }

        private void StopWatching()
        {
            foreach (IMyCubeGrid grid in watched)
                grid.OnGridSplit -= Split;
            watched.Clear();
        }

        /// <summary>
        /// Puts back everything kept at the start: what is left of it goes, with the pieces broken off it wherever they
        /// went and the items dropped near it, and each grid is built again as it was, with its own id, so the swarm block
        /// keeps its best wave. Players seated in them are let out first. In two steps, as the game removes grids at the
        /// end of a frame.
        /// </summary>
        private void PutBack()
        {
            if (putBackAt > 0 && tick >= putBackAt)
            {
                putBackAt = 0;
                StopWatching();
                var going = new HashSet<IMyEntity>();
                foreach (long id in gridsBefore)
                    AddEntity(going, id);
                foreach (long id in pieces)
                    AddEntity(going, id);
                foreach (IMyEntity e in MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref baseArea))
                    if (e is IMyCubeGrid && !gridsBefore.Contains(e.EntityId) && Broken((IMyCubeGrid)e)
                        || e is Sandbox.Game.Entities.MyFloatingObject && !itemsBefore.Contains(e.EntityId))
                        going.Add(e);
                Log.Info($"Bug Swarm: clearing away {going.Count} grids and items to put back {before.Count} grids");
                foreach (IMyEntity e in going)
                {
                    var grid = e as IMyCubeGrid;
                    if (grid == null)
                        continue;
                    var seats = new List<IMySlimBlock>();
                    grid.GetBlocks(seats, b => b.FatBlock is Sandbox.Game.Entities.MyCockpit);
                    foreach (IMySlimBlock seat in seats)
                        ((Sandbox.Game.Entities.MyCockpit)seat.FatBlock).RemovePilot();
                }
                foreach (IMyEntity e in going)
                    e.Close();
                block = null;
                phase = Phase.Ready;
                rebuildAt = tick + 10;
            }
            else if (rebuildAt > 0 && tick >= rebuildAt)
            {
                rebuildAt = 0;
                foreach (MyObjectBuilder_CubeGrid copy in before)
                    MyAPIGateway.Entities.CreateFromObjectBuilderAndAdd(copy);
                Log.Info($"Bug Swarm: the base is put back ({before.Count} grids)");
                before.Clear();
                // a player still down gets theirs when brought back
                foreach (KeyValuePair<long, Levels> player in levelsBefore)
                    if (!backAt.ContainsKey(player.Key))
                        levelsDue[player.Key] = player.Value;
                if (backAt.Count == 0)
                    levelsBefore.Clear();
            }
        }

        private static void AddEntity(HashSet<IMyEntity> into, long id)
        {
            IMyEntity e;
            if (MyAPIGateway.Entities.TryGetEntityById(id, out e) && !e.MarkedForClose)
                into.Add(e);
        }

        /// <summary>Whether a grid that came in the run near the base is wreckage: one with no seat or remote control, which a ship built in the meantime would have.</summary>
        private static bool Broken(IMyCubeGrid grid)
        {
            var controls = new List<IMySlimBlock>();
            grid.GetBlocks(controls, b => b.FatBlock is IMyShipController);
            return controls.Count == 0;
        }

        /// <summary>The run is over: the bugs left die off, so the grid can be welded up for the next.</summary>
        private void Stop()
        {
            if (Active == this)
            {
                Active = null;
                SayRunOn(false);
            }
            CountBugs();
            foreach (IMyCharacter bug in bugs)
                bug.Kill();
            ShowStatus();
        }

        private void StartWave()
        {
            wave++;
            toSpawn = firstWaveBugs + bugsPerWave * (wave - 1);
            spawned = 0;
            nextSpawn = tick + Second;
            phase = Phase.Wave;
            bearings.Clear();
            Vector3D up = Up();
            // the sides spaced evenly round the grid from a random start: one more every other wave, so later waves come at
            // every wall at once
            Vector3D any = Vector3D.CalculatePerpendicularVector(up);
            double start = random.NextDouble() * MathHelper.TwoPi;
            int count = Math.Min(1 + (wave - 1) / 2, MostSides);
            for (int i = 0; i < count; i++)
                bearings.Add(Vector3D.Transform(any, MatrixD.CreateFromAxisAngle(up, start + i * MathHelper.TwoPi / count)));
            foreach (IMySoundBlock alarm in alarms)
                if (!alarm.Closed)
                    alarm.Play();
            Log.Info($"Bug Swarm: wave {wave}, {toSpawn} bugs, damage x{WaveScale():0.00}");
            TellAll($"Wave {wave}: {toSpawn} bugs, from {bearings.Count} side{(bearings.Count == 1 ? "" : "s")}.", "Red", 5000);
        }

        private void SpawnNext()
        {
            nextSpawn = tick + SpawnInterval;
            // With no cap of its own the swarm fields as many at once as the world's bot limit lets the game spawn.
            if (spawned >= toSpawn || alive >= BotCap)
                return;
            Vector3D up = Up();
            Vector3D bearing = bearings[spawned % bearings.Count];
            // anywhere in its side's share of the circle, short of the next side's
            double share = MathHelper.TwoPi / bearings.Count;
            bearing = Vector3D.Transform(bearing, MatrixD.CreateFromAxisAngle(up, (random.NextDouble() - 0.5) * Math.Min(share * 0.8, 0.8)));
            double distance = reach + SpawnNearest + random.NextDouble() * (SpawnFarthest - SpawnNearest);
            Vector3D at = block.GetPosition() + bearing * distance;
            at = planet.GetClosestSurfacePointGlobal(ref at) + up * 0.5;

            string kind = Bug;
            if (wave >= brutesFrom && spawned % 4 == 3)
                kind = Brute;
            else if (wave >= stingersFrom && spawned % 3 == 2)
                kind = Stinger;
            MyVisualScriptLogicProvider.SpawnBot(kind, at);
            spawned++;
            lastSpawn = tick;
        }

        private void CountBugs()
        {
            bugs.Clear();
            bugIds.Clear();
            if (block == null)
                return;
            Vector3D middle = block.GetPosition();
            double within = reach + Wandered;
            Humans();
            found.Clear();
            MyAPIGateway.Entities.GetEntities(found, e => e is IMyCharacter);
            foreach (IMyEntity e in found)
            {
                // A spawned bug's identity is not marked as an NPC's, so IsBot says false for it: a bug is any character no person plays.
                var c = (IMyCharacter)e;
                if (!c.IsDead && !humans.Contains(c) && Vector3D.DistanceSquared(c.GetPosition(), middle) < within * within)
                {
                    bugs.Add(c);
                    bugIds[c.EntityId] = c;
                }
            }
            alive = bugs.Count;
            // Rotors and pistons can be built or broken off during a run, and a block whose neighbours are chewed away splits
            // off onto a grid of its own: the goal's grid is the bugs' wherever it ends up.
            grids.Clear();
            AddGroup(block.CubeGrid);
            if (TargetWorking())
                AddGroup(target.CubeGrid);
        }

        private void AddGroup(IMyCubeGrid grid)
        {
            gridList.Clear();
            MyAPIGateway.GridGroups.GetGroup(grid, GridLinkTypeEnum.Mechanical, gridList);
            foreach (IMyCubeGrid g in gridList)
                grids.Add(g);
        }

        /// <summary>
        /// A bug that can get no further bites the swarm block's grids: of the blocks within its reach, the one nearest its
        /// goal, so a wall it cannot get over (<see cref="SwarmAi"/>), or a floor it is caught under, is chewed through. One
        /// with its goal in reach bites that, above or below it as well as beside it. A bug bites only a block it can see:
        /// never one through another. With a player close by a stinger leaves the bite to its own attack on the player.
        /// </summary>
        private void Bite()
        {
            if (bugs.Count == 0 || Active != this)
                return;
            Vector3D goal = target.GetPosition();
            foreach (IMyCharacter bug in bugs)
            {
                int when;
                if (nextBite.TryGetValue(bug.EntityId, out when) && when > tick)
                    continue;
                Vector3D middle = bug.WorldAABB.Center;
                string kind = Kind(bug);
                if (kind == Stinger && PlayerWithin(middle, PlayerNear))
                    continue;
                // the goal itself, from below, above or beside it, as soon as it is in reach
                bool atGoal = target.WorldAABB.Distance(middle) < BiteRadius && InSight(middle, target.SlimBlock);
                if (!atGoal && !Stuck(bug, middle))
                    continue;
                var sphere = new BoundingSphereD(middle, BiteRadius);
                IMySlimBlock bitten = atGoal ? target.SlimBlock : null;
                double nearest = double.MaxValue;
                foreach (IMyEntity e in atGoal ? NoEntities : MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref sphere))
                {
                    if (!grids.Contains(e))
                        continue;
                    var grid = (IMyCubeGrid)e;
                    foreach (IMySlimBlock b in grid.GetBlocksInsideSphere(ref sphere))
                    {
                        double d = Vector3D.DistanceSquared(grid.GridIntegerToWorld(b.Position), goal);
                        if (d < nearest && InSight(middle, b))
                        {
                            nearest = d;
                            bitten = b;
                        }
                    }
                }
                if (bitten == null)
                    continue;
                bool brute = kind == Brute;
                bitten.DoDamage(brute ? biteDamage * 3 : biteDamage, SpiderDamage, true, null, bug.EntityId);
                bug.TriggerCharacterAnimationEvent("attack", true);
                MyVisualScriptLogicProvider.PlaySingleSoundAtPosition(brute ? "ArcBotSpiderAttackBite" : "ArcBotSpiderAttackClaw", middle);
                nextBite[bug.EntityId] = tick + BiteInterval;
                bites++;
            }
            if (nextBite.Count > 200)
                nextBite.Clear();
            if (lastMoved.Count > 200)
                lastMoved.Clear();
        }

        private readonly List<IHitInfo> sight = new List<IHitInfo>();

        /// <summary>Whether the nearest side of the block is in plain sight from the bug's middle, with no other block between.</summary>
        private bool InSight(Vector3D from, IMySlimBlock block)
        {
            if (!inReach)
                return true;
            BoundingBoxD box;
            block.GetWorldBoundingBox(out box, true);
            Vector3D to = Vector3D.Clamp(from, box.Min, box.Max);
            Vector3D along = to - from;
            double length = along.Length();
            if (length < 0.01)
                return true;
            along /= length;
            sight.Clear();
            MyAPIGateway.Physics.CastRay(from, to + along * 0.25, sight);
            IHitInfo first = null;
            foreach (IHitInfo hit in sight)
                if (grids.Contains(hit.HitEntity) && (first == null || hit.Fraction < first.Fraction))
                    first = hit;
            if (first == null)
                return true;
            var grid = (IMyCubeGrid)first.HitEntity;
            return grid.GetCubeBlock(grid.WorldToGridInteger(first.Position + along * 0.05)) == block;
        }

        /// <summary>Whether the bug has got less than <see cref="StuckWithin"/> from where it was <see cref="StuckFor"/> ago.</summary>
        private bool Stuck(IMyCharacter bug, Vector3D at)
        {
            MyTuple<Vector3D, int> moved;
            if (!lastMoved.TryGetValue(bug.EntityId, out moved) || Vector3D.DistanceSquared(moved.Item1, at) > StuckWithin * StuckWithin)
            {
                lastMoved[bug.EntityId] = new MyTuple<Vector3D, int>(at, tick);
                return false;
            }
            return tick - moved.Item2 > StuckFor;
        }

        private int TargetPercent() => target == null || target.Closed ? 0
                : (int)Math.Round(100 * target.SlimBlock.Integrity / target.SlimBlock.MaxIntegrity);

        /// <summary>The wave's state in the plugin's log, every ten seconds of a wave: what a test or a server owner reads.</summary>
        private void LogWave()
        {
            double nearest = double.MaxValue;
            foreach (IMyCharacter bug in bugs)
                nearest = Math.Min(nearest, Vector3D.Distance(bug.GetPosition(), target.GetPosition()));
            Log.Info($"Bug Swarm: wave {wave}, {spawned}/{toSpawn} spawned, {alive} alive, nearest " +
                (bugs.Count > 0 ? $"{nearest:0} m" : "none") + $", {bugs.FindAll(SwarmAi.OnWall).Count} on walls, {bites} bites so far, {target.CustomName} {TargetPercent()}%");
        }

        /// <summary>The characters people play, this frame (the bots are players too, and are left out).</summary>
        private void Humans()
        {
            humans.Clear();
            players.Clear();
            MyAPIGateway.Players.GetPlayers(players, p => !p.IsBot && p.Character != null);
            foreach (IMyPlayer p in players)
                humans.Add(p.Character);
        }

        private bool PlayerWithin(Vector3D at, double distance)
        {
            foreach (IMyCharacter c in humans)
                if (!c.IsDead && Vector3D.DistanceSquared(c.WorldAABB.Center, at) < distance * distance)
                    return true;
            return false;
        }

        /// <summary>
        /// A small cargo container on the swarm block's grid, in the free floor cell nearest it that does not stand in front
        /// of it, for a level whose maker chose no supplies container; null when there is no room.
        /// </summary>
        private IMyTerminalBlock PlaceSupplies()
        {
            IMyCubeGrid grid = block.CubeGrid;
            Base6Directions.Direction upward = grid.WorldMatrix.GetClosestDirection(Up());
            Vector3I up = Base6Directions.GetIntVector(upward);
            Vector3I front = block.Position + Base6Directions.GetIntVector(block.Orientation.Forward);
            Vector3I best = default(Vector3I);
            int bestDistance = int.MaxValue;
            for (int x = -4; x <= 4; x++)
                for (int y = -4; y <= 4; y++)
                    for (int z = -4; z <= 4; z++)
                    {
                        Vector3I cell = block.Position + new Vector3I(x, y, z);
                        int distance = (cell - block.Position).RectangularLength();
                        if (distance >= bestDistance || cell == front || grid.CubeExists(cell) || !grid.CubeExists(cell - up)
                            || !grid.CanAddCube(cell) || SomeoneIn(grid, cell))
                            continue;
                        best = cell;
                        bestDistance = distance;
                    }
            if (bestDistance == int.MaxValue)
            {
                Log.Info("Bug Swarm: no supplies container chosen, and no free floor beside the swarm block for one");
                return null;
            }
            var orientation = new MyBlockOrientation(Base6Directions.GetPerpendicular(upward), upward);
            var box = new MyObjectBuilder_CargoContainer
            {
                SubtypeName = grid.GridSizeEnum == MyCubeSize.Large ? "LargeBlockSmallContainer" : "SmallBlockSmallContainer",
                Min = best,
                BlockOrientation = orientation,
                Owner = block.OwnerId,
                ShareMode = MyOwnershipShareModeEnum.All,
                CustomName = "Swarm supplies",
                BuildPercent = 1f,
                IntegrityPercent = 1f,
            };
            // Built as the game builds a projected block, which every player's game is told of (IMyCubeGrid.AddBlock adds it
            // on the host only): placed whole by the host, then shared with everyone (the build gives it to the owner's faction).
            var cubes = (MyCubeGrid)grid;
            Quaternion turn;
            orientation.GetQuaternion(out turn);
            var location = new MyCubeGrid.MyBlockLocation(box.GetId(), best, best, best, turn, MyEntityIdentifier.AllocateId(), block.OwnerId);
            cubes.BuildBlockRequestInternal(new MyCubeGrid.MyBlockVisuals(block.SlimBlock.ColorMaskHSV.PackHSVToUint(), MyStringHash.NullOrEmpty),
                location, box, 0, true, block.OwnerId, Sandbox.Game.Multiplayer.Sync.MyId);
            var placed = grid.GetCubeBlock(best)?.FatBlock as IMyTerminalBlock;
            if (placed != null)
                cubes.ChangeOwnerRequest(cubes, (MyCubeBlock)placed, block.OwnerId, MyOwnershipShareModeEnum.All);
            Log.Info($"Bug Swarm: supplies container placed {(placed != null ? $"beside {block.CustomName}" : "failed")}");
            return placed;
        }

        private static bool SomeoneIn(IMyCubeGrid grid, Vector3I cell)
        {
            var space = new BoundingSphereD(grid.GridIntegerToWorld(cell), grid.GridSize);
            foreach (IMyEntity e in MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref space))
                if (e is IMyCharacter)
                    return true;
            return false;
        }

        /// <summary>While a run is on the supplies container never runs out of pistols and pistol magazines.</summary>
        private void KeepPistols()
        {
            IMyInventory inventory = supplies != null && !supplies.Closed ? supplies.GetInventory() : null;
            if (inventory == null)
                return;
            foreach (MyTuple<MyDefinitionId, int> item in Unlimited)
            {
                MyFixedPoint missing = item.Item2 - inventory.GetItemAmount(item.Item1);
                if (missing > 0 && inventory.CanItemsBeAdded(missing, item.Item1))
                    inventory.AddItems(missing, (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(item.Item1));
            }
        }

        /// <summary>Fills the supplies container back up to its restock counts; how many items it put in.</summary>
        private int TopUp()
        {
            int added = 0;
            IMyInventory inventory = supplies != null && !supplies.Closed ? supplies.GetInventory() : null;
            if (inventory == null)
                return added;
            foreach (MyTuple<MyDefinitionId, int> item in restock)
            {
                MyFixedPoint missing = item.Item2 - inventory.GetItemAmount(item.Item1);
                if (missing <= 0 || !inventory.CanItemsBeAdded(missing, item.Item1))
                    continue;
                var content = MyObjectBuilderSerializer.CreateNewObject(item.Item1) as MyObjectBuilder_PhysicalObject;
                if (content == null)
                    continue;
                inventory.AddItems(missing, content);
                added += missing.ToIntSafe();
            }
            return added;
        }

        private int WavesHeld()
        {
            return Math.Max(0, wave - startWave);
        }

        /// <summary>How many bugs the swarm fields at once: its own setting, past the world's bot limit if it is higher.</summary>
        internal int BotCap => mostAlive > 0 ? mostAlive : MyAPIGateway.Session.SessionSettings.TotalBotLimit;

        /// <summary>The bugs alive in the run, as last counted.</summary>
        internal IReadOnlyList<IMyCharacter> Bugs => bugs;

        /// <summary>The block the bugs are after, while it works; null otherwise.</summary>
        internal IMyTerminalBlock Target => TargetWorking() ? target : null;

        /// <summary>The swarm block's grid and the grids on its rotors and pistons: what the bugs chew and stingers climb.</summary>
        internal HashSet<IMyEntity> Grids => grids;

        /// <summary>How near a player has to be for a bug or a brute to leave its goal for them, in metres.</summary>
        internal int PlayerReach { get; private set; }


        /// <summary>The living characters people play, near or far.</summary>
        internal IEnumerable<IMyCharacter> People => humans;

        /// <summary>Whether this character is one of the swarm's bugs (any living character no person plays, near the grid).</summary>
        internal bool IsBug(long entityId) => bugIds.ContainsKey(entityId);

        private bool TargetWorking()
        {
            return target != null && !target.Closed && target.IsFunctional;
        }

        private Vector3D Up()
        {
            return Vector3D.Normalize(block.GetPosition() - planet.PositionComp.GetPosition());
        }

        private void ShowStatus()
        {
            if (screens.Count == 0)
                return;
            text.Clear();
            text.Append("BUG SWARM\n\n");
            string goal = target?.CustomName ?? "";
            switch (phase)
            {
                case Phase.Ready:
                    text.Append($"Press {block.CustomName}\nto wake the swarm.\n");
                    text.Append($"Keep {goal} working.\n\n");
                    break;
                case Phase.Break:
                    text.Append(wave < startWave ? "The swarm is waking\n" : $"Wave {wave} beaten\n");
                    text.Append($"Next wave in {Clock(Math.Max(0, phaseEnds - tick))}\n\n");
                    break;
                case Phase.Wave:
                    text.Append($"Wave {wave}\n");
                    text.Append($"Bugs: {alive + toSpawn - spawned}\n\n");
                    break;
                case Phase.Lost:
                    text.Append($"{goal.ToUpperInvariant()} IS DOWN\n");
                    text.Append($"You held {WavesHeld()} waves for {Clock(heldFor)}\n");
                    text.Append($"Weld it up and press\n{block.CustomName} to go again.\n");
                    break;
            }
            text.Append($"{goal}: {TargetPercent()} %\n");
            if (phase == Phase.Break || phase == Phase.Wave)
                text.Append($"Time: {Clock(tick - runStarted)}\n");
            text.Append($"Best: {best} wave{(best == 1 ? "" : "s")}");
            string shown = text.ToString();
            foreach (IMyTextPanel screen in screens)
                if (!screen.Closed)
                    screen.WriteText(shown);
        }

        private static string Clock(int ticks)
        {
            int seconds = ticks / Second;
            return $"{seconds / 60}:{seconds % 60:00}";
        }
    }
}
