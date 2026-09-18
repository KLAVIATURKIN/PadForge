using System;
using System.IO;
using System.Threading;
using PadForge.Engine.Common;
using PadForge.Engine.Common.OpenXr;
using Xunit;
using Xunit.Abstractions;

namespace PadForge.Tests
{
    /// <summary>
    /// Talks to a real OpenXR runtime when the machine has one (issue #403).
    ///
    /// <para>These skip themselves where no runtime is installed, which is
    /// most machines and every CI agent. They exist because the parts they
    /// cover cannot be proven any other way: a hand-written negotiation
    /// handshake and a hand-written struct layout either match the runtime's
    /// ABI or they do not, and nothing in a compile says which.</para>
    ///
    /// <para>They need no VR hardware. Negotiating, creating an instance and
    /// asking which extensions exist are all answered by the runtime library
    /// itself.</para>
    /// </summary>
    public class OpenXrRuntimeProbeTests
    {
        private readonly ITestOutputHelper _out;
        public OpenXrRuntimeProbeTests(ITestOutputHelper output) => _out = output;

        /// <summary>A runtime to talk to: whatever the registry lists, else
        /// a SteamVR installed beside the repo's own bench.</summary>
        private static OpenXrRuntimeEntry FindRuntime()
        {
            foreach (var entry in OpenXrRuntimeCatalog.Discover())
                if (entry.LibraryExists) return entry;

            const string bench = @"C:\SteamVR\steamxr_win64.json";
            if (File.Exists(bench))
            {
                var entry = OpenXrRuntimeCatalog.TryParseManifest(bench, File.ReadAllText(bench));
                if (entry != null && entry.LibraryExists) return entry;
            }
            return null;
        }

        [Fact]
        public void AManifestNamesALibraryThatExists()
        {
            var entry = FindRuntime();
            if (entry == null) { _out.WriteLine("no OpenXR runtime on this machine, skipped"); return; }

            _out.WriteLine($"runtime: {entry.Name}");
            _out.WriteLine($"manifest: {entry.ManifestPath}");
            _out.WriteLine($"library: {entry.LibraryPath}");
            Assert.True(entry.LibraryExists, entry.LibraryPath);
        }

        /// <summary>
        /// The whole native path, end to end: load the runtime, negotiate,
        /// create an instance, ask for a headless session.
        ///
        /// <para>What it asserts is narrow on purpose. Without a headset the
        /// runtime will refuse a session, and that refusal is a correct
        /// answer, not a failure. What must not happen is a crash, a hang, or
        /// a negotiation the runtime rejects, since those are the signs of a
        /// struct laid out wrong.</para>
        /// </summary>
        [Fact]
        public void TheNativePathReachesARuntimeAndComesBack()
        {
            var entry = FindRuntime();
            if (entry == null) { _out.WriteLine("no OpenXR runtime on this machine, skipped"); return; }

            var poses = 0;
            var source = new OpenXrHeadPoseSource(
                () => entry.ManifestPath,
                _ => Interlocked.Increment(ref poses),
                line => _out.WriteLine(line));

            source.Start();
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline
                   && (source.State == OpenXrSourceState.Connecting))
                Thread.Sleep(50);
            var state = source.State;
            long samples = source.SampleCount;
            string runtimeName = source.RuntimeName;
            source.Stop();

            _out.WriteLine($"state={state} runtime='{runtimeName}' samples={samples}");

            Assert.True(state != OpenXrSourceState.Connecting,
                "the source never left Connecting, which means a native call did not return");
            Assert.True(state != OpenXrSourceState.Failed,
                "the source threw, which on this path means a struct or a signature is wrong");

            // Stopping must be prompt and must not leave the thread behind.
            Assert.Equal(OpenXrSourceState.Stopped, source.State);
        }
    }
}
