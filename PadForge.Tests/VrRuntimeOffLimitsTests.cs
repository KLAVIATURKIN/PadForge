using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Engine.Common;
using PadForge.Engine.Common.OpenXr;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// No test reaches a VR runtime (<see cref="VrRuntimeOffLimits"/>). The
    /// opener and the loader the app uses for OpenXR, and the resolver that
    /// loads SteamVR's OpenVR client library, each throw in a test in place
    /// of loading a library or asking a runtime for an instance.
    ///
    /// <para>Every case hands the refused call something that names no real
    /// runtime: a manifest that is off disk, an empty file under a
    /// library's name, a discovery of its own. A gate that stopped refusing
    /// therefore fails these tests and still reaches nothing.</para>
    /// </summary>
    public class VrRuntimeOffLimitsTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir.FullName;
        }

        /// <summary>A manifest path nothing is at.</summary>
        private static string NoSuchManifest()
            => Path.Combine(Path.GetTempPath(), "padforge-no-such-runtime-" + Guid.NewGuid().ToString("N") + ".json");

        /// <summary>
        /// The gate is open by default, as it is in the app. The seam starts
        /// out empty: its type has no initializer that could fill it, and
        /// the next test shows nothing in the app or the engine does. With
        /// an empty seam every call passes. In this process the test
        /// assembly's initializer has filled it, and a call is refused.
        /// </summary>
        [Fact]
        public void TheGateIsOpenUntilATestAssemblyClosesIt()
        {
            Assert.Null(typeof(VrRuntimeGate).TypeInitializer);

            // The app's state: no seam.
            VrRuntimeGate.Enter("a runtime", null);
            // A seam that allows, and one that does not.
            VrRuntimeGate.Enter("a runtime", () => true);
            var refused = Assert.Throws<InvalidOperationException>(() => VrRuntimeGate.Enter("a runtime", () => false));
            Assert.StartsWith("A test reached a runtime. ", refused.Message);

            // This process: closed by VrRuntimeOffLimits.
            Assert.NotNull(VrRuntimeGate.MayReachForTest);
            Assert.Throws<InvalidOperationException>(() => VrRuntimeGate.Enter("a runtime"));
        }

        /// <summary>
        /// Nothing in the app or the engine closes the gate. The app cannot
        /// name the seam at all: it is internal to the engine, whose one
        /// friend is the test assembly. In the sources of both projects the
        /// seam is named twice, where it is declared with no value and where
        /// the gate reads it, so nothing there can set it.
        /// </summary>
        [Fact]
        public void NothingInTheAppOrTheEngineClosesTheGate()
        {
            string[] friends = typeof(VrRuntimeGate).Assembly.GetCustomAttributes<InternalsVisibleToAttribute>()
                .Select(friend => friend.AssemblyName).ToArray();
            Assert.Equal(new[] { "PadForge.Tests" }, friends);
            FieldInfo seam = typeof(VrRuntimeGate).GetField(nameof(VrRuntimeGate.MayReachForTest),
                                                           BindingFlags.Static | BindingFlags.NonPublic);
            Assert.True(seam.IsAssembly, "the seam is internal to the engine");

            string root = RepoRoot();
            var named = new List<string>();
            int read = 0;
            foreach (string project in new[] { "PadForge.App", "PadForge.Engine" })
            {
                foreach (string file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
                {
                    string[] parts = Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar);
                    if (parts.Contains("bin") || parts.Contains("obj")) continue;
                    read++;
                    foreach (string line in File.ReadLines(file))
                        if (line.Contains(nameof(VrRuntimeGate.MayReachForTest), StringComparison.Ordinal))
                            named.Add(Path.GetFileName(file) + ": " + line.Trim());
                }
            }

            Assert.True(read > 200, $"only {read} source files of the app and the engine were found");
            Assert.Equal(new[]
            {
                "VrRuntimeGate.cs: internal static volatile Func<bool> MayReachForTest;",
                "VrRuntimeGate.cs: public static void Enter(string what) => Enter(what, MayReachForTest);",
            }, named);
        }

        /// <summary>The opener the app uses is refused before it reads
        /// anything, with an exception that says what was reached and what
        /// to do in its place.</summary>
        [Fact]
        public void TheOpenerTheAppUsesIsRefusedInATest()
        {
            var log = new List<string>();

            var refused = Assert.Throws<InvalidOperationException>(() =>
                OpenXrSession.TryCreate(NoSuchManifest(), again: false, log.Add, out _, out _, out _));

            Assert.StartsWith("A test reached OpenXrSession.TryCreate, the opener the app uses. " +
                              "That loads a VR runtime's library or asks a runtime for an instance",
                              refused.Message);
            Assert.Empty(log);
        }

        /// <summary>A source left on the opener the app uses reaches no
        /// runtime either. Its loop stops as Failed, and its one line says
        /// why.</summary>
        [Fact]
        public void ASourceLeftOnTheAppsOpenerStopsAsFailedAndSaysWhy()
        {
            var log = new List<string>();
            using var source = new OpenXrHeadPoseSource(NoSuchManifest, _ => { }, log.Add);

            source.RunOnThisThread();

            Assert.Equal(OpenXrSourceState.Failed, source.State);
            Assert.StartsWith("OpenXR: head pose source stopped: A test reached OpenXrSession.TryCreate, the opener the app uses.",
                              Assert.Single(log));
        }

        /// <summary>The loader the app uses is refused before it asks
        /// Windows for the library, here an empty file under a library's
        /// name. Handed to the session's opener it reads as a setup that
        /// threw, with the refusal in what is handed back.</summary>
        [Fact]
        public void TheLoaderTheAppUsesIsRefusedInATest()
        {
            using var runtime = new OpenXrScriptedRuntime("Runtime X");
            var load = typeof(OpenXrSession)
                .GetMethod("LoadRuntime", BindingFlags.Static | BindingFlags.NonPublic)
                .CreateDelegate<OpenXrSession.RuntimeLoader>();

            var refused = Assert.Throws<InvalidOperationException>(() => load(runtime.Entry, out _));
            Assert.StartsWith($"A test reached the library of Runtime X, {runtime.LibraryPath}. ", refused.Message);

            var session = OpenXrSession.TryCreate(runtime.ManifestPath, again: false, _ => { }, load,
                () => Array.Empty<OpenXrRuntimeEntry>(), out var failure, out bool runtimeUnavailable, out string read);

            Assert.Null(session);
            Assert.Equal(OpenXrSourceState.Failed, failure);
            Assert.False(runtimeUnavailable);
            Assert.StartsWith("the setup threw: A test reached the library of Runtime X, ", read);
            Assert.Equal(0, runtime.InstancesCreated);
        }

        /// <summary>SteamVR's OpenVR client library is refused before
        /// anything looks for it, whatever this PC has installed, and the
        /// resolver still answers nothing for any other module.</summary>
        [Fact]
        public void TheOpenVrClientLibraryIsRefusedInATest()
        {
            int discoveries = 0;
            string Discover()
            {
                discoveries++;
                return null;
            }

            var refused = Assert.Throws<InvalidOperationException>(
                () => OpenVrConsumerService.ResolveOpenVrApi("openvr_api", Discover));
            Assert.StartsWith("A test reached openvr_api.dll, SteamVR's OpenVR client library. ", refused.Message);
            Assert.Throws<InvalidOperationException>(() => OpenVrConsumerService.ResolveOpenVrApi("OPENVR_API", Discover));
            Assert.Equal(0, discoveries);

            Assert.Equal(IntPtr.Zero, OpenVrConsumerService.ResolveOpenVrApi("kernel32", Discover));
            Assert.Equal(0, discoveries);
        }

        /// <summary>
        /// A head tracker row opened with OpenXR on starts its source on a
        /// thread of its own with the opener the app uses, as the engine's
        /// sweep does. In a test that source is refused and the row reads
        /// Failed. The row is told a manifest that is off disk, and neither
        /// UDP nor FreeTrack is on, so it opens nothing else.
        /// </summary>
        [Fact]
        public void AHeadTrackerRowWithOpenXrOnReachesNoRuntime()
        {
            using var row = new HeadTrackerDevice(false, 4242, false, 0, null, null, _ => { },
                                                  openXr: true, openXrManifest: NoSuchManifest());

            Assert.True(row.Open());

            Assert.True(SpinWait.SpinUntil(() => row.OpenXrState != OpenXrSourceState.Connecting, TimeSpan.FromMinutes(2)),
                        "the row's OpenXR source never left Connecting");
            Assert.Equal(OpenXrSourceState.Failed, row.OpenXrState);
        }

        /// <summary>Only the runtime probe may open the gate, by its name,
        /// and only when a person asked for it. One call in the whole test
        /// project opens it, the probe's. Nothing else in the test project
        /// names the seam or the flow that opens it, so no other test can
        /// open the gate by hand.</summary>
        [Fact]
        public void OnlyTheProbeMayOpenTheGate()
        {
            Assert.Equal(nameof(OpenXrRuntimeProbeTests) + "." + nameof(OpenXrRuntimeProbeTests.TheNativePathReachesARuntimeAndComesBack),
                         VrRuntimeOffLimits.Probe);

            var other = Assert.Throws<InvalidOperationException>(
                () => VrRuntimeOffLimits.AllowFor("OpenXrSessionRecoveryTests.AnExitingSessionEndsAndOpensNothing"));
            Assert.Contains("may not reach a VR runtime", other.Message);

            // A normal run has not asked for the probe, and then the probe's
            // own name opens nothing.
            if (Environment.GetEnvironmentVariable(VrRuntimeOffLimits.ProbeVariable) != "1")
            {
                var unasked = Assert.Throws<InvalidOperationException>(() => VrRuntimeOffLimits.AllowFor(VrRuntimeOffLimits.Probe));
                Assert.Contains(VrRuntimeOffLimits.ProbeVariable + "=1", unasked.Message);
            }

            string tests = Path.Combine(RepoRoot(), "PadForge.Tests");
            var sources = Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories)
                .Where(file =>
                {
                    string[] parts = Path.GetRelativePath(tests, file).Split(Path.DirectorySeparatorChar);
                    return !parts.Contains("bin") && !parts.Contains("obj");
                })
                .ToDictionary(file => file, File.ReadAllText);
            Assert.True(sources.Count > 500, "the test sources were not found");
            string[] Naming(string text) => sources.Where(source => source.Value.Contains(text, StringComparison.Ordinal))
                .Select(source => Path.GetFileName(source.Key)).OrderBy(name => name, StringComparer.Ordinal).ToArray();

            // The probe, and this file, whose calls above are refused.
            Assert.Equal(new[] { "OpenXrRuntimeProbeTests.cs", "VrRuntimeOffLimitsTests.cs" },
                         Naming("VrRuntimeOffLimits.AllowFor("));
            // The class that closes the gate, and this file.
            Assert.Equal(new[] { "VrRuntimeOffLimits.cs", "VrRuntimeOffLimitsTests.cs" },
                         Naming(nameof(VrRuntimeGate.MayReachForTest)));
            Assert.Equal(new[] { "VrRuntimeOffLimits.cs", "VrRuntimeOffLimitsTests.cs" }, Naming("s_allowed"));
        }

        /// <summary>
        /// An open gate stays with the test that opened it. It is open on
        /// that test's own flow and on a thread it starts, which is where
        /// the probe's source runs, and closed on a thread that was already
        /// running, as every other test's is. Nothing real is called here:
        /// the gate is opened by hand and asked directly.
        /// </summary>
        [Fact]
        public void AnOpenGateStaysWithTheTestThatOpenedIt()
        {
            static bool Passes()
            {
                try
                {
                    VrRuntimeGate.Enter("nothing at all");
                    return true;
                }
                catch (InvalidOperationException) { return false; }
            }

            var allowed = (AsyncLocal<string>)typeof(VrRuntimeOffLimits)
                .GetField("s_allowed", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            Assert.False(Passes());

            var go = new ManualResetEventSlim();
            bool elsewhere = true;
            var alreadyRunning = new Thread(() =>
            {
                go.Wait();
                elsewhere = Passes();
            })
            { IsBackground = true };
            alreadyRunning.Start();

            allowed.Value = VrRuntimeOffLimits.Probe;
            try
            {
                Assert.True(Passes());
                bool onItsOwnThread = false;
                var started = new Thread(() => onItsOwnThread = Passes());
                started.Start();
                started.Join();
                Assert.True(onItsOwnThread);

                go.Set();
                alreadyRunning.Join();
                Assert.False(elsewhere);
            }
            finally
            {
                allowed.Value = null;
                go.Set();
                alreadyRunning.Join();
            }
            Assert.False(Passes());
        }
    }
}
