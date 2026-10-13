// HelmetHead: the helmet on the eye in first person, turned with the head and carried by the neck and the body.
using System;
using HarmonyLib;
using Sandbox;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SpaceEngineersVR.Common;
using VRageMath;
using VRageRender.Animations;

namespace SpaceEngineersVR.Tracking
{
    /// <summary>
    /// With the helmet on the eyes (<see cref="Rendering.HelmetView"/>), the local character's head is posed on the
    /// eye every simulation frame: the helmet is where the camera is, so the camera is always inside it, and it turns with
    /// the head up to the neck's limits (<see cref="GameHead.LimitedLook"/>: 70 deg each way, 89 deg up and down), past
    /// which the eyes turn on inside it. The neck and then the spine lean toward it and the body moves under what is left,
    /// so the helmet stays on the neck. The view itself is not touched: HeadDummy, which the camera, the aim and the hands
    /// are built from, is put back exactly where the game and the plugin left it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Where the eye sits in the helmet: between the character's eyes (SE_RigL_Eye, SE_RigR_Eye) and at the height of the
    /// game's own first-person camera (HeadDummy), from the skeleton's bind pose: 7.5 cm ahead of the head bone and 2.7 cm below
    /// it (the female's eyes 0.3 cm further forward), 1.7 cm under the eyes' own height, so a player standing level, whose
    /// eye the plugin puts at HeadDummy's height, has the body where it is drawn and not sunk by that. Measured from the
    /// model (SE_astronaut_LOD0.mwm), from there the helmet's visor is 7 cm ahead, the shell 10 cm to each side and below and
    /// the hood 10 cm above; the camera's near plane is 5 cm. The game's own camera is on HeadDummy, 12.5 cm further forward,
    /// in the visor: that is why the game hides the head in first person. RenderDebug HelmetEyeMm=n puts the eye n mm further
    /// forward in the helmet (negative: back).
    /// </para>
    /// <para>
    /// Each frame, after the game's animation and its first-person placement of the bones (UpdateHeadAndWeapon, and
    /// <see cref="BodyShift"/> after it) and before the arms are solved (<see cref="HandIK"/>, which this runs ahead of):
    /// </para>
    /// <list type="number">
    /// <item>The eye is the head matrix the camera is built from this frame (MyCharacter.GetHeadMatrix, as
    /// <see cref="HandWorld"/> takes it for the hands), in the body's space: the tracked head with everything the plugin does to
    /// it (the head lock, the push out of the suit, the crouch, the body drawn ahead, the boots' smoothing).</item>
    /// <item>The helmet is turned by the head's look from the body, limited as a neck is, from the head bone's bind pose
    /// (upright, facing the body's way), and placed so its eye point is on the eye.</item>
    /// <item>The spine (SE_RigSpine2-4, each a share of it) bends toward it for all but the last 6 cm
    /// (<see cref="HelmetPose.NeckShare"/>) of the way, at most 12 deg a bone; then the neck points at it, at most 45 deg, and less
    /// as the helmet is behind or below the neck (so a duck does not swing the head forward). Leaning in, the shoulders and
    /// the arms come with the head as a person's do.</item>
    /// <item>What is left between the neck and the helmet is taken by moving the whole body under it (as BodyShift and the
    /// game's own first person move every bone), up to 50 cm (<see cref="HelmetPose.FollowMax"/>): a duck short of the crouch (30 cm) lowers
    /// the body, feet and all, by what the neck and spine cannot, and a character crouched lower than the player's own head
    /// is lifted by the difference. Past that the helmet stays on the eye and comes off the neck (logged): the head lock
    /// (Head reach) keeps the eye far nearer than that.</item>
    /// <item>The head bone is set on the eye, turned; HeadDummy is put back bit for bit (BodyShift recognises its move by
    /// HeadDummy's exact position).</item>
    /// </list>
    /// <para>
    /// The bones are the local character's, as drawn for the local player only: other players see the game's own animation,
    /// which bends the neck and the spine by the synced head pitch (HeadLocalXAngle, set by <see cref="CharacterHead"/>) and
    /// has no head yaw. The bones are posed once a simulation frame from the head as sampled for it; the renderer's late head
    /// correction (<see cref="CameraHeadLink"/>) moves the camera after that and not the helmet, so in a fast turn the helmet's
    /// rim lags the view by that correction, as the hands do. Not in a seat (the seat's head is its own, and the helmet is not
    /// turned there). RenderDebug HelmetHeadTurn=0 leaves the head where the game's animation has it (it does not turn with
    /// the head, and leaning or looking down takes the eye out of it); HelmetBodyFollow=0 leaves the spine and the body where they are
    /// (only the neck points at the helmet), =2 moves the body only across, never up or down (what is left up or down is the
    /// helmet's, off the neck); HelmetTrace=1 logs the pose once a second.
    /// </para>
    /// </remarks>
    internal static class HelmetHead
    {
        /// <summary>Debug: the head left as the game's animation poses it (<see cref="Rendering.RenderDebug"/> HelmetHeadTurn=0).</summary>
        public static bool TurnOff { get; set; }

