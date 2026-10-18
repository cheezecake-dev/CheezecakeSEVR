using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Rendering;

namespace SpaceEngineersVR.Input
{
    /// <summary>
    /// The game's lists, tables and grids count a double click only if the cursor has moved no more than 0.005 of its screen
    /// units between the two presses (about 5 pixels of 1080), which a hand-held pointer rarely keeps to between two pulls of
    /// the trigger. While the picture is VR's the check allows <see cref="Radius"/> instead (0.015 by default, between half a
    /// degree and a degree of aim at the default menu distance and width). The double click still acts on the item under the
    /// second press, so the radius stays well below one row.
    /// </summary>
    /// <remarks>
    /// The grids are also helped by the laser, which puts a second press near the first where the first was
    /// (<see cref="Laser.DoubleClickDegrees"/>); their check is widened here as well, for a press that does not come from the
    /// laser. RenderDebug DoubleClickRadius=n sets the radius in thousandths of the game's screen units (5 is the game's own).
    /// </remarks>
    internal static class DoubleClickTolerance
    {
        /// <summary>The distance the game allows between the two presses, in its normalised screen units.</summary>
        public const float Native = 0.005f;

        /// <summary>The distance allowed while the picture is VR's, unless RenderDebug DoubleClickRadius=n says otherwise.</summary>
        public const float DefaultRadius = 0.015f;

        /// <summary>The distance allowed while the picture is VR's (RenderDebug DoubleClickRadius=n).</summary>
        public static volatile float Radius = DefaultRadius;

        /// <summary>The controls with a mouse-press check of their own, each with a HandleNewMousePress that compares the distance.</summary>
        private static readonly Type[] Controls =
        {
            typeof(MyGuiControlGrid), typeof(MyGuiControlListbox), typeof(MyGuiControlTable),
            typeof(MyGuiControlMultiSelectTable), typeof(MyGuiControlResearchGraph),
        };

        private static Harmony pending;

        /// <summary>
        /// Patching these controls runs their class constructors, which ask the renderer for texture sizes. The play features
        /// can be patched before the game is even made (the headset is found at once), when the renderer has no textures yet:
        /// the constructors then fail, the runtime keeps the failure, and the game's own preload of the same types ends the
        /// process. So until the game's first update the controls wait for <see cref="Update"/>.
        /// </summary>
        public static void Patch(Harmony harmony)
        {
            if (Sandbox.MySandboxGame.Static?.IsFirstUpdateDone != true)
            {
                pending = harmony;
                Log.Info("Double-click tolerance: waits for the game's first update to hook the menus' controls");
                return;
            }
            PatchControls(harmony);
        }

        /// <summary>Game thread, once a frame: hooks the controls once the game has run its first update.</summary>
        public static void Update()
        {
            if (pending == null || Sandbox.MySandboxGame.Static?.IsFirstUpdateDone != true)
                return;
            Harmony harmony = pending;
            pending = null;
            PatchControls(harmony);
        }

        /// <summary>Each control on its own, so one the game changes shape costs only its own tolerance.</summary>
        private static void PatchControls(Harmony harmony)
        {
            int hooked = 0;
            foreach (Type control in Controls)
            {
                Type type = control;
                bool done = PatchRegistry.Apply(type.Name, () =>
                {
                    MethodInfo press = AccessTools.DeclaredMethod(type, "HandleNewMousePress") ?? throw new MissingMethodException(type.Name, "HandleNewMousePress");
                    harmony.Patch(press, transpiler: new HarmonyMethod(typeof(DoubleClickTolerance), nameof(Widen)));
                }, $"Double-click tolerance ({type.Name}) could not be hooked; jittery double clicks on it may not register");
                if (done)
                    hooked++;
            }
            Log.Info($"Double-click tolerance: hooked {hooked} of {Controls.Length} controls; in VR they allow {Radius:0.000} between the presses (the game's is {Native:0.000})");
        }

        /// <summary>The one distance the check compares with, 0.005, becomes a call to <see cref="Allowed"/>.</summary>
        // Adapted from ColbyJack1134's SpaceEngineersVR (Patches/DoubleClickTolerancePatch.cs), with his permission.
        private static IEnumerable<CodeInstruction> Widen(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            List<CodeInstruction> code = instructions.ToList();
            CodeInstruction[] matches = code.Where(c => c.opcode == OpCodes.Ldc_R4 && c.operand is float value && value == Native).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException("Unrecognized native double-click tolerance in " + __originalMethod.DeclaringType.FullName);
            matches[0].opcode = OpCodes.Call;
            matches[0].operand = AccessTools.Method(typeof(DoubleClickTolerance), nameof(Allowed));
            return code;
        }

        private static float Allowed() => StereoPatches.VRPicture ? Radius : Native;
    }
}
