using System;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.Components;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// Damage to the local character, by kind: a sharp tap on the side a hit came from, a heavy thump from an explosion, a
    /// bump from a collision or a fall, and a slow heartbeat while health or the suit's oxygen is low. Damage that burns or
    /// starves over time (fire, cold, radiation, no air) is left to the heartbeat, so it does not buzz every tick.
    /// </summary>
    internal static partial class Haptics
    {
        // ---- What each kind of damage feels like ----

        /// <summary>An impact (a collision, a fall): from a bump to a full hit, longer the harder.</summary>
        public const float ImpactMinAmplitude = 0.35f, ImpactMaxAmplitude = 1f;

        public const float ImpactMinSeconds = 0.1f, ImpactMaxSeconds = 0.35f;

        /// <summary>The damage that is a full hit. The game's own camera shake tops out at the same figure (MyCharacter.MAX_SHAKE_DAMAGE).</summary>
        public const float ImpactFullDamage = 90f;

        /// <summary>A strike (a bullet, a bite): short and sharp on one hand, a full one at <see cref="StrikeFullDamage"/>.</summary>
        public const float StrikeMinAmplitude = 0.55f, StrikeMaxAmplitude = 1f, StrikeSeconds = 0.05f, StrikeFullDamage = 30f;

        /// <summary>A strike from an unknown direction: both hands, softer.</summary>
        public const float StrikeBothShare = 0.7f;

        /// <summary>A blast: both hands, heavy and long, a full one at <see cref="BlastFullDamage"/>.</summary>
        public const float BlastMinAmplitude = 0.6f, BlastMaxAmplitude = 1f, BlastMinSeconds = 0.2f, BlastMaxSeconds = 0.55f, BlastFullDamage = 100f;

        /// <summary>How far to one side of the head (0..1 of the way across) a hit has to come from to be one hand's.</summary>
        public const float SideReach = 0.35f;

        // ---- Heartbeat: a "lub-dub" every HeartbeatSeconds while health or the suit's oxygen is low ----

        public const float HeartbeatSeconds = 1.5f, DubAfterSeconds = 0.2f;

        public const float LubMinAmplitude = 0.35f, LubMaxAmplitude = 0.65f, LubSeconds = 0.08f, DubShare = 0.7f, DubSeconds = 0.06f;

        /// <summary>Debug: the heartbeat at this severity (0..1) whatever the character's state (the drive's <c>haptics beat</c>).</summary>
        internal static float? BeatTest { get; set; }

        private static double nextBeat, dubAt = double.MaxValue;
        private static float dubSeverity;
        private static bool beating;

        // The hit being applied, from the damage call that carries it (see BeforeHit): where a bullet came from.
        [ThreadStatic] private static bool hitKnown;
        [ThreadStatic] private static Vector3D hitVelocity;
        [ThreadStatic] private static long hitAttacker;

        // ---- The mapping. Pure where it can be, so the drive can play it without a game. ----

        /// <summary>The damage that is a collision (a fall comes through here too, as does a block collapsing).</summary>
        internal static bool IsImpact(MyStringHash type) =>
            type == MyDamageType.Environment || type == MyDamageType.Fall || type == MyDamageType.Deformation
            || type == MyDamageType.Squeez || type == MyDamageType.Destruction;

        internal static bool IsBlast(MyStringHash type) => type == MyDamageType.Explosion || type == MyDamageType.Rocket || type == MyDamageType.Mine;

        /// <summary>Damage that comes in a steady trickle, which the heartbeat covers.</summary>
        internal static bool IsTrickle(MyStringHash type) =>
            type == MyDamageType.Fire || type == MyDamageType.Temperature || type == MyDamageType.Radioactivity
            || type == MyDamageType.Asphyxia || type == MyDamageType.LowPressure || type == MyDamageType.Hunger
            || type == MyDamageType.Weather || type == MyDamageType.Thruster || type == MyDamageType.OutOfBounds
            || type == MyDamageType.Suicide || type == MyDamageType.Debug;

        /// <summary>0 for no damage up to <paramref name="full"/> and beyond, linear.</summary>
        internal static float Fraction(float damage, float full) => MathHelper.Clamp(damage / full, 0f, 1f);

        internal static float ImpactAmplitude(float damage) => MathHelper.Lerp(ImpactMinAmplitude, ImpactMaxAmplitude, Fraction(damage, ImpactFullDamage));

        internal static float ImpactSeconds(float damage) => MathHelper.Lerp(ImpactMinSeconds, ImpactMaxSeconds, Fraction(damage, ImpactFullDamage));

        /// <summary>
        /// The pattern for damage of this kind. <paramref name="side"/> is where a hit came from, -1 (the left of the head) to 1
        /// (the right), or null when that is not known.
        /// </summary>
        internal static void Hurt(MyStringHash type, float damage, float? side)
        {
            if (!(damage > 0f) || IsTrickle(type))
                return;
            if (IsImpact(type))
            {
                float amplitude = ImpactAmplitude(damage), seconds = ImpactSeconds(damage);
                Pulse(Hand.Left, amplitude, seconds);
                Pulse(Hand.Right, amplitude, seconds);
            }
            else if (IsBlast(type))
            {
                float share = Fraction(damage, BlastFullDamage);
                float amplitude = MathHelper.Lerp(BlastMinAmplitude, BlastMaxAmplitude, share), seconds = MathHelper.Lerp(BlastMinSeconds, BlastMaxSeconds, share);
                Pulse(Hand.Left, amplitude, seconds);
                Pulse(Hand.Right, amplitude, seconds);
            }
            else
            {
                float amplitude = MathHelper.Lerp(StrikeMinAmplitude, StrikeMaxAmplitude, Fraction(damage, StrikeFullDamage));
                if (side is float s && s >= SideReach)
                    Pulse(Hand.Right, amplitude, StrikeSeconds);
                else if (side is float l && l <= -SideReach)
                    Pulse(Hand.Left, amplitude, StrikeSeconds);
                else
                {
                    Pulse(Hand.Left, amplitude * StrikeBothShare, StrikeSeconds);
                    Pulse(Hand.Right, amplitude * StrikeBothShare, StrikeSeconds);
                }
            }
        }

        /// <summary>
        /// Which side of the head a hit came from: along the direction the bullet travelled, or failing that towards whoever
        /// dealt it. Null when neither is known (a fall, a hit from nowhere in particular) or it came from straight ahead
        /// or behind, which the caller treats as both hands.
        /// </summary>
        private static float? HitSide()
        {
            var camera = MySector.MainCamera;
            if (camera == null)
                return null;
            Vector3D from;
            if (hitKnown && hitVelocity.LengthSquared() > 0.01)
                from = -hitVelocity;
            else if (hitAttacker != 0 && MyEntities.TryGetEntityById(hitAttacker, out MyEntity attacker) && attacker != null)
                from = attacker.PositionComp.GetPosition() - camera.Position;
            else
                return null;
            if (from.LengthSquared() < 0.01)
                return null;
            from.Normalize();
            return (float)Vector3D.Dot(from, camera.WorldMatrix.Right);
        }

        // ---- Heartbeat ----

        /// <summary>How low the character is, 0 (fine) to 1 (none left), the worse of health and the suit's oxygen.</summary>
        internal static float Danger(MyCharacter character)
        {
            float danger = 0f;
            MyCharacterStatComponent stats = character.StatComp;
            if (stats != null)
            {
                float critical = MyCharacterStatComponent.HEALTH_RATIO_CRITICAL;
                danger = Math.Max(danger, 1f - Math.Min(stats.HealthRatio, critical) / critical);
                if (stats.HealthRatio <= critical)
                    danger = Math.Max(danger, 0.01f);
            }
            return Math.Max(danger, Common.SuitAir.Shortness(character, out _));
        }

        /// <summary>Heartbeat for the strength of danger: the lub now, the dub a moment later (<see cref="BeatFrame"/>).</summary>
        internal static void Lub(float severity)
        {
            float amplitude = MathHelper.Lerp(LubMinAmplitude, LubMaxAmplitude, severity);
            Pulse(Hand.Left, amplitude, LubSeconds);
            Pulse(Hand.Right, amplitude, LubSeconds);
            dubSeverity = severity;
            dubAt = Seconds() + DubAfterSeconds;
        }

        /// <summary>One frame of the heartbeat for a danger of <paramref name="severity"/> (0 stops it). Called every frame the character updates.</summary>
        internal static void BeatFrame(float severity)
        {
            double now = Seconds();
            if (!(severity > 0f) || !VRSettings.HapticHeartbeat)
            {
                beating = false;
                dubAt = double.MaxValue;
                return;
            }
            if (!beating)
            {
                beating = true;
                nextBeat = now;
            }
            if (now >= dubAt)
            {
                dubAt = double.MaxValue;
                float amplitude = MathHelper.Lerp(LubMinAmplitude, LubMaxAmplitude, dubSeverity) * DubShare;
                Pulse(Hand.Left, amplitude, DubSeconds);
                Pulse(Hand.Right, amplitude, DubSeconds);
            }
            if (now >= nextBeat)
            {
                // A long pause (a loading screen, a pause menu) is not owed beats.
                nextBeat = Math.Max(nextBeat + HeartbeatSeconds, now + HeartbeatSeconds * 0.5);
                Lub(severity);
            }
        }

        // ---- The game's events ----

        private static void PatchDamage(Harmony harmony)
        {
            // Damage the character took. DoDamage returns true when the damage was applied.
            Hook(harmony, "damage", () => AccessTools.Method(typeof(MyCharacter), nameof(MyCharacter.DoDamage),
                new[] { typeof(float), typeof(MyStringHash), typeof(bool), typeof(long), typeof(MyStringHash?) }), nameof(AfterDamage));

            // A hit from another entity arrives through the damage interface, which knows where it came from (the plain
            // DoDamage it ends in does not); the hit is kept for the length of that call.
            PatchRegistry.Apply("damage direction", () =>
            {
                System.Reflection.MethodInfo target = null;
                foreach (System.Reflection.MethodInfo method in typeof(MyCharacter).GetMethods(
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
                {
                    if (method.Name.EndsWith("IMyDestroyableObject.DoDamage", StringComparison.Ordinal))
                        target = method;
                }
                harmony.Patch(target ?? throw new MissingMethodException("the character's damage interface was not found"),
                    prefix: new HarmonyMethod(typeof(Haptics), nameof(BeforeHit)), finalizer: new HarmonyMethod(typeof(Haptics), nameof(AfterHit)));
            }, "Haptics (damage direction) could not be hooked; hits will be felt in both hands");

            // The heartbeat is stepped by the local character's own update.
            Hook(harmony, "heartbeat", () => AccessTools.Method(typeof(MyCharacter), nameof(MyCharacter.UpdateAfterSimulation)), nameof(AfterCharacterUpdate));
        }

        private static void BeforeHit(MyCharacter __instance, MyHitInfo? hitInfo, long attackerId)
        {
            try
            {
                if (!IsLocal(__instance))
                    return;
                hitKnown = hitInfo.HasValue;
                hitVelocity = hitInfo?.Velocity ?? Vector3D.Zero;
                hitAttacker = attackerId;
            }
            catch (Exception e)
            {
                Failed(e, "damage direction");
            }
        }

        private static void AfterHit()
        {
            hitKnown = false;
            hitVelocity = Vector3D.Zero;
            hitAttacker = 0;
        }

        private static void AfterDamage(MyCharacter __instance, float damage, MyStringHash damageType, bool __result)
        {
            try
            {
                if (__result && IsLocal(__instance) && !IsTrickle(damageType))
                    Hurt(damageType, damage, IsImpact(damageType) || IsBlast(damageType) ? null : HitSide());
            }
            catch (Exception e)
            {
                Failed(e, "damage");
            }
        }

        private static void AfterCharacterUpdate(MyCharacter __instance)
        {
            try
            {
                if (IsLocal(__instance))
                {
                    BeatFrame(__instance.IsDead ? 0f : BeatTest ?? Danger(__instance));
                    MovementFrame(__instance);
                }
            }
            catch (Exception e)
            {
                Failed(e, "heartbeat");
            }
        }
    }
}
