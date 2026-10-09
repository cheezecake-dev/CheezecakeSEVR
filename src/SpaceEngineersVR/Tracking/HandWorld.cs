using Sandbox.Engine.Utils;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using VRage.Game.ModAPI.Interfaces;
using VRage.Game.Utils;
using VRageMath;

namespace SpaceEngineersVR.Tracking
{
    /// <summary>
    /// Why a hand's pose is not handed out although the hand is tracked: the view it would be put in is not the one the
    /// local character's body has.
    /// </summary>
    internal enum ViewRefusal
    {
        /// <summary>Nothing in the way (or the hand is simply not tracked, which is not a view's fault).</summary>
        None,

        /// <summary>The player has no character in the world (between a death and the respawn, a spectator with none).</summary>
        NoBody,

        /// <summary>The character is dead.</summary>
        Dead,

        /// <summary>The camera belongs to something else: the free spectator camera, a camera block, a remote control, a turret.</summary>
        OtherCamera,

        /// <summary>The camera is nowhere near the character: a jump (teleport, respawn) that the camera has not followed yet.</summary>
        Far,
    }

    /// <summary>
    /// Everything <see cref="HandWorld"/> reads of the game for one hand pose, in one value: the camera, the tracked head
    /// and the local character's body. <see cref="HandWorld.ViewOverride"/> stands one in for the game's.
    /// </summary>
    internal struct WorldView
    {
        /// <summary>There is a main camera; <see cref="Camera"/> is its world matrix.</summary>
        public bool HasCamera;

        public MatrixD Camera;

        /// <summary>The head is tracked: <see cref="HeadTracking"/> is its pose in the tracking space, <see cref="HeadFromCamera"/> the camera-to-head link.</summary>
        public bool HeadTracked;

        public Matrix HeadTracking, HeadFromCamera;

        /// <summary>The player has a character; where it is, how fast its top parent (the ship it sits in) moves, whether it is dead.</summary>
        public bool HasBody, BodyDead;

        public Vector3D BodyAt;
        public float BodySpeed;

        /// <summary>Null when the camera is the character's own (its eyes, or the seat it sits in); else what it is the camera of.</summary>
        public string OtherCamera;
    }

    /// <summary>
    /// A hand's pose in the game world, for everything that puts a hand in the scene (the laser's line, and the
    /// hand work to come: models, tools, building, cockpit controls).
    /// </summary>
    /// <remarks>
    /// The hand is taken relative to the tracked head, and that is put in the world where the camera has the head.
    /// So the hand stands in the scene exactly where it stands to the eyes, whatever the game does to the view: the
    /// anchor (<see cref="GameHead"/> turns the body), the height correction, a seat that carries the player, a ship
    /// turning under them are all already in the camera. When the camera was not built from the head (third person:
    /// the renderer puts the eyes at the head's tracking pose around the camera instead) the head's own pose is
    /// the way to the camera, as <see cref="Rendering.HudCamera"/> has it for the HUD.
    /// Game thread. During the draw phase of a frame (after the camera was built) it is exact; earlier in the frame
    /// it uses the previous frame's camera, a frame behind the hands.
    /// <para>
    /// That is what a hand is relative to the view, which is all the laser needs (<see cref="TryGetTrackingToWorld"/>).
    /// What <see cref="TryGet"/> gives is the hand of the character's body, for the things that act as the character
    /// (the arms, a fingertip, a tool): only while the camera is the character's own, and near it. The camera is the
    /// game's, and it is not always the body's: the free spectator camera, a camera block, a remote control or a turret
    /// are other places, and after a teleport or a respawn the camera is still where the character was until the next
    /// draw. A hand placed by such a camera is tens of metres from its own shoulder, so it is not given out.
    /// </para>
    /// </remarks>
    internal static class HandWorld
    {
        /// <summary>How far the camera may be from the character's position (its origin, at the feet), metres, however still it stands. An eye is 2 m up at most; a seat's camera a little more.</summary>
        internal const double CameraFromBodyMax = 4.0;

        /// <summary>
        /// And how far more it may be per metre a second the body moves: the camera is the previous draw's, the body is
        /// where the simulation put it this frame, so a ship at 100 m/s has them 1.7 m apart for a frame, and a slow
        /// frame is several. Seconds of the body's motion that are allowed for.
        /// </summary>
        internal const double LagSeconds = 0.1;

