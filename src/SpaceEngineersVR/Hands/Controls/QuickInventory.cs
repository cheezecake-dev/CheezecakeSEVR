using System;
using System.Collections.Generic;
using HarmonyLib;
using Sandbox;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.Gui;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Input.Bindings;
using VRage.Game;
using VRage.Game.Entity.UseObject;
using VRage.Input;
using VRage.ModAPI;
using VRage.Utils;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// Quick take, quick dump and quick production at a container: wherever the dominant hand points at something the game
    /// opens storage from (a cargo container, a connector, a conveyor port, a refinery, an assembler: anything whose use object
    /// opens an inventory and offers the Build Planner), with a welder, grinder or hand drill in it or with the hand empty, or
    /// with the right hand resting on one, the left stick click is the game's middle mouse there, the default withdraw of the
    /// Build Planner's components, the right stick click its Shift + middle mouse, the Build Planner put in production, and
    /// the right stick click with the right grip held its Alt + middle mouse, deposit all. On foot and on the jetpack; elsewhere
    /// the clicks stay sprint and reload.
    /// </summary>
    /// <remarks>
    /// <para>The game's code, not a copy of it: <c>MyGuiScreenGamePlay.BuildPlannerControls</c> is what the gameplay screen
    /// runs every frame for these shortcuts. It reads the character's detector for the use object, asks
    /// <c>MyControllerHelper.IsControl(context, BUILD_PLANNER)</c> for the middle mouse, and Shift, Ctrl and Alt for which
    /// action: none is the withdraw (Withdraw1: with a block in hand and the planner empty it first puts that block in the
    /// planner, with its notification and sound, then ProcessWithdraw takes the planner's components from the container,
    /// "Withdraw successful" or what is missing), Alt alone is the deposit (DepositOre: MyTerminalInventoryController.DepositAll,
    /// "Deposit successful" or how many kinds found no room), Shift alone is the production (AddProduction1: with a planner that
    /// is not empty, MyTerminalInventoryController.AddComponentsToProduction queues its components in the grid's assemblers,
    /// "put to production" or how many kinds found no assembler; an empty planner says so). It also checks what the middle
    /// mouse would: the use object offers the Build Planner and opens an inventory, and the block is friendly or the admin may
    /// use any terminal ("Access denied" otherwise). For the one call this makes on a press, the middle mouse reads pressed,
    /// Alt reads held for the dump and not for the others, Shift reads held for the production and not for the others, Ctrl
    /// reads not held, and the pad reads not last used, so the keyboard's path is taken whatever is on the desk.</para>
    /// <para>What the target is: what the dominant hand points at, the character detector's own use object, with a hand tool in
    /// it (a welder, a grinder or a hand drill; not a gun, whose aim is the gun's, and not the block placer a block or the
    /// colour tool is held with) or with nothing in it. The plugin casts that from the hand (<see cref="HandUse"/>) as the
    /// game casts it from the head for the middle mouse, so the game's answer stands and nothing answers for it. When that is
    /// not an inventory, it is the block the right hand is on (<see cref="GrabUse"/>: within reach of the palm, nothing solid
    /// in the way, highlighted with a light tick, only while the hand holds no tool, and the block the grip has taken hold of),
    /// and the detector answers with it for the call (<see cref="GrabUse.AnswerWith"/>, the seam its grab uses). With the
    /// dominant hand left the tool is in the left hand and points, so the right hand is the empty one that rests. Either way it
    /// counts only when <see cref="IsInventory"/> says the game would act. With a block in hand (<see cref="BuildMode"/>) there is
    /// no target at all: the stick clicks are building's there.</para>
    /// <para>After a deposit the game builds the detector's prompts again (UpdateInteractiveObjectNotification) for the use
    /// object it has. Inside the call that would be the hand's block, whose prompts would then stay on the HUD after the hand
    /// moved off, so it is held back and done right after the call for what the detector really has.</para>
    /// <para>The buttons: a new press counts only with the left stick click not held to build (<see cref="BuildMode.Modifier"/>:
    /// a paste or the voxel brush) and not with the colour tool in hand (A and B are its brush). The left stick click is the
    /// withdraw. The right stick click is the production with the right grip open, where it is nothing else: not with a hand
    /// weapon that reloads (<see cref="Reload"/>); and the deposit with the right grip held, when that grip is the Use's (the
    /// ray's, or a grab's on what the hand is on) and not another feature's (the hull, the helmet, an item). These are the
    /// bindings' container actions, which shadow sprint, the zero-g roll and reload here (ControllerBindings), except the
    /// production where the game would refuse it (<see cref="ProductionFree"/>). A and B are jump and the jetpack here as everywhere.</para>
    /// <para>The grip's Use waits: a right grip that goes down at a container is the Use key there, and its Use is held back
    /// for as long as the grip is held (<see cref="HoldsBack"/>: the ray Use in <see cref="VRControls"/> and a grab's in
    /// <see cref="GrabUse"/> ask), so the click can deposit without also opening the inventory. When the grip is let go the
    /// Use is done once, on what the grip would have used at the press (the detector's use object then for the ray, the
    /// grab's own for a grab), unless a deposit was taken meanwhile (<see cref="Deposited"/>), which drops it. The held-back
    /// grip keeps its claim (<see cref="GripClaim"/> decides once per press): the hull and ladder grab, asked after the
    /// grab at 30, do not take a grip that was already the Use's, and the ray Use is not claimed by anything. Anywhere else
    /// the grip is as it was.</para>
    /// </remarks>
    internal static class QuickInventory
    {
        /// <summary>The hand points at an inventory block, or rests on one, where the stick clicks act this frame (for the control hint).</summary>
        public static bool Ready { get; private set; }


        /// <summary>The right grip went down at a container and its Use waits for the grip to be let go (the ray Use and a grab's ask).</summary>
        public static bool HoldsBack => backing;

        /// <summary>A deposit was taken while the right grip's Use waited: that Use is dropped when the grip is let go.</summary>
        public static bool Deposited => deposited;

        private enum Firing
        {
            None,
            Take,
            Dump,
            Produce,
        }

        /// <summary>The game's call is running on this thread, as a take, a dump or a production: the inputs it reads answer for it.</summary>
        [ThreadStatic]
        private static Firing firing;

        private static readonly List<string> reported = new List<string>();
        private static bool hooked, refreshAsked, backing, backedRay, deposited;
        private static IMyUseObject backedUse;
        private static int errors;

        public static void Patch(Harmony harmony)
        {
            try
            {
                // The inputs that pick the action: one missing and the dump could withdraw, so all or none.
                harmony.Patch(AccessTools.Method(typeof(MyControllerHelper), nameof(MyControllerHelper.IsControl)),
                    postfix: new HarmonyMethod(typeof(QuickInventory), nameof(Control)));
                harmony.Patch(AccessTools.Method(typeof(MyVRageInput), nameof(MyVRageInput.IsAnyAltKeyPressed)),
                    postfix: new HarmonyMethod(typeof(QuickInventory), nameof(Alt)));
                harmony.Patch(AccessTools.Method(typeof(MyVRageInput), nameof(MyVRageInput.IsAnyShiftKeyPressed)),
                    postfix: new HarmonyMethod(typeof(QuickInventory), nameof(Shift)));
                harmony.Patch(AccessTools.Method(typeof(MyVRageInput), nameof(MyVRageInput.IsAnyCtrlKeyPressed)),
                    postfix: new HarmonyMethod(typeof(QuickInventory), nameof(NotHeld)));
                // After the toolbar wheel's, which says the pad is in use while the wheel runs.
                harmony.Patch(AccessTools.PropertyGetter(typeof(MyVRageInput), nameof(MyVRageInput.IsJoystickLastUsed)),
                    postfix: new HarmonyMethod(typeof(QuickInventory), nameof(NotHeld)) { priority = Priority.Last });
                hooked = true;
            }
            catch (Exception e)
            {
                hooked = false;
                PatchRegistry.Lost(e, "Quick take and dump could not be hooked; the stick clicks stay sprint and reload at a container");
                throw;
            }

            PatchRegistry.Apply("prompts", () =>
            {
                harmony.Patch(AccessTools.Method(typeof(MyCharacterDetectorComponent), nameof(MyCharacterDetectorComponent.UpdateInteractiveObjectNotification)),
                    prefix: new HarmonyMethod(typeof(QuickInventory), nameof(HoldPrompts)));
            }, "Quick take and dump: the game's prompts cannot be held back; after a dump the container's may stay on the HUD");
            PatchRegistry.Apply("notifications", () =>
            {
                harmony.Patch(AccessTools.Method(typeof(MyHudNotifications), nameof(MyHudNotifications.Add), new[] { typeof(MyHudNotificationBase) }),
                    prefix: new HarmonyMethod(typeof(QuickInventory), nameof(Notified)));
            }, "Quick take and dump: the game's notifications cannot be read; the log will not say what it reported");
            Log.Info("Quick take and dump: hooked (MyGuiScreenGamePlay.BuildPlannerControls, the game's middle mouse, Alt + middle mouse and Shift + middle mouse at a container); " +
                     "with a welder, grinder or drill in the dominant hand or the hand empty, pointing at an inventory block, or the right hand on one, on foot, " +
                     "the left stick click withdraws the Build Planner's components, the right stick click puts the planner in production and, with the right grip held, deposits all");
        }

        /// <summary>
        /// Game thread, once per frame from <see cref="VRControls.Update(double)"/>, after build mode and the colour tool and
        /// before the walking controls read the grip and the stick clicks. <paramref name="blocked"/>: a menu or the toolbar wheel has the hands.
        /// </summary>
        internal static void Frame(VRContext context, bool blocked)
        {
            IMyUseObject on = null;
            bool aimed = false;
            string source = null;
            bool hands = !blocked && (context == VRContext.OnFoot || context == VRContext.Jetpack);
            // With a block in hand the stick clicks are building's (BuildMode); with the colour tool A and B are its brush.
            if (hooked && errors < 3 && hands && !PaintMode.Active && !BuildMode.Active)
                on = Target(out aimed, out source);
            Ready = on != null;

            bool grip = VRInput.IsPressed(Hand.Right, VRButtons.Grip);
            // The grip is the Use's (the ray's, or a grab's on what the hand is on) and not another feature's: the hull, the helmet, an item.
            string owner = grip ? GripClaim.Owner(Hand.Right) : null;
            bool gripUse = grip && (owner == null || owner == GrabUse.ClaimName);
            Grip(grip, gripUse, owner == null, hands);

            bool modifier = BuildMode.Modifier; // the left stick click held to build: a paste, the voxel brush
            bool take = Ready && !modifier && ControllerBindings.Started(VRAction.QuickTake);
            bool dump = Ready && !modifier && !take && gripUse && ControllerBindings.Started(VRAction.QuickDepositAll);
            bool produce = Ready && !modifier && !take && !dump && ControllerBindings.Started(VRAction.QuickProduction);
            if (dump && backing)
                deposited = true;
            if (take || dump || produce)
            {
                Firing what = take ? Firing.Take : dump ? Firing.Dump : Firing.Produce;
                VRAction by = take ? VRAction.QuickTake : dump ? VRAction.QuickDepositAll : VRAction.QuickProduction;
                Fire(on, what, aimed, source, ControllerBindings.HandOf(by));
            }
        }

        /// <summary>
        /// The right grip's Use at a container: held back from the press (when the hand points at one or rests on one and the
        /// grip is the Use's), done when the grip is let go, unless a deposit was taken meanwhile. A grab's Use is
        /// <see cref="GrabUse"/>'s to do or drop; the ray's is done here, on what the detector had at the press.
        /// </summary>
        private static void Grip(bool grip, bool gripUse, bool ray, bool hands)
        {
            if (backing && !grip)
            {
                backing = false;
                if (backedRay)
                    UseRay(hands);
                backedRay = false;
            }
            if (grip && VRInput.IsNewPressed(Hand.Right, VRButtons.Grip))
            {
                deposited = false;
                backing = Ready && gripUse;
                backedRay = backing && ray;
                backedUse = null;
                if (backedRay)
                {
                    try
                    {
                        backedUse = MySession.Static?.LocalCharacter?.GetDetectorComponent()?.UseObject;
                    }
                    catch (Exception e)
                    {
                        if (errors++ < 3)
                            Log.Error(e, "Quick take and dump could not tell what the grip would use");
                    }
                }
            }
        }

        /// <summary>The ray Use that waited for the grip: once, on what the detector had at the press; not after a deposit, and not once a menu, a seat or the thing itself has gone.</summary>
        private static void UseRay(bool hands)
        {
            IMyUseObject use = backedUse;
            backedUse = null;
            MyCharacter character = MySession.Static?.LocalCharacter;
            string outcome;
            if (deposited)
                outcome = "dropped for a deposit";
            else if (use == null || GrabUse.Gone(use))
                outcome = "dropped (nothing to use)";
            else if (!hands || character == null)
                outcome = "dropped (a menu or a seat has the hands)";
            else
                outcome = GrabUse.Tap(character, use) ? "used once the grip was let go" : "not used (the game's Use failed)";
            NoteHeldBack(use, "pointing", outcome);
        }

        /// <summary>One log line for a right grip's Use that waited for the grip: done when the grip was let go, or dropped.</summary>
        internal static void NoteHeldBack(IMyUseObject use, string by, string outcome) =>
            Log.Info($"Quick inventory: the right grip's Use at {Name(use?.Owner)} ({by}) was held back, {outcome}");

        /// <summary>
        /// The inventory block the stick clicks act on, as the middle mouse would take it; null when there is none. What the
        /// dominant hand points at (a hand tool's aim, or an empty hand's ray: the detector's own use object, which the game
        /// keeps and nothing answers for), or else the block the right hand is on or has hold of. <paramref name="aimed"/> says
        /// which, and <paramref name="source"/> says it for the log.
        /// </summary>
        private static IMyUseObject Target(out bool aimed, out string source)
        {
            aimed = false;
            source = null;
            MyCharacter character = MySession.Static?.LocalCharacter;
            if (character == null)
                return null;
            try
            {
                // A gun's aim is the gun's, not a ray that points at things: only the resting hand is looked at then.
                bool tool = HoldsTool(character, out string name);
                if (tool || !FingerPress.IsHandTool(character.CurrentWeapon))
                {
                    IMyUseObject use = character.GetDetectorComponent()?.UseObject;
                    if (use != null && !GrabUse.Gone(use) && IsInventory(use, character))
                    {
                        aimed = true;
                        source = tool ? $"the {name}'s aim" : "pointing";
                        return use;
                    }
                }
                IMyUseObject touched = GrabUse.OnFoot(Hand.Right) ?? GrabUse.Grabbed(Hand.Right);
                if (touched == null || !IsInventory(touched, character))
                    return null;
                source = "the hand";
                return touched;
            }
            catch (Exception e)
            {
                aimed = false;
                if (errors++ < 3)
                    Log.Error(e, "Quick take and dump could not tell what the hand is on" + (errors >= 3 ? "; the stick clicks stay as they were for this run" : string.Empty));
                return null;
            }
        }

        /// <summary>The character holds a welder, a grinder or a hand drill: not a gun, and not the block placer (MyBlockPlacerBase is an engineer tool too) a block or the colour tool is held with.</summary>
        private static bool HoldsTool(MyCharacter character, out string name)
        {
            object weapon = character.CurrentWeapon;
            name = weapon is MyHandDrill ? "drill" : weapon is MyWelder ? "welder" : weapon is MyAngleGrinder ? "grinder" : "tool";
            return weapon is MyHandDrill || (weapon is MyEngineerToolBase && !(weapon is MyBlockPlacerBase));
        }

        /// <summary>The right stick click is nothing else here: not the block's other size (a block in hand), not reload (a hand weapon that reloads).</summary>
        /// <summary>The container's production would be taken here: not with a block in hand, nor a hand weapon that reloads, whose reload the control stays.</summary>
        internal static bool ProductionFree()
        {
            if (BuildMode.Active)
                return false;
            return !(MySession.Static?.LocalCharacter?.CurrentWeapon?.Reloadable ?? false);
        }

        /// <summary>
        /// BuildPlannerControls' own conditions: the use object offers the Build Planner and opens an inventory, and the block
        /// is friendly or the admin may use any terminal. Where the game would only say "Access denied", the clicks stay
        /// sprint and reload.
        /// </summary>
        private static bool IsInventory(IMyUseObject use, MyCharacter character)
        {
            if (!(use is MyUseObjectBase item) || (item.SupportedActions & UseActionEnum.BuildPlanner) == 0
                || (item.PrimaryAction != UseActionEnum.OpenInventory && item.SecondaryAction != UseActionEnum.OpenInventory))
                return false;
            return !(item.Owner is MyCubeBlock block)
                   || block.GetUserRelationToOwner(character.ControllerInfo.ControllingIdentityId).IsFriendly()
                   || MySession.Static.AdminSettings.HasFlag(AdminSettingsEnum.UseTerminals);
        }

        /// <summary>
        /// The game's middle mouse (or Alt, or Shift + middle mouse) on <paramref name="use"/>, now, and one log line of what it reported.
        /// What the hand points at is the detector's own answer and stands; the hand's block is answered for the call.
        /// <paramref name="by"/> is the hand whose click it was, which gets the pulse.
        /// </summary>
        private static void Fire(IMyUseObject use, Firing action, bool aimed, string source, Hand by)
        {
            string what = action == Firing.Dump ? "dump" : action == Firing.Produce ? "production" : "take";
            try
            {
                MyCharacter character = MySession.Static?.LocalCharacter;
                MyGuiScreenGamePlay gameplay = MyGuiScreenGamePlay.Static;
                if (character == null || gameplay == null || MySandboxGame.IsPaused)
                {
                    Log.Info($"Quick {what} at {Name(use.Owner)} ({source}): not done (no gameplay screen, or the game is paused)");
                    return;
                }
                MyStringId context = MySession.Static.ControlledEntity?.ControlContext ?? MyStringId.NullOrEmpty;
                reported.Clear();
                refreshAsked = false;
                firing = action;
                try
                {
                    if (aimed)
                        gameplay.BuildPlannerControls(context);
                    else
                        GrabUse.AnswerWith(use, () => gameplay.BuildPlannerControls(context));
                }
                finally
                {
                    firing = Firing.None;
                }
                if (refreshAsked)
                {
                    // The prompts of what the detector really has, as the game builds them after a deposit.
                    MyCharacterDetectorComponent detector = character.GetDetectorComponent();
                    if (detector?.UseObject != null)
                        detector.UpdateInteractiveObjectNotification();
                }
                Haptics.Pulse(by, GrabUse.UseAmplitude, GrabUse.UseSeconds);
                Log.Info($"Quick {what} at {Name(use.Owner)} ({source}): {(reported.Count > 0 ? string.Join("; ", reported) : "the game reported nothing")}");
            }
            catch (Exception e)
            {
                if (errors++ < 3)
                    Log.Error(e, $"Quick {what} at {Name(use?.Owner)} ({source}) failed" + (errors >= 3 ? "; the stick clicks stay as they were for this run" : string.Empty));
            }
        }

        private static string Name(IMyEntity owner)
        {
            string name = (owner as MyCubeBlock)?.DisplayNameText;
            return string.IsNullOrEmpty(name) ? GrabUse.Describe(owner) : $"{GrabUse.Describe(owner)} '{name}'";
        }

        // ---- What the game's call reads, while it runs ----

        /// <summary>MyControllerHelper.IsControl: the middle mouse (BUILD_PLANNER) was just pressed.</summary>
        private static void Control(MyStringId controlId, MyControlStateType type, ref bool __result)
        {
            if (firing != Firing.None && controlId == MyControlsSpace.BUILD_PLANNER && type == MyControlStateType.NEW_PRESSED)
                __result = true;
        }

        /// <summary>IsAnyAltKeyPressed: held for the dump, not for the others.</summary>
        private static void Alt(ref bool __result)
        {
            if (firing != Firing.None)
                __result = firing == Firing.Dump;
        }

        /// <summary>IsAnyShiftKeyPressed: held for the production (Shift without Ctrl is AddProduction1), not for the others.</summary>
        private static void Shift(ref bool __result)
        {
            if (firing != Firing.None)
                __result = firing == Firing.Produce;
        }

        /// <summary>IsAnyCtrlKeyPressed and IsJoystickLastUsed: no.</summary>
        private static void NotHeld(ref bool __result)
        {
            if (firing != Firing.None)
                __result = false;
        }

        /// <summary>The detector's prompts are built again after the call, for what it really has (see the remarks).</summary>
        private static bool HoldPrompts()
        {
            if (firing == Firing.None)
                return true;
            refreshAsked = true;
            return false;
        }

        /// <summary>MyHudNotifications.Add: what the game tells the player, for the log.</summary>
        private static void Notified(MyHudNotificationBase notification)
        {
            if (firing == Firing.None || notification == null)
                return;
            try
            {
                reported.Add(notification.GetText()?.Replace("\r", string.Empty).Replace('\n', ' ').Trim());
            }
            catch (Exception)
            {
                reported.Add(notification.GetType().Name);
            }
        }
    }
}
