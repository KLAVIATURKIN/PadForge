using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Work the poll thread did that could wait on the machine: the System
    /// Volume and App Volume macro actions called the audio service every
    /// frame while their macro ran, App Volume named every audio session's
    /// process through a call that walks every process when it cannot open
    /// its target, and a refused Xbox impulse write re-ran the XUSB interface
    /// sweep on every frame the poll thread re-armed it. These pin that the
    /// poll thread posts the volume and a worker applies it, and that a
    /// failed impulse write waits out a cooldown unless a stop or a user's
    /// test forces it.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class RateThreadOffloadTests
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

        private static bool VolumeWorkerRunning(InputManager im)
            => (bool)typeof(InputManager).GetField("_volumeWorkerRunning", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(im);

        [Fact]
        public void AVolumePost_NeverWaitsOnTheAudioService()
        {
            // Not disposed: a post that ran the apply inline would still be
            // waiting on it after the test ends.
            var gate = new ManualResetEventSlim(false);
            int applied = 0;
            var im = new InputManager { VolumeWorkerPeriodMs = 1, VolumeWorkerIdleMs = 100 };
            im.SystemVolumeApply = (v, osd) => { Interlocked.Increment(ref applied); gate.Wait(10_000); };
            try
            {
                // The poll thread's stand-in. Joined with a bound, so a post
                // that waits on the audio service fails here instead of hanging.
                var poster = new Thread(() =>
                {
                    try { for (int i = 0; i < 100; i++) im.PostSystemVolume(i / 100f, false); }
                    catch { }
                }) { IsBackground = true };
                poster.Start();
                Assert.True(poster.Join(1_000), "posting waited on the audio service");
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref applied) >= 1, 5_000), "the worker never applied a target");
            }
            finally { gate.Set(); }
            Assert.True(SpinWait.SpinUntil(() => !VolumeWorkerRunning(im), 5_000));
        }

        [Fact]
        public void TheNewestTargetWins_AndNoOlderOneFollowsIt()
        {
            var applied = new ConcurrentQueue<(string Target, float Volume)>();
            var im = new InputManager { VolumeWorkerPeriodMs = 1, VolumeWorkerIdleMs = 100 };
            im.AppVolumeApply = (v, p) => applied.Enqueue((p, v));
            im.SystemVolumeApply = (v, osd) => applied.Enqueue(("system", v));
            for (int i = 1; i <= 200; i++)
            {
                im.PostAppVolume(i / 200f, "game");
                im.PostAppVolume(1f - i / 200f, "chat");
                im.PostSystemVolume(i / 400f, true);
            }
            Assert.True(SpinWait.SpinUntil(() => !VolumeWorkerRunning(im), 5_000));

            float[] Seen(string target) => applied.Where(a => a.Target == target).Select(a => a.Volume).ToArray();
            var game = Seen("game");
            var chat = Seen("chat");
            var system = Seen("system");
            Assert.Equal(1f, game[^1]);
            Assert.Equal(0f, chat[^1]);
            Assert.Equal(0.5f, system[^1]);
            for (int i = 1; i < game.Length; i++) Assert.True(game[i] > game[i - 1], "an older game volume followed a newer one");
            for (int i = 1; i < chat.Length; i++) Assert.True(chat[i] < chat[i - 1], "an older chat volume followed a newer one");
            for (int i = 1; i < system.Length; i++) Assert.True(system[i] > system[i - 1], "an older system volume followed a newer one");
        }

        [Fact]
        public void TheVolumeWorkerEndsWhenNothingIsPosted_AndAPostStartsItAgain()
        {
            int applied = 0;
            var im = new InputManager { VolumeWorkerPeriodMs = 1, VolumeWorkerIdleMs = 50 };
            im.SystemVolumeApply = (v, osd) => Interlocked.Increment(ref applied);

            im.PostSystemVolume(0.5f, false);
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref applied) == 1 && !VolumeWorkerRunning(im), 5_000));
            im.PostSystemVolume(0.6f, false);
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref applied) == 2, 5_000));
            Assert.True(SpinWait.SpinUntil(() => !VolumeWorkerRunning(im), 5_000));
        }

        [Fact]
        public void BothMacroPaths_PostTheVolume_AndOnlyTheWorkerAppliesIt()
        {
            string macros = Src("PadForge.App", "Common", "Input", "InputManager.Step4b.EvaluateMacros.cs");
            Assert.Equal(2, Regex.Matches(macros, Regex.Escape("PostSystemVolume(vol * (action.VolumeLimit / 100f), action.ShowVolumeOsd);")).Count);
            Assert.Equal(2, Regex.Matches(macros, Regex.Escape("PostAppVolume(vol * (action.VolumeLimit / 100f), action.ProcessName);")).Count);
            // The definition and the worker's call, nothing else.
            Assert.Equal(2, Regex.Matches(macros, @"\bSetSystemVolume\(").Count);
            Assert.Equal(2, Regex.Matches(macros, @"\bSetAppVolume\(").Count);
            Assert.Contains("else SetSystemVolume(systemTarget, osd);", macros);
            Assert.Contains("else SetAppVolume(app.Value, app.Key);", macros);
            // Session processes are named by one limited query each.
            Assert.DoesNotContain("Process.GetProcessById(", macros);
            Assert.Equal(2, Regex.Matches(macros, Regex.Escape("ProcessImage.NameOf(pid);")).Count);
        }

        [Fact]
        public void TheForegroundCheck_ReadsThePathThroughALimitedQuery()
        {
            string fg = Src("PadForge.App", "Services", "ForegroundMonitorService.cs");
            Assert.Contains("string path = PadForge.Common.Input.ProcessImage.PathOf(pid);", fg);
            Assert.DoesNotContain(".MainModule", fg);
            Assert.DoesNotContain("Process.GetProcessById(", fg);
        }

        [Fact]
        public void AFailedImpulseWrite_WaitsOutItsCooldown_UnlessForced()
        {
            var saved = XboxImpulseHidWriter.Resolver;
            int resolves = 0;
            XboxImpulseHidWriter.Resolver = _ => { Interlocked.Increment(ref resolves); return null; };
            var ud = new UserDevice { VendorId = 0x045E, ProdId = 0x0B13, DevicePath = "XInput#impulse-cooldown-" + Guid.NewGuid().ToString("N") };
            try
            {
                XboxImpulseHidWriter.InvalidateCachedTargets();
                Assert.False(XboxImpulseHidWriter.Write(ud, 30000, 30000, 0, 0));
                Assert.Equal(1, resolves);
                Assert.True(XboxImpulseHidWriter.CoolingDown(ud.DevicePath, Environment.TickCount64));

                // The poll thread re-arms a refused write every frame. Those
                // retries cost nothing until the cooldown ends.
                for (int i = 0; i < 50; i++) Assert.False(XboxImpulseHidWriter.Write(ud, 30000, 30000, 0, 0));
                Assert.Equal(1, resolves);

                // A stop tries at once.
                Assert.False(XboxImpulseHidWriter.Write(ud, 0, 0, 0, 0, force: true));
                Assert.Equal(2, resolves);

                // A device change clears the cooldown.
                XboxImpulseHidWriter.InvalidateCachedTargets();
                Assert.False(XboxImpulseHidWriter.CoolingDown(ud.DevicePath, Environment.TickCount64));
                Assert.False(XboxImpulseHidWriter.Write(ud, 30000, 30000, 0, 0));
                Assert.Equal(3, resolves);

                // And the cooldown ends on its own.
                Assert.False(XboxImpulseHidWriter.CoolingDown(ud.DevicePath,
                    Environment.TickCount64 + XboxImpulseHidWriter.FailedRetryCooldownMs));
            }
            finally
            {
                XboxImpulseHidWriter.Resolver = saved;
                XboxImpulseHidWriter.InvalidateCachedTargets();
            }
        }
    }
}
