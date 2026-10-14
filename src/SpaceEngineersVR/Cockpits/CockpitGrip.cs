// CockpitGrip: a hand on a cockpit stick or handle bar closes round it, its fingers onto its surface and its palm kept off it.
using System;
using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using SpaceEngineersVR.Input;
using VRage.FileSystem;
using VRageMath;
using VRageRender.Animations;

namespace SpaceEngineersVR.Cockpits
{
    /// <summary>
    /// A hand holding a cockpit stick (<see cref="CockpitControls"/>) or a handle bar (<see cref="CockpitTouch"/>) wraps round it
    /// rather than through it. Its palm is placed and its fingers curled by ColbyJack1134's measurements (CockpitStickMath.GripPalm,
    /// <see cref="CockpitHandPose.StickAngle"/>), tuned for one grip's thickness; each control's grip is measured here from the
    /// installed cockpit's own model (<see cref="GripSurface"/>), and once the arm is placed (<see cref="Tracking.HandIK"/>):
    /// <list type="bullet">
    /// <item>each finger, thumb included, closes from open until it would go into the grip's surface, a finger's half-thickness
    /// (<see cref="FingerSkin"/>) off it, up to <see cref="MostShare"/> of his curl (a thin grip is closed round further than he
    /// had it); the index finger's curl still follows the trigger;</item>
    /// <item>the palm is eased out from the grip's line, from the next frame on (<see cref="PushPalm"/>), as far as its knuckles
    /// are inside the surface (<see cref="PalmSkin"/> off it), at most <see cref="MostPush"/>, and back in as far as they are clear.</item>
    /// </list>
    /// A steering wheel's or yoke's rim, and a control the model has no mesh for, keep his curl as it is.
    /// </summary>
    internal static class CockpitGrip
    {
        /// <summary>How far off the surface a finger bone stays (a gloved finger's half-thickness), and the palm's bones (the palm's), metres.</summary>
        private const float FingerSkin = .009f, PalmSkin = .014f;

        /// <summary>A finger's curl as shares of his: from nearly open, in steps, to this much closer than his.</summary>
        private const float OpenShare = .1f, ShareStep = .05f, MostShare = 1.4f;

        /// <summary>The most the palm is eased out off the grip, metres, and the share of its knuckles' depth taken in a frame.</summary>
        private const float MostPush = .03f, PushRate = .5f;

        /// <summary>One hand's hold: the control, how far its palm is eased out and which way (the body's model space).</summary>
        private sealed class Hold
        {
            public object Control;
            public float Push;
            public Vector3 Outward;
        }

        private static readonly Hold[] holds = { new Hold(), new Hold() };

        /// <summary>The surfaces measured, by control (null: none to be had).</summary>
        private static readonly Dictionary<object, GripSurface> surfaces = new Dictionary<object, GripSurface>();

        private static readonly FeatureGuard guard = new FeatureGuard(nameof(CockpitGrip));

        /// <summary>
        /// Game thread, from the arm solve, the arm placed, for a hand on one of his sticks or handle bars: his finger curl, then
        /// each finger closed onto the grip's surface, and the palm's depth in it noted for the next frame's push.
        /// <paramref name="fingers"/> holds <see cref="Tracking.HandIK"/>'s finger bones (index, middle, ring, little, thumb; three
        /// joints each) and <paramref name="palm"/> the palm bone.
        /// </summary>
        internal static void CloseFingers(Hand hand, MyCharacter body, MyCharacterBone[] bones, int[] fingers, int palm, float trigger)
        {
            CockpitHand.StickFingers(bones, fingers, trigger);
            Hold hold = holds[(int)hand];
            if (!guard.On || !Current(hand, body, out object control, out GripSurface surface, out Matrix restToModel))
            {
                hold.Control = null;
                return;
            }
            try
            {
                if (!ReferenceEquals(control, hold.Control))
                {
                    hold.Control = control;
                    hold.Push = 0f;
                }
                Matrix modelToRest = Matrix.Invert(restToModel);
                for (int finger = 0; finger < fingers.Length / 3; finger++)
                    Fit(bones, fingers, finger, trigger, surface, modelToRest);
                Palm(hold, bones, fingers, palm, surface, restToModel, modelToRest);
            }
            catch (Exception e)
            {
                hold.Control = null;
                guard.Failed(e, "Cockpit grip: the hand could not be fitted round the control; it is posed as before");
            }
        }

