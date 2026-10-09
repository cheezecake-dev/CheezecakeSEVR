using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Sandbox;
using Sandbox.Engine.Networking;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using Sandbox.Graphics;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Gui;
using SpaceEngineersVR.Rendering;
using SpaceEngineersVR.Tracking;
using VRage.FileSystem;
using VRage.Game.Entity.UseObject;
using VRage.Input;
using VRage.Utils;
using VRageMath;
using VRageRender;

namespace SpaceEngineersVR.Input
{
    /// <summary>
    /// Test aid: drives the game from a command file, with no keyboard or mouse input from the OS and without the
    /// window's focus, so a test can run while the PC is used for something else. Controlled by
    /// %APPDATA%\SpaceEngineers\SpaceEngineersVR.drive, read about ten times a second; no file, no drive, and nothing
    /// here does anything. Each line is one command, optionally after an id ("#12 control USE"); lines appended to the
    /// file run once each, in order, one after the other; a file rewritten with other text runs from its top. Each
    /// command logs one line when it ends: "Drive: #12 control USE done (...)" or "... failed (reason)".
    /// </summary>
    /// <remarks>
    /// Commands: load &lt;world&gt;, quit-to-menu, control &lt;MyControlsSpace id&gt; [seconds], key &lt;MyKeys&gt;[+&lt;MyKeys&gt;...]
    /// [seconds], escape, move x,z[,up] [seconds], look yaw,pitch, gui click x,y, gui hover x,y, gui release, type &lt;text&gt;,
    /// shot &lt;path&gt;, wait &lt;seconds&gt;, recentre (as the Options button; reports where the eye then is), sit [name] (into the nearest empty
    /// cockpit, named or not, as its door does), head (where the eye is,
    /// from the character's eye, and the play position). Everything goes in where the game reads it, after it has read (or, without focus,
    /// cleared) the real devices for the frame: the controls through the same questions the hands answer
    /// (<see cref="InputPatches"/>), keys into its keyboard state, typed text into its text input, the cursor and
    /// left button through the laser's path (<see cref="Laser"/>, <see cref="PanelCursor"/>). The window without focus
    /// would also have the game move the real cursor when a screen with a cursor opens (MyDX9Gui.SetMouseCursorVisibility);
    /// while the drive is on and the window has no focus that is dropped (<see cref="KeepsRealMouse"/>).
    /// </remarks>
    internal static class Drive
    {
        /// <summary>Debug: commands are not read or run (<see cref="RenderDebug"/> Drive=0).</summary>
        public static bool Off { get; set; }

        internal static readonly string ControlFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceEngineers", "SpaceEngineersVR.drive");

        private const int PollEveryFrames = 6;
        private const int TapFrames = 4;
        private const int SettleFrames = 10;
        private const double MenuWaitSeconds = 180, LoadSeconds = 300, QuitSeconds = 120, ShotSeconds = 10;

        private sealed class Command
        {
            public string Label, Verb, Args;
            public int Frames, Phase, Mark;
            public double Start, Since;
            public bool Ok;
            public string Detail, FocusBefore, Under, Path;
            public MyStringId Control;
            public MyKeys[] Keys;
            public double Hold = -1;
            public Vector2 Point, Gui;
            public Vector3 Move;
            public Vector3D From;
            public MatrixD Pose;
            public float Head;
            public bool Jetpack;
            public MyCharacter Character;
            public MyGuiControlTextbox Box;
        }

        private static readonly Queue<string> queue = new Queue<string>();
        private static Command current;
        private static bool active, everActive, focused, resumeSkips;
        private static string consumed = string.Empty, pendingTail;
        private static int frame, errors;
        private static DateTime processStart;

        // What the running command holds this frame, read by the game's questions in the same frame.
        private static MyStringId? controlId;
        private static bool controlNow, controlBefore;
        private static MyKeys[] keys;
        private static bool keysNow;
        private static Vector3 moveNow;
        private static Vector2 rotationNow;
        private static bool guiHeld, guiButton;
        private static Vector2 guiCursor;
        private static string pendingText;
        private static bool textInjected;

        // The game's own state, reached once.
        private static Dictionary<string, MyStringId> controls;
        private static AccessTools.FieldRef<MyVRageInput, List<char>> textInput;
        private static FieldInfo keyboardField;
        private static MethodInfo setKey;
        private static readonly object[] keyArgs = new object[2];

        // The shot, between the game thread and the render thread.
        private static readonly object shotGate = new object();
        private static string shotWanted, shotTaken, shotError, shotGeometry;
        private static int shotSerial;
        private static bool shotHooked;

        private static readonly List<MyGuiControlBase> underCursor = new List<MyGuiControlBase>();

        /// <summary>The drive is reading commands: the file is there and Drive=0 is not set.</summary>
        public static bool Active => active;

        /// <summary>The game must not move the real cursor: the drive is on and the window has no focus.</summary>
        public static bool KeepsRealMouse => active && !focused;

