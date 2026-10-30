using System;
using System.Diagnostics;
using System.Text;
using HarmonyLib;
using Sandbox;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SpaceEngineersVR.Common;
using SpaceEngineersVR.Input;
using VRageMath;
using VRageRender.Animations;

namespace SpaceEngineersVR.Tracking
{
    /// <summary>
    /// On foot in first person, the local character's body is drawn further forward under the eye, so the shoulders sit
    /// where a person's do: the game puts the camera on HeadDummy, at the front of the helmet, and the astronaut's
    /// shoulders are then 21 to 27 cm behind the eyes, which makes the arms look long and leaves a hand held at the waist
    /// out of their reach. The eye stays where it was in the world; only the body moves, so turning (<see cref="BodyFollow"/>)
    /// still turns the body round the eye and the view does not swing.
    /// </summary>
    /// <remarks>
    /// The game itself moves every bone in first person so the head bone is over the character's origin
    /// (MyCharacter.UpdateHeadAndWeapon, MyCharacterBone.TranslateAllBones); this moves them all <see cref="Forward"/> further,
    /// right after, in the same way. HeadDummy goes with them, and the head matrix (the camera, the aim, the hands' placement)
    /// is built from it, so <see cref="CharacterHead"/> takes the move off the head matrix again (<see cref="Ahead"/>). It is
    /// taken off only while the head bone is where this put it: a head matrix built before the game's own move in a frame,
    /// on bones the animation has just set, is left as the game builds it. Not in a seat (the seat places the body, and the
    /// body moved in it would go through the seat's back and the console), on a ladder or in third person, where the game
    /// does not move the bones either. <see cref="BodyVolume"/>'s table is the body around HeadDummy, so the eye's push-out
    /// is reckoned from where the body is drawn (<see cref="BodyEye.Offset"/>).
    /// <para>How far, each simulation frame (<see cref="Plan"/>, the same all frame, whoever asks first):</para>
    /// <list type="bullet">
    /// <item>With the head level the whole distance, and less as the head looks down, none from <see cref="PitchNone"/>: the
    /// body slides back under the eye as a person's shoulders do under a bending neck, so looking down shows the feet past
    /// the suit's neck ring and not the ring. That change is slowed to <see cref="Slide"/>.</item>
    /// <item>Only as far as there is room in front of the eye (<see cref="BodyGuard.Room"/>): a head sunk toward the collar
    /// would otherwise have the body come forward into the eye and the push-out hold the eye up, against the head. That
    /// gives way at once, with the head, and stays back for <see cref="HoldFrames"/> after the last frame the room held it
    /// before it comes forward again at <see cref="Return"/>.</item>
    /// </list>
    /// The room is steep in the eye's height where the eyes are over the suit's neck ring: across 2 cm of head height the
    /// room at the eyes changes by 7.5 cm (<see cref="BodyTable"/>), so about 4 cm of body for 1 cm of head. The hands are
    /// placed from the eye (<see cref="HandIK"/>), so every centimetre the body moves under the eye changes how far the
    /// arms must reach; without a hold, a nod, a step's bob or the head settling there would draw the body back and forward
    /// within 0.1-0.3 s, and both arms with it. Held, it gives way to the lowest the head goes and stays there.
    /// The head matrix is built before the bones are moved in a frame as well as after (the aim, the use ray), so the
    /// push-out reckons from this frame's distance, not from where last frame's move left the bones.
    /// RenderDebug BodyForwardMm=n sets another distance (0 draws the body where the game puts it).
    /// </remarks>
    internal static class BodyShift
    {
        /// <summary>
        /// How far ahead of the game's placement the body is drawn with the head level, metres: the shoulders from 21-27 cm
        /// behind the eyes to about 10-14 cm. More puts the eye over the suit's neck ring (its front rim is 9-12 cm behind HeadDummy).
        /// </summary>
        public const float Forward = 0.12f;

        /// <summary>Head pitch, degrees (down negative): the whole distance down to here, none from <see cref="PitchNone"/>, eased between.</summary>
        public const float PitchFull = -20f;

        /// <summary>Head pitch, degrees: looking further down than this, the body is where the game puts it.</summary>
        public const float PitchNone = -60f;

        /// <summary>The most the head's pitch moves the body in a simulation frame, metres (0.3 m/s: the whole distance in 0.4 s).</summary>
        private const float Slide = 0.3f / 60f;

        /// <summary>The most the body comes forward again after giving way, in a simulation frame, metres (0.25 m/s).</summary>
        private const float Return = 0.25f / 60f;

        /// <summary>
        /// After the room has made the body give way, the simulation frames it stays back before it comes forward again
        /// (0.75 s), counted from the last frame the room held it (gave way, or came back to within <see cref="Touch"/> of
        /// it): a head bobbing near the neck ring keeps it back at the lowest the head went, and it comes forward only once
        /// the head has stayed clear for this long. Longer than a step (0.45-0.67 s at 1.5-2.2 steps a second), so a walk's
        /// bob holds it the whole walk. Counted only from giving way, a step that outlasted the hold would draw it forward and
        /// back again every step.
        /// </summary>
        private const ulong HoldFrames = 45;