        /// <summary>
        /// RenderDebug HandIKFrame=0: the arms take the hands from the previous draw's camera, as they did before
        /// (<see cref="TryGetTrackingToBody"/>), for comparison.
        /// </summary>
        internal static bool PreviousCamera { get; set; }

        /// <summary>How the last <see cref="TryGetTrackingToBody"/> placed the tracking space, for the log.</summary>
        internal enum BodySource
        {
            /// <summary>From the head matrix the camera is built from this frame (first person, on foot or seated).</summary>
            Head,

            /// <summary>From the last camera sent, carried along with the body since (third person, an untracked head).</summary>
            CameraInBody,

            /// <summary>From the last camera sent, where it was in the world (RenderDebug HandIKFrame=0, or no camera sent yet).</summary>
            Camera,
        }

        internal static BodySource LastBodySource { get; private set; }

        /// <summary>The last camera sent, relative to the local character's body when it was sent (model space).</summary>
        private static MatrixD cameraInBody;

        private static MyCharacter cameraInBodyOf;

                /// <summary>Test seam: stands in for what the game says (the camera, the head, the character), so the placement can be driven without a game.</summary>
        internal static WorldView? ViewOverride { get; set; }

        /// <summary>For the log, from the last refusal: what the other camera is, and how far the camera was from the character (metres).</summary>
        internal static string LastOtherCamera { get; private set; }

        internal static double LastCameraFromBody { get; private set; }

        /// <summary>
        /// A hand's grip and aim poses in the world, as the character's body has them (double precision: the world is
        /// far from the origin). False when the hand is not tracked, there is no camera, or the camera is not the
        /// character's own and close to it (<paramref name="why"/> says which).
        /// </summary>
        public static bool TryGet(Hand hand, out MatrixD grip, out MatrixD aim, out ViewRefusal why) =>
            TryGet(VRInput.Get(hand), VRInput.Active, ReadView(), out grip, out aim, out why);

        public static bool TryGet(Hand hand, out MatrixD grip, out MatrixD aim) => TryGet(hand, out grip, out aim, out _);

        internal static bool TryGet(in HandState state, bool active, in WorldView view, out MatrixD grip, out MatrixD aim, out ViewRefusal why)
        {
            grip = aim = MatrixD.Identity;
            why = ViewRefusal.None;
            if (!active || !state.Tracked || !view.HasCamera)
                return false;
            why = Refusal(view);
            if (why != ViewRefusal.None)
                return false;
            MatrixD toWorld = ToWorld(view);
            grip = (MatrixD)state.Grip * toWorld;
            aim = (MatrixD)state.Aim * toWorld;
            return true;
        }

        /// <summary>
        /// A hand's grip and aim poses in the world as the coming draw will show them, for what acts in the simulation
        /// (a tool in the hand, a fingertip, the use and builder rays): <see cref="TryGet"/> but placed by this frame's
        /// head (<see cref="TryGetTrackingToBody"/>) rather than the previous draw's camera, so a body in motion does not
        /// leave the hand a frame behind (7 m/s sprinting is 10 cm). Game thread, after the simulation moved the body.
        /// Falls back to the previous camera's placement where there is no head to build from (what
        /// <see cref="TryGetTrackingToBody"/> does), and RenderDebug HandIKFrame=0 asks for it everywhere.
        /// </summary>
        internal static bool TryGetNow(MyCharacter body, Hand hand, out MatrixD grip, out MatrixD aim, out ViewRefusal why)
        {
            grip = aim = MatrixD.Identity;
            why = ViewRefusal.None;
            HandState state = VRInput.Get(hand);
            if (!VRInput.Active || !state.Tracked)
                return false;
            if (body?.PositionComp == null)
            {
                why = ViewRefusal.NoBody;
                return false;
            }
            if (!TryGetTrackingToBody(body, out MatrixD trackingToModel, out why))
                return false;
            MatrixD toWorld = trackingToModel * MatrixD.Invert(body.PositionComp.WorldMatrixNormalizedInv);
            grip = (MatrixD)state.Grip * toWorld;
            aim = (MatrixD)state.Aim * toWorld;
            return true;
        }

