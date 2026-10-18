using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The game's model loader (VRage.Render11.GeometryStage2.Model.MyModelFactory) loads models on worker threads and the
    /// render thread finishes them each frame (CompleteAsyncTasks), looking each one up in its table by file. A world's
    /// unload empties the table and tells loads still running to throw their result away, but the next world's first model
    /// asked for takes that back: a load from the old world that finishes after it is queued for a model no longer in the
    /// table, and the lookup throws on the render thread, which takes the game down as a world loads (docs/CRASHES.md, the
    /// KeyNotFoundException in MyModelFactory.CompleteAsyncTasks). A prefix drops such loads first, as the unload meant to;
    /// the model is asked for again by whatever in the new world uses it. Only while VR draws the picture
    /// (<see cref="StereoPatches.VRPicture"/>): played flat, the game's own loader runs as it is.
    /// </summary>
    internal static class ModelLoadRace
    {
        private static FieldInfo completedField, modelsField, pathField, modelField;
        private static MethodInfo unload;

        public static void Patch(Harmony harmony)
        {
            Type factory = AccessTools.TypeByName("VRage.Render11.GeometryStage2.Model.MyModelFactory")
                ?? throw new TypeLoadException("VRage.Render11.GeometryStage2.Model.MyModelFactory");
            Type data = AccessTools.Inner(factory, "ModelData") ?? throw new TypeLoadException("MyModelFactory.ModelData");
            completedField = AccessTools.Field(factory, "m_completedTasks") ?? throw new MissingFieldException("MyModelFactory.m_completedTasks");
            modelsField = AccessTools.Field(factory, "m_models") ?? throw new MissingFieldException("MyModelFactory.m_models");
            pathField = AccessTools.Field(data, "FullFilePath") ?? throw new MissingFieldException("ModelData.FullFilePath");
            modelField = AccessTools.Field(data, "Model") ?? throw new MissingFieldException("ModelData.Model");
            unload = AccessTools.Method(modelField.FieldType, "UnloadData") ?? throw new MissingMethodException("MyModel.UnloadData");
            harmony.Patch(AccessTools.Method(factory, "CompleteAsyncTasks") ?? throw new MissingMethodException("MyModelFactory.CompleteAsyncTasks"),
                prefix: new HarmonyMethod(typeof(ModelLoadRace), nameof(DropOrphans)));
        }

        // Render thread, each frame. The game's lock is the list itself, as CompleteAsyncTasks takes it.
        private static void DropOrphans(object __instance)
        {
            if (!StereoPatches.VRPicture)
                return;
            if (!(completedField.GetValue(__instance) is IList completed) || completed.Count == 0)
                return;
            var models = (IDictionary)modelsField.GetValue(__instance);
            int dropped = 0;
            string first = null;
            lock (completed)
            {
                for (int i = completed.Count - 1; i >= 0; i--)
                {
                    object task = completed[i];
                    string path = (string)pathField.GetValue(task);
                    if (path != null && models.Contains(path))
                        continue;
                    object model = modelField.GetValue(task);
                    if (model != null)
                        unload.Invoke(model, null);
                    completed.RemoveAt(i);
                    dropped++;
                    first = path;
                }
            }
            if (dropped > 0)
                Log.Info($"Model loading: {dropped} model load(s) from an unloaded world finished after the next world began loading; dropped, as the game's unload meant (first: {first})");
        }
    }
}
