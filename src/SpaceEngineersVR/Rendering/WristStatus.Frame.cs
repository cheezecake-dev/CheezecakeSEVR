// WristStatus, per-frame update: placement of the screen on the forearm, look-at fading and the Perf log text.

using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Gui;
using Sandbox.Game.GUI;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using VRage.Game;
using VRage.Game.ObjectBuilders.Definitions;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;
using VRageRender.Messages;

namespace SpaceEngineersVR.Rendering
{
    internal static partial class WristStatus
    {
        private static void Draw()
        {
            if (broken || errors >= 3)
                return;
            if (!hookedDraw || !hookedRedirect)
            {
                broken = true;
                return;
            }
            long start = Stopwatch.GetTimestamp();
            try
            {
                Frame();
            }
            catch (Exception e)
            {
                recording = false;
                Log.Error(e, ++errors < 3 ? "Wrist status failed this frame" : "Wrist status failed again; the screen is off for this run and the HUD's bars are back");
                if (errors >= 3)
                    ReleaseTexture("the screen failed");
            }
            long spent = Stopwatch.GetTimestamp() - start;
            Interlocked.Increment(ref costFrames);
            Interlocked.Add(ref costTicks, spent);
            if (spent > Interlocked.Read(ref costMaxTicks))
                Interlocked.Exchange(ref costMaxTicks, spent);
        }

        // The Perf line: the game thread's frame (laying out and sending the sprites, the glove's material), and the render worker's
        // drawing of the sprites into the screen's texture (MyOffscreenRenderer.RenderWork, recorded on its deferred context).
        private static long costFrames, costTicks, costMaxTicks, renderDraws, renderTicks;

        /// <summary>For the Perf line (<see cref="PerfMeter"/>): what the screen cost since the last one.</summary>
        internal static string PerfText()
        {
            long frames = Interlocked.Exchange(ref costFrames, 0);
            long ticks = Interlocked.Exchange(ref costTicks, 0);
            long max = Interlocked.Exchange(ref costMaxTicks, 0);
            long draws = Interlocked.Exchange(ref renderDraws, 0);
            long drawTicks = Interlocked.Exchange(ref renderTicks, 0);
            if (!Active)
                return "; wrist status off";
            if (frames == 0)
                return "; wrist status no frames";
            double ms = 1000.0 / Stopwatch.Frequency;
            return string.Format(CultureInfo.InvariantCulture, "; wrist status {0:0.000} ms/frame avg / {1:0.000} max on the game thread, {2:0.000} ms a draw x {3:0.00} draws a frame on a render worker",
                ticks * ms / frames, max * ms, draws == 0 ? 0.0 : drawTicks * ms / draws, (double)draws / frames);
        }

