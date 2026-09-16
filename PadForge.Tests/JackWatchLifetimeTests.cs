using System.Collections;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using PadForge.Common.Input;
using Xunit.Abstractions;

namespace PadForge.Tests;

[Collection("AudioProfileLifecycle")]
public sealed class JackWatchLifetimeTests(ITestOutputHelper output)
{
    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint GetFileType(IntPtr handle);
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoppedReaderKeepsItsHandleAndCannotPublishOldStatus(bool afterRead)
    {
        string name = "PadForge-jack-lifetime-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var connection = server.WaitForConnectionAsync(); var id = Guid.NewGuid();
        using var paused = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var type = typeof(AudioPassthroughService); var old = AudioPassthroughService.JackWatchReadBoundary;
        var watches = (IDictionary)type.GetField("_jackWatch", Static)!.GetValue(null); var gate = type.GetField("_jackLock", Static)!.GetValue(null);
        var states = (ConcurrentDictionary<Guid, bool>)type.GetField("s_padJackState", Static)!.GetValue(null);
        object watch = null; Thread reader = null; IntPtr handle = IntPtr.Zero; int boundaries = 0, rawReads = 0;
        try
        {
            AudioPassthroughService.JackWatchReadBoundary = (pad, before, raw, count) =>
            {
                if (pad != id) return; if (!before && count > 0) Interlocked.Increment(ref rawReads);
                if (before == !afterRead && Interlocked.Increment(ref boundaries) == 2) { paused.Set(); if (!release.Wait(5000)) throw new TimeoutException(); }
            };
            type.GetMethod("EnsureJackWatch", Static)!.Invoke(null, [id, @"\\.\pipe\" + name, false]);
            await connection.WaitAsync(TimeSpan.FromSeconds(5));
            lock (gate) watch = watches[id]; reader = (Thread)watch.GetType().GetField("Thread")!.GetValue(watch);
            var report = new byte[78]; report[0] = 1; report[54] = 1;
            await server.WriteAsync(report); Assert.True(SpinWait.SpinUntil(() => AudioPassthroughService.TryGetHeadphoneJack(id) == true, 2000));
            if (afterRead) { report[54] = 0; await server.WriteAsync(report); }
            Assert.True(paused.Wait(2000)); handle = (IntPtr)watch.GetType().GetField("Handle")!.GetValue(watch);
            type.GetMethod("StopJackWatch", Static)!.Invoke(null, [id]);
            bool retained = GetFileType(handle) == 3;
            AudioPassthroughService.NoteHeadphoneJack(id, true);
            release.Set();
            bool exited = reader.Join(2000);
            if (!exited) { await server.WriteAsync(report); Assert.True(reader.Join(2000)); }
            await ((Task)watch.GetType().GetField("Cancellation")!.GetValue(watch)).WaitAsync(TimeSpan.FromSeconds(2));
            bool current = AudioPassthroughService.TryGetHeadphoneJack(id) == true;
            output.WriteLine($"afterRead={afterRead} rawReads={rawReads} retainedBeforeExit={retained} drainedWithoutPacket={exited} currentJack={current} finalHandleType={GetFileType(handle)}");
            Assert.True(rawReads > 0); Assert.True(retained); Assert.True(exited); Assert.True(current); Assert.Equal((uint)0, GetFileType(handle));
        }
        finally
        {
            release.Set();
            if (watch != null) watch.GetType().GetField("Stop")!.SetValue(watch, true);
            if (reader?.IsAlive == true) { await server.WriteAsync(new byte[78]); Assert.True(reader.Join(2000)); }
            if (watch != null) await ((Task)watch.GetType().GetField("Cancellation")!.GetValue(watch)).WaitAsync(TimeSpan.FromSeconds(2));
            AudioPassthroughService.JackWatchReadBoundary = old; lock (gate) watches.Remove(id); states.TryRemove(id, out _);
        }
    }
}
