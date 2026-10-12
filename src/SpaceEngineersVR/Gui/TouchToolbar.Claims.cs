// TouchToolbar: claiming a cockpit screen - which screens are wanted, which one is picked, taking and releasing it.
using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Sandbox.Game.Components;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GUI;
using Sandbox.Game.Localization;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using SpaceEngineersVR.Hands;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;
using VRage.Audio;
using VRage.Game;
using VRage.Game.GUI.TextPanel;
using VRageMath;
using VRageRender;
using VRageRender.Messages;

namespace SpaceEngineersVR.Gui
{
    internal static partial class TouchToolbar
    {
        /// <summary>
        /// The screens the toolbar goes on, and why (logged when it changes): those set to Touch Toolbar in the screen's
        /// settings; with none, TouchToolbarScreen's, else the central-most if the setting is on; else none. False while the
        /// central-most pick waits for the shoulder and the screens' glass.
        /// </summary>
        private static bool Wanted(MyCockpit cockpit, List<int> into, out string why)
        {
            int count = SurfaceCount(cockpit);
            for (int i = 0; i < count; i++)
            {
                MyTextPanelComponent panel = Panel(cockpit, i);
                if (panel != null && panel.ContentType == ContentType.SCRIPT && panel.Script == TouchToolbarScript.Id)
                    into.Add(i);
            }
            if (into.Count > 0)
                why = $"set to {TouchToolbarScript.DisplayName} in the screen's settings: screen{(into.Count > 1 ? "s" : "")} {string.Join(", ", into)}";
            else if (ScreenOverride >= 0 && ScreenOverride < count)
            {
                into.Add(ScreenOverride);
                why = $"no screen is set to {TouchToolbarScript.DisplayName}; TouchToolbarScreen={ScreenOverride}";
            }
            else if (!VRSettings.TouchToolbar)
                why = $"no screen is set to {TouchToolbarScript.DisplayName}, and the setting \"Touch toolbar on the cockpit screen\" is off";
            else
            {
                int area = PickScreen(cockpit, out string pick);
                if (area == NotYet)
                {
                    why = null;
                    return false;
                }
                into.Add(area);
                why = $"no screen is set to {TouchToolbarScript.DisplayName}, so the setting's fallback: screen {area}, {pick}";
            }
            if (why != lastCase)
            {
                lastCase = why;
                Log.Info($"Touch toolbar: {cockpit.BlockDefinition.Id.SubtypeName} {cockpit.EntityId}: {why}");
            }
            return true;
        }

        /// <summary>The cockpit this player pilots, working and with at least one screen; null otherwise.</summary>
        private static MyCockpit Seat()
        {
            MySession session = MySession.Static;
            if (session?.LocalCharacter == null || !(session.ControlledEntity is MyCockpit cockpit))
                return null;
            if (cockpit.Pilot != session.LocalCharacter || cockpit.Closed || !cockpit.IsFunctional || !cockpit.IsWorking)
                return null;
            return SurfaceCount(cockpit) > 0 ? cockpit : null;
        }

        private static readonly AccessTools.FieldRef<MyCockpit, MyMultiTextPanelComponent> multiPanel =
            AccessTools.FieldRefAccess<MyCockpit, MyMultiTextPanelComponent>("m_multiPanel");

        private static int SurfaceCount(MyCockpit cockpit) => multiPanel(cockpit)?.SurfaceCount ?? 0;

        private static MyTextPanelComponent Panel(MyCockpit cockpit, int area)
        {
            MyMultiTextPanelComponent multi = multiPanel(cockpit);
            return multi != null && area >= 0 && area < multi.SurfaceCount ? multi.GetPanelComponent(area) : null;
        }

