// ShipModel, on the datapad: the hologram standing on the pad's screen, going where the pad goes.
using SpaceEngineersVR.Input;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    internal static partial class ShipModel
    {
        // ---- On the datapad ----
        // Raised from the datapad's Ship page (Gui.DatapadShipPage): the model stands on the pad's screen, its keel toward the
        // screen and its bow toward the top of the pad, and goes where the pad goes. A grip on it turns it there (both hands,
        // with the pad let go, also scale it), the page's buttons make it larger and smaller, and it goes when the pad does.

        /// <summary>The gap between the pad's screen and the model's underside, metres.</summary>
        private const float PadGap = 0.015f;

        /// <summary>The model's longest side as it is raised on the pad, as a share of the pad's width.</summary>
        private const float PadFill = 0.8f;

        /// <summary>The model on the datapad (else it rides the off hand, or stands where it was let go).</summary>
        private static bool onPad;

        /// <summary>The model's turn on the pad, in the pad's own frame (x right, y up, +Z out of the screen).</summary>
        private static Matrix padTurn;

        /// <summary>Keel on the screen, bow toward the top of the pad: the model's up out of the screen, its forward up the pad.</summary>
        private static readonly Matrix PadUpright = Matrix.CreateWorld(Vector3.Zero, Vector3.Up, Vector3.Backward);

        /// <summary>The model stands on the datapad.</summary>
        public static bool OnPad => Shown && !hiding && onPad;

        /// <summary>
        /// The datapad's Ship page: raises the model on the pad (showing the ship first, as the actions wheel would, when none
        /// is out), or puts it away when it is on the pad.
        /// </summary>
        public static void TogglePad()
        {
            if (!Enabled)
                return;
            if (OnPad)
            {
                Hide("put away from the datapad");
                return;
            }
            if (!(Shown && !hiding) && !Show())
                return;
            LetGoAll();
            following = false;
            onPad = true;
            padTurn = PadUpright;
            if (Datapad.TryGetPadNow(out _, out float width))
                scale = MathHelper.Clamp(PadFill * width / longest, SmallestSize / longest, LargestSize / longest);
            RidePad();
            Log.Info($"Ship model: '{shownName}' raised on the datapad, {scale * longest * 100:0} cm long; a grip on it turns it there");
        }

        /// <summary>The page's Larger and Smaller: the model on the pad scaled by <paramref name="factor"/>, within its smallest and largest.</summary>
        public static void ScaleOnPad(float factor)
        {
            if (!OnPad)
                return;
            scale = MathHelper.Clamp(scale * factor, SmallestSize / longest, LargestSize / longest);
            RidePad();
            Log.Info($"Ship model: {scale * longest * 100:0} cm long on the datapad");
        }

        /// <summary>The page's Upright: the model on the pad turned back to stand keel down, bow up the pad.</summary>
        public static void UprightOnPad()
        {
            if (!OnPad)
                return;
            padTurn = PadUpright;
            RidePad();
            Log.Info("Ship model: turned upright on the datapad again");
        }

        /// <summary>
        /// A grip of <paramref name="hand"/> is on the model on the pad: the pad leaves that grip to the model
        /// (<see cref="Datapad"/> takes grips before it does).
        /// </summary>
        internal static bool OnPadAt(Hand hand) => OnPad && Claim(hand);

        /// <summary>Game thread, a frame while on the pad: a hand's turn on it kept as its turn on the pad, and the model put there.</summary>
        private static void KeepOnPad()
        {
            if (!Datapad.TryGetPadNow(out Matrix pad, out _))
                return;
            if (held[0] || held[1])
                padTurn = Turn(pose) * Matrix.Transpose(Turn(pad));
            RidePad(pad);
        }

        /// <summary>The model on the pad as the pad is this frame (<see cref="Datapad.TryGetPadNow"/>); where it was when the pad cannot be had.</summary>
        private static void RidePad()
        {
            if (Datapad.TryGetPadNow(out Matrix pad, out _))
                RidePad(pad);
        }

        /// <summary>Turned as it is on the pad, its underside <see cref="PadGap"/> above the middle of the pad's screen.</summary>
        private static void RidePad(in Matrix pad)
        {
            Matrix turn = padTurn * Turn(pad);
            Vector3 normal = pad.Backward;
            pose = turn;
            pose.Translation = pad.Translation + normal * (PadGap + HalfAlong(turn, normal));
        }

        /// <summary>A pose's turn alone.</summary>
        private static Matrix Turn(in Matrix of)
        {
            Matrix turn = of;
            turn.Translation = Vector3.Zero;
            return turn;
        }
    }
}
