// HandCrosshair: on foot in VR the crosshair marks where the gun in the hand points, and is not drawn without one.
using System;
using HarmonyLib;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Gui;
using Sandbox.Game.Weapons;
using Sandbox.Game.Weapons.Guns;
using SpaceEngineersVR.Hands;
using SpaceEngineersVR.Input;
using VRage.ModAPI;
using VRageMath;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The game draws its crosshair in the middle of the screen, where the head looks: in a flat game that is where the
    /// tool works and the gun shoots, but in VR they point where the hand points. So on foot, with the game's crosshair on
    /// (its own Crosshair option), it is drawn where the held gun's aim meets the world: the same ray the game's
    /// shot is given (<see cref="ToolAim"/>), cast against what the game's own crosshair target is
    /// (MyHudCrosshair.GetTarget's default collision layer), and placed on the HUD panel by the game's own projection
    /// (MyHudCrosshair.GetProjectedVector, which <see cref="HudCamera"/> turns into the panel's). With nothing in the hand
    /// that aims (the empty hand, a block to place: the hand's ray and dot show those, <see cref="HandRay"/>), or an aim
    /// that leaves the panel, it is not drawn. Nor with a welder, grinder or hand drill: those work at their tip, within a
    /// couple of metres, so a crosshair on the far wall marked the wrong place; their laser and dot show it (<see cref="HandRay"/>). A seat, a camera, a turret and the spectator keep the game's own crosshair
    /// (a ship's is already placed on its nose by the game).
    /// </summary>
    /// <remarks>
    /// Fair to a flat player: it shows only where the item points, as theirs does, and only what their crosshair would
    /// meet. The game's own position is put back after the draw, so its logic that reads it is untouched.
    /// </remarks>
    internal static class HandCrosshair
    {
        /// <summary>Debug: the game's crosshair in the middle of the panel (<see cref="RenderDebug"/> HandCrosshair=0).</summary>
        public static bool Off { get; set; }

        /// <summary>How far the aim is cast for what it meets (metres); past that the crosshair is on the aim's far end.</summary>
        private const double CastReach = 300.0;

        private static readonly AccessTools.FieldRef<MyHudCrosshair, Vector2> position =
            AccessTools.FieldRefAccess<MyHudCrosshair, Vector2>("m_position");

        private static readonly FeatureGuard guard = new FeatureGuard(nameof(HandCrosshair));

        private static bool restore;
        private static Vector2 gamePosition;
        private static int lastState = -1;

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(MyHudCrosshair), nameof(MyHudCrosshair.Draw)),
                prefix: new HarmonyMethod(typeof(HandCrosshair), nameof(BeforeDraw)),
                postfix: new HarmonyMethod(typeof(HandCrosshair), nameof(AfterDraw)));
        }

        private static bool BeforeDraw(MyHudCrosshair __instance)
        {
            restore = false;
            if (Off || !guard.On || !StereoPatches.VRPicture || !VRInput.Layout)
                return true;
            try
            {
                MyCharacter character = Sandbox.Game.World.MySession.Static?.LocalCharacter;
                if (!HandTools.IsLocalOnFoot(character))
                    return Note(0, true);
                if (!HandRay.HoldsToolOrWeapon())
                    return Note(1, false);
                if (!ToolAim.TryGet(out character, out IMyEntity weapon, out Vector3D origin, out Vector3D forward))
                    return Note(2, true);
                // A welder, grinder or drill works at its tip, within its reach, not where a far cast would land: a crosshair
                // there only misleads. Its laser and dot show where it acts (HandRay).
                if (weapon is MyEngineerToolBase || weapon is MyHandDrill)
                    return Note(5, false);
                Vector3D end = origin + forward * CastReach;
                if (ToolAim.FirstHit(origin, end, character, weapon, MyPhysics.CollisionLayers.DefaultCollisionLayer, out double along))
                    end = origin + forward * along;
                Vector2 target = Vector2.Zero;
                if (!MyHudCrosshair.GetProjectedVector(end, ref target) || target.X < 0f || target.X > 1f
                    || target.Y < 0f || target.Y > Sandbox.Graphics.MyGuiManager.GetHudSize().Y)
                    return Note(3, false);
                gamePosition = position(__instance);
                restore = true;
                position(__instance) = target;
                return Note(4, true);
            }
            catch (Exception e)
            {
                guard.Failed(e, "Crosshair on the hand's aim failed; the game's crosshair is drawn as it is");
                return true;
            }
        }

        private static void AfterDraw(MyHudCrosshair __instance)
        {
            if (!restore)
                return;
            restore = false;
            position(__instance) = gamePosition;
        }

        private static readonly string[] StateNames =
        {
            "not on foot: the game's own crosshair",
            "nothing in the hand that aims: not drawn",
            "the item is not in the tracked hand: the game's own crosshair",
            "the hand's aim is off the HUD: not drawn",
            "on the held item's aim",
            "a hand tool, which works at its tip: not drawn",
        };

        /// <summary>Logs each change of what the crosshair does; passes <paramref name="draw"/> on.</summary>
        private static bool Note(int state, bool draw)
        {
            if (state != lastState)
            {
                lastState = state;
                Log.Info("Crosshair: " + StateNames[state]);
            }
            return draw;
        }
    }
}
