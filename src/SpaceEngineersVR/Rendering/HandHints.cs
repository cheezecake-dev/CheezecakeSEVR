// HandHints: small labels beside each hand naming what its buttons do now, in the world, until the player has learned them.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using HarmonyLib;
using Sandbox.Game.World;
using Sandbox.Graphics;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Input.Bindings;
using SpaceEngineersVR.Tracking;
using VRage.Generics;
using VRage.Utils;
using VRageMath;
using VRageRender;
using VRageRender.Messages;
using MyTransparentGeometry = VRage.Game.MyTransparentGeometry;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// Look at a hand and a small plate beside it names what each of its controls does in what you are doing now (walking,
    /// the jetpack, a block in hand, a seat, a turret, the tools wheel, a menu: <see cref="ControlGuide"/>), each control by
    /// the name it has on the controller in that hand (<see cref="Input.Bindings.ActionNames"/>). After the plugin's own lines
    /// come those of the actions other plugins and mods register (<see cref="ControlGuide.Outside"/>), each while its action
    /// is offered (<see cref="Api.ApiActions.Offered"/>), as far as the plate's rows go. A control's line fades a step each time
    /// it is used there and is gone after <see cref="LearnedAfter"/> uses; when all of a hand's lines are learned its plate no
    /// longer shows. Options > VR > Interface > Prompts: <see cref="VRSettings.HandHints"/> (Off, Until learned, Always) and a
    /// button that shows them all again. What has been learned is kept in the settings file (<see cref="VRSettings.HandHintsLearned"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The plate is in the world, as the wrist's status screen is: the game's own HUD fonts drawn by the game's offscreen sprite
    /// renderer into a texture of our own (MyRenderProxy.CreateGeneratedTexture, as an LCD's; drawn when what it shows changes,
    /// at most <see cref="RedrawsPerSecond"/> a second, ahead of the game's background work: <see cref="OffscreenPriority"/>),
    /// shown on a billboard of our own material (<see cref="PictureMaterial"/>)
    /// beside the controller, upright and turned to the eyes, after tone mapping (LDR), depth
    /// tested so the hands and the world pass in front of it. It rides with the character (<see cref="MotionExtrapolation"/>).
    /// </para>
    /// <para>
    /// Looked at: the plate's middle within <see cref="LookDegrees"/> of the gaze and nearer than <see cref="LookMetres"/>; it fades
    /// in and out over <see cref="FadeSeconds"/>. So the labels are there when the player looks down at a controller to find a
    /// button, and out of the way otherwise. The game's HUD reminders (<see cref="ControlHints"/>) say to look.
    /// </para>
    /// </remarks>
    internal static class HandHints
    {
        /// <summary>Debug: no labels (<see cref="RenderDebug"/> HandHints=0).</summary>
        public static bool Off { get; set; }

        /// <summary>Debug: the labels shown whether or not the hand is looked at (<see cref="RenderDebug"/> HandHintsAlways=1), for a shot.</summary>
        public static bool ShowUnlooked { get; set; }

        /// <summary>The settings' choices, in order.</summary>
        public static readonly string[] ModeNames = { "Off", "Until learned", "Always" };

        public const int ModeOff = 0, ModeUntilLearned = 1, ModeAlways = 2;

        /// <summary>A control's line is learned, and left out, once it has been used this many times in its situation.</summary>
        public const int LearnedAfter = 3;

        private const float LookDegrees = 30f, LookMetres = 1.0f, FadeSeconds = 0.25f;

        /// <summary>The plate's width, metres, and how far its middle stands out from the controller and up from it: clear of the glove and a held tool, which would hide it.</summary>
        private const float Width = 0.12f, Aside = 0.125f, Above = 0.035f;

        private const int TextureSide = 512, Rows = 8, RedrawsPerSecond = 10;
        private const float RowPixels = TextureSide / (float)Rows;

        /// <summary>A line's text is this share of its row's height.</summary>
        private const float TextShare = 0.62f;

        /// <summary>
        /// The smallest text the plugin gives the world and means to be read: a plate's line, <see cref="Width"/> over <see cref="Rows"/>
        /// rows of <see cref="TextShare"/>, at the farthest the plate is looked at from (<see cref="LookMetres"/>); the angle it
        /// subtends there, radians. The wrist panel's Controls page takes it as the least its text may subtend (<see cref="Gui.ControlsLayout.FloorRow"/>).
        /// </summary>
        internal const float LegibleTextRadians = Width / Rows * TextShare / LookMetres;

        /// <summary>A stick pushed this far counts as the stick used.</summary>
        private const float StickUsed = 0.7f;

        /// <summary>The settings file is written at most this often while lines are being learned, seconds.</summary>
        private const double SaveSeconds = 10.0;

        private static readonly string[] TextureNames = { "SEVRHandHintsLeft", "SEVRHandHintsRight" };
        private static readonly MyStringId[] MaterialIds = { MyStringId.GetOrCompute("SEVRHandHintsLeft"), MyStringId.GetOrCompute("SEVRHandHintsRight") };

        private static readonly Color Plate = new Color(9, 15, 22, 205);
        private static readonly Color KeyColour = new Color(192, 237, 249), WhatColour = new Color(232, 238, 242), TitleColour = new Color(140, 160, 172);

        private static readonly FeatureGuard guard = new FeatureGuard(nameof(HandHints));
        private static bool materialsAdded;

        // Game thread: the situation and its lines now, the learning counts (by ControlGuide.Line.Key), the sticks last frame.
        private static ControlGuide.Situation situation;
        private static bool haveSituation;
        private static List<ControlGuide.Line> lines = new List<ControlGuide.Line>();
        // What the lines were made for: made again only when one of these changes, not every frame.
        private static (ControlGuide.Situation Where, bool Swapped, Hand Dominant, bool ZeroG, bool Wrist, bool SprintMirrored, bool ReloadMirrored, int Names) linesFor;
        private static bool haveLines;
        // The outside actions' lines (SevrApi), after the plugin's own, and what they were made for: the situation, where the
        // player is for the bindings (an overlay such as a container in reach is not a situation of its own), the action table,
        // the bindings in use and the hand. Made again only when one of these changes.
        private static List<ControlGuide.Line> outside = new List<ControlGuide.Line>();
        private static (ControlGuide.Situation Where, BindingContext Contexts, int Table, int Names, Hand Dominant) outsideFor;
        private static bool haveOutside;
        private static readonly bool[] stickNow = new bool[2];
        private static readonly Dictionary<string, int> uses = new Dictionary<string, int>();
        private static string usesLoadedFrom;
        private static readonly bool[] stickOut = new bool[2];
        private static bool dirty;
        private static long savedAt;

        // Game thread: each hand's plate (fade, texture, what it shows).
        private static readonly HandPlate[] plates = { new HandPlate(Hand.Left), new HandPlate(Hand.Right) };

        private sealed class HandPlate
        {
            public HandPlate(Hand hand) => Hand = hand;

            public readonly Hand Hand;
            public float Alpha;
            public bool TextureMade, Drawn;
            // The lines shown this frame, and the ones the texture holds (with their situation): compared, not rebuilt as text.
            public readonly List<(string Name, string What, float Strength)> Shown = new List<(string, string, float)>(Rows);
            public readonly List<(string Name, string What, float Strength)> DrawnLines = new List<(string, string, float)>(Rows);
            public ControlGuide.Situation DrawnSituation;
            public long DrawnAt;
            public int RowsUsed;
            // What the log last said this plate shows (its situation and lines, not how faded): logged again when that changes.
            public string Logged;

            // The last frame's plate: how far from the eyes, how far off the gaze, and where in the eyes' frame (metres, + right, + up, + ahead).
            public double Distance, OffGaze;
            public Vector3D InView;
            public bool Placed;
        }

        /// <summary>At start-up with the renderer's features, no world drawn: the two plates' materials.</summary>
        public static void Register()
        {
            for (int h = 0; h < 2; h++)
            {
                PictureMaterial.Add(MaterialIds[h], TextureNames[h], 0.0005f);
                OffscreenPriority.Register(TextureNames[h]); // drawn as soon as the hands show, not after the load's background work
            }
            materialsAdded = true;
            Log.Info("Hand hints: transparent materials registered");
        }

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(MySession), nameof(MySession.DrawSync)),
                postfix: new HarmonyMethod(typeof(HandHints), nameof(Draw)));
            MySession.OnUnloading += () => Release("the world is unloading");
            Log.Info(materialsAdded ? "Hand hints: labels beside the hands" : "Hand hints: the materials are not registered; no labels");
        }

        /// <summary>The labels are on (the setting and the debug file), so the HUD's reminders may point to them.</summary>
        public static bool On => !Off && VRSettings.HandHints != ModeOff;

        /// <summary>The labels can show: VR's hands, the setting, the debug file.</summary>
        private static bool Wanted => !Off && materialsAdded && VRInput.Layout && VRSettings.HandHints != ModeOff;

        // ---- Learning (game thread, from VRControls once a frame) ----

        /// <summary>
        /// Game thread, once a frame after the controls are worked out: what the hands are doing (<paramref name="context"/>,
        /// a menu, the tools wheel), so the labels follow it, and each control used counted toward its line being learned.
        /// </summary>
        public static void Update(VRContext context, bool menu, bool wheel)
        {
            if (!guard.On)
                return;
            // Off (or no VR hands): nothing to follow or count.
            if (!Wanted)
            {
                haveSituation = haveLines = haveOutside = false;
                lines.Clear();
                outside.Clear();
                return;
            }
            try
            {
                Learn(context, menu, wheel);
            }
            catch (Exception e)
            {
                guard.Failed(e, "Hand hints could not follow the controls; no labels meanwhile");
            }
        }

        private static void Learn(VRContext context, bool menu, bool wheel)
        {
            LoadUses();
            if (context == VRContext.None || context == VRContext.Spectator)
            {
                haveSituation = haveLines = haveOutside = false;
                lines.Clear();
                outside.Clear();
                return;
            }
            ControlGuide.Situation now = ControlGuide.Now(context, menu, wheel, Hands.BuildMode.Active);
            if (!haveSituation || now != situation)
            {
                situation = now;
                haveSituation = true;
                stickOut[0] = stickOut[1] = true; // a stick already out as the situation changes is the old one's
            }
            bool onFoot = context == VRContext.OnFoot || context == VRContext.Jetpack;
            var want = (now, Hands.ItemSpots.TriggersSwapped && onFoot, VRSettings.DominantHand, VRSettings.ZeroGTurn != VRSettings.ZeroGTurnOff,
                WristButton.Enabled, ControllerBindings.MirrorsIn(VRAction.Sprint, context), ControllerBindings.MirrorsIn(VRAction.Reload, context), ActionNames.Version);
            if (!haveLines || want != linesFor)
            {
                lines = ControlGuide.For(want.Item1, want.Item2, want.Item3, want.Item4, want.Item5, want.Item6, want.Item7);
                linesFor = want;
                haveLines = true;
            }
            var wantOutside = (now, ControllerBindings.Contexts, VRActions.Version, ActionNames.Version, VRSettings.DominantHand);
            if (!haveOutside || wantOutside != outsideFor)
            {
                outside = ControlGuide.Outside(now, wantOutside.Item2);
                outsideFor = wantOutside;
                haveOutside = true;
            }

            for (int h = 0; h < 2; h++)
                stickNow[h] = VRInput.Get((Hand)h).Stick.Length() >= StickUsed;
            Count(lines, outsideOnly: false);
            Count(outside, outsideOnly: true);
            stickOut[0] = stickNow[0];
            stickOut[1] = stickNow[1];

            long ticks = Stopwatch.GetTimestamp();
            if (dirty && ticks - savedAt > SaveSeconds * Stopwatch.Frequency)
            {
                dirty = false;
                savedAt = ticks;
                StoreUses();
                VRSettings.Save();
            }
        }

        /// <summary>Each line whose control was used this frame counts a use toward its being learned (an outside action's only while it is offered).</summary>
        private static void Count(List<ControlGuide.Line> those, bool outsideOnly)
        {
            foreach (ControlGuide.Line line in those)
            {
                if (line.Name == null || outsideOnly && !Api.ApiRuntime.Actions.Offered(line.Action))
                    continue;
                bool used = line.Control == ControllerControl.Thumbstick
                    ? stickNow[(int)line.Hand] && !stickOut[(int)line.Hand]
                    : ControllerBindings.NewlyDown(line.Hand, line.Control);
                if (!used)
                    continue;
                string key = line.Key;
                uses.TryGetValue(key, out int count);
                if (count >= LearnedAfter)
                    continue;
                uses[key] = ++count;
                if (count == LearnedAfter)
                {
                    dirty = true;
                    Log.Info($"Hand hints: {line.QualifiedName} ({line.What}) learned {Situation(situation)}");
                }
            }
        }

        private static string Situation(ControlGuide.Situation where) => "(" + ControlGuide.Title(where).ToLowerInvariant() + ")";

        /// <summary>A line's uses so far, 0 to <see cref="LearnedAfter"/>.</summary>
        internal static int UsesOf(string key) => uses.TryGetValue(key, out int n) ? n : 0;

        /// <summary>Forgets what has been learned, so every label shows again (the options' button), and writes that to the settings file at once.</summary>
        public static void ShowAgain()
        {
            uses.Clear();
            dirty = false;
            VRSettings.HandHintsLearned = string.Empty;
            usesLoadedFrom = string.Empty;
            VRSettings.Save();
            Log.Info("Hand hints: all labels shown again");
        }

        /// <summary>The counts from the settings file, when it holds other ones than were read (start-up, Defaults, Undo).</summary>
        private static void LoadUses()
        {
            string text = VRSettings.HandHintsLearned ?? string.Empty;
            if (text == usesLoadedFrom)
                return;
            usesLoadedFrom = text;
            uses.Clear();
            foreach (string entry in text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = entry.IndexOf('=');
                if (eq > 0 && int.TryParse(entry.Substring(eq + 1), out int n) && n > 0)
                    uses[entry.Substring(0, eq).Trim()] = Math.Min(n, LearnedAfter);
            }
        }

        /// <summary>The learned lines into the settings, as "situation.hand.button=uses" separated by semicolons.</summary>
        private static void StoreUses()
        {
            var text = new StringBuilder();
            foreach (KeyValuePair<string, int> pair in uses)
            {
                if (pair.Value < LearnedAfter)
                    continue;
                if (text.Length > 0)
                    text.Append(';');
                text.Append(pair.Key).Append('=').Append(pair.Value);
            }
            usesLoadedFrom = text.ToString();
            VRSettings.HandHintsLearned = usesLoadedFrom;
        }

        /// <summary>How strongly a line shows: whole, then a step fainter with each use; 0 once learned (or with Always, whole).</summary>
        internal static float LineStrength(int count, bool always) =>
            always ? 1f : count >= LearnedAfter ? 0f : 1f - 0.25f * count;

        // ---- Drawing (game thread, as the frame is drawn) ----

        /// <summary>Postfix of MySession.DrawSync: each hand's plate where it is this frame.</summary>
        private static void Draw()
        {
            if (!guard.On)
                return;
            try
            {
                Frame();
            }
            catch (Exception e)
            {
                guard.Failed(e, "Hand hints could not be drawn; no labels meanwhile");
            }
        }

        private static long lastFrame;

        private static void Frame()
        {
            long now = Stopwatch.GetTimestamp();
            float seconds = lastFrame == 0 ? 0f : (float)Math.Min(0.1, (now - lastFrame) / (double)Stopwatch.Frequency);
            lastFrame = now;
            if (!Wanted || !haveSituation || !MyTransparentGeometry.HasCamera)
            {
                foreach (HandPlate plate in plates)
                    plate.Alpha = 0f;
                return;
            }
            MatrixD eyes = MySector.MainCamera.WorldMatrix;
            bool always = VRSettings.HandHints == ModeAlways;
            foreach (HandPlate plate in plates)
            {
                List<(string Name, string What, float Strength)> shown = plate.Shown;
                Shown(plate.Hand, always, shown);
                bool placed = HandWorld.TryGet(plate.Hand, out MatrixD grip, out _);
                plate.Placed = placed;
                Vector3D middle = Vector3D.Zero;
                bool looked = false;
                if (placed && shown.Count > 0)
                {
                    Vector3D side = plate.Hand == Hand.Left ? -eyes.Right : eyes.Right;
                    middle = grip.Translation + side * Aside + eyes.Up * Above;
                    Vector3D toPlate = middle - eyes.Translation;
                    double distance = toPlate.Length();
                    double offGaze = distance > 0.0 ? MathHelper.ToDegrees(Math.Acos(MathHelper.Clamp(Vector3D.Dot(toPlate / distance, eyes.Forward), -1.0, 1.0))) : 180.0;
                    plate.Distance = distance;
                    plate.OffGaze = offGaze;
                    plate.InView = new Vector3D(Vector3D.Dot(toPlate, eyes.Right), Vector3D.Dot(toPlate, eyes.Up), Vector3D.Dot(toPlate, eyes.Forward));
                    looked = ShowUnlooked || (distance > 0.05 && distance < LookMetres && offGaze < LookDegrees);
                }
                float step = seconds / FadeSeconds;
                plate.Alpha = looked ? Math.Min(1f, plate.Alpha + step) : Math.Max(0f, plate.Alpha - step);
                if (plate.Alpha <= 0f || !placed || shown.Count == 0)
                    continue;
                Redraw(plate, shown, now);
                if (plate.Drawn)
                    AddPlate(plate, middle, eyes);
            }
        }

        /// <summary>
        /// The lines a hand's plate shows now, into <paramref name="shown"/>: the controls its controller has, not yet learned (or
        /// all, with Always); the plugin's own first, then the outside actions offered now, as far as the rows go.
        /// </summary>
        private static void Shown(Hand hand, bool always, List<(string Name, string What, float Strength)> shown)
        {
            shown.Clear();
            AddShown(lines, hand, always, shown, outsideOnly: false);
            AddShown(outside, hand, always, shown, outsideOnly: true);
        }

        private static void AddShown(List<ControlGuide.Line> those, Hand hand, bool always, List<(string Name, string What, float Strength)> shown, bool outsideOnly)
        {
            foreach (ControlGuide.Line line in those)
            {
                if (shown.Count >= Rows - 1)
                    return;
                if (line.Hand != hand)
                    continue;
                string name = line.Name;
                if (name == null || outsideOnly && !Api.ApiRuntime.Actions.Offered(line.Action))
                    continue;
                float strength = LineStrength(UsesOf(line.Key), always);
                if (strength > 0f)
                    shown.Add((name, line.What, strength));
            }
        }

        /// <summary>The plate's picture, sent again only when what it shows changed, at most <see cref="RedrawsPerSecond"/> a second.</summary>
        private static void Redraw(HandPlate plate, List<(string Name, string What, float Strength)> shown, long now)
        {
            if (plate.Drawn && (Same(plate, shown) || now - plate.DrawnAt < Stopwatch.Frequency / RedrawsPerSecond))
                return;

            string target = TextureNames[(int)plate.Hand];
            if (!plate.TextureMade)
            {
                MyRenderProxy.CreateGeneratedTexture(target, TextureSide, TextureSide, MyGeneratedTextureType.RGBA, 1, null, generateMipmaps: true, immediatelyReady: false);
                plate.TextureMade = true;
                Log.Info($"Hand hints: texture made ({target}, {TextureSide} x {TextureSide})");
            }

            float pad = RowPixels * 0.3f;
            Text(target, "White", ControlGuide.Title(situation), pad, RowPixels * 0.18f, TextureSide - 2f * pad, RowPixels * 0.5f, TitleColour, MyRenderTextAlignmentEnum.Align_Left);
            float keyWidth = TextureSide * 0.34f;
            for (int i = 0; i < shown.Count; i++)
            {
                float y = RowPixels * (i + 1) + RowPixels * 0.12f;
                Text(target, "White", shown[i].Name, pad, y, keyWidth - pad, RowPixels * TextShare, MultiplyRgb(KeyColour, shown[i].Strength), MyRenderTextAlignmentEnum.Align_Right);
                Text(target, "White", shown[i].What, keyWidth + pad, y, TextureSide - keyWidth - 2f * pad, RowPixels * TextShare, MultiplyRgb(WhatColour, shown[i].Strength), MyRenderTextAlignmentEnum.Align_Left);
            }
            MyRenderProxy.RenderOffscreenTexture(target, Vector2.One, Plate);
            plate.Drawn = true;
            plate.DrawnSituation = situation;
            plate.DrawnLines.Clear();
            plate.DrawnLines.AddRange(shown);
            plate.DrawnAt = now;
            plate.RowsUsed = shown.Count + 1;
            // Each time the plate shows another situation or other lines (a line fading as it is learned is not news), so a run's
            // log says what the player could read, wherever the plate went.
            var said = new StringBuilder(ControlGuide.Title(situation));
            foreach ((string name, string what, float _) in shown)
                said.Append('|').Append(name).Append('~').Append(what);
            string key = said.ToString();
            if (plate.Logged != key)
            {
                plate.Logged = key;
                var text = new StringBuilder(ControlGuide.Title(situation));
                foreach ((string name, string what, float strength) in shown)
                    text.Append('|').Append(name).Append('~').Append(what).Append('~').Append(strength.ToString("0.00"));
                Log.Info($"Hand hints: {plate.Hand} hand's labels shown {Situation(situation)}, controller {ActionNames.Controller} ({ControllerBindings.HandProfile(plate.Hand) ?? "no profile yet"}): {text}");
            }
        }

        /// <summary>The texture already shows these lines in this situation.</summary>
        private static bool Same(HandPlate plate, List<(string Name, string What, float Strength)> shown)
        {
            if (plate.DrawnSituation != situation || plate.DrawnLines.Count != shown.Count)
                return false;
            for (int i = 0; i < shown.Count; i++)
            {
                (string name, string what, float strength) = shown[i];
                (string drawnName, string drawnWhat, float drawnStrength) = plate.DrawnLines[i];
                if (name != drawnName || what != drawnWhat || strength != drawnStrength)
                    return false;
            }
            return true;
        }

        private static Color MultiplyRgb(Color c, float k) => new Color((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k), c.A);

        private static readonly Dictionary<string, float> lineHeights = new Dictionary<string, float>();

        /// <summary>Text <paramref name="px"/> high in a box, smaller (to 70 %) and then cut short with ".." if it does not fit (as the touch toolbar's).</summary>
        private static void Text(string target, string font, string text, float x, float y, float width, float px, Color color, MyRenderTextAlignmentEnum align)
        {
            if (string.IsNullOrEmpty(text))
                return;
            if (!lineHeights.TryGetValue(font, out float line))
                lineHeights[font] = line = Math.Max(1f, MyGuiManager.MeasureStringRaw(font, "0", 1f).Y);
            float scale = px / line;
            float measured = MyGuiManager.MeasureStringRaw(font, text, scale).X;
            if (measured > width)
            {
                scale *= Math.Max(0.7f, width / measured);
                measured = MyGuiManager.MeasureStringRaw(font, text, scale).X;
            }
            while (measured > width && text.Length > 2)
            {
                text = text.Substring(0, text.Length - (text.EndsWith("..") ? 3 : 1)).TrimEnd() + "..";
                measured = MyGuiManager.MeasureStringRaw(font, text, scale).X;
            }
            // Aligned within the box: x is its left edge whatever the alignment, as the touch toolbar draws.
            MyRenderProxy.DrawStringAligned((int)MyStringHash.GetOrCompute(font), new Vector2(x, y), color, text, scale,
                float.PositiveInfinity, false, target, (int)Math.Round(width), align);
        }

        /// <summary>The plate's billboard, upright and turned to the eyes, its used rows only, in the character's space so it rides with the body.</summary>
        private static void AddPlate(HandPlate plate, Vector3D middle, in MatrixD eyes)
        {
            MyObjectsPool<MyBillboard> pool = MyRenderProxy.BillboardsPoolWrite;
            if (pool == null)
                return;
            float share = plate.RowsUsed / (float)Rows;
            double halfWidth = Width / 2.0, halfHeight = Width * share / 2.0;
            Vector3D right = eyes.Right * halfWidth, up = eyes.Up * halfHeight;
            Vector3D p0 = middle - right + up, p1 = middle + right + up, p2 = middle + right - up, p3 = middle - right - up;
            bool ride = MotionExtrapolation.TryRide(out uint parent, out MatrixD worldToLocal);
            if (ride)
            {
                p0 = Vector3D.Transform(p0, worldToLocal);
                p1 = Vector3D.Transform(p1, worldToLocal);
                p2 = Vector3D.Transform(p2, worldToLocal);
                p3 = Vector3D.Transform(p3, worldToLocal);
            }

            pool.AllocateOrCreate(out MyBillboard b);
            b.Material = MaterialIds[(int)plate.Hand];
            b.BlendType = MyBillboard.BlendTypeEnum.LDR;
            // The renderer takes the texture's corners in this order: top left, top right, bottom right, bottom left.
            b.Position0 = p0;
            b.Position1 = p1;
            b.Position2 = p2;
            b.Position3 = p3;
            b.UVOffset = Vector2.Zero;
            b.UVSize = new Vector2(1f, share);
            b.Color = new Vector4(plate.Alpha);
            b.ColorIntensity = 1f;
            b.SoftParticleDistanceScale = 1f;
            b.LocalType = MyBillboard.LocalTypeEnum.Custom;
            b.ParentID = ride ? parent : uint.MaxValue;
            b.DistanceSquared = (float)Vector3D.DistanceSquared(eyes.Translation, middle);
            b.Reflectivity = 0f;
            b.AlphaCutout = 0f;
            b.CustomViewProjection = -1;
            MyRenderProxy.AddBillboard(b);
        }

        private static void Release(string why)
        {
            if (dirty)
            {
                dirty = false;
                StoreUses();
                VRSettings.Save();
            }
            foreach (HandPlate plate in plates)
            {
                plate.Alpha = 0f;
                plate.Drawn = false;
                plate.DrawnLines.Clear();
                plate.Logged = null;
                if (!plate.TextureMade)
                    continue;
                plate.TextureMade = false;
                MyRenderProxy.DestroyGeneratedTexture(TextureNames[(int)plate.Hand]);
            }
            haveSituation = haveLines = haveOutside = false;
            Log.Info($"Hand hints: textures released ({why})");
        }

        /// <summary>For the drive: the situation, and each hand's plate.</summary>
        internal static string Describe()
        {
            if (!Wanted)
                return "off";
            var s = new StringBuilder(haveSituation ? ControlGuide.Title(situation) : "no situation");
            var shown = new List<(string Name, string What, float Strength)>();
            foreach (HandPlate plate in plates)
            {
                Shown(plate.Hand, VRSettings.HandHints == ModeAlways, shown);
                s.Append($"; {plate.Hand}: {shown.Count} lines, alpha {plate.Alpha:0.00}{(plate.Drawn ? ", drawn" : "")}{(plate.Placed ? $", {plate.Distance:0.00} m away, {plate.OffGaze:0} deg off the gaze, at ({plate.InView.X:0.00}, {plate.InView.Y:0.00}, {plate.InView.Z:0.00}) right, up, ahead" : ", hand not placed")}");
            }
            return s.ToString();
        }
    }
}