        /// <summary>
        /// The move from the tracking space to <paramref name="body"/>'s model space this frame, as the camera the coming
        /// draw builds will have it: what the arms are solved against (<see cref="HandIK"/>). Game thread, after the
        /// simulation has moved the body. False, with <paramref name="why"/>, where <see cref="TryGet"/> refuses.
        /// </summary>
        /// <remarks>
        /// The camera is built in the draw, after the simulation, from the body as it is then; the bones go to the
        /// renderer in the same draw. So the hands are placed by the head matrix that camera will be built from
        /// (MyCharacter.GetViewMatrix inverts GetHeadMatrix; a seat's MyCockpit.GetViewMatrix its own), taken now, in the
        /// body's own space: tracking space -> head (inverse of the tracked head, which that camera is built from) -> head
        /// in the world -> model space. The previous draw's camera, which <see cref="TryGet"/> uses, is where the body was a
        /// frame ago: at 100 m/s that is 1.7 m behind. Where the camera is not built from the head (third person, the head
        /// not tracked) the last camera sent is taken where it was relative to the body, which moves it with the body.
        /// </remarks>
        internal static bool TryGetTrackingToBody(MyCharacter body, out MatrixD trackingToModel, out ViewRefusal why)
        {
            trackingToModel = MatrixD.Identity;
            why = ViewRefusal.None;
            if (!VRInput.Active || body?.PositionComp == null)
                return false;
            WorldView view = ReadView();
            if (!view.HasCamera)
                return false;
            why = Refusal(view);
            if (why != ViewRefusal.None)
                return false;

            MatrixD modelFromWorld = body.PositionComp.WorldMatrixNormalizedInv;
            if (!PreviousCamera && view.HeadTracked && TryGetHeadCamera(body, out MatrixD head))
            {
                LastBodySource = BodySource.Head;
                trackingToModel = (MatrixD)Matrix.Invert(view.HeadTracking) * head * modelFromWorld;
                return true;
            }
            if (!PreviousCamera && cameraInBodyOf == body)
            {
                LastBodySource = BodySource.CameraInBody;
                view.Camera = cameraInBody;
                trackingToModel = ToWorld(view);
                return true;
            }
            LastBodySource = BodySource.Camera;
            trackingToModel = ToWorld(view) * modelFromWorld;
            return true;
        }

        /// <summary>
        /// The head matrix the camera is built from this frame, when the camera is <paramref name="body"/>'s first-person
        /// head (on foot) or the first-person head of the seat it sits in: what GetViewMatrix inverts. Taking it runs the
        /// head patches (<see cref="CharacterHead"/>, <see cref="CockpitHead"/>) as the camera's own call will, on the
        /// same snapshot of the head (<see cref="GameHead.Take"/>), so the two agree.
        /// </summary>
        private static bool TryGetHeadCamera(MyCharacter body, out MatrixD head)
        {
            head = MatrixD.Identity;
            IMyCameraController controller = MySession.Static?.CameraController;
            if (ReferenceEquals(controller, body))
            {
                // MyCharacter.GetViewMatrix: the head matrix in first person (or when it is forced), else the third-person camera.
                if (body.IsSitting || body.IsDead || !(body.IsInFirstPersonView || body.ForceFirstPersonCamera))
                    return false;
                head = body.GetHeadMatrix(true, true, false, body.ForceFirstPersonCamera, true);
                return true;
            }
            if (controller is MyCockpit seat && seat.Pilot == body)
            {
                // MyCockpit.GetViewMatrix: GetHeadMatrix(first person, first person), else the third-person camera.
                if (!(seat.IsInFirstPersonView || seat.ForceFirstPersonCamera))
                    return false;
                head = seat.GetHeadMatrix(true, true);
                return true;
            }
            return false;
        }

        /// <summary>Game thread, as a camera goes to the renderer: where it is relative to the local character's body.</summary>
        internal static void CameraSent(in MatrixD viewMatrix)
        {
            MyCharacter body = MySession.Static?.LocalCharacter;
            if (body?.PositionComp == null)
            {
                cameraInBodyOf = null;
                return;
            }
            cameraInBody = MatrixD.Invert(viewMatrix) * body.PositionComp.WorldMatrixNormalizedInv;
            cameraInBodyOf = body;
        }

        /// <summary>
        /// The move that takes a point given in the tracking space (the hands', and the GUI panel's) to the world: what
        /// the player sees, whatever camera that is. With no tracked head the tracking space sits at the camera, as
        /// the eyes do then.
        /// </summary>
        public static bool TryGetTrackingToWorld(out MatrixD toWorld)
        {
            toWorld = MatrixD.Identity;
            WorldView view = ViewOverride ?? ReadCamera(MySector.MainCamera);
            if (!view.HasCamera)
                return false;
            toWorld = ToWorld(view);
            return true;
        }

