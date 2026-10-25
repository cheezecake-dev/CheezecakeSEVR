using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using SpaceEngineers.Game.World;
using Sandbox.Graphics.GUI;
using System.Text;
using HarmonyLib;
using Sandbox.Engine.Physics;
using Sandbox.Game.AI;
using Sandbox.Game.AI.Navigation;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.GameSystems;
using Sandbox.ModAPI;
using Sandbox.Game.World;
using SpaceEngineers.Game.AI;
using SpaceEngineers.Game.Entities.Blocks;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Network;
using VRageMath;

namespace SpaceEngineersVR.Scenarios.BugSwarm
{
    /// <summary>
    /// The swarm's bugs run the game's own spider AI, with two changes while a <see cref="BugSwarmSession"/> is on:
    /// what a bug goes for, and, for stingers, which way is down. A swarm block's buttons wake the swarm whatever is on
    /// their toolbar, empty slots too.
    /// </summary>
    /// <remarks>
    /// The game's spider takes the best-ranked thing within its search radius: a player, then guns and tools, then
    /// wheels and thrusters, then power, from whichever grid it looked at last. Here a bug goes for a player within the
    /// swarm block's reach setting (a stinger for any player it can find) and otherwise for the block it is after.
    /// It walks straight at its target (the spider's tree steers without pathfinding), and its bites
    /// (<see cref="BugSwarmSession"/>) chew through the walls in its way.
    ///
    /// Every bug climbs, as a spider does: while it touches the swarm block's grids, the
    /// gravity it feels points into the surface it stands on, the wall it walks up against, or the face round an edge it walks over. The game then stands it on that surface as it
    /// stands an engineer on a hull with magnetic boots, and the bug walks on it as on a floor.
    /// </remarks>
    internal static class SwarmAi
    {
        private static bool patched;

        private static readonly AccessTools.FieldRef<MyAiTargetBase, MyAgentBot> BotOf =
            AccessTools.FieldRefAccess<MyAiTargetBase, MyAgentBot>("m_bot");

        // The way down for the character whose movement the game is working out, while it does; null for the planet's.
        [ThreadStatic] private static Vector3? climbDown;

        [ThreadStatic] private static List<MyPhysics.HitInfo> hits;

        // The way out of the surface each bug on the swarm's grids stands on, from its last movement.
        private static readonly ConcurrentDictionary<long, Vector3> surfaces = new ConcurrentDictionary<long, Vector3>();

        /// <summary>Patches the game once, at the first world loaded; the patches do nothing while no swarm is on.</summary>
        public static void Patch()
        {
            if (patched)
                return;
            patched = true;
            // Through the patch registry: a game update that renames what this hooks is in the log's patch line and the main
            // menu's notice, and the swarm blocks do nothing (the patches it had made come off with it).
            PatchRegistry.ApplyApart("BugSwarm", harmony =>
            {
                Hook(harmony, typeof(MyButtonPanel), nameof(MyButtonPanel.ActivateButton), prefix: nameof(ButtonPressed));
                Hook(harmony, typeof(SpaceEngineers.Game.Entities.UseObjects.MyUseObjectPanelButton), "Use", prefix: nameof(PanelButtonUsed));
                Hook(harmony, typeof(MySpaceRespawnComponent), "ShowMedicalScreen_Implementation", prefix: nameof(NoRespawnScreen));
                Hook(harmony, typeof(MySpaceRespawnComponent), "ShowMotD", prefix: nameof(NoWelcome));
                Hook(harmony, typeof(MyAIComponent), nameof(MyAIComponent.CanSpawnMoreBots), postfix: nameof(RoomForBug));
                Hook(harmony, typeof(MySpiderActions), "GetTargetWithPriority", prefix: nameof(PickTarget));
                Hook(harmony, typeof(MyAiTargetBase), nameof(MyAiTargetBase.IsMemoryTargetValid), postfix: nameof(KeepTarget));
                Hook(harmony, typeof(MyBotNavigation), "MoveCharacter", prefix: nameof(Steer));
                Hook(harmony, typeof(MyCharacter), "UpdatePhysicalMovement", prefix: nameof(BeforeMovement), postfix: nameof(AfterMovement));
                Hook(harmony, typeof(MyCharacter), "UpdateStandup", prefix: nameof(StandOnSurface));
                Hook(harmony, typeof(MyGravityProviderSystem), nameof(MyGravityProviderSystem.CalculateTotalGravityInPoint), postfix: nameof(Gravity));
            }, "Bug Swarm: the game could not be patched, so swarm blocks do nothing");
        }

