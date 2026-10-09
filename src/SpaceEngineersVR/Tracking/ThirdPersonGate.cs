using System;
using System.Diagnostics;
using HarmonyLib;
using Sandbox.Engine.Utils;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.ModAPI.Interfaces;
using VRageMath;

namespace SpaceEngineersVR.Tracking
{
    /// <summary>
    /// Third person is off by default in VR (<see cref="VRSettings.ThirdPerson"/>): your own view stays first person on
    /// foot and in every seat, the camera-mode key does nothing, and a save or seat that remembers third person is put
    /// back to first person. It is the gate the game has for a world that disables third person (the world setting
    /// Enable3rdPersonView), applied from our setting instead; the world's setting is not touched, because it is saved
    /// with the world and shared in multiplayer.
    /// </summary>
    /// <remarks>
    /// Every way into third person ends in a write to the controller's IsInFirstPersonView, so the gate is the setter
    /// of the two controllers that have the flag, the same place the game itself refuses it for a world with third
    /// person off: MyCharacter.IsInFirstPersonView (MyCharacter.cs:1364) and MyCockpit.IsInFirstPersonView
    /// (MyCockpit.cs:404). Through those pass the camera-mode key (MyGuiScreenGamePlay.SwitchCamera:1502), a saved
    /// camera that was third person (MySession.LoadCamera:3996 and SetEntityCameraPosition:4255, both via
    /// SetCameraController(ThirdPersonSpectator), :4225), a seat's default third-person view (IsDefault3rdView,
    /// :4264), a seat that is third-person-only (MyCockpit.ControlCamera:2565), the pilot's remembered view
    /// (MyCockpit.cs:709, :1749) and the character loading (MyCharacter.cs:3169). Around them:
    /// MyGuiScreenGamePlay.CanSwitchCamera (:134, the key and the radial menu entry) is false, so the key falls to the
    /// game's own "third person is off" branch (:401), and SwitchCamera itself does nothing for the callers that skip
    /// that check (the radial menu action, MyCharacter.Zoom:7850). MyShipController.Init (:1666) lets a seat that the
    /// block definition makes third-person-only be first person, as the game does for its world setting (:1671).
    /// Switching the setting off while in third person: MyThirdPersonSpectator.Update runs every frame the controller is
    /// not first person (MySession.cs:2573), so it puts the controller back there.
    /// The game's death camera (MyCharacter.GetViewMatrix:4916) writes the field itself and is left alone, as it is for a
    /// world with third person off. Every hook is its own patch: one that fails to apply is logged and the others stand.
    /// </remarks>
    internal static class ThirdPersonGate
    {
        private static bool failed;

        public static void Patch(Harmony harmony)
        {
            Hook("the character's view flag", () => harmony.Patch(
                AccessTools.PropertySetter(typeof(MyCharacter), nameof(MyCharacter.IsInFirstPersonView)),
                prefix: new HarmonyMethod(typeof(ThirdPersonGate), nameof(FirstPersonOnly))));
            Hook("the seat's view flag", () => harmony.Patch(
                AccessTools.PropertySetter(typeof(MyCockpit), nameof(MyCockpit.IsInFirstPersonView)),
                prefix: new HarmonyMethod(typeof(ThirdPersonGate), nameof(FirstPersonOnly))));
            Hook("the camera-mode check", () => harmony.Patch(
                AccessTools.PropertyGetter(typeof(MyGuiScreenGamePlay), nameof(MyGuiScreenGamePlay.CanSwitchCamera)),
                postfix: new HarmonyMethod(typeof(ThirdPersonGate), nameof(NoSwitch))));
            Hook("the camera switch", () => harmony.Patch(
                AccessTools.Method(typeof(MyGuiScreenGamePlay), nameof(MyGuiScreenGamePlay.SwitchCamera)),
                prefix: new HarmonyMethod(typeof(ThirdPersonGate), nameof(SwitchAllowed))));
            Hook("third-person-only seats", () => harmony.Patch(
                AccessTools.DeclaredMethod(typeof(MyShipController), nameof(MyShipController.Init),
                    new[] { typeof(MyObjectBuilder_CubeBlock), typeof(MyCubeGrid) }),
                postfix: new HarmonyMethod(typeof(ThirdPersonGate), nameof(FirstPersonSeat))));
            Hook("leaving third person when the setting goes off", () => harmony.Patch(
                AccessTools.DeclaredMethod(typeof(MyThirdPersonSpectator), nameof(MyThirdPersonSpectator.Update), Type.EmptyTypes),
                prefix: new HarmonyMethod(typeof(ThirdPersonGate), nameof(LeaveThirdPerson)) { priority = Priority.First }));
            Hook("the third-person camera note", () => harmony.Patch(
                AccessTools.DeclaredMethod(typeof(MyThirdPersonSpectator), nameof(MyThirdPersonSpectator.Update), Type.EmptyTypes),
                postfix: new HarmonyMethod(typeof(ThirdPersonGate), nameof(NoteCamera))));
            Hook("the first-person start (world load)", () => harmony.Patch(
                AccessTools.DeclaredMethod(typeof(MySession), "LoadWorld", new[] { typeof(MyObjectBuilder_Checkpoint), typeof(MyObjectBuilder_Sector) }),
                prefix: new HarmonyMethod(typeof(ThirdPersonGate), nameof(StartLoading))));
            Hook("the first-person start (world ready)", () => harmony.Patch(
                AccessTools.DeclaredMethod(typeof(MySession), nameof(MySession.BeforeStartComponents), Type.EmptyTypes),
                postfix: new HarmonyMethod(typeof(ThirdPersonGate), nameof(StartReady))));
            Hook("the first-person start (spawn)", () => harmony.Patch(
                AccessTools.DeclaredMethod(typeof(MyEntityController), nameof(MyEntityController.TakeControl),
                    new[] { typeof(global::Sandbox.Game.Entities.IMyControllableEntity) }),
                prefix: new HarmonyMethod(typeof(ThirdPersonGate), nameof(StartSpawn))));
        }

