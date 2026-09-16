using System.Collections.Concurrent;
using System.Reflection;
using PadForge.Common.Input;
using Xunit.Abstractions;

namespace PadForge.Tests;

[Collection("AudioProfileLifecycle")]
public sealed class RemoteAudioExpiryTests : IDisposable
{
    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    static readonly Type Service = typeof(AudioPassthroughService);
    readonly ConcurrentDictionary<Guid, AudioPassthroughService.RemoteAudioRing> rings;
    readonly ConcurrentDictionary<Guid, long> demand;
    readonly Action<Guid> oldHook;
    readonly ITestOutputHelper output;

    public RemoteAudioExpiryTests(ITestOutputHelper output)
    {
        this.output = output;
        AudioPassthroughService.Shutdown();
        foreach (string name in new[] { "_workerThread", "_btThread" })
            if (Service.GetField(name, Static)!.GetValue(null) is Thread thread && thread.IsAlive)
                Assert.True(thread.Join(5000));
        rings = (ConcurrentDictionary<Guid, AudioPassthroughService.RemoteAudioRing>)Service.GetField("_remoteRings", Static)!.GetValue(null)!;
        demand = (ConcurrentDictionary<Guid, long>)Service.GetField("_remoteAudioDemand", Static)!.GetValue(null)!;
        oldHook = AudioPassthroughService.RemoteAudioExpiring;
        // Keep worker startup outside this controlled dictionary transition.
        Service.GetField("_running", Static)!.SetValue(null, true);
    }

    public void Dispose()
    {
        AudioPassthroughService.RemoteAudioExpiring = oldHook;
        Service.GetField("_running", Static)!.SetValue(null, false);
        rings.Clear();
        demand.Clear();
    }

    void AssertSamples(Guid pad, float left, float right)
    {
        Assert.True(rings.TryGetValue(pad, out var ring));
        var samples = new float[2];
        ring.ReadFloat(samples, 0, 2);
        Assert.Equal(new[] { left, right }, samples);
    }

    [Fact]
    public async Task ResumedPcmSurvivesAnExpiryAlreadyInProgress()
    {
        var control = Guid.NewGuid();
        AudioPassthroughService.FeedRemoteAudio(control, [0, 64, 0, 32]);
        AssertSamples(control, .5f, .25f);
        var target = Guid.NewGuid();
        AudioPassthroughService.FeedRemoteAudio(target, [0, 32, 0, 16]);
        AssertSamples(target, .25f, .125f);
        long now = Environment.TickCount64;
        long old = now - 3000;
        demand[target] = old;
        using var expiring = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var writerStarted = new ManualResetEventSlim();
        AudioPassthroughService.RemoteAudioExpiring = id =>
        {
            if (id != target) return;
            expiring.Set();
            if (!release.Wait(5000)) throw new TimeoutException();
        };
        Task expiry = Task.Run(() => AudioPassthroughService.TryExpireRemoteAudio(target, old, now));
        Task resumed = null;
        bool racedThrough = false;
        try
        {
            Assert.True(expiring.Wait(2000));
            resumed = Task.Run(() =>
            {
                writerStarted.Set();
                AudioPassthroughService.FeedRemoteAudio(target, [0, 96, 0, 16]);
            });
            Assert.True(writerStarted.Wait(2000));
            racedThrough = await Task.WhenAny(resumed, Task.Delay(200)) == resumed;
        }
        finally
        {
            release.Set();
            await expiry.WaitAsync(TimeSpan.FromSeconds(5));
            if (resumed != null) await resumed.WaitAsync(TimeSpan.FromSeconds(5));
        }
        output.WriteLine($"controlAudible=True resumedBeforeExpiryFinished={racedThrough} demand={demand.ContainsKey(target)} ring={rings.ContainsKey(target)}");
        Assert.True(demand.ContainsKey(target));
        AssertSamples(target, .75f, .125f);
        Assert.True(rings.ContainsKey(control));
    }

    [Theory]
    [InlineData("expired", true, false)]
    [InlineData("fresh", false, true)]
    [InlineData("refreshed", true, true)]
    public void ExpiryPreservesFreshDemandAndRemovesOnlyItsOwnRing(string state, bool expired, bool retained)
    {
        var pad = Guid.NewGuid();
        AudioPassthroughService.FeedRemoteAudio(pad, [0, 64, 0, 32]);
        long now = Environment.TickCount64;
        long observed = state == "fresh" ? now : now - 3000;
        if (state != "refreshed") demand[pad] = observed;
        Assert.Equal(expired, AudioPassthroughService.TryExpireRemoteAudio(pad, observed, now));
        Assert.Equal(retained, demand.ContainsKey(pad));
        Assert.Equal(retained, rings.ContainsKey(pad));
        if (retained) AssertSamples(pad, .5f, .25f);
    }
}
