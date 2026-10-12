// WristStatus, sprite capture: records the wrist controls' sprite and text calls into a signature so the screen is redrawn only when they change.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using VRageMath;
using VRageRender;
using VRageRender.Messages;

namespace SpaceEngineersVR.Rendering
{
    internal static partial class WristStatus
    {
        // While the wrist's controls draw (game thread), their sprite and text calls are kept here instead of sent, and
        // what they show is folded into the frame's signature: positions and sizes to whole texture pixels, colours as drawn
        // (8 bits a channel), texts as written. The screen is redrawn from the kept calls only when the signature changes.
        [ThreadStatic] private static bool recording;

        private static readonly List<Call> calls = new List<Call>();

        private static ulong signature;

        private enum CallKind : byte { Sprite, SpriteExt, Text }

        private struct Call
        {
            public CallKind Kind;
            public string Texture, Mask, Text;
            public RectangleF Destination;
            public Rectangle? Source;
            public Color Color;
            public float Rotation, RotationSpeed, Scale, MaxWidth;
            public Vector2 Right, Origin, At;
            public bool IgnoreBounds, WaitTillLoaded;
            public int Font;
        }

        private static void Mix(int value)
        {
            unchecked
            {
                signature = (signature ^ (uint)value) * 1099511628211UL;
            }
        }

        private static void Mix(float value, float step) => Mix((int)Math.Round(value / step));

        private static void Mix(string text) => Mix(text == null ? 0 : text.GetHashCode());

        private static void Mix(Rectangle? rectangle)
        {
            if (rectangle == null)
            {
                Mix(-1);
                return;
            }
            Rectangle r = rectangle.Value;
            Mix(r.X);
            Mix(r.Y);
            Mix(r.Width);
            Mix(r.Height);
        }

        private static void Mix(RectangleF rectangle)
        {
            Mix(rectangle.X, 1f);
            Mix(rectangle.Y, 1f);
            Mix(rectangle.Width, 1f);
            Mix(rectangle.Height, 1f);
        }

        /// <summary>MyRenderProxy.DrawSprite while the wrist's controls draw: kept for the screen, not sent.</summary>
        private static bool TakeSprite(string texture, ref RectangleF destination, Rectangle? sourceRectangle, Color color, float rotation,
            bool ignoreBounds, bool waitTillLoaded, string targetTexture, string maskTexture, float rotSpeed)
        {
            if (!recording || targetTexture != null)
                return true;
            calls.Add(new Call
            {
                Kind = CallKind.Sprite, Texture = texture, Destination = destination, Source = sourceRectangle, Color = color,
                Rotation = rotation, IgnoreBounds = ignoreBounds, WaitTillLoaded = waitTillLoaded, Mask = maskTexture, RotationSpeed = rotSpeed,
            });
            Mix(1);
            Mix(texture);
            Mix(maskTexture);
            Mix(destination);
            Mix(sourceRectangle);
            Mix((int)color.PackedValue);
            Mix(rotation, 0.001f);
            Mix(rotSpeed, 0.001f);
            return false;
        }

        /// <summary>MyRenderProxy.DrawSpriteExt while the wrist's controls draw: kept for the screen, not sent.</summary>
        private static bool TakeSpriteExt(string texture, ref RectangleF destination, Rectangle? sourceRectangle, Color color, ref Vector2 rightVector,
            ref Vector2 origin, bool ignoreBounds, bool waitTillLoaded, string targetTexture, string maskTexture)
        {
            if (!recording || targetTexture != null)
                return true;
            calls.Add(new Call
            {
                Kind = CallKind.SpriteExt, Texture = texture, Destination = destination, Source = sourceRectangle, Color = color,
                Right = rightVector, Origin = origin, IgnoreBounds = ignoreBounds, WaitTillLoaded = waitTillLoaded, Mask = maskTexture,
            });
            Mix(2);
            Mix(texture);
            Mix(maskTexture);
            Mix(destination);
            Mix(sourceRectangle);
            Mix((int)color.PackedValue);
            Mix(rightVector.X, 0.001f);
            Mix(rightVector.Y, 0.001f);
            Mix(origin.X, 1f);
            Mix(origin.Y, 1f);
            return false;
        }

