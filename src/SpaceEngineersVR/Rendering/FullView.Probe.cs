// FullView, diagnostics: while Trace is on, reads back pixels of the overlay and of each eye around the blend, and the shot note.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using SharpDX.DXGI;
using D3D = SharpDX.Direct3D11;

namespace SpaceEngineersVR.Rendering
{
    internal static partial class FullView
    {
        // Render thread, for the trace: once a second the full-view texture and each eye before and after the blend, read at
        // ProbeDepths of the way in from the eye's outer edge (the left eye's is its left side, the right eye's its right) along the
        // middle row, copied into staging textures in that frame and read in the next. Slot 0 is the full-view texture, read at both
        // sides (the left's points, then the right's); slots 1 and 2 the left eye before and after, 3 and 4 the right eye's.
        private static readonly float[] ProbeDepths = { 0.02f, 0.10f, 0.20f, 0.30f, 0.50f };

        private const float ProbeV = 0.5f;

        private const int ProbeSlots = 5;

        private static readonly string[] ProbeNames = { "full view", "left eye before the blend", "left eye after it", "right eye before the blend", "right eye after it" };

        private static readonly D3D.Texture2D[] probeStaging = new D3D.Texture2D[ProbeSlots];

        private static readonly IntPtr[] probeDevice = new IntPtr[ProbeSlots];

        private static readonly string[] probeWhere = new string[ProbeSlots];

        private static readonly bool[] probeCopied = new bool[ProbeSlots];

        private static readonly float[][] probePixels = new float[ProbeSlots][]; // r,g,b,a of each point, 0-1

        private static bool probing, probePending;

        private static long probeAt;

        private static float probeAlpha, probeReach;

        private static Kind probeKind;

        private static string probeResult;

        private static PropertyInfo rcProperty, deviceContext, resource;

        private static D3D.DeviceContext Context() =>
            rcProperty?.GetValue(null) is object rc ? deviceContext?.GetValue(rc) as D3D.DeviceContext : null;

        // The probe's columns in a texture <width> across: slot 0's are the left side's ProbeDepths, then the right side's;
        // an eye's are the points of its own outer side.
        private static int[] ProbeColumns(int slot, int width)
        {
            int n = ProbeDepths.Length;
            var columns = new int[slot == 0 ? 2 * n : n];
            for (int i = 0; i < n; i++)
            {
                int near = Math.Max(0, Math.Min(width - 1, (int)(ProbeDepths[i] * width)));
                if (slot == 0)
                {
                    columns[i] = near;
                    columns[n + i] = width - 1 - near;
                }
                else
                    columns[i] = slot >= 3 ? width - 1 - near : near;
            }
            return columns;
        }

        // Queues copies of the probe's pixels of a game texture (one with IResource.Resource) into a row of staging texture <slot>.
        private static void ProbeCopy(int slot, object texture)
        {
            probeCopied[slot] = false;
            try
            {
                D3D.DeviceContext context = Context();
                var source = texture == null || resource == null ? null : resource.GetValue(texture) as D3D.Texture2D;
                if (context == null || source == null || source.IsDisposed)
                {
                    probeWhere[slot] = context == null ? "no render context" : "no texture";
                    return;
                }
                D3D.Texture2DDescription real = source.Description;
                if (real.SampleDescription.Count != 1 || real.Width < 1 || real.Height < 1)
                {
                    probeWhere[slot] = $"{real.Width}x{real.Height} with {real.SampleDescription.Count} samples, not read";
                    return;
                }
                int[] columns = ProbeColumns(slot, real.Width);
                int y = Math.Max(0, Math.Min(real.Height - 1, (int)(ProbeV * real.Height)));
                // The game's own device wrapper (MyRender11.DeviceInstance), never disposed here. Texture2D.Device is a
                // wrapper the texture keeps after the first ask; disposing it, as b3bb36e did, left every later ask a
                // dead one (the NullReferenceException on every readback after the first in the 02:12 run).
                D3D.Device device = EyeRenderer.D3DDevice;
                if (device == null || device.IsDisposed)
                {
                    probeWhere[slot] = "no device";
                    return;
                }
                D3D.Texture2D staging = probeStaging[slot];
                if (staging == null || staging.IsDisposed || probeDevice[slot] != device.NativePointer
                    || staging.Description.Format != real.Format || staging.Description.Width != columns.Length)
                {
                    staging?.Dispose();
                    staging = probeStaging[slot] = new D3D.Texture2D(device, new D3D.Texture2DDescription
                    {
                        Width = columns.Length,
                        Height = 1,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = real.Format,
                        Usage = D3D.ResourceUsage.Staging,
                        SampleDescription = new SampleDescription(1, 0),
                        BindFlags = D3D.BindFlags.None,
                        CpuAccessFlags = D3D.CpuAccessFlags.Read,
                        OptionFlags = D3D.ResourceOptionFlags.None
                    });
                    probeDevice[slot] = device.NativePointer;
                }
                for (int i = 0; i < columns.Length; i++)
                    context.CopySubresourceRegion(source, 0, new D3D.ResourceRegion(columns[i], y, 0, columns[i] + 1, y + 1, 1), staging, 0, i, 0, 0);
                probeWhere[slot] = $"({real.Width}x{real.Height} {real.Format}, row {y})";
                probeCopied[slot] = true;
            }
            catch (Exception e)
            {
                probeWhere[slot] = "copy failed: " + e.Message;
            }
        }