        /// <summary>
        /// Game thread, placing the palm on one of his controls (<paramref name="attached"/>, the body's model space): eased out off
        /// the grip as far as the last frame found the knuckles in it.
        /// </summary>
        internal static void PushPalm(Hand hand, ref Matrix attached)
        {
            Hold hold = holds[(int)hand];
            if (hold.Control != null && hold.Push > 0f)
                attached.Translation += hold.Outward * hold.Push;
        }

        /// <summary>The control the hand holds, its surface, and its rest space into the body's model space now; false when none of his, or no surface.</summary>
        private static bool Current(Hand hand, MyCharacter body, out object control, out GripSurface surface, out Matrix restToModel)
        {
            control = null;
            surface = null;
            restToModel = Matrix.Identity;
            if (body?.PositionComp == null)
                return false;
            Matrix pose;
            MyCockpit cockpit;
            CockpitRig rig;
            int actor;
            Vector3 contact, line;
            if (CockpitControls.TryHeldStick(hand, out CockpitRig.Stick stick, out pose, out cockpit, out rig))
            {
                control = stick;
                actor = stick.Actor;
                contact = stick.Contact;
                line = stick.GripShaft;
            }
            else if (CockpitTouch.TryHeldBar(hand, out CockpitRig.Handle handle, out pose, out cockpit, out rig))
            {
                control = handle;
                actor = handle.Actor;
                contact = handle.Center;
                line = handle.Line;
            }
            else
                return false;
            surface = SurfaceOf(rig, cockpit, control, actor, contact, line);
            if (surface == null)
                return false;
            restToModel = (Matrix)((MatrixD)pose * cockpit.WorldMatrix * body.PositionComp.WorldMatrixNormalizedInv);
            return restToModel.IsValid();
        }

        /// <summary>A control's surface, measured from its mesh in the installed cockpit model the first time it is held.</summary>
        private static GripSurface SurfaceOf(CockpitRig rig, MyCockpit cockpit, object control, int actor, Vector3 contact, Vector3 line)
        {
            if (surfaces.TryGetValue(control, out GripSurface known))
                return known;
            GripSurface surface = null;
            string subtype = cockpit.BlockDefinition.Id.SubtypeName;
            try
            {
                CockpitGeometry geometry = rig.Geometry(MyFileSystem.ContentPath);
                if (actor >= 0 && actor < geometry.Parts.Length)
                    surface = GripSurface.Measure(geometry.Parts[actor], contact, line);
                Log.Info(surface != null
                    ? $"Cockpit grip: {subtype}'s control (part {actor}) measured from the cockpit's model, its surface up to {surface.Widest * 100f:0.0} cm from its grip line; the hand closes round it"
                    : $"Cockpit grip: {subtype}'s control (part {actor}) has no mesh near its grip in the cockpit's model; the hand keeps the fixed curl");
            }
            catch (Exception e)
            {
                Log.Warn($"Cockpit grip: {subtype}'s control (part {actor}) could not be measured ({e.Message}); the hand keeps the fixed curl");
            }
            surfaces[control] = surface;
            return surface;
        }

        /// <summary>
        /// One finger closed from nearly open in steps of its share of his curl, as far as none of its bones would be in the
        /// surface; where even the first step is (the palm too deep), left there for the palm's push to clear.
        /// </summary>
        private static void Fit(MyCharacterBone[] bones, int[] fingers, int finger, float trigger, GripSurface surface, in Matrix modelToRest)
        {
            int first = fingers[finger * 3];
            if (first < 0 || first >= bones.Length || bones[first] == null)
                return;
            float best = OpenShare;
            for (float share = OpenShare; share <= MostShare + 1e-4f; share += ShareStep)
            {
                Pose(bones, fingers, finger, trigger, share);
                if (!FingerClear(bones, fingers, finger, surface, modelToRest))
                    break;
                best = share;
            }
            Pose(bones, fingers, finger, trigger, best);
        }

