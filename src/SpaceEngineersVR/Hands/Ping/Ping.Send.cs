// Ping: the GPS and the chat line, through the game's own paths only, so other players need no plugin.
using System;
using Sandbox.Engine.Multiplayer;
using Sandbox.Engine.Networking;
using Sandbox.Game.Gui;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using VRage.Game.ModAPI;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    internal static partial class Ping
    {
        /// <summary>How long the HUD line after a ping or mark stays up, milliseconds of simulation time.</summary>
        private const int NotifyMilliseconds = 3000;

        /// <summary>The live label while aiming is put up again this often; it is replaced as the target changes.</summary>
        private const int LabelMilliseconds = 500;

        /// <summary>The last ping's GPS in the local list, and when it is to fade (<see cref="HoldFade"/>).</summary>
        private static MyGps fading;
        private static TimeSpan fadeAt;

        private static MyHudNotificationBase label;
        private static string shownLabel;

        /// <summary>The last ping or mark made, for the drive's report.</summary>
        internal static string LastResult { get; private set; } = "none yet";

        /// <summary>
        /// Pings or marks what the ray meets now. A fresh cast with the voxel geometry, so what is sent is what is under the
        /// laser this frame, not the preview's.
        /// </summary>
        internal static void Commit(Kind kind)
        {
            if (!TryRay(VRControls.Context, out Vector3D origin, out Vector3D direction, out _))
            {
                Notify("Ping: no ray (the hand is not tracked)", NotifyMilliseconds);
                return;
            }
            Target target = Cast(origin, direction, voxelsToo: true);
            if (!Send(kind, target))
                return;
            flash = new Flash { Point = target.Point, Color = target.Color, Distance = Vector3D.Distance(origin, target.Point), Started = FlashClock() };
            Haptics.Pulse(VRSettings.DominantHand, CommitAmplitude, CommitSeconds);
        }

        /// <summary>
        /// The GPS, and for a ping the chat line. Internal so the drive can send a target it cast itself. A target that met
        /// nothing within range sends nothing and makes no GPS; returns whether anything was sent.
        /// </summary>
        internal static bool Send(Kind kind, Target target)
        {
            if (!target.Hit)
            {
                LastResult = $"no {(kind == Kind.Ping ? "ping" : "mark")}: nothing within {Metres(Range)}";
                Log.Info("Ping: " + LastResult);
                Notify($"Nothing in range ({Metres(Range)})", NotifyMilliseconds);
                return false;
            }
            MySession session = MySession.Static;
            long me = session.LocalPlayerId;
            string player = session.LocalHumanPlayer?.DisplayName ?? "VR";
            var gps = new MyGps
            {
                Name = target.Name,
                Description = kind == Kind.Ping ? $"{target.Line} (ping from {player})" : target.Line,
                // Rounded as the game's chat scan rounds them, so a ping that comes back over the chat is the same GPS.
                Coords = new Vector3D(Math.Round(target.Point.X, 2), Math.Round(target.Point.Y, 2), Math.Round(target.Point.Z, 2)),
                GPSColor = target.Color,
                ShowOnHud = true,
            };
            if (kind == Kind.Mark)
                gps.DiscardAt = null; // final: kept and saved, as one made in the GPS screen
            gps.UpdateHash();
            // The game ignores an add whose name and place are already in the list, and only restarts a temporary one's three
            // minutes, so a mark on what was just pinged is made final as the GPS screen's edit does.
            if (kind == Kind.Mark && session.Gpss.GetGps(me, gps.Hash) != null)
                session.Gpss.SendModifyGpsRequest(me, gps);
            else
                session.Gpss.SendAddGpsRequest(me, ref gps);
            if (kind == Kind.Ping)
            {
                // The game gives a temporary GPS three minutes (MyGps.DROP_NONFINAL_AFTER_SEC); a ping fades sooner. Only the
                // local copy: the server keeps its own as the game made it, and it goes with the session.
                gps.DiscardAt = TimeSpan.FromSeconds(session.ElapsedPlayTime.TotalSeconds + VRSettings.PingSeconds);
                fading = gps;
                fadeAt = gps.DiscardAt.Value;
            }

            string where = kind == Kind.Ping ? Chat(target.Line + " " + gps) : "your GPS list";
            LastResult = $"{(kind == Kind.Ping ? "ping" : "mark")} '{gps.Name}' ({target.What}) at {gps.Coords.X:0.##}, {gps.Coords.Y:0.##}, {gps.Coords.Z:0.##}; {where}";
            Log.Info("Ping: " + LastResult);
            Notify(kind == Kind.Ping ? $"Pinged: {target.Line}" : $"GPS added: {gps.Name}", NotifyMilliseconds);
            return true;
        }

        /// <summary>
        /// Game thread, every frame: keeps the last ping's own GPS to its time. The chat line that carries it comes back
        /// through the game's chat scan, which finds the GPS already listed and gives it three minutes again
        /// (MyGpsCollection.AddPlayerGps), at once alone and when the server echoes it in multiplayer.
        /// </summary>
        private static void HoldFade()
        {
            MySession session = MySession.Static;
            if (fading == null)
                return;
            if (session == null || !fading.DiscardAt.HasValue || session.ElapsedPlayTime > fadeAt)
            {
                fading = null; // gone with the session, kept since (marked), or faded
                return;
            }
            if (fading.DiscardAt.Value > fadeAt)
                fading.DiscardAt = fadeAt;
        }

        /// <summary>
        /// The chat line, in the faction's channel, or the global one only when the Ping chat setting says so for a player with
        /// no faction (it does not by default: the ping then stays the player's own marker), through the game's own send
        /// (MyGuiScreenChat.SendChatMessage, with the channel chosen here rather than the chat screen's). Every client that
        /// receives it adds the GPS in it to its list (MyGpsCollection.ParseChat). Returns where it went.
        /// </summary>
        private static string Chat(string text)
        {
            int setting = VRSettings.PingChat;
            if (setting == VRSettings.PingChatOff)
                return "no chat (Ping chat is off)";
            MySession session = MySession.Static;
            IMyFaction faction = session.Factions.TryGetPlayerFaction(session.LocalPlayerId);
            ChatChannel channel;
            long targetId = 0L;
            if (faction != null)
            {
                channel = ChatChannel.Faction;
                targetId = faction.FactionId;
            }
            else if (setting == VRSettings.PingChatFactionOrGlobal)
                channel = ChatChannel.Global;
            else
                return "no chat (no faction)";

            if (MyMultiplayer.Static != null)
                MyMultiplayer.Static.SendChatMessage(text, channel, targetId);
            else if (MyGameService.IsActive)
                MyHud.Chat.ShowMessageColoredSP(text, channel, targetId);
            else
                MyHud.Chat.ShowMessage(session.LocalHumanPlayer?.DisplayName ?? string.Empty, text);
            return MyMultiplayer.Static == null ? "chat (this game only)" : channel == ChatChannel.Faction ? $"faction chat [{faction.Tag}]" : "global chat";
        }

        /// <summary>Casts while aiming and keeps the HUD label on what is under the laser.</summary>
        private static void UpdateAim(VRContext context)
        {
            if (!TryRay(context, out Vector3D origin, out Vector3D direction, out _))
            {
                preview = default;
                SetLabel("Ping: point at something");
                return;
            }
            // The voxel geometry is meshed for a far surface, so it is cast only now and then while aiming; the physics every frame.
            bool voxelsToo = castFrame++ % VoxelCastEveryFrames == 0;
            Target target = Cast(origin, direction, voxelsToo);
            if (!voxelsToo && !target.Hit && preview.Hit && preview.What != null && preview.What.StartsWith("voxel", StringComparison.Ordinal))
                target = preview; // keep the far surface found last time until the next geometry cast
            preview = target;
            if (target.Hit)
                SetLabel($"{target.Line}, {Metres(Vector3D.Distance(origin, target.Point))}.  {AimVerb}");
            else
                SetLabel($"Nothing in range ({Metres(Range)})");
        }

        private static string Metres(double m) => m < 1000d ? $"{m:0} m" : $"{m / 1000d:0.0} km";

        private static void SetLabel(string text)
        {
            if (text == shownLabel && label != null && label.Alive)
                return;
            shownLabel = text;
            if (label != null)
                MyHud.Notifications?.Remove(label);
            label = Show(text, LabelMilliseconds * 20);
        }

        private static void ClearLabel()
        {
            if (label != null)
                MyHud.Notifications?.Remove(label);
            label = null;
            shownLabel = null;
            preview = default;
        }

        /// <summary>A line on the HUD (the game's notifications, level Important so the minimal HUD keeps it, as the control hints).</summary>
        private static void Notify(string text, int milliseconds) => Show(text, milliseconds);

        private static MyHudNotificationBase Show(string text, int milliseconds)
        {
            try
            {
                MyHudNotifications notifications = MyHud.Notifications;
                if (notifications == null || MySession.Static == null)
                    return null;
                var note = new MyHudNotificationDebug(text, milliseconds, level: MyNotificationLevel.Important);
                notifications.Add(note);
                return note;
            }
            catch (Exception e)
            {
                if (errors++ < 3)
                    Log.Error(e, "Ping: a HUD line could not be shown");
                return null;
            }
        }
    }
}
