using System;
using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    // Build mode's triggers and haptics.
    internal static partial class BuildMode
    {
        // ---- Triggers and haptics ----

        /// <summary>From VRControls with this frame's triggers as the game is given them (a trigger kept by a closing radial menu is not a pull).</summary>
        internal static void Triggers(bool place, bool remove, double seconds)
        {
            // While a symmetry plane is being set the triggers set and clear it (the game then leaves the setting): no block is expected.
            string mode = null;
            bool setting = ((place && !placeWas) || (remove && !removeWas)) && SettingPlane(MyCubeBuilder.Static, out mode);
            if (setting)
                Log.Info($"Build: {(place && !placeWas ? "place" : "remove")} pull while setting the symmetry plane ({mode})");
            else
            {
                if (place && !placeWas)
                {
                    placeAt = seconds;
                    placedAtPull = placed;
                }
                if (remove && !removeWas)
                {
                    removeAt = seconds;
                    removedAtPull = removed;
                }
            }
            placeWas = place;
            removeWas = remove;
        }

        private static void CheckRefused(MyCubeBuilder builder, double seconds)
        {
            Pending(builder, seconds, ref placeAt, placed, placedAtPull, placeWas, Hand.Right, "place");
            Pending(builder, seconds, ref removeAt, removed, removedAtPull, removeWas, Hand.Left, "remove");
        }

        private static void Pending(MyCubeBuilder builder, double seconds, ref double at, int count, int countAtPull, bool stillHeld, Hand hand, string what)
        {
            if (at < 0d)
                return;
            if (count != countAtPull)
            {
                at = -1d;
                return;
            }
            if (seconds - at < RefuseWindow)
                return;
            MyCubeBuilderGizmo.MyGizmoSpaceProperties space = Gizmo(builder).SpaceDefault;
            // A line or plane being dragged out (Ctrl in the game) places on the release: wait for it.
            if (stillHeld && (space.m_startBuild.HasValue || space.m_startRemove.HasValue))
                return;
            at = -1d;
            Refuse(hand, $"{what} refused: nothing {(what == "place" ? "placed" : "removed")} ({Why(builder, space, what)})");
        }

        private static string Why(MyCubeBuilder builder, MyCubeBuilderGizmo.MyGizmoSpaceProperties space, string what)
        {
            bool creative = MySession.Static != null && (MySession.Static.CreativeMode || MySession.Static.CreativeToolsEnabled(Sandbox.Game.Multiplayer.Sync.MyId));
            if (what == "remove")
                return !creative ? "survival: the game removes blocks with the grinder" : space.m_removeBlock == null ? "no block under the ray" : "the game kept it";
            return $"{(creative ? "creative" : "survival")}, placement {(space.m_buildAllowed ? "allowed" : "blocked or out of reach")}, {(CurrentGrid(builder) != null ? "on a grid" : "no grid")}";
        }

        private static void Refuse(Hand hand, string why)
        {
            Haptics.Pulse(hand, RefuseAmplitude, RefuseSeconds);
            secondBuzzAt = frameSeconds + RefuseGap;
            secondBuzzHand = hand;
            Log.Info("Build: " + why);
        }

        /// <summary>From <see cref="Haptics"/>: the game placed a block (any way: a pull, symmetry, a line).</summary>
        internal static void NotePlaced()
        {
            placed++;
            if (Active)
                Log.Info($"Build: placed {MyCubeBuilder.Static?.CurrentBlockDefinition?.Id.SubtypeName}");
        }

        private static void BeforeRemove(List<Vector3I> ___m_tmpBlockPositionList)
        {
            try
            {
                if (___m_tmpBlockPositionList == null || ___m_tmpBlockPositionList.Count == 0)
                    return;
                removed++;
                Haptics.Pulse(Hand.Left, RemoveAmplitude, RemoveSeconds);
                Log.Info($"Build: removed {___m_tmpBlockPositionList.Count} block{(___m_tmpBlockPositionList.Count == 1 ? string.Empty : "s")} at {___m_tmpBlockPositionList[0]}");
            }
            catch (Exception e)
            {
                if (errors++ < 3)
                    Log.Error(e, "Build mode: removal pulse failed");
            }
        }
    }
}