        /// <summary>Whether the body has this view, and if not, why not (the cheap checks first).</summary>
        internal static ViewRefusal Refusal(in WorldView view)
        {
            if (!view.HasBody)
                return ViewRefusal.NoBody;
            if (view.BodyDead)
                return ViewRefusal.Dead;
            if (view.OtherCamera != null)
            {
                LastOtherCamera = view.OtherCamera;
                return ViewRefusal.OtherCamera;
            }
            double distance = (view.Camera.Translation - view.BodyAt).Length();
            if (distance > CameraFromBodyMax + LagSeconds * view.BodySpeed || double.IsNaN(distance))
            {
                LastCameraFromBody = distance;
                return ViewRefusal.Far;
            }
            return ViewRefusal.None;
        }

        /// <summary>A refusal in words, with the last figures, for the log.</summary>
        internal static string Explain(ViewRefusal why)
        {
            switch (why)
            {
                case ViewRefusal.NoBody: return "the player has no character";
                case ViewRefusal.Dead: return "the character is dead";
                case ViewRefusal.OtherCamera: return $"the view is {LastOtherCamera}, not the character's";
                case ViewRefusal.Far: return $"the camera is {LastCameraFromBody:F0} m from the character (a teleport or respawn it has not followed yet)";
                default: return "the view is the character's";
            }
        }

        /// <summary>The game's camera, head and character as one value (or the test seam's).</summary>
        private static WorldView ReadView()
        {
            if (ViewOverride.HasValue)
                return ViewOverride.Value;
            WorldView view = ReadCamera(MySector.MainCamera);
            if (view.HasCamera)
                ReadBody(ref view);
            return view;
        }

        private static WorldView ReadCamera(MyCamera camera)
        {
            var view = new WorldView();
            if (camera == null)
                return view;
            view.HasCamera = true;
            view.Camera = camera.WorldMatrix;
            if (GameHead.Take())
            {
                view.HeadTracked = true;
                view.HeadTracking = GameHead.TrackingPose;
                view.HeadFromCamera = CameraHeadLink.HeadFromSentCamera;
            }
            return view;
        }

        private static void ReadBody(ref WorldView view)
        {
            MySession session = MySession.Static;
            MyCharacter body = session?.LocalCharacter;
            if (body == null || body.PositionComp == null)
                return;
            view.HasBody = true;
            view.BodyDead = body.IsDead || body.Closed;
            view.BodyAt = body.PositionComp.GetPosition();
            view.BodySpeed = body.GetTopMostParent()?.Physics?.LinearVelocity.Length() ?? 0f;
            view.OtherCamera = OtherCameraName(session.CameraController, body);
        }

        /// <summary>
        /// Null when the camera controller is the character's own: the character itself (on foot, on a ladder, third
        /// person), or the seat it sits in. Else a name for what the camera belongs to.
        /// </summary>
        private static string OtherCameraName(IMyCameraController controller, MyCharacter body)
        {
            if (controller == null)
                return "no camera";
            if (ReferenceEquals(controller, body) || ReferenceEquals(controller, body.Parent))
                return null;
            if (controller is MyShipController seat && seat.Pilot == body)
                return null;
            if (controller is MySpectatorCameraController)
                return "the spectator camera";
            if (controller is MyCameraBlock)
                return "a camera block's";
            return controller.GetType().Name + "'s";
        }

        private static MatrixD ToWorld(in WorldView view) =>
            view.HeadTracked ? Compose(view.HeadTracking, view.HeadFromCamera, view.Camera) : view.Camera;

        /// <summary>
        /// Tracking space to world, as a row-vector product: into the head's own space (inverse of the head's tracking
        /// pose), then into the camera's frame - unchanged when the camera was built from the head, else by the head's
        /// tracking pose, which is how the renderer places the eyes around such a camera - then out through the
        /// camera's world matrix. <paramref name="headFromCamera"/> is <see cref="CameraHeadLink.HeadFromSentCamera"/>:
        /// the identity in the first case, the inverse of the head's tracking pose in the second.
        /// </summary>
        internal static MatrixD Compose(in Matrix headTracking, in Matrix headFromCamera, in MatrixD cameraWorld) =>
            (MatrixD)(Matrix.Invert(headTracking) * Matrix.Invert(headFromCamera)) * cameraWorld;
    }
}
