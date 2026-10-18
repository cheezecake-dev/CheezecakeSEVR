// HelmetHud: the HUD is on the helmet's visor, so while the helmet is open the headset shows none.
using System;
using System.Diagnostics;
using System.Threading;
using HarmonyLib;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using VRageMath;
using VRageRender.Animations;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// With the helmet view on (<see cref="HelmetView"/>, <see cref="VRSettings.HelmetView"/>) the HUD is the helmet's: while
    /// the local character's helmet is open (MyCharacterOxygenComponent.HelmetEnabled false), on foot or in a seat, the
    /// headset shows no HUD, neither the flat panel (<see cref="HudPanel"/>: the crosshair, the markers, the notifications)
    /// nor the parts on the visor (<see cref="HudVisor"/>), and it comes back when the helmet closes. And the other way
    /// round, <see cref="VisorClosed"/>: the helmet is closed and seen from inside, for what is drawn on the visor (the HUD),
    /// on the same timing; <see cref="GlassCover"/>, how much of the view the glass covers as it swings, for the glass itself
    /// (<see cref="VisorGlass"/>); and <see cref="Worn"/>, the helmet on the head and seen from inside, open or closed, for the
    /// helmet round the view (<see cref="HelmetInterior"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// When: the game swings the visor (SE_RigHelmetGlassBone, the animation controller's Helmet layer) 98 deg up from where
    /// it rests closed, its bind pose, in about 0.3 s after a 0.1 s blend (helmet_open.mwm's keyframes, from 0.07 to 0.33 s;
    /// helmet_close.mwm's the same back down). The HUD goes when the glass is half-way up (49 deg from closed) and comes back
    /// when it is half-way down, read from the bone every frame drawn. A glass that does not get there (a character without
    /// the bone, or whose animation is not running) has the HUD follow the helmet <see cref="WaitAtMost"/> after it switched.
    /// A new character, a new model or a return to the game takes the helmet as it is, without waiting.
    /// </para>
    /// <para>
    /// A cut, not a fade: the headset's layers are composited as they are, with no opacity of their own (no
    /// XR_KHR_composition_layer_color_scale_bias here), and the visor's swing is over in a third of a second.
    /// </para>
    /// <para>
    /// Not touched: the game's HUD state (MyHud, the HUD mode Tab cycles, saved with the game), menus and the wrist panel
    /// (world-locked: <see cref="WorldMenu"/>), and the wrist's status (<see cref="WristStatus"/>). RenderDebug HelmetHud=0 keeps the HUD in the headset with the helmet open
    /// (<see cref="VisorClosed"/> still follows the helmet).
    /// </para>
    /// </remarks>
    internal static class HelmetHud
    {
        /// <summary>Debug: the HUD stays in the headset with the helmet open (<see cref="RenderDebug"/> HelmetHud=0).</summary>
        public static bool Off { get; set; }

        /// <summary>How far helmet_open swings the glass from closed, radians, and half of it: past half-way the HUD is gone.</summary>
        private static readonly float FullOpen = MathHelper.ToRadians(98f), HalfOpen = FullOpen / 2f;

        /// <summary>How far helmet_open swings the glass from closed, radians: what <see cref="GlassCover"/> is a share of.</summary>
        public static float GlassSwingAngle => FullOpen;

        /// <summary>A glass not half-way this long after the helmet switched (seconds) is not waited for: the HUD follows the helmet.</summary>
        private const double WaitAtMost = 0.5;

        /// <summary>A state sampled longer ago than this (seconds) is not used: the game has not drawn its scene lately.</summary>
        private const double Fresh = 0.5;

        private const string GlassBone = "SE_RigHelmetGlassBone";

        private static readonly FeatureGuard guard = new FeatureGuard(nameof(HelmetHud));

        private static volatile bool hidden, visorClosed, worn, lamp;
        private static volatile float glassCover;
        private static long restoredAt;
        private static long sampledAt;

        /// <summary>Game thread: the glass has swung past half-way open (or, without a glass to watch, the helmet has been open <see cref="WaitAtMost"/>).</summary>
        private static bool swung;

        // Game thread: the character and model the glass bone was found for, and when the helmet and the HUD began to differ (0: they do not).
        private static MyCharacter seenFor;
        private static object seenModel;
        private static int glass = -1;
        private static long switchedAt;
        private static bool hiddenLogged, glassLogged;

        /// <summary>The headset shows no HUD now: the helmet is open. Any thread (the render thread, <see cref="HudPanel"/>).</summary>
        public static bool Hidden => hidden && Recent;

        /// <summary>
        /// The helmet is closed and the view is from inside it: the helmet view on, the local character alive with a suit, its
        /// glass not past half-way open (timed as <see cref="Hidden"/>, whatever HelmetHud=0 says), and the camera its own eyes
        /// on foot or in a seat. Any thread (the render thread: <see cref="HudPanel"/>, <see cref="HudVisor"/>).
        /// </summary>
        public static bool VisorClosed => visorClosed && Recent;

        /// <summary>
        /// The helmet is on the head and the view is from inside it, open or closed: the helmet view on, the local character
        /// alive with a suit, the camera its own eyes on foot or in a seat. Any thread (the render thread: <see cref="HelmetInterior"/>).
        /// </summary>
        public static bool Worn => worn && Recent;

        /// <summary>
        /// How much of the view the visor's glass covers, 1 closed to 0 swung fully open, following the glass bone as it swings
        /// (helmet_open turns it <see cref="FullOpen"/>); without a bone to watch, 1 or 0 as <see cref="VisorClosed"/>. 0 while
        /// the helmet is not <see cref="Worn"/>. Any thread (the render thread: <see cref="VisorGlass"/>).
        /// </summary>
        public static float GlassCover => Worn ? glassCover : 0f;

        /// <summary>When the HUD last came back in the headset as the visor closed (Stopwatch ticks; 0 never): <see cref="HudVisor"/> powers it on from there.</summary>
        public static long RestoredAt => Interlocked.Read(ref restoredAt);

        /// <summary>The suit's lamp is on while the helmet is <see cref="Worn"/>: its spill lights the helmet's inside. Any thread (the render thread: <see cref="HelmetInterior"/>).</summary>
        public static bool LampOn => Worn && lamp;

        private static bool Recent => Stopwatch.GetTimestamp() - Interlocked.Read(ref sampledAt) < (long)(Fresh * Stopwatch.Frequency);

        public static void Patch(Harmony harmony)
        {
            // The gameplay screen's draw (only when there is a scene), once a frame the game draws, on the game thread.
            harmony.Patch(AccessTools.Method(typeof(MySession), nameof(MySession.DrawSync)),
                postfix: new HarmonyMethod(typeof(HelmetHud), nameof(AfterDraw)));
        }

        private static void AfterDraw()
        {
            if (!guard.On)
            {
                hidden = visorClosed = worn = false;
                glassCover = 0f;
                return;
            }
            try
            {
                Sample();
            }
            catch (Exception e)
            {
                hidden = visorClosed = worn = false;
                glassCover = 0f;
                guard.Failed(e, "Helmet HUD failed");
            }
        }

        /// <summary>Game thread: whether the helmet is open, and whether its glass has got half-way since it switched.</summary>
        private static void Sample()
        {
            long now = Stopwatch.GetTimestamp();
            MyCharacter character = MySession.Static?.LocalCharacter;
            bool tracked = HelmetView.On && character != null && !character.IsDead && character.OxygenComponent != null;
            bool open = tracked && !character.OxygenComponent.HelmetEnabled;
            bool stale = now - Interlocked.Read(ref sampledAt) >= (long)(Fresh * Stopwatch.Frequency);
            if (character != seenFor || !ReferenceEquals(seenModel, character?.Model))
            {
                seenFor = character;
                seenModel = character?.Model;
                glass = FindGlass(character);
                stale = true;
            }

            if (!tracked || stale)
            {
                // Off, or the helmet as it is: nothing to wait for.
                swung = open;
                switchedAt = 0;
            }
            else if (open != swung)
            {
                if (switchedAt == 0)
                    switchedAt = now;
                bool across = TryGlassAngle(character, out float angle) && (open ? angle >= HalfOpen : angle < HalfOpen);
                if (across || now - switchedAt >= (long)(WaitAtMost * Stopwatch.Frequency))
                {
                    swung = open;
                    switchedAt = 0;
                }
            }
            else
            {
                switchedAt = 0;
            }
            bool wasHidden = hidden;
            hidden = swung && !Off;
            if (wasHidden && !hidden && tracked)
                Interlocked.Exchange(ref restoredAt, now);
            worn = tracked && WallFade.OwnView(character);
            lamp = worn && character.LightEnabled;
            visorClosed = worn && !swung;
            // The bone while it agrees with the state (a glass on its way past half-way, or resting where the state says); the
            // state where it does not (no bone, or a glass whose animation is not running).
            float swing = 0f;
            bool agrees = worn && TryGlassAngle(character, out swing) && (swung ? swing >= HalfOpen : swing < HalfOpen);
            glassCover = !worn ? 0f : agrees ? MathHelper.Clamp(1f - swing / FullOpen, 0f, 1f) : swung ? 0f : 1f;
            Interlocked.Exchange(ref sampledAt, now);

            if (hidden && !hiddenLogged)
            {
                hiddenLogged = true;
                Log.Info($"Helmet view: the helmet is open, so the headset shows no HUD (the panel and the visor's parts); it comes back as the visor closes, " +
                         $"{(glass >= 0 ? $"timed by {GlassBone} half-way ({MathHelper.ToDegrees(HalfOpen):F0} deg)" : $"{WaitAtMost:F1} s after the helmet closes")}. " +
                         "The window, menus and the wrist panel keep theirs; RenderDebug HelmetHud=0 keeps the HUD with the helmet open");
            }
        }

        /// <summary>The glass bone's index in this character's skeleton, -1 (logged once) where it has none.</summary>
        private static int FindGlass(MyCharacter character)
        {
            if (character?.AnimationController == null)
                return -1;
            character.AnimationController.FindBone(GlassBone, out int index);
            if (index < 0 && !glassLogged)
            {
                glassLogged = true;
                Log.Info($"Helmet view: this character has no {GlassBone}; the HUD goes and comes back {WaitAtMost:F1} s after the helmet opens or closes");
            }
            return index;
        }

        /// <summary>How far the glass is turned from where it rests closed (its bind pose), radians.</summary>
        private static bool TryGlassAngle(MyCharacter character, out float angle)
        {
            angle = 0f;
            MyCharacterBone[] bones = character.AnimationController?.CharacterBones;
            if (glass < 0 || bones == null || glass >= bones.Length || bones[glass] == null)
                return false;
            // The bone's rotation is from its bind pose (MyCharacterBone.SetCompleteTransform takes the bind's off the keyframe's).
            Quaternion turn = bones[glass].Rotation;
            float length = turn.Length();
            if (length < 1e-6f)
                return false;
            angle = 2f * (float)Math.Acos(Math.Min(1f, Math.Abs(turn.W) / length));
            return true;
        }
    }
}
