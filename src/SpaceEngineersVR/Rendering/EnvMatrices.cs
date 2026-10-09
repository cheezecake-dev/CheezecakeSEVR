using System;
using HarmonyLib;
using VRageMath;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// Typed access to the renderer's internal <c>VRageRender.MyEnvironmentMatrices</c>, which holds one camera's
    /// view/projection state. MyRender11.Environment.Matrices is the camera every pass draws with.
    /// </summary>
    internal static class EnvMatrices
    {
        public static readonly Type Type = typeof(MyDX11Render).Assembly.GetType("VRageRender.MyEnvironmentMatrices", throwOnError: true);

        public static readonly AccessTools.FieldRef<object, Vector3D> CameraPosition = Field<Vector3D>("CameraPosition");
        public static readonly AccessTools.FieldRef<object, Matrix> ViewAt0 = Field<Matrix>("ViewAt0");
        public static readonly AccessTools.FieldRef<object, Matrix> InvViewAt0 = Field<Matrix>("InvViewAt0");
        public static readonly AccessTools.FieldRef<object, Matrix> ViewProjectionAt0 = Field<Matrix>("ViewProjectionAt0");
        public static readonly AccessTools.FieldRef<object, Matrix> InvViewProjectionAt0 = Field<Matrix>("InvViewProjectionAt0");
        public static readonly AccessTools.FieldRef<object, Matrix> Projection = Field<Matrix>("Projection");
        public static readonly AccessTools.FieldRef<object, Matrix> ProjectionForSkybox = Field<Matrix>("ProjectionForSkybox");
        public static readonly AccessTools.FieldRef<object, Matrix> InvProjection = Field<Matrix>("InvProjection");
        public static readonly AccessTools.FieldRef<object, MatrixD> ViewD = Field<MatrixD>("ViewD");
        public static readonly AccessTools.FieldRef<object, MatrixD> InvViewD = Field<MatrixD>("InvViewD");
        public static readonly AccessTools.FieldRef<object, MatrixD> ViewProjectionD = Field<MatrixD>("ViewProjectionD");
        public static readonly AccessTools.FieldRef<object, MatrixD> InvViewProjectionD = Field<MatrixD>("InvViewProjectionD");
        public static readonly AccessTools.FieldRef<object, Matrix> OriginalProjection = Field<Matrix>("OriginalProjection");
        public static readonly AccessTools.FieldRef<object, Matrix> OriginalProjectionFar = Field<Matrix>("OriginalProjectionFar");
        public static readonly AccessTools.FieldRef<object, BoundingFrustumD> ViewFrustumClippedD = Field<BoundingFrustumD>("ViewFrustumClippedD");
        public static readonly AccessTools.FieldRef<object, BoundingFrustumD> ViewFrustumClippedFarD = Field<BoundingFrustumD>("ViewFrustumClippedFarD");

        private static AccessTools.FieldRef<object, T> Field<T>(string name) => AccessTools.FieldRefAccess<T>(Type, name);

        /// <summary>The fields <see cref="MakeEye"/> changes, so the head camera can be put back after drawing an eye.</summary>
        public struct Snapshot
        {
            public Matrix ViewAt0, InvViewAt0, ViewProjectionAt0, InvViewProjectionAt0, Projection, ProjectionForSkybox, InvProjection;
            public MatrixD ViewD, InvViewD, ViewProjectionD, InvViewProjectionD;
            public MatrixD Frustum, FrustumFar;
        }

        public static Snapshot Save(object env) => new Snapshot
        {
            ViewAt0 = ViewAt0(env),
            InvViewAt0 = InvViewAt0(env),
            ViewProjectionAt0 = ViewProjectionAt0(env),
            InvViewProjectionAt0 = InvViewProjectionAt0(env),
            Projection = Projection(env),
            ProjectionForSkybox = ProjectionForSkybox(env),
            InvProjection = InvProjection(env),
            ViewD = ViewD(env),
            InvViewD = InvViewD(env),
            ViewProjectionD = ViewProjectionD(env),
            InvViewProjectionD = InvViewProjectionD(env),
            Frustum = ViewFrustumClippedD(env)?.Matrix ?? MatrixD.Identity,
            FrustumFar = ViewFrustumClippedFarD(env)?.Matrix ?? MatrixD.Identity,
        };

        public static void Restore(object env, in Snapshot s)
        {
            ViewAt0(env) = s.ViewAt0;
            InvViewAt0(env) = s.InvViewAt0;
            ViewProjectionAt0(env) = s.ViewProjectionAt0;
            InvViewProjectionAt0(env) = s.InvViewProjectionAt0;
            Projection(env) = s.Projection;
            ProjectionForSkybox(env) = s.ProjectionForSkybox;
            InvProjection(env) = s.InvProjection;
            ViewD(env) = s.ViewD;
            InvViewD(env) = s.InvViewD;
            ViewProjectionD(env) = s.ViewProjectionD;
            InvViewProjectionD(env) = s.InvViewProjectionD;
            if (ViewFrustumClippedD(env) != null)
                ViewFrustumClippedD(env).Matrix = s.Frustum;
            if (ViewFrustumClippedFarD(env) != null)
                ViewFrustumClippedFarD(env).Matrix = s.FrustumFar;
        }

        /// <summary>An eye's field of view as the tangents of its four half-angles (left and down negative), as OpenXR gives it.</summary>
        public struct FovTangents
        {
            public float Left, Right, Up, Down;
        }

        /// <summary>
        /// The same projection (near/far and depth mapping kept) narrowed or widened to an asymmetric field of view.
        /// Every perspective matrix the renderer uses (reverse-Z infinite, and the game's ordinary one for culling)
        /// keeps the field of view in M11/M22 and the off-centre shift in M31/M32, where Keen also puts ProjectionOffsetX/Y.
        /// </summary>
        public static Matrix WithFov(Matrix projection, in FovTangents fov)
        {
            projection.M11 = 2f / (fov.Right - fov.Left);
            projection.M22 = 2f / (fov.Up - fov.Down);
            projection.M31 = (fov.Right + fov.Left) / (fov.Right - fov.Left);
            projection.M32 = (fov.Up + fov.Down) / (fov.Up - fov.Down);
            return projection;
        }

        /// <summary>A projection's field of view as tangents: the inverse of <see cref="WithFov"/>.</summary>
        public static FovTangents Tangents(in Matrix projection) => new FovTangents
        {
            Left = (projection.M31 - 1f) / projection.M11,
            Right = (projection.M31 + 1f) / projection.M11,
            Up = (projection.M32 + 1f) / projection.M22,
            Down = (projection.M32 - 1f) / projection.M22,
        };

        /// <summary>
        /// Turns a head-centred camera into one eye's camera.
        /// </summary>
        /// <param name="env">Matrices the renderer just computed for this eye from the head-centred view.</param>
        /// <param name="eyeFromHead">The eye's pose relative to the head, in view space (x right, y up, -z forward).</param>
        /// <param name="fov">The eye's field of view, or null to keep the game camera's.</param>
        /// <remarks>
        /// The eye view is head view * inverse(eye pose). Its translation stays in ViewAt0 and CameraPosition stays
        /// at the head: the renderer derives eye_offset_in_world from that translation, and the shaders rebuild
        /// positions as depth * viewDirection - eye_offset_in_world (Frame.hlsli).
        /// Culling happens inside each eye's DrawGameScene from ViewFrustumClippedD, so the frustum is the eye's too:
        /// a head turned past the game camera's view would otherwise see things culled away.
        /// </remarks>
        public static void MakeEye(object env, Matrix eyeFromHead, FovTangents? fov)
        {
            Matrix headFromEye = Matrix.Invert(eyeFromHead);

            ref Matrix viewAt0 = ref ViewAt0(env);
            viewAt0 = viewAt0 * headFromEye;
            InvViewAt0(env) = Matrix.Invert(viewAt0);

            if (fov.HasValue)
            {
                Projection(env) = WithFov(Projection(env), fov.Value);
                ProjectionForSkybox(env) = WithFov(ProjectionForSkybox(env), fov.Value);
                InvProjection(env) = Matrix.Invert(Projection(env));
            }

            Matrix proj = Projection(env);
            ViewProjectionAt0(env) = viewAt0 * proj;
            InvViewProjectionAt0(env) = Matrix.Invert(ViewProjectionAt0(env));

            ref MatrixD viewD = ref ViewD(env);
            viewD = viewD * (MatrixD)headFromEye;
            InvViewD(env) = MatrixD.Invert(viewD);
            ViewProjectionD(env) = viewD * (MatrixD)proj;
            InvViewProjectionD(env) = MatrixD.Invert(ViewProjectionD(env));

            Matrix cull = OriginalProjection(env), cullFar = OriginalProjectionFar(env);
            if (fov.HasValue)
            {
                cull = WithFov(cull, fov.Value);
                cullFar = WithFov(cullFar, fov.Value);
            }
            if (ViewFrustumClippedD(env) != null)
                ViewFrustumClippedD(env).Matrix = viewD * (MatrixD)cull;
            if (ViewFrustumClippedFarD(env) != null)
                ViewFrustumClippedFarD(env).Matrix = viewD * (MatrixD)cullFar;
        }
    }
}