        private static void Frame()
        {
            long now = Stopwatch.GetTimestamp();
            float seconds = lastFrame == 0 ? 0f : (float)Math.Min(0.1, (now - lastFrame) / (double)Stopwatch.Frequency);
            lastFrame = now;

            if (!Active)
            {
                string why = Off ? "off (debug WristStatus=0)" : !VRSettings.WristStatus ? "off (setting)" : "off (no VR)";
                State(why);
                ReleaseTexture("the screen is " + why);
                return;
            }
            MySession session = MySession.Static;
            if (session != unloaded)
                unloaded = null;
            else if (session != null)
            {
                State("not drawn: the session is unloading");
                return;
            }
            MyCharacter character = session?.LocalCharacter;
            if (character == null || character.IsDead || character.Closed)
            {
                State("not drawn: no live character");
                Restore("no live character");
                return;
            }
            Face face = FaceOf(character);
            if (face == null)
            {
                State("not drawn: the character's model is not an astronaut's with the gauntlet's screen", character.Model?.AssetName);
                Restore("the character's model has no known gauntlet screen");
                return;
            }
            uint renderObject = character.Render?.GetRenderObjectID() ?? uint.MaxValue;
            if (renderObject == uint.MaxValue)
            {
                State("not drawn: the character is not in the render");
                return;
            }
            if (!Build())
            {
                State("not drawn: the HUD has no status bars to show");
                Restore("the HUD has no status bars to show");
                return;
            }

            // Brighter while looked at: the gaze within the show angle of the screen and the screen turned to the eyes, held a
            // moment; dim again once past the hide angles for a moment. Eyes that are not the character's do not look at it.
            bool placed = TryScreen(character, face, out Vector3D centre, out Vector3 normal);
            MatrixD eyes = MyTransparentGeometry.HasCamera ? MySector.MainCamera.WorldMatrix : MatrixD.Identity;
            Vector3D toScreen = centre - eyes.Translation;
            double distance = toScreen.Length();
            bool near = placed && MyTransparentGeometry.HasCamera && distance > 0.01 && distance <= FarthestEyes;
            float gaze = 180f, faced = 180f;
            if (near)
            {
                Vector3 toward = (Vector3)(toScreen / distance);
                gaze = Degrees((Vector3)eyes.Forward, toward);
                faced = Degrees(normal, -toward);
            }
            bool want = near && (looked
                ? !(gaze > WristLook.HideGazeDegrees || faced > WristLook.HideFaceDegrees)
                : gaze <= WristLook.ShowGazeDegrees && faced <= WristLook.ShowFaceDegrees);
            if (want != looked)
            {
                lookHeld += seconds;
                if (lookHeld >= (want ? WristLook.ShowSeconds : WristLook.HideSeconds))
                {
                    looked = want;
                    lookHeld = 0f;
                    Log.Info($"Wrist status: {(looked ? "looked at, full brightness" : "looked away, dim")} (gaze {gaze:0} deg off it, its face {faced:0} deg from the eyes)");
                }
            }
            else
            {
                lookHeld = 0f;
            }
            float goal = looked ? FullLevel : DimLevel;
            float step = seconds / FadeSeconds * (FullLevel - DimLevel);
            level = level < goal ? Math.Min(goal, level + step) : Math.Max(goal, level - step);

            // As an LCD draws (MyRenderComponentScreenAreas.RenderSpritesToTexture), when what it shows has changed: the
            // sprites to the texture's name, then the offscreen renderer is asked for it, cleared with alpha 0; then the
            // glove's material is given the texture.
            string picture = GlovePicture(character, out MyTextureChange? skin);
            EnsureTexture();
            Redraw(picture, MathHelper.Clamp(level * Brightness, 0f, 1f), now);
            Swap(renderObject, skin, now);

            screenNote = $"; on the {face.Name} astronaut's gauntlet screen, {face.Width * 100f:0.#} x {face.Height * 100f:0.#} cm, drawn into {picture}"
                         + (placed ? $", {distance * 100.0:0} cm from the eyes" : ", its bone not found");
            State($"on the gauntlet's screen ({(character.IsSitting ? "seated" : "on foot")}, the {face.Name} astronaut)");
            Values(now);
        }

        private static float Degrees(Vector3 a, Vector3 b)
        {
            float la = a.Length(), lb = b.Length();
            if (!(la > 1e-6f) || !(lb > 1e-6f))
                return 180f;
            return MathHelper.ToDegrees((float)Math.Acos(MathHelper.Clamp(Vector3.Dot(a, b) / (la * lb), -1f, 1f)));
        }

        /// <summary>The character's model's screen: the male or female astronaut's, by the model's name (null: neither).</summary>
        private static Face FaceOf(MyCharacter character)
        {
            string name = character.Model?.AssetName;
            if (string.IsNullOrEmpty(name))
                return null;
            name = name.Replace('/', '\\');
            if (name.EndsWith(FemaleFace.Model, StringComparison.OrdinalIgnoreCase))
                return FemaleFace;
            if (name.EndsWith(MaleFace.Model, StringComparison.OrdinalIgnoreCase))
                return MaleFace;
            return null;
        }