        /// <summary>Each hook on its own: the drive must never take another patch down with it.</summary>
        public static void Patch(Harmony harmony)
        {
            try
            {
                processStart = Process.GetCurrentProcess().StartTime.ToUniversalTime();
            }
            catch (Exception)
            {
                processStart = DateTime.UtcNow;
            }

            try
            {
                textInput = AccessTools.FieldRefAccess<MyVRageInput, List<char>>("m_currentTextInput");
                keyboardField = AccessTools.Field(typeof(MyVRageInput), "m_keyboardState");
                setKey = keyboardField == null ? null : AccessTools.Method(keyboardField.FieldType, "SetKey", new[] { typeof(MyKeys), typeof(bool) });
            }
            catch (Exception e)
            {
                Log.Error(e, "Drive: the game's keyboard and text input could not be reached; key and type will fail");
            }

            try
            {
                Type render = typeof(MyDX11Render).Assembly.GetType("VRageRender.MyRender11", throwOnError: true);
                harmony.Patch(AccessTools.Method(render, "Present"), prefix: new HarmonyMethod(typeof(Drive), nameof(BeforePresent)));
                shotHooked = DriveShot.Hook(render);
            }
            catch (Exception e)
            {
                Log.Error(e, "Drive: the renderer's present could not be hooked; shot will fail");
            }
        }

        /// <summary>
        /// Game thread, every frame, right after the game updated its input (InputPatches.FrameStart): reads the file,
        /// runs the command at the head of the queue, and puts what it holds into the game's input.
        /// </summary>
        public static void Frame(MyVRageInput input, bool gameFocused)
        {
            focused = gameFocused;
            controlBefore = controlNow;
            rotationNow = Vector2.Zero;
            try
            {
                if (frame++ % PollEveryFrames == 0)
                    Poll();
                if (active)
                    Run();
                Inject(input, gameFocused);
            }
            catch (Exception e)
            {
                if (errors++ < 5)
                    Log.Error(e, "Drive failed this frame");
                if (current != null)
                    Finish(current, false, "error: " + e.Message);
                Release();
            }
        }

        /// <summary>The control the running command holds, in the state asked about (pressed, newly pressed, newly released).</summary>
        public static bool Wants(MyStringId control, MyControlStateType type)
        {
            if (!controlId.HasValue || controlId.Value != control)
                return false;
            switch (type)
            {
                case MyControlStateType.NEW_PRESSED:
                case MyControlStateType.NEW_PRESSED_REPEATING:
                    return controlNow && !controlBefore;
                case MyControlStateType.PRESSED:
                    return controlNow;
                case MyControlStateType.NEW_RELEASED:
                    return !controlNow && controlBefore;
                default:
                    return false;
            }
        }

        /// <summary>Adds a move command's walk to the game's movement (x right, y up, z back, as the keys give it).</summary>
        public static void AddMovement(ref Vector3 delta)
        {
            if (moveNow == Vector3.Zero)
                return;
            Vector3 v = delta + new Vector3(moveNow.X, moveNow.Y, -moveNow.Z);
            delta = new Vector3(MathHelper.Clamp(v.X, -1f, 1f), MathHelper.Clamp(v.Y, -1f, 1f), MathHelper.Clamp(v.Z, -1f, 1f));
        }

        /// <summary>Adds a look command's turn to the game's rotation (x pitch, y yaw), for the one frame it is given in.</summary>
        public static void AddRotation(ref Vector2 rotation) => rotation += rotationNow;

        /// <summary>A gui command has the cursor: where on the GUI, in GUI pixels, and whether the left button is down.</summary>
        public static bool TryCursor(out Vector2 onGui, out bool left)
        {
            onGui = guiCursor;
            left = guiButton;
            return active && guiHeld;
        }

        // ---- The file. ----

        private static void Poll()
        {
            string text = null;
            DateTime written = default;
            try
            {
                if (!Off && File.Exists(ControlFile))
                {
                    text = File.ReadAllText(ControlFile);
                    written = File.GetLastWriteTimeUtc(ControlFile);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return;
            }

            if (text == null)
            {
                if (active)
                {
                    Stop(Off ? "Drive=0" : "the drive file was removed");
                    resumeSkips = Off;
                }
                return;
            }
            text = text.TrimStart('\uFEFF');
            if (!active)
            {
                active = true;
                consumed = string.Empty;
                pendingTail = null;
                // Lines already there are not run: a file left from an earlier run, or one Drive=0 was set over (it
                // would otherwise run again from its top).
                bool early = !everActive && written < processStart;
                if ((early || resumeSkips) && text.Trim().Length > 0)
                {
                    consumed = text;
                    Log.Info($"Drive on: {ControlFile}; its {Lines(text).Count} line(s) were written {(early ? "before this run" : "before Drive=0 was lifted")}, so they are skipped (append new ones)");
                }
                else
                    Log.Info($"Drive on: {ControlFile}");
                everActive = true;
                resumeSkips = false;
            }
            if (!text.StartsWith(consumed, StringComparison.Ordinal))
            {
                consumed = string.Empty;
                pendingTail = null;
                Log.Info("Drive: the file was rewritten; running it from the top");
            }

            string rest = text.Substring(consumed.Length);
            int end = rest.LastIndexOf('\n');
            string complete = end >= 0 ? rest.Substring(0, end + 1) : string.Empty;
            string tail = rest.Substring(end + 1);
            // A last line without its newline runs once it has stayed the same for a poll: it may still be being written.
            if (tail.Trim().Length > 0)
            {
                if (tail == pendingTail)
                {
                    complete += tail;
                    pendingTail = null;
                }
                else
                    pendingTail = tail;
            }
            if (complete.Length == 0)
                return;
            consumed += complete;
            foreach (string line in Lines(complete))
                queue.Enqueue(line);
        }

        private static List<string> Lines(string text)
        {
            var lines = new List<string>();
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length > 0 && !line.StartsWith("//", StringComparison.Ordinal))
                    lines.Add(line);
            }
            return lines;
        }

