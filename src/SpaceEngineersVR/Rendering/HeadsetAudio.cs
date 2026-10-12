using System;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
#if !LINUX
using SharpDX.XAudio2;
#else
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
#endif
using SpaceEngineersVR.OpenXR;

namespace SpaceEngineersVR.Rendering
{
#if LINUX
    /// <summary>
    /// Plays the game's sound through the headset while WiVRn is connected. WiVRn publishes the headset as a PipeWire audio
    /// sink (and source) for as long as the headset is connected. linux-compat's audio shim opens OpenAL Soft's default device,
    /// so the game's playback streams follow the desktop default; this moves them (found through its PipeWire client's process id in
    /// <c>pw-dump</c>) to the WiVRn sink with the "target.object" metadata the session manager obeys (<c>pw-metadata</c>), the same
    /// as pavucontrol's move. When the sink goes away, and when the game exits, the metadata is deleted so the stream goes back to
    /// the default output, and so it does while the picture is the game's because the headset is away
    /// (<see cref="StereoPatches.PlayingFlat"/>). Nothing in the game is patched; if pw-dump or pw-metadata is missing the sound stays on the default.
    /// No microphone: neither linux-compat's shim nor the Windows plugin opens a capture device, so there is nothing to route.
    /// </summary>
    internal static class HeadsetAudio
    {
        private const int PollMs = 3000;
        private const int ToolTimeoutMs = 4000;

        private static readonly List<uint> moved = new List<uint>();

        /// <summary>Wakes the poll when the picture switches, so the sound follows at once.</summary>
        private static readonly AutoResetEvent pictureChanged = new AutoResetEvent(false);

        public static void Patch(Harmony harmony)
        {
            if (!VRSettings.HeadsetAudio)
            {
                Log.Info("Sound: through the headset is off in the VR options");
                return;
            }
            if (RenderDebug.Peek("HeadsetAudio") == 0)
            {
                Log.Info("Sound: HeadsetAudio=0 in the render debug file");
                return;
            }
            // Started with or without a headset: the WiVRn sink appearing is what moves the sound, so a headset that
            // connects later in the run gets it too.
            StereoPatches.PictureChanged += _ => pictureChanged.Set();
            new Thread(Run) { IsBackground = true, Name = "SEVR headset audio", Priority = ThreadPriority.BelowNormal }.Start();
            AppDomain.CurrentDomain.ProcessExit += (_, __) => MoveBack();
        }

