using System;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using VRageMath;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// Tells the shaders where the eye is. Positions rebuilt from depth (sun shadows, lights, decals) come out relative
    /// to the eye, and the frame constant eye_offset_in_world moves them to the camera position the rest of the frame
    /// is relative to. The renderer fills it only for its own split-screen stereo regions and leaves it zero for a
    /// full-screen frame; each eye here is a full-screen frame drawn from the eye, so without it every rebuilt position
    /// is off by the eye's offset from the head, and the sun shadows differ between the eyes.
    /// </summary>
    internal static class EyeOffset
    {
        private static Action<Vector3> set;

        /// <summary>Debug: leave the offset as the renderer sets it (<see cref="RenderDebug"/> EyeOffset=0).</summary>
        public static bool Off { get; set; }

        public static void Patch(Harmony harmony)
        {
            Type common = typeof(MyDX11Render).Assembly.GetType("VRageRender.MyCommon", throwOnError: true);
            FieldInfo constants = AccessTools.Field(common, "FrameConstantsData");
            FieldInfo environment = AccessTools.Field(constants.FieldType, "Environment");
            FieldInfo eyeOffset = AccessTools.Field(environment.FieldType, "EyeOffsetInWorld");

            // MyCommon.FrameConstantsData.Environment.EyeOffsetInWorld = value, in place (both are structs). Every
            // UpdateFrameConstantsInternal call is given that field by ref.
            var method = new DynamicMethod("SetEyeOffsetInWorld", null, new[] { typeof(Vector3) }, common, skipVisibility: true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldsflda, constants);
            il.Emit(OpCodes.Ldflda, environment);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Stfld, eyeOffset);
            il.Emit(OpCodes.Ret);
            set = (Action<Vector3>)method.CreateDelegate(typeof(Action<Vector3>));

            harmony.Patch(AccessTools.Method(common, "UpdateFrameConstantsInternal"),
                postfix: new HarmonyMethod(typeof(EyeOffset), nameof(SetOffset)));
        }

        /// <summary>
        /// Minus the eye's position relative to the camera position, which is what the renderer's own formula gives for
        /// its stereo regions. For a mono frame the eye is at the camera position and it stays zero.
        /// </summary>
        private static void SetOffset(object envMatrices)
        {
            if (!Off)
                set(-EnvMatrices.InvViewAt0(envMatrices).Translation);
        }
    }
}
