using System;
using System.Diagnostics;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Weapons;
using SpaceEngineersVR.Input;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Game.Entity;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// The welder, the grinder and the hand drill each rumble in their own way while they work. The runtime takes an
    /// amplitude and a length and no pitch, so a tool's character is how its strength moves from one frame to the next:
    /// the welder a smooth swell, the grinder an uneven grind with a bite now and then, the drill a deep, slow thrum that
    /// grows with the spin-up and with what it bites into. What the tool is touching comes from the game's own tool state
    /// (the engineer tools' effect action, the drill's drilled entity and its voxel material).
    /// </summary>
    internal static partial class Haptics
    {
        internal enum ToolKind { Welder, Grinder, Drill }

        // ---- What each tool feels like. Amplitude 0..1 (before the strength setting). ----

        /// <summary>The welder on a block: a steady buzz with a slow swell; armor sits a little firmer than a component.</summary>
        public const float WelderBase = 0.22f, WelderFirmer = 0.08f, WelderSwell = 0.1f, WelderSwellHz = 9f;

        /// <summary>The grinder on a block: a rougher level every <c>1 / GrinderCellHz</c> s, and a bite on one cell in five.</summary>
        public const float GrinderBase = 0.28f, GrinderFirmer = 0.12f, GrinderCellHz = 35f, GrinderBite = 1.35f, GrinderBiteShare = 0.2f;

        /// <summary>The drill in contact: <c>base + hardness * more</c>, thrumming at <c>DrillHz</c> from a trough of <c>DrillTrough</c> to the peak.</summary>
        public const float DrillBase = 0.2f, DrillMore = 0.55f, DrillHz = 12f, DrillTrough = 0.55f;

        /// <summary>A tool that is running and touches nothing: faint. The drill's is by its spin.</summary>
        public const float WelderIdle = 0.06f, GrinderIdle = 0.1f, DrillIdle = 0.08f;

        /// <summary>A hand on a two-handed tool: the front hand (the support hand, out on the barrel) takes the most.</summary>
        public const float FrontShare = 1f, RearShare = 0.7f;

        /// <summary>A pulse is a bit over a frame long so the next one takes over without a gap, never under or over these.</summary>
        public const float ToolMinSeconds = 0.04f, ToolMaxSeconds = 0.12f, ToolOverlap = 1.25f;

        /// <summary>The drilled voxel material, looked up this often at most (it reads the voxel storage).</summary>
        private const double MaterialEvery = 0.15;

        private static double toolLastAt = double.MinValue;
        private static double materialAt = double.MinValue;
        private static MyVoxelBase materialVoxel;
        private static float materialHardness;

        // ---- The patterns. Pure, so the drive can play them without a game. ----

        /// <summary>
        /// How strong the tool buzzes at time <paramref name="t"/> (seconds). <paramref name="firm"/> is 0..1: how hard what it
        /// touches is (armor against a component; for the drill, the material). <paramref name="spin"/> is 0..1, the drill's
        /// spin-up and spin-down (the other tools are at speed at once).
        /// </summary>
        internal static float ToolLevel(ToolKind kind, bool contact, float firm, float spin, double t)
        {
            firm = MathHelper.Clamp(firm, 0f, 1f);
            spin = MathHelper.Clamp(spin, 0f, 1f);
            switch (kind)
            {
                case ToolKind.Welder:
                    return contact
                        ? (WelderBase + WelderFirmer * firm) * (1f + WelderSwell * (float)Math.Sin(2 * Math.PI * WelderSwellHz * t))
                        : WelderIdle;
                case ToolKind.Grinder:
                {
                    double n = Noise((long)Math.Floor(t * GrinderCellHz));
                    if (!contact)
                        return GrinderIdle * (0.6f + 0.4f * (float)n);
                    float rough = n < GrinderBiteShare ? GrinderBite : 0.55f + 0.45f * (float)n;
                    return Math.Min(1f, (GrinderBase + GrinderFirmer * firm) * rough);
                }
                default:
                {
                    double beat = 0.5 + 0.5 * Math.Cos(2 * Math.PI * DrillHz * t);
                    float thrum = DrillTrough + (1f - DrillTrough) * (float)(beat * beat);
                    float body = contact ? DrillBase + DrillMore * firm : DrillIdle;
                    return body * thrum * spin;
                }
            }
        }

        /// <summary>A deterministic value in 0..1 per cell, so the grinder's grain is uneven but needs no random source.</summary>
        internal static double Noise(long cell)
        {
            ulong x = (ulong)cell * 0x9E3779B97F4A7C15UL;
            x ^= x >> 29;
            x *= 0xBF58476D1CE4E5B9UL;
            x ^= x >> 32;
            return (x & 0xFFFFFF) / (double)0x1000000;
        }

        /// <summary>
        /// A voxel material's firmness: the game has no hardness figure, only a material type and the ore it yields. Loose
        /// ground is soft, ice is brittle, rock is firm and rock with ore in it the firmest.
        /// </summary>
        internal static float VoxelFirmness(string materialType, string minedOre)
        {
            string type = (materialType ?? string.Empty).ToLowerInvariant();
            if (type.Contains("soil") || type.Contains("sand") || type.Contains("grass") || type.Contains("snow"))
                return 0.2f;
            if (type == "ice")
                return 0.45f;
            bool ore = !string.IsNullOrEmpty(minedOre) && minedOre != "Stone" && minedOre != "Ice";
            return ore ? 0.95f : 0.7f;
        }

        /// <summary>A grid is metal.</summary>
        public const float GridFirmness = 0.85f, OtherFirmness = 0.3f;

        // ---- Playing a tool's frame ----

        private static double Seconds() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        /// <summary>
        /// One game frame of a working tool: its pattern, on the dominant hand, or on both when the other hand supports it
        /// (<see cref="TwoHand.Holds"/>), the front hand firmest. Another one-handed kick's pulse is not cut short
        /// (<see cref="Kickback.Rumbling"/>).
        /// </summary>
        internal static void ToolFrame(MyEntity tool, ToolKind kind, bool contact, float firm, float spin)
        {
            double now = Seconds();
            double frame = now - toolLastAt;
            toolLastAt = now;
            // The first frame of a burst has no frame time yet: a typical one.
            float seconds = MathHelper.Clamp(frame > 0.25 ? 0.017f : (float)frame * ToolOverlap, ToolMinSeconds, ToolMaxSeconds);
            float level = ToolLevel(kind, contact, firm, spin, now);
            if (TwoHand.Holds(tool))
            {
                Pulse(TwoHand.OffHand, level * FrontShare, seconds);
                if (!Kickback.Rumbling)
                    Pulse(ToolHand, level * RearShare, seconds);
            }
            else if (!Kickback.Rumbling)
            {
                Pulse(ToolHand, level, seconds);
            }
        }

        /// <summary>How hard the drill's target is: the voxel's material, a grid, anything else.</summary>
        private static float DrillFirmness(MyDrillBase drill)
        {
            MyEntity target = drill?.DrilledEntity;
            if (target is MyVoxelBase voxel)
            {
                double now = Seconds();
                if (!ReferenceEquals(voxel, materialVoxel) || now - materialAt > MaterialEvery)
                {
                    materialVoxel = voxel;
                    materialAt = now;
                    Vector3D at = drill.DrilledEntityPoint;
                    VRage.Game.MyVoxelMaterialDefinition material = voxel.GetMaterialAt(ref at);
                    materialHardness = material == null ? VoxelFirmness(null, null) : VoxelFirmness(material.MaterialTypeName, material.MinedOre);
                }
                return materialHardness;
            }
            return target is MyCubeGrid ? GridFirmness : OtherFirmness;
        }

        // ---- The game's tool state. Each hook answers for itself, like the others. ----

        private static void PatchTools(Harmony harmony)
        {
            // The welder and the grinder: the base class decides every frame whether the tool has a target (EffectAction).
            Hook(harmony, "welder and grinder", () => AccessTools.Method(typeof(MyEngineerToolBase), nameof(MyEngineerToolBase.UpdateAfterSimulation)),
                nameof(AfterToolUpdate));

            // The hand drill: its spin (the drill base's animation speed) and what it drills.
            Hook(harmony, "hand drill", () => AccessTools.Method(typeof(MyHandDrill), nameof(MyHandDrill.UpdateAfterSimulation)),
                nameof(AfterDrillUpdate));
        }

        private static readonly FeatureGuard toolGuard = new FeatureGuard(nameof(Haptics) + " welder and grinder");
        private static readonly FeatureGuard drillGuard = new FeatureGuard(nameof(Haptics) + " hand drill");

        private static void AfterToolUpdate(MyEngineerToolBase __instance, MyCharacter ___Owner, MyShootActionEnum? ___EffectAction)
        {
            if (!toolGuard.On)
                return;
            try
            {
                if (!__instance.IsShooting || __instance.IsHeatingUp || !IsLocal(___Owner))
                    return;
                ToolKind kind = __instance is MyAngleGrinder ? ToolKind.Grinder : ToolKind.Welder;
                bool contact = ___EffectAction == MyShootActionEnum.PrimaryAction;
                // Armor has no functional block of its own.
                float firm = contact && __instance.GetTargetBlock() is MySlimBlock block && block.FatBlock == null ? 1f : 0.35f;
                ToolFrame(__instance, kind, contact, firm, 1f);
            }
            catch (Exception e)
            {
                toolGuard.Failed(e, "Haptics (welder and grinder) failed");
            }
        }

        private static void AfterDrillUpdate(MyHandDrill __instance, bool ___m_objectInDrillingRange, MyDrillBase ___m_drillBase)
        {
            if (!drillGuard.On)
                return;
            try
            {
                if (!IsLocal(__instance.Owner))
                    return;
                // The spin runs down after the trigger is let go, and the hand feels it.
                float spin = ___m_drillBase.AnimationMaxSpeedRatio;
                if (!__instance.IsShooting && spin <= 0.02f)
                    return;
                bool contact = __instance.IsShooting && ___m_objectInDrillingRange && __instance.IsDrillingAnObject;
                ToolFrame(__instance, ToolKind.Drill, contact, contact ? DrillFirmness(___m_drillBase) : 0f, spin);
            }
            catch (Exception e)
            {
                drillGuard.Failed(e, "Haptics (hand drill) failed");
            }
        }
    }
}
