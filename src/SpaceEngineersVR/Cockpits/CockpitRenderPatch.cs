// Adapted from ColbyJack1134's SpaceEngineersVR (Patches/CockpitRenderPatch.cs, Patches/CockpitScreenVisibilityPatch.cs), with his permission.
// His patches were Harmony attributes; here they go on through PatchRegistry (one feature, so a game update that removes any
// of them takes the whole cockpit swap off (Patched stays false) and the seat keeps our own controls), and each body runs under a FeatureGuard
// because three of them run on the render thread. His RemoteCockpitModelPatch belongs to his remote-view feature and is not carried.
using System;
using HarmonyLib;
using Sandbox.Game.Entities.Character;
using VRage.Utils;
using VRageRender;
using VRageRender.Messages;

namespace SpaceEngineersVR.Cockpits
{
    internal static class CockpitRenderPatch
    {
        private static readonly FeatureGuard guard=new FeatureGuard("ColbyCockpit render");

        /// <summary>The render hooks his cockpit swap needs, and the post-simulation pose of the moving pieces.</summary>
        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(AccessTools.TypeByName("VRageRender.MyRender11"),"ProcessMessageInternal") ?? throw new MissingMethodException("MyRender11.ProcessMessageInternal"),
                prefix:new HarmonyMethod(typeof(CockpitRenderPatch),nameof(MaterialPrefix)));
            harmony.Patch(AccessTools.Method(AccessTools.TypeByName("VRage.Render11.Scene.Components.MyRenderableComponent"),"RebuildRenderProxies") ?? throw new MissingMethodException("MyRenderableComponent.RebuildRenderProxies"),
                postfix:new HarmonyMethod(typeof(CockpitRenderPatch),nameof(VerifyPostfix)));
            harmony.Patch(AccessTools.Method(AccessTools.TypeByName("VRageRender.MyMeshes"),"CreateRuntimeMesh") ?? throw new MissingMethodException("MyMeshes.CreateRuntimeMesh"),
                postfix:new HarmonyMethod(typeof(CockpitRenderPatch),nameof(RuntimeSectionsPostfix)));
            harmony.Patch(AccessTools.Method(typeof(MyRenderProxy),nameof(MyRenderProxy.UpdateModelProperties)) ?? throw new MissingMethodException("MyRenderProxy.UpdateModelProperties"),
                prefix:new HarmonyMethod(typeof(CockpitRenderPatch),nameof(ScreenVisibilityPrefix)));
            // His pieces were posed again after physics, from his character component's UpdateAfterSimulation: the pilot's, here.
            harmony.Patch(AccessTools.Method(typeof(MyCharacter),nameof(MyCharacter.UpdateAfterSimulation)),
                postfix:new HarmonyMethod(typeof(CockpitRenderPatch),nameof(AfterSimulation)));
            // Ahead of GrabUse (30): a grip that starts by one of his sticks takes the stick.
            Hands.GripClaim.Register(CockpitControls.ClaimName,29,CockpitControls.Take);
            // Ahead of the sticks: a grip that starts with the fingertip on one of his switches works the switch.
            Hands.GripClaim.Register(CockpitTouch.ClaimName,28,CockpitTouch.Take);
            Patched=true;
        }

        /// <summary>Every hook above is in; without them the seat keeps our own controls.</summary>
        internal static bool Patched { get; private set; }

        // Render thread.
        private static void MaterialPrefix(MyRenderMessageBase __0)
        {
            if(!guard.On) return;
            try { CockpitMaterials.Register(__0); }
            catch(Exception e) { guard.Failed(e,"ColbyCockpit: registering a cockpit material failed"); }
        }

        // Render thread.
        private static void VerifyPostfix(object __instance)
        {
            if(!guard.On) return;
            try { CockpitRender.Verify(__instance); }
            catch(Exception e) { guard.Failed(e,"ColbyCockpit: verifying the cockpit meshes failed"); }
        }

        // Render thread.
        private static void RuntimeSectionsPostfix(MyStringId nameKey,object __result)
        {
            if(!guard.On) return;
            try
            {
                if (nameKey.ToString().StartsWith("SEVR_Cockpit_",StringComparison.Ordinal)) CockpitRender.InitializeRuntimeSections(__result);
            }
            catch(Exception e) { guard.Failed(e,"ColbyCockpit: preparing a cockpit runtime mesh failed"); }
        }

        private static void ScreenVisibilityPrefix(uint id,string materialName,ref RenderFlags addFlags,ref RenderFlags removeFlags)
        {
            if(!guard.On) return;
            try { CockpitRender.PreserveScreenVisibility(id,materialName,ref addFlags,ref removeFlags); }
            catch(Exception e) { guard.Failed(e,"ColbyCockpit: keeping a moving screen visible failed"); }
        }

        // Game thread.
        private static void AfterSimulation(MyCharacter __instance)
        {
            if(!guard.On || !CockpitControls.Seated(__instance)) return;
            try { CockpitControls.RefreshVisuals(); }
            catch(Exception e) { guard.Failed(e,"ColbyCockpit: posing the cockpit's moving pieces failed"); }
        }
    }
}
