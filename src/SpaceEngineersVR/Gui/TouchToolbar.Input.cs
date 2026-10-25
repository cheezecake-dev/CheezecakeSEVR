// TouchToolbar: touches by finger or laser, activating slots, and the seated stick input.
using System;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Sandbox.Game.Components;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.GameSystems.TextSurfaceScripts;
using Sandbox.Game.GUI;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using SpaceEngineersVR.Hands;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;
using VRage.Audio;
using VRage.Game;
using VRage.Utils;
using VRageMath;
using VRageRender.Messages;

namespace SpaceEngineersVR.Gui
{
    internal static partial class TouchToolbar
    {
        // ---- Touches: a finger (ScreenTouch), or the laser and trigger on a screen out of reach. ----

        private static void OnTouched(MyCubeBlock block, int surfaceIndex, Vector2 pixel, TouchPhase phase, Hand hand)
        {
            try
            {
                foreach (Claim c in claims)
                    if (ReferenceEquals(block, c.Cockpit) && surfaceIndex == c.Area)
                    {
                        Touch(c, pixel, phase, hand, "touch");
                        return;
                    }
            }
            catch (Exception e)
            {
                Failed(e, "a touch");
            }
        }

        private static void Touch(Claim c, Vector2 pixel, TouchPhase phase, Hand hand, string how)
        {
            int at = HitTest(c.Layout, Point(c, pixel));
            switch (phase)
            {
                case TouchPhase.Down:
                    c.Pressed = at;
                    c.PressedOver = at != NoTarget;
                    break;
                case TouchPhase.Move:
                    c.PressedOver = c.Pressed != NoTarget && at == c.Pressed;
                    break;
                case TouchPhase.Up:
                    int was = c.Pressed;
                    c.Pressed = NoTarget;
                    c.PressedOver = false;
                    if (was != NoTarget && at == was)
                        Activate(c, was, hand, how);
                    break;
            }
            Draw(c, false);
        }

        private static void Activate(Claim c, int target, Hand hand, string how)
        {
            MyToolbar toolbar = Toolbar(c);
            if (toolbar == null)
            {
                Log.Info($"Touch toolbar: {how} on {(target >= LeftArrow ? "an arrow" : $"slot {target + 1}")} with no toolbar");
                return;
            }
            if (target == LeftArrow || target == RightArrow)
            {
                int from = toolbar.CurrentPage;
                MyGuiAudio.PlaySound(MyGuiSounds.HudClick);
                if (target == LeftArrow)
                    toolbar.PageDown();
                else
                    toolbar.PageUp();
                if (MySession.Static != null)
                    MySession.Static.ToolbarPageSwitches++;
                Haptics.Pulse(hand, 0.35f, 0.03f);
                Log.Info($"Touch toolbar: {how} on the {(target == LeftArrow ? "left" : "right")} arrow: page {from + 1} -> {toolbar.CurrentPage + 1} of {toolbar.PageCount}");
                return;
            }

            MyToolbarItem item = toolbar.GetSlotItem(target);
            var p = new Pending
            {
                Due = frame + LogAfterFrames,
                Toolbar = toolbar,
                Slot = target,
                Page = toolbar.CurrentPage,
                How = how,
                Item = item,
                Name = item?.DisplayName?.ToString() ?? "(empty)",
                Kind = item?.GetType().Name ?? "-",
                StateBefore = item?.IconText?.ToString() ?? "",
                SelectedBefore = toolbar.SelectedSlot,
            };
            if (!toolbar.CanPlayerActivateItems)
            {
                p.Refused = "the toolbar does not take activations now";
                FlushOne(p);
                return;
            }
            // As the slot's number key does in the seat (MyToolbarComponent.HandleInput).
            toolbar.ActivateItemAtSlot(target);
            Haptics.Pulse(hand, 0.35f, 0.03f);
            pending.Add(p);
        }

        private sealed class Pending
        {
            public int Due, Slot, Page;
            public int? SelectedBefore;
            public MyToolbar Toolbar;
            public MyToolbarItem Item;
            public string How, Name, Kind, StateBefore, Refused;
        }

