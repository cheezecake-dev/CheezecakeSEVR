using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Sandbox.Game;
using VRage.Game.ObjectBuilders.Campaign;
using VRage.Game.ObjectBuilders.VisualScripting;
using VRage.ObjectBuilders.Private;

namespace SpaceEngineersVR.Scenarios
{
    /// <summary>
    /// The scenarios that come with the plugin, listed in New Game > Story Scenarios beside the game's own.
    /// <para>
    /// They are the data file's <c>scenarios</c> folder asset: one folder per scenario, holding the scenario's <c>.scf</c> and
    /// its world (<c>tools/build-scenario.ps1</c> writes one). The game finds its scenarios once, in
    /// <c>MyCampaignManager.Init</c>, by scanning its own Content folder, so this registers the plugin's the same way the
    /// game registers its own, right after that scan: the <c>.scf</c> is read, the paths in it (relative, so the folder can
    /// be anywhere) are made whole against the folder Pulsar delivered, and the game's own <c>LoadCampaignData</c> takes it.
    /// From there the game lists it, starts it, saves it with its campaign marker and loads that save again.
    /// </para>
    /// <para>
    /// A scenario is registered as the game's own (<c>IsVanilla</c>): the other route, a local mod's, is wiped from the
    /// manager each time it refreshes its mods, and a save of one would not find its scenario again. The cost is that a
    /// scenario's save cannot be loaded without the plugin, as a Keen scenario's cannot be without the game's Content.
    /// </para>
    /// <para>
    /// Registering fails cleanly: a folder that is missing, unreadable or not a scenario the game accepts is logged and left
    /// out, and the game starts and plays as it does without it.
    /// </para>
    /// </summary>
    internal static class ScenarioRegistry
    {
        /// <summary>The folder asset in the data files, holding one folder per scenario.</summary>
        private const string AssetName = "scenarios";

        /// <summary>
        /// The scenarios this version lists, by their folder in the asset; a folder not named here is left out. Empty while every
        /// scenario is shelved, and then the game is not patched and nothing is looked for.
        /// </summary>
        private static readonly string[] Listed = BugSwarm.BugSwarmSession.Shelved ? new string[0] : new[] { BugSwarm.SwarmScenario.Folder };

        private sealed class Entry
        {
            public MyObjectBuilder_Campaign Campaign;
            public string WorldFolder;
        }

        private static readonly Dictionary<string, Entry> registered = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        private static IReadOnlyDictionary<string, string> assets;
        private static bool gameScanned, attempted;

        /// <summary>
        /// From the plugin's constructor, which runs before the game's scan for scenarios: has it register the plugin's
        /// right after. A plugin loaded after the scan registers at once.
        /// </summary>
        public static void Hook()
        {
            if (Listed.Length == 0)
            {
                Log.Info("Scenarios: none come with this version");
                return;
            }
            // Through the patch registry: a game update that renames what this hooks is in the log's patch line and the main menu's
            // notice, and the game starts and plays without the plugin's scenarios (nothing is registered without the hook).
            PatchRegistry.ApplyApart(nameof(ScenarioRegistry), harmony =>
            {
                var init = AccessTools.Method(typeof(MyCampaignManager), nameof(MyCampaignManager.Init));
                if (init == null)
                    throw new MissingMethodException(nameof(MyCampaignManager), nameof(MyCampaignManager.Init));
                harmony.Patch(init, postfix: new HarmonyMethod(typeof(ScenarioRegistry), nameof(AfterGameScan)));
                // The game's own scenarios are in the manager once it has scanned.
                if (MyCampaignManager.Static.Campaigns.Any())
                {
                    gameScanned = true;
                    Log.Info("Scenarios: the game had already listed its scenarios when the plugin loaded");
                }
            }, "Scenarios: the game could not be patched, so the plugin's scenarios are not listed");
        }

        /// <summary>From Plugin.LoadAssets: where Pulsar put the plugin's assets.</summary>
        public static void Delivered(IReadOnlyDictionary<string, string> delivered)
        {
            assets = delivered;
            TryRegister();
        }

        /// <summary>From Plugin.Init: says in the log where the scenarios stand, once the game is up.</summary>
        public static void Report()
        {
            if (Listed.Length == 0 || registered.Count > 0)
                return;
            Log.Warn($"Scenarios: none listed (game scanned: {gameScanned}; assets delivered: {assets != null}; attempted: {attempted})");
        }

        // Harmony postfix. An exception here would stop the game starting, so none leaves.
        private static void AfterGameScan()
        {
            try
            {
                gameScanned = true;
                TryRegister();
            }
            catch (Exception e)
            {
                Log.Error(e, "Scenarios: registering failed");
            }
        }

