using System;
using System.Collections.Generic;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GUI;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;
using VRage.Audio;
using VRage.Game;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    // Turning the block being placed, and turning a paste, by the hand's twist.
    internal static partial class BuildMode
    {
        // ---- Turning the block ----

        private static void Turn(MyCubeBuilder builder)
        {
            if (!Modifier)
            {
                EndTurn("left stick click let go");
                return;
            }
            if (turning && pasteTurn)
                EndTurn("a block in hand took over from the paste");
            MyCharacter character = MySession.Static?.LocalCharacter;
            if (character == null || !HandWorld.TryGetNow(character, Hand.Right, out MatrixD grip, out _, out _))
                return;
            MatrixD hand = grip.GetOrientation();
            if (!turning)
            {
                BeginTurn(builder, character, hand);
                return;
            }
            if (builder.CurrentBlockDefinition != turnBlock && !KeepTurn(builder))
                return;
            if (builder.CurrentBlockDefinition.Rotation == MyBlockRotation.None)
                return;

            // The stick: a 90 degree step per push left or right, about the body's up (ModifierStick).
            if (stickTurn != 0)
            {
                StickStep(builder, hand, character.WorldMatrix.Up, stickTurn > 0 ? -MathHelper.PiOver2 : MathHelper.PiOver2, stickTurn > 0 ? "right" : "left");
                return;
            }

            // The hand: its turn since the click went down, in the grid's frame (row vectors: hand = handStart * delta).
            MatrixD delta = MatrixD.Transpose(handStart) * hand;
            Matrix inFrame = (Matrix)(frame * delta * MatrixD.Transpose(frame));
            int best = Nearest(inFrame, dynamicTurn ? null : builder.CurrentBlockDefinition, false, out double bestAngle);
            if (best < 0 || best == snap)
                return;
            double shownAngle = Angle(Turns[snap], inFrame);
            if (bestAngle > shownAngle - SnapHysteresisDegrees)
                return;
            int was = snap;
            snap = best;
            if (Apply(builder, dynamicTurn ? Turns[best] * (Matrix)frame : localStart * Turns[best], out string facing))
            {
                steps++;
                Haptics.Pulse(Hand.Right, TickAmplitude, TickSeconds);
                Log.Info($"Build: turned by hand to {Describe(Turns[best])} from the click's start (was {Describe(Turns[was])}; hand {shownAngle:F0} deg from the old step, {bestAngle:F0} from the new); block {facing}");
            }
        }

        private static void BeginTurn(MyCubeBuilder builder, MyCharacter character, MatrixD hand)
        {
            MyCubeBuilderGizmo.MyGizmoSpaceProperties space = Gizmo(builder).SpaceDefault;
            dynamicTurn = builder.DynamicMode;
            pasteTurn = false;
            turnBlock = builder.CurrentBlockDefinition;
            handStart = hand;
            snap = Identity;
            steps = 0;
            Matrix local = space.m_localMatrixAdd.GetOrientation();
            if (dynamicTurn)
            {
                frame = space.m_worldMatrixAdd.GetOrientation();
                localStart = Matrix.Identity;
            }
            else
            {
                MyCubeGrid grid = CurrentGrid(builder);
                localStart = local;
                // On a grid its orientation; elsewhere the frame the block's orientation is in (world = local * frame).
                frame = grid != null ? grid.WorldMatrix.GetOrientation() : MatrixD.Transpose(local) * space.m_worldMatrixAdd.GetOrientation();
            }
            FreezeRay(character);
            turning = true;
            Log.Info($"Build: turn started ({(dynamicTurn ? "off a grid, the block's own axes" : CurrentGrid(builder) != null ? "on a grid" : "on the local grid")}; ray held{(rayFrozen ? string.Empty : " not available")}; block {builder.CurrentBlockDefinition.Id.SubtypeName}, rotation {builder.CurrentBlockDefinition.Rotation})");
        }

        /// <summary>Holds the builder's ray where the hand points now, relative to the body, for as long as the hand turns the block (<see cref="TryFrozenRay"/>).</summary>
        private static void FreezeRay(MyCharacter character)
        {
            rayFrozen = HandTools.TryGetRay(out Vector3D origin, out Vector3D direction);
            if (!rayFrozen)
                return;
            MatrixD toBody = MatrixD.Invert(character.WorldMatrix);
            rayOrigin = Vector3D.Transform(origin, toBody);
            rayDirection = Vector3D.TransformNormal(direction, toBody);
        }

        private static void EndTurn(string why)
        {
            if (!turning)
                return;
            turning = false;
            rayFrozen = false;
            Log.Info($"{(pasteTurn ? "Paste" : "Build")}: turn ended ({why}) after {steps} step{(steps == 1 ? string.Empty : "s")}");
        }

        /// <summary>
        /// The block in hand changed while the hand turns it (a stage step, the size switch): the game gave the new block the
        /// orientation it had last, or none (MyCubeBuilder.UpdateCubeBlockStageDefinition). A block of the same size is turned
        /// back to the way the hand holds it, so a stage step does not undo the turn; otherwise, or if the new block cannot face
        /// that way, the turn starts again from what the game shows. False when the turn ended.
        /// </summary>
        private static bool KeepTurn(MyCubeBuilder builder)
        {
            MyCubeBlockDefinition was = turnBlock, now = builder.CurrentBlockDefinition;
            turnBlock = now;
            if (was == null || now == null || was.CubeSize != now.CubeSize)
            {
                EndTurn($"block changed size, {Name(was)} -> {Name(now)}");
                return false;
            }
            MyCubeBuilderGizmo gizmo = Gizmo(builder);
            MyCubeBuilderGizmo.MyGizmoSpaceProperties space = gizmo.SpaceDefault;
            if (dynamicTurn)
            {
                // Off a grid the block's own world orientation is the turn's; the stage step set only the grid one.
                MatrixD world = Turns[snap] * (Matrix)frame;
                world.Translation = space.m_worldMatrixAdd.Translation;
                space.m_worldMatrixAdd = world;
            }
            else
            {
                Matrix held = localStart * Turns[snap];
                if (!MyCubeBuilder.CheckValidBlockRotation(held, now.Direction, now.Rotation))
                {
                    EndTurn($"{Name(now)} cannot face the way the hand held {Name(was)}");
                    return false;
                }
                MatrixD target = held;
                target.Translation = space.m_localMatrixAdd.Translation;
                CustomRotation(builder);
                gizmo.RotateAxis(ref target);
            }
            Log.Info($"Build: the turn keeps {Name(now)} facing as it held {Name(was)} ({Describe(Turns[snap])} from the click's start)");
            return true;
        }

        private static string Name(MyCubeBlockDefinition definition) => definition?.Id.SubtypeName ?? "none";

        /// <summary>A stick step: the block turned 90 degrees about the world axis nearest the grid axis, then the hand's turn starts again from here.</summary>
        private static void StickStep(MyCubeBuilder builder, MatrixD hand, Vector3D worldAxis, float angle, string direction)
        {
            Matrix now = dynamicTurn ? Turns[snap] * (Matrix)frame : localStart * Turns[snap];
            Matrix target;
            if (dynamicTurn)
                target = now * Matrix.CreateFromAxisAngle((Vector3)worldAxis, angle);
            else
            {
                Vector3 inFrame = Vector3.TransformNormal((Vector3)worldAxis, (Matrix)MatrixD.Transpose(frame));
                target = now * Matrix.CreateFromAxisAngle(GridAxis(inFrame), angle);
            }
            target = Round(target);
            if (!dynamicTurn && !MyCubeBuilder.CheckValidBlockRotation(target, builder.CurrentBlockDefinition.Direction, builder.CurrentBlockDefinition.Rotation))
            {
                Log.Info($"Build: stick turn {direction} refused, the block cannot face that way");
                return;
            }
            if (!Apply(builder, target, out string facing))
                return;
            steps++;
            Haptics.Pulse(Hand.Right, TickAmplitude, TickSeconds);
            Log.Info($"Build: turned with the stick ({direction}) 90 deg; block {facing}");
            // The hand's turn counts from here.
            if (dynamicTurn)
                frame = target;
            else
                localStart = target;
            handStart = hand;
            snap = Identity;
        }

        /// <summary>Sets the block's orientation as the game's rotate keys do, with the game's turn animation and sound. False when it did not change.</summary>
        private static bool Apply(MyCubeBuilder builder, Matrix orientation, out string facing)
        {
            MyCubeBuilderGizmo gizmo = Gizmo(builder);
            MyCubeBuilderGizmo.MyGizmoSpaceProperties space = gizmo.SpaceDefault;
            facing = string.Empty;
            if (dynamicTurn)
            {
                MatrixD world = space.m_worldMatrixAdd;
                MatrixD turned = orientation;
                turned.Translation = world.Translation;
                space.m_worldMatrixAdd = turned;
                facing = $"forward {Short(turned.Forward)} up {Short(turned.Up)} (world)";
                MyGuiAudio.PlaySound(MyGuiSounds.HudRotateBlock);
                return true;
            }
            Matrix before = space.m_localMatrixAdd;
            // The game's turn animation (MyCubeBuilder.CalculateBlockRotation sets the lock; the gizmo update slerps from
            // m_animationLastMatrix over ten frames). Started from what is shown now, so a step during a step does not jump.
            ref bool animating = ref animationLockRef(builder);
            space.m_animationLastMatrix = animating && space.m_animationProgress < 1f
                ? MatrixD.Slerp(space.m_animationLastMatrix, space.m_localMatrixAdd, space.m_animationProgress)
                : (MatrixD)space.m_localMatrixAdd;
            space.m_animationProgress = 0f;
            animating = true;
            MatrixD target = orientation;
            target.Translation = before.Translation;
            CustomRotation(builder);
            gizmo.RotateAxis(ref target);
            Matrix after = space.m_localMatrixAdd;
            facing = $"forward {Short(after.Forward)} up {Short(after.Up)} (grid)";
            if (Vector3.DistanceSquared(after.Forward, before.Forward) < 1e-4f && Vector3.DistanceSquared(after.Up, before.Up) < 1e-4f)
                return false;
            MyGuiAudio.PlaySound(MyGuiSounds.HudRotateBlock);
            return true;
        }

        /// <summary>
        /// The 24 turns nearest the hand's first; the best one allowed, and its angle from the hand's, degrees. Allowed: the
        /// block on a grid (<paramref name="block"/>, null for none) can face that way; with <paramref name="upOnly"/>, the
        /// turn keeps up where it is (a paste with one free axis).
        /// </summary>
        private static int Nearest(Matrix inFrame, MyCubeBlockDefinition block, bool upOnly, out double angle)
        {
            int best = -1;
            angle = double.MaxValue;
            for (int i = 0; i < Turns.Length; i++)
            {
                double a = Angle(Turns[i], inFrame);
                if (a >= angle)
                    continue;
                if (block != null && !MyCubeBuilder.CheckValidBlockRotation(localStart * Turns[i], block.Direction, block.Rotation))
                    continue;
                if (upOnly && Turns[i].Up != Vector3.Up)
                    continue;
                angle = a;
                best = i;
            }
            return best;
        }

        /// <summary>The angle between two orientations, degrees.</summary>
        internal static double Angle(Matrix a, Matrix b)
        {
            // trace(a^T b) = 1 + 2 cos(angle)
            Matrix d = Matrix.Transpose(a) * b;
            double cos = MathHelper.Clamp((d.M11 + d.M22 + d.M33 - 1f) * 0.5f, -1f, 1f);
            return MathHelper.ToDegrees(Math.Acos(cos));
        }

        /// <summary>The 24 orientations that keep a block's axes on a grid's: every signed permutation with determinant +1.</summary>
        internal static Matrix[] AllTurns()
        {
            var list = new List<Matrix>(24);
            Vector3[] axes = { Vector3.Right, Vector3.Left, Vector3.Up, Vector3.Down, Vector3.Backward, Vector3.Forward };
            foreach (Vector3 right in axes)
                foreach (Vector3 up in axes)
                {
                    if (Math.Abs(Vector3.Dot(right, up)) > 0.5f)
                        continue;
                    Matrix m = Matrix.Identity;
                    m.Right = right;
                    m.Up = up;
                    m.Backward = Vector3.Cross(right, up);
                    list.Add(m);
                }
            return list.ToArray();
        }

        /// <summary>The grid axis nearest a direction in the grid's frame.</summary>
        private static Vector3 GridAxis(Vector3 v)
        {
            Vector3 a = Vector3.Abs(v);
            if (a.X >= a.Y && a.X >= a.Z)
                return new Vector3(Math.Sign(v.X), 0f, 0f);
            if (a.Y >= a.Z)
                return new Vector3(0f, Math.Sign(v.Y), 0f);
            return new Vector3(0f, 0f, Math.Sign(v.Z));
        }

        private static Matrix Round(Matrix m)
        {
            Matrix r = Matrix.Identity;
            r.Right = Vector3I.Round(m.Right);
            r.Up = Vector3I.Round(m.Up);
            r.Backward = Vector3I.Round(m.Backward);
            return r;
        }

        /// <summary>A turn in words: its angle and the grid axis it is about.</summary>
        internal static string Describe(Matrix turn)
        {
            Quaternion q = Quaternion.CreateFromRotationMatrix(turn);
            double angle = MathHelper.ToDegrees(2.0 * Math.Acos(MathHelper.Clamp(Math.Abs(q.W), 0f, 1f)));
            if (angle < 1.0)
                return "no turn";
            Vector3 axis = new Vector3(q.X, q.Y, q.Z) * Math.Sign(q.W == 0f ? 1f : q.W);
            return $"{angle:F0} deg about {Short(Vector3.Normalize(axis))}";
        }

        private static string Short(Vector3D v)
        {
            string Axis(double c, string plus, string minus) => c > 0.5 ? plus : c < -0.5 ? minus : null;
            string s = Axis(v.X, "+X", "-X") ?? Axis(v.Y, "+Y", "-Y") ?? Axis(v.Z, "+Z", "-Z");
            return s ?? $"({v.X:F2},{v.Y:F2},{v.Z:F2})";
        }

        /// <summary>The builder's (or the paste's) ray while the hand turns the block or the paste: where it was when the left stick click went down, held relative to the body.</summary>
        internal static bool TryFrozenRay(MyCharacter character, out Vector3D origin, out Vector3D direction)
        {
            origin = direction = Vector3D.Zero;
            if (!turning || !rayFrozen || character == null)
                return false;
            MatrixD body = character.WorldMatrix;
            origin = Vector3D.Transform(rayOrigin, body);
            direction = Vector3D.Normalize(Vector3D.TransformNormal(rayDirection, body));
            return true;
        }

        // ---- Turning a paste ----

        /// <summary>
        /// The paste's turn, as a block's off a grid: the pasted grids' orientation (the clipboard's paste directions, which
        /// the game's rotate keys turn in MyGridClipboard.RotateAroundAxis) is the one it had when the click went down, turned by
        /// the right hand's turn since then in 90 degree steps about the paste's own axes, and by the right stick a step at a
        /// time. Snapped to a grid the game lines the paste up with that grid's axes every frame (TrySnapToSurface), which
        /// these steps keep; snapped with one free axis (OneAxisRotationMode, where the game's keys turn it about its up only)
        /// only those turns are made. The ray is held as for a block, so twisting the hand does not slide the paste.
        /// </summary>
        private static void PasteTurn(MyGridClipboard grids)
        {
            if (turning && !pasteTurn)
                EndTurn("a paste took over from the block");
            if (!Modifier || grids == null || !grids.IsActive || grids.PreviewGrids.Count == 0)
            {
                EndTurn(Modifier ? "the paste has no grids to turn" : "left stick click let go");
                return;
            }
            MyCharacter character = MySession.Static?.LocalCharacter;
            if (character == null || !HandWorld.TryGetNow(character, Hand.Right, out MatrixD grip, out _, out _))
                return;
            MatrixD hand = grip.GetOrientation();
            if (!turning)
            {
                BeginPasteTurn(grids, character, hand);
                return;
            }
            bool upOnly = grids.OneAxisRotationMode;

            // The stick: a 90 degree step per push left or right, about the paste's own axis nearest the body's up (ModifierStick).
            if (stickTurn != 0)
            {
                PasteStickStep(grids, hand, character.WorldMatrix.Up, stickTurn > 0 ? -MathHelper.PiOver2 : MathHelper.PiOver2, stickTurn > 0 ? "right" : "left", upOnly);
                return;
            }

            // The hand: its turn since the click went down, in the paste's frame (row vectors: hand = handStart * delta).
            MatrixD delta = MatrixD.Transpose(handStart) * hand;
            Matrix inFrame = (Matrix)(frame * delta * MatrixD.Transpose(frame));
            int best = Nearest(inFrame, null, upOnly, out double bestAngle);
            if (best < 0 || best == snap)
                return;
            double shownAngle = Angle(Turns[snap], inFrame);
            if (bestAngle > shownAngle - SnapHysteresisDegrees)
                return;
            int was = snap;
            snap = best;
            ApplyPaste(grids, Turns[best] * (Matrix)frame, out string facing);
            steps++;
            Haptics.Pulse(Hand.Right, TickAmplitude, TickSeconds);
            Log.Info($"Paste: turned by hand to {Describe(Turns[best])} from the click's start (was {Describe(Turns[was])}; hand {shownAngle:F0} deg from the old step, {bestAngle:F0} from the new); paste {facing}");
        }

        private static void BeginPasteTurn(MyGridClipboard grids, MyCharacter character, MatrixD hand)
        {
            PasteRefs();
            // A turn the game's keys left part-way (its orientation angle) is folded in first, as the game does before each of its own.
            applyAngle.Invoke(grids, null);
            Vector3 forward = pasteForwardRef(grids), up = pasteUpRef(grids);
            frame = Matrix.CreateWorld(Vector3.Zero, Vector3.Normalize(forward), Vector3.Normalize(up));
            dynamicTurn = false;
            pasteTurn = true;
            turnBlock = null;
            handStart = hand;
            snap = Identity;
            steps = 0;
            FreezeRay(character);
            turning = true;
            Log.Info($"Paste: turn started ({(grids.IsSnapped ? "snapped" : "not snapped")}{(grids.OneAxisRotationMode ? ", one free axis: about its up only" : string.Empty)}; ray held{(rayFrozen ? string.Empty : " not available")}; paste forward {Short(frame.Forward)} up {Short(frame.Up)} (world))");
        }

        /// <summary>A stick step: the paste turned 90 degrees about its own axis nearest the world axis, then the hand's turn starts again from here.</summary>
        private static void PasteStickStep(MyGridClipboard grids, MatrixD hand, Vector3D worldAxis, float angle, string direction, bool upOnly)
        {
            Matrix now = Turns[snap] * (Matrix)frame;
            Vector3 local = GridAxis(Vector3.TransformNormal((Vector3)worldAxis, Matrix.Transpose(now)));
            if (upOnly && Math.Abs(local.Y) < 0.5f)
            {
                Log.Info($"Paste: stick turn {direction} refused, the paste is snapped with one free axis (it turns about its up only)");
                return;
            }
            // Row vectors: a turn in the paste's own frame goes before its orientation.
            Matrix target = Round(Matrix.CreateFromAxisAngle(local, angle)) * now;
            ApplyPaste(grids, target, out string facing);
            steps++;
            Haptics.Pulse(Hand.Right, TickAmplitude, TickSeconds);
            Log.Info($"Paste: turned with the stick ({direction}) 90 deg; paste {facing}");
            // The hand's turn counts from here.
            frame = target;
            handStart = hand;
            snap = Identity;
        }

        /// <summary>Sets the paste's orientation where the game's rotate keys set it (its paste directions), with the game's turn sound.</summary>
        private static void ApplyPaste(MyGridClipboard grids, Matrix orientation, out string facing)
        {
            pasteForwardRef(grids) = orientation.Forward;
            pasteUpRef(grids) = orientation.Up;
            pasteAngleRef(grids) = 0f;
            facing = $"forward {Short(orientation.Forward)} up {Short(orientation.Up)} (world)";
            MyGuiAudio.PlaySound(MyGuiSounds.HudRotateBlock);
        }
    }
}
