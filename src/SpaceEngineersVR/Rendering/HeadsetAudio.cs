using System;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using SharpDX.XAudio2;
using SpaceEngineersVR.OpenXR;

namespace SpaceEngineersVR.Rendering
{
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
                Log.Error(e, "Sound through the headset is unavailable; the game's sound goes to the Windows default output");
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
                reason = "no headset was connected when the game started";
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
}