        // The forearm bone's index in the character it was found in.
        private static MyCharacter boneOf;

        private static int boneIndex = -1;

        /// <summary>
        /// The screen's middle and face in the world as the arm is drawn: the bind-pose point and face skinned by
        /// <see cref="PlateBone"/> as the game skins them (the bone's skin transform, then its absolute transform, then the
        /// character's world matrix). False with no such bone.
        /// </summary>
        private static bool TryScreen(MyCharacter character, Face face, out Vector3D centre, out Vector3 normal)
        {
            centre = Vector3D.Zero;
            normal = Vector3.Up;
            var bones = character.AnimationController?.CharacterBones;
            if (bones == null || character.PositionComp == null)
                return false;
            if (!ReferenceEquals(boneOf, character) || boneIndex < 0 || boneIndex >= bones.Length || bones[boneIndex]?.Name != PlateBone)
            {
                boneOf = character;
                character.AnimationController.FindBone(PlateBone, out boneIndex);
            }
            if (boneIndex < 0 || boneIndex >= bones.Length || bones[boneIndex] == null)
                return false;
            var bone = bones[boneIndex];
            MatrixD world = (MatrixD)(bone.SkinTransform * bone.AbsoluteTransform) * character.PositionComp.WorldMatrixRef;
            centre = Vector3D.Transform(face.Centre, world);
            normal = Vector3.Normalize((Vector3)Vector3D.TransformNormal(Vector3.Cross(face.Right, face.Up), world));
            return centre.IsValid() && normal.IsValid();
        }

        /// <summary>What the wrist shows against the game's own stats: on the first frame and every 10 s while shown.</summary>
        private static void Values(long now)
        {
            if (now < nextValuesAt)
                return;
            nextValuesAt = now + 10L * Stopwatch.Frequency;
            var line = new StringBuilder("Wrist status values (wrist / game):");
            foreach (MyStatControls controls in wrist)
            {
                var definition = DefinitionField.GetValue(controls) as MyObjectBuilder_StatControls;
                if (definition?.VisibleCondition != null && !definition.VisibleCondition.Eval())
                    continue;
                if (!(BindingsField.GetValue(controls) is IEnumerable bindings))
                    continue;
                foreach (object binding in bindings)
                {
                    if (!BindingFields(binding))
                        continue;
                    var style = styleField.GetValue(binding) as MyObjectBuilder_StatVisualStyle;
                    var control = controlField.GetValue(binding) as IMyStatControl;
                    if (!(style is MyObjectBuilder_TextStatVisualStyle text) || control == null || style.StatId == MyStringHash.NullOrEmpty)
                        continue;
                    if (text.Text == null || !text.Text.Contains("{STAT}"))
                        continue;
                    IMyHudStat stat = MyHud.Stats.GetStat(style.StatId);
                    if (stat == null)
                        continue;
                    line.Append(' ').Append(style.StatId.String).Append(' ').Append(control.StatString).Append(" / ").Append(stat.GetValueString())
                        .Append(" (").Append(stat.CurrentValue.ToString("0.###", CultureInfo.InvariantCulture)).Append(" of ")
                        .Append(stat.MaxValue.ToString("0.###", CultureInfo.InvariantCulture)).Append(");");
                }
            }
            IMyHudStat air = MyHud.Stats.GetStat(EnvOxygen);
            if (air != null)
                line.Append(" air outside ").Append(air.CurrentValue.ToString("0.##", CultureInfo.InvariantCulture));
            line.Append($"; HUD bars {(HudHidden ? "left out" : "shown")}; brightness {level * Brightness:0.00}{screenNote}");
            if (countFrom != 0)
                line.Append(string.Format(CultureInfo.InvariantCulture, "; redrawn {0} times in {1:0.0} s, over {2} frames",
                    redraws, (now - countFrom) / (double)Stopwatch.Frequency, redrawFrames));
            redraws = 0;
            redrawFrames = 0;
            countFrom = now;
            Log.Info(line.ToString());
        }
    }
}
