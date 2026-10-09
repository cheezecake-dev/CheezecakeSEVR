using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using HarmonyLib;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SpaceEngineersVR.Rendering;
using VRageRender;

namespace SpaceEngineersVR.Input
{
    /// <summary>
    /// The drive's <c>shot</c>: the window's back buffer, read through a staging texture of our own and written as a PNG by
    /// code of our own, so a shot cannot read memory it does not own.
    /// </summary>
    /// <remarks>
    /// The game's own way (MyTextureData.ToFile) crashed the game with an AccessViolationException: it makes the staging
    /// texture the size of the back buffer's real texture but then reads <c>MyBackbuffer.Size</c> pixels from it, and that
    /// size is MyRender11.ResolutionI - which <see cref="EyeRenderer"/> sets to the eye size (2064x2272 with a headset)
    /// while the swapchain stays the mirror window (1920x1080). Row 1081 on is past the end of the mapping.
    /// Here the staging texture is made from the back buffer's own description, the part wanted is clamped to it, every
    /// read is inside what was mapped, and the pixels are copied out into a managed array before anything is encoded.
    /// </remarks>
    internal static class DriveShot
    {
        /// <summary>One shot's pixels as read from the back buffer: tightly packed rows of 4 bytes a pixel, in the back buffer's order.</summary>
        internal sealed class Pixels
        {
            public byte[] Data;
            public int Width, Height;

            /// <summary>The back buffer is B,G,R,A (not R,G,B,A) in memory.</summary>
            public bool BlueFirst;

            public string Source;
        }

        private static PropertyInfo backbuffer, backbufferResource, renderContext, deviceContext;

        /// <summary>Finds the back buffer and the game's immediate context; false when this game version has other names.</summary>
        public static bool Hook(Type render)
        {
            backbuffer = AccessTools.Property(render, "Backbuffer");
            backbufferResource = backbuffer == null ? null : AccessTools.Property(backbuffer.PropertyType, "Resource");
            renderContext = AccessTools.Property(render, "RC");
            deviceContext = renderContext == null ? null : AccessTools.Property(renderContext.PropertyType, "DeviceContext");
            return backbufferResource != null && deviceContext != null;
        }

        /// <summary>
        /// Render thread, with the frame to show in the back buffer: copies the left eye's part of it (<see cref="PanelCursor.LeftArea"/>:
        /// the left half of the window on the desktop, all of it with a headset or in a menu, never more than the texture
        /// holds) into a managed array. A pixel of the shot is the window's pixel at its offset, so <c>gui</c> can use it.
        /// Null with <paramref name="error"/> set when it cannot.
        /// </summary>
        public static Pixels Read(out string error)
        {
            error = null;
            object buffer = backbuffer.GetValue(null);
            Texture2D source = buffer == null ? null : backbufferResource.GetValue(buffer) as Texture2D;
            if (source == null || source.IsDisposed || source.NativePointer == IntPtr.Zero)
            {
                error = "no back buffer";
                return null;
            }
            DeviceContext context = renderContext.GetValue(null) is object rc ? deviceContext.GetValue(rc) as DeviceContext : null;
            if (context == null)
            {
                error = "no render context";
                return null;
            }

            // What the texture really is: this, not the game's idea of its size, bounds everything below.
            Texture2DDescription real = source.Description;
            if (real.SampleDescription.Count != 1 || real.ArraySize != 1 || real.MipLevels != 1)
            {
                error = $"the back buffer is not a plain texture ({real.SampleDescription.Count} samples, {real.ArraySize} slices, {real.MipLevels} mips)";
                return null;
            }
            bool blueFirst;
            switch (real.Format)
            {
                case Format.R8G8B8A8_UNorm:
                case Format.R8G8B8A8_UNorm_SRgb:
                case Format.R8G8B8A8_Typeless:
                    blueFirst = false;
                    break;
                case Format.B8G8R8A8_UNorm:
                case Format.B8G8R8A8_UNorm_SRgb:
                case Format.B8G8R8A8_Typeless:
                case Format.B8G8R8X8_UNorm:
                case Format.B8G8R8X8_UNorm_SRgb:
                case Format.B8G8R8X8_Typeless:
                    blueFirst = true;
                    break;
                default:
                    error = $"the back buffer's format {real.Format} is not one a shot can save";
                    return null;
            }

            // The left eye's part of the window, cut to the texture (a headset's eye is taller than the window, so it hangs over).
            MyViewport eye = PanelCursor.LeftArea();
            int left = 0, top = 0, width = real.Width, height = real.Height;
            if (eye.Width > 0f && eye.Height > 0f)
            {
                int right = (int)Math.Min(real.Width, Math.Ceiling(eye.OffsetX + eye.Width));
                int bottom = (int)Math.Min(real.Height, Math.Ceiling(eye.OffsetY + eye.Height));
                left = (int)Math.Max(0, Math.Floor(eye.OffsetX));
                top = (int)Math.Max(0, Math.Floor(eye.OffsetY));
                if (right > left && bottom > top)
                {
                    width = right - left;
                    height = bottom - top;
                }
                else
                {
                    left = top = 0;
                }
            }
            if (width < 1 || height < 1)
            {
                error = $"the back buffer is {real.Width}x{real.Height}";
                return null;
            }

            using (var staging = new Texture2D(source.Device, new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = real.Format,
                Usage = ResourceUsage.Staging,
                SampleDescription = new SampleDescription(1, 0),
                BindFlags = BindFlags.None,
                CpuAccessFlags = CpuAccessFlags.Read,
                OptionFlags = ResourceOptionFlags.None
            }))
            {
                context.CopySubresourceRegion(source, 0, new ResourceRegion(left, top, 0, left + width, top + height, 1), staging, 0);
                SharpDX.DataBox box = context.MapSubresource(staging, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
                try
                {
                    int row = width * 4;
                    if (box.DataPointer == IntPtr.Zero || box.RowPitch < row)
                    {
                        error = $"the staging texture mapped with a row pitch of {box.RowPitch} for rows of {row} bytes";
                        return null;
                    }
                    var data = new byte[row * height];
                    for (int y = 0; y < height; y++)
                        System.Runtime.InteropServices.Marshal.Copy(box.DataPointer + y * box.RowPitch, data, y * row, row);
                    return new Pixels
                    {
                        Data = data,
                        Width = width,
                        Height = height,
                        BlueFirst = blueFirst,
                        Source = $"{real.Width}x{real.Height} {real.Format} back buffer, the left eye's part at {left},{top}"
                    };
                }
                finally
                {
                    context.UnmapSubresource(staging, 0);
                }
            }
        }