        private static Claim Take(MyCockpit cockpit, int area, string why)
        {
            var render = cockpit.Render as MyRenderComponentScreenAreas;
            MyTextPanelComponent panel = Panel(cockpit, area);
            if (render == null || panel == null)
            {
                if (refusedFor != cockpit.EntityId)
                    Log.Info($"Touch toolbar: {cockpit.BlockDefinition.Id.SubtypeName} {cockpit.EntityId} has no screen to draw on (render {(render == null ? "none" : "ok")}, screen {area})");
                refusedFor = cockpit.EntityId;
                retryAt = frame + 120;
                return null;
            }

            var size = new Vector2I((int)panel.TextureSize.X, (int)panel.TextureSize.Y);
            var c = new Claim
            {
                Cockpit = cockpit,
                Area = area,
                Panel = panel,
                Render = render,
                Texture = $"SEVRTouchToolbar_{cockpit.EntityId}_{area}",
                TextureSize = size,
                Surface = panel.SurfaceSize,
                Middle = LocalMiddle(cockpit, area),
            };
            c.Aspect = c.Surface / new Vector2(size.X, size.Y);
            c.Layout = Arrange(c.Surface);
            MyRenderProxy.CreateGeneratedTexture(c.Texture, size.X, size.Y, MyGeneratedTextureType.RGBA, 1, null, generateMipmaps: true, immediatelyReady: false);
            Draw(c, true);
            c.Ids = (uint[])render.RenderObjectIDs?.Clone();
            c.AppliedAt = frame;
            Apply(c, c.Texture);

            string was = wanted.TryGetValue(render, out Dictionary<int, string> paths) && paths.TryGetValue(area, out string path) ? path ?? "(hidden)" : "(not seen)";
            var at = new StringBuilder();
            for (int i = 0; i < Slots; i++)
                at.Append(i == 0 ? "" : " ").Append(i + 1).Append('@').Append(Pixel(c, c.Layout.Slots[i].Center));
            Log.Info($"Touch toolbar: on {cockpit.BlockDefinition.Id.SubtypeName} {cockpit.EntityId} '{cockpit.CubeGrid.DisplayName}' screen {area} '{panel.Name}' " +
                     $"({why}; texture {size.X}x{size.Y}, surface {c.Surface.X:0}x{c.Surface.Y:0}, layout {c.Layout.Columns}x{c.Layout.Rows}, cell {c.Layout.Cell:0} px; " +
                     $"{(Far(c, out string reach) ? "laser" : "touch")}: {reach}); it showed {was}. " +
                     $"Touch pixels (sprite coordinates): {at}, left@{Pixel(c, c.Layout.Left.Center)}, right@{Pixel(c, c.Layout.Right.Center)}");
            return c;
        }

        private static void Release(Claim c, string why)
        {
            if (laserOn == c)
            {
                laserOn = null;
                laserDown = false;
            }
            laserHit = false;
            laserAt = null;
            FlushPending(true);
            string back = "(not seen)";
            if (!c.Cockpit.Closed)
            {
                if (wanted.TryGetValue(c.Render, out Dictionary<int, string> paths) && paths.TryGetValue(c.Area, out string path))
                {
                    Apply(c, path);
                    back = path ?? "(hidden)";
                }
                // The panel skips a texture it thinks it already set: make it send its own again on its next update.
                if (c.Panel != null)
                    previousTexture(c.Panel) = "\u0001SEVR";
            }
            MyRenderProxy.DestroyGeneratedTexture(c.Texture);
            Log.Info($"Touch toolbar: gave screen {c.Area} of {c.Cockpit.BlockDefinition.Id.SubtypeName} {c.Cockpit.EntityId} back ({why}); it shows {back} again");
        }

        private static bool Same(uint[] a, uint[] b)
        {
            if (a == null || b == null)
                return a == b;
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }

        // ---- Which screen: the override, else the central-most one (on the seat's centre line, which keeps it in reach). ----

        private const int NotYet = -2;

        /// <summary>Screens whose sideways offsets differ by less than this are equally central: the nearest one wins.</summary>
        private const float CentralTie = 0.03f;

        /// <summary>Each cockpit's pick and its reason; one made before every screen's glass was in lasts only while seated.</summary>
        private static readonly Dictionary<long, (int Area, string Why, bool Final, int Seat)> picked = new Dictionary<long, (int, string, bool, int)>();

        private static readonly AccessTools.FieldRef<MyShipController, Vector3> headLocal =
            AccessTools.FieldRefAccess<MyShipController, Vector3>("m_headLocalPosition");