        private static void Stop(string reason)
        {
            if (current != null)
                Finish(current, false, "aborted: " + reason);
            queue.Clear();
            Release();
            active = false;
            consumed = string.Empty;
            pendingTail = null;
            Log.Info($"Drive off ({reason})");
        }

        /// <summary>Lets go of everything a command holds.</summary>
        private static void Release()
        {
            controlId = null;
            controlNow = controlBefore = false;
            keys = null;
            keysNow = false;
            moveNow = Vector3.Zero;
            rotationNow = Vector2.Zero;
            guiHeld = guiButton = false;
            pendingText = null;
            lock (shotGate)
                shotWanted = null;
        }

        // ---- Running commands. ----

        private static void Run()
        {
            if (current == null)
            {
                if (queue.Count == 0)
                    return;
                current = Begin(queue.Dequeue());
            }
            Command c = current;
            bool finished = Tick(c);
            c.Frames++;
            if (finished)
                Finish(c, c.Ok, c.Detail);
        }

        private static Command Begin(string line)
        {
            string text = line;
            string id = null;
            if (text.StartsWith("#", StringComparison.Ordinal))
            {
                int gap = text.IndexOf(' ');
                id = gap < 0 ? text : text.Substring(0, gap);
                text = gap < 0 ? string.Empty : text.Substring(gap + 1).Trim();
            }
            int space = text.IndexOf(' ');
            return new Command
            {
                Label = id == null ? text : id + " " + text,
                Verb = (space < 0 ? text : text.Substring(0, space)).ToLowerInvariant(),
                Args = space < 0 ? string.Empty : text.Substring(space + 1).Trim(),
                Start = Now(),
            };
        }

        private static void Finish(Command c, bool ok, string detail)
        {
            Log.Info($"Drive: {c.Label} {(ok ? "done" : "failed")}" + (string.IsNullOrEmpty(detail) ? string.Empty : $" ({detail})"));
            if (current == c)
                current = null;
        }

        private static bool Done(Command c, string detail)
        {
            c.Ok = true;
            c.Detail = detail;
            return true;
        }

        private static bool Fail(Command c, string reason)
        {
            c.Ok = false;
            c.Detail = reason;
            return true;
        }

        /// <summary>One frame of a command; true when it has ended (Ok and Detail say how).</summary>
        private static bool Tick(Command c)
        {
            switch (c.Verb)
            {
                case "load": return Load(c);
                case "quit-to-menu": return Quit(c);
                case "control": return Control(c);
                case "key": return Key(c, c.Args);
                case "escape": return Key(c, "Escape");
                case "move": return Move(c);
                case "look": return Look(c);
                case "gui": return GuiCommand(c);
                case "type": return Type(c);
                case "shot": return Shot(c);
                case "wait": return Wait(c);
                case "recentre": return Recentre(c);
                case "sit": return Sit(c);
                case "head": return Done(c, HeadReport());
                case "":
                    return Fail(c, "no command");
                default:
                    return Fail(c, $"unknown command '{c.Verb}' (load, quit-to-menu, control, key, escape, move, look, gui, type, shot, wait, recentre, sit, head)");
            }
        }

        private static MyCockpit sitIn;

        /// <summary>
        /// Seats the character in a cockpit as the cockpit's door does when used (<see cref="MyCockpit.RequestUse"/>,
        /// Manipulate), from wherever it is: the nearest empty one whose grid's name or own name has the text in it, or the
        /// nearest of all without a text. For the seated tests in the disposable worlds; it does not walk there.
        /// </summary>
        private static bool Sit(Command c)
        {
            MyCharacter character = MySession.Static?.LocalCharacter;
            if (c.Frames == 0)
            {
                if (character == null || character.IsDead)
                    return Fail(c, "no living character");
                string name = Unquote(c.Args);
                Vector3D at = character.PositionComp.GetPosition();
                List<MyCockpit> empty = MyEntities.GetEntities().OfType<MyCubeGrid>()
                    .SelectMany(grid => grid.GetFatBlocks().OfType<MyCockpit>())
                    .Where(cockpit => cockpit.Pilot == null)
                    .OrderBy(cockpit => Vector3D.Distance(cockpit.PositionComp.GetPosition(), at))
                    .ToList();
                bool Named(MyCockpit cockpit) => name.Length == 0
                    || cockpit.CubeGrid.DisplayName?.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0
                    || cockpit.CustomName?.ToString().IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0;
                string Describe(MyCockpit cockpit) =>
                    $"{cockpit.BlockDefinition.Id.SubtypeName} '{cockpit.CustomName}' on '{cockpit.CubeGrid.DisplayName}', {Vector3D.Distance(cockpit.PositionComp.GetPosition(), at):F0} m";
                sitIn = empty.FirstOrDefault(Named);
                if (sitIn == null)
                    return Fail(c, $"no empty cockpit{(name.Length > 0 ? $" named '{name}'" : "")}; the nearest: {string.Join("; ", empty.Take(6).Select(Describe))}");
                c.Path = Describe(sitIn);
                sitIn.RequestUse(UseActionEnum.Manipulate, character);
                return false;
            }
            if (sitIn != null && sitIn.Pilot == character)
                return Done(c, $"seated in the {c.Path}");
            return Elapsed(c) > 5.0 && Fail(c, $"not seated in the {c.Path} after 5 s");
        }

