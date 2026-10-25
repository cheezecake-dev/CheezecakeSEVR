using System;

namespace SpaceEngineersVR.Input.Bindings
{
    /// <summary>
    /// Waits for the player to press the control they want for a binding, from the controls as the controllers report them
    /// (<see cref="HandState.ControlsDown"/> and <see cref="HandState.ControlsTouched"/>), once a frame (<see cref="Update"/>).
    /// <list type="bullet">
    /// <item>It listens only once every control has been let go, so the press that started it (the laser's trigger on a
    /// button) is never taken.</item>
    /// <item>The first control to go down is taken. With a modifier, the first is the modifier, then the next control to go
    /// down while it is held; letting the modifier go first starts that over.</item>
    /// <item>A control that can only be touched (the thumbrest) is taken once a thumb has rested on it for
    /// <see cref="ThumbrestSeconds"/> with nothing pressed, so a thumb settling there on its way is not taken. The stick is
    /// never taken by touch: resting a thumb on it is how the stick is used, and its click stands for it.</item>
    /// <item>A trackpad pressed in one of its parts is that part, not the whole trackpad.</item>
    /// <item>Nothing pressed for <see cref="TimeoutSeconds"/>, or a control not let go for that long, gives up.</item>
    /// </list>
    /// </summary>
    internal sealed class BindingCapture
    {
        public const double TimeoutSeconds = 10d;

        /// <summary>A thumb rested on a thumbrest this long, with nothing pressed, is the thumbrest chosen.</summary>
        public const double ThumbrestSeconds = 0.5d;

        public enum Step
        {
            /// <summary>Not capturing.</summary>
            Idle,
            /// <summary>Waiting for every control to be let go.</summary>
            WaitRelease,
            /// <summary>Waiting for the modifier to go down.</summary>
            Modifier,
            /// <summary>Waiting for the control (with the modifier held, if there is one).</summary>
            Control,
            /// <summary>The control has been taken: <see cref="Control"/> and <see cref="Modifier"/> say what it is.</summary>
            Done,
            /// <summary>Nothing was pressed in time.</summary>
            TimedOut,
        }

        private readonly uint[] lastDown = new uint[2], lastTouched = new uint[2], down = new uint[2], touched = new uint[2];

        // Per hand: when a thumb came to rest on the thumbrest while listening for the control, or -1.
        private readonly double[] restingSince = { -1d, -1d };
        private bool withModifier;
        private double listeningSince;

        public Step Now { get; private set; } = Step.Idle;

        public ControlRef? Modifier { get; private set; }

        public ControlRef? Control { get; private set; }

        /// <summary>A control down now (the newest taken first: the control, then the modifier), for showing what is pressed.</summary>
        public ControlRef? Pressed { get; private set; }

        /// <summary>Capturing: waiting for the controls to be let go, or for a press.</summary>
        public bool Listening => Now == Step.WaitRelease || Now == Step.Modifier || Now == Step.Control;

        /// <summary>Begins: first everything let go, then the modifier if <paramref name="modifier"/>, then the control.</summary>
        public void Start(bool modifier, double now)
        {
            withModifier = modifier;
            Modifier = null;
            Control = null;
            Pressed = null;
            Now = Step.WaitRelease;
            listeningSince = now;
            restingSince[0] = restingSince[1] = -1d;
        }

        public void Cancel()
        {
            Now = Step.Idle;
            Modifier = null;
            Control = null;
            Pressed = null;
        }

        /// <summary>Seconds left before it gives up, while listening for a press.</summary>
        public double SecondsLeft(double now) => Math.Max(0d, TimeoutSeconds - (now - listeningSince));