        private static void Hook(Harmony harmony, Type type, string method, string prefix = null, string postfix = null)
        {
            var target = AccessTools.Method(type, method);
            if (target == null)
                throw new MissingMethodException(type.Name, method);
            harmony.Patch(target, prefix: prefix != null ? new HarmonyMethod(typeof(SwarmAi), prefix) : null,
                postfix: postfix != null ? new HarmonyMethod(typeof(SwarmAi), postfix) : null);
        }

        private static MyCharacter Bug(MyAiTargetBase ai, BugSwarmSession swarm)
        {
            MyCharacter bug = ai != null ? BotOf(ai)?.AgentEntity : null;
            return bug != null && swarm.IsBug(bug.EntityId) ? bug : null;
        }

        // Runs where the world is simulated, for a press on any player's machine; the game skips empty toolbar slots after this.
        private static void ButtonPressed(MyButtonPanel __instance)
        {
            long by = 0;
            if (MyEventContext.Current.Sender.Value != 0)
                by = MySession.Static.Players.TryGetIdentityId(MyEventContext.Current.Sender.Value);
            BugSwarmSession.Pressed(__instance, by);
        }

        private static readonly AccessTools.FieldRef<SpaceEngineers.Game.Entities.UseObjects.MyUseObjectPanelButton, MyButtonPanel> PanelOf =
            AccessTools.FieldRefAccess<SpaceEngineers.Game.Entities.UseObjects.MyUseObjectPanelButton, MyButtonPanel>("m_buttonPanel");

        // On the game of the player pressing it: a swarm block's button, in a game whose host cannot run the swarm, says why.
        // The press itself goes on as the game's.
        private static void PanelButtonUsed(SpaceEngineers.Game.Entities.UseObjects.MyUseObjectPanelButton __instance,
            VRage.Game.Entity.UseObject.UseActionEnum actionEnum)
        {
            try
            {
                if (actionEnum != VRage.Game.Entity.UseObject.UseActionEnum.Manipulate || !BugSwarmSession.BlocksOn)
                    return;
                MyButtonPanel panel = PanelOf(__instance);
                if (panel == null || !SwarmSettings.Of(panel).Enabled)
                    return;
                BugSwarmSession.Blocked why = BugSwarmSession.WhyNot();
                if (why != BugSwarmSession.Blocked.None)
                    MyAPIGateway.Utilities.ShowNotification(BugSwarmSession.Why(why), 6000, "Red");
            }
            catch (Exception e)
            {
                Log.Error(e, "Bug Swarm: a swarm block's press could not be checked");
            }
        }

        /// <summary>
        /// A player who dies in a run is brought back at the base by the host (<see cref="BugSwarmSession"/>); instead of the
        /// game's respawn screen they see a small notice that closes as they come back. Should the host not bring them back,
        /// the respawn screen opens after all.
        /// </summary>
        private static bool NoRespawnScreen(MySpaceRespawnComponent.MOTDData motd, long restrictedRespawn, bool showDiedInCryoMessage)
        {
            // a run just over: the host brings the player back once the base is put back
            if (respawnScreenAnyway || !BugSwarmSession.BringsBack
                || !BugSwarmSession.RunOn && (DateTime.UtcNow - BugSwarmSession.RunOverAt).TotalSeconds > 15)
                return true;
            // a line on the HUD, not a box, which would hold the controls once the player is back
            bool on = BugSwarmSession.RunOn;
            MyAPIGateway.Utilities.ShowNotification(on ? $"Back at the base in {BugSwarmSession.BackAfterSeconds} s."
                    : "The run is over. The base is being put back as it was, and you with it.",
                (on ? BugSwarmSession.BackAfterSeconds : BugSwarmSession.PutBackAfterSeconds) * 1000, "White");
            long dead = MyAPIGateway.Session.Player?.IdentityId ?? 0;
            MyAPIGateway.Utilities.InvokeOnGameThread(() => RespawnScreenIfStillDead(dead, motd, restrictedRespawn, 0));
            return false;
        }

        private static bool respawnScreenAnyway;

