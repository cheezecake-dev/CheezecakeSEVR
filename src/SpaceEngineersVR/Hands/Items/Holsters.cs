using System;
using System.Diagnostics;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Gui;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;
using VRage.Game;
using VRage.Game.Entity;
using VRageMath;
using VRageRender.Animations;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// Tool holsters (<see cref="VRSettings.ToolHolsters"/>): a place on the astronaut's body for each of the welder, the grinder
    /// and the hand drill (<see cref="VRSettings.WelderHolster"/>, <see cref="VRSettings.GrinderHolster"/>,
    /// <see cref="VRSettings.DrillHolster"/>). A grip of the tool hand that starts at a holster takes the best tool of that kind
    /// in the inventory into the hand; with that kind already in the hand, it puts it away.
    /// </summary>
    /// <remarks>
    /// The tool comes out through the game's own switch to one inventory item (MyCharacter.SwitchToWeapon(itemId)), as the
    /// toolbar's, so it is synced and the game's rules apply; putting it away is SwitchToWeapon(null), the toolbar's unselect.
    /// The best of a kind is the highest SpeedMultiplier of its hand item (MyEngineerToolBaseDefinition), then
    /// DistanceMultiplier, so modded tiers rank too. The places are the skeleton's, in its animated pose: the thigh joints for
    /// the hips, the upper spine (SE_RigSpine4) for the chest and the shoulders, out to the side; without the bones, heights
    /// measured from the feet. The grip is claimed through <see cref="GripClaim"/> at priority 7, before the helmet's, so a
    /// holster behind the shoulder is never the helmet's grip; the grip's meaning in empty air (Use) is skipped for that press.
    /// Only the tool hand reaches a holster, as only it holds tools.
    /// </remarks>
    internal static class Holsters
    {
        /// <summary>
        /// The holsters are shelved until they are reworked (they did not work properly in the headset): nothing of them is
        /// patched, the grip is never claimed at the body, and their settings are not in VR options. Tools and guns come from the
        /// toolbar, the tools wheel, the G menu and the inventory screen (<see cref="Gui.InventoryToolbar"/>,
        /// <see cref="Input.InventoryPull"/>). False turns it all back on.
        /// </summary>
        internal static readonly bool Shelved = true;

        /// <summary>Debug: once a second, where each holster is in the STAGE space (<see cref="Rendering.RenderDebug"/> HolsterTrace=1).</summary>
        public static bool Trace { get; set; }

        /// <summary>The name the grip is claimed under (<see cref="GripClaim"/>).</summary>
        public const string ClaimName = "Holster";

        /// <summary>The places a holster can be, in the order of the settings' choices; 0 is none.</summary>
        public static readonly string[] PlaceNames =
        {
            "None", "Right hip", "Left hip", "Right chest", "Left chest", "Behind the right shoulder", "Behind the left shoulder",
        };

        /// <summary>A grip within this of a holster's middle takes it, metres.</summary>
        internal const double Reach = 0.15;

        /// <summary>From the thigh joint: out to the side, up and forward (metres): on the outside of the hip, a little below the joint.</summary>
        private static readonly Vector3 HipOffset = new Vector3(0.10f, -0.08f, 0.0f);

        /// <summary>From the upper spine: out, up and forward: on the front of the chest.</summary>
        private static readonly Vector3 ChestOffset = new Vector3(0.10f, 0.0f, 0.14f);

        /// <summary>From the upper spine: out, up and back: behind the shoulder, reached over it.</summary>
        private static readonly Vector3 ShoulderOffset = new Vector3(0.14f, 0.08f, -0.16f);

        /// <summary>Without the bones: the hip joint and the upper spine this high above the feet, the hip this far out (metres).</summary>
        private const float HipHeightFallback = 0.95f, HipSideFallback = 0.10f, SpineHeightFallback = 1.35f;

        /// <summary>A tool taken: a firm tick. Put away: a lighter one. None of that kind: two light ticks, this far apart.</summary>
        public const float TakeAmplitude = 0.5f, TakeSeconds = 0.03f;
        public const float StowAmplitude = 0.3f, StowSeconds = 0.02f;
        public const float NoneAmplitude = 0.25f, NoneSeconds = 0.02f;
        private const double NoneGap = 0.09;

        private sealed class Kind
        {
            public readonly string Name, HandItemType;
            public readonly Func<int> Place;

            public Kind(string name, string handItemType, Func<int> place)
            {
                Name = name;
                HandItemType = handItemType;
                Place = place;
            }
        }

        private static readonly Kind[] kinds =
        {
            new Kind("welder", "MyObjectBuilder_Welder", () => VRSettings.WelderHolster),
            new Kind("grinder", "MyObjectBuilder_AngleGrinder", () => VRSettings.GrinderHolster),
            new Kind("hand drill", "MyObjectBuilder_HandDrill", () => VRSettings.DrillHolster),
        };

        private static readonly FeatureGuard guard = new FeatureGuard(nameof(Holsters));
        private static bool hooked;
        private static Kind pending;
        private static long noneTickAt, tracedAt;

        public static void Patch(Harmony harmony)
        {
            try
            {
                GripClaim.Register(ClaimName, GripPriority.Holsters, Take);
                harmony.Patch(AccessTools.Method(typeof(MyCharacter), nameof(MyCharacter.UpdateAfterSimulation)),
                    postfix: new HarmonyMethod(typeof(Holsters), nameof(AfterSimulation)));
                hooked = true;
                Log.Info($"Holsters: hooked (grip claim at priority {(int)GripPriority.Holsters}: the tool hand's grip within {Reach * 100:0} cm of a holster)");
            }
            catch (Exception e)
            {
                hooked = false;
                PatchRegistry.Lost(e, "The tool holsters could not be hooked; tools come from the toolbar and the tools wheel");
                throw;
            }
        }

        // ---- The grip claim ----

        /// <summary>A grip has just started on <paramref name="hand"/>: it is a holster's when the tool hand is at one right now.</summary>
        private static bool Take(Hand hand)
        {
            if (Shelved || !hooked || !guard.On || hand != VRSettings.DominantHand)
                return false;
            MyCharacter character = MySession.Static?.LocalCharacter;
            if (!Live(character) || ToolbarWheel.Owns || !HandWorld.TryGetNow(character, hand, out MatrixD grip, out _, out _))
                return false;
            Kind nearest = null;
            double best = Reach;
            foreach (Kind kind in kinds)
            {
                if (!TrySpot(character, kind.Place(), out Vector3D spot))
                    continue;
                double d = Vector3D.Distance(grip.Translation, spot);
                if (d <= best)
                {
                    best = d;
                    nearest = kind;
                }
            }
            if (nearest == null)
                return false;
            pending = nearest;
            return true;
        }

        private static bool Live(MyCharacter character) =>
            VRSettings.ToolHolsters && VRInput.Active && character != null && !character.Closed && !character.IsDead && !character.IsOnLadder
            && MySession.Static.ControlledEntity == character && MyScreenManager.GetScreenWithFocus() is MyGuiScreenGamePlay;

        // ---- Each frame ----

        /// <summary>Game thread, each simulation frame, after the character's own update.</summary>
        private static void AfterSimulation(MyCharacter __instance)
        {
            if (__instance != MySession.Static?.LocalCharacter || !guard.On)
                return;
            try
            {
                // Asking every frame keeps the grip claim current: it forgets a grip only when asked once it is let go.
                GripClaim.Holds(VRSettings.DominantHand, ClaimName);
                long now = Stopwatch.GetTimestamp();
                if (noneTickAt != 0 && now >= noneTickAt)
                {
                    noneTickAt = 0;
                    Haptics.Pulse(VRSettings.DominantHand, NoneAmplitude, NoneSeconds);
                }
                if (Trace && VRSettings.ToolHolsters && now - tracedAt >= Stopwatch.Frequency)
                {
                    tracedAt = now;
                    TraceSpots(__instance);
                }
                Kind kind = pending;
                pending = null;
                if (kind != null && Live(__instance))
                    Use(__instance, kind, now);
            }
            catch (Exception e)
            {
                pending = null;
                guard.Failed(e, "Holsters: the holster failed");
            }
        }

        /// <summary>The tool hand gripped at <paramref name="kind"/>'s holster: that kind in the hand goes away, else the best of it comes out.</summary>
        private static void Use(MyCharacter character, Kind kind, long now)
        {
            Hand hand = VRSettings.DominantHand;
            string where = HolsterName(kind.Place());
            MyPhysicalItemDefinition held = character.CurrentWeapon?.PhysicalItemDefinition;
            if (held != null && IsKind(held.Id, kind, out _))
            {
                character.SwitchToWeapon((MyToolbarItemWeapon)null);
                Haptics.Pulse(hand, StowAmplitude, StowSeconds);
                Log.Info($"Holsters: {hand} hand put the {held.DisplayNameText} away in the {where}");
                return;
            }
            if (!TryBest(character, kind, out uint item, out MyPhysicalItemDefinition definition, out int count))
            {
                Haptics.Pulse(hand, NoneAmplitude, NoneSeconds);
                noneTickAt = now + (long)(NoneGap * Stopwatch.Frequency);
                Log.Info($"Holsters: {hand} hand at the {where}: no {kind.Name} in the inventory");
                return;
            }
            if (!character.CanSwitchToWeapon(definition.Id))
            {
                Log.Info($"Holsters: {hand} hand at the {where}: the game does not switch to the {definition.DisplayNameText} now");
                return;
            }
            character.SwitchToWeapon(item);
            Haptics.Pulse(hand, TakeAmplitude, TakeSeconds);
            Log.Info($"Holsters: {hand} hand took the {definition.DisplayNameText} ({definition.Id.SubtypeName}) from the {where}, the best {kind.Name} of {count} in the inventory");
        }

        // ---- The tools ----

        /// <summary>The best tool of <paramref name="kind"/> in the inventory: its item, its definition, and how many of that kind there are.</summary>
        private static bool TryBest(MyCharacter character, Kind kind, out uint item, out MyPhysicalItemDefinition definition, out int count)
        {
            item = 0;
            definition = null;
            count = 0;
            MyInventory inventory = character.GetInventory();
            if (inventory == null)
                return false;
            float bestSpeed = float.MinValue, bestDistance = float.MinValue;
            foreach (MyPhysicalInventoryItem candidate in inventory.GetItems())
            {
                if (candidate.Content == null)
                    continue;
                MyDefinitionId id = candidate.Content.GetId();
                if (!IsKind(id, kind, out MyEngineerToolBaseDefinition tool))
                    continue;
                count++;
                float speed = tool?.SpeedMultiplier ?? 0f, distance = tool?.DistanceMultiplier ?? 0f;
                if (speed < bestSpeed || (speed == bestSpeed && distance <= bestDistance))
                    continue;
                bestSpeed = speed;
                bestDistance = distance;
                item = candidate.ItemId;
                definition = MyDefinitionManager.Static.GetPhysicalItemDefinition(id);
            }
            return definition != null;
        }

        /// <summary>A hand tool item of <paramref name="kind"/>, with its hand item's tool definition (null for one that has none).</summary>
        private static bool IsKind(MyDefinitionId id, Kind kind, out MyEngineerToolBaseDefinition tool)
        {
            tool = null;
            if (id.TypeId.ToString() != "MyObjectBuilder_PhysicalGunObject")
                return false;
            MyHandItemDefinition handItem = MyDefinitionManager.Static.TryGetHandItemForPhysicalItem(id);
            if (handItem == null || handItem.Id.TypeId.ToString() != kind.HandItemType)
                return false;
            tool = handItem as MyEngineerToolBaseDefinition;
            return true;
        }

        // ---- The places ----

        /// <summary>"right hip holster", "holster behind the right shoulder".</summary>
        private static string HolsterName(int place)
        {
            string name = PlaceNames[place].ToLowerInvariant();
            return name.StartsWith("behind", StringComparison.Ordinal) ? "holster " + name : name + " holster";
        }

        /// <summary>The middle of the holster at <paramref name="place"/> (one of <see cref="PlaceNames"/>) in the world; false for none.</summary>
        private static bool TrySpot(MyCharacter character, int place, out Vector3D spot)
        {
            spot = Vector3D.Zero;
            if (place <= 0 || place >= PlaceNames.Length)
                return false;
            MatrixD body = character.WorldMatrix;
            float side = place % 2 == 1 ? 1f : -1f;
            bool hip = place <= 2, chest = place == 3 || place == 4;
            MyCharacterBone bone = character.AnimationController?.FindBone(hip ? (side > 0 ? "SE_RigRThigh" : "SE_RigLThigh") : "SE_RigSpine4", out _);
            Vector3D from = bone != null
                ? Vector3D.Transform((Vector3D)bone.AbsoluteTransform.Translation, body)
                : hip
                    ? body.Translation + body.Up * HipHeightFallback + body.Right * side * HipSideFallback
                    : body.Translation + body.Up * SpineHeightFallback;
            Vector3 offset = hip ? HipOffset : chest ? ChestOffset : ShoulderOffset;
            spot = from + body.Right * side * offset.X + body.Up * offset.Y + body.Forward * offset.Z;
            return true;
        }

        /// <summary>Each holster's middle in the STAGE space (the floor's), for placing a hand on it from outside.</summary>
        private static void TraceSpots(MyCharacter character)
        {
            if (!(OpenXR.XrSession.LocalInStage is Matrix stage) || !HandWorld.TryGetTrackingToBody(character, out MatrixD trackingToModel, out _))
                return;
            MatrixD worldToStage = MatrixD.Invert(character.WorldMatrix) * MatrixD.Invert(trackingToModel) * (MatrixD)stage;
            foreach (Kind kind in kinds)
            {
                int place = kind.Place();
                if (!TrySpot(character, place, out Vector3D spot))
                    continue;
                Vector3D at = Vector3D.Transform(spot, worldToStage);
                Log.Info($"Holster trace: the {kind.Name}'s holster ({PlaceNames[place].ToLowerInvariant()}) at stage ({at.X:F3}, {at.Y:F3}, {at.Z:F3}) m");
            }
        }
    }
}