        private static void Pose(MyCharacterBone[] bones, int[] fingers, int finger, float trigger, float share)
        {
            for (int joint = 0; joint < 3; joint++)
            {
                int i = fingers[finger * 3 + joint];
                if (i < 0 || i >= bones.Length || bones[i] == null)
                    continue;
                bones[i].Rotation = Quaternion.CreateFromAxisAngle(Vector3.Backward, CockpitHandPose.StickAngle(bones[i].Name, trigger) * share);
                bones[i].Translation = Vector3.Zero;
            }
            bones[fingers[finger * 3]].ComputeAbsoluteTransform(); // each joint is a child of the one before: this carries the rest
        }

        /// <summary>The finger's joints, the points between them, and its tip (as the fingertip is drawn, FingerPress) are all a finger's half-thickness off the surface.</summary>
        private static bool FingerClear(MyCharacterBone[] bones, int[] fingers, int finger, GripSurface surface, in Matrix modelToRest)
        {
            Vector3 previous = Vector3.Zero;
            bool havePrevious = false;
            for (int joint = 0; joint < 3; joint++)
            {
                int i = fingers[finger * 3 + joint];
                if (i < 0 || i >= bones.Length || bones[i] == null)
                    continue;
                Matrix at = bones[i].AbsoluteTransform;
                Vector3 point = at.Translation;
                if (havePrevious && (!Clear(surface, previous + (point - previous) * .5f, modelToRest) || !Clear(surface, point, modelToRest)))
                    return false;
                previous = point;
                havePrevious = true;
                if (joint == 2)
                {
                    Vector3 back = at.Right;
                    float length = back.Length();
                    if (length > 1e-6f)
                    {
                        Vector3 tip = point - back * ((float)Hands.FingerPress.FingertipReach / length);
                        if (!Clear(surface, point + (tip - point) * .5f, modelToRest) || !Clear(surface, tip, modelToRest))
                            return false;
                    }
                }
            }
            return true;
        }

        /// <summary>A point of a finger (the body's model space) is a finger's half-thickness off the surface.</summary>
        private static bool Clear(GripSurface surface, Vector3 model, in Matrix modelToRest) =>
            surface.Clearance(Vector3.Transform(model, modelToRest)) >= FingerSkin;

        /// <summary>
        /// How deep the palm is in the grip, by its knuckles (each finger's first joint) and the points between them and the palm
        /// bone: the push eased towards the depth (out while they are in, back while they are clear), and its way out from the line.
        /// </summary>
        private static void Palm(Hold hold, MyCharacterBone[] bones, int[] fingers, int palm, GripSurface surface, in Matrix restToModel, in Matrix modelToRest)
        {
            if (palm < 0 || palm >= bones.Length || bones[palm] == null)
                return;
            Vector3 palmAt = bones[palm].AbsoluteTransform.Translation;
            float least = float.PositiveInfinity;
            Vector3 knuckles = Vector3.Zero;
            int count = 0;
            for (int finger = 0; finger < 4 && finger * 3 < fingers.Length; finger++)
            {
                int i = fingers[finger * 3];
                if (i < 0 || i >= bones.Length || bones[i] == null)
                    continue;
                Vector3 knuckle = bones[i].AbsoluteTransform.Translation;
                knuckles += knuckle;
                count++;
                least = Math.Min(least, surface.Clearance(Vector3.Transform(knuckle, modelToRest)) - PalmSkin);
                least = Math.Min(least, surface.Clearance(Vector3.Transform((knuckle + palmAt) * .5f, modelToRest)) - PalmSkin);
            }
            if (count == 0 || float.IsInfinity(least))
                return;
            hold.Push = MathHelper.Clamp(hold.Push - least * PushRate, 0f, MostPush);
            Vector3 outward = Vector3.TransformNormal(surface.Outward(Vector3.Transform(knuckles / count, modelToRest)), restToModel);
            if (outward.LengthSquared() > 1e-8f)
                hold.Outward = Vector3.Normalize(outward);
        }
    }
}