        private static void TryRegister()
        {
            if (attempted || !gameScanned || assets == null)
                return;
            attempted = true;
            string root;
            if (!assets.TryGetValue(AssetName, out root) || !Directory.Exists(root))
            {
                Log.Warn($"Scenarios: Pulsar delivered no '{AssetName}' folder, so the plugin's scenarios are not listed");
                return;
            }
            Action<MyObjectBuilder_Campaign> add = AddToManager();
            if (add == null)
                return;
            string[] folders;
            try
            {
                folders = Directory.GetDirectories(root);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Log.Warn($"Scenarios: {root} could not be read: {e.Message}");
                return;
            }
            foreach (string folder in folders)
                if (Listed.Contains(Path.GetFileName(folder), StringComparer.OrdinalIgnoreCase))
                    Register(folder, add);
        }

        /// <summary>The game's own routine for taking a scenario into its list (private, called by its scan and mod refresh).</summary>
        private static Action<MyObjectBuilder_Campaign> AddToManager()
        {
            var method = AccessTools.Method(typeof(MyCampaignManager), "LoadCampaignData");
            if (method == null)
            {
                Log.Warn("Scenarios: this game version has no LoadCampaignData, so the plugin's scenarios are not listed");
                return null;
            }
            return (Action<MyObjectBuilder_Campaign>)Delegate.CreateDelegate(typeof(Action<MyObjectBuilder_Campaign>),
                MyCampaignManager.Static, method);
        }

        private static void Register(string folder, Action<MyObjectBuilder_Campaign> add)
        {
            try
            {
                string[] files = Directory.GetFiles(folder, "*.scf");
                if (files.Length == 0)
                {
                    Log.Warn($"Scenarios: {folder} holds no .scf file; left out");
                    return;
                }
                string scf = files[0];
                MyObjectBuilder_VSFiles file;
                if (!MyObjectBuilderSerializerKeen.DeserializeXML(scf, out file) || file.Campaign == null)
                {
                    Log.Warn($"Scenarios: {scf} is not a scenario file the game reads; left out");
                    return;
                }
                MyObjectBuilder_Campaign campaign = file.Campaign;
                string world = WholePaths(campaign, folder);
                if (world == null)
                {
                    Log.Warn($"Scenarios: '{campaign.Name}' has no world file in {folder}; left out");
                    return;
                }
                if (string.IsNullOrEmpty(campaign.Name) || registered.ContainsKey(campaign.Name))
                {
                    Log.Warn($"Scenarios: {scf} has no name, or one already listed; left out");
                    return;
                }
                // As the game registers its own Content scenarios (MyCampaignManager.Init).
                campaign.IsVanilla = true;
                campaign.IsLocalMod = false;
                campaign.IsDebug = false;
                campaign.ModFolderPath = null;
                campaign.CampaignPath = scf;
                add(campaign);
                if (!MyCampaignManager.Static.Campaigns.Contains(campaign))
                {
                    Log.Warn($"Scenarios: the game did not take '{campaign.Name}' (it lists a scenario of that name, or needs content this game lacks); left out");
                    return;
                }
                registered[campaign.Name] = new Entry { Campaign = campaign, WorldFolder = world };
                Log.Info($"Scenarios: '{campaign.Name}' listed in New Game, world in {world}");
            }
            catch (Exception e)
            {
                Log.Error(e, $"Scenarios: {folder} could not be registered; left out");
            }
        }

        /// <summary>
        /// Makes the world paths in the scenario whole: the <c>.scf</c> holds them relative to its own folder, where the
        /// game's hold them relative to its Content folder. Returns the folder of the first world, or null when a world file
        /// the scenario names is missing.
        /// </summary>
        private static string WholePaths(MyObjectBuilder_Campaign campaign, string folder)
        {
            var machines = new List<MyObjectBuilder_CampaignSM>();
            if (campaign.StateMachine != null)
                machines.Add(campaign.StateMachine);
            if (campaign.StateMachines != null)
                machines.AddRange(campaign.StateMachines);
            string first = null;
            foreach (MyObjectBuilder_CampaignSM machine in machines.Distinct())
            {
                if (machine.Nodes == null)
                    continue;
                foreach (MyObjectBuilder_CampaignSMNode node in machine.Nodes)
                {
                    if (string.IsNullOrEmpty(node.SaveFilePath))
                        continue;
                    if (!Path.IsPathRooted(node.SaveFilePath))
                        node.SaveFilePath = Path.GetFullPath(Path.Combine(folder, node.SaveFilePath));
                    if (!File.Exists(node.SaveFilePath))
                        return null;
                    first = first ?? Path.GetDirectoryName(node.SaveFilePath);
                }
            }
            return first;
        }

        /// <summary>Whether this game's session is the scenario of that name: started from New Game, or a save of it loaded.</summary>
        public static bool IsRunning(string name)
        {
            Entry entry;
            return registered.TryGetValue(name, out entry)
                && ReferenceEquals(MyCampaignManager.Static.ActiveCampaign, entry.Campaign)
                && MyCampaignManager.Static.IsScenarioRunning;
        }

        /// <summary>The folder of the scenario's world, as delivered; null when no scenario of that name is listed.</summary>
        public static string WorldFolder(string name)
        {
            Entry entry;
            return registered.TryGetValue(name, out entry) ? entry.WorldFolder : null;
        }
    }
}
