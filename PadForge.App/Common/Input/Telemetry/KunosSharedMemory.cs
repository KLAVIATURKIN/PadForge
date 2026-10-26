using System;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace PadForge.Common.Input.Telemetry
{
    /// <summary>
    /// The three pages Assetto Corsa and Assetto Corsa Competizione write
    /// (issue #531): <c>Local\acpmf_physics</c>, <c>Local\acpmf_static</c> and
    /// <c>Local\acpmf_graphics</c>. Opened with <c>OpenExisting</c>, vAzhure's
    /// GameListener.cs pattern, so PadForge never creates a game's mapping:
    /// tm-bt-led's reader creates them, which hands the game a page of
    /// whatever size the reader asked for.
    ///
    /// <para>Read on the poll thread. While the game is not running the open
    /// is retried every <see cref="RetryMs"/>, and a page whose physics step
    /// stops moving for <see cref="StaleMs"/> is closed, so a game that exits
    /// lets its mapping go.</para>
    /// </summary>
    internal sealed class KunosSharedMemory : IDisposable
    {
        public const string PhysicsName = @"Local\acpmf_physics";
        public const string StaticName = @"Local\acpmf_static";
        public const string GraphicsName = @"Local\acpmf_graphics";
        public const long RetryMs = 2000;
        public const long StaleMs = 5000;

        /// <summary>Set by the test assembly for the life of its process
        /// (PadForge.Tests KunosOffLimits), never by the app. The three names
        /// are the ones the games share, and a test that opened them would
        /// read a running game. While this is set, a reader on those names
        /// opens nothing. A reader on names of a test's own still opens.
        /// False in production.</summary>
        internal static bool OffLimitsForTest;

        private readonly string _physicsName;
        private readonly string _staticName;
        private readonly string _graphicsName;
        private MemoryMappedFile _physicsMap, _staticMap, _graphicsMap;
        private MemoryMappedViewAccessor _physics, _statics, _graphics;
        private long _nextOpenMs;
        private int _lastPacketId;
        private long _lastMoveMs;
        private bool _seen;

        internal KunosSharedMemory(string physicsName = PhysicsName, string staticName = StaticName,
            string graphicsName = GraphicsName)
        {
            _physicsName = physicsName;
            _staticName = staticName;
            _graphicsName = graphicsName;
        }

        public readonly byte[] Physics = new byte[TelemetryDecoders.KunosPhysicsBytes];
        public readonly byte[] Statics = new byte[TelemetryDecoders.KunosStaticBytes];
        public readonly byte[] Graphics = new byte[TelemetryDecoders.KunosGraphicsBytes];

        public bool IsOpen => _physics != null;

        /// <summary>Copies the pages into the three buffers. True only when
        /// the physics step moved since the last read, which is what proves a
        /// game is writing: a mapping a game left behind keeps its last step.</summary>
        public bool TryRead(long nowMs)
        {
            if (_physics == null)
            {
                if (nowMs < _nextOpenMs) return false;
                _nextOpenMs = nowMs + RetryMs;
                if (!TryOpen()) return false;
                _seen = false;
                _lastMoveMs = nowMs;
            }
            try
            {
                _physics.ReadArray(0, Physics, 0, Physics.Length);
                _statics.ReadArray(0, Statics, 0, Statics.Length);
                _graphics.ReadArray(0, Graphics, 0, Graphics.Length);
            }
            catch (Exception)
            {
                Close();
                return false;
            }
            int packetId = BitConverter.ToInt32(Physics, 0);
            if (_seen && packetId == _lastPacketId)
            {
                if (nowMs - _lastMoveMs >= StaleMs)
                {
                    Close();
                    _nextOpenMs = nowMs + RetryMs;
                }
                return false;
            }
            bool first = !_seen;
            _seen = true;
            _lastPacketId = packetId;
            _lastMoveMs = nowMs;
            // The first read is a baseline, the FreeTrack reader's rule: a
            // page the last session left keeps its last step.
            return !first;
        }

        private bool TryOpen()
        {
            if (OffLimitsForTest && (_physicsName == PhysicsName || _staticName == StaticName || _graphicsName == GraphicsName))
                return false;
            try
            {
                _physicsMap = MemoryMappedFile.OpenExisting(_physicsName, MemoryMappedFileRights.Read);
                _staticMap = MemoryMappedFile.OpenExisting(_staticName, MemoryMappedFileRights.Read);
                _graphicsMap = MemoryMappedFile.OpenExisting(_graphicsName, MemoryMappedFileRights.Read);
                _physics = _physicsMap.CreateViewAccessor(0, Physics.Length, MemoryMappedFileAccess.Read);
                _statics = _staticMap.CreateViewAccessor(0, Statics.Length, MemoryMappedFileAccess.Read);
                _graphics = _graphicsMap.CreateViewAccessor(0, Graphics.Length, MemoryMappedFileAccess.Read);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Close();
                return false;
            }
        }

        private void Close()
        {
            try { _physics?.Dispose(); } catch { }
            try { _statics?.Dispose(); } catch { }
            try { _graphics?.Dispose(); } catch { }
            try { _physicsMap?.Dispose(); } catch { }
            try { _staticMap?.Dispose(); } catch { }
            try { _graphicsMap?.Dispose(); } catch { }
            _physics = _statics = _graphics = null;
            _physicsMap = _staticMap = _graphicsMap = null;
        }

        public void Dispose() => Close();
    }
}
