using System;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Engine.Common;
using Xunit;
using Xunit.Abstractions;

namespace PadForge.Tests
{
    /// <summary>
    /// The FreeTrack read runs inline on the input polling thread, so it must
    /// never wait on another process's writer, and it must never copy the heap
    /// without the mutex.
    ///
    /// <para>The reference client, freetrackclient.c FTGetData, puts its copy
    /// INSIDE the wait test: <c>if (ipc_mutex &amp;&amp; WaitForSingleObject(
    /// ipc_mutex, 16) == WAIT_OBJECT_0) { memcpy(...); ReleaseMutex(...); }</c>.
    /// A mutex it could not open therefore copies nothing at all. Its 16 ms is
    /// a game frame's budget; a poll tick has no such room, so the wait is zero
    /// here and a busy tick simply reports no new pose.</para>
    /// </summary>
    public sealed class FreeTrackReaderLifetimeTests
    {
        private readonly ITestOutputHelper output;
        public FreeTrackReaderLifetimeTests(ITestOutputHelper output) => this.output = output;

        private static string Unique(string stem) => stem + "_" + Guid.NewGuid().ToString("N");

        /// <summary>A writer holding the mutex must not stall the caller. The
        /// same reader succeeds once the writer releases, which proves the
        /// fixture really drives the lane rather than failing for some other
        /// reason.</summary>
        [Fact]
        public void AContestedReadReturnsImmediatelyAndRecoversAfterRelease()
        {
            string heap = Unique("PadForgeTestHeap"), mutexName = Unique("PadForgeTestMutex");
            using var reader = new FreeTrackReader(heap, mutexName);
            Assert.True(reader.Open(), "the reader could not map its own test heap");

            var buf = new byte[HeadPose.FreeTrackHeapBytes];
            Assert.True(reader.TryRead(buf), "positive control: an uncontested read must succeed");

            // A Windows mutex is reentrant for the thread that owns it, so the
            // writer must hold it from ANOTHER thread or the reader would
            // simply re-acquire it and the contention would never happen.
            using var taken = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            var writerThread = new Thread(() =>
            {
                using var writer = new Mutex(false, mutexName);
                writer.WaitOne();
                taken.Set();
                release.Wait(5000);
                writer.ReleaseMutex();
            }) { IsBackground = true };
            writerThread.Start();
            Assert.True(taken.Wait(5000), "the fixture could not take the writer mutex");

            bool contested;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                contested = reader.TryRead(buf);
                sw.Stop();
            }
            finally
            {
                release.Set();
                Assert.True(writerThread.Join(5000), "the writer thread did not exit");
            }

            output.WriteLine($"contested read returned {contested} in {sw.Elapsed.TotalMilliseconds:F2} ms");
            Assert.False(contested, "a contested read must report no new pose rather than copy");
            // The old inline wait was 16 ms per contested read on the poll
            // thread. Anything at or above it means the wait came back.
            Assert.True(sw.Elapsed.TotalMilliseconds < 8, $"the poll thread waited {sw.Elapsed.TotalMilliseconds:F2} ms on the writer");
            Assert.True(reader.TryRead(buf), "the reader must recover once the writer releases");
        }

        /// <summary>Without a usable mutex there is no safe copy, so the open
        /// fails and the mapping is released rather than left readable.</summary>
        [Fact]
        public void OpenFailsWhenTheMutexCannotBeTaken()
        {
            string heap = Unique("PadForgeTestHeap");
            // A path-shaped name is illegal for a mutex but legal for a
            // mapping, so the mapping succeeds and only the mutex fails.
            using var reader = new FreeTrackReader(heap, Unique("bad") + @"\x\y");
            bool opened = reader.Open();
            output.WriteLine("open returned " + opened);
            Assert.False(opened, "an open with no mutex must fail instead of reading unlocked");

            var buf = new byte[HeadPose.FreeTrackHeapBytes];
            Assert.False(reader.TryRead(buf), "a reader with no mutex must never copy the heap");
        }
    }
}