        private static void Run()
        {
            int pid = Process.GetCurrentProcess().Id;
            string lastSink = null;
            bool missingTools = false;
            while (!missingTools)
            {
                try
                {
                    string dump = Tool("pw-dump", "", out bool started);
                    if (!started)
                    {
                        Log.Info("Sound: pw-dump (PipeWire) is not installed; the game's sound stays on the default output");
                        return;
                    }
                    FindNodes(dump, pid, out string sink, out List<uint> streams);
                    bool flat = sink != null && StereoPatches.PlayingFlat;
                    if (flat)
                        sink = null;
                    if (sink != lastSink)
                    {
                        Log.Info(sink != null ? $"Sound: the headset's audio sink is {sink}"
                            : flat ? "Sound: playing flat while the headset is away; back on the default output"
                            : "Sound: the headset's audio sink is gone; back on the default output");
                        if (sink == null)
                            MoveBack();
                        lastSink = sink;
                    }
                    if (sink != null)
                    {
                        lock (moved)
                            moved.RemoveAll(id => !streams.Contains(id));
                        // A stream's target is lost while the game runs (seen when a world loads or unloads: the metadata is
                        // removed and the stream is back on the default output), so what is set is read back, not remembered.
                        Dictionary<uint, string> targets = streams.Count > 0 ? Targets() : null;
                        foreach (uint id in streams)
                        {
                            if (targets != null && targets.TryGetValue(id, out string target) && target == sink)
                                continue;
                            Tool("pw-metadata", $"{id} target.object {sink} Spa:String", out bool metadataStarted);
                            if (!metadataStarted)
                            {
                                missingTools = true;
                                Log.Info("Sound: pw-metadata (PipeWire) is not installed; the game's sound stays on the default output");
                                break;
                            }
                            bool again;
                            lock (moved)
                            {
                                again = moved.Contains(id);
                                if (!again)
                                    moved.Add(id);
                            }
                            Log.Info(again ? $"Sound: the game's stream {id} had left {sink}; moved to the headset again"
                                           : $"Sound: playing through {sink} (the game's stream {id} moved to the headset)");
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.Error(e, "Sound: choosing the headset's audio sink failed");
                    return;
                }
                pictureChanged.WaitOne(PollMs);
            }
        }

        /// <summary>
        /// The WiVRn sink's node name (null while none) and the ids of this process's playback streams. A stream node has
        /// no process id of its own (SDL's does not); the client object it belongs to (<c>client.id</c>) does.
        /// </summary>
        private static void FindNodes(string dump, int pid, out string sink, out List<uint> streams)
        {
            sink = null;
            streams = new List<uint>();
            string ownPid = pid.ToString(CultureInfo.InvariantCulture);
            var ownClients = new HashSet<string>();
            var playback = new List<KeyValuePair<uint, string>>();
            using (JsonDocument doc = JsonDocument.Parse(dump))
            {
                foreach (JsonElement obj in doc.RootElement.EnumerateArray())
                {
                    if (!obj.TryGetProperty("type", out JsonElement type) || !obj.TryGetProperty("info", out JsonElement info)
                        || !info.TryGetProperty("props", out JsonElement props))
                        continue;
                    string kind = type.GetString();
                    if (kind == "PipeWire:Interface:Client")
                    {
                        if (Prop(props, "application.process.id") == ownPid)
                            ownClients.Add(obj.GetProperty("id").GetUInt32().ToString(CultureInfo.InvariantCulture));
                        continue;
                    }
                    if (kind != "PipeWire:Interface:Node")
                        continue;
                    string cls = Prop(props, "media.class");
                    if (cls == "Audio/Sink")
                    {
                        string name = Prop(props, "node.name");
                        if (name != null && sink == null && (name.IndexOf("wivrn", StringComparison.OrdinalIgnoreCase) >= 0
                                                          || (Prop(props, "node.description") ?? "").IndexOf("wivrn", StringComparison.OrdinalIgnoreCase) >= 0))
                            sink = name;
                    }
                    else if (cls == "Stream/Output/Audio")
                    {
                        playback.Add(new KeyValuePair<uint, string>(obj.GetProperty("id").GetUInt32(), Prop(props, "client.id")));
                    }
                }
            }
            foreach (KeyValuePair<uint, string> stream in playback)
                if (stream.Value != null && ownClients.Contains(stream.Value))
                    streams.Add(stream.Key);
        }

        /// <summary>The "target.object" metadata now set, by stream node id (pw-metadata's listing, one line per key).</summary>
        private static Dictionary<uint, string> Targets()
        {
            var targets = new Dictionary<uint, string>();
            string text = Tool("pw-metadata", "-n default", out bool _);
            foreach (Match m in Regex.Matches(text ?? "", @"update: id:(\d+) key:'target\.object' value:'([^']*)'"))
                targets[uint.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)] = m.Groups[2].Value;
            return targets;
        }

        private static string Prop(JsonElement props, string name)
        {
            if (!props.TryGetProperty(name, out JsonElement v))
                return null;
            return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
        }

        /// <summary>Deletes the metadata that moved the streams, so the session manager puts them on the default output again.</summary>
        private static void MoveBack()
        {
            uint[] ids;
            lock (moved)
            {
                ids = moved.ToArray();
                moved.Clear();
            }
            foreach (uint id in ids)
            {
                try
                {
                    Tool("pw-metadata", $"-d {id} target.object", out bool _);
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>Runs a PipeWire tool and returns its output; <paramref name="started"/> false when it is not installed.</summary>
        private static string Tool(string file, string arguments, out bool started)
        {
            var start = new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            Process p;
            try
            {
                p = Process.Start(start);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                started = false;
                return null;
            }
            started = true;
            using (p)
            {
                Task<string> err = p.StandardError.ReadToEndAsync();
                Task<string> output = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(ToolTimeoutMs))
                {
                    try { p.Kill(); } catch (Exception) { }
                    return "";
                }
                return output.Result;
            }
        }
    }
#else
    /// <summary>
    /// Plays the game's sound through the headset's audio device, the Windows endpoint the headset runtime names for VR
    /// (Meta's XR_OCULUS_audio_device_guid; with a Quest on Link, "Headphones (Oculus Virtual Audio Device)"), instead of
    /// the Windows default output, which is often the PC's speakers.
    /// </summary>
    /// <remarks>
    /// The game (VRage.Audio MyXAudio2.StartEngine) makes its XAudio2 2.9 engine and its one mastering voice through
    /// <c>VRage.Platform.Windows.Audio.MyPlatformAudio.CreateMasteringVoice(int)</c>, which always passes a null device id:
    /// the Windows default. That call is replaced here by one with the headset's endpoint id; XAudio2 2.9 takes the
    /// IMMDevice::GetId form the runtime gives. The game reads the voice's channels and rate back from the voice, so a
    /// device with another layout needs nothing else. The game makes the voice again only when XAudio2 reports a critical
    /// error (the device went away): StartEngine runs again and so does this, so the choice is made afresh each time, and a
    /// headset device that is gone by then gives the Windows default. If anything here fails the game's own call runs.
    /// </remarks>
    internal static class HeadsetAudio
    {
        private const string PlatformAudio = "VRage.Platform.Windows.Audio.MyPlatformAudio";

        public static void Patch(Harmony harmony)
        {
            // Sound on the default output is still sound: a game update that moves this call only costs the headset routing.
            try
            {
                Type platformAudio = typeof(VRage.Platform.Windows.MyVRageWindows).Assembly.GetType(PlatformAudio)
                                     ?? throw new TypeLoadException(PlatformAudio + " not found");
                MethodInfo create = AccessTools.Method(platformAudio, "CreateMasteringVoice", new[] { typeof(int) })
                                    ?? throw new MissingMethodException(PlatformAudio, "CreateMasteringVoice");
                harmony.Patch(create, prefix: new HarmonyMethod(typeof(HeadsetAudio), nameof(CreateOnHeadset)));
            }
            catch (Exception e)
            {
                PatchRegistry.Lost(e, "Sound through the headset is unavailable; the game's sound goes to the Windows default output");
                throw;
            }
        }

        /// <summary>Prefix of MyPlatformAudio.CreateMasteringVoice: false when the voice was made on the headset's device.</summary>
        private static bool CreateOnHeadset(XAudio2 ___m_audioEngine, ref MasteringVoice __result)
        {
            try
            {
                string id = HeadsetDevice(out string name, out string reason);
                if (id != null)
                {
                    try
                    {
                        __result = new MasteringVoice(___m_audioEngine, 0, 0, id);
                        Log.Info($"Sound: playing through {name} (the headset's audio device from the runtime)");
                        return false;
                    }
                    catch (Exception e)
                    {
                        reason = $"XAudio2 could not open {name} {id}: {e.Message}";
                    }
                }
                Log.Info($"Sound: the Windows default output ({reason})");
            }
            catch (Exception e)
            {
                Log.Error(e, "Sound: the Windows default output (choosing the headset's audio device failed)");
            }
            return true;
        }

        /// <summary>The headset's audio endpoint id and its Windows name, or null with the reason to use the Windows default.</summary>
        private static string HeadsetDevice(out string name, out string reason)
        {
            name = null;
            if (!VRSettings.HeadsetAudio)
            {
                reason = "Sound through the headset is off in the VR options";
                return null;
            }
            if (RenderDebug.Peek("HeadsetAudio") == 0)
            {
                reason = "HeadsetAudio=0 in the render debug file";
                return null;
            }
            if (!XrRuntime.HasSystem)
            {
                // The voice is made when the game's sound starts; a headset that connects later does not move it.
                reason = "no headset was connected when the game's sound started";
                return null;
            }
            string id = XrRuntime.AudioOutputDeviceId(out reason);
            if (id == null)
                return null;
            return Endpoint.Describe(id, out name, out reason) ? id : null;
        }

        /// <summary>Windows' own view of an audio endpoint (MMDevice API): that it exists and plays, and its name.</summary>
        private static class Endpoint
        {
            private const int DeviceStateActive = 0x1;
            private static readonly PropertyKey FriendlyName = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

            public static bool Describe(string id, out string name, out string reason)
            {
                name = id;
                IMMDeviceEnumerator enumerator = null;
                IMMDevice device = null;
                IPropertyStore properties = null;
                try
                {
                    enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                    int hr = enumerator.GetDevice(id, out device);
                    if (hr < 0 || device == null)
                    {
                        reason = $"Windows has no audio device {id} (0x{hr:X8})";
                        return false;
                    }
                    if (device.OpenPropertyStore(0 /* STGM_READ */, out properties) >= 0 && properties != null)
                    {
                        PropertyKey key = FriendlyName;
                        if (properties.GetValue(ref key, out PropVariant value) >= 0)
                        {
                            if (value.Type == 31 /* VT_LPWSTR */ && value.Pointer != IntPtr.Zero)
                                name = Marshal.PtrToStringUni(value.Pointer);
                            PropVariantClear(ref value);
                        }
                    }
                    hr = device.GetState(out int state);
                    if (hr < 0 || state != DeviceStateActive)
                    {
                        reason = $"{name} is not playing (device state 0x{state:X}, 0x{hr:X8}; is the headset connected?)";
                        return false;
                    }
                    reason = null;
                    return true;
                }
                catch (Exception e)
                {
                    reason = $"looking up audio device {id} failed: {e.Message}";
                    return false;
                }
                finally
                {
                    if (properties != null)
                        Marshal.ReleaseComObject(properties);
                    if (device != null)
                        Marshal.ReleaseComObject(device);
                    if (enumerator != null)
                        Marshal.ReleaseComObject(enumerator);
                }
            }

            [DllImport("ole32.dll")]
            private static extern int PropVariantClear(ref PropVariant value);

            [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
            private class MMDeviceEnumerator
            {
            }

            [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            private interface IMMDeviceEnumerator
            {
                [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
                [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
                [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            }

            [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            private interface IMMDevice
            {
                [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr instance);
                [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore properties);
                [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
                [PreserveSig] int GetState(out int state);
            }

            [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            private interface IPropertyStore
            {
                [PreserveSig] int GetCount(out int count);
                [PreserveSig] int GetAt(int index, out PropertyKey key);
                [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct PropertyKey
            {
                public Guid FormatId;
                public int PropertyId;

                public PropertyKey(Guid formatId, int propertyId)
                {
                    FormatId = formatId;
                    PropertyId = propertyId;
                }
            }

            /// <summary>PROPVARIANT: the type tag, then (after three reserved words) the value; only a string's pointer is read.</summary>
            [StructLayout(LayoutKind.Explicit, Size = 24)]
            private struct PropVariant
            {
                [FieldOffset(0)] public ushort Type;
                [FieldOffset(8)] public IntPtr Pointer;
            }
        }
    }
#endif
}
