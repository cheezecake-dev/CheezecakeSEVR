using System;
using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Weapons;
using VRage.Game.Entity;
using VRage.Game.Models;
using VRage.ModAPI;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    internal static partial class Reload
    {
        /// <summary>
        /// The magazines drawn for a physical reload: the one in the other hand, the ones falling, and the well's own subpart
        /// hidden while it is out. Each is a bare entity with the magazine's model and nothing else: no physics, not saved, a
        /// preview (so never replicated), outside the game's pruning structure (so no ray or sensor finds it). Only this
        /// player sees them; others see the game's reload animation.
        /// </summary>
        private static class Shapes
        {
            private sealed class Falling
            {
                public MyEntity Entity;
                public Vector3D Velocity, SpinAxis;
                public long At, Last;
            }

            private static MyEntity hand;
            private static string handModel;
            private static BoundingBox handBox;
            private static readonly List<Falling> falling = new List<Falling>();
            private static MyEntitySubpart hiddenPart;
            private static bool hiddenParentVisible;
            private static bool near;

            /// <summary>Nothing drawn: no magazine in the hand and none falling.</summary>
            public static bool Idle => hand == null && falling.Count == 0;

            /// <summary>The in-hand magazine's model box, in its own space.</summary>
            public static BoundingBox HandBox => handBox;

            /// <summary>A model's box, in its own space, loading it if it is not yet; null when there is no such model.</summary>
            public static BoundingBox? ModelBox(string model)
            {
                MyModel m = MyModels.GetModelOnlyData(model);
                return m != null ? m.BoundingBox : (BoundingBox?)null;
            }

            // ---- The hand ----

            public static void TakeInHand(string model)
            {
                if (hand != null && (hand.Closed || handModel != model))
                    EndHand();
                if (hand == null)
                {
                    hand = Make(model);
                    handModel = model;
                    handBox = ModelBox(model) ?? new BoundingBox(new Vector3(-0.02f, -0.06f, -0.04f), new Vector3(0.02f, 0.06f, 0.04f));
                }
                hand.Render.Visible = false; // shown once placed, so it does not flash where it was made
            }

            public static void Hand(in MatrixD pose)
            {
                if (hand == null || hand.Closed)
                    return;
                MatrixD m = pose;
                hand.PositionComp.SetWorldMatrix(ref m);
                hand.Render.NearFlag = near;
                if (!hand.Render.Visible)
                    hand.Render.Visible = true;
            }

            /// <summary>The magazine in the hand put back in the pouch.</summary>
            public static void Stow() => EndHand();

            /// <summary>The magazine in the hand gone (seated, or put back).</summary>
            public static void EndHand()
            {
                if (hand != null && !hand.Closed)
                    hand.Close();
                hand = null;
                handModel = null;
            }

            /// <summary>The magazine in the hand let go of: it falls from where it is, at this speed.</summary>
            public static void DropFromHand(Vector3D velocity)
            {
                if (hand == null || hand.Closed || !hand.Render.Visible)
                {
                    EndHand();
                    return;
                }
                Fall(hand, velocity, hand.WorldMatrix.Right);
                hand = null;
                handModel = null;
            }

            // ---- Falling ----

            /// <summary>A magazine out of the well: it starts at the well's place with the character's speed plus <paramref name="push"/>.</summary>
            public static void Drop(string model, in MatrixD pose, MyCharacter character, Vector3D push, Vector3D spinAxis)
            {
                if (string.IsNullOrEmpty(model))
                    return;
                MyEntity e = Make(model);
                MatrixD m = pose;
                e.PositionComp.SetWorldMatrix(ref m);
                Vector3D velocity = (character?.Physics != null ? (Vector3D)character.Physics.LinearVelocity : Vector3D.Zero) + push;
                Fall(e, velocity, spinAxis);
            }

            private static void Fall(MyEntity e, Vector3D velocity, Vector3D spinAxis)
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                falling.Add(new Falling { Entity = e, Velocity = velocity, SpinAxis = Vector3D.Normalize(spinAxis), At = now, Last = now });
            }

            /// <summary>Each frame: the falling magazines move under the gravity where they are, turn, fade and go.</summary>
            public static void StepDrops(long now, bool nearFlag)
            {
                near = nearFlag;
                for (int i = falling.Count - 1; i >= 0; i--)
                {
                    Falling f = falling[i];
                    double age = (now - f.At) / (double)Second;
                    if (f.Entity.Closed || age >= DropSeconds)
                    {
                        if (!f.Entity.Closed)
                            f.Entity.Close();
                        falling.RemoveAt(i);
                        continue;
                    }
                    double dt = Math.Min(0.1, (now - f.Last) / (double)Second);
                    f.Last = now;
                    MatrixD m = f.Entity.WorldMatrix;
                    f.Velocity += (Vector3D)MyGravityProviderSystem.CalculateTotalGravityInPoint(m.Translation) * dt;
                    Vector3D at = m.Translation + f.Velocity * dt;
                    m.Translation = Vector3D.Zero;
                    m *= MatrixD.CreateFromAxisAngle(f.SpinAxis, DropSpin * dt);
                    m.Translation = at;
                    f.Entity.PositionComp.SetWorldMatrix(ref m);
                    f.Entity.Render.NearFlag = near;
                    float fade = (float)MathHelper.Clamp((age - (DropSeconds - DropFade)) / DropFade, 0.0, 1.0);
                    if (Math.Abs(f.Entity.Render.Transparency - fade) > 0.01f)
                    {
                        f.Entity.Render.Transparency = fade;
                        f.Entity.Render.UpdateTransparency();
                    }
                }
            }

            // ---- The well's own magazine ----

            /// <summary>The well's magazine subpart hidden; called each frame it is out, so a weapon shown again (which shows its subparts) does not bring it back.</summary>
            public static void HideWell(MyAutomaticRifleGun rifle)
            {
                if (rifle?.Subparts == null || !rifle.Subparts.TryGetValue(MyAutomaticRifleGun.NAME_SUBPART_MAGAZINE, out MyEntitySubpart part))
                    return;
                bool parentVisible = rifle.Render.Visible;
                if (!ReferenceEquals(hiddenPart, part))
                {
                    if (hiddenPart != null && !hiddenPart.Closed)
                        hiddenPart.Render.Visible = true;
                    hiddenPart = part;
                    hiddenParentVisible = parentVisible;
                    part.Render.Visible = false;
                    return;
                }
                if (parentVisible && !hiddenParentVisible)
                {
                    // The weapon shown again showed its children's render objects whatever their own flag.
                    part.Render.Visible = true;
                    part.Render.Visible = false;
                }
                hiddenParentVisible = parentVisible;
            }

            public static void ShowWell(MyAutomaticRifleGun rifle)
            {
                if (hiddenPart != null && !hiddenPart.Closed)
                    hiddenPart.Render.Visible = true;
                hiddenPart = null;
            }

            // ---- Making one ----

            private static MyEntity Make(string model)
            {
                MyEntity e = new MyEntity();
                e.Init(null, model, null, null);
                e.Save = false;
                e.SyncFlag = false;
                e.IsPreview = true;
                e.Render.CastShadows = false;
                e.Flags |= EntityFlags.IsNotGamePrunningStructureObject;
                e.Render.NearFlag = near;
                MyEntities.Add(e);
                return e;
            }
        }
    }
}
