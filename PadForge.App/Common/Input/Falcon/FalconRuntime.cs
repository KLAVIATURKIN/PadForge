using System;
using System.Threading;
using PadForge.Engine.Common.ForceDimension;

namespace PadForge.Common.Input.Falcon
{
    /// <summary>What the last sweep found (issue #521), for the Settings
    /// card's status lines. Immutable, published whole.</summary>
    internal sealed record FalconSweepStatus(
        string LibraryReason,
        string LibraryPath,
        string SdkVersion,
        string SdkRoot,
        int InUse,
        string[] NodeDrivers,
        string DriverPackage)
    {
        public static readonly FalconSweepStatus None = new("missing", string.Empty, string.Empty, null, 0, Array.Empty<string>(), null);

        /// <summary>A Falcon's USB node with no driver, which the Install
        /// button can give Force Dimension's.</summary>
        public bool HasDriverlessNode => Array.IndexOf(NodeDrivers, string.Empty) >= 0;

        /// <summary>The drivers on the Falcon's USB nodes other than none.</summary>
        public string OtherDrivers => string.Join(", ", Array.FindAll(NodeDrivers, d => d.Length > 0));
    }

    /// <summary>
    /// The Settings switch, the picked library and the loaded library for
    /// the Novint Falcon (issue #521), shared by the Settings card and the
    /// engine's sweep, the <see cref="KeyboardScreensRuntime"/> shape. The
    /// library is loaded on the sweep's worker, once for the process, and
    /// a failed load is tried again on the next sweep after 30 seconds.
    /// </summary>
    internal static class FalconRuntime
    {
        private const int LoadRetryMs = 30000;

        private static volatile bool _enabled;
        private static int _version;
        private static volatile string _picked;
        private static readonly object _libraryLock = new();
        private static ForceDimensionLibrary _library;
        private static string _reason = "missing";
        private static long _nextLoad;
        private static volatile FalconSweepStatus _status = FalconSweepStatus.None;

        /// <summary>Test seam: a scripted Falcon in place of Force
        /// Dimension's library. Null in the app.</summary>
        internal static IFalconLink LinkOverride;

        /// <summary>Drive Novint Falcon, mirrored from Settings.</summary>
        public static bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                Interlocked.Increment(ref _version);
            }
        }

        public static int Version => Volatile.Read(ref _version);

        /// <summary>A dhd64.dll the user picked, tried before the installed
        /// copies.</summary>
        public static string PickedLibrary
        {
            get => _picked;
            set
            {
                if (string.Equals(_picked, value, StringComparison.OrdinalIgnoreCase)) return;
                _picked = value;
                lock (_libraryLock)
                {
                    // A new pick is tried at the next sweep. A library that
                    // already loaded stays: the haptic threads hold it.
                    if (_library == null) _nextLoad = 0;
                }
            }
        }

        public static FalconSweepStatus Status
        {
            get => _status;
            set => _status = value ?? FalconSweepStatus.None;
        }

        /// <summary>The library, loading it when due, or null with
        /// <paramref name="reason"/> saying why.</summary>
        public static IFalconLink Acquire(long now, out string reason, out string path, out string version)
        {
            path = version = string.Empty;
            if (LinkOverride != null)
            {
                reason = string.Empty;
                return LinkOverride;
            }
            lock (_libraryLock)
            {
                if (_library == null && now >= _nextLoad)
                {
                    _nextLoad = now + LoadRetryMs;
                    _library = ForceDimensionLibrary.TryLoad(_picked, out _reason);
                }
                if (_library == null)
                {
                    reason = _reason;
                    return null;
                }
                reason = string.Empty;
                path = _library.Path;
                version = _library.SdkVersion;
                return _library;
            }
        }
    }
}
