using System;
using System.Threading;

namespace SpaceEngineersVR.Common
{
    /// <summary>
    /// A background thread that exists only while something wants it: <see cref="Ensure"/> starts it, it runs
    /// <c>step</c> every <c>intervalMs</c> while <c>wanted</c> says so, and it ends when <c>wanted</c> turns false (after
    /// <c>stopped</c> is told). Whatever needs it next calls <see cref="Ensure"/> again and a new thread starts. A step that
    /// throws ends it for good, after <c>failed</c> is told: <see cref="Ensure"/> then starts nothing, so a step that cannot
    /// work is not retried for ever. No game types, so it is tested on its own (tests/DemandThreadTests).
    /// </summary>
    /// <remarks>
    /// <see cref="Ensure"/> is for the hot path: one volatile read when the thread is running. A call that lands between the
    /// thread deciding to end and <see cref="Running"/> turning false starts nothing, and the next call does.
    /// </remarks>
    internal sealed class DemandThread
    {
        private readonly string name;
        private readonly int intervalMs;
        private readonly Func<bool> wanted;
        private readonly Action step;
        private readonly Action stopped;
        private readonly Action<Exception> failed;
        private volatile int state; // 0 not running, 1 running, 2 failed for good

        /// <param name="name">The thread's name, as a debugger or a stack dump shows it.</param>
        /// <param name="intervalMs">How long between one step's end and the next.</param>
        /// <param name="wanted">Whether the thread is needed now; read before each step, on the thread.</param>
        /// <param name="step">One round of the work, on the thread.</param>
        /// <param name="stopped">Told, on the thread, when it ends because <paramref name="wanted"/> turned false; may be null.</param>
        /// <param name="failed">Told the exception of a step that threw, on the thread, before it ends for good; may be null.</param>
        public DemandThread(string name, int intervalMs, Func<bool> wanted, Action step, Action stopped, Action<Exception> failed)
        {
            this.name = name;
            this.intervalMs = intervalMs;
            this.wanted = wanted ?? throw new ArgumentNullException(nameof(wanted));
            this.step = step ?? throw new ArgumentNullException(nameof(step));
            this.stopped = stopped;
            this.failed = failed;
        }

        /// <summary>The thread is running now (false once it has ended, or failed).</summary>
        public bool Running => state == 1;

        /// <summary>A step threw, or no thread could be started: it has ended for good.</summary>
        public bool Failed => state == 2;

        /// <summary>
        /// Starts the thread if it is not running and has not failed. True when this call started it; a thread that cannot be
        /// started counts as failed. Any thread.
        /// </summary>
        public bool Ensure()
        {
            if (state != 0 || Interlocked.CompareExchange(ref state, 1, 0) != 0)
                return false;
            try
            {
                new Thread(Run) { IsBackground = true, Name = name }.Start();
            }
            catch (Exception e)
            {
                // No thread to be had is the same as a step that cannot work, and nothing is thrown into the caller's hot path.
                Fail(e);
                return false;
            }
            return true;
        }

        private void Run()
        {
            try
            {
                while (wanted())
                {
                    step();
                    Thread.Sleep(intervalMs);
                }
                stopped?.Invoke();
            }
            catch (Exception e)
            {
                Fail(e);
                return;
            }
            state = 0;
        }

        private void Fail(Exception e)
        {
            state = 2;
            try
            {
                failed?.Invoke(e);
            }
            catch
            {
                // A handler that throws on the way out has nothing more to tell.
            }
        }
    }
}
