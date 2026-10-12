using System;
using System.Reflection;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.SessionComponents;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    // Build mode's reach into the game's private builder, clipboard and voxel-hand state.
    internal static partial class BuildMode
    {
        // ---- The builder's private parts ----

        private static MyCubeBuilderGizmo Gizmo(MyCubeBuilder builder)
        {
            if (gizmoRef == null)
                gizmoRef = AccessTools.FieldRefAccess<MyCubeBuilder, MyCubeBuilderGizmo>("m_gizmo");
            if (animationLockRef == null)
                animationLockRef = AccessTools.FieldRefAccess<MyCubeBuilder, bool>("m_animationLock");
            return gizmoRef(builder);
        }

        private static AccessTools.FieldRef<MyCubeBuilder, bool> customRotationRef;

        /// <summary>
        /// As the game's rotate keys do with the default mount point on (MyCubeBuilder.HandleRotationInput): the block has been
        /// turned by hand, so the builder stops turning it back to sit on its default mount point (CheckGizmoRotation, which
        /// RotateAxis runs) until the ray moves to another grid.
        /// </summary>
        private static void CustomRotation(MyCubeBuilder builder)
        {
            if (!builder.AlignToDefault)
                return;
            if (customRotationRef == null)
                customRotationRef = AccessTools.FieldRefAccess<MyCubeBuilder, bool>("m_customRotation");
            customRotationRef(builder) = true;
        }

        private static MyCubeGrid CurrentGrid(MyCubeBuilder builder)
        {
            if (currentGridGetter == null)
                currentGridGetter = AccessTools.PropertyGetter(typeof(MyCubeBuilder), "CurrentGrid");
            return currentGridGetter?.Invoke(builder, null) as MyCubeGrid;
        }

        private static bool SettingPlane(MyCubeBuilder builder, out string mode)
        {
            mode = SymmetryMode(builder);
            return mode != "NoPlane" && mode != "Disabled" && mode != "?";
        }

        private static string SymmetryMode(MyCubeBuilder builder)
        {
            if (symmetryModeGetter == null)
                symmetryModeGetter = AccessTools.PropertyGetter(typeof(MyCubeBuilder), "SymmetrySettingMode");
            return symmetryModeGetter?.Invoke(builder, null)?.ToString() ?? "?";
        }

        // ---- The clipboard's and the voxel hand's ----

        private static MethodInfo applyAngle;

        private static void PasteRefs()
        {
            if (pasteForwardRef != null)
                return;
            pasteForwardRef = AccessTools.FieldRefAccess<MyGridClipboard, Vector3>("m_pasteDirForward");
            pasteUpRef = AccessTools.FieldRefAccess<MyGridClipboard, Vector3>("m_pasteDirUp");
            pasteAngleRef = AccessTools.FieldRefAccess<MyGridClipboard, float>("m_pasteOrientationAngle");
            applyAngle = AccessTools.Method(typeof(MyGridClipboard), "ApplyOrientationAngle")
                         ?? throw new MissingMethodException(nameof(MyGridClipboard), "ApplyOrientationAngle");
        }

        /// <summary>How far out the paste is held, metres (the clipboard's drag distance, which MOVE_CLOSER and MOVE_FURTHER change by a tenth).</summary>
        private static float PasteDistance(MyGridClipboard grids)
        {
            if (dragDistanceRef == null)
                dragDistanceRef = AccessTools.FieldRefAccess<MyGridClipboard, float>("m_dragDistance");
            return dragDistanceRef(grids);
        }

        private static AccessTools.FieldRef<MySessionComponentVoxelHand, byte> voxelMaterialRef;

        /// <summary>The voxel hand's material, by name ("?" when it cannot be read).</summary>
        internal static string VoxelMaterialName()
        {
            try
            {
                MySessionComponentVoxelHand voxel = MySessionComponentVoxelHand.Static;
                if (voxel == null)
                    return "?";
                if (voxelMaterialRef == null)
                    voxelMaterialRef = AccessTools.FieldRefAccess<MySessionComponentVoxelHand, byte>("m_selectedMaterial");
                return MyDefinitionManager.Static.GetVoxelMaterialDefinition(voxelMaterialRef(voxel))?.Id.SubtypeName ?? "?";
            }
            catch (Exception)
            {
                return "?";
            }
        }
    }
}
