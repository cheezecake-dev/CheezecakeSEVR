// Adapted from ColbyJack1134's SpaceEngineersVR (Player/Controller.cs OpenXrGripToHandgrip, Player/Shared/CockpitHandPose.cs
// GripWrist, Player/Shared/ArmMath.cs PalmCorrection, Player/TrackedArms.cs WristForPalm, Player/Shared/ArmSkeleton.cs Apply's
// stick fingers, Player/TrackedDevice.cs Vibrate), with his permission.
// The shim between his cockpit code and our hands: his Controller (a SteamVR device with a grip pose) becomes our Hand and
// VRInput's OpenXR grip; his TrackedArms' palm offset is worked out from the pilot's bones the way his ArmSkeleton does.
using System;
using Sandbox.Game.Entities.Character;
using SpaceEngineersVR.Input;
using VRageMath;
using VRageRender.Animations;

namespace SpaceEngineersVR.Cockpits
{
    internal static class CockpitHand
    {
        /// <summary>
        /// RenderDebug ColbyGrip: true pre-multiplies his openxr_grip -> Touch handgrip conversion onto our grip (the
        /// frame his cockpit's palm, wrist and capture numbers were tuned in); false takes our OpenXR grip as it is.
        /// </summary>
        internal static bool Convert = true;

        /// <summary>RenderDebug ColbyGripX/Y/Z (metres, set in mm) and ColbyGripPitch (degrees): a further offset in the grip frame.</summary>
        internal static Vector3 Nudge;
        internal static float NudgePitch;

        // Touch handgrip relative to Touch openxr_grip, from the SteamVR Quest 2 render models (his comment).
        internal static Matrix OpenXrGripToHandgrip(bool left) =>
            Component(0,.003f,.097f,5.037f)*Matrix.Invert(Component(left ? .007f:-.007f,-.00182941f,.1019482f,20.6f));
        private static Matrix Component(float x,float y,float z,float pitch) => Matrix.CreateRotationX(MathHelper.ToRadians(pitch))*Matrix.CreateTranslation(x,y,z);

        /// <summary>His Controller.GripTracking: the hand grip in tracking space.</summary>
        internal static Matrix GripTracking(Hand hand)
        {
            Matrix grip=VRInput.Get(hand).Grip;
            Matrix offset=Convert ? OpenXrGripToHandgrip(hand==Hand.Left) : Matrix.Identity;
            if(Nudge!=Vector3.Zero || NudgePitch!=0) offset=Component(Nudge.X,Nudge.Y,Nudge.Z,NudgePitch)*offset;
            return offset*grip;
        }

        /// <summary>His CockpitHandPose.GripWrist.</summary>
        internal static Matrix GripWrist(Matrix grip) => Matrix.CreateTranslation(0,.02f,.09f)*grip;

        private static MyCharacter offsetsFor;
        private static Matrix? leftOffset,rightOffset;

        /// <summary>His TrackedArms.WristForPalm: the wrist that puts the suit's palm on <paramref name="palm"/>.</summary>
        internal static Matrix WristForPalm(MyCharacter pilot,Hand hand,Matrix palm)
        {
            if(offsetsFor!=pilot)
            {
                offsetsFor=pilot; leftOffset=rightOffset=null;
                var d=pilot?.Definition;
                if(d!=null)
                {
                    leftOffset=PalmOffset(pilot,d.LeftForearmBone,d.LeftHandIKEndBone,-1);
                    rightOffset=PalmOffset(pilot,d.RightForearmBone,d.RightHandIKEndBone,1);
                }
            }
            var offset=hand==Hand.Left ? leftOffset : rightOffset;
            return offset==null ? palm : Matrix.Invert(offset.Value)*palm;
        }

        private static Matrix? PalmOffset(MyCharacter character,string lowerName,string palmName,float side)
        {
            if(string.IsNullOrEmpty(lowerName) || string.IsNullOrEmpty(palmName) || character.AnimationController==null) return null;
            MyCharacterBone lower=character.AnimationController.FindBone(lowerName,out _),palm=character.AnimationController.FindBone(palmName,out _);
            if(lower==null || palm==null) return null;
            return PalmCorrection(palm.GetAbsoluteRigTransform(),lower.GetAbsoluteRigTransform(),side);
        }

        internal static Matrix PalmCorrection(Matrix palmRig,Matrix forearmRig,float side)
        {
            Vector3 forward=palmRig.Translation-forearmRig.Translation;
            forward.Normalize();
            Vector3 up=Math.Abs(Vector3.Dot(forward,Vector3.Up))<0.95f ? Vector3.Up : Vector3.Backward;
            Matrix grip=Matrix.CreateWorld(Vector3.Zero,forward,up);
            // Correct the suit palm axes in grip space: left +90 degrees, right -90.
            // Inward-pointing thumbs then point up with an upright controller.
            return palmRig.GetOrientation()*Matrix.Transpose(grip.GetOrientation())*Matrix.CreateRotationZ(-side*MathHelper.PiOver2)*HandInGrip;
        }
        // Palm frame in the controller grip frame: forward runs from forearm to palm, up through the thumb.
        internal static readonly Matrix HandInGrip=Matrix.CreateRotationX(MathHelper.ToRadians(-39f));

        /// <summary>His WeaponPose.Palm: a point just above the grip.</summary>
        internal static Vector3 Palm(Matrix grip) => Vector3.Transform(new Vector3(0,.02f,0),grip);

        /// <summary>
        /// His Controller.Vibrate(delay, duration, frequency, amplitude) on our haptics path (Hands.Haptics.Pulse: the "Haptic
        /// strength" setting and Haptics=0 apply). OpenXR leaves the frequency to the runtime, and he always passes no delay,
        /// so a pulse is his amplitude for his duration.
        /// </summary>
        internal static void Vibrate(Hand hand,float delay,float duration,float frequency,float amplitude) =>
            Hands.Haptics.Pulse(hand,amplitude,duration);

        /// <summary>
        /// His ArmSkeleton finger step for Fingers.Stick (each finger bone turned to CockpitHandPose.StickRotation, the index
        /// closing with the trigger), on HandIK's finger bones: Index, Middle, Ring, Little, Thumb, three joints each.
        /// </summary>
        internal static void StickFingers(MyCharacterBone[] bones,int[] fingers,float trigger)
        {
            for(int finger=0;finger<fingers.Length/3;finger++)
            {
                int first=fingers[finger*3];
                if(first<0 || first>=bones.Length) continue;
                for(int joint=0;joint<3;joint++)
                {
                    int i=fingers[finger*3+joint];
                    if(i<0 || i>=bones.Length || bones[i]==null) continue;
                    bones[i].Rotation=CockpitHandPose.StickRotation(bones[i].Name,trigger);
                    bones[i].Translation=Vector3.Zero;
                }
                bones[first].ComputeAbsoluteTransform();
            }
        }
    }
}
