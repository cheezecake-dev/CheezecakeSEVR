// Test hook for the drive's "content" command (see Drive).
using System;
using System.Runtime.CompilerServices;
using System.Text;
using Sandbox.Game.Components;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems.TextSurfaceScripts;
using Sandbox.Game.Localization;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using SpaceEngineersVR.Hands;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;
using VRage;
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
        /// Drive "content": lists the seated cockpit's screens' content. "content n &lt;script id|touch&gt;" first sets screen n
        /// to that script through the game's own properties (ContentType, Script), as its screen settings do: synced, and
        /// kept with the world.
        /// </summary>
        internal static bool DriveContent(string args, out string detail)
        {
            if (!(MySession.Static?.ControlledEntity is MyCockpit cockpit))
            {
                detail = "not seated in a cockpit";
                return false;
            }
            string[] words = (args ?? string.Empty).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int count = SurfaceCount(cockpit);
            if (words.Length >= 2)
            {
                MyTextPanelComponent panel = int.TryParse(words[0], out int area) ? Panel(cockpit, area) : null;
                if (panel == null)
                {
                    detail = $"no screen {words[0]} (0..{count - 1})";
                    return false;
                }
                string id = words[1].Equals("touch", StringComparison.OrdinalIgnoreCase) ? TouchToolbarScript.Id : words[1];
                if (MyTextSurfaceScriptFactory.Instance == null || !MyTextSurfaceScriptFactory.Instance.Scripts.TryGetValue(id, out _))
                {
                    detail = $"no script '{id}' among the screens' scripts";
                    return false;
                }
                string before = ContentOf(panel);
                panel.ContentType = ContentType.SCRIPT;
                panel.Script = id;
                Log.Info($"Touch toolbar: drive set screen {area} of {cockpit.BlockDefinition.Id.SubtypeName} {cockpit.EntityId} from {before} to {ContentOf(panel)}");
            }
            var text = new StringBuilder();
            for (int i = 0; i < count; i++)
                text.Append(i == 0 ? "" : "; ").Append(i).Append(' ').Append(ContentOf(Panel(cockpit, i)));
            detail = text.ToString();
            return true;
        }

        private static string ContentOf(MyTextPanelComponent panel) =>
            panel == null ? "none" : panel.ContentType == ContentType.SCRIPT ? $"script '{panel.Script}'" : panel.ContentType.ToString().ToLowerInvariant();
    }
}