        // ---- A view starts in first person. ----

        /// <summary>How long after the world is ready, or the player's character takes control, the view is still just starting, seconds.</summary>
        private const double StartSeconds = 6.0;

        /// <summary>While a world loads: the longest it is allowed to take before the view stops counting as starting, seconds.</summary>
        private const double LoadSeconds = 300.0;

        private static long startingUntil;
        private static bool startNoted;

        /// <summary>
        /// The view is starting: a world is loading or has just loaded, or the player's character has just spawned. In VR
        /// that is always first person, whether the game was saved in third person, starts the new character in it or
        /// brings the camera back to where it was; "Allow third person" only lets the player switch to it themselves
        /// (the camera-mode key ends the starting at once). Game thread.
        /// </summary>
        public static bool Starting => Stopwatch.GetTimestamp() < startingUntil;

        private static void Start(double seconds, string why)
        {
            long until = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
            if (!Starting)
                startNoted = false;
            startingUntil = until;
            if (VRSettings.ThirdPerson)
                Log.Info($"Third person is allowed, but a {why} starts in first person");
        }

        /// <summary>The player has chosen: the camera-mode key counts, and what follows is theirs.</summary>
        private static void EndStarting() => startingUntil = 0;

        private static void StartLoading() => Start(LoadSeconds, "world load");

        private static void StartReady() => Start(StartSeconds, "world");

        /// <summary>The local player's character takes control from nothing (a new game, a respawn): not from a seat the player left.</summary>
        private static void StartSpawn(MyEntityController __instance, global::Sandbox.Game.Entities.IMyControllableEntity entity)
        {
            try
            {
                if (!(entity is MyCharacter) || MySession.Static == null || __instance.Player == null
                    || __instance.Player != MySession.Static.LocalHumanPlayer)
                    return;
                global::Sandbox.Game.Entities.IMyControllableEntity was = __instance.ControlledEntity;
                if (was == null || was is MyCharacter)
                    Start(StartSeconds, "spawn");
            }
            catch (Exception e)
            {
                Log.Error(e, "Third-person gate could not tell a spawn");
            }
        }

        private static void Hook(string what, Action patch)
        {
            try
            {
                patch();
            }
            catch (Exception e)
            {
                // That way into third person stays the game's; nothing else depends on it.
                Log.Error(e, "Third-person gate could not hook " + what);
            }
        }

        /// <summary>The game's gate for a world with third person off, from our setting: asking for third person gets first person.</summary>
        private static void FirstPersonOnly(ref bool value)
        {
            if (value)
                return;
            if (!VRSettings.ThirdPerson)
                value = true;
            else if (Starting)
            {
                value = true;
                NoteStart();
            }
        }

        /// <summary>One line per start, the first time the gate turns a third-person view back.</summary>
        private static void NoteStart()
        {
            if (startNoted)
                return;
            startNoted = true;
            Log.Info("Third person is allowed, and the game's starting view was third person: first person instead");
        }

