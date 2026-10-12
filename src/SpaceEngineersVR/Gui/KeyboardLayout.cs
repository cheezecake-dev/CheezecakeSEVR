// KeyboardLayout: the on-screen keyboard's key kinds, key and row definitions, and layouts.
using System.Collections.Generic;
using Sandbox;
using Sandbox.Graphics;
using Sandbox.Graphics.GUI;
using VRage.Audio;
using VRage.Game;
using VRage.Input;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Gui
{
    /// <summary>What a key does.</summary>
    internal enum KeyKind
    {
        Char,
        Space,
        Back,
        Shift,
        Caps,
        Left,
        Right,
        Clear,
        Done,
        Cancel,

        // The keys of the keyboard for a multi-line text only.
        Enter,
        Tab,
        Delete,
        Up,
        Down,
        Home,
        End,
        Undo,
        Redo,
        Close,
    }

    /// <summary>One key of the on-screen keyboard.</summary>
    internal sealed class KeyDef
    {
        public KeyDef(KeyKind kind, char normal, char shifted, float width, string text)
        {
            Kind = kind;
            Normal = normal;
            Shifted = shifted;
            Width = width;
            Text = text;
        }

        public KeyKind Kind { get; }

        /// <summary>The character typed (a <see cref="KeyKind.Char"/> key) without and with Shift.</summary>
        public char Normal { get; }

        public char Shifted { get; }

        /// <summary>How many key widths it spans.</summary>
        public float Width { get; }

        /// <summary>The label of a key that is not a character.</summary>
        public string Text { get; }
    }

    /// <summary>A row of keys, starting <see cref="Offset"/> key widths in.</summary>
    internal sealed class KeyRow
    {
        public KeyRow(float offset, KeyDef[] keys, float[] starts)
        {
            Offset = offset;
            Keys = keys;
            Starts = starts;
        }

        public float Offset { get; }

        public KeyDef[] Keys { get; }

        /// <summary>Where each key starts, in key widths from the keyboard's left edge (the offset and any gap before it included).</summary>
        public float[] Starts { get; }
    }

    /// <summary>
    /// The keys: a US QWERTY keyboard, five rows. The one for a line of text is fourteen key widths across. The one for a
    /// multi-line text (<see cref="Multiline"/>) has the same letters with an editor's keys around them: Enter makes a
    /// new line, Tab, Delete, Home and End, Undo and Redo, and the four arrows in their usual cluster.
    /// </summary>
    internal static class KeyboardLayout
    {
        public const float Units = 14f;

        public const float MultilineUnits = 17.25f;

        public static readonly KeyRow[] Rows =
        {
            Row(0f, Pairs("1234567890-=", "!@#$%^&*()_+"), Special(KeyKind.Back, 2f, "Back")),
            Row(0.5f, Pairs("qwertyuiop[]", "QWERTYUIOP{}"), Special(KeyKind.Left, 0.75f, "<"), Special(KeyKind.Right, 0.75f, ">")),
            Row(0.75f, Pairs("asdfghjkl;'" + "\\" + "`", "ASDFGHJKL:\"|~")),
            Row(0f, Special(KeyKind.Shift, 1.5f, "Shift"), Special(KeyKind.Caps, 1.5f, "Caps"), Pairs("zxcvbnm,./", "ZXCVBNM<>?"), Special(KeyKind.Clear, 1f, "Clr")),
            Row(0f, Special(KeyKind.Cancel, 3f, "Cancel"), Special(KeyKind.Space, 8f, "Space"), Special(KeyKind.Done, 3f, "Done")),
        };

        /// <summary>A number in a row is a gap of that many key widths.</summary>
        public static readonly KeyRow[] Multiline =
        {
            Row(0f, Pairs("1234567890-=", "!@#$%^&*()_+"), Special(KeyKind.Back, 2f, "Back"), 0.25f,
                Special(KeyKind.Home, 1.5f, "Home"), Special(KeyKind.End, 1.5f, "End")),
            Row(0.5f, Pairs("qwertyuiop[]", "QWERTYUIOP{}"), Special(KeyKind.Delete, 1.5f, "Del"), 0.25f,
                Special(KeyKind.Undo, 1.5f, "Undo"), Special(KeyKind.Redo, 1.5f, "Redo")),
            Row(0.75f, Pairs("asdfghjkl;'" + "\\" + "`", "ASDFGHJKL:\"|~")),
            Row(0f, Special(KeyKind.Shift, 1.5f, "Shift"), Special(KeyKind.Caps, 1.5f, "Caps"), Pairs("zxcvbnm,./", "ZXCVBNM<>?"), Special(KeyKind.Tab, 1f, "Tab"), 1.25f,
                Special(KeyKind.Up, 1f, "Up")),
            Row(0f, Special(KeyKind.Close, 3f, "Close"), Special(KeyKind.Space, 8f, "Space"), Special(KeyKind.Enter, 3f, "Enter"), 0.25f,
                Special(KeyKind.Left, 1f, "<"), Special(KeyKind.Down, 1f, "Down"), Special(KeyKind.Right, 1f, ">")),
        };

        private static KeyDef Special(KeyKind kind, float width, string text) => new KeyDef(kind, '\0', '\0', width, text);

        /// <summary>One character key per character of the two strings (the same length: without and with Shift).</summary>
        private static KeyDef[] Pairs(string normal, string shifted)
        {
            var keys = new KeyDef[normal.Length];
            for (int i = 0; i < keys.Length; i++)
                keys[i] = new KeyDef(KeyKind.Char, normal[i], shifted[i], 1f, null);
            return keys;
        }

        /// <summary>A row from keys, groups of keys and gaps, in order.</summary>
        private static KeyRow Row(float offset, params object[] parts)
        {
            var keys = new List<KeyDef>();
            var starts = new List<float>();
            float x = offset;
            void Add(KeyDef key)
            {
                keys.Add(key);
                starts.Add(x);
                x += key.Width;
            }
            foreach (object part in parts)
            {
                if (part is KeyDef one)
                    Add(one);
                else if (part is float gap)
                    x += gap;
                else
                    foreach (KeyDef key in (KeyDef[])part)
                        Add(key);
            }
            return new KeyRow(offset, keys.ToArray(), starts.ToArray());
        }
    }
}
