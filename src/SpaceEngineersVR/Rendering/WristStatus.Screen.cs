// WristStatus, the screen's texture: the offscreen picture, its redraw, and the glove material that shows it.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Sandbox;
using Sandbox.Definitions;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GUI;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Game.ObjectBuilders.Definitions;
using VRage.Utils;
using VRageMath;
using VRageRender;
using VRageRender.Messages;

namespace SpaceEngineersVR.Rendering
{
    internal static partial class WristStatus
    {
        /// <summary>The screen's texture, made as the game makes an LCD's (MyRenderComponentScreenAreas.CreateTexture).</summary>
        private static void EnsureTexture()
        {
            if (textureMade)
                return;
            MyRenderProxy.CreateGeneratedTexture(Target, TextureSize, TextureSize, MyGeneratedTextureType.RGBA, 1, null, generateMipmaps: true, immediatelyReady: false);
            textureMade = true;
            drawnOnce = false;
            Log.Info($"Wrist status: texture made ({Target}, {TextureSize} x {TextureSize}, RGBA: R8G8B8A8 sRGB with mipmaps, as an LCD's)");
        }

        /// <summary>
        /// The screen's texture, released as the game releases an LCD's (MyRenderComponentScreenAreas.ReleaseTexture): the
        /// glove is given its own picture back first, as the game puts another texture on a screen before it destroys one.
        /// </summary>
        private static void ReleaseTexture(string why)
        {
            Restore(why);
            if (!textureMade)
                return;
            textureMade = false;
            drawnOnce = false;
            MyRenderProxy.DestroyGeneratedTexture(Target);
            Log.Info($"Wrist status: texture released ({why})");
        }

        // Game thread: the render object whose left glove shows the texture (uint.MaxValue: none), the skin's change for that
        // glove it was sent with (its own textures go back with it), and when it is sent again.
        private static uint swappedOn = uint.MaxValue;

        private static MyTextureChange? swappedSkin;

        private static long resendAt;

        // The last model whose glove picture was looked up, and that picture (null: the model does not say).
        private static object pictureModel;

        private static string pictureOfModel;

        /// <summary>
        /// The glove's colour-metal picture the screen is drawn into: the skin's for the left glove if the character wears one
        /// that changes it (the game's skins change the glove's textures by its material, as the wrist does), else the model's
        /// own, read from its LeftGlove material. <paramref name="skin"/> is the skin's whole change for the glove, if any.
        /// </summary>
        private static string GlovePicture(MyCharacter character, out MyTextureChange? skin)
        {
            skin = null;
            if (character.Components.TryGet(out MyAssetModifierComponent modifiers) && modifiers.AssetModifiers != null)
            {
                foreach (MyDefinitionId id in modifiers.AssetModifiers)
                {
                    Dictionary<MyStringId, MyTextureChange> changes = MyDefinitionManager.Static.GetAssetModifierDefinitionForRender(id.SubtypeId).SkinTextureChanges;
                    if (changes != null && changes.TryGetValue(GloveMaterialId, out MyTextureChange change))
                        skin = change;
                }
            }
            if (skin?.ColorMetalFileName != null)
                return skin.Value.ColorMetalFileName;

            var model = character.Model;
            if (!ReferenceEquals(model, pictureModel))
            {
                pictureModel = model;
                pictureOfModel = null;
                var meshes = model?.GetMeshList();
                if (meshes != null)
                {
                    foreach (var mesh in meshes)
                    {
                        if (mesh?.Material?.Name == GloveMaterial && mesh.Material.Textures != null
                            && mesh.Material.Textures.TryGetValue("ColorMetalTexture", out string path) && !string.IsNullOrEmpty(path))
                        {
                            pictureOfModel = path;
                            break;
                        }
                    }
                }
            }
            return pictureOfModel ?? GloveTexture;
        }

