using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Common.Input
{
    /// <summary>
    /// A running process's image path from its id, through a limited-query
    /// handle and <c>QueryFullProcessImageNameW</c>, the access Windows still
    /// grants for a protected process. <c>Process.GetProcessById(id).ProcessName</c>
    /// walks every process when it cannot open its target (245 to 639 ms
    /// measured on a PC with about 480 processes), and
    /// <c>Process.MainModule</c> needs memory-read access, which a protected
    /// process refuses. This asks the one process and returns null when it
    /// cannot answer.
    /// </summary>
    internal static class ProcessImage
    {
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const int ERROR_INSUFFICIENT_BUFFER = 122;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, char[] name, ref uint size);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        /// <summary>The full path of the process's image, or null when the
        /// process is gone or answers no limited query.</summary>
        public static string PathOf(uint processId)
        {
            if (processId == 0) return null;
            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (handle == IntPtr.Zero) return null;
            try
            {
                for (uint capacity = 512; capacity <= 32768; capacity *= 4)
                {
                    var buffer = new char[capacity];
                    uint size = capacity;
                    if (QueryFullProcessImageNameW(handle, 0, buffer, ref size))
                        return new string(buffer, 0, (int)size);
                    if (Marshal.GetLastWin32Error() != ERROR_INSUFFICIENT_BUFFER) return null;
                }
                return null;
            }
            finally { CloseHandle(handle); }
        }

        /// <summary>The image name without its extension, as
        /// <c>Process.ProcessName</c> gives it, or null.</summary>
        public static string NameOf(uint processId)
        {
            string path = PathOf(processId);
            return string.IsNullOrEmpty(path) ? null : Path.GetFileNameWithoutExtension(path);
        }
    }

    /// <summary>
    /// Which of a few named programs run, from one walk of the process list.
    /// A walk costs what the kernel's process query costs: 230 to 290 ms on a
    /// PC with about 480 processes and 10,000 threads, where no lighter call
    /// was faster. <c>Process.GetProcessesByName</c> walks the whole list
    /// once per name, so a check of several names asks for one walk here.
    /// Worker threads only. A thread that holds a rate asks a
    /// <see cref="PeriodicWorkerCheck"/>.
    /// </summary>
    internal static class ProcessPresence
    {
        /// <summary>The entries of <paramref name="names"/> (image names
        /// without .exe, compared without case) that a running process
        /// carries, spelled as the caller spelled them. Empty when none runs
        /// or the walk fails.</summary>
        public static HashSet<string> RunningAmong(IReadOnlyCollection<string> names)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Process[] procs = null;
            try
            {
                procs = Process.GetProcesses();
                foreach (var p in procs)
                {
                    string n;
                    try { n = p.ProcessName; }
                    catch { continue; }
                    foreach (var name in names)
                        if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) { found.Add(name); break; }
                }
            }
            catch { }
            finally
            {
                if (procs != null)
                    foreach (var p in procs) { try { p.Dispose(); } catch { } }
            }
            return found;
        }
    }

    /// <summary>
    /// A yes-or-no check that runs on a worker every interval, one at a time,
    /// so the thread that asks never waits on it: the handheld sweep's worker
    /// half and <c>InputManager.FlydigiWatchTick</c>, made safe for several
    /// asking threads, since every Razer analog keyboard's reader asks the
    /// one Synapse check.
    /// </summary>
    internal sealed class PeriodicWorkerCheck
    {
        /// <summary>One finished check. <see cref="Generation"/> grows by one
        /// per check, so a caller acts on each result once.</summary>
        internal sealed class Result
        {
            public bool Value { get; init; }
            public long Generation { get; init; }
        }

        private readonly Func<bool> _check;
        private readonly int _intervalMs;
        private int _inFlight;
        private long _startedMs = long.MinValue / 2;
        private long _generation;
        private volatile Result _latest;

        public PeriodicWorkerCheck(Func<bool> check, int intervalMs)
        {
            _check = check;
            _intervalMs = intervalMs;
        }

        /// <summary>The newest finished check, or null before the first one
        /// ends.</summary>
        public Result Latest => _latest;

        /// <summary>Starts the check on a worker when none is in flight and
        /// the interval has passed since the last one started, and returns
        /// <see cref="Latest"/>. Never waits on the check. Any thread.</summary>
        public Result Poll(long now)
        {
            if (Volatile.Read(ref _inFlight) == 0
                && now - Interlocked.Read(ref _startedMs) >= _intervalMs
                && Interlocked.CompareExchange(ref _inFlight, 1, 0) == 0)
            {
                // Read again under the flag: a check another thread started
                // may have ended between the two reads.
                if (now - Interlocked.Read(ref _startedMs) < _intervalMs)
                {
                    Volatile.Write(ref _inFlight, 0);
                    return _latest;
                }
                Interlocked.Exchange(ref _startedMs, now);
                Task.Run(() =>
                {
                    try
                    {
                        bool value = _check();
                        _latest = new Result { Value = value, Generation = Interlocked.Increment(ref _generation) };
                    }
                    catch
                    {
                        // A check that throws publishes nothing. The next
                        // interval tries again.
                    }
                    finally { Volatile.Write(ref _inFlight, 0); }
                });
            }
            return _latest;
        }
    }
}
