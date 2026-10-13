using System;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using VRage.Game.ModAPI;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// The controllers buzz on what happens to the local character, from the game's own events (Harmony postfixes, so
    /// nothing is faked):
    /// <list type="bullet">
    /// <item>a shot of a hand weapon: a pulse shaped by the gun's own recoil and rate of fire, a launcher's a heavy thump, in the
    /// other hand too while it holds the fore grip; and a light click when the trigger is pulled on an empty gun
    /// (<c>Haptics.Guns.cs</c>);</item>
    /// <item>a welder, a grinder or a hand drill working: a rumble of its own kind that follows what it touches, asked for
    /// again every frame (<c>Haptics.Tools.cs</c>);</item>
    /// <item>the character taking damage: a pattern for the kind of damage, and a slow heartbeat at low health or oxygen
    /// (<c>Haptics.Damage.cs</c>);</item>
    /// <item>a block placed from the cube builder: a short click on the dominant hand;</item>
    /// <item>movement, in both hands: the jetpack's thrust, the magnetic boots locking on and letting go, a hard landing
    /// (<c>Haptics.Movement.cs</c>).</item>
    /// </list>
    /// All of it is scaled by the "Haptic strength" setting (0 is off), and <c>Haptics=0</c> in the render debug file
    /// switches it off. A pulse goes through <see cref="VRInput.Haptic"/>, so the headset's controllers play it, or the
    /// desktop hands log it. Another player's character, a turret, a ship's tool: none of those buzz.
    /// </summary>
    internal static partial class Haptics
    {
        /// <summary>Debug: no haptics (<see cref="Rendering.RenderDebug"/> Haptics=0).</summary>
        public static bool Off { get; set; }

        // ---- What each event feels like. Amplitude 0..1 (before the strength setting), duration in seconds. ----

        /// <summary>A block placed: a click.</summary>
        public const float PlaceAmplitude = 0.6f, PlaceSeconds = 0.03f;

        private static int errors;
        private static Harmony harmony;
        private static bool lateHooked;

        /// <summary>The hand that fires and works the tools (<see cref="VRSettings.DominantHand"/>).</summary>
        public static Hand ToolHand => VRSettings.DominantHand;

        /// <summary>
        /// Each hook on its own, like the hand input's: an event that cannot be hooked loses its buzz and nothing else.
        /// </summary>
        public static void Patch(Harmony harmony)
        {
            // One shot of a hand weapon: the game calls this when the weapon's cooldown allowed it (MyCharacter.ShootInternal),
            // for a character on this machine, whoever it belongs to. (The private Shoot(Vector3, Vector3D?) is the other overload.)
            Hook(harmony, "firing", () => AccessTools.Method(typeof(MyAutomaticRifleGun), nameof(MyAutomaticRifleGun.Shoot),
                new[] { typeof(MyShootActionEnum), typeof(Vector3), typeof(Vector3D?), typeof(string) }), nameof(AfterShoot));

            PatchGuns(harmony);
            PatchTools(harmony);
            PatchDamage(harmony);

            // The cube builder is hooked later (AfterSessionStart): MyCubeBuilder's static constructor looks up definitions, and
            // Harmony runs it as it patches, which this early (before the game has loaded any) would fail, and for good.
            Haptics.harmony = harmony;
            Hook(harmony, "block placed (session start)", () => AccessTools.Method(typeof(MySession), nameof(MySession.BeforeStartComponents)),
                nameof(AfterSessionStart));
        }

        /// <summary>
        /// Every block the cube builder places, however it got there (a click, the hand block placer, symmetry), ends in this
        /// overload; its result is true when the request was made.
        /// </summary>
        internal static System.Reflection.MethodBase BlockPlacedTarget() => AccessTools.Method(typeof(MyCubeBuilder), "AddBlocksToBuildQueueOrSpawn",
            new[] { typeof(Sandbox.Definitions.MyCubeBlockDefinition), typeof(MatrixD).MakeByRefType(), typeof(Vector3I), typeof(Vector3I),
                typeof(Vector3I), typeof(Quaternion), typeof(MyCubeGrid.MyBlockVisuals) });

        private static void Hook(Harmony harmony, string what, Func<System.Reflection.MethodBase> target, string postfix)
        {
            PatchRegistry.Apply(what, () => harmony.Patch(target() ?? throw new MissingMethodException($"the game method for {what} was not found"),
                    postfix: new HarmonyMethod(typeof(Haptics), postfix)),
                $"Haptics ({what}) could not be hooked; it will not buzz");
        }

        // ---- The effects. Internal, so a probe can drive them without a game. ----

        /// <summary>One pulse, scaled by the strength setting; nothing when haptics are off, the strength is 0 or no hands are in use.</summary>
        internal static void Pulse(Hand hand, float amplitude, float seconds)
        {
            float strength = VRSettings.HapticStrength;
            if (Off || !VRInput.Active || strength <= 0f || amplitude <= 0f)
                return;
            VRInput.Haptic(hand, Math.Min(1f, amplitude * strength), seconds);
        }

        internal static void BlockPlaced() => Pulse(ToolHand, PlaceAmplitude, PlaceSeconds);

        /// <summary>The character is the one playing on this machine.</summary>
        internal static bool IsLocal(MyCharacter character) => character != null && ReferenceEquals(character, MySession.Static?.LocalCharacter);

        // ---- The game's events. A hook that throws would take the game's frame with it, so each one answers for itself. ----

        private static void AfterShoot(MyAutomaticRifleGun __instance, MyShootActionEnum action)
        {
            try
            {
                if (action == MyShootActionEnum.PrimaryAction && IsLocal(__instance.Owner))
                    Shot(__instance);
            }
            catch (Exception e)
            {
                Failed(e, "firing");
            }
        }

        /// <summary>A world is loaded and its components (the cube builder among them) exist: the hooks that had to wait go on, once.</summary>
        private static void AfterSessionStart()
        {
            if (lateHooked || harmony == null)
                return;
            lateHooked = true;
            Hook(harmony, "block placed", BlockPlacedTarget, nameof(AfterBlockAdded));
        }

        private static void AfterBlockAdded(bool __result)
        {
            try
            {
                if (__result)
                {
                    BlockPlaced();
                    BuildMode.NotePlaced();
                }
            }
            catch (Exception e)
            {
                Failed(e, "block placed");
            }
        }

        private static void Failed(Exception e, string what)
        {
            if (errors++ < 3)
                Log.Error(e, $"Haptics ({what}) failed");
        }
    }
}
