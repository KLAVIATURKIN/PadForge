using System;
using System.Collections.Generic;
using System.Reflection;

namespace PadForge.Common
{
    /// <summary>
    /// The handlers of a static event, held the way Strings.CultureChanged
    /// holds its own: a handler with no target (a static method) strongly, an
    /// instance handler as a weak reference to its target beside its method.
    /// Subscribing never keeps the subscriber alive, so a view model nothing
    /// else holds can be collected and stops being called. A collected
    /// subscriber's entry is dropped when the event is raised, and every 64th
    /// subscription sweeps the list, so an event that is never raised does
    /// not grow without bound. <see cref="Live"/> lists the handlers in the
    /// order they subscribed, and a raiser that calls them in turn stops at an
    /// exception the way a multicast delegate does. An unsubscribe removes the
    /// subscriber's entry at once.
    /// </summary>
    internal sealed class WeakHandlers<T> where T : Delegate
    {
        private readonly object _lock = new();
        private readonly List<(T Strong, WeakReference<object> Target, MethodInfo Method)> _entries = new();

        public void Add(T handler)
        {
            if (handler == null) return;
            lock (_lock)
            {
                foreach (T one in handler.GetInvocationList())
                {
                    if (_entries.Count % 64 == 63)
                        _entries.RemoveAll(e => e.Strong == null && !e.Target.TryGetTarget(out _));
                    _entries.Add(one.Target == null
                        ? (one, null, null)
                        : (null, new WeakReference<object>(one.Target), one.Method));
                }
            }
        }

        public void Remove(T handler)
        {
            if (handler == null) return;
            lock (_lock)
            {
                foreach (T one in handler.GetInvocationList())
                {
                    // The last match, the entry a multicast removal takes.
                    for (int i = _entries.Count - 1; i >= 0; i--)
                    {
                        var e = _entries[i];
                        bool same = e.Strong != null
                            ? one.Target == null && e.Strong.Equals(one)
                            : one.Target != null && e.Method == one.Method
                              && e.Target.TryGetTarget(out object target) && ReferenceEquals(target, one.Target);
                        if (!same) continue;
                        _entries.RemoveAt(i);
                        break;
                    }
                }
            }
        }

        /// <summary>The live handlers in subscription order, each bound to its
        /// target again. Entries whose subscriber was collected are dropped
        /// on the way.</summary>
        public List<T> Live()
        {
            var live = new List<T>();
            lock (_lock)
            {
                int kept = 0;
                for (int i = 0; i < _entries.Count; i++)
                {
                    var e = _entries[i];
                    if (e.Strong != null) live.Add(e.Strong);
                    else if (e.Target.TryGetTarget(out object target))
                        live.Add((T)Delegate.CreateDelegate(typeof(T), target, e.Method));
                    else continue;
                    _entries[kept++] = e;
                }
                _entries.RemoveRange(kept, _entries.Count - kept);
            }
            return live;
        }
    }
}
