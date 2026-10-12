using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SpaceEngineersVR.OpenXR;
using SpaceEngineersVR.Rendering;
using VRageMath;
using VRageRender;

namespace SpaceEngineersVR.Input
{
    /// <summary>
    /// The drive's <c>shot eyes</c>: the two eye images as they go to the headset, left and right side by side in one PNG, so
    /// what differs between the eyes (exposure, flares, particles, smoke, the eye offset) can be compared from one capture.
    /// The window's mirror shows one eye only, and <c>shot both</c> only has both on the desktop, never with a headset.
    /// </summary>
    /// <remarks>
    /// <see cref="EyeRenderer"/> asks <see cref="Capture"/> for each eye right where it copies the finished eye (after the GUI,
    /// the overlays and <see cref="Vignette"/>) into the eye's OpenXR swapchain image; with a request waiting, the eye is copied
    /// on the GPU into a staging texture of our own, and nothing waits for it. <see cref="Poll"/> then tries to map the copies
    /// before each present, without ever blocking, so the frame goes on whatever the GPU is doing; the PNG is composed and
    /// written on another thread, as <see cref="DriveShot"/>'s is.
    /// </remarks>
    internal static class DriveEyes
    {
        private static readonly PropertyInfo resource =
            typeof(MyDX11Render).Assembly.GetType("VRage.Render11.Resources.IResource")?.GetProperty("Resource");

        /// <summary>The longest the copies may take to finish on the GPU; past it the request fails.</summary>
        private const int CopyWaitMilliseconds = 5000;

        /// <summary>The widest a PNG may be before each eye is halved to fit.</summary>
        private const int MaxWidth = 8192;

        /// <summary>Frames in a row that are not for the headset before the request is given up on (a state change takes a frame or two).</summary>
        private const int NoHeadsetFrames = 30;

        // The request, between the game thread and the render thread.
        private static readonly object gate = new object();
        private static string wanted, taken, error, note;
        private static int serial;
        private static volatile bool asked;

        // Render thread only.
        private static int noHeadset;

        /// <summary>The two eyes' copies on their way: staging textures of the eye images, from one frame.</summary>
        private sealed class Grab
        {
            public int Serial, Count, Width, Height;
            public long Frame;
            public Format Format;
            public bool BlueFirst;
            public string Path, Image;
            public readonly Texture2D[] Staging = new Texture2D[2];
            public readonly Stopwatch Waited = Stopwatch.StartNew();

            public void Dispose()
            {
                for (int i = 0; i < Staging.Length; i++)
                {
                    Staging[i]?.Dispose();
                    Staging[i] = null;
                }
            }
        }

        private static Grab grab;

        // ---- Game thread. ----

        /// <summary>Asks for the next frame drawn for the headset to be saved to <paramref name="path"/>.</summary>
        public static void Request(string path)
        {
            lock (gate)
            {
                wanted = path;
                taken = error = note = null;
                serial++;
                asked = true;
            }
        }

        /// <summary>Gives the request up: nothing is saved for it, and it is not answered.</summary>
        public static void Cancel()
        {
            lock (gate)
            {
                wanted = null;
                asked = false;
                serial++;
            }
        }

        /// <summary>True when the request has ended: <paramref name="path"/> is the PNG, or <paramref name="failure"/> says why not.</summary>
        public static bool Result(out string path, out string failure, out string detail)
        {
            lock (gate)
            {
                path = taken;
                failure = error;
                detail = note;
            }
            return path != null || failure != null;
        }

        // ---- Render thread. ----

        /// <summary>
        /// Top of a frame's eyes, <paramref name="headset"/> as <see cref="EyeRenderer"/> found it: a request cannot be met by a frame
        /// that is not for the headset (the session is idle or asleep, or there is none), and fails with the reason once that has lasted.
        /// </summary>
        public static void FrameBegin(bool headset)
        {
            if (headset)
            {
                noHeadset = 0;
                return;
            }
            if (!asked || ++noHeadset < NoHeadsetFrames)
                return;
            string why;
            if (!XrSession.Exists)
                why = "there is no headset session (no headset runtime was found, or the session ended)";
            else if (!XrSession.Running)
                why = $"the headset session is not running (OpenXR session state {XrSession.State}; the headset is asleep or not worn)";
            else
                why = $"the runtime does not want its eyes drawn (OpenXR session state {XrSession.State})";
            noHeadset = 0;
            int mine;
            lock (gate)
                mine = serial;
            Finish(mine, null, "no headset session is drawing: " + why, null);
        }

