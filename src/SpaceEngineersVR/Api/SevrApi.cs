// SevrApi: the plugin's public API for mods and other plugins, so they can present themselves properly in VR without being patched.
using System;
using System.Collections.Generic;
using Sandbox.Game.World;
using SpaceEngineersVR.Rendering;
using Sandbox.ModAPI;
using VRageMath;

namespace SpaceEngineersVR.Api
{
    /// <summary>
    /// The plugin's API for mods and other plugins (docs/MOD_API.md): what a mod needs to draw its HUD so it reads in a headset,
    /// handed over through the game's mod messaging, the way Text HUD API and other frameworks hand over theirs, since a mod
    /// cannot reference a plugin. Mods get an API to call rather than being special-cased; a mod that does not work in VR yet is
    /// changed to call it, and the change offered to its author.
    /// <para>
    /// The handshake, on <see cref="Channel"/>: while a world runs, the plugin listens there, sends the API (a
    /// Dictionary&lt;string, Delegate&gt;, see <see cref="Build"/>) when the world starts, and sends it again to anyone who sends
    /// <see cref="Request"/>, so a mod loaded before or after the plugin gets it either way. Every member is read on the game
    /// thread, where mods run. It gives presentation only (where and whether the HUD is shown); nothing about the game a player
    /// without a headset could not see (fairness, docs/DEVELOPMENT.md).
    /// </para>
    /// <para>
    /// Versioned: <see cref="Version"/> grows when members are added; a member's meaning never changes, and one that has to is a
    /// new member under a new name.
    /// </para>
    /// </summary>
    internal static class SevrApi
    {
        /// <summary>The mod message channel: "SEVRAPI1" in ASCII, as a long.</summary>
        public const long Channel = 6000296989824141617L;

        /// <summary>What a mod sends on <see cref="Channel"/> to be sent the API.</summary>
        public const string Request = "SEVR.ApiRequest";

        /// <summary>The API's version: 1, the HUD members.</summary>
        public const int Version = 1;

        private static readonly FeatureGuard guard = new FeatureGuard(nameof(SevrApi));
        private static Dictionary<string, Delegate> api;
        private static MySession listening;
        private static Action<object> handler;

        /// <summary>
        /// Game thread, every frame (<see cref="Plugin.Update"/>): starts listening, and sends the API, when a world starts, and
        /// stops when it ends.
        /// </summary>
        public static void Update()
        {
            if (!guard.On)
                return;
            try
            {
                MySession session = MySession.Static;
                bool ready = session != null && session.Ready && MyAPIGateway.Utilities != null;
                if (ready && ReferenceEquals(session, listening))
                    return;
                Stop();
                if (!ready)
                    return;
                api = api ?? Build();
                handler = Received;
                MyAPIGateway.Utilities.RegisterMessageHandler(Channel, handler);
                listening = session;
                MyAPIGateway.Utilities.SendModMessage(Channel, api);
                Log.Info($"SEVR API: version {Version} offered on mod channel {Channel} (\"SEVRAPI1\"); a mod sends \"{Request}\" there to be sent it (docs/MOD_API.md)");
            }
            catch (Exception e)
            {
                guard.Failed(e, "SEVR API could not be offered");
            }
        }

        private static void Stop()
        {
            if (handler != null && MyAPIGateway.Utilities != null)
                MyAPIGateway.Utilities.UnregisterMessageHandler(Channel, handler);
            handler = null;
            listening = null;
        }

        /// <summary>A message on the channel: a request is answered with the API; anything else (the API itself, as sent) is not ours to answer.</summary>
        private static void Received(object message)
        {
            if (message is string text && text == Request)
                MyAPIGateway.Utilities.SendModMessage(Channel, api);
        }

        /// <summary>
        /// The API's members, by name. Delegates of types the mod whitelist allows (Func and the VRageMath types), so a mod can call
        /// them as they are:
        /// <list type="bullet">
        /// <item>"Version": Func&lt;int&gt;, <see cref="Version"/>.</item>
        /// <item>"IsActive": Func&lt;bool&gt;, the game is shown in a headset now.</item>
        /// <item>"HudVisible": Func&lt;bool&gt;, the headset shows the HUD now (false while the helmet is open with the helmet view on).</item>
        /// <item>"HudDepth": Func&lt;double&gt;, metres from the eyes at which the HUD is seen: draw a HUD this far out, not at the camera.</item>
        /// <item>"HudTangents": Func&lt;Vector2D&gt;, the tangents of half the HUD area's width and height, from the eyes.</item>
        /// <item>"HudToWorld": Func&lt;Vector2D, Vector3D&gt;, a point of the HUD area (x right and y up, -1 to 1, as Text HUD API's
        /// screen space) to where it is in the world, <c>HudDepth</c> ahead of the eyes, on the view ray through it.</item>
        /// </list>
        /// </summary>
        private static Dictionary<string, Delegate> Build() => new Dictionary<string, Delegate>
        {
            ["Version"] = new Func<int>(() => Version),
            ["IsActive"] = new Func<bool>(IsActive),
            ["HudVisible"] = new Func<bool>(() => IsActive() && !HelmetHud.Hidden),
            ["HudDepth"] = new Func<double>(() => VRSettings.HudDistance),
            ["HudTangents"] = new Func<Vector2D>(Tangents),
            ["HudToWorld"] = new Func<Vector2D, Vector3D>(HudToWorld),
        };

        private static bool IsActive() => StereoPatches.VRPicture && OpenXR.XrSession.Running;

        /// <summary>Half the HUD's width and height as tangents from the eyes: <see cref="VRSettings.HudWidthDegrees"/> wide, the GUI's 16:9 high.</summary>
        private static Vector2D Tangents()
        {
            double wide = Math.Tan(MathHelper.ToRadians(VRSettings.HudWidthDegrees) / 2.0);
            return new Vector2D(wide, wide * GuiLayout.Size.Y / GuiLayout.Size.X);
        }

        /// <summary>The world point of HUD point <paramref name="hud"/>: from the head camera, along its ray through the HUD area, <see cref="VRSettings.HudDistance"/> ahead.</summary>
        private static Vector3D HudToWorld(Vector2D hud)
        {
            var camera = MySector.MainCamera;
            if (camera == null)
                return Vector3D.Zero;
            MatrixD head = camera.WorldMatrix;
            Vector2D tangents = Tangents();
            double depth = VRSettings.HudDistance;
            return head.Translation + depth * (head.Forward + hud.X * tangents.X * head.Right + hud.Y * tangents.Y * head.Up);
        }
    }
}