        // The server's welcome message, which the game shows again at each respawn, waits until the run is over.
        private static bool NoWelcome() => !BugSwarmSession.RunOn && (DateTime.UtcNow - BugSwarmSession.RunOverAt).TotalSeconds > 15;

        // Checked each frame: the host brings a player back a moment after they die, or, when the run ends meanwhile, once
        // the base is put back; a minute on, or a quarter of one after the run ended, the respawn screen opens after all.
        private static void RespawnScreenIfStillDead(long dead, MySpaceRespawnComponent.MOTDData motd, long restrictedRespawn, int frames)
        {
            IMyPlayer player = MyAPIGateway.Session?.Player;
            if (player == null || player.IdentityId != dead || player.Character != null && !player.Character.IsDead)
                return;
            if (frames < 60 * 60 && (BugSwarmSession.RunOn || (DateTime.UtcNow - BugSwarmSession.RunOverAt).TotalSeconds < 15))
            {
                MyAPIGateway.Utilities.InvokeOnGameThread(() => RespawnScreenIfStillDead(dead, motd, restrictedRespawn, frames + 1));
                return;
            }
            respawnScreenAnyway = true;
            try
            {
                AccessTools.Method(typeof(MySpaceRespawnComponent), "ShowMedicalScreen_Implementation")
                    .Invoke(null, new object[] { motd, restrictedRespawn, false });
            }
            finally
            {
                respawnScreenAnyway = false;
            }
        }

        // While a swarm runs, the game spawns bots past its world's limit up to the swarm's own (most bugs at once).
        private static void RoomForBug(MyAIComponent __instance, ref bool __result)
        {
            BugSwarmSession swarm = BugSwarmSession.Active;
            if (!__result && swarm != null && __instance.Bots.GetGeneratedBotCount() < swarm.BotCap)
                __result = true;
        }

        private static bool PickTarget(MySpiderActions __instance, float radius, ref MyBBMemoryTarget outTarget,
            ref MyBBMemoryInt priority, ref MyBehaviorTreeState __result)
        {
            BugSwarmSession swarm = BugSwarmSession.Active;
            MyCharacter bug = swarm != null ? Bug(__instance.AiTargetBase, swarm) : null;
            if (bug == null)
                return true;
            if (priority == null)
                priority = new MyBBMemoryInt();

            Vector3D at = bug.PositionComp.GetPosition();
            double reach = BugSwarmSession.IsStinger(bug) ? radius : swarm.PlayerReach;
            double nearest = reach * reach;
            IMyCharacter prey = null;
            foreach (IMyCharacter person in swarm.People)
            {
                double distance = Vector3D.DistanceSquared(person.GetPosition(), at);
                if (!person.IsDead && distance <= nearest)
                {
                    nearest = distance;
                    prey = person;
                }
            }

            IMyTerminalBlock goal = swarm.Target;
            if (prey != null)
            {
                MyBBMemoryTarget.SetTargetEntity(ref outTarget, MyAiTargetEnum.CHARACTER, prey.EntityId);
                priority.IntValue = 1;
            }
            else if (goal != null)
            {
                MyBBMemoryTarget.SetTargetCube(ref outTarget, goal.Position, goal.CubeGrid.EntityId);
                priority.IntValue = 4;
            }
            else
            {
                MyBBMemoryTarget.UnsetTarget(ref outTarget);
                __result = MyBehaviorTreeState.FAILURE;
                return false;
            }
            __result = MyBehaviorTreeState.SUCCESS;
            return false;
        }

        // The game's spiders do not go for grids, so it would drop the swarm's goal as soon as it was set.
        private static void KeepTarget(MyAiTargetBase __instance, MyBBMemoryTarget targetMemory, ref bool __result)
        {
            if (__result || targetMemory == null || targetMemory.TargetType != MyAiTargetEnum.CUBE)
                return;
            BugSwarmSession swarm = BugSwarmSession.Active;
            var grid = swarm != null ? MyAPIGateway.Entities.GetEntityById(targetMemory.EntityId) as IMyCubeGrid : null;
            if (grid != null && swarm.Grids.Contains(grid) && Bug(__instance, swarm) != null
                && grid.GetCubeBlock(targetMemory.BlockPosition) != null)
                __result = true;
        }