        /// <summary>
        /// Render thread, in <see cref="EyeRenderer"/>'s eye loop for a frame that goes to the headset: eye <paramref name="eye"/>
        /// (0 left, 1 right) is finished in <paramref name="target"/>, a game texture; <paramref name="size"/> is the part of it
        /// that is submitted. Never throws.
        /// </summary>
        public static void Capture(object target, int eye, Vector2I size, long frame)
        {
            if (!asked)
                return;
            int mine = 0;
            try
            {
                string path;
                lock (gate)
                {
                    path = wanted;
                    mine = serial;
                }
                if (path == null)
                    return;

                // An older request's copies, or a frame that did not get to the second eye: start again from this eye.
                if (grab != null && (grab.Serial != mine || grab.Frame != frame))
                {
                    grab.Dispose();
                    grab = null;
                }

                Texture2D source = target == null || resource == null ? null : resource.GetValue(target) as Texture2D;
                DeviceContext context = DriveShot.Context();
                SharpDX.Direct3D11.Device device = EyeRenderer.D3DDevice;
                if (source == null || source.IsDisposed || context == null || device == null || device.IsDisposed)
                {
                    Finish(mine, null, resource == null ? "this game version has no IResource.Resource" : "the eye's texture or the render context is not there", null);
                    return;
                }
                Texture2DDescription real = source.Description;
                if (real.SampleDescription.Count != 1 || real.ArraySize != 1 || real.MipLevels != 1)
                {
                    Finish(mine, null, $"the eye image is not a plain texture ({real.SampleDescription.Count} samples, {real.ArraySize} slices, {real.MipLevels} mips)", null);
                    return;
                }
                if (!DriveShot.TryByteOrder(real.Format, out bool blueFirst))
                {
                    Finish(mine, null, $"the eye image's format {real.Format} is not one a shot can save", null);
                    return;
                }
                int width = Math.Min(real.Width, size.X), height = Math.Min(real.Height, size.Y);
                if (width < 1 || height < 1)
                {
                    Finish(mine, null, $"the eye image is {real.Width}x{real.Height}", null);
                    return;
                }

                if (grab == null)
                {
                    Vector2I image = EyeRenderer.EyeResolution;
                    grab = new Grab
                    {
                        Serial = mine,
                        Frame = frame,
                        Path = path,
                        Width = width,
                        Height = height,
                        Format = real.Format,
                        BlueFirst = blueFirst,
                        Image = image.X == width && image.Y == height ? string.Empty : $" (dynamic resolution: the eye image is {image.X}x{image.Y}, this is the part drawn)"
                    };
                }
                Grab g = grab;
                if (g.Staging[eye] != null || g.Width != width || g.Height != height || g.Format != real.Format)
                {
                    // Not the second eye of the same frame, or the eyes differ in size or format: nothing to put side by side.
                    string why = g.Staging[eye] != null ? $"eye {eye} was drawn twice in frame {frame}" : $"the eyes differ ({g.Width}x{g.Height} {g.Format} and {width}x{height} {real.Format})";
                    g.Dispose();
                    grab = null;
                    Finish(mine, null, why, null);
                    return;
                }

                // The game's own device wrapper (MyRender11.DeviceInstance), never disposed here.
                g.Staging[eye] = new Texture2D(device, new Texture2DDescription
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
                });
                context.CopySubresourceRegion(source, 0, new ResourceRegion(0, 0, 0, width, height, 1), g.Staging[eye], 0);
                g.Count++;
                if (g.Count == 2)
                {
                    g.Waited.Restart();
                    lock (gate)
                    {
                        if (serial == mine)
                        {
                            wanted = null;
                            asked = false;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                grab?.Dispose();
                grab = null;
                Finish(mine, null, "error: " + e.Message, null);
            }
        }

        /// <summary>
        /// Render thread, before each present: when both eyes' copies have finished on the GPU, reads them out and has the PNG
        /// written. A copy that has not finished is looked at again at the next present; nothing here waits. Never throws.
        /// </summary>
        public static void Poll()
        {
            Grab g = grab;
            if (g == null)
                return;
            try
            {
                int mine;
                lock (gate)
                    mine = serial;
                if (g.Serial != mine)
                {
                    // The command gave up on it.
                    grab = null;
                    g.Dispose();
                    return;
                }
                if (g.Count < 2)
                {
                    // The frame ended before its second eye; the request is still waiting for a whole frame.
                    grab = null;
                    g.Dispose();
                    return;
                }
                DeviceContext context = DriveShot.Context();
                if (context == null)
                {
                    grab = null;
                    g.Dispose();
                    Finish(g.Serial, null, "no render context", null);
                    return;
                }

                var boxes = new SharpDX.DataBox[2];
                byte[][] eyes = null;
                string failure = null;
                int mapped = 0;
                try
                {
                    for (; mapped < 2; mapped++)
                    {
                        SharpDX.DataBox box = default;
                        try
                        {
                            // An unfinished copy comes back as an empty box (SharpDX swallows WAS_STILL_DRAWING under DoNotWait),
                            // or as that error from a build that does not: both mean look again later, and neither is mapped.
                            box = context.MapSubresource(g.Staging[mapped], 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.DoNotWait);
                        }
                        catch (SharpDX.SharpDXException e) when (e.ResultCode == SharpDX.DXGI.ResultCode.WasStillDrawing)
                        {
                        }
                        if (box.DataPointer == IntPtr.Zero)
                            break;
                        boxes[mapped] = box;
                    }
                    if (mapped == 2)
                    {
                        int row = g.Width * 4;
                        eyes = new byte[2][];
                        for (int i = 0; i < 2 && failure == null; i++)
                        {
                            if (boxes[i].RowPitch < row)
                            {
                                failure = $"the staging texture mapped with a row pitch of {boxes[i].RowPitch} for rows of {row} bytes";
                                break;
                            }
                            eyes[i] = new byte[row * g.Height];
                            for (int y = 0; y < g.Height; y++)
                                Marshal.Copy(boxes[i].DataPointer + y * boxes[i].RowPitch, eyes[i], y * row, row);
                        }
                    }
                }
                finally
                {
                    for (int i = 0; i < mapped; i++)
                        context.UnmapSubresource(g.Staging[i], 0);
                }
                if (mapped < 2 && failure == null)
                {
                    if (g.Waited.ElapsedMilliseconds <= CopyWaitMilliseconds)
                        return;
                    failure = $"the GPU had not finished the copies after {CopyWaitMilliseconds} ms";
                }
                if (failure != null)
                {
                    grab = null;
                    g.Dispose();
                    Finish(g.Serial, null, failure, null);
                    return;
                }

                grab = null;
                g.Dispose();
                int serialNow = g.Serial, width = g.Width, height = g.Height;
                string path = g.Path, image = g.Image;
                long frame = g.Frame;
                bool blueFirst = g.BlueFirst;
                ThreadPool.QueueUserWorkItem(_ => Encode(serialNow, path, frame, eyes, width, height, blueFirst, image));
            }
            catch (Exception e)
            {
                grab = null;
                g.Dispose();
                Finish(g.Serial, null, "error: " + e.Message, null);
            }
        }

        /// <summary>Ends the request numbered <paramref name="mine"/>; one that was given up on or replaced is not answered.</summary>
        private static void Finish(int mine, string path, string failure, string detail)
        {
            lock (gate)
            {
                if (mine != serial)
                    return;
                wanted = null;
                asked = false;
                taken = failure == null ? path : null;
                error = failure;
                note = detail;
            }
        }

        // ---- The PNG. A pool thread: nothing here may throw, or the game would go down with the thread. ----

        private static void Encode(int mine, string path, long frame, byte[][] eyes, int width, int height, bool blueFirst, string image)
        {
            string failure = null, detail = null;
            try
            {
                bool halve = width * 2 > MaxWidth && width >= 2 && height >= 2;
                DriveShot.Pixels both = Compose(eyes, width, height, blueFirst, halve);
                DriveShot.WritePng(path, both);
                int each = both.Width / 2;
                detail = $"left eye on the left, right on the right, each {each}x{both.Height}, "
                    + (halve ? $"halved from {width}x{height} to fit {MaxWidth} wide" : "full size")
                    + $", draw frame {frame}{image}";
            }
            catch (Exception e)
            {
                failure = "the PNG could not be written: " + e.Message;
            }
            Finish(mine, failure == null ? path : null, failure, detail);
        }

        /// <summary>
        /// The left eye and the right side by side, in the eyes' own byte order, each at its size or (<paramref name="halve"/>)
        /// at half of it in both directions by the mean of 2x2 pixels. Alpha is made opaque: the runtime ignores the eyes' alpha,
        /// so it is not part of what the headset shows (and a viewer would show the holes).
        /// </summary>
        private static DriveShot.Pixels Compose(byte[][] eyes, int width, int height, bool blueFirst, bool halve)
        {
            int w = halve ? width / 2 : width, h = halve ? height / 2 : height;
            int stride = width * 4, outStride = w * 8;
            var data = new byte[outStride * h];
            for (int e = 0; e < 2; e++)
            {
                byte[] source = eyes[e];
                for (int y = 0; y < h; y++)
                {
                    int to = y * outStride + e * w * 4;
                    if (!halve)
                    {
                        System.Buffer.BlockCopy(source, y * stride, data, to, stride);
                        continue;
                    }
                    int top = 2 * y * stride, bottom = top + stride;
                    for (int x = 0; x < w; x++)
                    {
                        int a = top + 8 * x, b = bottom + 8 * x;
                        for (int k = 0; k < 3; k++)
                            data[to + 4 * x + k] = (byte)((source[a + k] + source[a + 4 + k] + source[b + k] + source[b + 4 + k] + 2) >> 2);
                    }
                }
                for (int y = 0; y < h; y++)
                {
                    int alpha = y * outStride + e * w * 4 + 3;
                    for (int x = 0; x < w; x++)
                        data[alpha + 4 * x] = 255;
                }
            }
            return new DriveShot.Pixels
            {
                Data = data,
                Width = w * 2,
                Height = h,
                BlueFirst = blueFirst,
                Source = $"two {width}x{height} eye images"
            };
        }
    }
}