        /// <summary>
        /// The character's left glove shows the texture: its material's colour-metal texture changed to it, as the game puts an
        /// LCD's texture on a block's screen (MyRenderProxy.ChangeMaterialTexture), the skin's other textures for the glove
        /// kept. Sent when the render object or the skin changes, and again every <see cref="ResendSeconds"/>.
        /// </summary>
        private static void Swap(uint renderObject, MyTextureChange? skin, long now)
        {
            bool sameSkin = Nullable.Equals(skin, swappedSkin);
            if (renderObject == swappedOn && sameSkin && now < resendAt)
                return;
            if (renderObject != swappedOn || !sameSkin)
            {
                if (swappedOn != uint.MaxValue && renderObject != swappedOn)
                    Restore("the character's render object changed");
                Log.Info($"Wrist status: on the gauntlet's screen (the {GloveMaterial} material of render object {renderObject}{(skin.HasValue ? ", the skin's textures for it kept" : "")})");
            }
            MyRenderProxy.ChangeMaterialTexture(renderObject, GloveMaterial, Target, skin?.NormalGlossFileName, skin?.ExtensionsFileName, skin?.AlphamaskFileName);
            swappedOn = renderObject;
            swappedSkin = skin;
            resendAt = now + (long)(ResendSeconds * Stopwatch.Frequency);
        }

        /// <summary>The glove's own textures back (the skin's, if one changes it), as the game takes a skin off.</summary>
        private static void Restore(string why)
        {
            if (swappedOn == uint.MaxValue)
                return;
            if (swappedSkin is MyTextureChange skin)
                MyRenderProxy.ChangeMaterialTexture(swappedOn, GloveMaterial, skin.ColorMetalFileName, skin.NormalGlossFileName, skin.ExtensionsFileName, skin.AlphamaskFileName);
            else
                MyRenderProxy.ChangeMaterialTexture(swappedOn, GloveMaterial);
            Log.Info($"Wrist status: the gauntlet's screen has its own picture back ({why})");
            swappedOn = uint.MaxValue;
            swappedSkin = null;
        }

        // At most this many redraws a second, like a game LCD; each is the offscreen renderer's whole job, see Redraw.
        private const int RedrawsPerSecond = 10;

        // A redraw at least this often even when nothing shown changed (see Redraw).
        private const long KeepAliveSeconds = 5;

        // Game thread: whether the texture holds a picture (since it was made), the signature of that picture, and when it was
        // sent; redraws and frames counted for the values line since its last one, and in the screen's first minute.
        private static bool drawnOnce, minuteSaid;

        private static ulong drawnSignature;

        private static long drawnAt, minuteStart, countFrom;

        private static int redraws, redrawFrames, minuteRedraws, minuteFrames;

        /// <summary>
        /// The texture's picture, sent again only when what it shows has changed, at most <see cref="RedrawsPerSecond"/>
        /// times a second (a change waits for the next allowed frame, so none is lost). Each frame the wrist's controls are
        /// drawn into a list instead of sent (<see cref="Record"/>), and a signature is made of what they would draw, to the
        /// texture's pixel and the colour's 8 bits, with the glove's picture, the plate's opacity and the dimming: a bar that
        /// moves less than a pixel, or a value whose text and bar look the same, draws nothing new. A redraw is the whole
        /// texture: the game's offscreen renderer cannot draw part of one. MyOffscreenRenderer.RenderWork hands the sprites to
        /// MyRender11.DrawSpritesOffscreen, which clears the whole render target first (ClearRtv, with the clear colour, or 0
        /// without one), and then makes all the texture's mips again (GenerateMips); and a texture it finds without a render
        /// target view is reset and cleared on the render thread (SubmitWork). So the glove's picture is drawn in again with
        /// each redraw, under the screen.
        /// </summary>
        private static void Redraw(string picture, float shade, long now)
        {
            float opacity = MySandboxGame.Config?.HUDBkOpacity ?? 0.6f;
            Color plate = MyGuiControlBase.ApplyColorMaskModifiers(Vector4.One, true, opacity);
            int dim = (int)Math.Round((1f - shade) * 255f);
            signature = 14695981039346656037UL;
            Mix(picture);
            Mix((int)plate.PackedValue);
            Mix(dim);
            Record(opacity);

            if (minuteStart == 0)
                minuteStart = now;
            if (countFrom == 0)
                countFrom = now;
            redrawFrames++;
            minuteFrames++;
            // Also every KeepAliveSeconds when nothing changed, so a texture the renderer cleared (a device reset, or one it
            // found without a render target view) or a sprite drawn before its texture loaded is not left on the screen.
            bool send = !drawnOnce || (signature != drawnSignature && now - drawnAt >= Stopwatch.Frequency / RedrawsPerSecond)
                        || now - drawnAt >= KeepAliveSeconds * Stopwatch.Frequency;
            if (send)
            {
                Send(picture, plate, dim);
                drawnOnce = true;
                drawnSignature = signature;
                drawnAt = now;
                redraws++;
                minuteRedraws++;
            }
            if (!minuteSaid && now - minuteStart >= 60L * Stopwatch.Frequency)
            {
                minuteSaid = true;
                Log.Info($"Wrist status: the screen was redrawn {minuteRedraws} times in its first minute, over {minuteFrames} frames (at most {RedrawsPerSecond} a second, each the whole {TextureSize} x {TextureSize} texture)");
            }
        }

        /// <summary>
        /// The wrist's controls drawn into <see cref="calls"/>, laid out on the screen's part of the texture, and their
        /// signature mixed into <see cref="signature"/>; nothing is sent.
        /// </summary>
        private static void Record(float opacity)
        {
            // The inventory row and the line on the outside sit under the last bar: one row higher when the game shows no food bar.
            var origin = new Vector2(ScreenPixels.X, ScreenPixels.Y);
            bool food = foodShown == null || foodShown.Eval();
            if (lower != null)
            {
                Vector2 at = origin + new Vector2(0f, food ? 0f : -RowStep * PixelsPerUnit);
                if (lower.Position != at)
                    lower.Position = at;
            }

            calls.Clear();
            recording = true;
            try
            {
                foreach (MyStatControls controls in wrist)
                {
                    if (controls != lower && controls.Position != origin)
                        controls.Position = origin;
                    controls.RegisterAlphaMultiplier(VisualStyleCategory.Background, opacity);
                    controls.Draw(1f, 1f);
                }
            }
            finally
            {
                recording = false;
            }
        }

        /// <summary>
        /// The picture, to the wrist's target, and the offscreen renderer asked to draw it: the glove's colour-metal picture,
        /// all of it; the screen's panel painted the bezel's grey and its lit part black; the HUD's left plate there at the
        /// HUD's background opacity (as MyStatControlImage.Draw colours it: the colour mask times the alpha, all four
        /// channels); the controls' recorded sprites and texts over it; then the lit part darkened by <paramref name="dim"/>
        /// of 255.
        /// </summary>
        private static void Send(string picture, Color plate, int dim)
        {
            var whole = new RectangleF(0f, 0f, TextureSize, TextureSize);
            MyRenderProxy.DrawSprite(picture, ref whole, null, Color.White, 0f, true, true, Target);
            Sprite(PanelPixels, Bezel);
            Sprite(ScreenPixels, Color.Black);
            RectangleF screen = ScreenPixels;
            MyRenderProxy.DrawSprite(PlateTexture, ref screen, PlateSource, plate, 0f, true, true, Target);
            SendCalls();
            if (dim > 0)
                Sprite(ScreenPixels, new Color(0, 0, 0, dim));
            MyRenderProxy.RenderOffscreenTexture(Target, Vector2.One, Clear);
            if (!sentFromSaid)
            {
                sentFromSaid = true;
                Log.Info($"Wrist status: the screen's sprites are sent from thread {ThreadName()} (the game thread)");
            }
        }

        private static void Sprite(RectangleF rectangle, Color color) =>
            MyRenderProxy.DrawSprite(BlankTexture, ref rectangle, null, color, 0f, true, true, Target);

        /// <summary>A line when the screen's state changes; <paramref name="detail"/> is said with it, and is no change on its own.</summary>
        private static void State(string text, string detail = null)
        {
            if (text == lastState)
                return;
            lastState = text;
            Log.Info("Wrist status: " + text + (detail == null ? string.Empty : ", " + detail));
        }

        /// <summary>The fields of MyStatControls' StatBinding (a private class): the style and the control it made.</summary>
        private static bool BindingFields(object binding)
        {
            if (styleField == null && binding != null)
            {
                styleField = binding.GetType().GetField("Style", BindingFlags.Public | BindingFlags.Instance);
                controlField = binding.GetType().GetField("Control", BindingFlags.Public | BindingFlags.Instance);
            }
            return styleField != null && controlField != null;
        }
    }
}
