using System;
using System.Collections.Generic;
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
    /// <para>They exist because the parts they cover cannot be proven any
    /// other way: a hand-written negotiation handshake and a hand-written
    /// struct layout either match the runtime's ABI or they do not, and
    /// nothing in a compile says which.</para>
    ///
    /// <para><b>They are opt-in, and a normal suite run skips them.</b> Asking
    /// a runtime to negotiate STARTS that runtime. With SteamVR registered,
    /// running the suite launched SteamVR on a machine whose owner had just
    /// closed it. A test may not start a heavyweight application as a side
    /// effect of being run, so these only run when
    /// <c>PADFORGE_OPENXR_PROBE=1</c> is set, which is a deliberate act.</para>
    ///
    /// <para>They need no VR hardware. Negotiating, creating an instance and
    /// asking which extensions exist are all answered by the runtime library
    /// itself.</para>
    /// </summary>
    public class OpenXrRuntimeProbeTests
    {
        private readonly ITestOutputHelper _out;
        public OpenXrRuntimeProbeTests(ITestOutputHelper output) => _out = output;

        /// <summary>True only when a person asked for these to run. Starting
        /// a runtime is their whole point and also their whole cost.</summary>
        private static bool ProbeRequested =>
            string.Equals(Environment.GetEnvironmentVariable("PADFORGE_OPENXR_PROBE"), "1",
                          StringComparison.Ordinal);

        /// <summary>A runtime to talk to: whatever the registry lists, else
        /// a SteamVR installed beside the repo's own bench. Null unless the
        /// probe was asked for, so nothing here can start a runtime by
        /// accident.</summary>
        private static OpenXrRuntimeEntry FindRuntime()
        {
            if (!ProbeRequested) return null;
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
            if (entry == null) { _out.WriteLine("skipped: set PADFORGE_OPENXR_PROBE=1 to run this, which starts the runtime"); return; }

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
        /// answer, not a failure. What must not happen is a crash, a hang, a
        /// negotiation the runtime rejects or a call that fails, since those
        /// are the signs of a struct laid out wrong
        /// (<see cref="AssertTheSourceCameBack"/>).</para>
        /// </summary>
        [Fact]
        public void TheNativePathReachesARuntimeAndComesBack()
        {
            var entry = FindRuntime();
            if (entry == null) { _out.WriteLine("skipped: set PADFORGE_OPENXR_PROBE=1 to run this, which starts the runtime"); return; }

            var poses = 0;
            var lines = new List<string>();
            var source = new OpenXrHeadPoseSource(
                () => entry.ManifestPath,
                _ => Interlocked.Increment(ref poses),
                line =>
                {
                    lock (lines) lines.Add(line);
                    _out.WriteLine(line);
                });

            OpenXrSourceState state;
            long samples;
            string runtimeName;
            // The one place a test reaches a real runtime, and only because
            // a person asked for it. Every other test is refused at the
            // opener (VrRuntimeOffLimits).
            using (VrRuntimeOffLimits.AllowFor(
                       nameof(OpenXrRuntimeProbeTests) + "." + nameof(TheNativePathReachesARuntimeAndComesBack)))
            {
                source.Start();
                var deadline = DateTime.UtcNow.AddSeconds(8);
                while (DateTime.UtcNow < deadline
                       && (source.State == OpenXrSourceState.Connecting))
                    Thread.Sleep(50);
                state = source.State;
                samples = source.SampleCount;
                runtimeName = source.RuntimeName;
                source.Stop();
            }
            string[] written;
            lock (lines) written = lines.ToArray();

            _out.WriteLine($"state={state} runtime='{runtimeName}' samples={samples}");

            AssertTheSourceCameBack(state, written);

            // Stopping must be prompt and must not leave the thread behind.
            Assert.Equal(OpenXrSourceState.Stopped, source.State);
        }

        /// <summary>
        /// What the probe asks of the state a source reached. It must have
        /// left Connecting, and it must be neither Failed nor NoRuntime.
        ///
        /// <para>Failed is a setup or a loop that threw, an xrGetSystem
        /// that answered an error other than a headset that is away, or a
        /// session the runtime refused to begin. The source's last line
        /// says which, with the call and its answer, so the probe fails
        /// with that line. It used to say the source threw and point at a
        /// struct or a signature, about a runtime that had answered.</para>
        ///
        /// <para>NoRuntime, for a runtime whose library the probe has just
        /// found on disk, is a library that did not load or a negotiation
        /// it refused. That is the handshake this probe exists to prove,
        /// and it used to pass.</para>
        ///
        /// <para>It stands apart from the probe so a normal run reaches
        /// it, with a scripted runtime in the place of a real one
        /// (OpenXrSessionScriptedRuntimeTests).</para>
        /// </summary>
        internal static void AssertTheSourceCameBack(OpenXrSourceState state, IReadOnlyList<string> lines)
        {
            string last = lines.Count > 0 ? lines[^1] : "(it wrote no line)";
            Assert.True(state != OpenXrSourceState.Connecting,
                "the source never left Connecting, which means a native call did not return");
            Assert.True(state != OpenXrSourceState.NoRuntime,
                "the runtime's library is on disk and the source found no runtime in it, "
                + "and its last line says why: " + last);
            Assert.True(state != OpenXrSourceState.Failed,
                "the source settled as Failed, and its last line says why: " + last);
        }
    }
}
