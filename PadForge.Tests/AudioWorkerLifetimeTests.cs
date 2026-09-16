using System.Collections;
using System.Reflection;
using NAudio.Wave;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit.Abstractions;

namespace PadForge.Tests;

[Collection("AudioProfileLifecycle")]
public sealed class AudioWorkerLifetimeTests(ITestOutputHelper output)
{
    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    static readonly Type Service = typeof(AudioPassthroughService);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RestartRejectsTheOldWorkersDesiredSinks(bool oldWantedAudio, bool captureStartup)
    {
        AudioPassthroughService.Shutdown();
        foreach (string name in new[] { "_workerThread", "_btThread" })
            if (Service.GetField(name, Static)!.GetValue(null) is Thread prior && prior.IsAlive)
                Assert.True(prior.Join(6000));
        var oldDevices = SettingsManager.UserDevices;
        var oldSettings = SettingsManager.UserSettings;
        var oldCreated = SettingsManager.SlotCreated;
        var oldEnabled = SettingsManager.SlotEnabled;
        var oldDemand = AudioPassthroughService.SlotWantsMacroAudioProvider;
        var oldConfig = AudioPassthroughService.PassthroughConfigProvider;
        var oldCompleted = AudioPassthroughService.AudioWorkerPassCompleted;
        var oldFactory = AudioPassthroughService.LoopbackCaptureFactory;
        var gate = Service.GetField("_lock", Static)!.GetValue(null)!;
        var sinks = (IDictionary)Service.GetField("_sinks", Static)!.GetValue(null)!;
        var captures = (IDictionary)Service.GetField("_captures", Static)!.GetValue(null)!;
        using var snapshotHeld = new ManualResetEventSlim();
        using var releaseSnapshot = new ManualResetEventSlim();
        using var staleCompleted = new ManualResetEventSlim();
        using var releaseCompleted = new ManualResetEventSlim();
        using var currentCompleted = new ManualResetEventSlim();
        Thread oldWorker = null, newWorker = null, oldStream = null;
        Guid oldPad = Guid.NewGuid(), currentPad = Guid.NewGuid();
        Guid assigned = oldPad;
        Capture retiredCapture = null, currentCapture = null;
        bool snapshotReleased = false, completionReleased = false;
        void Assign(Guid id)
        {
            assigned = id;
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices.Items.Add(new UserDevice
            {
                InstanceGuid = id, ProductGuid = id, ProductName = "Controlled peer audio",
                InstanceName = "Controlled peer audio", VendorId = 0x054c, ProdId = 0x0ce6,
                CapType = InputDeviceType.Gamepad, IsOnline = true, IsEnabled = true,
                DevicePath = "peer://worker-lifetime/" + id
            });
            var setting = new UserSetting { InstanceGuid = id, ProductGuid = id, MapTo = 0, IsEnabled = true };
            var mapping = new PadSetting();
            mapping.UpdateChecksum();
            setting.SetPadSetting(mapping);
            SettingsManager.UserSettings.Items.Add(setting);
        }
        try
        {
            SettingsManager.SlotCreated = new bool[16];
            SettingsManager.SlotEnabled = new bool[16];
            SettingsManager.SlotCreated[0] = SettingsManager.SlotEnabled[0] = true;
            Assign(oldPad);
            AudioPassthroughService.PassthroughConfigProvider = slot => captureStartup && slot == 0
                ? new[] { (assigned, true, "controlled-" + assigned) } : Array.Empty<(Guid, bool, string)>();
            AudioPassthroughService.SlotWantsMacroAudioProvider = slot =>
            {
                Interlocked.CompareExchange(ref oldWorker, Thread.CurrentThread, null);
                if (ReferenceEquals(oldWorker, Thread.CurrentThread))
                {
                    if (slot == 1 && !captureStartup)
                    {
                        snapshotHeld.Set();
                        snapshotReleased = releaseSnapshot.Wait(10000);
                    }
                    return slot == 0 && oldWantedAudio;
                }
                return slot == 0;
            };
            AudioPassthroughService.LoopbackCaptureFactory = endpoint =>
            {
                var capture = new Capture();
                var entry = AudioPassthroughService.CreateCaptureEntry(endpoint, Guid.Empty, capture);
                if (ReferenceEquals(Thread.CurrentThread, oldWorker))
                {
                    retiredCapture = capture;
                    snapshotHeld.Set();
                    snapshotReleased = releaseSnapshot.Wait(10000);
                }
                else currentCapture = capture;
                return entry;
            };
            AudioPassthroughService.AudioWorkerPassCompleted = thread =>
            {
                if (ReferenceEquals(thread, oldWorker))
                {
                    staleCompleted.Set();
                    completionReleased = releaseCompleted.Wait(10000);
                }
                else currentCompleted.Set();
            };
            AudioPassthroughService.Reconcile();
            Assert.True(snapshotHeld.Wait(5000));
            oldStream = (Thread)Service.GetField("_btThread", Static)!.GetValue(null)!;
            AudioPassthroughService.Shutdown();
            Assign(currentPad);
            AudioPassthroughService.Reconcile();
            newWorker = (Thread)Service.GetField("_workerThread", Static)!.GetValue(null)!;
            Assert.NotSame(oldWorker, newWorker);
            Assert.True(currentCompleted.Wait(5000));
            lock (gate) Assert.True(sinks.Contains(currentPad), "The new worker did not create its control sink.");
            releaseSnapshot.Set();
            Assert.True(staleCompleted.Wait(5000));
            bool currentPresent, retiredPresent;
            lock (gate)
            {
                currentPresent = sinks.Contains(currentPad);
                retiredPresent = sinks.Contains(oldPad);
            }
            output.WriteLine($"oldWantedAudio={oldWantedAudio} newWorkerControl=True currentPresent={currentPresent} retiredPresent={retiredPresent}");
            Assert.True(currentPresent);
            Assert.False(retiredPresent);
            if (captureStartup)
            {
                lock (gate)
                {
                    output.WriteLine($"captures={captures.Count} retiredCaptureDisposals={retiredCapture.Disposals} currentCaptureDisposals={currentCapture.Disposals}");
                    Assert.Single(captures);
                    Assert.Same(currentCapture, ((AudioPassthroughService.CaptureEntry)captures["controlled-" + currentPad]).Cap);
                }
                Assert.Equal(1, retiredCapture.Disposals);
                Assert.Equal(0, currentCapture.Disposals);
            }
        }
        finally
        {
            releaseSnapshot.Set();
            releaseCompleted.Set();
            AudioPassthroughService.Shutdown();
            foreach (var worker in new[] { oldWorker, newWorker, oldStream,
                Service.GetField("_btThread", Static)!.GetValue(null) as Thread })
                if (worker?.IsAlive == true) Assert.True(worker.Join(6000));
            AudioPassthroughService.AudioWorkerPassCompleted = oldCompleted;
            AudioPassthroughService.SlotWantsMacroAudioProvider = oldDemand;
            AudioPassthroughService.PassthroughConfigProvider = oldConfig;
            AudioPassthroughService.LoopbackCaptureFactory = oldFactory;
            SettingsManager.UserDevices = oldDevices;
            SettingsManager.UserSettings = oldSettings;
            SettingsManager.SlotCreated = oldCreated;
            SettingsManager.SlotEnabled = oldEnabled;
        }
        Assert.True(snapshotReleased);
        Assert.True(completionReleased);
    }

    [Fact]
    public void RetiredRoutingNotificationCannotChangeTheCurrentRoute()
    {
        var routed = (bool[])typeof(SoundMacroService).GetField("_controllerRouted", Static)!.GetValue(null)!;
        bool previous = routed[15];
        try
        {
            SoundMacroService.SetSlotControllerRouted(15, true);
            SoundMacroService.SetSlotControllerRouted(15, false, () => false);
            Assert.True(routed[15]);
            SoundMacroService.SetSlotControllerRouted(15, false, () => true);
            Assert.False(routed[15]);
        }
        finally { SoundMacroService.SetSlotControllerRouted(15, previous); }
    }

    sealed class Capture : IWaveIn
    {
        public int Disposals;
        public WaveFormat WaveFormat { get; set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public event EventHandler<WaveInEventArgs> DataAvailable { add { } remove { } }
        public event EventHandler<StoppedEventArgs> RecordingStopped { add { } remove { } }
        public void StartRecording() { }
        public void StopRecording() { }
        public void Dispose() => Disposals++;
    }
}
