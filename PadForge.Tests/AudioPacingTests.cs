using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using PadForge.Common.Input;
using Xunit.Abstractions;

namespace PadForge.Tests;

[Collection("AudioProfileLifecycle")]
public sealed class AudioPacingTests(ITestOutputHelper output)
{
    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

    sealed class Clock(int divisor) : TimeProvider
    {
        public long WallOffset;
        public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow().AddTicks(Interlocked.Read(ref WallOffset));
        public override long GetTimestamp() => Stopwatch.GetTimestamp() / divisor;
        public override long TimestampFrequency => Stopwatch.Frequency / divisor;
    }

    /// <summary>Runs the real pacer, BtThreadMain, on a thread of its own
    /// at the priority the service gives it (AudioPassthroughService starts
    /// it at Highest). These tests are about real pacing on purpose: the
    /// pacer waits on a real high-resolution timer, and only its schedule
    /// reads the injected clock. At normal priority, with every processor
    /// busy, the pacer lost ticks to other programs' threads (16 of the 20
    /// a window needs) with nothing wrong in it. The service runs it above
    /// them for that reason.</summary>
    sealed class Run : IDisposable
    {
        readonly TimeProvider oldClock;
        readonly Action<bool> oldBoundary;
        readonly Thread thread;
        Exception error;

        public Run(TimeProvider clock, Action<bool> boundary)
        {
            var type = typeof(AudioPassthroughService);
            AudioPassthroughService.Shutdown();
            foreach (string name in new[] { "_workerThread", "_btThread" })
                if (type.GetField(name, Static)!.GetValue(null) is Thread old && old.IsAlive)
                    Assert.True(old.Join(6000));
            oldClock = AudioPassthroughService.AudioPacingClock;
            oldBoundary = AudioPassthroughService.AudioPacingBoundary;
            AudioPassthroughService.AudioPacingClock = clock;
            AudioPassthroughService.AudioPacingBoundary = boundary;
            thread = new Thread(() =>
            {
                try { type.GetMethod("BtThreadMain", Static)!.Invoke(null, null); }
                catch (Exception ex) { error = ex; }
            }) { IsBackground = true, Priority = ThreadPriority.Highest };
            lock (type.GetField("_lock", Static)!.GetValue(null)!)
            {
                type.GetField("_btThread", Static)!.SetValue(null, thread);
                type.GetField("_running", Static)!.SetValue(null, true);
            }
            thread.Start();
        }

        public void Dispose()
        {
            AudioPassthroughService.Shutdown();
            Assert.True(thread.Join(2000));
            AudioPassthroughService.AudioPacingClock = oldClock;
            AudioPassthroughService.AudioPacingBoundary = oldBoundary;
            Assert.Null(error);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public async Task WallClockChangesPreserveTheTickCadence(int divisor)
    {
        var clock = new Clock(divisor);
        int ticks = 0;
        using var run = new Run(clock, before => { if (!before) Interlocked.Increment(ref ticks); });
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= 3, 5000));
        // Ticks per 400 ms of the window's measured length. The delay that
        // ends a window runs late on a busy PC, and a raw count then grew
        // with the window, so windows at one cadence counted differently.
        // A window that ends early keeps its raw count.
        async Task<int> Window()
        {
            int before = Volatile.Read(ref ticks);
            long start = Stopwatch.GetTimestamp();
            await Task.Delay(400);
            int counted = Volatile.Read(ref ticks) - before;
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            return (int)(counted * 400.0 / Math.Max(400.0, ms));
        }
        int control = await Window();
        Interlocked.Exchange(ref clock.WallOffset, -TimeSpan.TicksPerHour);
        int backward = await Window();
        Interlocked.Exchange(ref clock.WallOffset, TimeSpan.TicksPerHour);
        int forward = await Window();
        output.WriteLine($"frequency={clock.TimestampFrequency} controlTicks={control} backwardTicks={backward} forwardTicks={forward}");
        Assert.True(control >= 20);
        Assert.True(backward >= control / 2);
        Assert.True(forward >= control / 2);
    }

    [Fact]
    public void ADelayedTickDoesNotCauseAnImmediateSecondDelivery()
    {
        int iteration = 0;
        var ends = new ConcurrentQueue<(int Iteration, long Timestamp)>();
        using var run = new Run(TimeProvider.System, before =>
        {
            if (before)
            {
                iteration++;
                if (iteration == 8) Thread.Sleep(45);
            }
            else ends.Enqueue((iteration, Stopwatch.GetTimestamp()));
        });
        Assert.True(SpinWait.SpinUntil(() => ends.Count >= 15, 5000));
        var times = ends.ToArray().ToDictionary(x => x.Iteration, x => x.Timestamp);
        double Gap(int after, int before) => (times[after] - times[before]) * 1000d / Stopwatch.Frequency;
        double control = Gap(7, 6);
        double delayed = Gap(8, 7);
        double following = Gap(9, 8);
        output.WriteLine($"controlGapMs={control:F3} delayedGapMs={delayed:F3} followingGapMs={following:F3}");
        Assert.True(control >= 5);
        Assert.True(delayed >= 40);
        Assert.True(following >= 5);
    }
}
