using System.Collections;
using System.Reflection;
using NAudio.Wave;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit.Abstractions;

namespace PadForge.Tests;

[Collection("AudioProfileLifecycle")]
public sealed class UsbPlayerLifetimeTests : IDisposable
{
    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    static readonly Type Audio = typeof(AudioPassthroughService);
    readonly object gate;
    readonly IDictionary sinks;
    readonly Thread priorWorker;
    readonly Thread worker = Thread.CurrentThread;
    readonly List<Guid> ids = new();
    readonly ITestOutputHelper output;

    public UsbPlayerLifetimeTests(ITestOutputHelper output)
    {
        this.output = output;
        AudioPassthroughService.Shutdown();
        foreach (string name in new[] { "_workerThread", "_btThread" })
            if (Audio.GetField(name, Static)!.GetValue(null) is Thread prior && prior.IsAlive)
                Assert.True(prior.Join(6000));
        gate = Audio.GetField("_lock", Static)!.GetValue(null)!;
        sinks = (IDictionary)Audio.GetField("_sinks", Static)!.GetValue(null)!;
        priorWorker = Audio.GetField("_workerThread", Static)!.GetValue(null) as Thread;
        lock (gate)
        {
            Audio.GetField("_workerThread", Static)!.SetValue(null, worker);
            Audio.GetField("_running", Static)!.SetValue(null, true);
        }
    }

    public void Dispose()
    {
        AudioPassthroughService.Shutdown();
        Audio.GetField("_workerThread", Static)!.SetValue(null, priorWorker);
        foreach (var id in ids) AudioPassthroughService.TryConsumeSpeakerPathCleared(id);
    }

    static object Call(string method, params object[] args) => Audio.GetMethod(method, Static)!.Invoke(null, args);
    object Sink()
    {
        var type = Audio.GetNestedType("Sink", BindingFlags.NonPublic)!;
        var sink = Activator.CreateInstance(type, true)!;
        Guid id = Guid.NewGuid();
        type.GetField("DeviceGuid")!.SetValue(sink, id);
        type.GetField("Slot")!.SetValue(sink, 14);
        lock (gate) sinks[id] = sink;
        ids.Add(id);
        return sink;
    }
    static T Field<T>(object sink, string field) => (T)sink.GetType().GetField(field)!.GetValue(sink)!;

    bool Start(object sink, Player player)
    {
        var feed = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        var bytes = new byte[16];
        Buffer.BlockCopy(new[] { .25f, -.5f, .25f, -.5f }, 0, bytes, 0, bytes.Length);
        feed.AddSamples(bytes, 0, bytes.Length);
        try { return (bool)Call("TryStartUsbPlayer", sink, player, feed, worker); }
        catch (TargetInvocationException) { return false; }
    }

    Player Control()
    {
        var player = new Player();
        var sink = Sink();
        Assert.True(Start(sink, player));
        Assert.True((bool)Call("SinkAlive", sink));
        Assert.True(player.NonzeroInput);
        return player;
    }

