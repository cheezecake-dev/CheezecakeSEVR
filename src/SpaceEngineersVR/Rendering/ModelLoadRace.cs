using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
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
    /// <remarks>
    /// The same prefix watches for models that stay unloaded. Until its load finishes, everything drawn with a model shows the
    /// loader's placeholder (Models\Debug\LoadingQuad.mwm), and a block or item whose model the new pipeline cannot draw moves
    /// to the old pipeline only then; a load that never finishes leaves it missing from the world for good. Every five seconds
    /// the table is read for models still on the placeholder, and when some have waited 30 s or more the log says how many,
    /// which, and the state of the low-priority workers the loads run on (suspended, busy, queued): queued loads with every
    /// worker busy are slow, queued loads with idle workers are asleep, and nothing queued with models still waiting means the
    /// loads were lost (the game logs a failed load's exception in SpaceEngineers.log, followed by "model.LoadState:").
    /// </remarks>
    internal static class ModelLoadRace
    {
        private const int CheckSeconds = 5, LateSeconds = 30, RepeatSeconds = 60, PathsNamed = 8;

        private static FieldInfo completedField, modelsField, pathField, modelField, dummyField;
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
            dummyField = AccessTools.Field(factory, "m_dummyModel");
            if (dummyField == null)
                Log.Warn("Model loading: MyModelFactory.m_dummyModel not found; models that stay unloaded are not reported");
            harmony.Patch(AccessTools.Method(factory, "CompleteAsyncTasks") ?? throw new MissingMethodException("MyModelFactory.CompleteAsyncTasks"),
                prefix: new HarmonyMethod(typeof(ModelLoadRace), nameof(DropOrphans)));
        }

        // Render thread, each frame. The game's lock is the list itself, as CompleteAsyncTasks takes it.
        private static void DropOrphans(object __instance)
        {
            if (!StereoPatches.VRPicture)
                return;
            var models = (IDictionary)modelsField.GetValue(__instance);
            WatchLate(__instance, models);
            if (!(completedField.GetValue(__instance) is IList completed) || completed.Count == 0)
                return;
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

        // Path -> when it was first seen on the placeholder. Render thread only.
        private static readonly Dictionary<string, long> waiting = new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly HashSet<string> onPlaceholder = new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<string> late = new List<string>(), loaded = new List<string>();
        private static long nextCheck, lastReport;
        private static int lastLate;

        private static void WatchLate(object factory, IDictionary models)
        {
            long now = Stopwatch.GetTimestamp();
            if (dummyField == null || now < nextCheck)
                return;
            nextCheck = now + CheckSeconds * Stopwatch.Frequency;
            object dummy = dummyField.GetValue(factory);
            if (dummy == null)
                return;
            onPlaceholder.Clear();
            foreach (DictionaryEntry entry in models)
                if (entry.Key is string path && entry.Value != null && ReferenceEquals(modelField.GetValue(entry.Value), dummy))
                    onPlaceholder.Add(path);
            loaded.Clear();
            foreach (var pair in waiting)
                if (!onPlaceholder.Contains(pair.Key))
                    loaded.Add(pair.Key);
            foreach (string path in loaded)
                waiting.Remove(path);
            late.Clear();
            long oldest = 0;
            foreach (string path in onPlaceholder)
            {
                if (!waiting.TryGetValue(path, out long since))
                    waiting[path] = since = now;
                long age = now - since;
                if (age >= LateSeconds * Stopwatch.Frequency)
                {
                    late.Add(path);
                    oldest = Math.Max(oldest, age);
                }
            }
            if (late.Count == 0)
            {
                if (lastLate > 0)
                    Log.Info($"Model loading: the late models have loaded ({onPlaceholder.Count} still loading, none for {LateSeconds} s)");
                lastLate = 0;
                return;
            }
            if (late.Count == lastLate && now - lastReport < RepeatSeconds * Stopwatch.Frequency)
                return;
            lastLate = late.Count;
            lastReport = now;
            late.Sort(StringComparer.Ordinal);
            var text = new StringBuilder();
            text.Append($"Model loading: {late.Count} model(s) still on the loader's placeholder after {LateSeconds} s or more (oldest {oldest / Stopwatch.Frequency} s; {onPlaceholder.Count} waiting in all, {models.Count} in the table, {Finished(factory)} finished and not yet taken in); low-priority workers: {Workers()}; ");
            for (int i = 0; i < Math.Min(PathsNamed, late.Count); i++)
                text.Append(i == 0 ? "" : ", ").Append(late[i]);
            if (late.Count > PathsNamed)
                text.Append($" and {late.Count - PathsNamed} more");
            Log.Warn(text.ToString());
        }

        private static int Finished(object factory) => completedField.GetValue(factory) is IList completed ? completed.Count : -1;

        // The worker array the game's model loads run on (WorkPriority.VeryLow), read by reflection: suspended, busy, queued.
        private static string Workers()
        {
            try
            {
                object scheduler = ParallelTasks.Parallel.Scheduler;
                MethodInfo get = AccessTools.Method(scheduler.GetType(), "GetWorkerArray");
                if (get == null)
                    return "not readable (" + scheduler.GetType().Name + ")";
                object array = get.Invoke(scheduler, new object[] { ParallelTasks.WorkPriority.VeryLow });
                bool suspended = (bool)AccessTools.Field(array.GetType(), "m_suspended").GetValue(array);
                object queue = AccessTools.Field(array.GetType(), "m_taskQueue").GetValue(array);
                int queued = (int)queue.GetType().GetProperty("Count").GetValue(queue);
                var workers = (Array)AccessTools.Property(array.GetType(), "Workers").GetValue(array);
                int busy = 0;
                foreach (object worker in workers)
                    if ((long)AccessTools.Field(worker.GetType(), "Worklog").GetValue(worker) != 0)
                        busy++;
                return $"{(suspended ? "suspended" : "running")}, {busy} of {workers.Length} busy, {queued} queued";
            }
            catch (Exception e)
            {
                return "not readable (" + e.GetType().Name + ")";
            }
        }
    }
}
