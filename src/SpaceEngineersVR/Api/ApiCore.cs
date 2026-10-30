// ApiCore: the registry behind SevrApi version 2: owners, their failure guards, refusals logged once, and the lists registrations live in (no game types; tests/ApiTests runs it).
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace SpaceEngineersVR.Api
{
    /// <summary>How long a registration lasts.</summary>
    internal enum ApiScope
    {
        /// <summary>Until it is taken away or the game closes: made through <see cref="SevrApi.GetApi"/> (a plugin).</summary>
        Run,

        /// <summary>Until the world unloads: made through the mod channel (a mod, whose delegates die with the world).</summary>
        World,
    }

    /// <summary>The ids an integrator names things by.</summary>
    internal static class ApiIds
    {
        /// <summary>The longest owner or id.</summary>
        public const int MaxLength = 64;

        /// <summary>An owner: 1 to 64 letters, digits, <c>_</c>, <c>.</c> or <c>-</c> ("MyPlugin", "com.example.tools").</summary>
        public static bool IsOwner(string text) => Valid(text, dot: true);

        /// <summary>
        /// An id within an owner: 1 to 64 letters, digits, <c>_</c> or <c>-</c>; no <c>.</c>, so a full id (<see cref="Full"/>)
        /// reads only one way.
        /// </summary>
        public static bool IsId(string text) => Valid(text, dot: false);

        /// <summary>The full id, <c>owner.id</c>: what an action is saved and asked for by.</summary>
        public static string Full(string owner, string id) => owner + "." + id;

        /// <summary>The longest definition subtype id taken (<see cref="IsSubtype"/>).</summary>
        public const int MaxSubtype = 128;

        /// <summary>
        /// A definition's subtype id as the game's definitions name it ("AutomaticRifleItem", a mod's "MyRifle_Item"): 1 to
        /// <see cref="MaxSubtype"/> characters, no control characters, not starting or ending with a space; matched exactly.
        /// </summary>
        public static bool IsSubtype(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > MaxSubtype || char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[text.Length - 1]))
                return false;
            foreach (char c in text)
                if (char.IsControl(c))
                    return false;
            return true;
        }

        private static bool Valid(string text, bool dot)
        {
            if (string.IsNullOrEmpty(text) || text.Length > MaxLength)
                return false;
            foreach (char c in text)
            {
                bool ok = c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '_' || c == '-' || dot && c == '.';
                if (!ok)
                    return false;
            }
            return true;
        }
    }

    /// <summary>A list of registrations, by owner and key. <see cref="ApiCore"/> asks each to take entries away.</summary>
    internal interface IApiRegistry
    {
        /// <summary>Takes away <paramref name="owner"/>'s entries (all of them, or the one keyed <paramref name="id"/>). How many went.</summary>
        int Remove(string owner, string id);

        /// <summary>
        /// Takes away every entry made over the mod channel (<see cref="ApiScope.World"/>) in a world other than
        /// <paramref name="world"/>. How many went.
        /// </summary>
        int RemoveOtherWorlds(object world);
    }

    /// <summary>
    /// Registrations of one kind, by owner and key, copied on write: a change makes a new array, so something walking
    /// <see cref="Snapshot"/> (a frame's dispatch) walks the entries as they were when it took it, whatever a callback it runs
    /// registers or takes away. Game thread.
    /// </summary>
    internal sealed class ApiList<T> where T : class
    {
        public sealed class Entry
        {
            public readonly string Owner, Key;
            public readonly ApiScope Scope;

            /// <summary>The world it was made in, for <see cref="ApiScope.World"/> (<see cref="ApiCore.World"/>); null otherwise.</summary>
            public readonly object World;

            public readonly T Value;

            public Entry(string owner, string key, ApiScope scope, object world, T value)
            {
                Owner = owner;
                Key = key;
                Scope = scope;
                World = scope == ApiScope.World ? world : null;
                Value = value;
            }
        }

        private volatile Entry[] snapshot = Array.Empty<Entry>();

        /// <summary>The entries now; never changed in place.</summary>
        public Entry[] Snapshot => snapshot;

        /// <summary>The entry <paramref name="owner"/> keyed <paramref name="key"/>, or null.</summary>
        public Entry Find(string owner, string key)
        {
            foreach (Entry e in snapshot)
                if (e.Owner == owner && e.Key == key)
                    return e;
            return null;
        }

        /// <summary>How many entries <paramref name="owner"/> has.</summary>
        public int CountOf(string owner)
        {
            int n = 0;
            foreach (Entry e in snapshot)
                if (e.Owner == owner)
                    n++;
            return n;
        }

        /// <summary>Adds the entry, or replaces the one with its owner and key in its place. The replaced one, or null.</summary>
        public Entry Set(Entry entry)
        {
            Entry[] old = snapshot;
            for (int i = 0; i < old.Length; i++)
            {
                if (old[i].Owner != entry.Owner || old[i].Key != entry.Key)
                    continue;
                var copy = (Entry[])old.Clone();
                copy[i] = entry;
                snapshot = copy;
                return old[i];
            }
            var grown = new Entry[old.Length + 1];
            Array.Copy(old, grown, old.Length);
            grown[old.Length] = entry;
            snapshot = grown;
            return null;
        }

        /// <summary>Takes away the entries <paramref name="which"/> picks; the ones taken.</summary>
        public List<Entry> Remove(Func<Entry, bool> which)
        {
            Entry[] old = snapshot;
            var kept = new List<Entry>(old.Length);
            var gone = new List<Entry>();
            foreach (Entry e in old)
                (which(e) ? gone : kept).Add(e);
            if (gone.Count > 0)
                snapshot = kept.ToArray();
            return gone;
        }

        /// <summary>The choice for <see cref="IApiRegistry.Remove"/>: the owner's entries, or its one keyed <paramref name="id"/>.</summary>
        public static Func<Entry, bool> Picks(string owner, string id) => e => e.Owner == owner && (id == null || e.Key == id);

        /// <summary>The choice for <see cref="IApiRegistry.RemoveOtherWorlds"/>.</summary>
        public static Func<Entry, bool> OtherWorlds(object world) =>
            e => e.Scope == ApiScope.World && !ReferenceEquals(e.World, world);
    }

    /// <summary>
    /// The owners of registrations, and the rules every member keeps (docs/MOD_API.md, "Rules for every member"):
    /// <list type="bullet">
    /// <item>Game thread only. A call from another thread is refused (false or the "no VR" value), and logged once per owner
    /// and member; it is never queued, as a queue would hide an ordering bug.</item>
    /// <item>Invalid input (a malformed owner or id, an unknown name, a null delegate) is refused the same way. Nothing throws
    /// into the caller.</item>
    /// <item>Every callback of an owner runs through that owner's <see cref="FeatureGuard"/>: an exception is logged with the
    /// owner's name, and a burst switches that owner's callbacks off (30 s, then 2 min, then for the session), so one plugin's
    /// fault never stops this plugin's features or another's. The guard writes the owner's first exception in full; each later
    /// one is a line of its own, at most <see cref="MaxExceptionLines"/> in <see cref="ExceptionWindowSeconds"/>, then a count.</item>
    /// <item>Every callback is timed: one slower than <see cref="SlowCallbackMs"/> is logged once per owner and callback. It is
    /// never cut off: a callback on the game thread cannot be stopped safely, so the frame waits for it.</item>
    /// </list>
    /// </summary>
    internal sealed class ApiCore
    {
        /// <summary>The longest name an owner may be listed under.</summary>
        public const int MaxOwnerName = 48;

        /// <summary>How many different refusals are logged; past that they are counted, not written, so a caller in a loop cannot fill the log.</summary>
        public const int MaxLoggedRefusals = 256;

        /// <summary>
        /// How many of an owner's exceptions after its first (which its guard writes in full, with the stack) are written, a line
        /// each, in <see cref="ExceptionWindowSeconds"/>; the rest are counted, and the count written when the window ends. The
        /// guard switches an owner off at three exceptions in ten seconds, so a callback that throws more thinly, every four
        /// seconds say, is never switched off and would write a line every four seconds for the whole run. Five lines in five
        /// minutes still shows each new fault and how often it comes, at a line a minute at most.
        /// </summary>
        public const int MaxExceptionLines = 5;

        /// <summary>The window <see cref="MaxExceptionLines"/> counts in, in seconds; it opens at the first line written.</summary>
        public const double ExceptionWindowSeconds = 300.0;

        /// <summary>
        /// A callback slower than this, in milliseconds, is logged, once per owner and callback. Callbacks run on the game thread
        /// inside the frame and the frame waits for them: a frame is 11.1 ms at 90 Hz (13.9 ms at 72 Hz, 8.3 ms at 120 Hz), nearly
        /// all of it the game's update and the plugin's rendering, and an action's <c>available</c> answer is asked every frame.
        /// 1 ms, a ninth of a 90 Hz frame, is many times what answering from a field takes, and on its own enough to drop a frame
        /// that is already full. A callback's first slow call is not logged (a method's first call includes compiling it), nor a
        /// call during which a garbage collection ran (that pause is the game's).
        /// </summary>
        public const double SlowCallbackMs = 1.0;

        /// <summary>How many different slow callbacks are kept per owner: past that a new one is not, so ids made up on the fly cannot grow it.</summary>
        public const int MaxSlowCallbacksKept = 256;

        private sealed class Owner
        {
            public readonly FeatureGuard Guard;
            public string Name;

            /// <summary>Exceptions of the run, the first included.</summary>
            public int Exceptions;

            /// <summary>The window of later exceptions' lines: when it opened (a timestamp), lines written in it, and exceptions not written.</summary>
            public long WindowOpened;
            public int WindowLines, Unwritten;

            /// <summary>How many times each callback was slow (up to 2), by what it is ("callback for action boost"); null until one is.</summary>
            public Dictionary<string, int> Slow;

            public Owner(string id, Func<long> clock)
            {
                Guard = new FeatureGuard("SEVR API caller " + id, clock);
                Name = id;
            }
        }

        private readonly Func<bool> onGameThread;
        private readonly Func<object> world;
        private readonly Func<long> clock;
        private readonly Dictionary<string, Owner> owners = new Dictionary<string, Owner>(StringComparer.Ordinal);
        private readonly List<IApiRegistry> registries = new List<IApiRegistry>();
        private readonly HashSet<string> logged = new HashSet<string>(StringComparer.Ordinal);
        private readonly object logGate = new object();
        private int unlogged;

        /// <summary>How many owners have exceptions counted and not written yet (<see cref="EndExceptionWindows"/>).</summary>
        private int ownersUnwritten;

        /// <param name="onGameThread">True on the game thread (the plugin: the game's main thread).</param>
        /// <param name="world">The world loaded now, as an object compared by reference (the plugin: the game's session); null for none.</param>
        /// <param name="timestamp">
        /// The time in Stopwatch ticks, for the owners' guards, their exception lines and the callbacks' timing; null for
        /// <see cref="Stopwatch.GetTimestamp"/> (a test passes its own).
        /// </param>
        public ApiCore(Func<bool> onGameThread, Func<object> world, Func<long> timestamp = null)
        {
            this.onGameThread = onGameThread ?? throw new ArgumentNullException(nameof(onGameThread));
            this.world = world ?? throw new ArgumentNullException(nameof(world));
            clock = timestamp ?? Stopwatch.GetTimestamp;
        }

        /// <summary>
        /// The world loaded now: what a registration made over the mod channel is tied to, so it goes with that world and no other
        /// (<see cref="EndOtherWorlds"/>), whichever order a world's mods and the plugin's frame come in.
        /// </summary>
        public object World => world();

        /// <summary>A kind of registration, taken away by <see cref="Unregister"/> and <see cref="EndOtherWorlds"/>.</summary>
        public void Add(IApiRegistry registry) => registries.Add(registry);

        /// <summary>The call is on the game thread; if not, it is refused and logged once for <paramref name="owner"/> and <paramref name="member"/>.</summary>
        public bool OnGameThread(string owner, string member)
        {
            if (onGameThread())
                return true;
            Refuse(owner, member, "called off the game thread; every member is for the game thread only");
            return false;
        }

        /// <summary>
        /// The usual entry check for a member that names an owner: the game thread, and a valid owner id. False (refused and
        /// logged once) otherwise.
        /// </summary>
        public bool Admit(string owner, string member)
        {
            if (!OnGameThread(owner, member))
                return false;
            if (ApiIds.IsOwner(owner))
                return true;
            Refuse(owner, member, $"the owner id is not 1 to {ApiIds.MaxLength} letters, digits, '_', '.' or '-'");
            return false;
        }

        /// <summary>The call is refused: logged once per owner, member and reason (<see cref="MaxLoggedRefusals"/>).</summary>
        public void Refuse(string owner, string member, string why)
        {
            string who = ApiIds.IsOwner(owner) ? owner : owner == null ? "(no owner)" : "(an invalid owner id)";
            string key = who + "|" + member + "|" + why;
            lock (logGate)
            {
                if (logged.Contains(key))
                    return;
                if (logged.Count >= MaxLoggedRefusals)
                {
                    if (unlogged++ == 0)
                        Log.Warn($"SEVR API: {MaxLoggedRefusals} different refused calls logged; later ones are not");
                    return;
                }
                logged.Add(key);
            }
            Log.Warn($"SEVR API: {who} called {member}: refused, {why} (docs/MOD_API.md; this is logged once)");
        }

        /// <summary>
        /// Runs <paramref name="call"/>, one of <paramref name="owner"/>'s callbacks (<paramref name="what"/> names it in the log),
        /// under its guard, timed. False when its callbacks are off or it threw.
        /// </summary>
        public bool Invoke(string owner, string what, Action call) => Invoke(owner, what, RunCall, call);

        /// <summary>Calls the callback it is given; one delegate for the run, so <see cref="Invoke(string, string, Action)"/> makes none.</summary>
        private static readonly Action<Action> RunCall = call => call();

        /// <summary>
        /// As <see cref="Invoke(string, string, Action)"/>, giving the callback <paramref name="arg"/>: a frame's dispatch passes
        /// its argument this way rather than in a new closure, so it allocates nothing.
        /// </summary>
        public bool Invoke<T>(string owner, string what, Action<T> call, T arg)
        {
            Owner o = OwnerOf(owner);
            if (!o.Guard.On)
                return false;
            long start = clock();
            int collections = GC.CollectionCount(0);
            try
            {
                call(arg);
                return true;
            }
            catch (Exception e)
            {
                Threw(o, owner, what, e);
                return false;
            }
            finally
            {
                Timed(o, owner, what, start, collections);
            }
        }

        /// <summary>As <see cref="Invoke(string, string, Action)"/>, for a callback that answers: <paramref name="fallback"/> when it is off or throws.</summary>
        public T Ask<T>(string owner, string what, Func<T> call, T fallback)
        {
            Owner o = OwnerOf(owner);
            if (!o.Guard.On)
                return fallback;
            long start = clock();
            int collections = GC.CollectionCount(0);
            try
            {
                return call();
            }
            catch (Exception e)
            {
                Threw(o, owner, what, e);
                return fallback;
            }
            finally
            {
                Timed(o, owner, what, start, collections);
            }
        }

        /// <summary>
        /// Game thread, once a frame: writes the count of each owner's exceptions not written in a window that has ended, so a
        /// count is written even when no exception comes after it. Nothing to do (one read) while no owner has any.
        /// </summary>
        public void EndExceptionWindows()
        {
            if (ownersUnwritten == 0)
                return;
            long now = clock();
            foreach (KeyValuePair<string, Owner> pair in owners)
                if (pair.Value.Unwritten > 0 && WindowEnded(pair.Value, now))
                    CloseWindow(pair.Value, pair.Key);
        }

        /// <summary>
        /// One of <paramref name="o"/>'s callbacks threw: its guard counts it (and writes the first in full); a later one is a
        /// line of its own, within the owner's window (<see cref="MaxExceptionLines"/>), else counted.
        /// </summary>
        private void Threw(Owner o, string owner, string what, Exception e)
        {
            o.Guard.Failed(e, $"SEVR API: {owner}'s {what} threw");
            if (++o.Exceptions == 1)
                return;
            long now = clock();
            if (o.WindowLines > 0 && WindowEnded(o, now))
                CloseWindow(o, owner);
            if (o.WindowLines == 0)
                o.WindowOpened = now;
            if (o.WindowLines < MaxExceptionLines)
            {
                o.WindowLines++;
                Log.Warn($"SEVR API: {owner}'s {what} threw again, {e.GetType().Name}: {OneLine(e.Message, 200)} (exception {o.Exceptions} of the run)");
                return;
            }
            if (o.Unwritten++ == 0)
                ownersUnwritten++;
        }

        private static bool WindowEnded(Owner o, long now) => (double)(now - o.WindowOpened) / Stopwatch.Frequency >= ExceptionWindowSeconds;

        /// <summary>The owner's window ended: the count of exceptions it did not write is written, and the next line opens a new one.</summary>
        private void CloseWindow(Owner o, string owner)
        {
            if (o.Unwritten > 0)
            {
                Log.Warn($"SEVR API: {owner}: {o.Unwritten} more exception(s) not written in {ExceptionWindowSeconds:0} s "
                         + $"(at most {MaxExceptionLines} lines per owner in {ExceptionWindowSeconds:0} s)");
                ownersUnwritten--;
            }
            o.WindowLines = o.Unwritten = 0;
        }

        /// <summary>
        /// A callback started at <paramref name="start"/> ended: logged once when it was slower than <see cref="SlowCallbackMs"/>
        /// for the second time, not counting a call during which a garbage collection ran.
        /// </summary>
        private void Timed(Owner o, string owner, string what, long start, int collections)
        {
            double ms = (clock() - start) * 1000.0 / Stopwatch.Frequency;
            if (ms <= SlowCallbackMs || GC.CollectionCount(0) != collections)
                return;
            Dictionary<string, int> slow = o.Slow ?? (o.Slow = new Dictionary<string, int>(StringComparer.Ordinal));
            slow.TryGetValue(what, out int times);
            if (times >= 2 || times == 0 && slow.Count >= MaxSlowCallbacksKept)
                return;
            slow[what] = ++times;
            if (times == 2)
                Log.Warn($"SEVR API: {owner}'s {what} took {ms:0.0} ms, over the {SlowCallbackMs:0.0} ms a callback may take; "
                         + "it runs on the game thread, so the frame waits for it (this is logged once)");
        }

        /// <summary>
        /// <paramref name="owner"/>'s callbacks are called: false while a burst of exceptions has them switched off. An owner
        /// with nothing registered yet is enabled; an invalid id is not.
        /// </summary>
        public bool OwnerEnabled(string owner)
        {
            if (!ApiIds.IsOwner(owner))
                return false;
            return !owners.TryGetValue(owner, out Owner o) || o.Guard.On;
        }

        /// <summary>The name <paramref name="owner"/> is listed under (the bindings page's heading): its id until it gives one.</summary>
        public string NameOf(string owner) => owners.TryGetValue(owner, out Owner o) ? o.Name : owner;

        /// <summary>Gives <paramref name="owner"/> a name to be listed under. False (refused) for an empty, too long or multi-line name.</summary>
        public bool SetName(string owner, string name, string member)
        {
            string trimmed = name?.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxOwnerName || HasControlCharacters(trimmed))
            {
                Refuse(owner, member, $"the name is empty, over {MaxOwnerName} characters, or has line breaks or other control characters");
                return false;
            }
            OwnerOf(owner).Name = trimmed;
            return true;
        }

        /// <summary>Takes away <paramref name="owner"/>'s registrations (all, or those keyed <paramref name="id"/>). How many went.</summary>
        public int Unregister(string owner, string id)
        {
            int gone = 0;
            foreach (IApiRegistry r in registries)
                gone += r.Remove(owner, id);
            return gone;
        }

        /// <summary>
        /// Takes away every registration made over the mod channel in a world other than <paramref name="current"/> (a world
        /// unloaded, or another loaded in its place: a mod's delegates die with its world). How many went.
        /// </summary>
        public int EndOtherWorlds(object current)
        {
            int gone = 0;
            foreach (IApiRegistry r in registries)
                gone += r.RemoveOtherWorlds(current);
            return gone;
        }

        /// <summary>
        /// A caller's text to show on one line (a menu item's state): line breaks and other control characters made spaces,
        /// trimmed, and cut to <paramref name="max"/> characters with ".." at the end; empty for null.
        /// </summary>
        public static string OneLine(string text, int max)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            if (IsOneLine(text, max))
                return text;                // already one clean line: the same string, nothing made (a menu item's state, each frame)
            var chars = text.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (char.IsControl(chars[i]))
                    chars[i] = ' ';
            string line = new string(chars).Trim();
            return line.Length <= max ? line : line.Substring(0, Math.Max(0, max - 2)).TrimEnd() + "..";
        }

        /// <summary><paramref name="text"/> is what <see cref="OneLine"/> would make of it: no control characters, nothing to trim, short enough.</summary>
        private static bool IsOneLine(string text, int max)
        {
            if (text.Length > max || char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[text.Length - 1]))
                return false;
            foreach (char c in text)
                if (char.IsControl(c))
                    return false;
            return true;
        }

        public static bool HasControlCharacters(string text)
        {
            foreach (char c in text)
                if (char.IsControl(c))
                    return true;
            return false;
        }

        private Owner OwnerOf(string owner)
        {
            if (!owners.TryGetValue(owner, out Owner o))
                owners[owner] = o = new Owner(owner, clock);
            return o;
        }
    }
}