    [Theory]
    [InlineData("init")]
    [InlineData("play")]
    [InlineData("stopped")]
    [InlineData("error")]
    public void FailedStartupDisposesItsPlayerWithoutPublishingIt(string failure)
    {
        Control();
        var player = new Player { Failure = failure };
        var sink = Sink();
        bool committed = Start(sink, player);
        output.WriteLine($"healthyControl=True failure={failure} committed={committed} disposals={player.Disposals}");
        try
        {
            Assert.False(committed);
            Assert.Null(Field<IWavePlayer>(sink, "Player"));
            Assert.Equal(1, player.Disposals);
            Assert.Equal(0, player.HandlerCount);
        }
        finally
        {
            if (player.Disposals == 0 && Field<IWavePlayer>(sink, "Player") == null) player.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RuntimeStopInvalidatesTheSinkEvenIfStateStillSaysPlaying(bool keepPlayingState)
    {
        Control();
        var player = new Player();
        var sink = Sink();
        Assert.True(Start(sink, player));
        player.Fail(keepPlayingState);
        bool alive = (bool)Call("SinkAlive", sink);
        bool failed = Field<bool>(sink, "TransportFailed");
        output.WriteLine($"healthyControl=True state={player.PlaybackState} alive={alive} failed={failed}");
        Assert.False(alive);
        Assert.True(failed);
    }

    [Fact]
    public void RetiredPlaybackCallbackCannotFailItsReplacement()
    {
        Control();
        var player = new Player();
        var sink = Sink();
        Assert.True(Start(sink, player));
        var pending = player.Handler;
        Assert.NotNull(pending);
        object detached;
        lock (gate) detached = Call("DetachTransport_NoLock", sink);
        Call("DisposeTransport", detached);
        Assert.Equal(0, player.HandlerCount);
        var replacement = new Player();
        Assert.True(Start(sink, replacement));
        pending(player, new StoppedEventArgs(new InvalidOperationException("late playback failure")));
        Assert.False(Field<bool>(sink, "TransportFailed"));
        Assert.Same(replacement, Field<IWavePlayer>(sink, "Player"));
        Assert.True((bool)Call("SinkAlive", sink));
        Assert.True(AudioPassthroughService.PeekSpeakerPathCleared(Field<Guid>(sink, "DeviceGuid")));
    }

    [Theory]
    [InlineData(PlaybackState.Stopped)]
    [InlineData(PlaybackState.Paused)]
    public void ReconcileDetachesAnInactivePlayerBeforeRetry(PlaybackState state)
    {
        Control();
        var sink = Sink();
        var player = new Player();
        Assert.True(Start(sink, player));
        Guid id = Field<Guid>(sink, "DeviceGuid");
        var oldDevices = SettingsManager.UserDevices;
        var oldSettings = SettingsManager.UserSettings;
        var oldCreated = SettingsManager.SlotCreated;
        var oldEnabled = SettingsManager.SlotEnabled;
        var oldDemand = AudioPassthroughService.SlotWantsMacroAudioProvider;
        var oldConfig = AudioPassthroughService.PassthroughConfigProvider;
        var oldDsp = AudioPassthroughService.DspConfigProvider;
        var oldNotify = AudioPassthroughService.AudioRoutingNotified;
        int notices = 0;
        try
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.SlotCreated = new bool[16];
            SettingsManager.SlotEnabled = new bool[16];
            SettingsManager.SlotCreated[14] = SettingsManager.SlotEnabled[14] = true;
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PadForge-missing-device-" + id);
            sink.GetType().GetField("HidPath")!.SetValue(sink, path);
            SettingsManager.UserDevices.Items.Add(new UserDevice
            {
                InstanceGuid = id, ProductGuid = id, VendorId = 0x054c, ProdId = 0x0ce6,
                IsOnline = true, IsEnabled = true, CapType = InputDeviceType.Gamepad,
                DevicePath = path
            });
            SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = id, ProductGuid = id, MapTo = 14, IsEnabled = true });
            AudioPassthroughService.SlotWantsMacroAudioProvider = slot => slot == 14;
            AudioPassthroughService.PassthroughConfigProvider = _ => Array.Empty<(Guid, bool, string)>();
            AudioPassthroughService.DspConfigProvider = null;
            AudioPassthroughService.AudioRoutingNotified = slot => { if (slot == 14) notices++; };
            Call("ReconcileOnWorker", worker);
            Assert.Equal(1, notices);
            Assert.Same(player, Field<IWavePlayer>(sink, "Player"));
            player.ChangeStateQuietly(state);
            Call("ReconcileOnWorker", worker);
            Assert.True(sinks.Contains(id));
            Assert.Null(Field<IWavePlayer>(sink, "Player"));
            Assert.Equal(1, player.Disposals);
            Assert.Equal(0, player.HandlerCount);
            Assert.True(AudioPassthroughService.PeekSpeakerPathCleared(id));
            output.WriteLine($"state={state} liveNoticeControl=1 totalNotices={notices} disposals={player.Disposals}");
            Assert.Equal(2, notices);
        }
        finally
        {
            var watches = (IDictionary)Audio.GetField("_jackWatch", Static)!.GetValue(null)!;
            var jackGate = Audio.GetField("_jackLock", Static)!.GetValue(null)!;
            Thread[] readers;
            lock (jackGate) readers = watches.Values.Cast<object>().Select(w => (Thread)w.GetType().GetField("Thread")!.GetValue(w)!).ToArray();
            AudioPassthroughService.Shutdown();
            foreach (var reader in readers) if (reader.IsAlive) Assert.True(reader.Join(2000));
            SettingsManager.UserDevices = oldDevices;
            SettingsManager.UserSettings = oldSettings;
            SettingsManager.SlotCreated = oldCreated;
            SettingsManager.SlotEnabled = oldEnabled;
            AudioPassthroughService.SlotWantsMacroAudioProvider = oldDemand;
            AudioPassthroughService.PassthroughConfigProvider = oldConfig;
            AudioPassthroughService.DspConfigProvider = oldDsp;
            AudioPassthroughService.AudioRoutingNotified = oldNotify;
        }
    }

    [Fact]
    public void LosingStartupReleasesTheCandidate()
    {
        Control();
        var sink = Sink();
        var player = new Player
        {
            OnPlay = () => Audio.GetField("_workerThread", Static)!.SetValue(null, new Thread(() => { }))
        };
        Assert.False(Start(sink, player));
        Assert.Null(Field<IWavePlayer>(sink, "Player"));
        Assert.Equal(1, player.Disposals);
        Assert.Equal(0, player.HandlerCount);
    }

    sealed class Player : IWavePlayer
    {
        public string Failure;
        public int Disposals, Stops;
        public bool NonzeroInput;
        public Action OnPlay;
        public EventHandler<StoppedEventArgs> Handler;
        public int HandlerCount => Handler?.GetInvocationList().Length ?? 0;
        public event EventHandler<StoppedEventArgs> PlaybackStopped { add => Handler += value; remove => Handler -= value; }
        public float Volume { get; set; }
        public PlaybackState PlaybackState { get; private set; }
        public WaveFormat OutputWaveFormat { get; private set; }
        IWaveProvider source;
        public void Init(IWaveProvider provider)
        {
            if (Failure == "init") throw new InvalidOperationException("init failed");
            source = provider;
            OutputWaveFormat = provider.WaveFormat;
        }
        public void Play()
        {
            PlaybackState = PlaybackState.Playing;
            if (Failure == "play") throw new InvalidOperationException("play failed");
            var bytes = new byte[16];
            source.Read(bytes, 0, bytes.Length);
            NonzeroInput = bytes.Any(b => b != 0);
            OnPlay?.Invoke();
            if (Failure == "stopped") Fail(false);
            if (Failure == "error") Fail(true);
        }
        public void Fail(bool keepPlaying)
        {
            if (!keepPlaying) PlaybackState = PlaybackState.Stopped;
            Handler?.Invoke(this, new StoppedEventArgs(new InvalidOperationException("playback failed")));
        }
        public void Stop()
        {
            Stops++;
            PlaybackState = PlaybackState.Stopped;
            Handler?.Invoke(this, new StoppedEventArgs());
        }
        public void Pause() => PlaybackState = PlaybackState.Paused;
        public void ChangeStateQuietly(PlaybackState state) => PlaybackState = state;
        public void Dispose() { Disposals++; }
    }
}
