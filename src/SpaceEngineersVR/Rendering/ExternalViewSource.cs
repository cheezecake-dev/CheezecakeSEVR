using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRageMath;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// What the external view (<see cref="ExternalView"/>) shows, and the switching of it: off, third person (the chase
    /// camera behind and above the ship) or the view from one of the ship's camera blocks. The actions wheel works it
    /// (<see cref="Gui.ExternalViewEntries"/>); the choice is kept in the settings file like any other setting
    /// (<see cref="VRSettings.ExternalViewOn"/>, <see cref="VRSettings.ExternalViewIsCamera"/>, <see cref="VRSettings.ExternalViewCameraId"/>).
    /// <list type="bullet">
    /// <item>Which cameras: the game's own rule for the cameras its camera switch steps through while the player looks
    /// through one (MyGridCameraSystem.UpdateRelayedCameras): the cameras of the grids the seat's grid is connected to by
    /// antenna (the grid's own if none), working (functional, enabled and powered) and open to the local player, and that
    /// the game would not drop the view of at once (MyGridCameraSystem.CameraIsInRangeAndPlayerHasAccessLocal). On foot there
    /// is no seat's grid to start from, so there are none.</item>
    /// <item>How a camera is drawn: only its world matrix and field of view are read, for the external view's own third scene
    /// pass. The player's view is never moved to the camera, and the block is never made the active one, so nothing the game
    /// does for a camera view (the overlay, the zoom keys, the camera's activity count) happens.</item>
    /// <item>A camera that cannot be used now (gone, unpowered, out of antenna range, no access) leaves the screen on third
    /// person, with a short label, and the choice stays: it returns to the camera when that works again.</item>
    /// </list>
    /// </summary>
    internal static class ExternalViewSource
    {
        /// <summary>How often the chosen camera is checked against the game's rules while it is shown, seconds (each frame only checks that it still works).</summary>
        private const double CheckSeconds = 0.5;

        /// <summary>The longest a camera's name is on the wheel, characters.</summary>
        private const int NameLength = 22;

        private static readonly AccessTools.FieldRef<MyCameraBlock, float> FieldOfView = Resolve();

        private static MyCameraBlock current;
        private static double checkedAt = -1d;
        private static bool lost;
        private static int savePending;

        private static AccessTools.FieldRef<MyCameraBlock, float> Resolve()
        {
            try
            {
                return AccessTools.FieldRefAccess<MyCameraBlock, float>("m_fov");
            }
            catch (Exception e)
            {
                Log.Error(e, "External view: a camera block's field of view could not be read; cameras are drawn at the default");
                return null;
            }
        }

        private static double Seconds() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        // ---- Words. ----

        /// <summary>The chosen camera block's name as the wheel and the labels say it (shortened).</summary>
        public static string NameOf(MyCameraBlock camera)
        {
            string name = null;
            try
            {
                name = camera?.CustomName?.ToString();
            }
            catch
            {
            }
            return Short(string.IsNullOrWhiteSpace(name) ? "Camera" : name.Trim());
        }

        private static string Short(string name) => name.Length <= NameLength ? name : name.Substring(0, NameLength - 1) + "~";

        /// <summary>What the screen shows now, in a few words: "Off", "Third person", "Camera: Nose", or "Third person (Nose out of reach)".</summary>
        public static string Describe()
        {
            if (!ExternalView.On)
                return "Off";
            if (!VRSettings.ExternalViewIsCamera)
                return "Third person";
            string name = Short(VRSettings.ExternalViewCameraName);
            return lost ? $"Third person ({name} out of reach)" : $"Camera: {name}";
        }

        /// <summary>The camera the screen is showing (not one it fell back from), by entity id.</summary>
        public static bool IsShowing(MyCameraBlock camera) =>
            camera != null && ExternalView.On && VRSettings.ExternalViewIsCamera && !lost && VRSettings.ExternalViewCameraId == camera.EntityId;

        // ---- Which cameras. ----

        /// <summary>
        /// The cameras the player may view from where they are: the game's relayed list (connected grids in entity id order, the
        /// cameras of each that work and are open to the local player; the seat's own grid's if none), those the game would
        /// not drop at once. Empty on foot. Game thread.
        /// </summary>
        public static List<MyCameraBlock> Reachable()
        {
            var list = new List<MyCameraBlock>();
            try
            {
                MyCubeGrid grid = (MySession.Static?.ControlledEntity?.Entity as MyCubeBlock)?.CubeGrid;
                if (grid == null || MyAntennaSystem.Static == null)
                    return list;
                var connected = MyAntennaSystem.Static.GetConnectedGridsInfo(grid).ToList();
                connected.Sort((a, b) => a.EntityId.CompareTo(b.EntityId));
                foreach (MyAntennaSystem.BroadcasterInfo info in connected)
                {
                    if (MyEntities.TryGetEntityById(info.EntityId, out MyCubeGrid other))
                        AddWorking(other, list);
                }
                if (list.Count == 0)
                    AddWorking(grid, list);
                list.RemoveAll(camera => !MyGridCameraSystem.CameraIsInRangeAndPlayerHasAccessLocal(camera));
                return list;
            }
            catch (Exception e)
            {
                Log.Error(e, "External view: the ship's cameras could not be listed");
                return new List<MyCameraBlock>();
            }
        }

        private static void AddWorking(MyCubeGrid grid, List<MyCameraBlock> list)
        {
            foreach (MyTerminalBlock block in grid.GridSystems.TerminalSystem.Blocks)
            {
                if (block is MyCameraBlock camera && camera.IsWorking && camera.HasLocalPlayerAccess())
                    list.Add(camera);
            }
        }

        /// <summary>The words for each camera in a list: its name, and its grid's name after it when two share one.</summary>
        public static List<string> Names(List<MyCameraBlock> cameras)
        {
            var names = new List<string>(cameras.Count);
            foreach (MyCameraBlock camera in cameras)
                names.Add(NameOf(camera));
            for (int i = 0; i < cameras.Count; i++)
            {
                if (names.Count(name => name == names[i]) > 1)
                    names[i] = Short(NameOf(cameras[i])) + " (" + Short(cameras[i].CubeGrid?.DisplayName ?? "?") + ")";
            }
            return names;
        }

        // ---- Switching. ----

        /// <summary>Turns the screen off. What it showed is kept for the next time it is turned on.</summary>
        public static void TurnOff() => Switch("External view off", off: true, camera: null);

        /// <summary>Shows the chase camera, turning the screen on if it is off.</summary>
        public static void UseThirdPerson() => Switch("External view: third person", off: false, camera: null);

        /// <summary>Shows a camera block's view, turning the screen on if it is off.</summary>
        public static void UseCamera(MyCameraBlock camera) =>
            Switch($"External view: camera {NameOf(camera)}", off: false, camera: camera);

        /// <summary>
        /// The next source after the one chosen, round: third person, then each camera, then third person again. Turns the screen
        /// on if it is off. False when there is no camera to step to.
        /// </summary>
        public static bool Next()
        {
            List<MyCameraBlock> cameras = Reachable();
            if (cameras.Count == 0)
            {
                Log.Info("External view: next camera: there is none to step to");
                return false;
            }
            int at = 0;
            if (VRSettings.ExternalViewIsCamera)
            {
                long id = VRSettings.ExternalViewCameraId;
                int index = cameras.FindIndex(camera => camera.EntityId == id);
                at = index < 0 ? 0 : index + 1;
            }
            int to = (at + 1) % (cameras.Count + 1);
            if (to == 0)
                UseThirdPerson();
            else
                UseCamera(cameras[to - 1]);
            return true;
        }

        /// <summary>What the next step would show, for the wheel's words under "Next camera".</summary>
        public static string NextName(List<MyCameraBlock> cameras)
        {
            int at = 0;
            if (VRSettings.ExternalViewIsCamera)
            {
                long id = VRSettings.ExternalViewCameraId;
                int index = cameras.FindIndex(camera => camera.EntityId == id);
                at = index < 0 ? 0 : index + 1;
            }
            int to = (at + 1) % (cameras.Count + 1);
            return to == 0 ? "Third person" : "Camera: " + Names(cameras)[to - 1];
        }

        private static void Switch(string label, bool off, MyCameraBlock camera)
        {
            bool wasOn = ExternalView.On;
            if (ExternalView.Debug.HasValue)
                Log.Info($"External view: RenderDebug ExternalView={(ExternalView.Debug.Value ? 1 : 0)} is in force; the setting changes, the screen follows the override");
            if (camera != null)
            {
                VRSettings.SetExternalViewCamera(camera.EntityId, NameOf(camera));
                VRSettings.ExternalViewIsCamera = true;
                current = camera;
                checkedAt = Seconds();
                lost = false;
            }
            else if (!off)
            {
                VRSettings.ExternalViewIsCamera = false;
                lost = false;
            }
            VRSettings.ExternalViewOn = !off;
            if (wasOn && !off)
                ExternalView.Retime("the source was switched");
            Log.Info($"External view: {Describe()} ({(off ? "turned off" : wasOn ? "switched" : "turned on")})");
            HudToolbar.Notice(label);
            QueueSave();
        }

        /// <summary>Writes the settings file on a pool thread, so the frame that switched does not wait for the disk; changes made meanwhile make one more write.</summary>
        private static void QueueSave()
        {
            if (Interlocked.Exchange(ref savePending, 1) != 0)
                return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Interlocked.Exchange(ref savePending, 0);
                VRSettings.Save();
            });
        }

        // ---- The picture. ----

        /// <summary>
        /// Game thread, once a frame while the screen is on: the chosen camera's world matrix and vertical field of view
        /// (radians), when the source is a camera and it can be used. False for the chase camera: the source is third
        /// person, or the camera cannot be used now.
        /// </summary>
        public static bool TryCameraView(out MatrixD world, out float field)
        {
            world = MatrixD.Identity;
            field = 1f;
            if (!VRSettings.ExternalViewIsCamera)
            {
                current = null;
                lost = false;
                return false;
            }
            MyCameraBlock camera = Usable();
            if (camera == null)
                return false;
            MatrixD view = camera.GetViewMatrix();
            MatrixD.Invert(ref view, out world);
            field = FieldOfView != null ? FieldOfView(camera) : 1f;
            return true;
        }

        /// <summary>The chosen camera, if it can be used: found, working, and (every <see cref="CheckSeconds"/>) in range and open to the player.</summary>
        private static MyCameraBlock Usable()
        {
            long id = VRSettings.ExternalViewCameraId;
            if (current != null && (current.EntityId != id || current.Closed || current.MarkedForClose))
                current = null;
            double now = Seconds();
            if (current != null && current.IsWorking && now - checkedAt < CheckSeconds)
                return current;
            if (current == null && lost && now - checkedAt < CheckSeconds)
                return null;

            checkedAt = now;
            MyCameraBlock found = current;
            string why = null;
            if (found == null)
            {
                if (id == 0L)
                    why = "none is chosen";
                else if (!MyEntities.TryGetEntityById(id, out found) || found.Closed)
                {
                    found = null;
                    why = "it is not in this world";
                }
            }
            if (found != null && !found.CanUse())
            {
                why = !found.IsWorking ? "it is not working" : "it is out of antenna range or closed to you";
                found = null;
            }
            current = found;
            if (found == null)
            {
                if (!lost)
                {
                    lost = true;
                    string name = Short(VRSettings.ExternalViewCameraName);
                    Log.Info($"External view: camera {name} cannot be shown ({why}); third person instead");
                    ExternalView.Retime("the camera was lost");
                    HudToolbar.Notice($"Camera {name} out of reach: third person");
                }
                return null;
            }
            if (lost)
            {
                lost = false;
                Log.Info($"External view: camera {NameOf(found)} can be shown again");
                ExternalView.Retime("the camera came back");
                HudToolbar.Notice($"External view: camera {NameOf(found)}");
            }
            return found;
        }
    }
}