        // ---- The PNG, from the copy. Any thread. ----

        private static readonly uint[] crcTable = MakeCrcTable();

        private static uint[] MakeCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        /// <summary>Writes the pixels as an 8-bit RGBA PNG; the file appears whole or not at all.</summary>
        public static void WritePng(string path, Pixels pixels)
        {
            int width = pixels.Width, height = pixels.Height, stride = width * 4;
            if (width < 1 || height < 1 || pixels.Data == null || pixels.Data.Length != stride * height)
                throw new InvalidOperationException("the pixels do not match their size");

            // Each row: filter type 1 (Sub), then the row as R,G,B,A with every byte less the one a pixel before it.
            var raw = new byte[(stride + 1) * height];
            var rgba = new byte[stride];
            for (int y = 0; y < height; y++)
            {
                int from = y * stride;
                for (int x = 0; x < stride; x += 4)
                {
                    rgba[x] = pixels.Data[from + x + (pixels.BlueFirst ? 2 : 0)];
                    rgba[x + 1] = pixels.Data[from + x + 1];
                    rgba[x + 2] = pixels.Data[from + x + (pixels.BlueFirst ? 0 : 2)];
                    rgba[x + 3] = pixels.Data[from + x + 3];
                }
                int to = y * (stride + 1);
                raw[to] = 1;
                for (int i = 0; i < stride; i++)
                    raw[to + 1 + i] = (byte)(rgba[i] - (i >= 4 ? rgba[i - 4] : 0));
            }

            byte[] zlib;
            using (var packed = new MemoryStream())
            {
                packed.WriteByte(0x78);
                packed.WriteByte(0x9C);
                using (var deflate = new DeflateStream(packed, CompressionLevel.Fastest, true))
                    deflate.Write(raw, 0, raw.Length);
                uint a = 1, b = 0;
                foreach (byte value in raw)
                {
                    a = (a + value) % 65521;
                    b = (b + a) % 65521;
                }
                WriteUInt32(packed, (b << 16) | a);
                zlib = packed.ToArray();
            }

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            string part = path + ".part";
            using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                file.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
                var header = new MemoryStream();
                WriteUInt32(header, (uint)width);
                WriteUInt32(header, (uint)height);
                header.Write(new byte[] { 8, 6, 0, 0, 0 }, 0, 5); // 8 bits, RGBA, deflate, adaptive filtering, no interlace
                WriteChunk(file, "IHDR", header.ToArray());
                WriteChunk(file, "IDAT", zlib);
                WriteChunk(file, "IEND", new byte[0]);
            }
            if (File.Exists(path))
                File.Delete(path);
            File.Move(part, path);
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            var name = new byte[] { (byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3] };
            WriteUInt32(stream, (uint)data.Length);
            stream.Write(name, 0, 4);
            stream.Write(data, 0, data.Length);
            uint crc = 0xFFFFFFFFu;
            foreach (byte value in name)
                crc = crcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
            foreach (byte value in data)
                crc = crcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
            WriteUInt32(stream, crc ^ 0xFFFFFFFFu);
        }

        private static void WriteUInt32(Stream stream, uint value)
        {
            stream.WriteByte((byte)(value >> 24));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }
    }
}
