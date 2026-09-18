using System;

namespace PadForge.Common.Input
{
    /// <summary>
    /// The Dashboard mirror the engine reads for head tracking (issue #355),
    /// the shape of <see cref="HandheldButtonRegistry.FeatureEnabled"/>:
    /// static, written by the Dashboard view model on the UI thread, read by
    /// the poll thread's device sweep and by the device on every read.
    ///
    /// <para><see cref="Version"/> bumps on a change that needs the row
    /// reopened (either input toggle or an active UDP port). The two ranges are
    /// read live on every poll, so a range edit takes effect at once.</para>
    /// </summary>
    internal static class HeadTrackingRuntime
    {
        public const int DefaultUdpPort = 4242;
        public const int DefaultRotationRangeDeg = 90;
        public const int DefaultTranslationRangeCm = 30;

        private static volatile bool _enabled;
        private static volatile int _udpPort = DefaultUdpPort;
        private static volatile bool _freeTrackEnabled;
        private static volatile bool _openXrEnabled;
        private static volatile string _openXrRuntimeManifest = string.Empty;
        private static volatile int _rotationRangeDeg = DefaultRotationRangeDeg;
        private static volatile int _translationRangeCm = DefaultTranslationRangeCm;
        private static volatile int _version;

        /// <summary>The UDP input toggle. FreeTrack is enabled independently.</summary>
        public static bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                _version++;
            }
        }

        /// <summary>Whether the headset is read through OpenXR (issue #403).
        ///
        /// <para>This is the path for a user whose headset runs on Virtual
        /// Desktop's runtime with no SteamVR at all, which the SteamVR
        /// consumer cannot serve: it loads its native library out of a
        /// SteamVR install, so no SteamVR means no head input.</para></summary>
        public static bool OpenXrEnabled
        {
            get => _openXrEnabled;
            set
            {
                if (_openXrEnabled == value) return;
                _openXrEnabled = value;
                _version++;
            }
        }

        /// <summary>Manifest of the OpenXR runtime to read the headset from,
        /// or empty for the machine's default.
        ///
        /// <para>Naming one matters when a game uses Virtual Desktop's
        /// runtime while the system default is another, which is the ordinary
        /// arrangement for someone who also has SteamVR installed. Choosing
        /// here changes only this process.</para></summary>
        public static string OpenXrRuntimeManifest
        {
            get => _openXrRuntimeManifest ?? string.Empty;
            set
            {
                string v = value ?? string.Empty;
                if (string.Equals(_openXrRuntimeManifest, v, StringComparison.OrdinalIgnoreCase)) return;
                _openXrRuntimeManifest = v;
                if (_openXrEnabled) _version++;
            }
        }

        public static bool AnyEnabled => _enabled || _freeTrackEnabled || _openXrEnabled;

        /// <summary>UDP port OpenTrack's "UDP over network" output sends to.</summary>
        public static int UdpPort
        {
            get => _udpPort;
            set
            {
                int v = Math.Clamp(value, 1, 65535);
                if (_udpPort == v) return;
                _udpPort = v;
                if (_enabled) _version++;
            }
        }

        /// <summary>Whether FreeTrack 2.0 shared memory input is enabled.</summary>
        public static bool FreeTrackEnabled
        {
            get => _freeTrackEnabled;
            set
            {
                if (_freeTrackEnabled == value) return;
                _freeTrackEnabled = value;
                _version++;
            }
        }

        /// <summary>Head rotation, in degrees, that moves a rotation axis
        /// to full deflection.</summary>
        public static int RotationRangeDeg
        {
            get => _rotationRangeDeg;
            set => _rotationRangeDeg = Math.Clamp(value, 1, 180);
        }

        /// <summary>Head travel, in centimeters, that moves a translation
        /// axis to full deflection.</summary>
        public static int TranslationRangeCm
        {
            get => _translationRangeCm;
            set => _translationRangeCm = Math.Clamp(value, 1, 500);
        }

        /// <summary>Bumps when the row must reopen to pick up a change.</summary>
        public static int Version => _version;
    }
}