        private static void BeforeMovement(MyCharacter __instance)
        {
            climbDown = null;
            BugSwarmSession swarm = BugSwarmSession.Active;
            if (swarm == null || !swarm.Climbs || __instance.IsDead || !swarm.IsBug(__instance.EntityId))
                return;
            climbDown = ClimbDown(__instance, swarm.Grids);
            if (climbDown.HasValue)
                surfaces[__instance.EntityId] = -climbDown.Value;
            else
                surfaces.TryRemove(__instance.EntityId, out _);
        }

        private static void AfterMovement()
        {
            climbDown = null;
        }

        private static void Gravity(ref Vector3 __result)
        {
            if (climbDown.HasValue)
                __result = climbDown.Value * __result.Length();
        }

        /// <summary>
        /// The way down for a bug on the swarm's grids: into a wall ahead of it, into the surface under it, or into the face
        /// round an edge it has walked over. Null off them, where the planet holds it.
        /// </summary>
        private static Vector3? ClimbDown(MyCharacter bug, HashSet<IMyEntity> grids)
        {
            MatrixD frame = bug.WorldMatrix;
            Vector3D middle = bug.PositionComp.WorldAABB.Center;
            double reach = bug.PositionComp.LocalVolume.Radius + 0.5;
            Vector3D under = middle + frame.Down * reach;
            Vector3 normal;
            if (Surface(middle, middle + frame.Forward * reach, grids, out normal)
                || Surface(middle, under, grids, out normal)
                || Surface(under, under + frame.Backward * reach, grids, out normal))
                return -normal;
            return null;
        }

        /// <summary>
        /// Where a bug walks, set as the game turns its path into movement, for the path-found chase and the straight walk
        /// alike: a bug on a wall whose goal is behind the wall heads up it, to go over the top (the game's path runs along
        /// the ground, and would turn it back down); a bug on the ground or a floor with a wall ahead goes through a gap in it
        /// it can see, as a spider would; with none it walks at the wall and up it.
        /// </summary>
        private static void Steer(MyBotNavigation __instance, ref Vector3 ___m_forwardVector)
        {
            BugSwarmSession swarm = BugSwarmSession.Active;
            var bug = __instance.BotEntity as MyCharacter;
            if (swarm == null || !swarm.FindsWayIn && !swarm.Climbs || bug == null || !swarm.IsBug(bug.EntityId))
                return;
            Vector3D at = bug.PositionComp.GetPosition();
            Vector3 gravity = MyGravityProviderSystem.CalculateNaturalGravityInPoint(at);
            if (gravity.LengthSquared() < 0.01f)
                return;
            Vector3D up = -Vector3D.Normalize(gravity);
            Vector3D goal = __instance.TargetPoint;
            Vector3D? way = null;
            Vector3 normal;
            bool onGrid = surfaces.TryGetValue(bug.EntityId, out normal);
            if (onGrid && Vector3D.Dot(normal, up) < 0.7)
                // on a wall: up it while the goal is behind it, else across or down it toward the goal
                way = Vector3D.Dot(goal - at, normal) < 0 ? Vector3D.Reject(up, normal) : Vector3D.Reject(goal - at, normal);
            else
            {
                Vector3D? gap = swarm.FindsWayIn ? ThroughAGap(bug, goal, up, swarm.Grids) : null;
                if (gap.HasValue)
                    way = gap.Value - bug.PositionComp.WorldAABB.Center;
                else if (onGrid && swarm.Climbs)
                    // on a floor or a wall top of the base the game's path, laid on the ground outside, no longer leads
                    // anywhere: straight for the goal, over the edge and down the far side
                    way = Vector3D.Reject(goal - at, up);
            }
            if (!way.HasValue || way.Value.LengthSquared() < 0.01)
                return;
            way = Vector3D.Normalize(way.Value);
            ___m_forwardVector = way.Value;
            if (__instance.WantedSpeed < 1f)
                setWantedSpeed(__instance, 1f);
        }

        private static readonly Action<MyBotNavigation, float> setWantedSpeed =
            AccessTools.MethodDelegate<Action<MyBotNavigation, float>>(AccessTools.PropertySetter(typeof(MyBotNavigation), nameof(MyBotNavigation.WantedSpeed)));

        // metres: how near a wall must be before a bug looks along it for a way through, and how far past it it looks
        private const double WallNear = 8, PastWall = 2;

