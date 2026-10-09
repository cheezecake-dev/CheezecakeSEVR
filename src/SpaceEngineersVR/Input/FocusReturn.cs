using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SpaceEngineersVR.Input
{
    /// <summary>
    /// Test aid: a game started for a test never keeps the foreground. With the environment variable SEVR_FOCUS_BACK set
    /// (a test launcher sets it, to the window that had the foreground at launch), a background
    /// thread watches the foreground window from before the game's Main runs; whenever a window of this process takes it
    /// (the game shows and focuses its window at start, MySandboxGame.InitQuickLaunch), it is handed straight back to the
    /// window that had it before. The game is the foreground process at that moment, so Windows lets it give the
    /// foreground away. No keyboard or mouse input is sent. Without the variable nothing here runs. A take with a mouse
    /// button or Alt down is someone at the PC clicking or Alt-Tabbing into the game: from then on, for the rest of the
    /// run, the game keeps the foreground it is given (logged once).
    /// </summary>
    internal static class FocusReturn
    {
        public const string Variable = "SEVR_FOCUS_BACK";
        private const int PollMilliseconds = 20;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder text, int count);

        // Read only: whether a key or button is down now, and when the last input of any kind was.
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
