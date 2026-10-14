using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// The helmet and the suit lamp by hand, through the game's own calls (the ones the keys make), so the sound, the
    /// animation and the multiplayer sync are the game's. On foot and in a seat: seated, the helmet is switched through the seat
    /// as the HELMET key does there, and the lamp is still the suit's own (never the ship's lights, which the L key switches
    /// in a seat). The grip at the helmet asks before the cockpit's sticks and switches (priority 10, theirs 28 and up).
    /// <list type="bullet">
    /// <item><b>Sun visor, by grip on its front.</b> With the helmet view on, a grip that starts on the front of the helmet,
    /// anywhere from the brow to the chin (<see cref="OnFront"/>), holds the sun visor (<see cref="Rendering.SunVisor"/>):
    /// drawn down <see cref="SlideDistance"/> it lowers it over the glass, pushed up as far it raises it, each with a firm
    /// pulse. It rides on the glass, so it comes down only with the helmet closed. The front is settled before the sides, so a
    /// grip there is only ever the sun visor, never the helmet or the lamp.</item>
    /// <item><b>Helmet, by grip on its right side.</b> A grip with either hand on the helmet (within <see cref="HelmetReach"/>
    /// of its shell) off its front and right of the middle of the head (<see cref="OnRightHalf"/>) opens or closes the helmet
    /// (<c>SwitchHelmet</c>, as the HELMET key does).</item>
    /// <item><b>Lamp, by grip on its left side.</b> A grip on the left side toggles the suit lamp (<c>SwitchLights</c>, as the
    /// key does). Either hand works on either side. Not the left hand at the helmet's mouth, the chin: that is push to talk
    /// (<see cref="VoiceTalk"/>), which asks first.</item>
    /// <item><b>Lamp, by tap.</b> A tap of a fingertip on the side of the head (the temple, either side) toggles it the same
    /// way: the tip touches, lifts within a second and a half, and no grip was pressed meanwhile (so reaching for the helmet
    /// does not switch the lamp twice). Not with a tool in that hand.</item>
    /// </list>
    /// With the helmet view off (<see cref="VRSettings.HelmetView"/>) the helmet is not by hand: a grip on either half
    /// toggles the lamp, as it did before the helmet view.
    /// Only a grip that <b>starts</b> on the helmet counts: it is claimed through <see cref="GripClaim"/> at priority 10, so the
    /// grip's old meanings (Use on the right hand, the toolbar wheel on the left) are skipped for that press, and a grip
    /// anywhere else keeps them. Where on the helmet it is (front, right or left) is settled as the grip starts. A light tick marks the hand reaching the
    /// helmet; the toggle is a firm pulse.
    /// </summary>
    /// <remarks>
    /// Positions are in the character's model space (X to its right, Y up, Z back), from the eyes, placed from the head this
    /// frame (<see cref="HandWorld.TryGetTrackingToBody"/>, the placement the arms use), so they ride with the body at any speed.
    /// The helmet's middle and the temples move with the eyes. Its front and sides turn as the helmet does
    /// (<see cref="Rendering.HelmetTurn"/>): with the head a little, then no further, so a head turned far round inside it
    /// still has the helmet's front ahead of the body. The hands are the controllers', the real hands: the drawn
    /// hand is held off the helmet's shell by <see cref="HelmetStop"/>, and these go on reading where the real hand is, at the real
    /// head, so a hand held to the helmet counts as before.
    /// </remarks>
    internal static class HeadGestures
    {
        /// <summary>Debug: no helmet or lamp gestures (<see cref="Rendering.RenderDebug"/> HeadGestures=0).</summary>
        public static bool Off { get; set; }

        /// <summary>The name the grip is claimed under (<see cref="GripClaim"/>).</summary>
        public const string ClaimName = "Helmet";

        /// <summary>The helmet asks first of the physical interactions, after a hand on the held tool's own grip.</summary>
        public const int ClaimPriority = 10;

        /// <summary>A tick as the hand reaches the helmet, or a fingertip the temple.</summary>
        public const float TickAmplitude = 0.22f, TickSeconds = 0.02f;

        /// <summary>The helmet or the lamp toggled: firm.</summary>
        public const float FirmAmplitude = 0.8f, FirmSeconds = 0.08f;

        /// <summary>
        /// The front of the helmet, where the sun visor is held: a grip at least <see cref="FrontAhead"/> in front of the middle of
        /// the head, along the helmet's own forward, and nearer its forward than its sides (within 45 degrees of it, seen from
        /// above), at any height; or, over the top, at least <see cref="BrowAbove"/> above that middle.
        /// </summary>
        public const double FrontAhead = 0.02, BrowAbove = 0.05;

        /// <summary>How far a hand on the front of the helmet is drawn down or pushed up to slide the sun visor, metres.</summary>
        public const double SlideDistance = 0.05;

        /// <summary>A tap on the temple: lifted within this long, seconds.</summary>
        public const double TapSeconds = 1.5;

        /// <summary>The helmet's shell is a sphere of this radius, metres, round the middle of the head.</summary>
        public const float HelmetShell = 0.14f;

        /// <summary>A grip this near the shell's surface (about 12 cm) is on the helmet, metres.</summary>
        public const float HelmetReach = 0.12f;

        /// <summary>The middle of the head, from the eyes: a little behind and below.</summary>
        public static readonly Vector3 HeadCentre = new Vector3(0f, -0.01f, 0.09f);

        /// <summary>A hand already on the helmet stays on it this much further out, as a factor on the reach, so a trembling hand on the edge does not tick over and over.</summary>
        public const double Hysteresis = 1.15;

        /// <summary>The temple, from the eyes: the side of the head, level with the eyes and a little behind them (+X right, -X left).</summary>
        public static readonly Vector3 Temple = new Vector3(0.085f, -0.02f, 0.05f);

        /// <summary>A fingertip this near a temple touches it, metres.</summary>
        public const double TempleTouch = 0.055;

        /// <summary>And has left it when it is this far, metres (2 cm of hysteresis).</summary>
        public const double TempleLeave = 0.075;

        /// <summary>No tick more often than this on one hand, seconds.</summary>
        private const double TickEverySeconds = 0.2;

        private const double VerifyAfterSeconds = 0.3;

        /// <summary>The character's model space, this frame: where the eyes are and how the tracking space maps into it.</summary>
        private struct Frame
        {
            public Vector3D Head;
            public MatrixD ToModel;
        }

        /// <summary>One hand's gestures.</summary>
        private sealed class HandRun
        {
            /// <summary>This hand's grip is held by the helmet.</summary>
            public bool Holding;

            /// <summary>The grip that the helmet took started on the right half of it (the helmet's), not the left (the lamp's); settled as the grip is claimed.</summary>
            public bool GripOnRight;

            /// <summary>The grip that the helmet took started on its front: it holds the sun visor, and where the hand was when it last moved it.</summary>
            public bool GripOnFront;
            public double SlideFromY;

            /// <summary>The hand is on the helmet now, for the tick.</summary>
            public bool Hover;

            public long TickedAt;

            public bool LampTouching, LampLocked;
            public long LampSince;

            public void Forget()
            {
                Holding = Hover = LampTouching = LampLocked = GripOnFront = false;
            }
        }

        private static readonly HandRun[] runs = { new HandRun(), new HandRun() };
        private static readonly Hand[] bothHands = { Hand.Left, Hand.Right };
        private static readonly long Second = Stopwatch.Frequency;

        private static readonly FeatureGuard guard = new FeatureGuard(nameof(HeadGestures));
        private static bool hooked;

        // ---- Hooks ----

        /// <summary>Registers the grip claim and hooks the per-frame update. A hook that cannot be made leaves the rest of the plugin as it is.</summary>
        public static void Patch(Harmony harmony)
        {
            try
            {
                GripClaim.Register(ClaimName, ClaimPriority, Take);
                harmony.Patch(AccessTools.Method(typeof(MyCharacter), nameof(MyCharacter.UpdateAfterSimulation)),
                    postfix: new HarmonyMethod(typeof(HeadGestures), nameof(AfterSimulation)));
                hooked = true;
                Log.Info($"Helmet and lamp by hand: hooked (grip claim at priority {ClaimPriority}: a grip within {HelmetReach * 100:0} cm of the {HelmetShell * 100:0} cm helmet shell holds the sun visor on its front, opens or closes the helmet on its right side and switches the lamp on its left, as a fingertip tap on either temple switches the lamp)");
            }
            catch (Exception e)
            {
                hooked = false;
                PatchRegistry.Lost(e, "The helmet and lamp gestures could not be hooked; the helmet and the lamp stay on the keys and the wrist panel");
                throw;
            }
        }

        // ---- The grip claim ----

        /// <summary>
        /// A grip has just started on <paramref name="hand"/>: it is the helmet's when the hand is on the helmet right now, and
        /// where on it is settled here, from the hand's pose as the grip starts: the front (the sun visor), else the right side
        /// (the helmet) or the left (the lamp).
        /// </summary>
        private static bool Take(Hand hand)
        {
            if (!hooked || !guard.On)
                return false;
            MyCharacter character = MySession.Static?.LocalCharacter;
            if (!Live(character) || ToolbarWheel.Owns || !TryFrame(character, out Frame frame))
                return false;
            if (!TryHand(frame, hand, out Vector3D grip, out _) || !OnHelmet(grip, frame.Head, false))
                return false;
            runs[(int)hand].GripOnFront = Rendering.HelmetView.On && OnFront(grip, frame.Head);
            runs[(int)hand].GripOnRight = OnRightHalf(grip, frame.Head);
            return true;
        }

        // ---- The per-frame update ----

        /// <summary>Game thread, each simulation frame, after the character's own update.</summary>
        private static void AfterSimulation(MyCharacter __instance)
        {
            if (__instance != MySession.Static?.LocalCharacter || !guard.On)
                return;
            try
            {
                // Asking after every frame keeps the grip claim current: it forgets a grip only when asked once it is let go.
                bool[] holds = { GripClaim.Holds(Hand.Left, ClaimName), GripClaim.Holds(Hand.Right, ClaimName) };

                if (!Live(__instance) || !TryFrame(__instance, out Frame frame))
                {
                    foreach (HandRun run in runs)
                        run.Forget();
                    return;
                }

                long now = Stopwatch.GetTimestamp();
                foreach (Hand hand in bothHands)
                    Step(hand, runs[(int)hand], holds[(int)hand], __instance, frame, now);
                RunVerifies(now);
            }
            catch (Exception e)
            {
                guard.Failed(e, "The helmet and lamp gestures failed");
            }
        }

        /// <summary>Whether the gestures are in play: on foot or in a seat, not up a ladder, and the game screen has the focus (no menu).</summary>
        private static bool Live(MyCharacter character) =>
            !Off && VRSettings.HelmetAndLamp && VRInput.Active && character != null && Controlled(character) != null && !character.IsOnLadder
            && !character.Closed && character.PositionComp != null && MyScreenManager.GetScreenWithFocus() is MyGuiScreenGamePlay;

        /// <summary>
        /// What the local player controls with <paramref name="character"/> in it, the entity the game's HELMET key acts on: the
        /// character on foot, or the seat it sits in. Null otherwise (dead, another character, a remote control, a turret).
        /// </summary>
        private static VRage.Game.ModAPI.Interfaces.IMyControllableEntity Controlled(MyCharacter character)
        {
            if (character.IsDead || character != MySession.Static?.LocalCharacter)
                return null;
            object controlled = MySession.Static.ControlledEntity;
            if (ReferenceEquals(controlled, character))
                return character;
            if (controlled is MyShipController seat && seat.Pilot == character)
                return seat;
            return null;
        }

        /// <summary>The model space this frame, placed as the arms are (from this frame's head).</summary>
        private static bool TryFrame(MyCharacter character, out Frame frame)
        {
            frame = default;
            if (!HandWorld.TryGetTrackingToBody(character, out MatrixD toModel, out _))
                return false;
            // The eyes in the tracking space: the tracked head (no head tracked: the tracking space sits at the eyes).
            Vector3 head = GameHead.Take() ? GameHead.TrackingPose.Translation : Vector3.Zero;
            frame.ToModel = toModel;
            frame.Head = Vector3D.Transform((Vector3D)head, toModel);
            return true;
        }

        /// <summary>A hand's grip and its index fingertip in the model space.</summary>
        private static bool TryHand(in Frame frame, Hand hand, out Vector3D grip, out Vector3D tip)
        {
            grip = tip = Vector3D.Zero;
            HandState state = VRInput.Get(hand);
            if (!state.Tracked)
                return false;
            grip = Vector3D.Transform((Vector3D)state.Grip.Translation, frame.ToModel);
            tip = FingerPress.Fingertip(hand, state, frame.ToModel);
            return true;
        }

        /// <summary>The grip is on the helmet: within <see cref="HelmetReach"/> of its shell (further, by <see cref="Hysteresis"/>, for a hand already there).</summary>
        internal static bool OnHelmet(in Vector3D grip, in Vector3D head, bool already) =>
            Vector3D.Distance(grip, head + (Vector3D)HeadCentre) <= (HelmetShell + HelmetReach) * (already ? Hysteresis : 1.0);

        /// <summary>
        /// The grip is on the right half of the helmet: right of the middle of the head (<see cref="HeadCentre"/>) along the
        /// helmet's own right, which turns with the head as far as the helmet gives (<see cref="Rendering.HelmetTurn"/>), in the
        /// character's model space (X to its right, Z back), so a head turned far round still has the helmet's right on its right.
        /// </summary>
        internal static bool OnRightHalf(in Vector3D grip, in Vector3D head)
        {
            Rendering.HelmetTurn.Look(out float yaw, out _);
            Vector3D offset = grip - (head + (Vector3D)HeadCentre);
            return offset.X * Math.Cos(yaw) - offset.Z * Math.Sin(yaw) > 0.0;
        }

        /// <summary>
        /// The grip is on the front of the helmet, which holds the sun visor whether it is up or down and whether the helmet is open
        /// or closed: at least <see cref="FrontAhead"/> ahead of the middle of the head and nearer the helmet's forward than its
        /// right or left, at any height; or over the brow, <see cref="BrowAbove"/> above that middle. Along the helmet's own
        /// forward and right (turned as <see cref="OnRightHalf"/> turns them).
        /// </summary>
        internal static bool OnFront(in Vector3D grip, in Vector3D head)
        {
            Rendering.HelmetTurn.Look(out float yaw, out _);
            Vector3D offset = grip - (head + (Vector3D)HeadCentre);
            double ahead = -(offset.X * Math.Sin(yaw) + offset.Z * Math.Cos(yaw));
            double side = offset.X * Math.Cos(yaw) - offset.Z * Math.Sin(yaw);
            return ahead >= FrontAhead && (ahead >= Math.Abs(side) || offset.Y >= BrowAbove);
        }

        /// <summary>How far a fingertip is from the nearer temple, metres.</summary>
        internal static double TempleDistance(in Vector3D tip, in Vector3D head)
        {
            Vector3 right = Temple, left = new Vector3(-Temple.X, Temple.Y, Temple.Z);
            return Math.Min(Vector3D.Distance(tip, head + (Vector3D)right), Vector3D.Distance(tip, head + (Vector3D)left));
        }

        private static void Step(Hand hand, HandRun run, bool holds, MyCharacter character, in Frame frame, long now)
        {
            if (!TryHand(frame, hand, out Vector3D grip, out Vector3D tip))
            {
                run.Forget();
                return;
            }

            // A toggle has its own pulse, which a tick in the same frame would replace: the hover is only noted then.
            bool changed = false;
            if (holds && !run.Holding)
            {
                run.Holding = true;
                changed = true;
                if (run.GripOnFront)
                    run.SlideFromY = grip.Y;
                else if (!Rendering.HelmetView.On)
                    ToggleLamp(hand, character, "grip on the helmet", "lamp grip");
                else if (run.GripOnRight)
                    ToggleHelmet(hand, character, "grip on the right of the helmet", "helmet grip");
                else
                    ToggleLamp(hand, character, "grip on the left of the helmet", "lamp grip");
            }
            else if (!holds && run.Holding)
            {
                run.Holding = false;
                changed = true;
            }
            else if (holds && run.GripOnFront)
            {
                Slide(hand, run, grip);
            }

            bool gripPressed = VRInput.IsPressed(hand, VRButtons.Grip);
            if (gripPressed)
                run.Hover = false;
            else if (changed)
                run.Hover = OnHelmet(grip, frame.Head, false);
            else
                Hover(hand, run, grip, frame, now);
            Lamp(hand, run, character, tip, frame, gripPressed, hand == VRSettings.DominantHand, now);
        }

        /// <summary>
        /// The game's SwitchHelmet, as the HELMET key calls it (MyGuiScreenGamePlay, on the controlled entity): the character on
        /// foot, the seat when seated (MyShipController's, which switches its pilot's), raises the sync event, and every machine
        /// plays the helmet's animation and sound (MyCharacter.OnSwitchHelmet, MyCharacterOxygenComponent.SwitchHelmet). <paramref name="how"/> and <paramref name="check"/> name the gesture in the log.
        /// </summary>
        private static void ToggleHelmet(Hand hand, MyCharacter character, string how, string check)
        {
            bool before = HelmetOn(character);
            VRage.Game.ModAPI.Interfaces.IMyControllableEntity controlled = Controlled(character);
            if (controlled == null)
                return;
            controlled.SwitchHelmet();
            Haptics.Pulse(hand, FirmAmplitude, FirmSeconds);
            Note($"Helmet: {hand} {how}: helmet {(before ? "on" : "off")} -> toggled");
            Verify($"{hand} {check}", () => "helmet " + (HelmetOn(character) ? "on" : "off"));
        }

        /// <summary>
        /// A hand holding the front of the helmet: drawn down <see cref="SlideDistance"/> since the visor last moved, it lowers the sun visor;
        /// pushed up as far, it raises it. Only the way it is not already going counts, and the hand's place is taken again each time.
        /// </summary>
        private static void Slide(Hand hand, HandRun run, in Vector3D grip)
        {
            double moved = grip.Y - run.SlideFromY;
            bool lower = moved <= -SlideDistance && !Rendering.SunVisor.Down;
            bool raise = moved >= SlideDistance && Rendering.SunVisor.Down;
            if (!lower && !raise)
            {
                // The way it already went: follow the hand, so the next slide is measured from where it turns back.
                if (Rendering.SunVisor.Down ? moved < 0.0 : moved > 0.0)
                    run.SlideFromY = grip.Y;
                return;
            }
            run.SlideFromY = grip.Y;
            if (!Rendering.SunVisor.Move(lower))
            {
                // Only coming down can be refused: the glass it rides on is swung up with the helmet open.
                Note($"Sun visor: {hand} hand on the front of the helmet drew down, but the helmet is open and the visor rides on its glass; it stays up");
                return;
            }
            Haptics.Pulse(hand, FirmAmplitude, FirmSeconds);
            Note($"Sun visor: {hand} hand on the front of the helmet {(lower ? "drew it down" : "pushed it up")}");
        }

        /// <summary>The helmet is on (closed), as the game's HUD and the wrist panel read it.</summary>
        private static bool HelmetOn(MyCharacter character) => character.OxygenComponent?.HelmetEnabled ?? false;

        /// <summary>
        /// The character's own SwitchLights, as the key calls it on foot: the suit lamp, in a seat too (there the key switches the
        /// ship's lights instead; the suit lamp is switched and drawn seated as on foot, MyCharacter.EnableLights).
        /// <paramref name="how"/> and <paramref name="check"/> name the gesture in the log.
        /// </summary>
        private static void ToggleLamp(Hand hand, MyCharacter character, string how, string check)
        {
            bool before = character.LightEnabled;
            character.SwitchLights();
            Haptics.Pulse(hand, FirmAmplitude, FirmSeconds);
            Note($"Lamp: {hand} {how}: lamp {(before ? "on" : "off")} -> toggled");
            Verify($"{hand} {check}", () => "lamp " + (character.LightEnabled ? "on" : "off"));
        }

        // ---- The tick: a hand reaching the helmet ----

        private static void Hover(Hand hand, HandRun run, in Vector3D grip, in Frame frame, long now)
        {
            // The left hand at the mouth is push to talk's (VoiceTalk), which takes that grip first: no helmet tick there.
            bool on = OnHelmet(grip, frame.Head, run.Hover) && !VoiceTalk.AtMouth(hand);
            if (on == run.Hover)
                return;
            run.Hover = on;
            if (!on || now - run.TickedAt < TickEverySeconds * Second)
                return;
            run.TickedAt = now;
            Haptics.Pulse(hand, TickAmplitude, TickSeconds);
            NoteHover($"Helmet: {hand} hand on the helmet");
        }

        // ---- The lamp: a tap on the temple ----

        private static void Lamp(Hand hand, HandRun run, MyCharacter character, in Vector3D tip, in Frame frame, bool gripPressed, bool toolHand, long now)
        {
            // A tool in the tool hand points where the tool does, not like a finger.
            bool busy = toolHand && character.CurrentWeapon != null;
            double distance = TempleDistance(tip, frame.Head);
            if (!run.LampTouching)
            {
                if (run.LampLocked)
                {
                    if (distance > TempleLeave)
                        run.LampLocked = false;
                    return;
                }
                if (!busy && !gripPressed && distance <= TempleTouch)
                {
                    run.LampTouching = true;
                    run.LampSince = now;
                    Haptics.Pulse(hand, TickAmplitude, TickSeconds);
                }
                return;
            }

            // Touching. A grip means this is the hand reaching for the helmet; resting there is not a tap.
            if (gripPressed || busy || now - run.LampSince > TapSeconds * Second)
            {
                run.LampTouching = false;
                run.LampLocked = distance <= TempleLeave;
                return;
            }
            if (distance <= TempleLeave)
                return;

            // Lifted off in time: a tap.
            run.LampTouching = false;
            ToggleLamp(hand, character, "fingertip tapped the temple", "lamp tap");
        }

        // ---- The log: lines for the grabs and the releases (rate-limited), and what the game says a moment later ----

        private const int NotesPerMinute = 40, HoverNotesPerMinute = 12;
        private static long noteWindow, hoverWindow;
        private static int notes, hoverNotes;

        private static void Note(string text)
        {
            if (Allow(ref noteWindow, ref notes, NotesPerMinute))
                Log.Info(text);
        }

        private static void NoteHover(string text)
        {
            if (Allow(ref hoverWindow, ref hoverNotes, HoverNotesPerMinute))
                Log.Info(text);
        }

        private static bool Allow(ref long window, ref int count, int perMinute)
        {
            long now = Stopwatch.GetTimestamp();
            if (now - window > 60 * Second)
            {
                window = now;
                count = 0;
            }
            return count++ < perMinute;
        }

        private struct Check
        {
            public long Due;
            public string What;
            public Func<string> Read;
        }

        private static readonly List<Check> checks = new List<Check>();

        /// <summary>A moment after a gesture, the game's own state is read back into the log: that is the evidence the call took.</summary>
        private static void Verify(string what, Func<string> read)
        {
            if (checks.Count < 8)
                checks.Add(new Check { Due = Stopwatch.GetTimestamp() + (long)(VerifyAfterSeconds * Second), What = what, Read = read });
        }

        private static void RunVerifies(long now)
        {
            for (int i = checks.Count - 1; i >= 0; i--)
            {
                if (now < checks[i].Due)
                    continue;
                Check check = checks[i];
                checks.RemoveAt(i);
                Log.Info($"Helmet and lamp by hand: {check.What} -> {check.Read()}");
            }
        }
    }
}
