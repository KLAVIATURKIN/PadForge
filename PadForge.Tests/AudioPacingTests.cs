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
            }) { IsBackground = true };
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
        async Task<int> Window()
        {
            int before = Volatile.Read(ref ticks);
            await Task.Delay(400);
            return Volatile.Read(ref ticks) - before;
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