        /// <summary>
        /// The surface to draw on, and why (for the log): the one whose picture's middle is least to the side of the
        /// seat's centre line (the seat's forward axis through its head), the nearest of equally central ones.
        /// <see cref="NotYet"/> while the shoulder or some screen's glass is still to come, for up to
        /// <see cref="PickWaitFrames"/> after sitting. A pick made with every screen's glass is kept for the cockpit; one
        /// made without, until the pilot leaves the seat.
        /// </summary>
        private static int PickScreen(MyCockpit cockpit, out string why)
        {
            int count = SurfaceCount(cockpit);
            if (picked.TryGetValue(cockpit.EntityId, out var known) && known.Area < count && (known.Final || known.Seat == seatedAt))
            {
                why = known.Why;
                return known.Area;
            }

            Hand hand = VRSettings.DominantHand;
            string side = hand.ToString().ToLowerInvariant();
            bool shoulderKnown = HandIK.TryShoulder(hand, MySession.Static?.LocalCharacter, out Vector3D shoulder);
            bool eyesKnown = TryEyes(out Vector3D eyes);
            var middles = new Vector3?[count];
            int found = Middles(cockpit, middles);
            if ((!shoulderKnown || found < count) && frame - seatedAt < SettleFrames + PickWaitFrames)
            {
                why = null;
                return NotYet;
            }

            // The seat's centre line, block space: through its head, along the block's forward (-Z); sideways is X.
            MatrixD toWorld = cockpit.PositionComp.WorldMatrixRef;
            Vector3 head = headLocal(cockpit);
            string headFrom = "the seat's head";
            if (head == Vector3.Zero && eyesKnown)
            {
                head = (Vector3)Vector3D.Transform(eyes, cockpit.PositionComp.WorldMatrixNormalizedInv);
                headFrom = "the eyes (the seat has no head position)";
            }

            var seen = new StringBuilder();
            int best = -1;
            float bestOffset = float.MaxValue, bestNear = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                seen.Append($" {i} '{Panel(cockpit, i)?.Name}'");
                if (!middles[i].HasValue)
                {
                    seen.Append(" no glass;");
                    continue;
                }
                Vector3 local = middles[i].Value;
                float sideways = local.X - head.X, offset = Math.Abs(sideways), near = Vector3.Distance(local, head);
                Vector3D middle = Vector3D.Transform((Vector3D)local, toWorld);
                double fromEyes = eyesKnown ? Vector3D.Distance(eyes, middle) : double.NaN;
                double fromShoulder = shoulderKnown ? Vector3D.Distance(shoulder, middle) : double.NaN;
                bool inReach = shoulderKnown ? fromShoulder <= ArmReachMeters : eyesKnown && fromEyes <= EyeReachMeters;
                seen.Append($" {offset:0.00} m {(sideways >= 0f ? "right" : "left")} of the centre line")
                    .Append(eyesKnown ? $", {fromEyes:0.00} m from the eyes" : ", eyes unknown")
                    .Append(shoulderKnown ? $", {fromShoulder:0.00} m from the {side} shoulder" : ", shoulder unknown")
                    .Append(inReach ? ", in reach;" : ", out of reach;");
                bool better = best < 0 || offset < bestOffset - CentralTie || (offset < bestOffset + CentralTie && near < bestNear);
                if (better)
                {
                    best = i;
                    bestOffset = offset;
                    bestNear = near;
                }
            }

            if (best < 0)
            {
                best = 0;
                why = "no screen's glass found, so screen 0";
            }
            else
            {
                var middle = Vector3D.Transform((Vector3D)middles[best].Value, toWorld);
                string reach = shoulderKnown
                    ? (Vector3D.Distance(shoulder, middle) <= ArmReachMeters ? "in the arm's reach, so touch" : "out of the arm's reach, so the laser and trigger")
                    : eyesKnown ? (Vector3D.Distance(eyes, middle) <= EyeReachMeters ? "in reach of the eyes (shoulder unknown), so touch" : "out of reach of the eyes (shoulder unknown), so the laser")
                    : "reach unknown";
                why = $"the central-most, {bestOffset:0.00} m off the centre line through {headFrom}" +
                      $"{(count > 1 ? $" (ties within {CentralTie * 100f:0} cm go to the nearest)" : "")}; {reach}";
            }
            picked[cockpit.EntityId] = (best, why, found == count, seatedAt);
            string seated = shoulderKnown && eyesKnown ? $"; the {side} shoulder is {Vector3D.Distance(shoulder, eyes):0.00} m from the eyes, seated" : "";
            Log.Info($"Touch toolbar: {cockpit.BlockDefinition.Id.SubtypeName} {cockpit.EntityId} screens ({found} of {count} with glass):{seen} picked {best}: {why}{seated}");
            return best;
        }

        /// <summary>Where the eyes are (world): the game's camera, which the headset moves.</summary>
        private static bool TryEyes(out Vector3D eyes)
        {
            eyes = MySector.MainCamera?.Position ?? Vector3D.Zero;
            return MySector.MainCamera != null;
        }
    }
}
