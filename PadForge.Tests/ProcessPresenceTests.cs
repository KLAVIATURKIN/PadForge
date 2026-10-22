using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A walk of the process list cost 230 to 290 ms on a PC with about 480
    /// processes and 10,000 threads. These pin the two answers to that: one
    /// walk for any number of names, and a check that runs on a worker so the
    /// thread that asks never waits on it. The Razer Synapse check took one
    /// walk per name on the analog keyboard's reader, which froze the
    /// keyboard's reads, and the Logitech check took one walk per name.
    /// </summary>
    public class ProcessPresenceTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static string Src(params string[] parts)
            => File.ReadAllText(Path.Combine(RepoRoot(), Path.Combine(parts))).Replace("\r\n", "\n");

        [Fact]
        public void RunningAmong_FindsThisProcess_SpelledAsAsked()
        {
            string self;
            using (var me = Process.GetCurrentProcess()) self = me.ProcessName;
            string asked = self.ToUpperInvariant();
            string absent = "padforge-no-such-process-" + Guid.NewGuid().ToString("N");

            var found = ProcessPresence.RunningAmong(new[] { asked, absent });

            Assert.Single(found);
            Assert.Equal(asked, found.Single());
        }

        [Fact]
        public void ProcessImage_NamesThisProcess_AndNothingForAnIdNoProcessCarries()
        {
            using var me = Process.GetCurrentProcess();
            uint pid = (uint)me.Id;
            Assert.Equal(Environment.ProcessPath, ProcessImage.PathOf(pid), ignoreCase: true);
            Assert.Equal(me.ProcessName, ProcessImage.NameOf(pid), ignoreCase: true);
            Assert.Null(ProcessImage.PathOf(0));
            Assert.Null(ProcessImage.NameOf(0));
            // Windows process ids are multiples of four, so this one is never
            // a process.
            Assert.Null(ProcessImage.PathOf(0x7FFFFFFE));
        }

        [Fact]
        public void Poll_NeverWaitsOnTheCheck_AndIsNullUntilTheFirstOneEnds()
        {
            using var gate = new ManualResetEventSlim(false);
            var check = new PeriodicWorkerCheck(() => { gate.Wait(10_000); return true; }, 0);
            try
            {
                var clock = Stopwatch.StartNew();
                Assert.Null(check.Poll(Environment.TickCount64));
                Assert.True(clock.ElapsedMilliseconds < 1_000, $"the poll waited {clock.ElapsedMilliseconds} ms for the check");
            }
            finally { gate.Set(); }
            Assert.True(SpinWait.SpinUntil(() => check.Latest != null, 5_000));
            Assert.True(check.Latest.Value);
            Assert.Equal(1, check.Latest.Generation);
        }

        [Fact]
        public void OneCheckRunsAtATime_WhileSeveralThreadsAsk()
        {
            using var gate = new ManualResetEventSlim(false);
            int checks = 0;
            var check = new PeriodicWorkerCheck(() => { Interlocked.Increment(ref checks); gate.Wait(10_000); return false; }, 0);
            try
            {
                var askers = Enumerable.Range(0, 8).Select(_ => new Thread(() =>
                {
                    for (int i = 0; i < 200; i++) check.Poll(Environment.TickCount64);
                }) { IsBackground = true }).ToList();
                askers.ForEach(t => t.Start());
                askers.ForEach(t => Assert.True(t.Join(5_000), "an asking thread waited on the check"));
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref checks) == 1, 5_000));
                Thread.Sleep(200);
                Assert.Equal(1, Volatile.Read(ref checks));
            }
            finally { gate.Set(); }
            // The next poll after the check ends starts another.
            Assert.True(SpinWait.SpinUntil(() =>
            {
                check.Poll(Environment.TickCount64);
                return Volatile.Read(ref checks) == 2;
            }, 5_000));
        }

        [Fact]
        public void ChecksStartAnIntervalApart_AndEachHasItsOwnGeneration()
        {
            int checks = 0;
            var check = new PeriodicWorkerCheck(() => Interlocked.Increment(ref checks) % 2 == 1, 2_000);

            check.Poll(100_000);
            Assert.True(SpinWait.SpinUntil(() => check.Latest?.Generation == 1, 5_000));
            Assert.True(check.Latest.Value);
            check.Poll(101_999);
            Thread.Sleep(100);
            Assert.Equal(1, Volatile.Read(ref checks));
            check.Poll(102_000);
            Assert.True(SpinWait.SpinUntil(() => check.Latest?.Generation == 2, 5_000));
            Assert.False(check.Latest.Value);
        }

        [Fact]
        public void ACheckThatThrows_PublishesNothing_AndTheNextIntervalTriesAgain()
        {
            int checks = 0;
            var check = new PeriodicWorkerCheck(() =>
            {
                if (Interlocked.Increment(ref checks) == 1) throw new InvalidOperationException("first check fails");
                return true;
            }, 1_000);

            check.Poll(50_000);
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref checks) == 1, 5_000));
            Thread.Sleep(100);
            Assert.Null(check.Latest);
            check.Poll(51_000);
            Assert.True(SpinWait.SpinUntil(() => check.Latest != null, 5_000));
            Assert.Equal(1, check.Latest.Generation);
        }

        [Theory]
        [InlineData(new string[0], null)]
        [InlineData(new[] { "LCore" }, "LCore")]
        [InlineData(new[] { "lgs", "LCore" }, "lgs")]
        [InlineData(new[] { "LCore", "lghub_agent" }, "lghub_agent")]
        public void TheLogitechHost_IsTheFirstRunning_InTheReferenceOrder(string[] running, string expected)
        {
            var set = new HashSet<string>(running, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(expected, LogiLedEngineNative.FirstRunning(set));
        }

        [Fact]
        public void TheSynapseAndLogitechChecks_WalkOnce_AndTheReaderNeverWalks()
        {
            string hid = Src("PadForge.App", "Common", "Input", "AnalogKeyboardHid.cs");
            Assert.Contains("=> ProcessPresence.RunningAmong(AnalogKeyboardCatalog.SynapseProcessNames).Count > 0;", hid);
            Assert.DoesNotContain("GetProcessesByName", hid);

            string device = Src("PadForge.App", "Common", "Input", "AnalogKeyboardDevice.cs");
            int at = device.IndexOf("private void RunPasses(", StringComparison.Ordinal);
            Assert.True(at > 0);
            int end = device.IndexOf("private void NoteNewKeys(", at, StringComparison.Ordinal);
            Assert.True(end > at);
            string reader = device.Substring(at, end - at);
            Assert.Contains("var synapse = SynapseCheck.Poll(Environment.TickCount64);", reader);
            Assert.DoesNotContain("IsSynapseRunning", reader);
            Assert.Contains("new PeriodicWorkerCheck(AnalogKeyboardHidRuntime.IsSynapseRunning, SynapseCheckIntervalMs);", device);

            string logi = Src("PadForge.App", "Services", "LogiLedEngineNative.cs");
            Assert.Contains("=> FirstRunning(PadForge.Common.Input.ProcessPresence.RunningAmong(HostProcesses));", logi);
            Assert.DoesNotContain("GetProcessesByName", logi);
        }
    }
}
