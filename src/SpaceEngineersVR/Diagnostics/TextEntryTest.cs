using System;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Rendering;
using VRageMath;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// The drive's test aids for typing (<see cref="Input.Drive"/>):
    /// <list type="bullet">
    /// <item>"edit text|data|code [name|lines]" opens a multi-line text editor as the game does: "data" a block's custom
    /// data (MyTerminalBlock.OpenWindow, the terminal's Custom Data button), "text" an LCD's text (MyTextPanel.OpenWindow,
    /// as using it does), "code" a programmable block's code (its Edit button's SendOpenEditorRequest); the nearest such
    /// block, or the nearest whose name or subtype has the text in it. With no such block, or with a number instead of a
    /// name, "text" and "code" open the same screens on their own (MyGuiScreenTextPanel, MyGuiScreenEditor) holding that
    /// many lines of sample text (12 and 400 by default). It reports where the text is in the shot, to click.</item>
    /// <item>"keyboard [state]" says what the on-screen keyboard is typing into and where; "keyboard press &lt;label&gt;"
    /// presses a key as its button does ("a", "A", "!", "Enter", "Back", "Space", "Up"); "keyboard where &lt;label&gt;"
    /// says where a key is in the shot, for a "gui click" through the laser's path.</item>
    /// </list>
    /// </summary>
    internal static class TextEntryTest
    {
        private const int OpenFrames = 120;

        private static string opening;

        /// <summary>One frame of an "edit" or "keyboard" command; true when it has ended.</summary>
        internal static bool Step(string verb, string args, int frames, out bool ok, out string detail)
        {
            ok = false;
            detail = null;
            string text = (args ?? string.Empty).Trim();
            int space = text.IndexOf(' ');
            string first = (space < 0 ? text : text.Substring(0, space)).ToLowerInvariant();
            string rest = space < 0 ? string.Empty : text.Substring(space + 1).Trim();
            return verb == "edit" ? Edit(first, rest, frames, out ok, out detail) : Keyboard(first, rest, out ok, out detail);
        }

        // ---- edit ----

        private static bool Edit(string kind, string rest, int frames, out bool ok, out string detail)
        {
            ok = false;
            detail = null;
            if (frames == 0)
            {
                if (kind != "text" && kind != "data" && kind != "code")
                {
                    detail = "edit needs text, data or code (then a block's name, or a number of lines)";
                    return true;
                }
                if (MySession.Static == null)
                {
                    detail = "no world is loaded";
                    return true;
                }
                opening = Open(kind, rest, out string failure);
                if (opening == null)
                {
                    detail = failure;
                    return true;
                }
                return false;
            }

            MyGuiScreenBase focus = MyScreenManager.GetScreenWithFocus();
            MyGuiControlMultilineText box = (focus as MyGuiScreenText)?.Description ?? (focus as MyGuiScreenEditor)?.Description;
            if (focus != null && focus.State == MyGuiScreenState.OPENED && box is MyGuiControlMultilineEditableText editor)
            {
                ok = true;
                Vector2 middle = Shot(editor.GetPositionAbsoluteTopLeft() + editor.Size / 2f);
                detail = $"{opening}: {focus.GetFriendlyName()} is open, {editor.Text.Length} characters; click its text at {middle.X:0},{middle.Y:0} in the shot";
                return true;
            }
            if (frames < OpenFrames)
                return false;
            detail = $"{opening}, but no text editor had the focus in {OpenFrames} frames (focus {focus?.GetFriendlyName() ?? "none"})";
            return true;
        }

        /// <summary>Opens the editor; what was opened, or null and why not.</summary>
        private static string Open(string kind, string rest, out string failure)
        {
            failure = null;
            bool count = int.TryParse(rest, out int lines) && lines > 0;
            string name = count ? null : rest;
            switch (kind)
            {
                case "data":
                {
                    if (count)
                    {
                        failure = "custom data needs a block: edit data [name]";
                        return null;
                    }
                    MyTerminalBlock block = Nearest<MyTerminalBlock>(name);
                    if (block == null)
                    {
                        failure = $"no terminal block{Named(name)} in the world";
                        return null;
                    }
                    block.OpenWindow(isEditable: true, sync: true);
                    return $"custom data of '{block.CustomName}' ({Away(block)})";
                }
                case "text":
                {
                    MyTextPanel panel = count ? null : Nearest<MyTextPanel>(name);
                    if (panel != null)
                    {
                        panel.OpenWindow(isEditable: true, sync: true, isPublic: true);
                        return $"text of '{panel.CustomName}' ({Away(panel)})";
                    }
                    if (!string.IsNullOrEmpty(name))
                    {
                        failure = $"no LCD{Named(name)} in the world";
                        return null;
                    }
                    lines = count ? lines : 12;
                    OpenOnItsOwn(new MyGuiScreenTextPanel("VR keyboard test", "", "", Sample(false, lines), _ => LetGo(), null, null, editable: true));
                    return $"a text panel editor on its own, {lines} lines (no LCD{(count ? " asked for" : " in the world")})";
                }
                default:
                {
                    MyProgrammableBlock block = count ? null : Nearest<MyProgrammableBlock>(name);
                    if (block != null)
                    {
                        MethodInfo send = AccessTools.Method(typeof(MyProgrammableBlock), "SendOpenEditorRequest");
                        if (send == null)
                        {
                            failure = "MyProgrammableBlock.SendOpenEditorRequest was not found (the game changed?)";
                            return null;
                        }
                        send.Invoke(block, null);
                        return $"code of '{block.CustomName}' ({Away(block)})";
                    }
                    if (!string.IsNullOrEmpty(name))
                    {
                        failure = $"no programmable block{Named(name)} in the world";
                        return null;
                    }
                    lines = count ? lines : 400;
                    OpenOnItsOwn(new MyGuiScreenEditor(Sample(true, lines), _ => LetGo(), () => { }));
                    return $"a code editor on its own, {lines} lines (no programmable block{(count ? " asked for" : " in the world")})";
                }
            }
        }

        /// <summary>As the blocks open their editors: the screen is the gameplay screen until it closes.</summary>
        private static void OpenOnItsOwn(MyGuiScreenBase screen)
        {
            MyGuiScreenGamePlay.TmpGameplayScreenHolder = MyGuiScreenGamePlay.ActiveGameplayScreen;
            MyScreenManager.AddScreen(MyGuiScreenGamePlay.ActiveGameplayScreen = screen);
        }

        /// <summary>As the blocks do when their editor closes.</summary>
        private static void LetGo()
        {
            MyGuiScreenGamePlay.ActiveGameplayScreen = MyGuiScreenGamePlay.TmpGameplayScreenHolder;
            MyGuiScreenGamePlay.TmpGameplayScreenHolder = null;
        }

        private static string Sample(bool code, int lines)
        {
            var text = new StringBuilder();
            if (!code)
            {
                for (int i = 1; i <= lines; i++)
                    text.Append("Line ").Append(i).Append(" of the VR keyboard's test text.").Append(i < lines ? "\n" : string.Empty);
                return text.ToString();
            }
            text.Append("// VR keyboard test, ").Append(lines).Append(" lines\npublic Program()\n{\n}\n\npublic void Main(string argument, UpdateType updateSource)\n{\n");
            for (int i = 8; i < lines; i++)
                text.Append("    Echo(\"line ").Append(i).Append("\");\n");
            text.Append('}');
            return text.ToString();
        }

        private static T Nearest<T>(string name) where T : MyTerminalBlock
        {
            Vector3D at = Here();
            return MyEntities.GetEntities().OfType<MyCubeGrid>()
                .SelectMany(grid => grid.GetFatBlocks())
                .OfType<T>()
                .Where(b => string.IsNullOrEmpty(name) || Has(b.CustomName?.ToString(), name) || Has(b.BlockDefinition?.Id.SubtypeName, name))
                .OrderBy(b => Vector3D.DistanceSquared(b.PositionComp.GetPosition(), at))
                .FirstOrDefault();
        }

        private static bool Has(string text, string part) => (text?.IndexOf(part, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;

        private static Vector3D Here() =>
            MySession.Static?.LocalCharacter?.PositionComp.GetPosition() ?? MySession.Static?.ControlledEntity?.Entity?.PositionComp.GetPosition() ?? Vector3D.Zero;

        private static string Away(MyTerminalBlock block) => $"{Vector3D.Distance(block.PositionComp.GetPosition(), Here()):0.0} m away";

        private static string Named(string name) => string.IsNullOrEmpty(name) ? string.Empty : $" named '{name}'";

        // ---- keyboard ----

        private static bool Keyboard(string what, string label, out bool ok, out string detail)
        {
            ok = false;
            VRKeyboardScreen keyboard = VRKeyboardHook.Current;
            if (what == string.Empty || what == "state")
            {
                ok = true;
                detail = keyboard == null ? $"no on-screen keyboard is open (focus {MyScreenManager.GetScreenWithFocus()?.GetFriendlyName() ?? "none"})" : State(keyboard);
                return true;
            }
            if (what != "press" && what != "where")
            {
                detail = "keyboard [state], keyboard press <label>, keyboard where <label>";
                return true;
            }
            if (keyboard == null)
            {
                detail = "no on-screen keyboard is open";
                return true;
            }
            if (label.Length == 0)
            {
                detail = $"keyboard {what} needs a key's label";
                return true;
            }
            KeyDef key = keyboard.FindKey(label, out string labels);
            if (key == null)
            {
                detail = $"no key '{label}' (keys: {labels})";
                return true;
            }
            ok = true;
            if (what == "where")
            {
                Vector2 at = Shot(keyboard.KeyCentre(key));
                detail = $"key '{label}' ({key.Kind}) at {at.X:0},{at.Y:0} in the shot";
                return true;
            }
            keyboard.PressKey(key);
            detail = keyboard.Finished ? $"pressed '{label}' ({key.Kind}); the keyboard closed" : $"pressed '{label}' ({key.Kind}); {keyboard.Describe()}";
            return true;
        }

        private static string State(VRKeyboardScreen keyboard)
        {
            string state = keyboard.Describe();
            MultilineTyping typing = keyboard.Typing;
            if (typing == null)
                return state;
            typing.CaretAt(out Vector2 caret, out float height);
            RectangleF area = typing.TextArea;
            Vector2 at = Shot(caret + new Vector2(0f, height / 2f));
            Vector2 from = Shot(new Vector2(area.X, area.Y)), to = Shot(new Vector2(area.X + area.Width, area.Y + area.Height));
            return $"{state}; in the shot the caret is at {at.X:0},{at.Y:0} and the text area {from.X:0},{from.Y:0} to {to.X:0},{to.Y:0}";
        }

        /// <summary>A point on the GUI (normalised) to where it is in the drive's shot.</summary>
        private static Vector2 Shot(Vector2 normalized) =>
            PanelCursor.GuiToWindow(Sandbox.Graphics.MyGuiManager.GetScreenCoordinateFromNormalizedCoordinate(normalized));
    }
}
