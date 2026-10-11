using System;
using System.Collections.Generic;
using HarmonyLib;
using Sandbox;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using VRage.Game;
using VRage.Game.Entity.UseObject;
using VRage.Input;
using VRage.ModAPI;
using VRage.Utils;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// Quick take and quick dump at a container: with the right hand on an inventory block (a cargo container, a connector,
    /// a conveyor port, a refinery, an assembler: anything whose use object opens an inventory and offers the Build
    /// Planner), A is the game's middle mouse there, the default withdraw of the Build Planner's components, and B its
    /// Alt + middle mouse, deposit all. On foot and on the jetpack, with or without a block in hand; elsewhere A and B stay
    /// jump and the jetpack.
    /// </summary>
    /// <remarks>
    /// <para>The game's code, not a copy of it: <c>MyGuiScreenGamePlay.BuildPlannerControls</c> is what the gameplay screen
    /// runs every frame for these shortcuts. It reads the character's detector for the use object, asks
    /// <c>MyControllerHelper.IsControl(context, BUILD_PLANNER)</c> for the middle mouse, and Shift, Ctrl and Alt for which
    /// action: none is the withdraw (Withdraw1: with a block in hand and the planner empty it first puts that block in the
    /// planner, with its notification and sound, then ProcessWithdraw takes the planner's components from the container,
    /// "Withdraw successful" or what is missing), Alt alone is the deposit (DepositOre: MyTerminalInventoryController.DepositAll,
    /// "Deposit successful" or how many kinds found no room). It also checks what the middle mouse would: the use object
    /// offers the Build Planner and opens an inventory, and the block is friendly or the admin may use any terminal ("Access
    /// denied" otherwise). For the one call this makes on a press, the detector answers with the block the hand is on
    /// (<see cref="GrabUse.AnswerWith"/>, the seam its grab uses), the middle mouse reads pressed, Alt reads held for the dump
    /// and not for the take, Shift and Ctrl read not held, and the pad reads not last used, so the keyboard's path is taken
    /// whatever is on the desk.</para>
    /// <para>After a deposit the game builds the detector's prompts again (UpdateInteractiveObjectNotification) for the use
    /// object it has. Inside the call that would be the hand's block, whose prompts would then stay on the HUD after the hand
    /// moved off, so it is held back and done right after the call for what the detector really has.</para>
    /// <para>What the hand is on is <see cref="GrabUse"/>'s: within reach of the palm, nothing solid in the way, highlighted with
    /// a light tick, and only while the hand holds no tool. A press counts only with the right grip open (with the grip
    /// held the hand is opening the block, or A and B step a block's stage in build mode), and not with the colour tool in
    /// hand (A and B are its brush). A press taken here stays out of jump and the jetpack until it is let go
    /// (<see cref="TakesA"/>, <see cref="TakesB"/>).</para>
    /// </remarks>
    internal static class QuickInventory
    {
        /// <summary>The right hand is on an inventory block where A and B take and dump this frame (for the control hint).</summary>
        public static bool Ready { get; private set; }

        /// <summary>A was pressed for a take and is not jump or the jetpack's climb until it is let go.</summary>
        public static bool TakesA => takesA;

        /// <summary>B was pressed for a dump and is not the jetpack until it is let go.</summary>
        public static bool TakesB => takesB;

        private enum Firing
        {
            None,
            Take,
            Dump,
        }

        /// <summary>The game's call is running on this thread, as a take or a dump: the inputs it reads answer for it.</summary>
        [ThreadStatic]
        private static Firing firing;

        private static readonly List<string> reported = new List<string>();
        private static bool hooked, takesA, takesB, refreshAsked;
        private static int errors;

        public static void Patch(Harmony harmony)
        {
            try
            {
                // The inputs that pick the action: one missing and B could withdraw, so all or none.
                harmony.Patch(AccessTools.Method(typeof(MyControllerHelper), nameof(MyControllerHelper.IsControl)),
                    postfix: new HarmonyMethod(typeof(QuickInventory), nameof(Control)));
                harmony.Patch(AccessTools.Method(typeof(MyVRageInput), nameof(MyVRageInput.IsAnyAltKeyPressed)),
                    postfix: new HarmonyMethod(typeof(QuickInventory), nameof(Alt)));
                harmony.Patch(AccessTools.Method(typeof(MyVRageInput), nameof(MyVRageInput.IsAnyShiftKeyPressed)),
                    postfix: new HarmonyMethod(typeof(QuickInventory), nameof(NotHeld)));
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
                Log.Error(e, "Quick take and dump could not be hooked; A and B stay jump and the jetpack at a container");
                return;
            }

            try
            {
                harmony.Patch(AccessTools.Method(typeof(MyCharacterDetectorComponent), nameof(MyCharacterDetectorComponent.UpdateInteractiveObjectNotification)),
                    prefix: new HarmonyMethod(typeof(QuickInventory), nameof(HoldPrompts)));
            }
            catch (Exception e)
            {
                Log.Error(e, "Quick take and dump: the game's prompts cannot be held back; after a dump the container's may stay on the HUD");
            }
            try
            {
                harmony.Patch(AccessTools.Method(typeof(MyHudNotifications), nameof(MyHudNotifications.Add), new[] { typeof(MyHudNotificationBase) }),
                    prefix: new HarmonyMethod(typeof(QuickInventory), nameof(Notified)));
            }
            catch (Exception e)
            {
                Log.Error(e, "Quick take and dump: the game's notifications cannot be read; the log will not say what it reported");
            }
            Log.Info("Quick take and dump: hooked (MyGuiScreenGamePlay.BuildPlannerControls, the game's middle mouse and Alt + middle mouse at a container); " +
                     "with the right hand on an inventory block on foot, A withdraws the Build Planner's components and B deposits all");
        }

        /// <summary>
        /// Game thread, once per frame from <see cref="VRControls.Update(double)"/>, after build mode and the colour tool and
        /// before the walking controls read A and B. <paramref name="blocked"/>: a menu or the toolbar wheel has the hands.
        /// </summary>
        internal static void Frame(VRContext context, bool blocked)
        {
            IMyUseObject on = null;
            if (hooked && errors < 3 && !blocked && (context == VRContext.OnFoot || context == VRContext.Jetpack) && !PaintMode.Active)
                on = Container();
            Ready = on != null;
            bool busy = VRInput.IsPressed(Hand.Right, VRButtons.Grip) || BuildMode.Modifier; // a grab or use, or the stage keys
            bool take = Ready && !busy && VRInput.IsNewPressed(Hand.Right, VRButtons.A);
            bool dump = Ready && !busy && !take && VRInput.IsNewPressed(Hand.Right, VRButtons.B);
            takesA = VRInput.IsPressed(Hand.Right, VRButtons.A) && (takesA || take);
            takesB = VRInput.IsPressed(Hand.Right, VRButtons.B) && (takesB || dump);
            if (take || dump)
                Fire(on, dump);
        }

        /// <summary>The inventory block the right hand is on, as the middle mouse would take it there; null when there is none.</summary>
        private static IMyUseObject Container()
        {
            IMyUseObject use = GrabUse.OnFoot(Hand.Right);
            MyCharacter character = MySession.Static?.LocalCharacter;
            if (use == null || character == null)
                return null;
            try
            {
                return IsInventory(use, character) ? use : null;
            }
            catch (Exception e)
            {
                if (errors++ < 3)
                    Log.Error(e, "Quick take and dump could not tell what the hand is on" + (errors >= 3 ? "; A and B stay jump and the jetpack for this run" : string.Empty));
                return null;
            }
        }

        /// <summary>
        /// BuildPlannerControls' own conditions: the use object offers the Build Planner and opens an inventory, and the block
        /// is friendly or the admin may use any terminal. Where the game would only say "Access denied", A and B stay jump
        /// and the jetpack.
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

        /// <summary>The game's middle mouse (or Alt + middle mouse) on <paramref name="use"/>, now, and one log line of what it reported.</summary>
        private static void Fire(IMyUseObject use, bool dump)
        {
            string what = dump ? "dump" : "take";
            try
            {
                MyCharacter character = MySession.Static?.LocalCharacter;
                MyGuiScreenGamePlay gameplay = MyGuiScreenGamePlay.Static;
                if (character == null || gameplay == null || MySandboxGame.IsPaused)
                {
                    Log.Info($"Quick {what} at {Name(use.Owner)}: not done (no gameplay screen, or the game is paused)");
                    return;
                }
                MyStringId context = MySession.Static.ControlledEntity?.ControlContext ?? MyStringId.NullOrEmpty;
                reported.Clear();
                refreshAsked = false;
                firing = dump ? Firing.Dump : Firing.Take;
                try
                {
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
                Haptics.Pulse(Hand.Right, GrabUse.UseAmplitude, GrabUse.UseSeconds);
                Log.Info($"Quick {what} at {Name(use.Owner)}: {(reported.Count > 0 ? string.Join("; ", reported) : "the game reported nothing")}");
            }
            catch (Exception e)
            {
                if (errors++ < 3)
                    Log.Error(e, $"Quick {what} at {Name(use?.Owner)} failed" + (errors >= 3 ? "; A and B stay jump and the jetpack for this run" : string.Empty));
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

        /// <summary>IsAnyAltKeyPressed: held for the dump, not for the take.</summary>
        private static void Alt(ref bool __result)
        {
            if (firing != Firing.None)
                __result = firing == Firing.Dump;
        }

        /// <summary>IsAnyShiftKeyPressed, IsAnyCtrlKeyPressed and IsJoystickLastUsed: no.</summary>
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
