using System;
using System.Threading;
using HarmonyLib;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// One lock on the game's sprite queue (MySpritesManager.m_drawQueue, a plain Dictionary). While a loading screen is up
    /// the game defers its state changes: ProcessStateChanges runs on a ParallelTasks worker and adds the sprites drawn into
    /// a texture (LCDs, the wrist) to the queue while the render thread adds the screen's sprites and takes queued ones out.
    /// Two writers at once break the dictionary and the render thread goes down at a world's load (docs/CRASHES.md). The
    /// game's two writers take <see cref="Gate"/>, and so do our readers of the queue (<see cref="HudVisor"/>,
    /// <see cref="FullView"/>'s trace). Only while VR draws the picture (<see cref="StereoPatches.VRPicture"/>): played flat,
    /// the game's writers take no lock, as in the game.
    /// </summary>
    internal static class SpriteQueueLock
    {
        internal static readonly object Gate = new object();

        public static void Patch(Harmony harmony)
        {
            Type manager = AccessTools.TypeByName("VRage.Render11.Sprites.MySpritesManager")
                ?? throw new TypeLoadException("VRage.Render11.Sprites.MySpritesManager");
            foreach (string name in new[] { "AddMessage", "AcquireDrawMessages" })
                harmony.Patch(AccessTools.Method(manager, name) ?? throw new MissingMethodException("MySpritesManager." + name),
                    prefix: new HarmonyMethod(typeof(SpriteQueueLock), nameof(Enter)),
                    finalizer: new HarmonyMethod(typeof(SpriteQueueLock), nameof(Leave)));
        }

        private static void Enter(ref bool __state)
        {
            if (StereoPatches.VRPicture)
                Monitor.Enter(Gate, ref __state);
        }

        // A finalizer runs whether or not the method threw, and leaves the exception as it was.
        private static void Leave(bool __state)
        {
            if (__state)
                Monitor.Exit(Gate);
        }
    }
}