        /// <summary>
        /// Debug (<see cref="Rendering.RenderDebug"/> HelmetBodyFollow=n): 1 the spine and the body carry the helmet; 0 they stay
        /// where they are, only the neck pointed at it; 2 the body moves only across, not up or down.
        /// </summary>
        public static int FollowMode { get; set; } = 1;

        /// <summary>Debug: the eye this many millimetres further forward in the helmet (<see cref="Rendering.RenderDebug"/> HelmetEyeMm=n).</summary>
        public static int EyeMm { get; set; }

        /// <summary>Debug: the pose logged once a second (<see cref="Rendering.RenderDebug"/> HelmetTrace=1).</summary>
        public static bool TraceOn { get; set; }

        /// <summary>The astronaut's eye point from the head bone, the head upright, until the rig is built: 7.5 cm ahead and 2.7 cm below (the remarks).</summary>
        private static readonly Vector3 BindEyeFromHead = new Vector3(0f, -0.027f, -0.075f);

        /// <summary>The helmet off the neck by more than this is logged (at most every <see cref="OffLogFrames"/>), metres.</summary>
        private const float OffLogged = 0.02f;

        private const ulong OffLogFrames = 300, TraceFrames = 60;

        private static readonly FeatureGuard guard = new FeatureGuard(nameof(HelmetHead));

        // The rig for one character and model (null with rigFor set: not the astronaut's skeleton).
        private static MyCharacter rigFor;
        private static object rigModel;
        private static HelmetPose.Rig rig;
        private static bool missingLogged, appliedLogged;
        private static ulong offLogFrame = ulong.MaxValue, traceFrame = ulong.MaxValue;

        public static void Patch(Harmony harmony)
        {
            // Ahead of the arms (HandIK, default priority): the spine and the body are where the shoulders come from.
            harmony.Patch(AccessTools.Method(typeof(MyCharacter), nameof(MyCharacter.UpdateAfterSimulation)),
                postfix: new HarmonyMethod(typeof(HelmetHead), nameof(AfterSimulation)) { priority = Priority.High });
        }

        /// <summary>Game thread, each simulation frame, the bones final for the frame but the arms.</summary>
        private static void AfterSimulation(MyCharacter __instance)
        {
            if (__instance != MySession.Static?.LocalCharacter || !guard.On || TurnOff)
                return;
            try
            {
                if (!Rendering.HelmetView.Worn(__instance))
                    return;
                MyCharacterBone[] bones = __instance.AnimationController?.CharacterBones;
                Matrix[] absolute = __instance.BoneAbsoluteTransforms;
                HelmetPose.Rig found = RigOf(__instance, bones);
                if (found == null || absolute == null || absolute.Length != bones.Length)
                    return;

                // The eye: the head matrix this frame's camera is built from, in the body's space; the look, limited as a neck is.
                MatrixD headWorld = __instance.GetHeadMatrix(true, true, false, __instance.ForceFirstPersonCamera, true);
                Vector3 eye = (Vector3)Vector3D.Transform(headWorld.Translation, __instance.PositionComp.WorldMatrixNormalizedInv);
                GameHead.LimitedLook(out float yaw, out float pitch);

                HelmetPose.Result result = HelmetPose.Pose(bones, absolute, found, eye, yaw, pitch, EyeMm / 1000f, FollowMode);
                Report(found, yaw, pitch, result);
            }
            catch (Exception e)
            {
                guard.Failed(e, "Helmet head failed");
            }
        }

        /// <summary>
        /// The middle of the helmet from the eye, in the body's space (x right, y up, z back), metres: the head bone, with the helmet
        /// placed on the eye and turned as <see cref="HelmetPose.Pose"/> places it (the look limited as a neck is, <see cref="EyeMm"/>
        /// included). From the rig built for <paramref name="character"/>, or the astronaut's bind pose before one is. Game thread,
        /// for <see cref="HelmetWalls"/>.
        /// </summary>
        internal static Vector3 CentreFromEye(MyCharacter character)
        {
            Vector3 eye = rigFor == character && rig != null ? rig.EyeFromHead : BindEyeFromHead;
            GameHead.LimitedLook(out float yaw, out float pitch);
            return -Vector3.TransformNormal(eye + Vector3.Forward * (EyeMm / 1000f), Matrix.CreateRotationX(pitch) * Matrix.CreateRotationY(yaw));
        }