        /// <summary>Recentres the head on all axes, as the Options button does, and once it is taken reports where the eye is.</summary>
        private static bool Recentre(Command c)
        {
            if (c.Frames == 0)
            {
                if (!GameHead.Take())
                    return Fail(c, "no head is tracked (a headset or the fake head)");
                GameHead.Recentre("the drive");
                return false;
            }
            GameHead.Take();
            if (GameHead.RecentrePending)
                return Elapsed(c) > 5.0 && Fail(c, "no head came in 5 s to take it from");
            return Done(c, HeadReport());
        }

        /// <summary>
        /// Where the game puts the eye this frame, from the character's eye, and the play position; in a seat, also where the
        /// seat holds it (<see cref="CockpitHead"/>).
        /// </summary>
        private static string HeadReport()
        {
            if (!GameHead.Take())
                return "no head tracked";
            Vector3 at = GameHead.Head.Translation;
            MyCharacter character = Character();
            string crouch = character == null ? "no character" : (character.MovementFlags & MyCharacterMovementFlags.Crouch) != 0 ? "crouching" : "not crouching";
            string report = $"eye at ({at.X:F2}, {at.Y:F2}, {at.Z:F2}) m from the character's eye (x right, y up, z back), yaw {MathHelper.ToDegrees(GameHead.Yaw):F0} deg, " +
                            $"{(VRSettings.SeatedPlay ? "seated" : "standing")} play, {(GameHead.SeatCentred ? "placed from the sit-down" : "placed from the on-foot centre")}, {crouch}";
            ulong frame = MySandboxGame.Static.SimulationFrameCounter;
            if (BodyGuard.LastFrame > 0 && BodyGuard.LastFrame + 2 >= frame)
                report += $"; on foot: the eye is {BodyGuard.LastEyeY:F3} m from the standing character's eye height";
            if (CockpitHead.LastFrame > 0 && CockpitHead.LastFrame + 2 >= frame)
            {
                Vector3 head = CockpitHead.LastOffset, held = CockpitHead.LastHeld;
                float behind = CockpitHead.LastFromHead.Z + held.Z;
                report += $"; in a seat: head ({head.X:F3}, {head.Y:F3}, {head.Z:F3}) m from the seat's eye, eye held at " +
                          $"({held.X:F3}, {held.Y:F3}, {held.Z:F3}), {behind * 100f:F1} cm behind the pilot's head";
            }
            return report + $"; focus {Focus()}";
        }

        private static bool Wait(Command c)
        {
            if (c.Frames == 0 && !TrySeconds(c.Args, out c.Hold))
                return Fail(c, "wait needs seconds, e.g. 'wait 2'");
            return Elapsed(c) >= c.Hold && Done(c, $"waited {Elapsed(c):0.00} s");
        }

        private static bool Load(Command c)
        {
            switch (c.Phase)
            {
                case 0:
                    if (c.Args.Length == 0)
                        return Fail(c, "load needs a world name, e.g. 'load My World'");
                    if (MySession.Static != null && MyGuiScreenGamePlay.Static != null)
                        return Fail(c, $"a world is already loaded ({MySession.Static.Name}); quit-to-menu first");
                    if (!AtMainMenu())
                    {
                        if (Elapsed(c) > MenuWaitSeconds)
                            return Fail(c, $"the main menu did not come up in {MenuWaitSeconds:0} s (focus {Focus()})");
                        return false;
                    }
                    string path = FindWorld(Unquote(c.Args), out string why);
                    if (path == null)
                        return Fail(c, why);
                    c.Path = path;
                    c.Since = Now();
                    c.Phase = 1;
                    Log.Info($"Drive: {c.Label}: loading {path}");
                    MySandboxGame.Static.Invoke(() => MySessionLoader.LoadSingleplayerSession(path), "SpaceEngineersVR drive load");
                    return false;
                default:
                    double loading = Now() - c.Since;
                    if (Loaded())
                        return Done(c, $"loaded in {loading:0.0} s; focus {Focus()}");
                    if (loading > 5 && MySession.Static == null && !MyScreenManager.ExistsScreenOfType(typeof(MyGuiScreenLoading))
                        && MyScreenManager.GetScreenWithFocus() is MyGuiScreenMessageBox)
                        return Fail(c, "the game showed a message box instead of loading (shot it to read it)");
                    if (loading > LoadSeconds)
                        return Fail(c, $"not loaded after {LoadSeconds:0} s (focus {Focus()})");
                    return false;
            }
        }