        // The next frame: maps the staging textures (the copies are a frame old, so the wait is short) and keeps the numbers.
        private static void ProbeRead()
        {
            probePending = false;
            D3D.DeviceContext context = Context();
            for (int slot = 0; slot < ProbeSlots; slot++)
            {
                probePixels[slot] = null;
                if (!probeCopied[slot] || context == null)
                    continue;
                probeCopied[slot] = false;
                D3D.Texture2D staging = probeStaging[slot];
                try
                {
                    SharpDX.DataBox box = context.MapSubresource(staging, 0, D3D.MapMode.Read, D3D.MapFlags.None);
                    try
                    {
                        Format format = staging.Description.Format;
                        int count = staging.Description.Width, bytes = BytesPerPixel(format);
                        var pixels = new float[count * 4];
                        for (int i = 0; i < count; i++)
                            Decode(IntPtr.Add(box.DataPointer, i * bytes), format, pixels, i * 4);
                        probePixels[slot] = pixels;
                    }
                    finally
                    {
                        context.UnmapSubresource(staging, 0);
                    }
                }
                catch (Exception e)
                {
                    probeWhere[slot] = "read failed: " + e.Message;
                }
            }
            probeResult = ProbeText();
        }

        // The numbers read, as the trace line has them: the full-view texture's alpha is how much of the eye the overlay covers.
        private static string ProbeText()
        {
            int n = ProbeDepths.Length;
            string depths = string.Join("/", ProbeDepths.Select(depth => (depth * 100).ToString("0")));
            string sprite = probeKind == Kind.Again ? "the last sprite again" : "the game's sprite from that frame";
            string drawn = probeReach > 0f ? $"{BloodPulse.Layers} deep, reaching {probeReach * 100:0}% in" : "whole";
            var parts = new List<string>(ProbeSlots);
            float[] full = probePixels[0];
            if (full == null)
                parts.Add($"{ProbeNames[0]} {probeWhere[0] ?? "not copied"}");
            else
            {
                string expected = string.Join(" ", ProbeDepths.Select(depth => Coverage(depth, probeAlpha, probeReach).ToString("0.00")));
                parts.Add($"{ProbeNames[0]} alpha {Values(full, 0, n, 3, 1f, "0.00")} left and {Values(full, n, n, 3, 1f, "0.00")} right at {depths}% in, " +
                    $"expected {expected} {probeWhere[0]}");
            }
            for (int slot = 1; slot < ProbeSlots; slot++)
            {
                float[] pixels = probePixels[slot];
                parts.Add(pixels == null ? $"{ProbeNames[slot]} {probeWhere[slot] ?? "not copied"}"
                    : $"{ProbeNames[slot]} r,g,b of 255 {string.Join(" ", Enumerable.Range(0, n).Select(i => string.Join(",", Enumerable.Range(0, 3).Select(c => Value(pixels, i, c, 255f, "0")))))}");
            }
            return $"sprite alpha {probeAlpha:0.00} ({sprite}; {drawn}): " + string.Join(", ", parts);
        }