        /// <summary>
        /// Game thread: where the helmet is, for the hands to stop on (<see cref="Hands.HelmetStop"/>): the head bone's bind pose in
        /// the body's space (what the shell is measured from, <paramref name="bind"/>) and the head bone as it stands now, after
        /// the pose above, in the same space (<paramref name="posed"/>). A point measured on the model at rest follows the helmet
        /// by <c>point * Invert(bind) * posed</c>. False where the helmet is not worn or the skeleton is not the astronaut's.
        /// </summary>
        internal static bool TryHeadFrame(MyCharacter character, out Matrix bind, out Matrix posed)
        {
            bind = posed = Matrix.Identity;
            MyCharacterBone[] bones = character?.AnimationController?.CharacterBones;
            if (bones == null || !Rendering.HelmetView.Worn(character))
                return false;
            HelmetPose.Rig found = RigOf(character, bones);
            if (found == null || found.Head >= bones.Length)
                return false;
            bind = found.Bind;
            posed = bones[found.Head].AbsoluteTransform;
            return true;
        }

        /// <summary>The rig of this character's model, built once per character and model; null (logged once) where the skeleton is not the astronaut's.</summary>
        private static HelmetPose.Rig RigOf(MyCharacter character, MyCharacterBone[] bones)
        {
            if (bones == null)
                return null;
            object model = character.Model;
            if (rigFor == character && ReferenceEquals(rigModel, model))
                return rig;
            rigFor = character;
            rigModel = model;
            int head = Find(character, "SE_RigHead"), neck = Find(character, "SE_RigNeck"), dummy = CharacterFields.HeadBoneIndex(character);
            int left = Find(character, "SE_RigL_Eye"), right = Find(character, "SE_RigR_Eye");
            int[] spine = new int[HelmetPose.SpineNames.Length];
            for (int i = 0; i < spine.Length; i++)
                spine[i] = Find(character, HelmetPose.SpineNames[i]);
            rig = HelmetPose.Build(bones, head, neck, dummy, left, right, spine);
            if (rig == null && !missingLogged)
            {
                missingLogged = true;
                Log.Info($"Helmet view: this character's skeleton is not the astronaut's (head {head}, neck {neck}, HeadDummy {dummy}, eyes {left}/{right}); the head is left as the game poses it");
            }
            else if (rig != null && rig.Spine.Length == 0)
            {
                Log.Info($"Helmet view: this skeleton has no {string.Join(", ", HelmetPose.SpineNames)}; only the neck and the body carry the helmet");
            }
            return rig;
        }

        /// <summary>The once-a-run line, the helmet off the neck (at most every 5 s), and the trace (RenderDebug HelmetTrace=1).</summary>
        private static void Report(HelmetPose.Rig found, float yaw, float pitch, in HelmetPose.Result result)
        {
            if (!appliedLogged)
            {
                appliedLogged = true;
                Vector3 eye = found.EyeFromHead;
                Log.Info($"Helmet view: the helmet follows the eye in first person, turned with the head up to {MathHelper.ToDegrees(Input.VRControls.MaxLookYaw):F0} deg each way and " +
                         $"{MathHelper.ToDegrees(Input.VRControls.MaxLookPitch):F0} deg up and down (the eyes turn on inside it past that); eye point {-eye.Z * 100f:F1} cm ahead of the head bone and " +
                         $"{-eye.Y * 100f:F1} cm below it{(EyeMm != 0 ? $", moved {EyeMm} mm forward" : "")}; the spine ({found.Spine.Length} bones), the neck and then the body carry it. " +
                         "RenderDebug HelmetHeadTurn=0 leaves the head to the game, HelmetBodyFollow=0 keeps the spine and the body still, HelmetTrace=1 logs the pose");
            }
            ulong frame = MySandboxGame.Static.SimulationFrameCounter;
            if (result.Off > OffLogged && (offLogFrame == ulong.MaxValue || frame - offLogFrame >= OffLogFrames))
            {
                offLogFrame = frame;
                string why = FollowMode == 0 ? "the body follow is off" : FollowMode == 2 ? "the body follows only across" : $"{HelmetPose.FollowMax * 100f:F0} cm at most";
                Log.Info($"Helmet view: the helmet is {result.Off * 100f:F0} cm off the neck: the eye is further from the body than it follows ({why}); the helmet stays on the eye");
            }
            if (TraceOn && (traceFrame == ulong.MaxValue || frame - traceFrame >= TraceFrames))
            {
                traceFrame = frame;
                Vector3 follow = result.Follow;
                Log.Info($"Helmet trace: look yaw {MathHelper.ToDegrees(yaw):F0} pitch {MathHelper.ToDegrees(pitch):F0} deg (head {MathHelper.ToDegrees(GameHead.Yaw):F0}, {MathHelper.ToDegrees(GameHead.Pitch):F0}), " +
                         $"spine {MathHelper.ToDegrees(result.Spine):F0} deg, neck {MathHelper.ToDegrees(result.Neck):F0} deg, body moved {follow.X * 100f:F1} right {follow.Y * 100f:F1} up {follow.Z * 100f:F1} back cm, " +
                         $"off the neck {result.Off * 100f:F1} cm, body drawn ahead {BodyShift.Applied * 100f:F1} cm");
            }
        }

        private static int Find(MyCharacter character, string name)
        {
            character.AnimationController.FindBone(name, out int index);
            return index;
        }
    }
}
