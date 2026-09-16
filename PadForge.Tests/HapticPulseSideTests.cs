using System.Collections;
using System.Reflection;
using NAudio.Wave;
using PadForge.Common.Input;
using Xunit.Abstractions;

namespace PadForge.Tests;

[Collection("AudioProfileLifecycle")]
public sealed class HapticPulseSideTests : IDisposable
{
    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    static readonly Type Service = typeof(HapticToneService);
    static readonly Type SinkType = Service.GetNestedType("Sink", BindingFlags.NonPublic)!;
    readonly object gate = Service.GetField("_lock", Static)!.GetValue(null)!;
    readonly IList sinks = (IList)Service.GetField("_sinks", Static)!.GetValue(null)!;
    readonly Func<IntPtr, byte[], bool> writer = HapticToneService.PcmPacketWriter;
    readonly List<object> owned = new();
    readonly List<byte[]> packets = new();
    readonly ITestOutputHelper output;
    readonly long now = Environment.TickCount64;

    sealed class Silence : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(8000, 2);
        public int Read(float[] buffer, int offset, int count) { Array.Clear(buffer, offset, count); return count; }
    }

    public HapticPulseSideTests(ITestOutputHelper output)
    {
        this.output = output;
        Assert.Empty(sinks);
        Assert.Null(Service.GetField("_reconcileTimer", Static)!.GetValue(null));
        HapticToneService.PcmPacketWriter = (_, bytes) => { packets.Add((byte[])bytes.Clone()); return true; };
    }

    object Create(bool muLaw)
    {
        var sink = Activator.CreateInstance(SinkType, true)!;
        Set(sink, "DeviceGuid", Guid.NewGuid());
        Set(sink, "PcmSource", new Silence());
        Set(sink, "PcmArmed", true);
        Set(sink, "PcmCapable", true);
        Set(sink, "Running", true);
        Set(sink, "PcmMuLaw", muLaw);
        Set(sink, "OutLen", 64);
        Set(sink, "PcmLastContentMs", now);
        lock (gate) sinks.Add(sink);
        owned.Add(sink);
        return sink;
    }
    static void Set(object sink, string name, object value) => SinkType.GetField(name)!.SetValue(sink, value);
    static T Get<T>(object sink, string name) => (T)SinkType.GetField(name)!.GetValue(sink)!;
    void Pulse(object sink, int side, float amplitude)
        => HapticToneService.QueueTouchpadPulse(Get<Guid>(sink, "DeviceGuid"), side, amplitude);
    float[] Tick(object sink)
    {
        packets.Clear();
        Service.GetMethod("DispatchQueuedPulse", Static)!.Invoke(null, [sink, now]);
        Service.GetMethod("StreamTritonPcmTick", Static)!.Invoke(null, [sink, 0f, 0f, false, false, now]);
        return ((float[])SinkType.GetField("PcmFloatBuf")!.GetValue(sink)!).ToArray();
    }
    static float Peak(float[] samples, int side) => samples.Where((_, i) => i % 2 == side).Select(Math.Abs).Max();

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void PcmSwipePulseKeepsItsRequestedSide(int side, bool muLaw)
    {
        var sink = Create(muLaw);
        Pulse(sink, side, .8f);
        var samples = Tick(sink);
        float active = Peak(samples, side), opposite = Peak(samples, 1 - side);
        Assert.True(active > .1f);
        var emitted = packets.ToArray();
        var control = Create(muLaw);
        Pulse(control, 1 - side, .8f);
        Assert.True(Peak(Tick(control), 1 - side) > .1f);
        output.WriteLine($"side={side} muLaw={muLaw} activePeak={active} oppositePeak={opposite} oppositeControl=True");
        output.WriteLine("firstPacket=" + Convert.ToHexString(emitted[0]));
        Assert.Equal(0f, opposite);
        int start = side == 0 ? 33 : 2;
        int count = muLaw ? 31 : 30;
        byte silence = muLaw ? (byte)255 : (byte)0;
        Assert.All(emitted, packet => Assert.All(packet.Skip(start).Take(count), value => Assert.Equal(silence, value)));
    }

    [Fact]
    public void SimultaneousPulsesKeepTheirOwnMaximumAmplitude()
    {
        var sink = Create(false);
        Pulse(sink, 0, .4f);
        Pulse(sink, 0, .8f);
        Pulse(sink, 0, .1f);
        Pulse(sink, 1, .2f);
        var samples = Tick(sink);
        Assert.True(Peak(samples, 1) > .05f);
        Assert.InRange(Peak(samples, 0) / Peak(samples, 1), 3.99f, 4.01f);
    }

    [Fact]
    public void OppositePulseDoesNotResetAnExistingSidesDecayOrPhase()
    {
        var overlap = Create(false);
        var control = Create(false);
        Pulse(overlap, 0, .8f);
        Pulse(control, 0, .8f);
        Tick(overlap);
        Tick(control);
        Pulse(overlap, 1, .4f);
        var mixed = Tick(overlap);
        var leftOnly = Tick(control);
        Assert.Equal(leftOnly.Where((_, i) => i % 2 == 0), mixed.Where((_, i) => i % 2 == 0));
        Assert.Equal(0f, Peak(leftOnly, 1));
        Assert.True(Peak(mixed, 1) > .1f);
    }

    [Fact]
    public void ADecayedPulseDoesNotKeepItsOldAmplitude()
    {
        var sink = Create(false);
        Pulse(sink, 0, .8f);
        Assert.True(Peak(Tick(sink), 0) > .4f);
        for (int i = 0; i < 20; i++) Tick(sink);
        Pulse(sink, 0, .1f);
        float peak = Peak(Tick(sink), 0);
        Assert.InRange(peak, .01f, .08f);
    }

    [Fact]
    public void CooldownRestoresTheWholePendingPulse()
    {
        var sink = Create(false);
        Set(sink, "PcmArmed", false);
        Set(sink, "Handle", new IntPtr(1));
        Set(sink, "PulseLastSendMs", now);
        Pulse(sink, 0, .8f);
        Pulse(sink, 1, .2f);
        try
        {
            // The cooldown returns before this synthetic handle reaches I/O.
            Service.GetMethod("DispatchQueuedPulse", Static)!.Invoke(null, [sink, now]);
            Assert.Equal(3, Get<int>(sink, "PulsePendingSides"));
        }
        finally { Set(sink, "Handle", IntPtr.Zero); }
        Set(sink, "PcmArmed", true);
        Pulse(sink, 0, .1f);
        Pulse(sink, 1, .5f);
        var samples = Tick(sink);
        Assert.InRange(Peak(samples, 0) / Peak(samples, 1), 1.59f, 1.61f);
    }

    public void Dispose()
    {
        lock (gate)
            foreach (var sink in owned) sinks.Remove(sink);
        foreach (var sink in owned) (Get<object>(sink, "PcmRing") as IDisposable)?.Dispose();
        HapticToneService.PcmPacketWriter = writer;
    }
}