        /// <summary>MyRenderProxy.DrawString while the wrist's controls draw: kept for the screen, not sent.</summary>
        private static bool TakeText(int fontIndex, Vector2 screenCoord, Color colorMask, string text, float screenScale, float screenMaxWidth,
            bool ignoreBounds, string targetTexture)
        {
            if (!recording || targetTexture != null)
                return true;
            calls.Add(new Call
            {
                Kind = CallKind.Text, Font = fontIndex, At = screenCoord, Color = colorMask, Text = text, Scale = screenScale,
                MaxWidth = screenMaxWidth, IgnoreBounds = ignoreBounds,
            });
            Mix(3);
            Mix(fontIndex);
            Mix(screenCoord.X, 1f);
            Mix(screenCoord.Y, 1f);
            Mix((int)colorMask.PackedValue);
            Mix(text);
            Mix(screenScale, 0.001f);
            Mix(screenMaxWidth, 1f);
            return false;
        }

        /// <summary>The kept calls, sent to the screen's texture in the order the controls made them.</summary>
        private static void SendCalls()
        {
            foreach (Call call in calls)
            {
                RectangleF destination = call.Destination;
                switch (call.Kind)
                {
                    case CallKind.Sprite:
                        MyRenderProxy.DrawSprite(call.Texture, ref destination, call.Source, call.Color, call.Rotation, call.IgnoreBounds, call.WaitTillLoaded,
                            Target, call.Mask, call.RotationSpeed);
                        break;
                    case CallKind.SpriteExt:
                        Vector2 right = call.Right, origin = call.Origin;
                        MyRenderProxy.DrawSpriteExt(call.Texture, ref destination, call.Source, call.Color, ref right, ref origin, call.IgnoreBounds,
                            call.WaitTillLoaded, Target, call.Mask);
                        break;
                    default:
                        MyRenderProxy.DrawString(call.Font, call.At, call.Color, call.Text, call.Scale, call.MaxWidth, call.IgnoreBounds, Target);
                        break;
                }
            }
        }

        private static string ThreadName()
        {
            Thread thread = Thread.CurrentThread;
            return $"{thread.ManagedThreadId}{(string.IsNullOrEmpty(thread.Name) ? string.Empty : " \"" + thread.Name + "\"")}";
        }

        /// <summary>
        /// Where the game processes its render messages (a worker or the render thread): the offscreen renderer took a
        /// texture's sprites (MyOffscreenRenderer.Add, from its message).
        /// </summary>
        private static void Taken(MyRenderMessageRenderOffscreenTexture rMessage)
        {
            if (rMessage?.OffscreenTexture != Target || Interlocked.Exchange(ref takenOnSaid, 1) != 0)
                return;
            Log.Info($"Wrist status: the screen's sprites reach the offscreen renderer on thread {ThreadName()} (MyOffscreenRenderer.Add, as the game processes its render messages)");
        }

        /// <summary>Render thread: the offscreen renderer hands a texture's drawing to a worker (MyOffscreenRenderer.SubmitWork, from the scene's draw).</summary>
        private static void Submitting(object data)
        {
            if (!(workMessageField?.GetValue(data) is MyRenderMessageRenderOffscreenTexture message) || message.OffscreenTexture != Target)
                return;
            if (Interlocked.Exchange(ref submittedOnSaid, 1) == 0)
                Log.Info($"Wrist status: the render thread is thread {ThreadName()} (MyOffscreenRenderer.SubmitWork hands the screen's drawing to a worker there)");
        }

        /// <summary>A render worker: the offscreen renderer draws a texture's sprites into it, on a deferred context of its own.</summary>
        private static void WorkStarting(object data)
        {
            workStart = 0;
            if (!(workMessageField?.GetValue(data) is MyRenderMessageRenderOffscreenTexture message) || message.OffscreenTexture != Target)
                return;
            workStart = Stopwatch.GetTimestamp();
            if (Interlocked.Exchange(ref drawnOnSaid, 1) == 0)
                Log.Info($"Wrist status: the screen's sprites are drawn into its texture on render worker thread {ThreadName()}, on a deferred context of its own");
        }

        private static void WorkDone()
        {
            if (workStart == 0)
                return;
            Interlocked.Add(ref renderTicks, Stopwatch.GetTimestamp() - workStart);
            Interlocked.Increment(ref renderDraws);
            workStart = 0;
        }
    }
}