        // Channel <channel> (0 r to 3 a) of points <first> to <first + count - 1>, times <scale>, space between.
        private static string Values(float[] pixels, int first, int count, int channel, float scale, string format) =>
            string.Join(" ", Enumerable.Range(first, count).Select(i => Value(pixels, i, channel, scale, format)));

        private static string Value(float[] pixels, int point, int channel, float scale, string format)
        {
            float value = pixels[point * 4 + channel];
            return float.IsNaN(value) ? "?" : (value * scale).ToString(format);
        }

        private static int BytesPerPixel(Format format) => format.ToString().Contains("16G16B16A16") ? 8 : 4;

        // One pixel as r,g,b,a, 0-1 (the 10-bit formats' alpha is of its 2 bits, half floats as stored; sRGB formats as stored,
        // not linearised), or NaN for a format not handled here.
        private static void Decode(IntPtr p, Format format, float[] into, int at)
        {
            switch (format)
            {
                case Format.B8G8R8A8_UNorm:
                case Format.B8G8R8A8_UNorm_SRgb:
                case Format.B8G8R8A8_Typeless:
                case Format.B8G8R8X8_UNorm:
                case Format.B8G8R8X8_UNorm_SRgb:
                case Format.B8G8R8X8_Typeless:
                    into[at] = Marshal.ReadByte(p, 2) / 255f;
                    into[at + 1] = Marshal.ReadByte(p, 1) / 255f;
                    into[at + 2] = Marshal.ReadByte(p, 0) / 255f;
                    into[at + 3] = Marshal.ReadByte(p, 3) / 255f;
                    break;
                case Format.R8G8B8A8_UNorm:
                case Format.R8G8B8A8_UNorm_SRgb:
                case Format.R8G8B8A8_Typeless:
                    for (int c = 0; c < 4; c++)
                        into[at + c] = Marshal.ReadByte(p, c) / 255f;
                    break;
                case Format.R10G10B10A2_UNorm:
                case Format.R10G10B10A2_Typeless:
                {
                    uint v = (uint)Marshal.ReadInt32(p);
                    into[at] = (v & 1023) / 1023f;
                    into[at + 1] = ((v >> 10) & 1023) / 1023f;
                    into[at + 2] = ((v >> 20) & 1023) / 1023f;
                    into[at + 3] = (v >> 30) / 3f;
                    break;
                }
                case Format.R16G16B16A16_Float:
                case Format.R16G16B16A16_Typeless:
                    for (int c = 0; c < 4; c++)
                        into[at + c] = Half(p, c * 2);
                    break;
                default:
                    for (int c = 0; c < 4; c++)
                        into[at + c] = float.NaN;
                    break;
            }
        }

        private static float Half(IntPtr p, int offset)
        {
            int h = (ushort)Marshal.ReadInt16(p, offset);
            int sign = (h >> 15) & 1, exponent = (h >> 10) & 31, mantissa = h & 1023;
            float value = exponent == 0 ? mantissa / 1024f * (float)Math.Pow(2, -14)
                : exponent == 31 ? float.PositiveInfinity
                : (1f + mantissa / 1024f) * (float)Math.Pow(2, exponent - 15);
            return sign == 1 ? -value : value;
        }

        /// <summary>Render thread, at the present: what the last eyes had of the overlay, for the test screenshot note.</summary>
        internal static string ShotNote()
        {
            double ms = (Stopwatch.GetTimestamp() - frameStamp) * 1000.0 / Stopwatch.Frequency;
            string what = frameKind == Kind.New ? $"the game's sprite from this frame, alpha {heldAlpha:0.00}"
                : frameKind == Kind.Again ? $"the last sprite again (no new game frame), alpha {heldAlpha:0.00}"
                : "none";
            return $"blood overlay: {what}, laid over {frameEyes} eye{(frameEyes == 1 ? "" : "s")}, eyes drawn {ms:0} ms before this present";
        }
    }
}
