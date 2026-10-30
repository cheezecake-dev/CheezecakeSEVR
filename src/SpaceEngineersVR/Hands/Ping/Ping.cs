using System;
using HarmonyLib;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Input.Bindings;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// Point and ping: aim at something within the suit radio's range (ore the HUD marks, a block or grid, a player, a spot
    /// on an asteroid or a planet) and either ping it for the team or mark it as a GPS of your own. It tells no more than
    /// the game's own HUD does: a grid that is not the player's or their faction's is only "Large grid", a player outside
    /// the faction only "Player", with no friend or foe, and ore is named only where the HUD draws its marker. The target
    /// and what it is called come from the game's own data (Ping.Target.cs); the marker is the game's GPS, and the team
    /// hears of it through the game's chat with the GPS in the game's own text form, which every client turns into a GPS
    /// without the plugin (Ping.Send.cs). While aiming, a long laser runs from the hand to the target, and a ping flashes
    /// where it lands (Ping.Draw.cs).
    /// <list type="bullet">
    /// <item>Hold X (<see cref="HoldSeconds"/>) on foot, on the jetpack or in a ship's seat: a tick, and the laser shows what
    /// you point at, named on the HUD. Let go of X to ping it. Pull the pointing hand's trigger while X is held to mark it as
    /// your own GPS instead. A tap of X is still the dampeners, given when it is let go (the bindings' Dampeners).</item>
    /// <item>The actions wheel's Ping and Mark GPS entries (<see cref="Gui.PingEntries"/>) start the same aim with X up: the
    /// trigger pings or marks, B cancels, and it gives up after <see cref="WheelAimSeconds"/>.</item>
    /// </list>
    /// On foot the ray is the pointing hand's; in a seat the hands hold the controls, so it is where the head looks. A ray that
    /// meets nothing within <see cref="Range"/> makes no ping and no GPS. Off with the Ping setting's "Hold X" off (the wheel
    /// stays), or RenderDebug Ping=0.
    /// </summary>
    internal static partial class Ping
    {
        /// <summary>Debug: no ping at all, and X is the dampeners on its press again (<see cref="Rendering.RenderDebug"/> Ping=0).</summary>
        public static bool Off { get; set; }

        /// <summary>How long X is held before it aims; shorter and a quick tap for the dampeners would aim, longer and it feels slow.</summary>
        public const double HoldSeconds = 0.35;

        /// <summary>An aim started from the wheel ends by itself after this long, so a forgotten one does not keep the trigger.</summary>
        public const double WheelAimSeconds = 15.0;

        /// <summary>
        /// The longest ray, metres: the broadcast radius of the character's suit radio, the antenna a player on foot always has
        /// (200 m, set in MyCharacter.Init). Measured from the character, so a ping reaches no further than the suit's radio.
        /// Read from the character, so a different radius (a mod's) is followed.
        /// </summary>
        public static double Range
        {
            get
            {
                float radius = MySession.Static?.LocalCharacter?.RadioBroadcaster?.BroadcastRadius ?? 0f;
                return radius > 0f ? radius : SuitRadioRange;
            }
        }

        /// <summary>The suit radio's broadcast radius as MyCharacter.Init sets it, metres; used when there is no radio to read.</summary>
        private const double SuitRadioRange = 200.0;

        /// <summary>How long after the wheel starts an aim its fading screen may still hold the focus.</summary>
        private const double WheelFadeSeconds = 1.0;

        /// <summary>The tick as an aim starts and as a ping or mark is made.</summary>
        private const float TickAmplitude = 0.35f, TickSeconds = 0.03f;

        private const float CommitAmplitude = 0.6f, CommitSeconds = 0.05f;

        /// <summary>What a commit does.</summary>
        internal enum Kind
        {
            /// <summary>A temporary GPS of one's own plus a chat line with the GPS in it, for the team.</summary>
            Ping,

            /// <summary>A permanent GPS of one's own, told to nobody.</summary>
            Mark,
        }

        private enum Aim
        {
            None,

            /// <summary>X is held past <see cref="HoldSeconds"/>: its release pings, the trigger marks.</summary>
            HeldX,

            /// <summary>Started from the actions wheel: the trigger commits <see cref="wheelKind"/>, B cancels.</summary>
            Wheel,
        }

        private static Aim aim;
        private static Kind wheelKind;
        private static double aimStartedAt;
        private static bool xMarked, hooked;
        private static VRContext aimContext;
        private static readonly FeatureGuard guard = new FeatureGuard(nameof(Ping));

        /// <summary>The Ping binding (holding X, by default) aims: on while the setting is.</summary>
        private static bool HoldSetting => !Off && VRSettings.PingHoldX;

        /// <summary>Something is being aimed at: the laser and the label are up, and the pointing hand's trigger commits.</summary>
        public static bool Aiming => aim != Aim.None;

        /// <summary>An aim started from the actions wheel is up: the trigger commits it and B stops it (ControllerBindings' PingAim).</summary>
        public static bool AimingFromWheel => aim == Aim.Wheel;

        public static void Patch(Harmony harmony)
        {
            PatchRegistry.Apply("draw", () =>
            {
                harmony.Patch(AccessTools.Method(typeof(MySession), nameof(MySession.DrawSync)),
                    postfix: new HarmonyMethod(typeof(Ping), nameof(Draw)));
            }, "Ping: the laser and the flash cannot be drawn; pings and marks still work");
            hooked = true;
            Log.Info("Ping: hold X to ping, the trigger with it to mark a GPS; the actions wheel has both");
        }

        /// <summary>
        /// Game thread, once a frame from <see cref="VRControls"/>, before the buttons are turned into the game's controls.
        /// <paramref name="blocked"/>: a menu, the toolbar wheel or a camera block's view has the hands.
        /// </summary>
        internal static void Frame(VRContext context, bool blocked, double seconds)
        {
            if (!guard.On)
            {
                aim = Aim.None;
                return;
            }
            try
            {
                HoldFade();
                bool applies = hooked && !Off && !blocked && VRInput.Active && Applies(context) && MySession.Static?.LocalCharacter != null;

                // The wheel that started an aim keeps the focus for a moment while it fades out.
                if (!applies && aim == Aim.Wheel && seconds - aimStartedAt < WheelFadeSeconds)
                    return;
                if (!applies || (aim != Aim.None && context != aimContext))
                {
                    if (aim != Aim.None)
                        EndAim(applies ? "the seat changed" : "the hands are needed elsewhere");
                    return;
                }

                HoldX(context, seconds);
                if (aim == Aim.Wheel)
                    WheelAim(seconds);
                if (aim != Aim.None)
                    UpdateAim(context);
            }
            catch (Exception e)
            {
                guard.Failed(e, "Ping: the frame failed");
                aim = Aim.None;
            }
        }

        /// <summary>On foot, on the jetpack, and in a seat that flies (not a turret, whose view is its own).</summary>
        private static bool Applies(VRContext context) =>
            context == VRContext.OnFoot || context == VRContext.Jetpack || context == VRContext.Ship;

        /// <summary>
        /// Ping (X held past <see cref="HoldSeconds"/>, by default) aims, let go it pings; Mark a GPS (a trigger pulled with X
        /// held) marks instead. A tap of X is the dampeners (the bindings' Dampeners).
        /// </summary>
        private static void HoldX(VRContext context, double seconds)
        {
            if (!HoldSetting)
            {
                if (aim == Aim.HeldX)
                    EndAim("the setting went off");
                return;
            }
            if (aim == Aim.None && ControllerBindings.Started(VRAction.Ping))
            {
                xMarked = false;
                StartAim(Aim.HeldX, Kind.Ping, context, seconds);
                Haptics.Pulse(ControllerBindings.HandOf(VRAction.Ping), TickAmplitude, TickSeconds);
            }
            if (aim != Aim.HeldX)
                return;
            if (ControllerBindings.IsActive(VRAction.Ping))
            {
                if (!xMarked && ControllerBindings.Started(VRAction.MarkGps))
                {
                    xMarked = true;
                    Commit(Kind.Mark);
                }
                return;
            }
            // Let go.
            if (!xMarked)
                Commit(Kind.Ping);
            EndAim(null);
        }

        private static void WheelAim(double seconds)
        {
            if (ControllerBindings.Started(VRAction.PingCancel))
            {
                EndAim("cancelled");
                Notify("Ping cancelled", NotifyMilliseconds);
                return;
            }
            if (seconds - aimStartedAt > WheelAimSeconds)
            {
                EndAim("timed out");
                return;
            }
            if (CommitPressed())
            {
                Commit(wheelKind);
                EndAim(null);
            }
        }

        /// <summary>
        /// Ping or mark (the pointing hand's trigger, by default: mirrored left-handed) was pressed this frame; the press is the
        /// aim's, not a shot or a weld (ControllerBindings: it belongs to the aim).
        /// </summary>
        private static bool CommitPressed() => ControllerBindings.Started(VRAction.PingCommit, VRSettings.DominantHand == Hand.Left);

        /// <summary>The actions wheel's entries can start an aim: on foot, on the jetpack or in a pilot seat.</summary>
        internal static bool CanStart() => !Off && hooked && Applies(VRControls.Context);

        /// <summary>The actions wheel's Ping or Mark GPS: aims with the pointing hand (or the head in a seat) until the trigger or B.</summary>
        internal static void StartFromWheel(Kind kind)
        {
            VRContext context = VRControls.Context;
            if (!CanStart())
            {
                Notify("Ping: on foot, on the jetpack or in a pilot seat", NotifyMilliseconds);
                return;
            }
            wheelKind = kind;
            StartAim(Aim.Wheel, kind, context, System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);
            Haptics.Pulse(VRSettings.DominantHand, TickAmplitude, TickSeconds);
        }

        private static void StartAim(Aim how, Kind kind, VRContext context, double seconds)
        {
            aim = how;
            aimContext = context;
            aimStartedAt = seconds;
            shownLabel = null;
            castFrame = 0;
            Log.Info($"Ping: aiming to {(kind == Kind.Ping ? "ping" : "mark")} ({(how == Aim.HeldX ? "X held" : "actions wheel")}, {(context == VRContext.Ship ? "head" : "hand")} ray)");
        }

        private static void EndAim(string why)
        {
            if (aim == Aim.None)
                return;
            aim = Aim.None;
            ClearLabel();
            if (why != null)
                Log.Info("Ping: aim ended, " + why);
        }

        /// <summary>The prompt while aiming, per how it was started.</summary>
        private static string AimVerb => aim == Aim.Wheel ? (wheelKind == Kind.Ping ? "Trigger: ping" : "Trigger: mark GPS")
            : MarkName is string mark ? "Let go: ping. " + mark + ": mark GPS" : "Let go: ping";

        /// <summary>
        /// What marks a GPS while a ping is aimed by holding: the control alone when the button that aims is its held button
        /// ("Trigger", with X held), else all of it ("Right Menu + Left trigger" on a Vive wand); null with none bound.
        /// </summary>
        private static string MarkName
        {
            get
            {
                Binding mark = ActionNames.Find(VRAction.MarkGps), ping = ActionNames.Find(VRAction.Ping);
                if (mark == null)
                    return null;
                if (ping != null && mark.Modifier.HasValue && mark.Modifier.Value.Equals(ping.Control))
                    return ActionNames.Control(mark.Control.Hand, mark.Control.Control);
                return ActionNames.Name(VRAction.MarkGps);
            }
        }
    }
}
