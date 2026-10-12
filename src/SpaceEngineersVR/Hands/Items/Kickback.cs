using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using SpaceEngineersVR.Common;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.OpenXR;
using SpaceEngineersVR.Tracking;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// Kick for items made for two hands but held in one: while the trigger is down the held item and its aim (bullets, grind
    /// and drill point) are thrown about, and they settle back to the hand when it is let go. With both hands on the item
    /// (<see cref="TwoHand"/>) only <see cref="TwoHands"/> of the kick is left, none by default. Only the tool hand's pose
    /// (<see cref="HandTools.TryGetHand"/>) is moved, never the view, so the item's placement, the arm that follows it, the
    /// shot direction and the tool sensors take the kick and the camera does not.
    /// </summary>
    /// <remarks>
    /// <para>Which items: the game's item data puts the left hand on a grip of its own along the tool (SimulateLeftHand and a
    /// LeftHand spot) for the rifles, rocket launchers, grinders and hand drills. A pistol's support hand only cups the tool
    /// hand: in one hand a pistol flicks up per shot, cupped by the other it does not kick (<see cref="PistolsKick"/>). Welders
    /// and the block placer have SimulateLeftHand false and never kick.</para>
    /// <para>Guns kick per shot from the gun's own recoil (the weapon definition's vertical and horizontal ground recoil, times
    /// the game's multiplier for stance and aiming; off with the world's EnableRecoil), scaled up. The game applies that recoil
    /// to the character instead, turning the head on foot and spinning the body in zero g; in a headset that turns the view.
    /// For the local character, while the headset session has the focus, it is left out for a shot only when this kick
    /// reaches that shot and is at least as strong (<see cref="Judge"/>); otherwise the game's recoil applies
    /// (<see cref="KeepsRecoil"/>, <see cref="BeforeGameRecoil"/>, <see cref="AfterGunUpdate"/>): on the jetpack's spin as the
    /// game does it, and where the game would turn the head, which the tracked head overwrites, to the aim that fires the
    /// shots instead (the hand's, or the head's with the hand not tracked), never the view (<see cref="RecoilOnAim"/>).</para>
    /// <para>The grinder and drill fight the hand whenever they run (less in the air, <see cref="AirShare"/>): a yank on
    /// spin-up, a climb and a sideways pull while the trigger stays down, and jerks. Amounts are in units of the tool's own
    /// camera shake, so the drill, which the game shakes harder, kicks harder. The drill also twists the hand about its bit.</para>
    /// <para>Two springs: a fast one for the snap of each kick and a slow one for where the kicks have pushed the item, which
    /// returns in about 0.4 s after the trigger is let go. The drawn arm follows the item, so how far it can be thrown is held
    /// inside limits (<see cref="MaxUp"/>, <see cref="MaxSide"/>, <see cref="MaxTwist"/>, <see cref="MaxBack"/>), approached
    /// softly and never reached.</para>
    /// <para>The "Tool and weapon kickback" setting scales it (Off, Light: half, Full). RenderDebug Kickback=0 turns it off;
    /// KickScale, KickAir and KickTwoHand tune it live (<see cref="Scale"/>, <see cref="Air"/>, <see cref="TwoHands"/>);
    /// KickbackTrace=1 logs every frame. Otherwise one log line when a use starts and one when it has settled (see
    /// Kickback.Log.cs).</para>
    /// </remarks>
    internal static partial class Kickback
    {
        /// <summary>Debug: no kickback (<see cref="Rendering.RenderDebug"/> Kickback=0), whatever the setting says.</summary>
        public static bool Off { get; set; }

        /// <summary>Debug: a log line every simulation frame while an item is in use or settling (<see cref="Rendering.RenderDebug"/> KickbackTrace=1).</summary>
        public static bool Trace { get; set; }

        /// <summary>Live tuning: a multiplier on all of the kick, on top of the setting (<see cref="Rendering.RenderDebug"/> KickScale=x; default 1).</summary>
        public static double Scale { get; set; } = 1.0;

        /// <summary>Live tuning: the share of a grinder's or drill's kick left spinning in the air (<see cref="Rendering.RenderDebug"/> KickAir=x; default <see cref="AirShare"/>).</summary>
        public static double Air { get; set; } = AirShare;

        /// <summary>Live tuning: the share of the one-handed kick left with both hands on the item (<see cref="Rendering.RenderDebug"/> KickTwoHand=x; default <see cref="TwoHandShare"/>).</summary>
        public static double TwoHands { get; set; } = TwoHandShare;

        // ---- State (game thread) ----

        private static bool hooked;
        private static readonly FeatureGuard guard = new FeatureGuard(nameof(Kickback));
        private static int applyErrors, shotErrors;

        /// <summary>The item the state is for; a new one starts still.</summary>
        private static MyEntity current;
        private static MyHandItemDefinition currentItem;
        private static Kind currentKind;
        private static readonly Dictionary<MyHandItemDefinition, Hold> holds = new Dictionary<MyHandItemDefinition, Hold>();

        /// <summary>1 with the item in one hand, 0 with the other hand on its grip; eased (<see cref="TwoHandEaseSeconds"/>).</summary>
        private static double oneHand = 1.0;

        /// <summary>The share of the one-handed kick for the hands on the item: 1 in one hand, <see cref="TwoHands"/> in both, eased between.</summary>
        private static double hands = 1.0;

        /// <summary>How much of the kick reaches the item this frame: the setting's share, times <see cref="Scale"/> and <see cref="hands"/>, in use.</summary>
        private static double drive;

        private static double useTime, heat;
        private static bool inUse, biting, burst;

        /// <summary>Seconds since the item was last in use: a grinder or drill yanks as it spins up only after a rest (<see cref="SpinUpRestSeconds"/>).</summary>
        private static double restTime = SpinUpRestSeconds;

        /// <summary>A gun's climb and throw in this burst, degrees for a drive of 1: the slow spring's target is these times the drive, so Light, a live scale and the other hand taking the grip all scale what has built up.</summary>
        private static double burstUp, burstSide;

        /// <summary>The slow spring: where the kicks pushed the item (degrees up, degrees right, degrees of twist) and where it is headed.</summary>
        private static double slowUp, slowSide, slowTwist, slowUpV, slowSideV, slowTwistV, targetUp, targetSide, targetTwist;

        /// <summary>The fast spring: the snap of each kick (degrees up, degrees right, degrees of twist, metres back).</summary>
        private static double fastUp, fastSide, fastTwist, fastBack, fastUpV, fastSideV, fastTwistV, fastBackV;

        /// <summary>What <see cref="Apply"/> turns the pose by: radians up, radians right, radians of twist (the top to the right), metres back. Written once a frame.</summary>
        private static double outUp, outSide, outTwist, outBack;

        /// <summary>When the kick's last pulse ends (<see cref="Stopwatch"/> ticks), for <see cref="Rumbling"/>.</summary>
        private static long rumbleUntil;

        /// <summary>The game's own sideways draw (-1..1) for the shot being fired, kept from its recoil (<see cref="BeforeGameRecoil"/>) for the wrist; null when it has none.</summary>
        private static float? gameSide;

        /// <summary>The game's recoil since the last log line: shots it was left out of and shots it applied to, and the most the head angles were turned back in a frame (degrees).</summary>
        private static int recoilKept, recoilLet;
        private static double headUndone;

        /// <summary>The shot whose kick was already judged and applied as the game's recoil arrived inside it (on the server, <see cref="BeforeGameRecoil"/>), so <see cref="AfterShoot"/> does not kick it again.</summary>
        private static MyAutomaticRifleGun judgedShot;

        /// <summary>On a client: the verdicts of the local shots whose recoil has not arrived from the server yet, oldest first.</summary>
        private static readonly Queue<Verdict> unanswered = new Queue<Verdict>();

        /// <summary>The gun whose own recoil was let through and is still running (rising or going back): its head animation is left to it.</summary>
        private static MyAutomaticRifleGun gameRecoilGun;

        /// <summary>
        /// What the game's recoil would hold over the current burst (degrees, <see cref="Judge"/>): the gun, its total and
        /// the game time of its last shot (milliseconds).
        /// </summary>
        private static MyAutomaticRifleGun burstGun;
        private static double burstGame;
        private static int burstAt;

        /// <summary>The reason last logged for the game's recoil being left out or applied, and how many such lines so far.</summary>
        private static string recoilWhy, recoilMode;
        private static int recoilLines;

        private const int MaxRecoilLines = 40, MaxUnanswered = 16;

        /// <summary>On a client, how long a shot's verdict waits for its recoil from the server (seconds); later, the recoil applies.</summary>
        private const double UnansweredSeconds = 1.0;

        // ---- One use, for the log ----

        private static bool episode, episodeGun;
        private static string episodeItem;
        private static long episodeStart, settleSince;
        private static int framesOne, framesTwo, shots, jerks, spinUps, misses, ownMisses, useFrames, biteFrames, limitedFrames, trackedFrames;
        private static double peakUp, peakSide, peakTwist, peakBack, viewTurn, viewMove;
        private static readonly List<Vector3D> hits = new List<Vector3D>(), ownHits = new List<Vector3D>();
        private static MatrixD lastView;
        private static bool lastViewValid;

        private const int MaxHits = 900;

        /// <summary>How far ahead of the hand the aim point is looked for (metres); a hit further off counts as a miss.</summary>
        private const double MeasureReach = 6.0, MeasureStart = 0.25;

        // ---- Hooks ----

        /// <summary>The per-frame update (after the character's own) and each shot of a hand gun.</summary>
        public static void Patch(Harmony harmony)
        {
            if (!PatchRegistry.Apply("kick", () =>
            {
                harmony.Patch(AccessTools.Method(typeof(MyCharacter), nameof(MyCharacter.UpdateAfterSimulation)),
                    postfix: new HarmonyMethod(typeof(Kickback), nameof(AfterSimulation)));
                // After the shot's own buzz (Haptics, normal priority), so the kick's pulse is the one that plays.
                harmony.Patch(AccessTools.Method(typeof(MyAutomaticRifleGun), nameof(MyAutomaticRifleGun.Shoot),
                        new[] { typeof(MyShootActionEnum), typeof(Vector3), typeof(Vector3D?), typeof(string) }),
                    postfix: new HarmonyMethod(typeof(Kickback), nameof(AfterShoot)) { priority = Priority.Low });
                hooked = true;
                Log.Info($"Kickback: hooked (a two-handed item in one hand kicks; guns {GunClimbScale}x their recoil up to {GunMaxClimb} deg, pistols a {PistolClimbScale}x flip " +
                         $"peaking {1000.0 / PistolOmega:0} ms after the shot; grinder and drill jerks {JerkDegreesPerShake} deg and climb {ClimbDegreesPerShake} deg per unit of the game's shake, " +
                         $"{AirShare} of it in the air, the drill twisting {DrillTwistDegreesPerShake} deg; two hands {TwoHandShare}; at most {MaxUp} deg up, {MaxSide} aside, " +
                         $"{MaxTwist} twist, {MaxBack * 100} cm back; settles in about {4.74 / SettleOmega:0.00} s; recoil multipliers {(RecoilMultipliers != null ? "from the game" : "not found, 1")})");
            }, "Kickback could not be hooked; tools and guns in one hand do not kick"))
                hooked = false;

            // The game's own recoil on the local character in VR, left out of a shot only when the wrist kick reaches it and is
            // at least as strong: the shot's recoil as it arrives (on the server at once, on a client when the server's value
            // does), and the head angles the gun's update animates (a save and restore around it, so nothing it does to the
            // head is missed while its recoil is left out).
            PatchRegistry.Apply("game recoil", () =>
            {
                harmony.Patch(AccessTools.Method(typeof(MyAutomaticRifleGun), "RecoilValueChangedCallback"),
                    prefix: new HarmonyMethod(typeof(Kickback), nameof(BeforeGameRecoil)));
                harmony.Patch(AccessTools.Method(typeof(MyAutomaticRifleGun), nameof(MyAutomaticRifleGun.UpdateAfterSimulation)),
                    prefix: new HarmonyMethod(typeof(Kickback), nameof(BeforeGunUpdate)),
                    postfix: new HarmonyMethod(typeof(Kickback), nameof(AfterGunUpdate)));
                Log.Info("Kickback: while the headset has the focus, the game's own gun recoil is left out of a shot the wrist kick replaces at least as strongly; " +
                         "otherwise it applies, turning the aim that fires the shot where the game would turn the head (never the view), and on the jetpack's spin as usual");
            }, "Kickback: the game's own gun recoil could not be hooked; each shot turns the head (or spins the body on the jetpack), and the view with it");
        }

        /// <summary>The share of the one-handed kick the setting asks for: 0 (Off, or RenderDebug Kickback=0), a half (Light), all (Full).</summary>
        private static double Share => Off ? 0.0 : VRSettings.KickbackShare;

        // ---- The tool hand's pose ----

        /// <summary>
        /// Turns the tool hand's grip and aim poses (as <see cref="HandTools.TryGetHand"/> gives them, after
        /// <see cref="TwoHand.Apply"/>) by this frame's kick: twisted about the aim, up and to the side about the tool hand's
        /// grip (a pistol's wrist), then back along the aim. Then by the game's own recoil where it applies to the aim
        /// (<see cref="RecoilOnAim"/>), up and to the side about the grip. Nothing while there is neither.
        /// </summary>
        internal static void Apply(MyCharacter character, ref MatrixD grip, ref MatrixD aim)
        {
            double up = outUp, side = outSide, twist = outTwist, back = outBack;
            bool kick = (up != 0.0 || side != 0.0 || twist != 0.0 || back != 0.0) && character != null && current != null && ReferenceEquals(character.CurrentWeapon, current);
            bool recoil = GameRecoilOnAim(character, out double recoilUp, out double recoilRight);
            if (!kick && !recoil)
                return;
            try
            {
                if (kick)
                    Turn(ref grip, ref aim, up, side, twist, back, PivotBehindGrip);
                if (recoil)
                    Turn(ref grip, ref aim, recoilUp, recoilRight, 0.0, 0.0, 0.0);
            }
            catch (Exception e)
            {
                if (applyErrors++ < 3)
                    Log.Error(e, "Kickback could not turn the tool hand's pose");
            }
        }

        /// <summary>How far behind the grip, along the aim, the item turns: a pistol about the wrist, the rest about the grip.</summary>
        private static double PivotBehindGrip => currentKind == Kind.Pistol ? PistolWristBehindGrip : 0.0;

        private static void Turn(ref MatrixD grip, ref MatrixD aim, double up, double side, double twist, double back, double pivotBehind)
        {
            Vector3D forward = aim.Forward;
            double length = forward.Length();
            if (!(length > 1e-6))
                return;
            forward /= length;
            Vector3D upAxis = Vector3D.Reject(aim.Up, forward);
            if (!(upAxis.LengthSquared() > 1e-12))
                return;
            upAxis.Normalize();
            Vector3D right = Vector3D.Cross(forward, upAxis);
            // The twist about the aim (a turn about forward takes the top to the right), then up about the aim's right (the
            // muzzle rises), then to the side about its up (a turn about up is to the left).
            MatrixD turn = MatrixD.CreateFromAxisAngle(forward, twist) * MatrixD.CreateFromAxisAngle(right, up) * MatrixD.CreateFromAxisAngle(upAxis, -side);
            Vector3D pivot = grip.Translation - forward * pivotBehind;
            MatrixD m = MatrixD.CreateTranslation(-pivot) * turn * MatrixD.CreateTranslation(pivot - forward * back);
            grip *= m;
            aim *= m;
        }

        // ---- Each shot ----

        /// <summary>
        /// A shot of the gun in hand. It comes before this frame's <see cref="Update"/> (the character fires before its own
        /// UpdateAfterSimulation ends), and a semi-automatic fires only on the first frame of a press, when <see cref="drive"/>
        /// is still the last frame's nothing: so the shot's share is worked out here, from the same parts, and the log's
        /// episode opens here if it is not open yet.
        /// </summary>
        private static void AfterShoot(MyAutomaticRifleGun __instance, MyShootActionEnum action)
        {
            try
            {
                if (ReferenceEquals(judgedShot, __instance) || action != MyShootActionEnum.PrimaryAction)
                    return;
                Verdict verdict = KickShot(__instance, 1f, null);
                // A client hears the shot's recoil later, from the server; the verdict waits for it.
                if (!Sandbox.Game.Multiplayer.Sync.IsServer && KeepsRecoil(__instance.Owner))
                {
                    if (unanswered.Count >= MaxUnanswered)
                        unanswered.Dequeue();
                    unanswered.Enqueue(verdict);
                }
            }
            catch (Exception e)
            {
                if (shotErrors++ < 3)
                    Log.Error(e, "Kickback could not kick a shot");
            }
            finally
            {
                judgedShot = null;
                gameSide = null;
            }
        }

        /// <summary>A shot of the gun: judged against the game's recoil for it (<see cref="Judge"/>, before its kick lands), then kicked if the kick is on for it.</summary>
        private static Verdict KickShot(MyAutomaticRifleGun gun, float vertical, float? side)
        {
            Verdict verdict = Judge(gun, gun.Owner, vertical, side);
            if (verdict.Drive > 0.0)
            {
                if (!episode)
                    Begin(currentItem, Stopwatch.GetTimestamp());
                Shot(gun, verdict.Drive);
            }
            return verdict;
        }

        /// <summary>One shot's kick, <paramref name="shotDrive"/> of the one-handed kick at Full.</summary>
        private static void Shot(MyAutomaticRifleGun gun, double shotDrive)
        {
            ShotRecoil(gun, out double vertical, out double horizontal);
            // Sideways: the game's own draw for this shot when its recoil came with it (on the server, at once), else a draw like it.
            double draw = gameSide.HasValue ? MathHelper.Clamp(gameSide.Value, -1.0, 1.0) : random.NextDouble() * 2.0 - 1.0;
            if (currentKind == Kind.Pistol)
            {
                // A flip per shot that comes back on its own: a shot before the last has come back adds to what is left of it.
                double scale = PistolClimbScale * shotDrive;
                Kick(ref fastUpV, vertical * scale, PistolOmega);
                Kick(ref fastSideV, horizontal * draw * scale, PistolOmega);
                Kick(ref fastBackV, PistolBack * shotDrive, PistolOmega);
                shots++;
                if (hands >= 0.5)
                    Rumble(ShotKickAmplitude, PistolKickSeconds);
                return;
            }
            // A burst starts from wherever the last one left the gun.
            if (!burst)
            {
                burst = true;
                burstUp = MathHelper.Clamp(slowUp / shotDrive, -GunMaxClimb, GunMaxClimb);
                burstSide = MathHelper.Clamp(slowSide / shotDrive, -GunMaxSide, GunMaxSide);
            }
            burstUp += vertical * GunClimbScale * Math.Max(0.0, 1.0 - burstUp / GunMaxClimb);
            double throw_ = horizontal * draw * GunSideScale;
            burstSide += throw_ * Math.Max(0.0, 1.0 - Math.Sign(throw_) * burstSide / GunMaxSide);
            Kick(ref fastUpV, vertical * GunJoltScale * shotDrive, JoltOmega);
            Kick(ref fastBackV, GunBack * shotDrive, JoltOmega);
            shots++;
            if (hands >= 0.5)
                Rumble(ShotKickAmplitude, ShotKickSeconds);
        }

        /// <summary>The gun's recoil for a shot that the kick is scaled from, degrees: its ground recoil times the game's multiplier for the stance, or the fallback for a gun with none.</summary>
        private static void ShotRecoil(MyAutomaticRifleGun gun, out double vertical, out double horizontal)
        {
            MyWeaponDefinition definition = gun.GunBase?.WeaponDefinition;
            vertical = definition?.RecoilGroundVertical ?? 0.0;
            horizontal = definition?.RecoilGroundHorizontal ?? 0.0;
            if (!(vertical > 0.0) && !(horizontal > 0.0))
            {
                vertical = GunFallbackVertical;
                horizontal = GunFallbackHorizontal;
            }
            else if (RecoilMultipliers != null && gun.Owner != null)
            {
                RecoilMultipliers(gun, gun.Owner, out float v, out float h);
                vertical *= v;
                horizontal *= h;
            }
        }

        /// <summary>The kick's own pulse on the tool hand, and how long the tool's contact buzz waits for it (<see cref="Rumbling"/>).</summary>
        private static void Rumble(float amplitude, float seconds)
        {
            Haptics.Pulse(Haptics.ToolHand, amplitude, seconds);
            rumbleUntil = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
        }

        /// <summary>
        /// A kick's pulse is still playing. The grinder's and drill's light contact buzz is asked for every frame, and the runtime
        /// replaces a vibration in progress with the next one, so it would cut each jerk to a frame; it waits for it instead
        /// (<see cref="Haptics.ToolFrame"/>).
        /// </summary>
        internal static bool Rumbling => Stopwatch.GetTimestamp() < rumbleUntil;

        /// <summary>A kick that peaks at <paramref name="peak"/> on the fast spring (of rate <paramref name="omega"/>), as a push on its speed.</summary>
        private static void Kick(ref double velocity, double peak, double omega) => velocity += peak * omega * Math.E;

        /// <summary>The fast spring's rate for the item in hand: a pistol's flick is slower than a rifle's snap.</summary>
        private static double FastOmega => currentKind == Kind.Pistol ? PistolOmega : JoltOmega;

        // ---- The game's own recoil on the local character ----

        /// <summary>
        /// The game's recoil on <paramref name="owner"/> is judged shot by shot (<see cref="Judge"/>): the local character,
        /// while the headset session has the focus. Without a headset in use (none connected, it was lost, or the runtime's
        /// dashboard is up) the head is the game's and the recoil turns it as usual.
        /// </summary>
        private static bool KeepsRecoil(MyCharacter owner) => XrSession.Focused && Haptics.IsLocal(owner);

        /// <summary>
        /// A shot's recoil reaching a hand gun (MyAutomaticRifleGun.RecoilValueChangedCallback, on the character's synced
        /// recoil value). On the jetpack, or falling in zero g, it sets the body's angular velocity (by RecoilJetpackVertical
        /// and Horizontal, degrees a second): the whole character turns. Otherwise it sets the head's next pitch
        /// (RecoilGroundVertical times the movement multiplier, animated over the next frames by AnimateRecoil) and turns
        /// the head sideways at once (RecoilGroundHorizontal times its draw, AddHorizontalRecoil). For the local character in
        /// VR (<see cref="KeepsRecoil"/>) none of it runs when the shot's verdict says the wrist kick replaces it: the gun is
        /// then never recoiling, so the jetpack's own damping (MyCharacter, IsRecoiling) stops any spin as usual. With any
        /// other verdict the jetpack's spin runs as the game's, and a turn of the head goes to the aim instead
        /// (<see cref="RecoilOnAim"/>), the head angles left alone. An error here lets it all run.
        /// </summary>
        private static bool BeforeGameRecoil(MyAutomaticRifleGun __instance)
        {
            try
            {
                MyCharacter owner = __instance.Owner;
                if (!KeepsRecoil(owner))
                    return true;
                owner.GetRecoilData(out float vertical, out float side);
                Verdict verdict;
                if (Sandbox.Game.Multiplayer.Sync.IsServer)
                {
                    // On the server it arrives inside the shot, before AfterShoot: the shot is judged and kicked here, with its own draw.
                    gameSide = side;
                    judgedShot = __instance;
                    verdict = KickShot(__instance, vertical, side);
                }
                else
                {
                    verdict = Answer();
                }
                if (!verdict.Replaces && ComfortOff())
                {
                    verdict.Replaces = true;
                    verdict.Why = "Game recoil in VR is Off (single player)";
                    verdict.Figures = null;
                }
                Note(verdict);
                if (!verdict.Replaces)
                {
                    recoilLet++;
                    // Off the jetpack's spin the game would turn the head, which the tracked head overwrites: the aim takes it instead.
                    if (!(owner.ShouldRecoilRotate && owner.JetpackRunning) && RecoilOnAim(__instance, owner, vertical, side))
                        return false;
                    gameRecoilGun = __instance;
                    return true;
                }
                recoilKept++;
                return false;
            }
            catch (Exception e)
            {
                if (shotErrors++ < 3)
                    Log.Error(e, "Kickback could not judge the game's recoil; it applies");
                return true;
            }
        }

        /// <summary>
        /// The "Game recoil in VR" setting is Off and in force: only with no one else in the session, that is the game's own
        /// single-player test (Sync.MultiplayerActive false: no MyMultiplayer, an offline world) or a host alone in its
        /// session (Sync.IsServer, MyPlayerCollection.GetOnlineHumanPlayerCount 1). Anywhere else Off works as Auto. The
        /// mode in force is logged when it changes.
        /// </summary>
        private static bool ComfortOff()
        {
            bool alone = Sandbox.Game.Multiplayer.Sync.IsServer
                         && (!Sandbox.Game.Multiplayer.Sync.MultiplayerActive || (MySession.Static?.Players?.GetOnlineHumanPlayerCount() ?? 2) <= 1);
            bool off = VRSettings.GameRecoilOff && alone;
            string mode = off ? "Off (single player): the game's recoil is always left out"
                : VRSettings.GameRecoilOff ? "Auto (set to Off, but others are in the session)" : "Auto";
            if (mode != recoilMode)
            {
                recoilMode = mode;
                Log.Info($"Kickback: Game recoil in VR is {mode}");
            }
            return off;
        }

        /// <summary>On a client: the verdict of the oldest local shot still waiting for its recoil, or one that lets the recoil through when there is none.</summary>
        private static Verdict Answer()
        {
            long now = Stopwatch.GetTimestamp();
            while (unanswered.Count > 0)
            {
                Verdict verdict = unanswered.Dequeue();
                if (now - verdict.At <= UnansweredSeconds * Stopwatch.Frequency)
                    return verdict;
            }
            return new Verdict { Why = "no wrist kick is known for this shot" };
        }

        /// <summary>The kick for one shot, set against the game's recoil for it.</summary>
        private struct Verdict
        {
            /// <summary>The share of the one-handed kick at Full the shot kicks with; 0 when it does not kick.</summary>
            public double Drive;

            /// <summary>The kick reaches the shot and is at least as strong as the game's recoil for it, which can then be left out.</summary>
            public bool Replaces;

            /// <summary>Why (logged when it changes), and the figures behind it.</summary>
            public string Why, Figures;

            /// <summary>When it was judged (<see cref="Stopwatch"/> ticks).</summary>
            public long At;
        }

        /// <summary>
        /// The kick for a shot of <paramref name="gun"/>, worked out before it lands, set against the game's recoil for it
        /// (<paramref name="vertical"/> and <paramref name="side"/>: the game's synced values, the side null where not known
        /// yet). Both are measured as how far the shot turns the aim, in degrees.
        /// <list type="bullet">
        /// <item>The game's, a shot: on foot, the head's pitch and turn, RecoilGround times the stance's multiplier
        /// (sideways at the full draw where the draw is not known). On the jetpack, the most the body's spin can turn it
        /// before the character's damping stops it: the speed squared over twice the damping.</item>
        /// <item>The game's, over the burst: those added up while it would keep them (shots within the gun's reset time),
        /// and going back as fast as at most the game's return once it would let go.</item>
        /// <item>The kick's, a shot: only its snap up, the fast spring's peak, as much of it as gets past the soft limits
        /// with the gun already climbed and thrown aside as far as its burst can take it. The climb a shot adds, the sideways
        /// throw and the shove back are not counted.</item>
        /// <item>The kick's, over the burst: where the item is pushed now, with this shot's snap, past the soft limits.</item>
        /// </list>
        /// The game's recoil is left out only when the kick reaches the shot (the kick is on for this gun, and the hand is
        /// tracked, so the shot leaves along the kicked aim) and is at least the game's by both measures.
        /// </summary>
        private static Verdict Judge(MyAutomaticRifleGun gun, MyCharacter owner, float vertical, float? side)
        {
            var verdict = new Verdict { At = Stopwatch.GetTimestamp() };
            if (MySession.Static?.Settings?.EnableRecoil == false)
                return Against(ref verdict, "the world has recoil off");
            if (!hooked)
                return Against(ref verdict, "the wrist kick is not hooked");
            if (!(Share > 0.0))
                return Against(ref verdict, Off ? "kickback off (RenderDebug)" : "kickback Off");
            if (current == null || !IsGun(currentKind) || !ReferenceEquals(gun, current) || !Haptics.IsLocal(owner))
                return Against(ref verdict, "no wrist kick for this gun");
            verdict.Drive = Share * Math.Max(0.0, Scale) * hands;
            if (!(verdict.Drive > 0.0))
            {
                verdict.Drive = 0.0;
                return Against(ref verdict, "nothing is left of the wrist kick");
            }
            if (HandTools.Off || !HandWorld.TryGetNow(owner, VRSettings.DominantHand, out _, out _, out _))
                return Against(ref verdict, "the hand is not tracked");
            MyWeaponDefinition definition = gun.GunBase?.WeaponDefinition;
            if (definition == null || RecoilMultipliers == null)
                return Against(ref verdict, "the game's recoil figures could not be read");

            double game;
            if (owner.ShouldRecoilRotate && owner.JetpackRunning)
            {
                double damping = MathHelper.ToDegrees((double)(owner.Definition?.RecoilJetpackDampeningRadPerFrame ?? 0f)) / Step;
                double pitch = definition.RecoilJetpackVertical * vertical;
                double offset = MyAutomaticRifleGun.HORIZONTAL_RECOIL_OFFSET;
                double yaw = definition.RecoilJetpackHorizontal * (side.HasValue ? Math.Abs(side.Value) * (1.0 - offset) + offset : 1.0);
                game = damping > 0.0 ? (pitch * pitch + yaw * yaw) / (2.0 * damping) : double.PositiveInfinity;
            }
            else
            {
                RecoilMultipliers(gun, owner, out float v, out float h);
                double pitch = definition.RecoilGroundVertical * vertical * v;
                double yaw = definition.RecoilGroundHorizontal * (side.HasValue ? Math.Abs(side.Value) : 1.0) * h;
                game = Math.Sqrt(pitch * pitch + yaw * yaw);
            }

            int now = Sandbox.MySandboxGame.TotalGamePlayTimeInMilliseconds;
            double reset = gun.GunBase.WeaponProperties.RecoilResetTimeMilliseconds;
            double since = now - burstAt;
            if (!ReferenceEquals(gun, burstGun))
                burstGame = 0.0;
            else if (since > reset)
                burstGame *= Math.Max(0.0, 1.0 - MyAutomaticRifleGun.RECOIL_RETURN_SPEED * (since - reset) / (Step * 1000.0));
            burstGun = gun;
            burstAt = now;
            burstGame += game;

            ShotRecoil(gun, out double up, out _);
            bool rifle = currentKind == Kind.Gun;
            double snap = up * (rifle ? GunJoltScale : PistolClimbScale) * verdict.Drive;
            double climbed = (rifle ? Math.Max(slowUp, GunMaxClimb * verdict.Drive) : Math.Max(0.0, slowUp)) + Math.Max(0.0, fastUp);
            double aside = (rifle ? Math.Max(Math.Abs(slowSide), GunMaxSide * verdict.Drive) : Math.Abs(slowSide)) + Math.Abs(fastSide);
            double kick = LimitedUp(climbed + snap, aside) - LimitedUp(climbed, aside);
            double held = LimitedUp(slowUp + Math.Max(0.0, fastUp) + snap, Math.Abs(slowSide) + Math.Abs(fastSide));

            if (!(kick >= game))
                return Against(ref verdict, "the wrist kick is weaker", $"{kick:0.0} deg a shot against the game's {game:0.0}");
            if (!(held >= burstGame))
                return Against(ref verdict, "the burst has outgrown the wrist kick", $"the game would hold {burstGame:0.0} deg, the wrist kick {held:0.0}");
            verdict.Replaces = true;
            verdict.Why = "the wrist kick replaces it";
            verdict.Figures = $"{kick:0.0} deg a shot against the game's {game:0.0}";
            return verdict;
        }

        private static Verdict Against(ref Verdict verdict, string why, string figures = null)
        {
            verdict.Replaces = false;
            verdict.Why = why;
            verdict.Figures = figures;
            return verdict;
        }

        /// <summary>How far up the item is turned once <paramref name="up"/> and <paramref name="side"/> (degrees) have been through the soft limits (<see cref="Limit"/>).</summary>
        private static double LimitedUp(double up, double side)
        {
            double twist = 0.0, back = 0.0;
            Limit(ref up, ref side, ref twist, ref back);
            return up;
        }

        /// <summary>One log line when the reason the game's recoil is left out or applies changes, up to <see cref="MaxRecoilLines"/>.</summary>
        private static void Note(in Verdict verdict)
        {
            if (verdict.Why == recoilWhy)
                return;
            recoilWhy = verdict.Why;
            if (++recoilLines > MaxRecoilLines)
            {
                if (recoilLines == MaxRecoilLines + 1)
                    Log.Info("Kickback: further changes to whether the game's recoil applies are not logged; each use's settle line counts them");
                return;
            }
            string figures = verdict.Figures != null ? ": " + verdict.Figures : string.Empty;
            Log.Info(verdict.Replaces ? $"Kickback: the game's recoil is left out, {verdict.Why}{figures}" : $"Kickback: the game's recoil applies ({verdict.Why}{figures})");
        }

        /// <summary>The local character's head angles around a gun's update (Harmony's state, as ExitUpright's).</summary>
        public struct HeadAngles
        {
            public MyCharacter Owner;
            public float X, Y;
        }

        /// <summary>
        /// The local character's head angles before the gun's update, which animates the game's recoil into them; none taken
        /// while a recoil the game was let apply is still running (rising or going back), which is left to it.
        /// </summary>
        private static void BeforeGunUpdate(MyAutomaticRifleGun __instance, out HeadAngles __state)
        {
            __state = default;
            try
            {
                if (ReferenceEquals(gameRecoilGun, __instance))
                {
                    if (__instance.IsRecoiling)
                        return;
                    gameRecoilGun = null;
                }
                MyCharacter owner = __instance.Owner;
                if (KeepsRecoil(owner))
                    __state = new HeadAngles { Owner = owner, X = owner.HeadLocalXAngle, Y = owner.HeadLocalYAngle };
            }
            catch (Exception e)
            {
                if (shotErrors++ < 3)
                    Log.Error(e, "Kickback could not keep the game's recoil off the character");
            }
        }

        /// <summary>
        /// After the gun's update: whatever its recoil animation did to the local character's head angles (AnimateRecoil,
        /// AnimateRecoilHorizontal; with no recoil started there is normally nothing, but a stale return angle would turn
        /// the game's head) is put back, and the recoil on the aim moves on a frame as the game's would
        /// (<see cref="AnimateAimRecoil"/>).
        /// </summary>
        private static void AfterGunUpdate(MyAutomaticRifleGun __instance, HeadAngles __state)
        {
            try
            {
                if (ReferenceEquals(__instance, aimGun))
                    AnimateAimRecoil(__instance);
            }
            catch (Exception e)
            {
                AimStill();
                if (shotErrors++ < 3)
                    Log.Error(e, "Kickback could not move the game's recoil on the aim");
            }
            MyCharacter owner = __state.Owner;
            if (owner == null)
                return;
            try
            {
                float x = owner.HeadLocalXAngle, y = owner.HeadLocalYAngle;
                if (x == __state.X && y == __state.Y)
                    return;
                owner.HeadLocalXAngle = __state.X;
                owner.HeadLocalYAngle = __state.Y;
                headUndone = Math.Max(headUndone, Math.Max(Math.Abs(x - __state.X), Math.Abs(y - __state.Y)));
            }
            catch (Exception e)
            {
                if (shotErrors++ < 3)
                    Log.Error(e, "Kickback could not keep the game's recoil off the character");
            }
        }

        // ---- Each frame ----

        private static void AfterSimulation(MyCharacter __instance)
        {
            if (__instance != MySession.Static?.LocalCharacter || !guard.On)
                return;
            try
            {
                Update(__instance);
            }
            catch (Exception e)
            {
                Still();
                current = null;
                guard.Failed(e, "Kickback failed");
            }
        }

        private static void Update(MyCharacter character)
        {
            double share = Share;
            MyEntity tool = character.CurrentWeapon as MyEntity;
            MyHandItemDefinition item = character.HandItemDefinition;
            bool live = hooked && share > 0.0 && !HandTools.Off && VRInput.Active && HandTools.IsLocalOnFoot(character)
                        && !character.IsOnLadder && !character.Closed;
            Kind kind = live ? KindOf(tool, item) : Kind.None;

            // A new item (or none, or the feature off) starts still.
            if (!ReferenceEquals(tool, current) || kind != currentKind)
            {
                End("switched");
                Still();
                recoilKept = recoilLet = 0;
                headUndone = 0.0;
                current = kind != Kind.None ? tool : null;
                currentItem = item;
                currentKind = kind;
                oneHand = 1.0;
                hands = 1.0;
                restTime = SpinUpRestSeconds;
            }
            if (kind == Kind.None)
                return;

            bool twoHands = TwoHand.Holds(tool);
            oneHand = Ease.Approach(oneHand, twoHands ? 0.0 : 1.0, Step / TwoHandEaseSeconds);
            double twoShare = MathHelper.Clamp(TwoHands, 0.0, 1.0);
            hands = twoShare + (1.0 - twoShare) * Ease.Smooth01(oneHand);
            bool wasInUse = inUse;
            inUse = InUse(tool, kind, out biting);
            // The log's episode opens before this frame's kicks are counted, so a spin-up on the first frame of use is in it (a
            // gun's first shot, earlier in the frame, has opened it already: AfterShoot).
            if (inUse && !wasInUse && !episode)
                Begin(item, Stopwatch.GetTimestamp());
            drive = inUse ? share * Math.Max(0.0, Scale) * hands : 0.0;
            useTime = drive > 0.0 ? useTime + Step : 0.0;
            heat = drive > 0.0 ? HeatStart + (1.0 - HeatStart) * Ease.Smooth01(useTime / BuildSeconds) : 0.0;

            if (IsGun(kind))
            {
                // The climb holds while the trigger is down (between shots, through a reload) and goes back once it is let go,
                // or once the other hand has taken all of it.
                if (!(drive > 0.0))
                {
                    burst = false;
                    burstUp = burstSide = 0.0;
                }
                targetUp = burstUp * drive;
                targetSide = burstSide * drive;
                targetTwist = 0.0;
            }
            else
            {
                Tool(tool, kind, inUse && !wasInUse);
            }
            restTime = inUse ? 0.0 : restTime + Step;

            Spring(ref slowUp, ref slowUpV, targetUp, SettleOmega);
            Spring(ref slowSide, ref slowSideV, targetSide, SettleOmega);
            Spring(ref slowTwist, ref slowTwistV, targetTwist, SettleOmega);
            Spring(ref fastUp, ref fastUpV, 0.0, FastOmega);
            Spring(ref fastSide, ref fastSideV, 0.0, FastOmega);
            Spring(ref fastTwist, ref fastTwistV, 0.0, FastOmega);
            Spring(ref fastBack, ref fastBackV, 0.0, FastOmega);

            double up = slowUp + fastUp, side = slowSide + fastSide, twist = slowTwist + fastTwist, back = fastBack;
            if (Math.Abs(up) < 1e-4 && Math.Abs(side) < 1e-4 && Math.Abs(twist) < 1e-4 && Math.Abs(back) < 1e-5 && !inUse)
            {
                Still();
                up = side = twist = back = 0.0;
            }
            bool limited = Limit(ref up, ref side, ref twist, ref back);
            outUp = MathHelper.ToRadians(up);
            outSide = MathHelper.ToRadians(side);
            outTwist = MathHelper.ToRadians(twist);
            outBack = back;

            Measure(character, tool, item, twoHands, wasInUse, up, side, twist, back, limited);
        }

        /// <summary>The grinder's and the drill's climb, pull, twist, spin-up yank and jerks for this frame.</summary>
        private static void Tool(MyEntity tool, Kind kind, bool started)
        {
            double maxShake, mean, torque, torqueShare, twistPerShake, jerkTwist;
            if (kind == Kind.Grinder)
            {
                maxShake = MyAngleGrinder.GRINDER_MAX_SHAKE;
                mean = GrinderMean != null ? GrinderMean((MyAngleGrinder)tool) : GrinderMeanFallback;
                torque = GrinderTorqueSide;
                torqueShare = GrinderTorqueShare;
                twistPerShake = jerkTwist = 0.0;
            }
            else
            {
                maxShake = MyDrillBase.DRILL_MAX_SHAKE;
                MyDrillBase drillBase = DrillBase != null ? DrillBase((MyHandDrill)tool) : null;
                mean = drillBase != null && DrillMean != null ? DrillMean(drillBase) : DrillMeanFallback;
                torque = DrillTorqueSide;
                torqueShare = DrillTorqueShare;
                twistPerShake = DrillTwistDegreesPerShake * DrillTwistSide;
                jerkTwist = DrillJerkTwistShare * DrillTwistSide;
            }
            if (!(drive > 0.0))
            {
                targetUp = targetSide = targetTwist = 0.0;
                return;
            }
            double bite = biting ? 1.0 : MathHelper.Clamp(Air, 0.0, 1.5);
            double strength = drive * bite;
            targetUp = ClimbDegreesPerShake * maxShake * strength * heat;
            targetSide = targetUp * torqueShare * torque;
            targetTwist = twistPerShake * maxShake * strength * heat;

            // The motor spinning up yanks the tool, biting or not: the grinder to its torque's side, the drill about its bit.
            if (started && restTime >= SpinUpRestSeconds)
            {
                double yank = SpinUpDegreesPerShake * maxShake * drive;
                Kick(ref fastUpV, yank * 0.5, JoltOmega);
                if (kind == Kind.Drill)
                    Kick(ref fastTwistV, yank * DrillTwistSide, JoltOmega);
                else
                    Kick(ref fastSideV, yank * torque, JoltOmega);
                spinUps++;
                if (hands >= 0.5)
                    Rumble(SpinUpAmplitude, SpinUpSeconds);
            }

            if (random.NextDouble() >= JerksPerSecond * bite * Step)
                return;
            // The game's draw: an exponential with the tool's mean intensity, capped at its maximum shake.
            double size = Math.Min(-Math.Log(1.0 - random.NextDouble()) * mean, 1.0);
            double peak = size * maxShake * JerkDegreesPerShake * strength;
            double angle = MathHelper.ToRadians(JerkTilt * torque + (random.NextDouble() * 2.0 - 1.0) * JerkSpread);
            Kick(ref fastUpV, peak * Math.Cos(angle), JoltOmega);
            Kick(ref fastSideV, peak * Math.Sin(angle), JoltOmega);
            if (jerkTwist != 0.0)
                Kick(ref fastTwistV, peak * jerkTwist, JoltOmega);
            jerks++;
            if (hands >= 0.5)
                Rumble(JerkMinAmplitude + (JerkMaxAmplitude - JerkMinAmplitude) * (float)size, JerkSeconds);
        }

        /// <summary>
        /// Holds the throw inside what the drawn arm can follow (<see cref="MaxUp"/> and <see cref="MaxSide"/> together,
        /// <see cref="MaxTwist"/>, <see cref="MaxBack"/>): up to <see cref="LimitKnee"/> of a limit as it is, past it squeezed so
        /// the limit is approached and never reached. True when it took more than 5% off any of it (for the log).
        /// </summary>
        private static bool Limit(ref double up, ref double side, ref double twist, ref double back)
        {
            bool limited = false;
            double u = up / MaxUp, s = side / MaxSide;
            double r = Math.Sqrt(u * u + s * s);
            if (r > LimitKnee)
            {
                double k = Soft(r) / r;
                up *= k;
                side *= k;
                limited = k < 0.95;
            }
            limited |= Squeeze(ref twist, MaxTwist);
            limited |= Squeeze(ref back, MaxBack);
            return limited;
        }

        private static bool Squeeze(ref double value, double most)
        {
            double r = Math.Abs(value) / most;
            if (!(r > LimitKnee))
                return false;
            double k = Soft(r) / r;
            value *= k;
            return k < 0.95;
        }

        /// <summary>A share of a limit past <see cref="LimitKnee"/>, squeezed: the same at the knee and as steep, toward 1 and never past it.</summary>
        private static double Soft(double r) => LimitKnee + (1.0 - LimitKnee) * Math.Tanh((r - LimitKnee) / (1.0 - LimitKnee));

        /// <summary>The item is in use: a gun's trigger down, a grinder or drill on; and whether a tool bites into something (the game's own tests, as <see cref="Haptics"/> uses them).</summary>
        private static bool InUse(MyEntity tool, Kind kind, out bool bite)
        {
            bite = false;
            switch (kind)
            {
                case Kind.Gun:
                case Kind.Pistol:
                    bite = true;
                    return ((MyAutomaticRifleGun)tool).IsShooting;
                case Kind.Grinder:
                    var grinder = (MyAngleGrinder)tool;
                    bite = grinder.IsShooting && !grinder.IsHeatingUp && EffectAction != null && EffectAction(grinder) == MyShootActionEnum.PrimaryAction;
                    return grinder.IsShooting;
                case Kind.Drill:
                    var drill = (MyHandDrill)tool;
                    bite = drill.IsShooting && InDrillingRange != null && InDrillingRange(drill) && drill.IsDrillingAnObject;
                    return drill.IsShooting;
                default:
                    return false;
            }
        }

        /// <summary>What kind of kick the item has (see <see cref="HoldOf"/> and the remarks).</summary>
        private static Kind KindOf(MyEntity tool, MyHandItemDefinition item)
        {
            if (tool == null || item == null || tool is MyBlockPlacerBase)
                return Kind.None;
            Hold hold = HoldOf(item);
            if (hold == Hold.OneHand)
                return Kind.None;
            if (tool is MyAutomaticRifleGun)
                return hold == Hold.Along ? Kind.Gun : PistolsKick ? Kind.Pistol : Kind.None;
            if (hold != Hold.Along)
                return Kind.None;
            if (tool is MyAngleGrinder)
                return Kind.Grinder;
            if (tool is MyHandDrill)
                return Kind.Drill;
            return Kind.None;
        }

        /// <summary>
        /// The game puts its left hand on the pistols too (the four, the flare gun included), cupping the tool hand from the side
        /// rather than on a grip of their own along the gun. True: in one hand a pistol flips up with each shot (the Pistol
        /// figures above); held with the other hand cupping it, the two-handed share (none by default, <see cref="TwoHands"/>). False: pistols never kick.
        /// </summary>
        internal static readonly bool PistolsKick = true;

        /// <summary>How the game holds an item: in one hand, with the other on a grip of its own along it, or with the other cupping the tool hand.</summary>
        private enum Hold
        {
            OneHand,
            Along,
            Cupped,
        }

        /// <summary>
        /// The one test for which items kick, from the game's data: it puts the left hand on the item (SimulateLeftHand, and a
        /// LeftHand spot) on a grip along it (the test <see cref="TwoHand"/> aims by: the rifles, launchers, grinders, drills),
        /// or cupping the tool hand (share none: the pistols). Welders, the block placer and the like have SimulateLeftHand false.
        /// </summary>
        private static Hold HoldOf(MyHandItemDefinition item)
        {
            if (holds.TryGetValue(item, out Hold hold))
                return hold;
            hold = !item.SimulateLeftHand || !(item.LeftHand.Translation.LengthSquared() > 1e-8f) ? Hold.OneHand
                : TwoHand.AimShare(item.RightHand.Translation, item.LeftHand.Translation) > 0.0 ? Hold.Along
                : Hold.Cupped;
            holds[item] = hold;
            Log.Info($"Kickback: the {Name(item)} is " + (hold == Hold.Along ? "made for two hands; in one it kicks"
                : hold == Hold.Cupped ? "held with the other hand cupping the tool hand; " + (PistolsKick ? "a gun of these flips up with each shot in one hand" : "it never kicks")
                : "a one-hand item (the game leaves the other hand off it); it never kicks"));
            return hold;
        }

        // ---- The springs ----

        /// <summary>One frame of a critically damped spring toward <paramref name="target"/> (exact for a target that holds over the frame).</summary>
        private static void Spring(ref double x, ref double v, double target, double omega)
        {
            double y = x - target, e = Math.Exp(-omega * Step), k = v + omega * y;
            x = target + (y + k * Step) * e;
            v = (v - omega * k * Step) * e;
        }

        private static void Still()
        {
            slowUp = slowSide = slowTwist = slowUpV = slowSideV = slowTwistV = targetUp = targetSide = targetTwist = 0.0;
            fastUp = fastSide = fastTwist = fastBack = fastUpV = fastSideV = fastTwistV = fastBackV = 0.0;
            outUp = outSide = outTwist = outBack = 0.0;
            useTime = heat = drive = burstUp = burstSide = 0.0;
            burst = inUse = biting = false;
        }

        // ---- Helpers ----

        private static string Level()
        {
            if (Off)
                return "off (RenderDebug)";
            string level = new[] { "Off", "Light", "Full" }[Math.Max(0, Math.Min(2, VRSettings.KickbackLevel))];
            if (Scale != 1.0)
                level += $", KickScale {Scale:0.##}";
            if (Air != AirShare)
                level += $", KickAir {Air:0.##}";
            if (TwoHands != TwoHandShare)
                level += $", KickTwoHand {TwoHands:0.##}";
            return level;
        }

        private static double Angle(Vector3D a, Vector3D b)
        {
            double la = a.Length(), lb = b.Length();
            if (!(la > 1e-9) || !(lb > 1e-9))
                return 0.0;
            return MathHelper.ToDegrees(Math.Acos(MathHelper.Clamp(Vector3D.Dot(a, b) / (la * lb), -1.0, 1.0)));
        }

        private static bool IsGun(Kind kind) => kind == Kind.Gun || kind == Kind.Pistol;

        private static string Name(MyHandItemDefinition item) => item?.Id.SubtypeName ?? "tool";

        private static AccessTools.FieldRef<TOwner, TValue> Field<TOwner, TValue>(string name)
        {
            try
            {
                return AccessTools.FieldRefAccess<TOwner, TValue>(name);
            }
            catch (Exception e)
            {
                Log.Warn($"Kickback: {typeof(TOwner).Name}.{name} not found ({e.GetType().Name}); its fallback is used");
                return null;
            }
        }

        private static RecoilMultipliersCall Multipliers()
        {
            try
            {
                var method = AccessTools.Method(typeof(MyAutomaticRifleGun), "GetRecoilMultipliers", new[] { typeof(MyCharacter), typeof(float).MakeByRefType(), typeof(float).MakeByRefType() });
                return method == null ? null : (RecoilMultipliersCall)Delegate.CreateDelegate(typeof(RecoilMultipliersCall), method);
            }
            catch (Exception e)
            {
                Log.Warn($"Kickback: the game's recoil multipliers not found ({e.GetType().Name}); 1 is used");
                return null;
            }
        }
    }
}
