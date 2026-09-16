using System.Collections;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.ViewModels;

namespace PadForge.Tests;

[Collection("AudioProfileLifecycle")]
public sealed class EffectsDispatcherDemandTests
{
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    static void Call(UserEffectsDispatcher d, string name) =>
        typeof(UserEffectsDispatcher).GetMethod(name, Instance)!.Invoke(d, name == "OnAnimTickCore" ? [null] : null);
    static bool Active(UserEffectsDispatcher d) =>
        (bool)typeof(UserEffectsDispatcher).GetField("_animTickActive", Instance)!.GetValue(d);
    static void ParkTimer(UserEffectsDispatcher d) =>
        (typeof(UserEffectsDispatcher).GetField("_animTimer", Instance)!.GetValue(d) as Timer)?.Change(Timeout.Infinite, Timeout.Infinite);

    sealed class Scope : IDisposable
    {
        readonly DeviceCollection devices = SettingsManager.UserDevices;
        readonly SettingsCollection settings = SettingsManager.UserSettings;
        readonly Func<int, IReadOnlyDictionary<Guid, DeviceSlotConfig>> configs = UserEffectsDispatcher.SlotPerDeviceConfigsProvider;
        readonly Func<float> peak = UserEffectsDispatcher.AudioPeakProvider;
        readonly Func<int, Guid> target = UserEffectsDispatcher.TestRumbleTargetGuidProvider;
        readonly Func<int, (byte right, byte left)> rumble = UserEffectsDispatcher.SlotRawRumbleProvider;
        public int Dispatches;
        public Scope()
        {
            AudioPassthroughService.Shutdown();
            foreach (string name in new[] { "_workerThread", "_btThread" })
                if (typeof(AudioPassthroughService).GetField(name, Static)!.GetValue(null) is Thread t && t.IsAlive)
                    Assert.True(t.Join(5000));
            SettingsManager.UserDevices = new();
            SettingsManager.UserSettings = new();
            UserEffectsDispatcher.SlotPerDeviceConfigsProvider = null;
            UserEffectsDispatcher.AudioPeakProvider = () => .25f;
            UserEffectsDispatcher.SlotRawRumbleProvider = _ => (0, 0);
            UserEffectsDispatcher.TestRumbleTargetGuidProvider = _ => { Dispatches++; return Guid.Empty; };
        }
        public void Dispose()
        {
            UserEffectsDispatcher.SlotPerDeviceConfigsProvider = configs;
            UserEffectsDispatcher.AudioPeakProvider = peak;
            UserEffectsDispatcher.TestRumbleTargetGuidProvider = target;
            UserEffectsDispatcher.SlotRawRumbleProvider = rumble;
            SettingsManager.UserDevices = devices;
            SettingsManager.UserSettings = settings;
        }
    }

    [Theory]
    [InlineData(LightbarMode.Off)]
    [InlineData(LightbarMode.AudioPulse)]
    public void LiveSpeakerRoutingSurvivesTicksAndIdleRecheck(LightbarMode mode)
    {
        using var scope = new Scope();
        const int slot = 15;
        var id = Guid.NewGuid();
        var audioType = typeof(AudioPassthroughService);
        var sinkType = audioType.GetNestedType("Sink", BindingFlags.NonPublic)!;
        var sink = Activator.CreateInstance(sinkType, true)!;
        sinkType.GetField("Slot")!.SetValue(sink, slot);
        sinkType.GetField("DeviceGuid")!.SetValue(sink, id);
        sinkType.GetField("BtHandle")!.SetValue(sink, new IntPtr(1));
        var sinks = (IDictionary)audioType.GetField("_sinks", Static)!.GetValue(null);
        var gate = audioType.GetField("_lock", Static)!.GetValue(null);
        var config = new DeviceSlotConfig { LightbarMode = mode };
        using var dispatcher = new UserEffectsDispatcher(slot, config, startTimer: false);
        lock (gate) sinks.Add(id, sink);
        try
        {
            Assert.True(AudioPassthroughService.SlotWantsSpeakerPath(slot));
            Call(dispatcher, "UpdateAnimTimer");
            ParkTimer(dispatcher);
            Assert.True(Active(dispatcher));
            scope.Dispatches = 0;
            Call(dispatcher, "OnAnimTickCore");
            Call(dispatcher, "OnAnimTickCore");
            Assert.Equal(2, scope.Dispatches);
            Assert.True(Active(dispatcher));
            Call(dispatcher, "StopAnimTimerIfStillIdle");
            Assert.True(Active(dispatcher));
            lock (gate) sinks.Remove(id);
            config.LightbarMode = LightbarMode.Off;
            Call(dispatcher, "OnAnimTickCore");
            Assert.False(Active(dispatcher));
            dispatcher.Dispose();
            Call(dispatcher, "UpdateAnimTimer");
            Assert.False(Active(dispatcher));
        }
        finally { lock (gate) sinks.Remove(id); }
    }

    [Theory]
    [InlineData(LightbarMode.AudioPulse, 1)]
    [InlineData(LightbarMode.AudioPulseRandom, 1)]
    [InlineData(LightbarMode.AudioThresholds, 1)]
    [InlineData(LightbarMode.AudioGradient, 1)]
    [InlineData(LightbarMode.AudioCrossFade, 1)]
    [InlineData(LightbarMode.AudioPulseRainbow, 2)]
    [InlineData(LightbarMode.Rainbow, 2)]
    [InlineData(LightbarMode.Breathing, 2)]
    [InlineData(LightbarMode.Strobe, 2)]
    [InlineData(LightbarMode.ColorCycle, 2)]
    [InlineData(LightbarMode.InputReactive, 2)]
    [InlineData(LightbarMode.InputReactiveCycle, 2)]
    [InlineData(LightbarMode.InputReactiveFixed, 2)]
    public void FlatAudioPreservesEveryOtherDevicesAnimation(LightbarMode otherMode, int expected)
    {
        using var scope = new Scope();
        var audio = new DeviceSlotConfig { LightbarMode = LightbarMode.AudioPulse };
        var other = new DeviceSlotConfig { LightbarMode = otherMode };
        UserEffectsDispatcher.SlotPerDeviceConfigsProvider = _ =>
            new Dictionary<Guid, DeviceSlotConfig> { [Guid.Empty] = audio, [new Guid("00000000-0000-0000-0000-000000000001")] = other };
        using var dispatcher = new UserEffectsDispatcher(15, audio, startTimer: false);
        Call(dispatcher, "OnAnimTickCore");
        Assert.Equal(1, scope.Dispatches);
        Call(dispatcher, "OnAnimTickCore");
        Assert.Equal(expected, scope.Dispatches);
    }
}