        private static bool Quit(Command c)
        {
            switch (c.Phase)
            {
                case 0:
                    if (MySession.Static == null)
                        return Done(c, "no world was loaded; focus " + Focus());
                    c.Since = Now();
                    c.Phase = 1;
                    // Unloaded without saving, as the pause menu's exit does after "don't save".
                    MySandboxGame.Static.Invoke(MySessionLoader.UnloadAndExitToMenu, "SpaceEngineersVR drive quit");
                    return false;
                default:
                    if (AtMainMenu())
                        return Done(c, $"at the main menu after {Now() - c.Since:0.0} s");
                    return Now() - c.Since > QuitSeconds && Fail(c, $"not at the main menu after {QuitSeconds:0} s (focus {Focus()})");
            }
        }

        private static bool Control(Command c)
        {
            switch (c.Phase)
            {
                case 0:
                    string[] parts = c.Args.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 1 || parts.Length > 2)
                        return Fail(c, "control needs a MyControlsSpace id and optional seconds, e.g. 'control USE' or 'control JUMP 0.5'");
                    if (!Controls().TryGetValue(parts[0], out c.Control))
                        return Fail(c, $"'{parts[0]}' is not a MyControlsSpace control");
                    if (parts.Length == 2 && !TrySeconds(parts[1], out c.Hold))
                        return Fail(c, $"'{parts[1]}' is not seconds");
                    c.FocusBefore = Focus();
                    controlId = c.Control;
                    controlNow = true;
                    c.Phase = 1;
                    return false;
                case 1:
                    if (c.Hold >= 0 ? Elapsed(c) >= c.Hold : c.Frames >= TapFrames)
                    {
                        controlNow = false;
                        c.Mark = c.Frames;
                        c.Phase = 2;
                    }
                    return false;
                default:
                    if (c.Frames - c.Mark < SettleFrames)
                        return false;
                    controlId = null;
                    return Done(c, $"{Held(c)}; focus {c.FocusBefore} -> {Focus()}");
            }
        }

        private static bool Key(Command c, string args)
        {
            switch (c.Phase)
            {
                case 0:
                    if (setKey == null || keyboardField == null)
                        return Fail(c, "the game's keyboard state could not be reached at start");
                    string[] parts = args.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 1 || parts.Length > 2)
                        return Fail(c, "key needs MyKeys names joined by + and optional seconds, e.g. 'key Enter' or 'key LeftAlt+F10'");
                    var list = new List<MyKeys>();
                    foreach (string name in parts[0].Split('+'))
                    {
                        if (!Enum.TryParse(name, ignoreCase: true, out MyKeys key) || key == MyKeys.None)
                            return Fail(c, $"'{name}' is not a MyKeys name");
                        list.Add(key);
                    }
                    if (parts.Length == 2 && !TrySeconds(parts[1], out c.Hold))
                        return Fail(c, $"'{parts[1]}' is not seconds");
                    c.Keys = list.ToArray();
                    c.FocusBefore = Focus();
                    keys = c.Keys;
                    keysNow = true;
                    c.Phase = 1;
                    return false;
                case 1:
                    if (c.Hold >= 0 ? Elapsed(c) >= c.Hold : c.Frames >= TapFrames)
                    {
                        keysNow = false;
                        c.Mark = c.Frames;
                        c.Phase = 2;
                    }
                    return false;
                default:
                    if (c.Frames - c.Mark < SettleFrames)
                        return false;
                    keys = null;
                    return Done(c, $"{Held(c)}; focus {c.FocusBefore} -> {Focus()}");
            }
        }

        private static bool Move(Command c)
        {
            switch (c.Phase)
            {
                case 0:
                    string[] parts = c.Args.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 1 || parts.Length > 2 || !TryNumbers(parts[0], 2, 3, out float[] n))
                        return Fail(c, "move needs x,z[,up] (-1..1, x right, z forward) and optional seconds, e.g. 'move 0,1 2'");
                    if (parts.Length == 2 && !TrySeconds(parts[1], out c.Hold))
                        return Fail(c, $"'{parts[1]}' is not seconds");
                    c.Move = new Vector3(MathHelper.Clamp(n[0], -1f, 1f), n.Length == 3 ? MathHelper.Clamp(n[2], -1f, 1f) : 0f, MathHelper.Clamp(n[1], -1f, 1f));
                    moveNow = c.Move;
                    if (c.Hold < 0)
                        return Done(c, c.Move == Vector3.Zero ? "stopped" : "moving until the next move");
                    c.Character = Character();
                    if (c.Character != null)
                        c.From = c.Character.PositionComp.GetPosition();
                    c.Phase = 1;
                    return false;
                default:
                    if (Elapsed(c) < c.Hold)
                        return false;
                    moveNow = Vector3.Zero;
                    MyCharacter character = Character();
                    string moved = character != null && character == c.Character
                        ? $"moved {Vector3D.Distance(c.From, character.PositionComp.GetPosition()):0.00} m" : "no character to measure";
                    return Done(c, $"{moved} in {Elapsed(c):0.00} s");
            }
        }

        /// <summary>
        /// A turn in one frame of rotation input. The character turns by -y * RotationSpeed * 0.02 radians (on foot and on
        /// the jetpack); it pitches its head by -x * RotationSpeed degrees on foot, its body by -x * RotationSpeed * 0.02
        /// radians on the jetpack (MyCharacter.Rotate, MyCharacterJetpackComponent.MoveAndRotate).
        /// </summary>
        private static bool Look(Command c)
        {
            switch (c.Phase)
            {
                case 0:
                    if (!TryNumbers(c.Args, 2, 2, out float[] n))
                        return Fail(c, "look needs yaw,pitch in degrees (+ left, + up), e.g. 'look 30,-10'");
                    MyCharacter character = Character();
                    float speed = character?.RotationSpeed ?? 0.13f;
                    if (speed <= 0f)
                        return Fail(c, "the character cannot turn (rotation speed 0)");
                    c.Character = character;
                    c.Jetpack = character != null && character.JetpackRunning;
                    if (character != null)
                    {
                        c.Pose = character.WorldMatrix;
                        c.Head = character.HeadLocalXAngle;
                    }
                    float yaw = MathHelper.ToRadians(n[0]);
                    float pitch = c.Jetpack ? MathHelper.ToRadians(n[1]) / (speed * 0.02f) : n[1] / speed;
                    rotationNow = new Vector2(-pitch, -yaw / (speed * 0.02f));
                    c.Point = new Vector2(n[0], n[1]);
                    c.Phase = 1;
                    return false;
                default:
                    if (c.Frames < 6)
                        return false;
                    MyCharacter now = Character();
                    if (now == null || now != c.Character)
                        return Done(c, "no character to measure");
                    Vector3D up = c.Pose.Up, before = c.Pose.Forward, after = now.WorldMatrix.Forward;
                    Vector3D flat = Vector3D.Normalize(after - up * Vector3D.Dot(after, up));
                    double yawTurned = MathHelper.ToDegrees(Math.Atan2(Vector3D.Dot(Vector3D.Cross(before, flat), up), Vector3D.Dot(before, flat)));
                    double pitchTurned = c.Jetpack
                        ? MathHelper.ToDegrees(Math.Asin(MathHelper.Clamp(Vector3D.Dot(after, up), -1.0, 1.0)))
                        : now.HeadLocalXAngle - c.Head;
                    return Done(c, $"asked yaw {c.Point.X:0.#}, pitch {c.Point.Y:0.#}; turned yaw {yawTurned:0.0}, pitch {pitchTurned:0.0} degrees{(c.Jetpack ? ", jetpack" : string.Empty)}");
            }
        }

        private static bool GuiCommand(Command c)
        {
            string[] parts = c.Args.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string what = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;
            if (what == "release")
            {
                guiHeld = guiButton = false;
                return Done(c, "cursor let go");
            }
            bool click = what == "click";
            if (!click && what != "hover")
                return Fail(c, "gui needs click x,y, hover x,y or release (x,y in pixels of a shot)");
            switch (c.Phase)
            {
                case 0:
                    if (parts.Length != 2 || !TryNumbers(parts[1], 2, 2, out float[] n))
                        return Fail(c, $"gui {what} needs x,y in pixels of a shot, e.g. 'gui {what} 640,360'");
                    if (MySandboxGame.Static == null || !MySandboxGame.Static.IsCursorVisible)
                        return Fail(c, $"no cursor showing: no screen wants the mouse (focus {Focus()})");
                    c.Point = new Vector2(n[0], n[1]);
                    c.Gui = PanelCursor.WindowToGui(c.Point);
                    c.FocusBefore = Focus();
                    guiCursor = c.Gui;
                    guiHeld = true;
                    guiButton = false;
                    c.Phase = 1;
                    return false;
                case 1:
                    // Two frames for the game to read the cursor where it is now, then what it is over.
                    if (c.Frames < 2)
                        return false;
                    c.Under = Under();
                    if (!click)
                        return Done(c, $"GUI {c.Gui.X:0},{c.Gui.Y:0}; over {c.Under}");
                    guiButton = true;
                    c.Mark = c.Frames;
                    c.Phase = 2;
                    return false;
                case 2:
                    if (c.Frames - c.Mark >= TapFrames)
                    {
                        guiButton = false;
                        c.Mark = c.Frames;
                        c.Phase = 3;
                    }
                    return false;
                default:
                    return c.Frames - c.Mark >= SettleFrames
                           && Done(c, $"GUI {c.Gui.X:0},{c.Gui.Y:0}; on {c.Under}; focus {c.FocusBefore} -> {Focus()}");
            }
        }

        private static bool Type(Command c)
        {
            switch (c.Phase)
            {
                case 0:
                    if (textInput == null)
                        return Fail(c, "the game's text input could not be reached at start");
                    string text = Unquote(c.Args);
                    if (text.Length == 0)
                        return Fail(c, "type needs text, e.g. 'type hello' (quote it to keep spaces at the ends)");
                    MyGuiScreenBase screen = MyScreenManager.GetScreenWithFocus();
                    if (!(screen is VRKeyboardScreen))
                    {
                        c.Box = screen?.FocusedControl as MyGuiControlTextbox;
                        if (c.Box == null)
                            return Fail(c, $"no text box has focus (focus {Focus()}, control {screen?.FocusedControl?.GetType().Name ?? "none"}); click one first");
                    }
                    c.Path = text;
                    pendingText = text;
                    c.Phase = 1;
                    return false;
                default:
                    if (c.Frames < 3)
                        return false;
                    return Done(c, c.Box != null
                        ? $"{c.Path.Length} characters; the text box now holds '{c.Box.Text}'"
                        : $"{c.Path.Length} characters into the on-screen keyboard");
            }
        }

        private static bool Shot(Command c)
        {
            switch (c.Phase)
            {
                case 0:
                    if (!shotHooked)
                        return Fail(c, "the renderer could not be hooked at start");
                    string path = Unquote(c.Args);
                    if (path.Length == 0)
                        return Fail(c, "shot needs a file path, e.g. 'shot C:\\temp\\menu.png'");
                    if (!Path.IsPathRooted(path))
                        path = Path.Combine(Path.GetDirectoryName(ControlFile), path);
                    if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                        path += ".png";
                    c.Path = Path.GetFullPath(path);
                    lock (shotGate)
                    {
                        shotWanted = c.Path;
                        shotTaken = shotError = null;
                        shotSerial++;
                    }
                    c.Phase = 1;
                    return false;
                default:
                    string taken, error;
                    lock (shotGate)
                    {
                        taken = shotTaken;
                        error = shotError;
                    }
                    if (error != null)
                        return Fail(c, error);
                    if (taken != null)
                        return Done(c, $"{PngSize(taken)} {taken}");
                    return Elapsed(c) > ShotSeconds && Fail(c, $"the renderer did not present a frame in {ShotSeconds:0} s");
            }
        }

        /// <summary>
        /// Render thread, just before the window's back buffer is presented: the requested shot. The pixels are copied out
        /// here through a staging texture of our own (<see cref="DriveShot"/>); the PNG is encoded and written on another
        /// thread, so the frame waits for the copy only.
        /// </summary>
        private static void BeforePresent()
        {
            string path;
            int serial;
            lock (shotGate)
            {
                path = shotWanted;
                shotWanted = null;
                serial = shotSerial;
            }
            if (path == null)
                return;
            string error = null;
            try
            {
                DriveShot.Pixels pixels = DriveShot.Read(out error);
                if (pixels != null)
                {
                    ThreadPool.QueueUserWorkItem(_ => EncodeShot(serial, path, pixels));
                    return;
                }
            }
            catch (Exception e)
            {
                error = "error: " + e.Message;
            }
            FinishShot(serial, null, error ?? "nothing was read");
        }

        /// <summary>A pool thread: the PNG from the copy. Nothing here may throw, or the game would go down with the thread.</summary>
        private static void EncodeShot(int serial, string path, DriveShot.Pixels pixels)
        {
            string error = null;
            try
            {
                DriveShot.WritePng(path, pixels);
                lock (shotGate)
                {
                    if (serial == shotSerial && pixels.Source != shotGeometry)
                    {
                        shotGeometry = pixels.Source;
                        Log.Info($"Drive: shots save {pixels.Width}x{pixels.Height} of the {pixels.Source}");
                    }
                }
            }
            catch (Exception e)
            {
                error = "the PNG could not be written: " + e.Message;
            }
            FinishShot(serial, error == null ? path : null, error);
        }

        /// <summary>Ends the shot asked for as number <paramref name="serial"/>; one that was given up on is not answered.</summary>
        private static void FinishShot(int serial, string path, string error)
        {
            lock (shotGate)
            {
                if (serial != shotSerial)
                    return;
                shotTaken = error == null ? path : null;
                shotError = error;
            }
        }

        // ---- Into the game's input. ----

        private static void Inject(MyVRageInput input, bool gameFocused)
        {
            if (input == null)
                return;
            if (keysNow && keys != null && setKey != null)
            {
                // Into this frame's state, after the game has read (or, without focus, cleared) the real keyboard: the
                // game moves it to 'previous' next frame, so pressed, new pressed and released agree.
                object keyboard = keyboardField.GetValue(input);
                foreach (MyKeys key in keys)
                {
                    keyArgs[0] = key;
                    keyArgs[1] = true;
                    setKey.Invoke(keyboard, keyArgs);
                }
            }
            if (textInput == null)
                return;
            if (pendingText != null)
            {
                // The characters typed this frame. Without focus the game no longer swaps this list with the window's
                // buffer, so what it last held is cleared first, and again the frame after.
                List<char> typed = textInput(input);
                if (!gameFocused)
                    typed.Clear();
                typed.AddRange(pendingText);
                pendingText = null;
                textInjected = true;
            }
            else if (textInjected)
            {
                if (!gameFocused)
                    textInput(input).Clear();
                textInjected = false;
            }
        }

        // ---- Helpers. ----

        private static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        private static double Elapsed(Command c) => Now() - c.Start;

        private static string Held(Command c) => c.Hold >= 0 ? $"held {Elapsed(c):0.00} s" : $"tapped for {TapFrames} frames";

        /// <summary>The screen with focus; a screen that is closing still has it for a few frames, and says so.</summary>
        private static string Focus()
        {
            MyGuiScreenBase screen = MyScreenManager.GetScreenWithFocus();
            if (screen == null)
                return "none";
            return screen.State == MyGuiScreenState.CLOSING || screen.State == MyGuiScreenState.CLOSED
                ? screen.GetType().Name + " (closing)" : screen.GetType().Name;
        }

        private static MyCharacter Character() => MySession.Static?.ControlledEntity as MyCharacter;

        private static bool AtMainMenu() =>
            MySession.Static == null && MyPerGameSettings.GUI.MainMenu != null && MyScreenManager.ExistsScreenOfType(MyPerGameSettings.GUI.MainMenu)
            && !MyScreenManager.ExistsScreenOfType(typeof(MyGuiScreenLoading));

        private static bool Loaded() =>
            MySession.Static != null && MySession.Static.Ready && MyGuiScreenGamePlay.Static != null
            && !MyScreenManager.ExistsScreenOfType(typeof(MyGuiScreenLoading));

        /// <summary>A local save: the folder of that name in the saves, or else the one world whose session has that name.</summary>
        private static string FindWorld(string name, out string why)
        {
            why = null;
            if (name.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 || name.Contains(".."))
            {
                why = "load takes a save's name, not a path";
                return null;
            }
            string saves = MyFileSystem.SavesPath;
            string folder = Path.Combine(saves, name);
            if (File.Exists(Path.Combine(folder, "Sandbox.sbc")))
                return folder;
            List<string> named = MyLocalCache.GetAvailableWorldInfos()
                .Where(w => w.Item2 != null && string.Equals(w.Item2.SessionName, name, StringComparison.OrdinalIgnoreCase))
                .Select(w => w.Item1).ToList();
            if (named.Count == 1)
                return named[0];
            why = named.Count == 0 ? $"no local save named '{name}' in {saves}" : $"{named.Count} saves have the session name '{name}'; use the folder name";
            return null;
        }

        /// <summary>What the cursor is over on the screen with focus: the control drawn on top.</summary>
        private static string Under()
        {
            MyGuiScreenBase screen = MyScreenManager.GetScreenWithFocus();
            if (screen == null)
                return "nothing (no screen has focus)";
            underCursor.Clear();
            screen.GetControlsUnderMouseCursor(MyGuiManager.MouseCursorPosition, underCursor, visibleOnly: true);
            MyGuiControlBase top = underCursor.Count > 0 ? underCursor[underCursor.Count - 1] : null;
            underCursor.Clear();
            switch (top)
            {
                case null: return "nothing";
                case MyGuiControlButton button: return $"button '{button.Text ?? button.Name}'";
                case MyGuiControlLabel label: return $"label '{label.Text}'";
                case MyGuiControlTextbox box: return $"text box '{box.Text}'";
                default: return top.GetType().Name + (string.IsNullOrEmpty(top.Name) ? string.Empty : $" '{top.Name}'");
            }
        }

        private static Dictionary<string, MyStringId> Controls()
        {
            if (controls != null)
                return controls;
            controls = new Dictionary<string, MyStringId>(StringComparer.OrdinalIgnoreCase);
            foreach (FieldInfo field in typeof(MyControlsSpace).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.FieldType == typeof(MyStringId))
                    controls[field.Name] = (MyStringId)field.GetValue(null);
            }
            return controls;
        }

        private static string Unquote(string text)
        {
            text = text.Trim();
            return text.Length >= 2 && text[0] == '"' && text[text.Length - 1] == '"' ? text.Substring(1, text.Length - 2) : text;
        }

        private static bool TrySeconds(string text, out double seconds) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) && seconds >= 0 && seconds < 3600;

        private static bool TryNumbers(string text, int min, int max, out float[] numbers)
        {
            string[] parts = text.Split(',');
            numbers = new float[parts.Length];
            if (parts.Length < min || parts.Length > max)
                return false;
            for (int i = 0; i < parts.Length; i++)
            {
                if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]))
                    return false;
            }
            return true;
        }

        /// <summary>A PNG's width and height from its header.</summary>
        private static string PngSize(string path)
        {
            try
            {
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var header = new byte[24];
                    if (file.Read(header, 0, 24) < 24)
                        return "?x?";
                    int width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
                    int height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
                    return $"{width}x{height}";
                }
            }
            catch (Exception)
            {
                return "?x?";
            }
        }
    }
}