        /// <summary>The controls this frame, each hand's bits <c>1 &lt;&lt; (int)ControllerControl</c>, at <paramref name="now"/> seconds.</summary>
        public void Update(uint leftDown, uint rightDown, uint leftTouched, uint rightTouched, double now)
        {
            down[0] = leftDown;
            down[1] = rightDown;
            touched[0] = leftTouched;
            touched[1] = rightTouched;
            Pressed = FirstDown(down);
            try
            {
                switch (Now)
                {
                    case Step.WaitRelease:
                        if (down[0] == 0u && down[1] == 0u)
                        {
                            Now = withModifier ? Step.Modifier : Step.Control;
                            listeningSince = now;
                        }
                        else if (now - listeningSince > TimeoutSeconds)
                            Now = Step.TimedOut;
                        break;

                    case Step.Modifier:
                    {
                        ControlRef? got = NewDown(down, touched, now, touchOnly: false);
                        if (got.HasValue)
                        {
                            Modifier = got;
                            Now = Step.Control;
                            listeningSince = now;
                        }
                        else if (now - listeningSince > TimeoutSeconds)
                            Now = Step.TimedOut;
                        break;
                    }

                    case Step.Control:
                    {
                        if (Modifier.HasValue && !IsDown(down, Modifier.Value))
                        {
                            // Let go before the second control: the modifier is chosen again.
                            Modifier = null;
                            Now = Step.Modifier;
                            listeningSince = now;
                            break;
                        }
                        ControlRef? got = NewDown(down, touched, now, touchOnly: true);
                        if (got.HasValue && !(Modifier.HasValue && got.Value.Equals(Modifier.Value)))
                        {
                            Control = got;
                            Now = Step.Done;
                        }
                        else if (now - listeningSince > TimeoutSeconds)
                            Now = Step.TimedOut;
                        break;
                    }
                }
            }
            finally
            {
                Array.Copy(down, lastDown, 2);
                Array.Copy(touched, lastTouched, 2);
            }
        }

        private static bool IsDown(uint[] down, ControlRef control) => (down[(int)control.Hand] & (1u << (int)control.Control)) != 0u;

        /// <summary>
        /// A control that went down this frame (a trackpad's part before the whole trackpad), or with <paramref name="touchOnly"/>
        /// a thumbrest rested on for <see cref="ThumbrestSeconds"/>.
        /// </summary>
        private ControlRef? NewDown(uint[] down, uint[] touched, double now, bool touchOnly)
        {
            uint thumbrest = 1u << (int)ControllerControl.Thumbrest;
            for (int h = 0; h < 2; h++)
            {
                if (!touchOnly || (touched[h] & thumbrest) == 0u || down[h] != 0u)
                    restingSince[h] = -1d;
                else if ((lastTouched[h] & thumbrest) == 0u)
                    restingSince[h] = now;
            }
            for (int h = 0; h < 2; h++)
            {
                uint fresh = down[h] & ~lastDown[h];
                if (fresh == 0u)
                    continue;
                ControlRef? whole = null;
                for (int c = 0; c < ControlRef.ControlCount; c++)
                {
                    if ((fresh & (1u << c)) == 0u)
                        continue;
                    var control = new ControlRef((Hand)h, (ControllerControl)c);
                    if (control.Control == ControllerControl.Trackpad)
                    {
                        whole = control;
                        continue;
                    }
                    return control;
                }
                if (whole.HasValue)
                    return whole;
            }
            for (int h = 0; h < 2; h++)
            {
                if (restingSince[h] >= 0d && now - restingSince[h] >= ThumbrestSeconds)
                    return new ControlRef((Hand)h, ControllerControl.Thumbrest);
            }
            return null;
        }

        private ControlRef? FirstDown(uint[] down)
        {
            if (Control.HasValue && IsDown(down, Control.Value))
                return Control;
            for (int h = 0; h < 2; h++)
            {
                for (int c = 0; c < ControlRef.ControlCount; c++)
                {
                    var control = new ControlRef((Hand)h, (ControllerControl)c);
                    if ((down[h] & (1u << c)) != 0u && !(Modifier.HasValue && control.Equals(Modifier.Value)))
                        return control;
                }
            }
            return Modifier.HasValue && IsDown(down, Modifier.Value) ? Modifier : null;
        }
    }
}
