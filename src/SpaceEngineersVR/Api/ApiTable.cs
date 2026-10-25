// ApiTable: SevrApi's members for one route, built once whichever thread asks first, and handed out so no caller can change another's (no game types; tests/ApiTests runs it).
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SpaceEngineersVR.Api
{
    /// <summary>
    /// The members SevrApi hands one route (a plugin, the mod channel, scripts), built on first use and then kept. The table
    /// built is never handed out, so it is never changed after it is built, and callers on any thread can copy it at once:
    /// <list type="bullet">
    /// <item><see cref="Copy"/>: a new dictionary each call, the caller's own to keep or change. A plugin's <c>GetApi</c>,
    /// and each send on the mod channel, so a mod that changes what it was sent changes it only for the handlers of that one
    /// send, never for a later send or another world.</item>
    /// <item><see cref="ReadOnly"/>: one read-only view, the same for every caller, as nothing can change it (scripts).</item>
    /// </list>
    /// The build runs under a lock, so two first callers on two threads (a plugin may call <c>GetApi</c> from any) build it
    /// once and both see it whole. A build that throws is not kept: the next caller builds again.
    /// </summary>
    internal sealed class ApiTable
    {
        private readonly Func<Dictionary<string, Delegate>> build;
        private readonly object gate = new object();
        private volatile Dictionary<string, Delegate> members;
        private volatile IReadOnlyDictionary<string, Delegate> readOnly;

        /// <param name="build">Makes the members; called once, under the table's lock.</param>
        public ApiTable(Func<Dictionary<string, Delegate>> build)
        {
            this.build = build ?? throw new ArgumentNullException(nameof(build));
        }

        /// <summary>A new dictionary of the members, the caller's own.</summary>
        public Dictionary<string, Delegate> Copy() => new Dictionary<string, Delegate>(Members());

        /// <summary>The members as one read-only view, the same object for every caller.</summary>
        public IReadOnlyDictionary<string, Delegate> ReadOnly()
        {
            IReadOnlyDictionary<string, Delegate> view = readOnly;
            if (view != null)
                return view;
            lock (gate)
                return readOnly ?? (readOnly = new ReadOnlyDictionary<string, Delegate>(Members()));
        }

        private Dictionary<string, Delegate> Members()
        {
            Dictionary<string, Delegate> built = members;
            if (built != null)
                return built;
            lock (gate)
                return members ?? (members = build());
        }
    }
}
