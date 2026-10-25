// OffscreenPriority: the plugin's own offscreen sprite pictures are drawn ahead of the game's background work.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ParallelTasks;
using VRageRender;
using VRageRender.Messages;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The game draws an LCD's sprites into its texture on a worker (MyOffscreenRenderer.SubmitWork, at most five at a time),
    /// at the lowest priority there is (WorkPriority.VeryLow); the texture shows nothing until that has run. Right after a
    /// world loads the workers are busy with the load's own work, and measured on the Red Ship test world a picture sent as
    /// the hands appeared waited 5.4 s for a worker, so the labels beside the hands were missing that long. A texture of
    /// ours that the player looks at straight away is named here (<see cref="Register"/>) and its picture goes to the workers
    /// at <see cref="Priority"/> instead; every other texture, the game's LCDs among them, keeps the game's own priority.
    /// </summary>
    internal static class OffscreenPriority
    {
        /// <summary>Ours go ahead of the load's background work, as most of the game's tasks do.</summary>
        private const WorkPriority Priority = WorkPriority.Normal;

        private static readonly ConcurrentDictionary<string, bool> names = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private static FieldInfo messageField;

        /// <summary>Any thread: the offscreen texture <paramref name="textureName"/> (MyRenderProxy.CreateGeneratedTexture) is drawn at <see cref="Priority"/>.</summary>
        public static void Register(string textureName) => names[textureName] = true;

        public static void Patch(Harmony harmony)
        {
            Type renderer = typeof(MyDX11Render).Assembly.GetType("VRage.Render11.Render.MyOffscreenRenderer", throwOnError: true);
            MethodInfo submit = AccessTools.Method(renderer, "SubmitWork")
                ?? throw new MissingMethodException("MyOffscreenRenderer.SubmitWork");
            Type data = submit.GetParameters()[0].ParameterType;
            messageField = AccessTools.Field(data, "Message") ?? throw new MissingFieldException("MyOffscreenRenderer.MyData.Message");
            harmony.Patch(submit, transpiler: new HarmonyMethod(typeof(OffscreenPriority), nameof(Transpile)));
            Log.Info("Offscreen priority: the plugin's own offscreen pictures are drawn ahead of the game's background work");
        }

        /// <summary>The priority given to Parallel.Start in SubmitWork comes from <see cref="For"/>, which is handed the work's data.</summary>
        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            MethodInfo pick = AccessTools.Method(typeof(OffscreenPriority), nameof(For));
            int replaced = 0;
            for (int i = 0; i < code.Count; i++)
            {
                if (!(code[i].operand is MethodInfo called) || called.DeclaringType != typeof(Parallel) || called.Name != "Start")
                    continue;
                ParameterInfo[] parameters = called.GetParameters();
                if (parameters.Length == 0 || parameters[parameters.Length - 1].ParameterType != typeof(WorkPriority) || i == 0)
                    continue;
                // The last argument, the priority, is the constant pushed just before the call.
                if (!code[i - 1].LoadsConstant())
                    continue;
                // ... the game's priority stays on the stack; For takes it and the work's data and gives the one to use.
                code.Insert(i, new CodeInstruction(OpCodes.Ldarg_0));
                code.Insert(i + 1, new CodeInstruction(OpCodes.Call, pick));
                replaced++;
                i += 2;
            }
            if (replaced != 1)
                throw new InvalidOperationException($"MyOffscreenRenderer.SubmitWork: expected one Parallel.Start with a priority, found {replaced}");
            return code;
        }

        /// <summary>Render thread: <see cref="Priority"/> for a picture of one of ours, else the game's own.</summary>
        private static WorkPriority For(WorkPriority game, object data)
        {
            if (names.IsEmpty || data == null)
                return game;
            string name = (messageField.GetValue(data) as MyRenderMessageRenderOffscreenTexture)?.OffscreenTexture;
            return name != null && names.ContainsKey(name) && Priority < game ? Priority : game;
        }
    }
}
