// FocusReturn on Windows: the foreground watcher, through user32.
#if !LINUX
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SpaceEngineersVR.Input
{
    internal static partial class FocusReturn
    {
        private const int PollMilliseconds = 20;

        // The foreground window and its owner: telling the game's own windows from anyone else's.
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        // The one call that changes anything: gives the foreground back. Windows allows it because this process holds the
        // foreground at that moment.
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr window);

        // Names for the log line only.
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder text, int count);

        // Read only, and sends no input: whether a key or button is down now (GetAsyncKeyState), and when the last input of any
        // kind was (GetLastInputInfo). They tell a take by a person at the PC from the game's own.
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);

        [StructLayout(LayoutKind.Sequential)]
        private struct LastInputInfo
        {
            public uint Size;
            public uint Time;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetLastInputInfo(ref LastInputInfo info);

        public static void Start()
        {
            string value = Environment.GetEnvironmentVariable(Variable);
            if (string.IsNullOrEmpty(value))
                return;
            long.TryParse(value, out long first);
            var thread = new Thread(() => Watch(new IntPtr(first))) { IsBackground = true, Name = "SEVR focus return", Priority = ThreadPriority.BelowNormal };
            thread.Start();
            Log.Info($"Focus return on ({Variable}={value}): the game hands the foreground back whenever it takes it");
        }

        private static void Watch(IntPtr other)
        {
            uint self = (uint)Process.GetCurrentProcess().Id;
            long lastLog = 0;
            int held = 0, handed = 0, failed = 0;
            string taken = null;
            while (true)
            {
                try
                {
                    IntPtr foreground = GetForegroundWindow();
                    if (foreground != IntPtr.Zero)
                    {
                        GetWindowThreadProcessId(foreground, out uint owner);
                        if (owner != self)
                            other = foreground;
                        else if (other != IntPtr.Zero && IsWindow(other))
                        {
                            if (ByPerson())
                            {
                                // The player clicked or Alt-Tabbed into the game: it is theirs now, for the rest of the run.
                                Log.Info($"Focus return off for the rest of this run: the game took the foreground with a mouse button or Alt down, so someone at the PC brought it forward ({Describe(foreground)})");
                                return;
                            }
                            if (taken == null)
                                taken = Describe(foreground);
                            if (SetForegroundWindow(other))
                                handed++;
                            else
                                failed++;
                            held++;
                        }
                    }
                    long now = Stopwatch.GetTimestamp();
                    if (held > 0 && now - lastLog > Stopwatch.Frequency)
                    {
                        lastLog = now;
                        Log.Info($"Focus return: the game took the foreground ({taken}); handed back to '{Title(other)}' ({handed} handed back, {failed} refused)");
                        held = handed = failed = 0;
                        taken = null;
                    }
                }
                catch (Exception e)
                {
                    Log.Error(e, "Focus return stopped");
                    return;
                }
                Thread.Sleep(PollMilliseconds);
            }
        }

        /// <summary>Which window of the game took the foreground, and whether someone at the PC may have done it (a click, Alt+Tab).</summary>
        private static string Describe(IntPtr window)
        {
            var kind = new StringBuilder(128);
            GetClassName(window, kind, kind.Capacity);
            var last = new LastInputInfo { Size = (uint)Marshal.SizeOf(typeof(LastInputInfo)) };
            string input = GetLastInputInfo(ref last) ? $"last input {unchecked((uint)Environment.TickCount - last.Time)} ms before" : "last input unknown";
            bool mouse = Down(VkLeftButton) || Down(VkRightButton) || Down(VkMiddleButton);
            return $"window '{Title(window)}' {kind}; mouse button {(mouse ? "down" : "up")}, Alt {(Down(VkAlt) ? "down" : "up")}, {input}";
        }

        private const int VkLeftButton = 0x01, VkRightButton = 0x02, VkMiddleButton = 0x04, VkAlt = 0x12;

        /// <summary>A mouse button or Alt is down: the take came from someone at the PC (a click on the game, Alt+Tab), not from the game.</summary>
        private static bool ByPerson() => Down(VkLeftButton) || Down(VkRightButton) || Down(VkMiddleButton) || Down(VkAlt);

        private static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

        private static string Title(IntPtr window)
        {
            var text = new StringBuilder(256);
            GetWindowText(window, text, text.Capacity);
            return text.ToString();
        }
    }
}
#endif
