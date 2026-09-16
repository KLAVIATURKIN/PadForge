using System.Reflection;
using System.Collections;
using NAudio.Wave;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using Xunit.Abstractions;

namespace PadForge.Tests;

[Collection("AudioProfileLifecycle")]
public sealed class HapticPcmOwnershipTests(ITestOutputHelper output)
{
    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    static readonly Type Service = typeof(HapticToneService);

    sealed class Silence : ISampleProvider
    {
        public int Reads;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(8000, 2);
        public int Read(float[] buffer, int offset, int count) { Reads++; Array.Clear(buffer, offset, count); return count; }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void ControlChangesDropBufferedSamplesWhileRepeatsPreserveThem(bool muLaw, bool stop)
    {
        var type = Service.GetNestedType("Sink", BindingFlags.NonPublic)!;
        var sink = Activator.CreateInstance(type, true)!;
        type.GetField("PcmSource")!.SetValue(sink, new Silence());
        type.GetField("PcmArmed")!.SetValue(sink, true);
        type.GetField("Running")!.SetValue(sink, true);
        type.GetField("PcmMuLaw")!.SetValue(sink, muLaw);
        type.GetField("OutLen")!.SetValue(sink, 64);
        var writer = HapticToneService.PcmPacketWriter;
        var packets = new List<byte[]>();
        bool accept = false;
        HapticToneService.PcmPacketWriter = (_, bytes) => { packets.Add((byte[])bytes.Clone()); return accept; };
        var tick = Service.GetMethod("StreamTritonPcmTick", Static)!;
        long now = Environment.TickCount64;
        try
        {
            Assert.True((bool)tick.Invoke(null, [sink, 200f, .5f, false, true, now])!);
            Assert.NotEmpty(packets);
            byte silence = muLaw ? (byte)255 : (byte)0;
            int bytesPerSide = muLaw ? 31 : 30;
            bool IsSilent(byte[] packet) => packet.Skip(2).Take(bytesPerSide).All(b => b == silence)
                && packet.Skip(33).Take(bytesPerSide).All(b => b == silence);
            Assert.Contains(packets, p => !IsSilent(p));
            packets.Clear();
            accept = true;
            Assert.True((bool)tick.Invoke(null, [sink, stop ? 0f : 200f, stop ? 0f : .5f, false, true, now + 10])!);
            int stale = packets.Count(p => !IsSilent(p));
            output.WriteLine($"muLaw={muLaw} stop={stop} positiveBefore=True packetsAfter={packets.Count} nonzeroPackets={stale}");
            output.WriteLine("firstAfterStop=" + Convert.ToHexString(packets[0]));
            Assert.NotEmpty(packets);
            if (stop) Assert.Equal(0, stale);
            else Assert.True(stale > 0);
            Assert.Equal((stop ? 80 : 160) / (muLaw ? 31 : 15), packets.Count);
        }
        finally
        {
            HapticToneService.PcmPacketWriter = writer;
            (type.GetField("PcmRing")!.GetValue(sink) as IDisposable)?.Dispose();
        }
    }

    [Theory]
    [InlineData("local", true)]
    [InlineData("remote", false)]
    [InlineData("test", false)]
    [InlineData("suppressed", false)]
    [InlineData("offline", false)]
    [InlineData("retired", false)]
    public void IdlePcmDrainHonorsPriorityAndOwnership(string state, bool expectedRead)
    {
        var type = Service.GetNestedType("Sink", BindingFlags.NonPublic)!;
        var sink = Activator.CreateInstance(type, true)!;
        var source = new Silence();
        var owner = new UserDevice { InstanceGuid = Guid.NewGuid(), IsOnline = state != "offline", VendorId = 0x28de, ProdId = 0x1302, DevicePath = "controlled-pcm" };
        type.GetField("OwnerDevice")!.SetValue(sink, owner);
        type.GetField("DeviceGuid")!.SetValue(sink, owner.InstanceGuid);
        type.GetField("HidPath")!.SetValue(sink, owner.DevicePath);
        type.GetField("Family")!.SetValue(sink, Enum.Parse(Service.GetNestedType("Family", BindingFlags.NonPublic)!, "Steam2026"));
        type.GetField("PcmSource")!.SetValue(sink, source);
        type.GetField("PcmArmed")!.SetValue(sink, true);
        type.GetField("Running")!.SetValue(sink, true);
        type.GetField("Handle")!.SetValue(sink, new IntPtr(1));
        type.GetField("OutLen")!.SetValue(sink, 64);
        long now = Environment.TickCount64;
        type.GetField("PcmLastContentMs")!.SetValue(sink, now);
        if (state == "remote") type.GetField("RemoteRequest")!.SetValue(sink, new RemoteToneRequest(owner, 200, .5f, null));
        if (state == "test") type.GetField("TestUntilMs")!.SetValue(sink, now + 1000);
        var gate = Service.GetField("_lock", Static)!.GetValue(null)!;
        var sinks = (IList)Service.GetField("_sinks", Static)!.GetValue(null)!;
        var suppressed = Service.GetField("_suppressed", Static)!;
        bool prior = (bool)suppressed.GetValue(null)!;
        var writer = HapticToneService.PcmPacketWriter;
        Assert.Empty(sinks);
        Assert.Null(Service.GetField("_reconcileTimer", Static)!.GetValue(null));
        try
        {
            // The packet callback consumes this synthetic handle before native I/O.
            HapticToneService.PcmPacketWriter = (_, _) => true;
            lock (gate)
            {
                suppressed.SetValue(null, state == "suppressed");
                if (state != "retired") sinks.Add(sink);
            }
            var result = ((bool Read, bool Content))Service.GetMethod("DrainIdlePcm", Static)!.Invoke(null, [sink, now])!;
            Assert.Equal(expectedRead, result.Read);
            Assert.Equal(expectedRead ? 1 : 0, source.Reads);
        }
        finally
        {
            lock (gate) { sinks.Remove(sink); suppressed.SetValue(null, prior); }
            type.GetField("Handle")!.SetValue(sink, IntPtr.Zero);
            HapticToneService.PcmPacketWriter = writer;
            (type.GetField("PcmRing")!.GetValue(sink) as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public void ARejectedIdleDrainDoesNotAdvanceItsDiagnosticCount()
    {
        int calls = 0;
        int drained = HapticToneService.IdleCatchUpDrainOwned(() => 100, () => { calls++; return (false, false); });
        Assert.Equal(1, calls);
        Assert.Equal(0, drained);
    }
}