        /// <summary>The room coming back to within this of where the body is held, metres, holds it again (<see cref="HoldFrames"/>).</summary>
        private const float Touch = 0.003f;

        /// <summary>The simulation frame the room last made the body give way (<see cref="ulong.MaxValue"/>: not since it was last drawn ahead).</summary>
        private static ulong gaveWayFrame = ulong.MaxValue;

        /// <summary>RenderDebug BodyForwardMm=n: this many millimetres instead of <see cref="Forward"/> (null: not set; 0: the body where the game puts it).</summary>
        public static int? DebugMm { get; set; }

        /// <summary>The character the bones were last moved for, by how much, and the head bone's model-space z right after.</summary>
        private static MyCharacter shifted;
        private static float shiftedBy, shiftedZ;

        /// <summary>The character on foot in first person last frame, and how far its body was drawn ahead then (0 or more).</summary>
        private static MyCharacter drawnFor;
        private static float drawn;

        /// <summary>This frame's plan: for whom, which frame, the distance, the pitch's share before the room, what the room took, the pitch.</summary>
        private static MyCharacter plannedFor;
        private static ulong plannedFrame = ulong.MaxValue;
        private static float planned, pitched, roomTook, pitchDeg;

        private static float noted = -1f;
        private static readonly FeatureGuard guard = new FeatureGuard(nameof(BodyShift));
        private static int shareNoted;
        private static bool earlyNoted;

        /// <summary>The distance asked for with the head level, metres (0: off).</summary>
        public static float Distance => DebugMm.HasValue ? Math.Max(0, Math.Min(300, DebugMm.Value)) / 1000f : Forward;

        /// <summary>The distance the body was drawn ahead by this frame, metres: <see cref="Distance"/>, less the head's pitch and what it gave way to the head.</summary>
        public static float Applied => shifted != null ? shiftedBy : 0f;

        public static void Patch(Harmony harmony)
        {
            try
            {
                harmony.Patch(AccessTools.Method(typeof(MyCharacter), "UpdateHeadAndWeapon"),
                    postfix: new HarmonyMethod(typeof(BodyShift), nameof(AfterHeadAndWeapon)));
            }
            catch (Exception e)
            {
                PatchRegistry.Lost(e, "Body shift could not be hooked; the body stays where the game puts it under the eye");
                throw;
            }
        }

        /// <summary>
        /// How far the head matrix being built for <paramref name="character"/> is to go back (metres along the body's back):
        /// what this moved the body forward by, while the head bone is still where the move put it; 0 otherwise.
        /// </summary>
        public static float Ahead(MyCharacter character, int headBone)
        {
            if (character != shifted || shiftedBy == 0f)
                return 0f;
            Matrix[] bones = character.BoneAbsoluteTransforms;
            if (bones == null || headBone < 0 || headBone >= bones.Length)
                return 0f;
            // Exactly the value the move left: the animation sets the bones afresh each frame, and then this is not it.
            return bones[headBone].Translation.Z == shiftedZ ? shiftedBy : 0f;
        }

        /// <summary>
        /// Where the eye's push-out is to reckon the body from, for a head matrix whose bones are <paramref name="ahead"/> in
        /// front (<see cref="Ahead"/>): this frame's distance (<see cref="Plan"/>), which the bones are moved to before the
        /// body is drawn, also when the head matrix is built before that.
        /// </summary>
        public static float Reckon(MyCharacter character, float ahead)
        {
            float plan = Plan(character);
            if (!earlyNoted && Math.Abs(plan - ahead) > 0.005f && ahead > 0f)
            {
                earlyNoted = true;
                Log.Info($"Body shift: a head matrix was built before the body was moved this frame (bones {ahead * 100f:F1} cm ahead, this frame {plan * 100f:F1}); " +
                         $"the push-out reckons from this frame's. Built from {Callers()}");
            }
            return plan;
        }

        /// <summary>
        /// How far ahead the body is drawn this simulation frame, metres: computed once, by the first to ask (a head matrix
        /// built early in the frame, or the move itself), and the same for everyone after.
        /// </summary>
        public static float Plan(MyCharacter character)
        {
            ulong frame = MySandboxGame.Static.SimulationFrameCounter;
            if (character == plannedFor && frame == plannedFrame)
                return planned;
            plannedFor = character;
            plannedFrame = frame;
            float asked = Applies(character) ? Distance : 0f;
            bool was = drawnFor == character;
            bool tracked = asked > 0f && GameHead.Take();
            pitchDeg = tracked ? MathHelper.ToDegrees(GameHead.Pitch) : 0f;
            // Less as the head looks down, slowed; then as far as there is room in front of the eye, at once; forward again
            // held for a moment after the room last took some, then eased.
            float target = asked * PitchShare(pitchDeg);
            pitched = was ? Ease.Approach(pitched, target, Slide) : target;
            float by = tracked ? BodyGuard.Room(character, GameHead.Head.Translation, pitched) : pitched;
            roomTook = pitched - by;
            if (!was)
                gaveWayFrame = ulong.MaxValue;
            else
            {
                // The room is what holds the body here (it gave way, or came back to within a touch of where the body is):
                // the hold starts again.
                if (roomTook > 0f && by <= drawn + Touch)
                    gaveWayFrame = frame;
                by = Holding(frame) ? Math.Min(by, drawn) : Math.Min(by, drawn + Return);
            }
            planned = Math.Max(0f, by);
            return planned;
        }

