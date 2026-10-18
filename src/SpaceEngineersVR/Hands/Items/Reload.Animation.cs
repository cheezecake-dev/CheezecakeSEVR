// Adapted from ColbyJack1134's SpaceEngineersVR (Multiplayer/ReloadAnimation.cs), with his permission.
using System;
using System.Diagnostics;
using HarmonyLib;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using VRage.Game.Entity;
using VRageMath;
using VRageRender.Animations;
using VRageRender.Import;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// The game's own timed reload (R with rounds left, a gun drawn empty, <see cref="VRSettings.PhysicalReload"/> off), drawn
    /// on the gun's own art, after ColbyJack1134's. The game's reload animation carries the magazine to the astronaut's
    /// animated left hand (MyCharacter.RealignMagazineWithHand, between its MagazineUnplug and MagazinePlug events); in VR that
    /// hand is the tracked one, so the magazine would hang where the animation's hand is, away from the drawn hand. Here:
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>as it unplugs, the magazine drops out of the well (the model's dummy "subpart_magazine") along the well's down,
    /// <see cref="AnimDropDepth"/> in <see cref="AnimDropSeconds"/>, then is in the other hand: held where the animation holds it
    /// relative to the hand (sampled before the game's hand IK, MyCharacter.UpdateHandBones), on the hand as drawn;</item>
    /// <item>as it plugs, it slides from the hand back into the well in <see cref="AnimSeatSeconds"/>;</item>
    /// <item>with the other hand on the front grip (<see cref="TwoHand"/>), or left-handed (the animation's left hand is then
    /// the gun hand), no hand takes it: it is hidden while out and comes back in the well.</item>
    /// </list>
    /// The physical reload (Reload.Magazine.cs) draws its own magazines; this is only the game's timed one.
    /// </remarks>
    internal static partial class Reload
    {
        /// <summary>The drop out of the well as the magazine unplugs: this far (metres) in this long (seconds).</summary>
        private const double AnimDropDepth = 0.12, AnimDropSeconds = 0.12;

        /// <summary>The slide from the hand back into the well as it plugs (seconds).</summary>
        private const double AnimSeatSeconds = 0.16;

        /// <summary>The animation's hold, sampled this long ago at most (seconds), is still the hold.</summary>
        private const double SampleFresh = 0.2;

        /// <summary>The magazine's path between the well and the hand.</summary>
        private sealed class Motion
        {
            private bool detached, returning;
            private double changed;

            internal MatrixD Pose(bool unplugged, double now, MatrixD dock, MatrixD hand)
            {
                if (unplugged != detached)
                {
                    returning = !unplugged;
                    detached = unplugged;
                    changed = now;
                }
                if (detached)
                {
                    double elapsed = now - changed;
                    if (elapsed >= AnimDropSeconds)
                        return hand;
                    MatrixD drop = dock;
                    drop.Translation += dock.Down * (AnimDropDepth * elapsed / AnimDropSeconds);
                    return drop;
                }
                if (!returning)
                    return dock;
                float t = MathHelper.SmoothStep(0, 1, (float)Math.Min(1, (now - changed) / AnimSeatSeconds));
                MatrixD result = MatrixD.CreateFromQuaternion(Quaternion.Slerp(Quaternion.CreateFromRotationMatrix(hand), Quaternion.CreateFromRotationMatrix(dock), t));
                result.Translation = Vector3D.Lerp(hand.Translation, dock.Translation, t);
                if (t >= 1)
                    returning = false;
                return result;
            }
        }

        private static MyAutomaticRifleGun animGun;
        private static Motion animMotion;
        private static MatrixD animHold;
        private static long animAt;
        private static bool animSuppress, animHidden, animWasVisible;

        private static void PatchAnimation(Harmony harmony)
        {
            try
            {
                harmony.Patch(AccessTools.Method(typeof(MyCharacter), "UpdateHandBones"),
                    prefix: new HarmonyMethod(typeof(Reload), nameof(BeforeHandBones)));
            }
            catch (Exception e)
            {
                Log.Error(e, "Reload: the game's hand animation could not be hooked; a timed reload shows its magazine where the game's animated hand is");
            }
        }

        private static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;

        /// <summary>The game's timed reload of the local character's gun is under way, with the hands tracked.</summary>
        private static bool TimedReload(MyCharacter character, out MyAutomaticRifleGun rifle)
        {
            rifle = character?.CurrentWeapon as MyAutomaticRifleGun;
            return rifle != null && !rifle.Closed && rifle.IsReloading && hooked && !Off && VRInput.Active && !HandTools.Off
                   && HandTools.IsLocalOnFoot(character) && well == Well.In && Stopwatch.GetTimestamp() >= noRealignUntil;
        }

        /// <summary>MyCharacter.UpdateHandBones, before the game's hand IK: where the reload animation holds the magazine in the left hand.</summary>
        private static void BeforeHandBones(MyCharacter __instance)
        {
            if (__instance != MySession.Static?.LocalCharacter || !guard.On)
                return;
            try
            {
                if (!TimedReload(__instance, out MyAutomaticRifleGun rifle))
                {
                    ClearAnimation();
                    return;
                }
                MyCharacterBone left = __instance.AnimationController?.FindBone(__instance.Definition.LeftHandIKEndBone, out _);
                MyCharacterBone held = __instance.GetLeftHandBone();
                if (left == null || held == null)
                    return;
                if (!ReferenceEquals(animGun, rifle))
                {
                    ClearAnimation();
                    animGun = rifle;
                    animMotion = new Motion();
                    animSuppress = false;
                }
                // Only the magazine's place in the animated hand is sampled; the arm is never moved from it.
                animHold = (MatrixD)held.AbsoluteTransform * MatrixD.Invert(left.AbsoluteTransform);
                animAt = Stopwatch.GetTimestamp();
            }
            catch (Exception e)
            {
                ClearAnimation();
                guard.Failed(e, "Reload: the reload animation's magazine could not be read");
            }
        }

        /// <summary>Game thread, from <see cref="Tracking.HandIK"/> once both arms are solved: the timed reload's magazine on the gun or in the hand as drawn.</summary>
        internal static void ArmsSolved(MyCharacter character)
        {
            if (animGun == null || !guard.On)
                return;
            try
            {
                long now = Stopwatch.GetTimestamp();
                if (!TimedReload(character, out MyAutomaticRifleGun rifle) || !ReferenceEquals(rifle, animGun) || Seconds(now - animAt) > SampleFresh)
                {
                    ClearAnimation();
                    return;
                }
                MyCharacterBone left = character.AnimationController?.FindBone(character.Definition.LeftHandIKEndBone, out _);
                if (left == null || rifle.Model?.Dummies == null || !rifle.Model.Dummies.TryGetValue(MyAutomaticRifleGun.NAME_DUMMY_MAGAZINE, out MyModelDummy socket))
                    return;
                MatrixD dock = (MatrixD)Matrix.Normalize(socket.Matrix) * rifle.WorldMatrix;
                if (!animSuppress && (TwoHand.Holds(rifle) || ItemSpots.ToolHand == Hand.Left))
                {
                    animSuppress = true;
                    Log.Info($"Reload: the {Name(character)}'s timed reload: {(ItemSpots.ToolHand == Hand.Left ? "left-handed" : "the other hand holds the front grip")}, so its magazine is hidden while out");
                }
                AnimationHidden(animSuppress && character.ShouldPositionMagazine);
                if (animSuppress)
                {
                    rifle.SetMagazinePosition(dock);
                    return;
                }
                MatrixD hand = animHold * (MatrixD)left.AbsoluteTransform * character.WorldMatrix;
                MatrixD pose = animMotion.Pose(character.ShouldPositionMagazine, Seconds(now), dock, hand);
                if (pose.IsValid())
                    rifle.SetMagazinePosition(pose);
            }
            catch (Exception e)
            {
                ClearAnimation();
                guard.Failed(e, "Reload: the reload animation's magazine could not be placed");
            }
        }

        private static void AnimationHidden(bool hide)
        {
            if (animHidden == hide || animGun?.Subparts == null || !animGun.Subparts.TryGetValue(MyAutomaticRifleGun.NAME_SUBPART_MAGAZINE, out MyEntitySubpart magazine))
                return;
            if (hide)
                animWasVisible = magazine.Render.Visible;
            magazine.Render.Visible = !hide && animWasVisible;
            animHidden = hide;
        }

        /// <summary>The timed reload's magazine let go: shown, and back in the well unless the physical reload has it.</summary>
        private static void ClearAnimation()
        {
            if (animGun == null)
                return;
            if (!animGun.Closed)
            {
                AnimationHidden(false);
                if (well == Well.In)
                    animGun.ResetMagazinePosition();
            }
            animGun = null;
            animHidden = false;
        }
    }
}