        private static void FlushPending(bool all)
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                if (!all && pending[i].Due > frame)
                    continue;
                Pending p = pending[i];
                pending.RemoveAt(i);
                FlushOne(p);
            }
        }

        /// <summary>The one line for an activation: the slot, its item and page, and what the game shows for it after.</summary>
        private static void FlushOne(Pending p)
        {
            string after;
            if (p.Refused != null)
                after = p.Refused;
            else
            {
                string state = p.Item?.IconText?.ToString() ?? "";
                int? selected = p.Toolbar?.SelectedSlot;
                after = $"state '{p.StateBefore}' -> '{state}', selected slot {Slot(p.SelectedBefore)} -> {Slot(selected)}";
            }
            Log.Info($"Touch toolbar: {p.How} on slot {p.Slot + 1}, page {p.Page + 1}: {p.Name} ({p.Kind}); {after}");
        }

        private static string Slot(int? slot) => slot.HasValue ? (slot.Value + 1).ToString() : "none";

        /// <summary>
        /// From the seat's input (<see cref="VRControls"/>), before the triggers go to the game: a screen out of reach is
        /// tapped by pointing the dominant hand at it and pulling the trigger, which is then the screen's until let go.
        /// The hand is the laser's only while nothing else has it (<see cref="LaserOwner"/>): holding the stick it is the
        /// stick's, and its trigger fires; at a screen's glass it is touching, and the touch's.
        /// </summary>
        internal static void SeatedInput()
        {
            try
            {
                laserHit = false;
                laserAt = null;
                Hand hand = VRSettings.DominantHand;
                // The nearest screen out of reach the dominant hand points at.
                Claim on = null;
                Vector2 pixel = OffScreen;
                float nearest = 4f;
                MatrixD aim = MatrixD.Identity;
                bool aiming = !Off && claims.Count > 0 && VRInput.Active && LaserOwner(hand) == null && HandWorld.TryGet(hand, out MatrixD _, out aim);
                if (aiming)
                    foreach (Claim c in claims)
                        if (Far(c) && Ray(c, aim.Translation, aim.Forward, out float distance, out Vector2 uv) && distance < nearest)
                        {
                            nearest = distance;
                            on = c;
                            pixel = ScreenGlass.Pixel(c.Panel, uv);
                            laserHit = true;
                            laserAt = c;
                        }
                foreach (Claim c in claims)
                    c.Hover = c == on ? HitTest(c.Layout, Point(c, pixel)) : NoTarget;

                if (on != null && !laserDown && VRInput.IsNewPressed(hand, VRButtons.Trigger))
                {
                    VRControls.SwallowTrigger(hand, $"the {hand.ToString().ToLowerInvariant()} trigger pulled on the touch toolbar");
                    laserDown = true;
                    laserOn = on;
                    Touch(on, pixel, TouchPhase.Down, hand, "laser");
                }
                else if (laserDown && laserOn != null)
                {
                    Vector2 at = on == laserOn ? pixel : OffScreen;
                    if (aiming && VRInput.IsPressed(hand, VRButtons.Trigger))
                        Touch(laserOn, at, TouchPhase.Move, hand, "laser");
                    else
                    {
                        Claim was = laserOn;
                        laserDown = false;
                        laserOn = null;
                        Touch(was, at, TouchPhase.Up, hand, "laser");
                    }
                }
                else
                {
                    laserDown = false;
                    laserOn = null;
                }
            }
            catch (Exception e)
            {
                Failed(e, "the laser");
            }
        }

        /// <summary>
        /// Who has the dominant hand instead of the laser, or null: the cockpit's control it is on (a stick, a throttle or a
        /// switch, <see cref="Cockpits.CockpitControls.HandBusy"/>: the hand is drawn on it, not where the controller points
        /// from, and its trigger is the control's), or the screen its fingertip or palm is at (<see cref="TouchOwns"/>: it
        /// touches that). A change of owner is logged, at most once a second.
        /// </summary>
        private static string LaserOwner(Hand hand)
        {
            string owner = null, kind = null;
            if (Cockpits.CockpitControls.HandBusy(hand))
            {
                owner = "it is on one of the cockpit's controls, whose trigger is the control's";
                kind = "control";
            }
            else if (MySession.Static?.LocalCharacter is MyCharacter pilot && HandWorld.TryGetNow(pilot, hand, out MatrixD grip, out _, out _))
            {
                ScreenGlass.Screen at = ScreenGlass.At(hand, FingerPress.Fingertip(hand, grip), grip.Translation, TouchOwns, out float distance);
                if (at != null)
                {
                    owner = $"it is touching {at.Key} ({(distance >= 0f ? $"{distance * 100f:0} cm in front" : $"{-distance * 100f:0} cm through")})";
                    kind = "touch";
                }
            }
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (kind != laserOwner && now - laserOwnerLoggedAt >= System.Diagnostics.Stopwatch.Frequency)
            {
                laserOwnerLoggedAt = now;
                laserOwner = kind;
                Log.Info($"Touch toolbar: the {hand.ToString().ToLowerInvariant()} hand is {(owner == null ? "free for the laser" : $"not the laser's: {owner}")}");
            }
            return owner;
        }

        /// <summary>
        /// The laser, in the frame's draw: where the dominant controller's aim meets the screen the input found it on, as
        /// the controller is in the camera being drawn (the seat's input worked it out from the previous draw's camera),
        /// drawn from the drawn hand's index fingertip (<see cref="FingerPress.Fingertip(Hand, in MatrixD)"/>; the aim
        /// pose's own origin is below the drawn hand); nothing when it no longer meets that screen, so no line is left
        /// behind where the hand was. Given in the pilot's space (<see cref="Rendering.MotionExtrapolation.AddLine"/>), so
        /// it stays on the hand and the screen when the view and the ship are moved on past the game frame; a line given in the
        /// world would move against the cockpit while the ship moves.
        /// </summary>
        private static void DrawLaser()
        {
            if (!laserHit || laserAt == null || !MyTransparentGeometry.HasCamera)
                return;
            Hand hand = VRSettings.DominantHand;
            if (!HandWorld.TryGet(hand, out MatrixD grip, out MatrixD aim)
                || !Ray(laserAt, aim.Translation, aim.Forward, out float length, out Vector2 _) || length < 0.02f)
                return;
            Vector3D from = FingerPress.Fingertip(hand, grip);
            Vector3D along = aim.Translation + aim.Forward * length - from;
            double reach = along.Length();
            if (reach < 0.02)
                return;
            var dir = (Vector3)(along / reach);
            Rendering.MotionExtrapolation.AddLine(LaserMaterial, LaserColor, from, dir, (float)reach - 0.005f, 0.002f);
            Rendering.MotionExtrapolation.AddPoint(DotMaterial, Vector4.One, from + along * ((reach - 0.004) / reach), 0.006f);
        }
    }
}
