using System;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// The controllers buzz on what happens to the local character, from the game's own events (Harmony postfixes, so
    /// nothing is faked):
    /// <list type="bullet">
    /// <item>a shot of a hand weapon: a short, strong pulse on the dominant hand;</item>
    /// <item>a welder, a grinder or a hand drill working on a target: a light buzz on the dominant hand, asked for again
    /// every frame it is in contact (each pulse is a little longer than a frame, so it does not stutter);</item>
    /// <item>the character taking impact damage (a collision, a fall): both hands, harder the more damage;</item>
    /// <item>a block placed from the cube builder: a short click on the dominant hand.</item>
    /// </list>
    /// All of it is scaled by the "Haptic strength" setting (0 is off), and <c>Haptics=0</c> in the render debug file
    /// switches it off. A pulse goes through <see cref="VRInput.Haptic"/>, so the headset's controllers play it, or the
    /// desktop hands log it. Another player's character, a turret, a ship's tool: none of those buzz.
    /// </summary>
    internal static class Haptics
    {
        /// <summary>Debug: no haptics (<see cref="Rendering.RenderDebug"/> Haptics=0).</summary>
        public static bool Off { get; set; }

        // ---- What each event feels like. Amplitude 0..1 (before the strength setting), duration in seconds. ----

        /// <summary>A shot: short and strong.</summary>
        public const float FireAmplitude = 0.9f, FireSeconds = 0.06f;

        /// <summary>A tool in contact: light, and a little over a frame long so the next frame's pulse takes over without a gap.</summary>
        public const float ToolAmplitude = 0.25f, ToolSeconds = 0.06f;

        /// <summary>A block placed: a click.</summary>
        public const float PlaceAmplitude = 0.6f, PlaceSeconds = 0.03f;

        /// <summary>An impact: from a bump to a full hit, longer the harder.</summary>
        public const float ImpactMinAmplitude = 0.35f, ImpactMaxAmplitude = 1f;

        public const float ImpactMinSeconds = 0.1f, ImpactMaxSeconds = 0.35f;

        /// <summary>The damage that is a full hit. The game's own camera shake tops out at the same figure (MyCharacter.MAX_SHAKE_DAMAGE).</summary>
        public const float ImpactFullDamage = 90f;

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

            // The welder and the grinder: the base class decides every frame whether the tool has a target (EffectAction).
            Hook(harmony, "welder and grinder", () => AccessTools.Method(typeof(MyEngineerToolBase), nameof(MyEngineerToolBase.UpdateAfterSimulation)),
                nameof(AfterToolUpdate));

            // The hand drill: the same test the game uses to shake the camera while drilling.
            Hook(harmony, "hand drill", () => AccessTools.Method(typeof(MyHandDrill), nameof(MyHandDrill.UpdateAfterSimulation)),
                nameof(AfterDrillUpdate));

            // Damage the character took. DoDamage returns true when the damage was applied; a collision is "Environment".
            Hook(harmony, "impacts", () => AccessTools.Method(typeof(MyCharacter), nameof(MyCharacter.DoDamage),
                new[] { typeof(float), typeof(MyStringHash), typeof(bool), typeof(long), typeof(MyStringHash?) }), nameof(AfterDamage));

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
            try
            {
                System.Reflection.MethodBase method = target() ?? throw new MissingMethodException($"the game method for {what} was not found");
                harmony.Patch(method, postfix: new HarmonyMethod(typeof(Haptics), postfix));
            }
            catch (Exception e)
            {
                Log.Error(e, $"Haptics ({what}) could not be hooked; it will not buzz");
            }
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

        internal static void Shot() => Pulse(ToolHand, FireAmplitude, FireSeconds);

        internal static void ToolContact() => Pulse(ToolHand, ToolAmplitude, ToolSeconds);

        internal static void BlockPlaced() => Pulse(ToolHand, PlaceAmplitude, PlaceSeconds);

        /// <summary>Both hands, by how much damage was done.</summary>
        internal static void Impact(float damage)
        {
            if (!(damage > 0f))
                return;
            float amplitude = ImpactAmplitude(damage), seconds = ImpactSeconds(damage);
            Pulse(Hand.Left, amplitude, seconds);
            Pulse(Hand.Right, amplitude, seconds);
        }

        /// <summary>0 for no damage up to <see cref="ImpactFullDamage"/> and beyond, linear.</summary>
        internal static float Fraction(float damage) => MathHelper.Clamp(damage / ImpactFullDamage, 0f, 1f);

        internal static float ImpactAmplitude(float damage) => MathHelper.Lerp(ImpactMinAmplitude, ImpactMaxAmplitude, Fraction(damage));

        internal static float ImpactSeconds(float damage) => MathHelper.Lerp(ImpactMinSeconds, ImpactMaxSeconds, Fraction(damage));

        /// <summary>The damage that is a collision (a fall comes through here too: the game has no separate fall damage).</summary>
        internal static bool IsImpact(MyStringHash damageType) => damageType == MyDamageType.Environment;

        /// <summary>The character is the one playing on this machine.</summary>
        internal static bool IsLocal(MyCharacter character) => character != null && ReferenceEquals(character, MySession.Static?.LocalCharacter);

        // ---- The game's events. A hook that throws would take the game's frame with it, so each one answers for itself. ----

        private static void AfterShoot(MyAutomaticRifleGun __instance, MyShootActionEnum action)
        {
            try
            {
                if (action == MyShootActionEnum.PrimaryAction && IsLocal(__instance.Owner))
                    Shot();
            }
            catch (Exception e)
            {
                Failed(e, "firing");
            }
        }

        private static void AfterToolUpdate(MyEngineerToolBase __instance, MyCharacter ___Owner, MyShootActionEnum? ___EffectAction)
        {
            try
            {
                if (__instance.IsShooting && !__instance.IsHeatingUp && ___EffectAction == MyShootActionEnum.PrimaryAction && IsLocal(___Owner))
                    ToolContact();
            }
            catch (Exception e)
            {
                Failed(e, "welder and grinder");
            }
        }

        private static void AfterDrillUpdate(MyHandDrill __instance, bool ___m_objectInDrillingRange)
        {
            try
            {
                if (__instance.IsShooting && ___m_objectInDrillingRange && __instance.IsDrillingAnObject && IsLocal(__instance.Owner))
                    ToolContact();
            }
            catch (Exception e)
            {
                Failed(e, "hand drill");
            }
        }

        private static void AfterDamage(MyCharacter __instance, float damage, MyStringHash damageType, bool __result)
        {
            try
            {
                if (__result && IsImpact(damageType) && IsLocal(__instance))
                    Impact(damage);
            }
            catch (Exception e)
            {
                Failed(e, "impacts");
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
                    BlockPlaced();
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
