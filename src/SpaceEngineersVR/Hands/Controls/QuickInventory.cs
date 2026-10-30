using System;
using System.Collections.Generic;
using HarmonyLib;
using Sandbox;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Gui;
using Sandbox.Game.GUI;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Input.Bindings;
using VRage.Audio;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.Entity.UseObject;
using VRage.ModAPI;

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
    /// <para>The game's own operations, as its middle mouse runs them in MyGuiScreenGamePlay.BuildPlannerControls, called on
    /// the target: the take is its Withdraw1 (with a block in hand and the planner empty it first puts that block in the
    /// planner, with its notification and sound; then MyTerminalInventoryController.Withdraw takes the planner's components
    /// from the container, "Withdraw successful" or what is missing, and an empty planner says so), the dump its DepositOre
    /// (MyTerminalInventoryController.DepositAll, "Deposit successful" or how many kinds found no room, and the detector's
    /// prompts built again), the production its AddProduction1 (with a planner that is not empty,
    /// MyTerminalInventoryController.AddComponentsToProduction queues its components in the grid's assemblers, "put to
    /// production" or how many kinds found no assembler; an empty planner says so). The controller is internal to the game,
    /// so its operations are reached by name (<see cref="Patch"/>). The middle mouse's own checks are <see cref="IsInventory"/>'s:
    /// the use object offers the Build Planner and opens an inventory, and the block is friendly or the admin may use any
    /// terminal.</para>
    /// <para>What the target is: what the dominant hand points at, the character detector's own use object, with a hand tool in
    /// it (a welder, a grinder or a hand drill; not a gun, whose aim is the gun's, and not the block placer a block or the
    /// colour tool is held with) or with nothing in it. The plugin casts that from the hand (<see cref="HandUse"/>) as the
    /// game casts it from the head for the middle mouse, so the game's answer stands and nothing answers for it. When that is
    /// not an inventory, it is the block the right hand is on (<see cref="GrabUse"/>: within reach of the palm, nothing solid
    /// in the way, highlighted with a light tick, only while the hand holds no tool, and the block the grip has taken hold of).
    /// With the dominant hand left the tool is in the left hand and points, so the right hand is the empty one that rests. Either way it
    /// counts only when <see cref="IsInventory"/> says the game would act. With a block in hand (<see cref="BuildMode"/>) there is
    /// no target at all: the stick clicks are building's there.</para>
    /// <para>The buttons: a new press counts only with the left stick click not held to build (<see cref="BuildMode.Modifier"/>:
    /// a paste or the voxel brush) and not with the colour tool in hand (A and B are its brush). The left stick click is the
    /// withdraw. The right stick click is the production with the right grip open, where it is nothing else: not with a hand
    /// weapon that reloads (<see cref="Reload"/>); and the deposit with the right grip held, when that grip is the Use's (the
    /// ray's, or a grab's on what the hand is on) and not another feature's (the hull, the helmet, an item). These are the
    /// bindings' container actions, which shadow sprint, the zero-g roll and reload here (ControllerBindings), except the
    /// production where the game would refuse it (<see cref="ProductionFree"/>). A and B are jump and the jetpack here as everywhere.</para>
    /// <para>The grip's Use waits: a right grip that goes down at a container is the Use key there, and its Use is held back
    /// for as long as the grip is held (<see cref="GripClaim.HoldUse"/>: the ray Use in <see cref="VRControls"/> and a grab's in
    /// <see cref="GrabUse"/> ask <see cref="GripClaim.UseWaits"/>), so the click can deposit without also opening the inventory. When the grip is let go the
    /// Use is done once, on what the grip would have used at the press (the detector's use object then for the ray, the
    /// grab's own for a grab), unless a deposit was taken meanwhile (<see cref="GripClaim.DropUse"/>), which drops it. The held-back
    /// grip keeps its claim (<see cref="GripClaim"/> decides once per press): the hull and ladder grab, asked after the
    /// grab at 30, do not take a grip that was already the Use's, and the ray Use is not claimed by anything. Anywhere else
    /// the grip is as it was.</para>
    /// </remarks>
    internal static class QuickInventory
    {
        /// <summary>The hand points at an inventory block, or rests on one, where the stick clicks act this frame (for the control hint).</summary>
        public static bool Ready { get; private set; }


        private enum Firing
        {
            Take,
            Dump,
            Produce,
        }

        private static bool hooked, backedRay;
        private static IMyUseObject backedUse;
        private static readonly FeatureGuard guard = new FeatureGuard(nameof(QuickInventory));

        // MyTerminalInventoryController's operations (internal to the game).
        private delegate List<MyIdentity.BuildPlanItem.Component> WithdrawCall(MyEntity interactedEntity, MyInventory[] inventories,
            ref HashSet<MyInventory> usedTargetInventories, int? persistentMultiple);

        private static WithdrawCall withdraw;
        private static Func<List<MyIdentity.BuildPlanItem.Component>, string> missingText;
        private static Func<MyInventory, MyEntity, int> depositAll;
        private static Func<MyEntity, int?, int> addToProduction;

        public static void Patch(Harmony harmony)
        {
            try
            {
                // All or none: a take without the dump's or the production's operation would leave a click that does nothing.
                Type controller = AccessTools.TypeByName("Sandbox.Game.Gui.MyTerminalInventoryController")
                                  ?? throw new MissingMemberException("MyTerminalInventoryController");
                withdraw = Call<WithdrawCall>(controller, "Withdraw",
                    typeof(MyEntity), typeof(MyInventory[]), typeof(HashSet<MyInventory>).MakeByRefType(), typeof(int?));
                missingText = Call<Func<List<MyIdentity.BuildPlanItem.Component>, string>>(controller, "GetMissingComponentsText",
                    typeof(List<MyIdentity.BuildPlanItem.Component>));
                depositAll = Call<Func<MyInventory, MyEntity, int>>(controller, "DepositAll", typeof(MyInventory), typeof(MyEntity));
                addToProduction = Call<Func<MyEntity, int?, int>>(controller, "AddComponentsToProduction", typeof(MyEntity), typeof(int?));
                hooked = true;
            }
            catch (Exception e)
            {
                hooked = false;
                PatchRegistry.Lost(e, "Quick take and dump could not reach the game's inventory operations; the stick clicks stay sprint and reload at a container");
                throw;
            }
            Log.Info("Quick take and dump: the game's Build Planner operations (MyTerminalInventoryController Withdraw, DepositAll, AddComponentsToProduction); " +
                     "with a welder, grinder or drill in the dominant hand or the hand empty, pointing at an inventory block, or the right hand on one, on foot, " +
                     $"{ActionNames.Name(VRAction.QuickTake)} withdraws the Build Planner's components, {ActionNames.Name(VRAction.QuickProduction)} puts the planner in production " +
                     $"and {ActionNames.Name(VRAction.QuickDepositAll)} deposits all");
        }

        private static T Call<T>(Type type, string name, params Type[] parameters) where T : Delegate =>
            AccessTools.MethodDelegate<T>(AccessTools.Method(type, name, parameters) ?? throw new MissingMethodException(type.Name, name));

        /// <summary>
        /// Game thread, once per frame from <see cref="VRControls.Update(double)"/>, after build mode and the colour tool and
        /// before the walking controls read the grip and the stick clicks. <paramref name="blocked"/>: a menu or the toolbar wheel has the hands.
        /// </summary>
        internal static void Frame(VRContext context, bool blocked)
        {
            IMyUseObject on = null;
            string source = null;
            bool hands = !blocked && (context == VRContext.OnFoot || context == VRContext.Jetpack);
            // With a block in hand the stick clicks are building's (BuildMode); with the colour tool A and B are its brush.
            if (hooked && guard.On && hands && !PaintMode.Active && !BuildMode.Active)
                on = Target(out source);
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
            if (dump)
                GripClaim.DropUse(Hand.Right);
            if (take || dump || produce)
            {
                Firing what = take ? Firing.Take : dump ? Firing.Dump : Firing.Produce;
                VRAction by = take ? VRAction.QuickTake : dump ? VRAction.QuickDepositAll : VRAction.QuickProduction;
                Fire(on, what, source, ControllerBindings.HandOf(by));
            }
        }

        /// <summary>
        /// The right grip's Use at a container: held back from the press (when the hand points at one or rests on one and the
        /// grip is the Use's), done when the grip is let go, unless a deposit was taken meanwhile. A grab's Use is
        /// <see cref="GrabUse"/>'s to do or drop; the ray's is done here, on what the detector had at the press.
        /// </summary>
        private static void Grip(bool grip, bool gripUse, bool ray, bool hands)
        {
            if (backedRay && !grip)
            {
                backedRay = false;
                UseRay(hands);
            }
            if (grip && VRInput.IsNewPressed(Hand.Right, VRButtons.Grip))
            {
                bool backing = Ready && gripUse;
                if (backing)
                    GripClaim.HoldUse(Hand.Right);
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
                        guard.Failed(e, "Quick take and dump could not tell what the grip would use");
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
            if (GripClaim.UseDropped(Hand.Right))
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
        /// keeps), or else the block the right hand is on or has hold of. <paramref name="source"/> says which, for the log.
        /// </summary>
        private static IMyUseObject Target(out string source)
        {
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
                guard.Failed(e, "Quick take and dump could not tell what the hand is on");
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
        /// The game's middle mouse (or Alt, or Shift + middle mouse) on <paramref name="use"/>, now, and one log line of what it
        /// reported. <paramref name="by"/> is the hand whose click it was, which gets the pulse.
        /// </summary>
        private static void Fire(IMyUseObject use, Firing action, string source, Hand by)
        {
            string what = action == Firing.Dump ? "dump" : action == Firing.Produce ? "production" : "take";
            try
            {
                MyCharacter character = MySession.Static?.LocalCharacter;
                if (character == null || MyGuiScreenGamePlay.Static == null || MySandboxGame.IsPaused)
                {
                    Log.Info($"Quick {what} at {Name(use.Owner)} ({source}): not done (no gameplay screen, or the game is paused)");
                    return;
                }
                if (!(use.Owner is MyEntity owner))
                {
                    Log.Info($"Quick {what} at {Name(use.Owner)} ({source}): not done (it is not an entity the game's operations take)");
                    return;
                }
                string reported = action == Firing.Dump ? Dump(character, owner) : action == Firing.Produce ? Produce(character, owner) : Take(character, owner);
                Haptics.Pulse(by, GrabUse.UseAmplitude, GrabUse.UseSeconds);
                Log.Info($"Quick {what} at {Name(use.Owner)} ({source}): {reported}");
            }
            catch (Exception e)
            {
                guard.Failed(e, $"Quick {what} at {Name(use?.Owner)} ({source}) failed");
            }
        }

        /// <summary>Withdraw1: with a block in hand and the planner empty, that block goes in the planner first; then the planner's components come out of the container (ProcessWithdraw).</summary>
        private static string Take(MyCharacter character, MyEntity owner)
        {
            string added = null;
            if (MyCubeBuilder.Static.ToolbarBlockDefinition != null && character.BuildPlanner.Count == 0
                && character.AddToBuildPlanner(MyCubeBuilder.Static.CurrentBlockDefinition))
            {
                added = Notify(MyNotificationSingletons.BuildPlannerComponentsAdded) + "; ";
                MyGuiAudio.PlaySound(MyGuiSounds.HudItem);
            }
            if (character.BuildPlanner.Count == 0)
                return added + EmptyPlanner();
            var used = new HashSet<MyInventory>();
            List<MyIdentity.BuildPlanItem.Component> missing = withdraw(owner, new[] { character.GetInventory() }, ref used, null);
            return added + (missing.Count == 0
                ? Notify(MyNotificationSingletons.WithdrawSuccessful)
                : Notify(MyNotificationSingletons.WithdrawFailed, missingText(missing)));
        }

        /// <summary>DepositOre: everything the suit holds that the container takes; then the detector's prompts are built again.</summary>
        private static string Dump(MyCharacter character, MyEntity owner)
        {
            int left = depositAll(character.GetInventory(), owner);
            string reported = left > 0 ? Notify(MyNotificationSingletons.DepositFailed, left) : Notify(MyNotificationSingletons.DepositSuccessful);
            character.GetDetectorComponent()?.UpdateInteractiveObjectNotification();
            return reported;
        }

        /// <summary>AddProduction1: the planner's components queued in the grid's assemblers.</summary>
        private static string Produce(MyCharacter character, MyEntity owner)
        {
            if (character.BuildPlanner.Count == 0)
                return EmptyPlanner();
            int left = addToProduction(owner, null);
            return left > 0 ? Notify(MyNotificationSingletons.PutToProductionFailed, left) : Notify(MyNotificationSingletons.PutToProductionSuccessful);
        }

        /// <summary>The game's own notice of an empty planner (it names the key that opens the G menu), and its text.</summary>
        private static string EmptyPlanner()
        {
            MyGuiScreenGamePlay.ShowEmptyBuildPlannerNotification();
            return Text(MyHud.Notifications.Get(MyNotificationSingletons.BuildPlannerEmpty));
        }

        /// <summary>Shows one of the game's notifications as BuildPlannerControls does, with its format argument if it takes one, and gives its text.</summary>
        private static string Notify(MyNotificationSingletons which, object argument = null)
        {
            if (argument != null)
                MyHud.Notifications.Get(which)?.SetTextFormatArguments(argument);
            return Text(MyHud.Notifications.Add(which));
        }

        /// <summary>A notification's text on one line, for the log.</summary>
        private static string Text(MyHudNotificationBase notification)
        {
            if (notification == null)
                return "no notification";
            try
            {
                return notification.GetText()?.Replace("\r", string.Empty).Replace('\n', ' ').Trim();
            }
            catch (Exception)
            {
                return notification.GetType().Name;
            }
        }

        private static string Name(IMyEntity owner)
        {
            string name = (owner as MyCubeBlock)?.DisplayNameText;
            return string.IsNullOrEmpty(name) ? GrabUse.Describe(owner) : $"{GrabUse.Describe(owner)} '{name}'";
        }
    }
}
