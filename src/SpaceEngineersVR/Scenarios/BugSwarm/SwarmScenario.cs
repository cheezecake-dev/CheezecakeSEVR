namespace SpaceEngineersVR.Scenarios.BugSwarm
{
    /// <summary>
    /// The VR Bug Swarm scenario, listed in New Game > Story Scenarios (<see cref="ScenarioRegistry"/>). Its world is built by
    /// <c>tools/build-scenario.ps1</c> from a save, and in it <see cref="BugSwarmSession"/> works as it does in any world with
    /// Bug Swarm on button panels on, except that it is on without the option, asks nothing before waking the swarm, and
    /// always puts the base back.
    /// </summary>
    internal static class SwarmScenario
    {
        /// <summary>The scenario's name in the game's campaign list: the <c>Name</c> in its .scf file.</summary>
        public const string Name = "VR Bug Swarm";

        /// <summary>Its folder in the <c>scenarios</c> asset.</summary>
        public const string Folder = "VRBugSwarm";
    }
}
