using System;
using System.Collections.Generic;
using System.IO;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.Utils;
using VRageMath;

namespace SevrSampleMod
{
    /// <summary>
    /// A sample of Cheezecake's SE VR's API for a mod (docs/MOD_API.md): the handshake on the mod channel, an action the player
    /// binds, an item on the actions wheel with this mod's own icon, and the HUD members. Every line it writes to the game's
    /// log starts "SevrSample: mod:". Without the VR plugin nothing arrives, and the mod does nothing more.
    /// </summary>
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class SevrSampleSession : MySessionComponentBase
    {
        const long Channel = 6000296989824141617L;
        const string Request = "SEVR.ApiRequest";
        const string Owner = "SevrSampleMod";

        Dictionary<string, Delegate> api;
        Func<bool> isActive, hudVisible;
        Func<double> hudDepth;
        Func<Vector2D> hudTangents;
        Func<Vector2D, Vector3D> hudToWorld;
        Func<string, string> actionLabel;

        bool registered;
        bool lastActive, lastHudVisible;
        int ticks, uses, sends;

        public override void LoadData()
        {
            MyAPIGateway.Utilities.RegisterMessageHandler(Channel, Received);
            MyAPIGateway.Utilities.SendModMessage(Channel, Request);
            Log("asked for the API");
        }

        protected override void UnloadData()
        {
            MyAPIGateway.Utilities.UnregisterMessageHandler(Channel, Received);
            var unregister = Get<Func<string, bool>>("Unregister");
            if (unregister != null)
                Log("Unregister at the world's end: " + unregister(Owner));
            api = null;
            isActive = hudVisible = null;
            hudDepth = null;
            hudTangents = null;
            hudToWorld = null;
            actionLabel = null;
            registered = false;
            sends = 0;
        }

        void Received(object message)
        {
            var offered = message as Dictionary<string, Delegate>;
            if (offered == null)
                return;                                // a request, ours or another mod's
            sends++;
            if (api != null)
            {
                Log("the API again (send " + sends + "), ignored");
                return;                                // the API again: the plugin sends it for every request
            }
            api = new Dictionary<string, Delegate>(offered);   // our own copy: the one sent is shared with every mod

            var version = Get<Func<int>>("Version");
            isActive = Get<Func<bool>>("IsActive");
            hudVisible = Get<Func<bool>>("HudVisible");
            hudDepth = Get<Func<double>>("HudDepth");
            hudTangents = Get<Func<Vector2D>>("HudTangents");
            hudToWorld = Get<Func<Vector2D, Vector3D>>("HudToWorld");
            Log("received the API, version " + (version == null ? "unknown" : version().ToString()));
            LogHud();
            if (version == null || version() < 2)
                return;                                // an older plugin: the HUD members only

            var setOwnerName = Get<Func<string, string, bool>>("SetOwnerName");
            var registerAction = Get<Func<string, string, string, string, string, bool, Func<bool>, Action<int>, bool>>("RegisterAction");
            var registerItem = Get<Func<string, string, string, string, string, string, Action, Func<bool>, Func<string>, string, bool>>("RegisterMenuItem");
            actionLabel = Get<Func<string, string>>("ActionLabel");
            if (setOwnerName == null || registerAction == null || registerItem == null || actionLabel == null)
            {
                Log("a version 2 member is missing or of another type");
                return;
            }

            bool named = setOwnerName(Owner, "SEVR sample mod");
            // Anywhere the player plays; no suggestion, so the player binds it on the bindings page.
            bool action = registerAction(Owner, "hello", "Sample mod hello", "Gameplay", null, false, null, on =>
            {
                if (on == 1)
                {
                    Log("action hello on");
                    MyAPIGateway.Utilities.ShowNotification("Hello from the controller", 1500);
                }
            });
            // An item on the actions wheel with this mod's own icon.
            string icon = Path.Combine(ModContext.ModPath, "Textures", "SevrSampleIcon.dds");
            bool item = registerItem(Owner, "ActionsWheel", "hello", null, "Sample mod hello", icon, () =>
            {
                uses++;
                // Asks for the API again, as a mod loaded later would: the plugin sends it to every handler, and this mod
                // ignores it (it logs "the API again").
                MyAPIGateway.Utilities.SendModMessage(Channel, Request);
                Log("actions wheel item used (" + uses + " times); asked for the API again");
                MyAPIGateway.Utilities.ShowNotification("Hello from the wheel", 1500);
            }, null, () => "Used " + uses + " times", null);
            registered = action && item;
            Log("registered: owner name " + named + ", action " + action + " (bound to '" + actionLabel(Owner + ".hello")
                + "'), wheel item " + item + " with icon " + icon);
        }

        public override void UpdateAfterSimulation()
        {
            // The HUD members are cheap; this reads them once a second and logs when VR or the HUD comes or goes.
            if (isActive == null || ++ticks % 60 != 0)
                return;
            bool active = isActive();
            bool visible = hudVisible != null && hudVisible();
            if (active != lastActive || visible != lastHudVisible)
            {
                lastActive = active;
                lastHudVisible = visible;
                LogHud();
            }
        }

        void LogHud()
        {
            if (isActive == null)
                return;
            Vector2D tangents = hudTangents != null ? hudTangents() : Vector2D.Zero;
            Vector3D centre = hudToWorld != null ? hudToWorld(Vector2D.Zero) : Vector3D.Zero;
            Log("IsActive " + isActive() + ", HudVisible " + (hudVisible != null && hudVisible())
                + ", HudDepth " + (hudDepth != null ? hudDepth() : 0.0).ToString("0.00")
                + ", HudTangents " + tangents.X.ToString("0.000") + " x " + tangents.Y.ToString("0.000")
                + ", HudToWorld(0,0) " + (centre == Vector3D.Zero ? "zero" : "set") + (registered ? ", registered" : ""));
        }

        T Get<T>(string name) where T : class
        {
            Delegate member;
            return api != null && api.TryGetValue(name, out member) ? member as T : null;
        }

        static void Log(string text)
        {
            MyLog.Default.WriteLineAndConsole("SevrSample: mod: " + text);
        }
    }
}