        /// <summary>
        /// A point through an opening in the wall between the bug and its goal: the nearest bearing to the goal's, within 60
        /// degrees either side, along which the bug sees past the wall's face. Null with no wall near or no opening in sight.
        /// </summary>
        private static Vector3D? ThroughAGap(MyCharacter bug, Vector3D goal, Vector3D up, HashSet<IMyEntity> grids)
        {
            Vector3D middle = bug.PositionComp.WorldAABB.Center;
            Vector3D ahead = Vector3D.Reject(goal - middle, up);
            double far = ahead.Length();
            if (far < 1)
                return null;
            ahead /= far;
            Vector3 wallNormal;
            Vector3D wall;
            if (!Surface(middle, middle + ahead * Math.Min(far, WallNear), grids, out wallNormal, out wall))
                return null;
            double look = Vector3D.Distance(middle, wall) / 0.5 + PastWall;
            for (int step = 1; step <= 6; step++)
                for (int side = -1; side <= 1; side += 2)
                {
                    Vector3D way = Vector3D.Transform(ahead, MatrixD.CreateFromAxisAngle(up, side * step * MathHelper.ToRadians(10)));
                    Vector3D end = middle + way * look;
                    Vector3 n;
                    Vector3D hit;
                    if (Vector3D.Dot(end - wall, wallNormal) < -PastWall && !Surface(middle, end, grids, out n, out hit))
                        return end;
                }
            return null;
        }

        /// <summary>
        /// While a bug moves on the swarm's grids it stands up toward the surface the climbing found, not the one the game
        /// supports it on: a spider's rotation follows its support, which for one on the ground facing a wall is the ground.
        /// The turn is the game's own, a little quicker.
        /// </summary>
        private static bool StandOnSurface(ref Vector3 chUp, ref Vector3 chForward)
        {
            if (!climbDown.HasValue)
                return true;
            Vector3 toward = -climbDown.Value;
            float cos = MathHelper.Clamp(Vector3.Dot(Vector3.Normalize(chUp), toward), -1f, 1f);
            Vector3 axis = Vector3.Cross(chUp, toward);
            if (cos < 0.9999f && axis.LengthSquared() > 0f)
            {
                float angle = Math.Min((float)Math.Acos(cos), TurnPerFrame);
                Matrix turn = Matrix.CreateFromAxisAngle(Vector3.Normalize(axis), angle);
                chUp = Vector3.TransformNormal(chUp, turn);
                chForward = Vector3.TransformNormal(chForward, turn);
            }
            return false;
        }

        /// <summary>Whether the bug stands on a wall or a slope of the swarm's grids rather than on the level, for the log.</summary>
        internal static bool OnWall(IMyCharacter bug)
        {
            Vector3 normal;
            if (!surfaces.TryGetValue(bug.EntityId, out normal))
                return false;
            Vector3 gravity = MyGravityProviderSystem.CalculateNaturalGravityInPoint(bug.GetPosition());
            return gravity.LengthSquared() > 0.01f && Vector3.Dot(normal, -Vector3.Normalize(gravity)) < 0.7f;
        }

        // radians a frame: a quarter turn onto a wall in about a third of a second
        private const float TurnPerFrame = 0.08f;

        private static bool Surface(Vector3D from, Vector3D to, HashSet<IMyEntity> grids, out Vector3 normal)
        {
            Vector3D point;
            return Surface(from, to, grids, out normal, out point);
        }

        private static bool Surface(Vector3D from, Vector3D to, HashSet<IMyEntity> grids, out Vector3 normal, out Vector3D point)
        {
            if (hits == null)
                hits = new List<MyPhysics.HitInfo>();
            hits.Clear();
            MyPhysics.CastRay(from, to, hits, MyPhysics.CollisionLayers.NoVoxelCollisionLayer);
            double nearest = double.MaxValue;
            normal = Vector3.Zero;
            point = to;
            foreach (MyPhysics.HitInfo hit in hits)
            {
                double distance = Vector3D.DistanceSquared(hit.Position, from);
                if (grids.Contains(hit.HkHitInfo.GetHitEntity()) && distance < nearest)
                {
                    nearest = distance;
                    normal = hit.HkHitInfo.Normal;
                    point = hit.Position;
                }
            }
            return nearest < double.MaxValue;
        }
    }
}
