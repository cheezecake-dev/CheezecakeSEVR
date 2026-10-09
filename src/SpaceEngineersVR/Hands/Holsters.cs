using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Gui;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// Tools from the body, and the helmet and the lamp from the head, with the hands. All of it goes through the game's
    /// own calls (the ones the toolbar and the radial menu make), so the sound, the animation, the inventory rules and the
    /// multiplayer sync are the game's.
    /// <list type="bullet">
    /// <item><b>Draw.</b> The tool hand (<see cref="VRSettings.DominantHand"/>, the one that holds the item) gripped with the
    /// hand inside a holster: the welder on that hand's hip, the grinder on the other hip, the rifle over that shoulder, the
    /// hand drill over the other (<see cref="HolsterZones"/>). The best tier the inventory holds is equipped
    /// (<c>MyCharacter.SwitchToWeapon</c>); with none, a double buzz and a line in the log. A tool already in hand is swapped.</item>
    /// <item><b>Stow.</b> A grip that starts in the holster of the tool in hand and is let go inside it unequips it
    /// (<c>SwitchToWeapon(null)</c>, as the radial menu's Unequip does). Let go elsewhere, the tool stays in hand.</item>
    /// <item><b>Helmet.</b> A grip with either hand on the helmet (within 12 cm of its shell) toggles it
    /// (<c>SwitchHelmet</c>, as the key does).</item>
    /// <item><b>Lamp.</b> A tap of a fingertip on the side of the head (the temple) toggles the suit lamp (<c>SwitchLights</c>):
    /// the tip touches, lifts within a second and a half, and no grip was pressed meanwhile (so reaching for the helmet
    /// does not also switch the lamp). Not with a tool in that hand.</item>
    /// </list>
    /// Only a grip that <b>starts</b> in a zone counts: it is claimed through <see cref="GripClaim"/> at priority 10, so the
    /// grip's old meanings (Use on the right hand, the toolbar wheel on the left) are skipped for that press, and a grip
    /// anywhere else keeps them. A light tick marks the hand entering a zone, so a holster can be found without looking;
    /// a draw or a stow is a firm pulse; a draw with nothing to draw is a double buzz.
    /// </summary>
    /// <remarks>
    /// The zones are in the character's model space, placed from the head this frame (<see cref="HandWorld.TryGetTrackingToBody"/>,
    /// the placement the arms use), so they ride with the body at any speed and keep its orientation, not the head's.
    /// </remarks>
    internal static class Holsters
    {
        /// <summary>Debug: no holsters, helmet or lamp gestures (<see cref="Rendering.RenderDebug"/> Holsters=0).</summary>
        public static bool Off { get; set; }

        /// <summary>The name the grip is claimed under (<see cref="GripClaim"/>).</summary>
        public const string ClaimName = "Holsters";

        /// <summary>Holsters and the helmet ask first of the physical interactions.</summary>
        public const int ClaimPriority = 10;

        /// <summary>A tick as the hand enters a zone.</summary>
        public const float TickAmplitude = 0.22f, TickSeconds = 0.02f;

        /// <summary>A draw, a stow, the helmet: firm.</summary>
        public const float FirmAmplitude = 0.8f, FirmSeconds = 0.08f;

        /// <summary>Nothing to draw: two buzzes, this far apart.</summary>
        public const float BuzzAmplitude = 0.5f, BuzzSeconds = 0.05f, BuzzSecondSeconds = 0.08f, BuzzGapSeconds = 0.12f;

        /// <summary>A tap on the temple: lifted within this long, seconds.</summary>
        public const double TapSeconds = 1.5;

        /// <summary>No tick more often than this on one hand, seconds.</summary>
        private const double TickEverySeconds = 0.2;

        private const double VerifyAfterSeconds = 0.3;

        /// <summary>A tool family: the holster it lives in and its physical items, the best tier first.</summary>
        private sealed class Family
        {
            public Spot Spot;
            public string Name;
            public string[] Items;
        }

        private static readonly Family[] Families =
        {
            new Family { Spot = Spot.Hip, Name = "welder", Items = new[] { "Welder4Item", "Welder3Item", "Welder2Item", "WelderItem" } },
            new Family { Spot = Spot.OffHip, Name = "grinder", Items = new[] { "AngleGrinder4Item", "AngleGrinder3Item", "AngleGrinder2Item", "AngleGrinderItem" } },
            new Family
            {
                Spot = Spot.Shoulder, Name = "rifle",
                Items = new[] { "UltimateAutomaticRifleItem", "PreciseAutomaticRifleItem", "RapidFireAutomaticRifleItem", "AutomaticRifleItem" },
            },
            new Family { Spot = Spot.OffShoulder, Name = "hand drill", Items = new[] { "HandDrill4Item", "HandDrill3Item", "HandDrill2Item", "HandDrillItem" } },
        };

        /// <summary>The character's model space, this frame: where the eyes are and how the tracking space maps into it.</summary>
        private struct Frame
        {
            public Vector3D Head;
            public MatrixD ToModel;
        }

        /// <summary>One hand's gestures.</summary>
        private sealed class HandRun
        {
            /// <summary>Set by the taker: where the grip that was just claimed started.</summary>
            public Spot Pending;

            /// <summary>This hand's grip is held by the holsters, started at <see cref="Target"/>.</summary>
            public bool Holding;

            public Spot Target;

            /// <summary>The grip started at the holster of the tool in hand: letting go inside it stows.</summary>
            public bool Stowing;

            /// <summary>The spot the hand is in now, for the tick.</summary>
            public Spot Hover;

            public long TickedAt;

            /// <summary>The second buzz is due (Stopwatch ticks); 0 for none.</summary>
            public long BuzzAt;

            public bool LampTouching, LampLocked;
            public long LampSince;

            public void Forget()
            {
                Pending = Spot.None;
                Holding = Stowing = LampTouching = LampLocked = false;
                Target = Hover = Spot.None;
                BuzzAt = 0;
            }
        }

        private static readonly HandRun[] runs = { new HandRun(), new HandRun() };
        private static readonly Hand[] bothHands = { Hand.Left, Hand.Right };
        private static readonly long Second = Stopwatch.Frequency;

        private static int errors;
        private static bool hooked;

        // ---- Hooks ----

        /// <summary>Registers the grip claim and hooks the per-frame update. A hook that cannot be made leaves the rest of the plugin as it is.</summary>
        public static void Patch(Harmony harmony)
        {
            try
            {
                GripClaim.Register(ClaimName, ClaimPriority, Take);
                harmony.Patch(AccessTools.Method(typeof(MyCharacter), nameof(MyCharacter.UpdateAfterSimulation)),
                    postfix: new HarmonyMethod(typeof(Holsters), nameof(AfterSimulation)));
                hooked = true;
                Log.Info("Holsters: hooked (grip claim at priority " + ClaimPriority + ", character update)");
            }
            catch (Exception e)
            {
                hooked = false;
                Log.Error(e, "Holsters could not be hooked; the tools, the helmet and the lamp stay on the toolbar and the keys");
            }
        }

        // ---- The grip claim ----

        /// <summary>A grip has just started on <paramref name="hand"/>: it is the holsters' when the hand is in a zone right now.</summary>
        private static bool Take(Hand hand)
        {
            HandRun run = runs[(int)hand];
            run.Pending = Spot.None;
            if (!hooked || errors >= 3)
                return false;
            MyCharacter character = MySession.Static?.LocalCharacter;
            if (!Live(character) || ToolbarWheel.Owns || !TryFrame(character, out Frame frame))
                return false;
            if (!TryHand(frame, hand, out Vector3D grip, out _))
                return false;
            Spot spot = HolsterZones.Pick(hand == VRSettings.DominantHand, VRSettings.DominantHand == Hand.Right, grip, frame.Head, Spot.None);
            run.Pending = spot;
            return spot != Spot.None;
        }

        // ---- The per-frame update ----

        /// <summary>Game thread, each simulation frame, after the character's own update.</summary>
        private static void AfterSimulation(MyCharacter __instance)
        {
            if (errors >= 3 || __instance != MySession.Static?.LocalCharacter)
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
                if (errors++ < 3)
                    Log.Error(e, "Holsters failed" + (errors >= 3 ? "; they stay off for this run" : string.Empty));
            }
        }

        /// <summary>Whether the holsters are in play: on foot, not up a ladder, and the game screen has the focus (no menu).</summary>
        private static bool Live(MyCharacter character) =>
            !Off && VRSettings.Holsters && VRInput.Active && character != null && HandTools.IsLocalOnFoot(character) && !character.IsOnLadder
            && !character.Closed && character.PositionComp != null && MyScreenManager.GetScreenWithFocus() is MyGuiScreenGamePlay;

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
            Vector3D forward = Vector3D.TransformNormal((Vector3D)state.Aim.Forward, frame.ToModel);
            double length = forward.Length();
            tip = length > 1e-9 ? grip + forward * (FingerPress.FingertipReach / length) : grip;
            return true;
        }

        private static void Step(Hand hand, HandRun run, bool holds, MyCharacter character, in Frame frame, long now)
        {
            if (run.BuzzAt != 0 && now >= run.BuzzAt)
            {
                run.BuzzAt = 0;
                Haptics.Pulse(hand, BuzzAmplitude, BuzzSecondSeconds);
            }
            if (!TryHand(frame, hand, out Vector3D grip, out Vector3D tip))
            {
                run.Forget();
                return;
            }
            bool rightHanded = VRSettings.DominantHand == Hand.Right;
            bool toolHand = hand == VRSettings.DominantHand;

            // A claim or a release has its own pulse, which a tick in the same frame would replace: the spot is only noted then.
            bool changed = false;
            if (holds && !run.Holding)
            {
                run.Holding = true;
                run.Target = run.Pending;
                run.Stowing = false;
                changed = true;
                Claim(hand, run, character, rightHanded);
            }
            else if (!holds && run.Holding)
            {
                run.Holding = false;
                changed = true;
                Release(hand, run, character, rightHanded, grip, frame);
                run.Stowing = false;
            }

            bool gripPressed = VRInput.IsPressed(hand, VRButtons.Grip);
            if (gripPressed)
                run.Hover = Spot.None;
            else if (changed)
                run.Hover = HolsterZones.Pick(toolHand, rightHanded, grip, frame.Head, Spot.None);
            else
                Hover(hand, run, rightHanded, toolHand, grip, frame, now);
            Lamp(hand, run, character, tip, frame, gripPressed, toolHand, now);
        }

        // ---- A grip claimed ----

        private static void Claim(Hand hand, HandRun run, MyCharacter character, bool rightHanded)
        {
            Spot spot = run.Target;
            string where = HolsterZones.Describe(spot, rightHanded);
            if (spot == Spot.Helmet)
            {
                bool before = character.OxygenComponent?.HelmetEnabled ?? false;
                ((VRage.Game.ModAPI.Interfaces.IMyControllableEntity)character).SwitchHelmet();
                Haptics.Pulse(hand, FirmAmplitude, FirmSeconds);
                Note($"Holsters: {hand} grip on the {where}: helmet {(before ? "closed" : "open")} -> toggled");
                Verify($"{hand} helmet toggle", () => "helmet " + ((character.OxygenComponent?.HelmetEnabled ?? false) ? "closed" : "open"));
                return;
            }

            Family family = FamilyOf(spot);
            if (family == null)
                return;
            Family inHand = FamilyOfWeapon(character.CurrentWeapon);
            if (inHand != null && inHand.Spot == spot)
            {
                // The tool that lives here is in hand: this grip is the stow, if it is let go inside the holster.
                run.Stowing = true;
                Haptics.Pulse(hand, TickAmplitude, TickSeconds * 2f);
                Note($"Holsters: {hand} grip on the {where} holding the {family.Name}: let go here to stow it");
                return;
            }

            if (!TryPick(character, family, out MyDefinitionId item))
            {
                Haptics.Pulse(hand, BuzzAmplitude, BuzzSeconds);
                run.BuzzAt = Stopwatch.GetTimestamp() + (long)(BuzzGapSeconds * Second);
                Note($"Holsters: {hand} grip on the {where}: no {family.Name} to draw (none in the inventory the character can switch to)");
                return;
            }
            string was = Describe(character.CurrentWeapon);
            character.SwitchToWeapon(item);
            Haptics.Pulse(hand, FirmAmplitude, FirmSeconds);
            Note($"Holsters: {hand} grip on the {where}: drew {item.SubtypeName} (was {was})");
            Verify($"{hand} draw {item.SubtypeName}", () => "in hand: " + Describe(character.CurrentWeapon));
        }

        private static void Release(Hand hand, HandRun run, MyCharacter character, bool rightHanded, in Vector3D grip, in Frame frame)
        {
            string where = HolsterZones.Describe(run.Target, rightHanded);
            if (!run.Stowing)
                return;
            Family inHand = FamilyOfWeapon(character.CurrentWeapon);
            if (inHand == null || inHand.Spot != run.Target)
            {
                Note($"Holsters: {hand} grip let go at the {where}: the tool is no longer in hand, nothing to stow");
                return;
            }
            if (!HolsterZones.Within(run.Target, rightHanded, grip, frame.Head))
            {
                Note($"Holsters: {hand} grip let go away from the {where}: the {inHand.Name} stays in hand");
                return;
            }
            string was = Describe(character.CurrentWeapon);
            character.SwitchToWeapon((MyToolbarItemWeapon)null);
            Haptics.Pulse(hand, FirmAmplitude, FirmSeconds);
            Note($"Holsters: {hand} grip let go at the {where}: stowed the {inHand.Name} (was {was})");
            Verify($"{hand} stow", () => "in hand: " + Describe(character.CurrentWeapon));
        }

        // ---- The tick: a hand entering a zone ----

        private static void Hover(Hand hand, HandRun run, bool rightHanded, bool toolHand, in Vector3D grip, in Frame frame, long now)
        {
            Spot spot = HolsterZones.Pick(toolHand, rightHanded, grip, frame.Head, run.Hover);
            if (spot == run.Hover)
                return;
            run.Hover = spot;
            if (spot == Spot.None || now - run.TickedAt < TickEverySeconds * Second)
                return;
            run.TickedAt = now;
            Haptics.Pulse(hand, TickAmplitude, TickSeconds);
            NoteHover($"Holsters: {hand} hand entered the {HolsterZones.Describe(spot, rightHanded)}");
        }

        // ---- The lamp: a tap on the temple ----

        private static void Lamp(Hand hand, HandRun run, MyCharacter character, in Vector3D tip, in Frame frame, bool gripPressed, bool toolHand, long now)
        {
            // A tool in the tool hand points where the tool does, not like a finger.
            bool busy = toolHand && character.CurrentWeapon != null;
            double distance = HolsterZones.TempleDistance(tip, frame.Head);
            if (!run.LampTouching)
            {
                if (run.LampLocked)
                {
                    if (distance > HolsterZones.TempleLeave)
                        run.LampLocked = false;
                    return;
                }
                if (!busy && !gripPressed && distance <= HolsterZones.TempleTouch)
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
                run.LampLocked = distance <= HolsterZones.TempleLeave;
                return;
            }
            if (distance <= HolsterZones.TempleLeave)
                return;

            // Lifted off in time: a tap.
            run.LampTouching = false;
            bool before = character.LightEnabled;
            character.SwitchLights();
            Haptics.Pulse(hand, FirmAmplitude, FirmSeconds);
            Note($"Holsters: {hand} fingertip tapped the temple: lamp {(before ? "on" : "off")} -> toggled");
            Verify($"{hand} lamp tap", () => "lamp " + (character.LightEnabled ? "on" : "off"));
        }

        // ---- What the game has ----

        private static Family FamilyOf(Spot spot)
        {
            foreach (Family family in Families)
            {
                if (family.Spot == spot)
                    return family;
            }
            return null;
        }

        /// <summary>The family a weapon in hand belongs to (by its physical item), or null: a pistol, the builder, nothing.</summary>
        private static Family FamilyOfWeapon(IMyHandheldGunObject<MyDeviceBase> weapon)
        {
            string item = weapon?.PhysicalItemDefinition?.Id.SubtypeName;
            if (item == null)
                return null;
            foreach (Family family in Families)
            {
                if (Array.IndexOf(family.Items, item) >= 0)
                    return family;
            }
            return null;
        }

        private static string Describe(IMyHandheldGunObject<MyDeviceBase> weapon) =>
            weapon == null ? "nothing" : $"{weapon.DefinitionId.SubtypeName} ({weapon.PhysicalItemDefinition?.Id.SubtypeName})";

        /// <summary>
        /// The best tier of the family the character's inventory holds and the game lets it switch to. Where the game needs no
        /// item (creative: <c>WeaponTakesBuilderFromInventory</c> is false) and the inventory holds none, the plainest tier.
        /// </summary>
        private static bool TryPick(MyCharacter character, Family family, out MyDefinitionId item)
        {
            item = default;
            foreach (string name in family.Items)
            {
                var id = new MyDefinitionId(typeof(MyObjectBuilder_PhysicalGunObject), name);
                if (character.FindWeaponItemByDefinition(id).HasValue && character.CanSwitchToWeapon(id))
                {
                    item = id;
                    return true;
                }
            }
            var plain = new MyDefinitionId(typeof(MyObjectBuilder_PhysicalGunObject), family.Items[family.Items.Length - 1]);
            if (!character.WeaponTakesBuilderFromInventory(plain) && Sandbox.Definitions.MyDefinitionManager.Static.HandItemExistsFor(plain)
                && character.CanSwitchToWeapon(plain))
            {
                item = plain;
                return true;
            }
            return false;
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
                Log.Info($"Holsters: {check.What} -> {check.Read()}");
            }
        }
    }
}
