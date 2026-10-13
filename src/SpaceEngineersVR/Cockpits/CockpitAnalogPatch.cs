// Adapted from ColbyJack1134's SpaceEngineersVR (Multiplayer/MultiplayerPatches.cs AnalogSavePatch; the AnalogControl.Update
// call in Multiplayer/MultiplayerRuntime.cs), with his permission.
// Phase 6 of docs/COCKPIT_PORT.md. A piston or rotor a cockpit handle is driving is saved without the velocity the handle gave
// it (his AnalogSavePatch), and the drive is stepped once a game frame, from MySession.Update (his ran it from his multiplayer
// update, on the host or without one). Each body runs under a FeatureGuard.
using System;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Game;

namespace SpaceEngineersVR.Cockpits
{
    internal static class CockpitAnalogPatch
    {
        private static readonly FeatureGuard guard=new FeatureGuard("ColbyCockpit handles");

        public static void Patch(Harmony harmony)
        {
            foreach(var type in new[] {typeof(Sandbox.Game.Entities.Blocks.MyPistonBase),typeof(Sandbox.Game.Entities.Cube.MyMotorStator)})
                harmony.Patch(AccessTools.Method(type,"GetObjectBuilderCubeBlock") ?? throw new MissingMethodException(type.Name+".GetObjectBuilderCubeBlock"),
                    postfix:new HarmonyMethod(typeof(CockpitAnalogPatch),nameof(SavedPostfix)));
            harmony.Patch(AccessTools.Method(typeof(MySession),nameof(MySession.Update)) ?? throw new MissingMethodException("MySession.Update"),
                postfix:new HarmonyMethod(typeof(CockpitAnalogPatch),nameof(UpdatePostfix)));
        }

        private static void SavedPostfix(MyCubeBlock __instance,MyObjectBuilder_CubeBlock __result)
        {
            if(!guard.On) return;
            try { AnalogControl.SavedVelocity(__instance.EntityId,__result); }
            catch(Exception e) { guard.Failed(e,"ColbyCockpit: saving a block a handle drives failed"); }
        }

        private static void UpdatePostfix()
        {
            if(!guard.On) return;
            try { AnalogControl.Update(); }
            catch(Exception e) { guard.Failed(e,"ColbyCockpit: driving the blocks the cockpit handles move failed"); }
        }
    }
}
