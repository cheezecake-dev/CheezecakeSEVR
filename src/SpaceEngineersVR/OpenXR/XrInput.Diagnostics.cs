// XrInput, diagnostics: logs the active interaction profiles and the sources behind each action.

using System;
using System.Collections.Generic;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;

namespace SpaceEngineersVR.OpenXR
{
    internal sealed unsafe partial class XrInput
    {
        /// <summary>
        /// The profile the runtime picked for each hand: the first read, then only when it changes. Also where a hand is found to be
        /// on a Vive wand (or no longer), which puts its controls in the Quest's layout (<see cref="WandLayout"/>).
        /// </summary>
        private void LogProfiles()
        {
            try
            {
                if (viveProfile == 0)
                    viveProfile = StringToPath(WandLayout.Profile);
                for (int h = 0; h < 2; h++)
                {
                    var state = new XrInteractionProfileState { Type = XrStructureType.InteractionProfileState };
                    Xr.Check(Xr.xrGetCurrentInteractionProfile(session, handPaths[h], ref state), "xrGetCurrentInteractionProfile", XrRuntime.Instance);
                    if (state.InteractionProfile == loggedProfile[h])
                        continue;
                    loggedProfile[h] = state.InteractionProfile;
                    Log.Info($"OpenXR {HandNames[h]} hand interaction profile: {PathName(state.InteractionProfile)}");
                    SetWand(h, state.InteractionProfile == viveProfile);
                    // The grip fit (the palm's turn and shift on the grip) is kept per profile.
                    GripFit.SetProfile((Hand)h, state.InteractionProfile == 0 ? null : PathName(state.InteractionProfile));
                }
            }
            catch (Exception e)
            {
                Log.Warn($"Could not read the interaction profiles: {e.Message}");
            }
        }

        /// <summary>
        /// Which controls the runtime bound to a real source, as of the last sync: an action that is not active has nothing
        /// behind it (the profile the runtime chose has no such control, or took none of the bindings). This is what says,
        /// on a headset session, that Menu, X, Y, A and B are really there, without anyone pressing them.
        /// </summary>
        private void LogSources()
        {
            try
            {
                var names = new List<string>();
                for (int h = 0; h < 2; h++)
                {
                    ulong hand = handPaths[h];
                    var list = new List<string>();
                    if (BooleanActive(menuAction, hand))
                        list.Add("menu");
                    if (BooleanActive(stickClickAction, hand))
                        list.Add("stick click");
                    if (FloatActive(triggerAction, hand))
                        list.Add("trigger");
                    if (FloatActive(squeezeAction, hand))
                        list.Add("squeeze");
                    if (Vector2Active(stickAction, hand))
                        list.Add("stick");
                    if (palmAction != 0 && PoseActive(palmAction, hand))
                        list.Add("palm pose");
                    if (BooleanActive(triggerTouchAction, hand))
                        list.Add("trigger touch");
                    if (BooleanActive(stickTouchAction, hand))
                        list.Add("stick touch");
                    if (BooleanActive(thumbrestTouchAction, hand))
                        list.Add("thumbrest touch");
                    names.Add($"{HandNames[h]}: {(list.Count > 0 ? string.Join(", ", list) : "nothing")}");
                }
                var buttons = new List<string>();
                if (BooleanActive(aAction, 0))
                    buttons.Add("A");
                if (BooleanActive(bAction, 0))
                    buttons.Add("B");
                if (BooleanActive(xAction, 0))
                    buttons.Add("X");
                if (BooleanActive(yAction, 0))
                    buttons.Add("Y");
                names.Add("face buttons: " + (buttons.Count > 0 ? string.Join(" ", buttons) : "none"));
                var touches = new List<string>();
                if (BooleanActive(aTouchAction, 0))
                    touches.Add("A");
                if (BooleanActive(bTouchAction, 0))
                    touches.Add("B");
                if (BooleanActive(xTouchAction, 0))
                    touches.Add("X");
                if (BooleanActive(yTouchAction, 0))
                    touches.Add("Y");
                names.Add("face touch: " + (touches.Count > 0 ? string.Join(" ", touches) : "none"));
                Log.Info("OpenXR bound sources (active after a sync): " + string.Join("; ", names));
            }
            catch (Exception e)
            {
                Log.Warn($"Could not read which controls are bound: {e.Message}");
            }
        }

        private bool BooleanActive(ulong action, ulong subactionPath)
        {
            var info = new XrActionStateGetInfo { Type = XrStructureType.ActionStateGetInfo, Action = action, SubactionPath = subactionPath };
            var state = new XrActionStateBoolean { Type = XrStructureType.ActionStateBoolean };
            Xr.Check(Xr.xrGetActionStateBoolean(session, ref info, ref state), "xrGetActionStateBoolean", XrRuntime.Instance);
            return state.IsActive != 0;
        }

        private bool PoseActive(ulong action, ulong subactionPath)
        {
            var info = new XrActionStateGetInfo { Type = XrStructureType.ActionStateGetInfo, Action = action, SubactionPath = subactionPath };
            var state = new XrActionStatePose { Type = XrStructureType.ActionStatePose };
            Xr.Check(Xr.xrGetActionStatePose(session, ref info, ref state), "xrGetActionStatePose", XrRuntime.Instance);
            return state.IsActive != 0;
        }

        private bool FloatActive(ulong action, ulong subactionPath)
        {
            var info = new XrActionStateGetInfo { Type = XrStructureType.ActionStateGetInfo, Action = action, SubactionPath = subactionPath };
            var state = new XrActionStateFloat { Type = XrStructureType.ActionStateFloat };
            Xr.Check(Xr.xrGetActionStateFloat(session, ref info, ref state), "xrGetActionStateFloat", XrRuntime.Instance);
            return state.IsActive != 0;
        }

        private bool Vector2Active(ulong action, ulong subactionPath)
        {
            var info = new XrActionStateGetInfo { Type = XrStructureType.ActionStateGetInfo, Action = action, SubactionPath = subactionPath };
            var state = new XrActionStateVector2f { Type = XrStructureType.ActionStateVector2f };
            Xr.Check(Xr.xrGetActionStateVector2f(session, ref info, ref state), "xrGetActionStateVector2f", XrRuntime.Instance);
            return state.IsActive != 0;
        }

        private static string PathName(ulong path)
        {
            if (path == 0)
                return "(none yet)";
            byte* buffer = stackalloc byte[256];
            return Xr.xrPathToString(XrRuntime.Instance, path, 256, out uint _, buffer) >= 0 ? Xr.ReadString(buffer, 256) : path.ToString();
        }
    }
}