        /// <summary>The body is being held back after the room last made it give way (<see cref="HoldFrames"/>).</summary>
        private static bool Holding(ulong frame) => frame >= gaveWayFrame && frame - gaveWayFrame < HoldFrames;

        /// <summary>
        /// For the arms' jump log (<see cref="HandIK"/>): this frame's distance, the pitch's share before the room, what the
        /// room took, and whether it is held back after giving way.
        /// </summary>
        internal static string Describe()
        {
            ulong frame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            return $"body {Applied * 100f:F1} cm ahead (the pitch's {pitched * 100f:F1}, the room took {roomTook * 100f:F1}" +
                   (Holding(frame) ? $", held, {frame - gaveWayFrame} frames since the room last held it)" : ")");
        }

        /// <summary>The share of the distance at this head pitch (degrees): 1 down to <see cref="PitchFull"/>, 0 from <see cref="PitchNone"/>, smoothstep between.</summary>
        public static float PitchShare(float pitch)
        {
            float t = MathHelper.Clamp((pitch - PitchNone) / (PitchFull - PitchNone), 0f, 1f);
            return t * t * (3f - 2f * t);
        }

        /// <summary>The game's own condition for moving the bones under the head (MyCharacter.UpdateHeadAndWeapon), with the hands on.</summary>
        private static bool Applies(MyCharacter character)
        {
            return guard.On && VRInput.Active && character != null && character == MySession.Static?.LocalCharacter
                   && character.IsInFirstPersonView && MySession.Static.CameraController == character
                   && !character.IsBot && !character.IsOnLadder;
        }

        /// <summary>Game thread, each simulation frame, right after the game moved the bones under the head.</summary>
        private static void AfterHeadAndWeapon(MyCharacter __instance)
        {
            if (__instance != MySession.Static?.LocalCharacter || !guard.On)
                return;
            try
            {
                shifted = null;
                MyCharacterBone[] bones = __instance.AnimationController?.CharacterBones;
                int head = CharacterFields.HeadBoneIndex(__instance);
                float asked = Applies(__instance) ? Distance : 0f;
                if (asked <= 0f || bones == null || head < 0 || head >= bones.Length)
                {
                    drawnFor = null;
                    Note(0f);
                    return;
                }
                Note(asked);
                float by = Plan(__instance);
                drawnFor = __instance;
                drawn = by;
                NoteShare(asked, by);
                if (by <= 0f)
                    return;
                MyCharacterBone.TranslateAllBones(bones, new Vector3(0f, 0f, -by));
                shifted = __instance;
                shiftedBy = by;
                shiftedZ = __instance.BoneAbsoluteTransforms[head].Translation.Z;
            }
            catch (Exception e)
            {
                shifted = null;
                drawnFor = null;
                guard.Failed(e, "Body shift failed");
            }
        }

        /// <summary>One line when the distance asked for changes (off, on, another distance).</summary>
        private static void Note(float by)
        {
            if (by == noted)
                return;
            bool first = noted < 0f;
            noted = by;
            if (by > 0f)
                Log.Info($"Body shift: on foot in first person, the body is drawn up to {by * 100f:F1} cm ahead of the head bone (head level), the eye where it was");
            else if (!first)
                Log.Info("Body shift: the body is where the game puts it (off, or not on foot in first person)");
        }

        /// <summary>A line, at most every 5 seconds, while the body is drawn a centimetre or more short of the whole distance, and why.</summary>
        private static void NoteShare(float asked, float by)
        {
            if (asked - by < 0.01f || Environment.TickCount - shareNoted < 5000)
                return;
            shareNoted = Environment.TickCount;
            Vector3 head = GameHead.Head.Translation;
            Log.Info($"Body shift: drawn {by * 100f:F1} cm ahead of {asked * 100f:F1} (head pitch {pitchDeg:F0} deg: {pitched * 100f:F1} cm; " +
                     $"given way to the head {roomTook * 100f:F1} cm, head {head.Y:F2} m up, {head.Z:F2} m back from neutral)");
        }

        /// <summary>For the log: the methods that built the head matrix, innermost first.</summary>
        private static string Callers()
        {
            StackFrame[] frames = new StackTrace(2, false).GetFrames();
            if (frames == null)
                return "(no stack)";
            StringBuilder s = new StringBuilder();
            int shown = 0;
            foreach (StackFrame f in frames)
            {
                System.Reflection.MethodBase m = f.GetMethod();
                if (m == null)
                    continue;
                if (shown++ > 0)
                    s.Append(" < ");
                s.Append(m.DeclaringType?.Name).Append('.').Append(m.Name);
                if (shown >= 9)
                    break;
            }
            return s.ToString();
        }
    }
}
