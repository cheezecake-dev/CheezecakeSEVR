using System;
using System.Linq;
using SpaceEngineersVR;

/// <summary>
/// Pulsar's early hook, the only Pulsar entry point that runs before the game creates its render device.
/// </summary>
/// <remarks>
/// Pulsar (v2.4.3, Shared/Preloader.cs) looks up a type named exactly "Preloader", in the global namespace, in every enabled
/// plugin, and calls its public static void methods "Initialize" and "Finish". Legacy/Program.cs SetupPlugins calls them
/// after it has compiled and loaded the plugins and before SetupGame starts the game: Initialize before Pulsar installs its
/// Bin64 assembly resolver, Finish after it. The plugin's IPlugin instance is made much later, in the game's own plugin Init,
/// after the render device exists. So Finish does what SpaceEngineersVR.Launcher does before the game's Main.
/// There is no Initialize: the game's assemblies cannot be resolved yet when it runs. Under the launcher nothing calls this.
/// </remarks>
public static class Preloader
{
    public static void Finish()
    {
        try
        {
            // The game gets the same arguments (Pulsar passes its own on to MyProgram.Main); element 0 is the executable.
            Bootstrap.PreGameInit(Environment.GetCommandLineArgs().Skip(1).ToArray(), "Pulsar's Preloader hook");
        }
        catch (Exception e)
        {
            // Never stop the game from starting: an exception here becomes a Pulsar message box, which nobody in a headset sees.
            try
            {
                Log.Error(e, "SpaceEngineersVR could not set up before the game; continuing without VR");
            }
            catch
            {
                Console.Error.WriteLine(e);
            }
        }
    }
}