        /// <summary>The camera-mode key and the radial menu entry have nothing to switch to.</summary>
        private static void NoSwitch(ref bool __result)
        {
            if (!VRSettings.ThirdPerson)
                __result = false;
        }

        private static bool SwitchAllowed()
        {
            if (!VRSettings.ThirdPerson)
                return false;
            EndStarting();
            return true;
        }

        /// <summary>A seat whose block is third-person-only is a first-person seat here, as the game makes it for a world with third person off.</summary>
        private static void FirstPersonSeat(MyShipController __instance)
        {
            try
            {
                if (!VRSettings.ThirdPerson)
                    __instance.EnableFirstPersonView = true;
            }
            catch (Exception e)
            {
                Log.Error(e, "Third-person gate could not make a seat first person");
            }
        }

        /// <summary>
        /// The game calls this every frame the camera controller is not first person. With the setting off that is
        /// the setting having just been switched off (or something the gate does not cover): put the controller back.
        /// </summary>
        private static void LeaveThirdPerson()
        {
            bool starting = VRSettings.ThirdPerson && Starting;
            if (failed || (VRSettings.ThirdPerson && !starting))
                return;
            try
            {
                IMyCameraController camera = MySession.Static?.CameraController;
                if (camera == null || camera.IsInFirstPersonView || !camera.EnableFirstPersonView)
                    return;
                if (camera is MyCharacter character ? character.IsDead : !(camera is MyCockpit))
                    return;
                camera.IsInFirstPersonView = true;
                if (camera.IsInFirstPersonView)
                    Log.Info(starting ? "Third person is allowed, but the view is starting: first person" : "Third person is off: your view is back to first person");
            }
            catch (Exception e)
            {
                failed = true;
                Log.Error(e, "Third-person gate failed while leaving third person; it stops trying");
            }
        }

        private static bool noteFailed;
        private static long lastNoteAt;
        private static string lastNoteKey;
        private static AccessTools.FieldRef<MyThirdPersonSpectator, Vector3D> cameraAt, targetAt;
        private static AccessTools.FieldRef<MyThirdPersonSpectator, double> safeMaximum;

        /// <summary>
        /// A line in the log while your own character is in third person: how far from the head the game put the camera,
        /// how far it lets the camera go, and whether it has forced the view back to first person. The game pulls the
        /// camera in (MyThirdPersonSpectator.HandleIntersection:676) or forces first person (IsCameraForced:399, so
        /// MyCharacter.ForceFirstPersonCamera:1404, and GetViewMatrix:4938 builds the view from the head) where something
        /// is just above or behind the character; the view then hardly changes from first person. Nothing here changes it.
        /// One line when the distance or the forcing changes (at most one a second), and every ten seconds otherwise.
        /// </summary>
        private static void NoteCamera()
        {
            if (noteFailed || !VRSettings.ThirdPerson)
                return;
            try
            {
                if (!(MySession.Static?.CameraController is MyCharacter character) || character.IsDead || character.IsInFirstPersonView)
                    return;
                MyThirdPersonSpectator spectator = MyThirdPersonSpectator.Static;
                if (spectator == null)
                    return;
                if (cameraAt == null)
                {
                    cameraAt = AccessTools.FieldRefAccess<MyThirdPersonSpectator, Vector3D>("m_positionSafe");
                    targetAt = AccessTools.FieldRefAccess<MyThirdPersonSpectator, Vector3D>("m_target");
                    safeMaximum = AccessTools.FieldRefAccess<MyThirdPersonSpectator, double>("m_safeMaximumDistance");
                }
                double away = (cameraAt(spectator) - targetAt(spectator)).Length();
                bool forced = spectator.IsCameraForced();
                string key = Math.Round(away * 4.0) / 4.0 + "/" + forced;
                long now = Environment.TickCount;
                if (now - lastNoteAt < 1000 || (key == lastNoteKey && now - lastNoteAt < 10000))
                    return;
                lastNoteAt = now;
                lastNoteKey = key;
                Log.Info($"Third person on foot: the camera is {away:F2} m from the head, the game lets it go up to {safeMaximum(spectator):F1} m, forced to first person: {(forced ? "yes" : "no")}");
            }
            catch (Exception e)
            {
                noteFailed = true;
                Log.Error(e, "Third-person camera note failed; it stops");
            }
        }
    }
}
