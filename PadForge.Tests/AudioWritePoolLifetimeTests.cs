using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using PadForge.Common.Input;
using Xunit.Abstractions;

namespace PadForge.Tests;

[Collection("AudioProfileLifecycle")]
public sealed class AudioWritePoolLifetimeTests(ITestOutputHelper output)
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetHandleInformation(IntPtr handle, out uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(IntPtr handle, uint timeout);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CancelIoEx(IntPtr handle, IntPtr overlap);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr handle);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportRetirementCancelsWritesFromAnotherThreadAndReleasesTheirStorage(bool delayedCompletion)
    {
        string name = "PadForge-write-pool-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 1, 1);
        var connection = server.WaitForConnectionAsync();
        var audio = typeof(AudioPassthroughService);
        var native = audio.GetNestedType("NativeMethods", BindingFlags.NonPublic)!;
        IntPtr handle = (IntPtr)native.GetMethod("OpenHid")!.Invoke(null, [@"\\.\pipe\" + name])!;
        Assert.NotEqual(new IntPtr(-1), handle);
        await connection.WaitAsync(TimeSpan.FromSeconds(5));
        var pool = new AudioPassthroughService.BtWritePool(65536, delayedCompletion ? (_, _) => { } : null);
        var events = ((IntPtr[])pool.GetType().GetField("_ev", Private)!.GetValue(pool)!).ToArray();
        var overlaps = ((IntPtr[])pool.GetType().GetField("_ol", Private)!.GetValue(pool)!).ToArray();
        var pins = ((GCHandle[])pool.GetType().GetField("_pin", Private)!.GetValue(pool)!).ToArray();
        using var work = new BlockingCollection<Action>();
        int writerId = 0;
        var writer = new Thread(() =>
        {
            writerId = Environment.CurrentManagedThreadId;
            foreach (var action in work.GetConsumingEnumerable()) action();
        }) { IsBackground = true };
        writer.Start();
        async Task Send(byte[] bytes)
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            work.Add(() =>
            {
                try
                {
                    Assert.True(pool.TrySend(handle, bytes, out bool hardFail));
                    Assert.False(hardFail);
                    done.SetResult(true);
                }
                catch (Exception ex) { done.SetException(ex); }
            });
            await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        try
        {
            await Send([17, 34, 51, 68]);
            var control = new byte[4];
            await server.ReadExactlyAsync(control).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new byte[] { 17, 34, 51, 68 }, control);
            await Send(new byte[65536]);
            Assert.Equal(258u, WaitForSingleObject(events[1], 0));
            Assert.NotEqual(writerId, Environment.CurrentManagedThreadId);
            var sinkType = audio.GetNestedType("Sink", BindingFlags.NonPublic)!;
            var sink = Activator.CreateInstance(sinkType, true)!;
            sinkType.GetField("BtHandle")!.SetValue(sink, handle);
            sinkType.GetField("Tx")!.SetValue(sink, pool);
            if (delayedCompletion)
            {
                pool.Dispose();
                Assert.False(pool.Cleanup.IsCompleted);
                Assert.True(GetHandleInformation(events[1], out _));
                Assert.True(((GCHandle[])pool.GetType().GetField("_pin", Private)!.GetValue(pool)!)[1].IsAllocated);
                await server.ReadExactlyAsync(new byte[65536]).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
            else
            {
                pool.Dispose();
                await pool.Cleanup.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.All(events, h => Assert.False(GetHandleInformation(h, out _)));
                audio.GetMethod("DisposeTransport", Static)!.Invoke(null, [sink]);
                handle = IntPtr.Zero;
            }
            await pool.Cleanup.WaitAsync(TimeSpan.FromSeconds(5));
            int retainedEvents = events.Count(h => GetHandleInformation(h, out _));
            output.WriteLine($"delayedCompletion={delayedCompletion} controlBytes={control.Length} pendingBeforeStop=True retainedEvents={retainedEvents}");
            Assert.Equal(0, retainedEvents);
            Assert.False(pool.TrySend(IntPtr.Zero, [1], out bool retired));
            Assert.True(retired);
        }
        finally
        {
            work.CompleteAdding();
            Assert.True(writer.Join(2000));
            if (handle != IntPtr.Zero)
            {
                CancelIoEx(handle, IntPtr.Zero);
                CloseHandle(handle);
            }
            pool.Dispose();
            await pool.Cleanup.WaitAsync(TimeSpan.FromSeconds(5));
            // Baseline disposal lost pending slots. Release only storage whose
            // event remains owned and whose native completion has arrived.
            for (int i = 0; i < events.Length; i++)
            {
                if (!GetHandleInformation(events[i], out _)) continue;
                Assert.Equal(0u, WaitForSingleObject(events[i], 2000));
                CloseHandle(events[i]);
                Marshal.FreeHGlobal(overlaps[i]);
                if (pins[i].IsAllocated) pins[i].Free();
            }
        }
    }
}
