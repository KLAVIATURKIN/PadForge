using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The SteelSeries worker against scripted report channels (#494): what
    /// it opens and when, the reports it sends and how far apart, what it
    /// hands back, and what it does when a device stops answering, a dongle's
    /// mouse sleeps or SteelSeries GG starts.
    ///
    /// <para>Nothing here waits for a length of time. The worker reads a
    /// clock the test sets, each channel stamps its calls with that clock,
    /// and a test changes one thing, the clock, a color, a table or a
    /// scripted answer, and then waits for two passes of the worker to end:
    /// the first may have begun before the change, the second began after
    /// it. A pass sends everything that is due at the clock it read, so the
    /// passes after it send nothing until the test changes something
    /// again.</para>
    ///
    /// <para>A test whose order across units matters sets the color before
    /// the table. A pass reads the table once, at its start, so every unit
    /// of the table is first served in one pass, in the order of the walk.
    /// A color set second could land between two units of a pass.</para>
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public class SteelSeriesDirectBackendTests : IDisposable
    {
        private static readonly Guid Box = new("11111111-2222-3333-4444-555555555555");
        private static readonly Guid OtherBox = new("99999999-2222-3333-4444-555555555555");
        private static readonly Guid Device = new("00000000-0000-0000-0000-0000000000a1");
        private static readonly Guid OtherDevice = new("00000000-0000-0000-0000-0000000000b2");

        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly List<FakeReportChannel> _channels = new();
        private readonly ConcurrentQueue<(string Path, string Frame, int TimeoutMs)> _oneShots = new();
        // Set by the test's thread and read by the worker's.
        private volatile Action<FakeReportChannel> _script = _ => { };
        private volatile Func<string, IHidReportChannel> _opener;
        private volatile Func<string, Guid> _containerOf = _ => Box;
        private volatile Action _onOneShot;
        private long _tick = 1_000_000;
        private int _opens;

        public SteelSeriesDirectBackendTests()
        {
            PeripheralOutputs.ResetForTest();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
        }

        public void Dispose()
        {
            SettingsManager.UserDevices = _devices;
            SettingsManager.UserSettings = _settings;
            PeripheralOutputs.ResetForTest();
        }

        // ── The bench ──

        private long Tick() => Interlocked.Read(ref _tick);
        private void Advance(long ms) => Interlocked.Add(ref _tick, ms);
        private int Opens => Volatile.Read(ref _opens);

        /// <summary>The opener the worker is handed. Every open gets a
        /// channel of its own, since a channel that was closed takes no more
        /// calls, and each stamps its calls with the test's clock.</summary>
        private IHidReportChannel Open(string path)
        {
            Interlocked.Increment(ref _opens);
            var custom = _opener;
            return custom != null ? custom(path) : NewChannel(path);
        }

        private FakeReportChannel NewChannel(string path, bool featureOnly = false)
        {
            var channel = new FakeReportChannel
            {
                Path = path, FeatureOnly = featureOnly,
                InputReportLength = 65, OutputReportLength = 65, FeatureReportLength = 643, Ticks = Tick,
            };
            _script(channel);
            lock (_channels) _channels.Add(channel);
            return channel;
        }

        /// <summary>The channel of the latest open, or of the open with
        /// this number.</summary>
        private FakeReportChannel Channel(int index = -1)
        {
            lock (_channels) return index < 0 ? _channels[^1] : _channels[index];
        }

        private SteelSeriesDirectBackend Make(int activeTickMs = 1, int idleTickMs = 5, Func<long> ticks = null)
            => new(Open, path => _containerOf(path), ticks ?? Tick,
                (path, frame, timeoutMs) =>
                {
                    _oneShots.Enqueue((path, Hex(frame), timeoutMs));
                    _onOneShot?.Invoke();
                    return true;
                },
                activeTickMs, idleTickMs);

        /// <summary>A started worker that has scanned these collections.</summary>
        private SteelSeriesDirectBackend Started(params HidCollectionInfo[] present)
        {
            var backend = Make();
            backend.Start();
            backend.Rescan(present);
            Passes(backend);
            return backend;
        }

        /// <summary>Waits for passes of the worker to end, by count.</summary>
        private static void Passes(SteelSeriesDirectBackend backend, int count = 2)
        {
            for (int i = 0; i < count; i++)
            {
                int before = backend.PassCount;
                backend.Wake();
                Assert.True(SpinWait.SpinUntil(() => backend.PassCount > before, 30000), "the worker made a pass");
            }
        }

        /// <summary>The control collection of a product, as the walk
        /// reports it.</summary>
        private static HidCollectionInfo Control(ushort productId, int iface, ushort output = 65, ushort feature = 643,
            ushort input = 65, string tag = "a")
            => new()
            {
                Path = $@"\\?\hid#vid_1038&pid_{productId:x4}&mi_{iface:x2}#{tag}",
                VendorId = 0x1038, ProductId = productId, UsagePage = 0xFFC0, Usage = 0x0001, InterfaceNumber = iface,
                InputReportLength = input, OutputReportLength = output, FeatureReportLength = feature,
            };

        private static OutputPath PathOf(HidCollectionInfo collection) => new(OutputFamily.SteelSeriesUnit, collection.Path);

        /// <summary>One row that holds these paths for lighting.</summary>
        private static void Link(params OutputPath[] paths)
            => PeripheralOutputs.PublishLinks(new LinkTable(new[] { new DeviceLinks { Device = Device, Lighting = paths } }));

        private static void Light(int rgb, int player = 1)
            => PeripheralOutputs.SetLighting(Device, slot: 0, player, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

        private static void Unlight() => PeripheralOutputs.ReleaseLighting(Device, 0);

        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace('-', ' ');

        /// <summary>The reports a channel was sent: "out" for an output
        /// report and "set" for a feature report, the bytes as the worker
        /// handed them over, and the clock at that call.</summary>
        private static List<(string Kind, string Bytes, long At)> Writes(FakeReportChannel channel)
            => channel.Calls.Where(c => c.Kind == "out" || c.Kind == "set")
                .Select(c => (c.Kind, Hex(c.Bytes), c.Tick)).ToList();

        private static string Frames(IEnumerable<SteelSeriesFrame> frames)
            => string.Join(" | ", frames.Select(f => (f.Report == SteelSeriesReport.Output ? "out " : "set ") + Hex(f.Buffer)));

        private static string Sent(FakeReportChannel channel)
            => string.Join(" | ", Writes(channel).Select(w => w.Kind + " " + w.Bytes));

        private string Recorded(SteelSeriesDirectBackend backend)
            => string.Join(" | ", backend.Recorded.Select(r => r.Path + " = " + string.Join(", ", r.Frames.Select(Hex))));

        /// <summary>The input reports a scripted device has to give, each
        /// as Windows delivers it, the report ID position first.</summary>
        private sealed class Inbox
        {
            private readonly ConcurrentQueue<byte[]> _reports = new();
            public void Add(params byte[] report) => _reports.Enqueue(report);
            public byte[] Next() => _reports.TryDequeue(out var report) ? report : null;
            public bool IsEmpty => _reports.IsEmpty;
        }

        /// <summary>A dongle that answers every color report as the mouse
        /// answers it while it listens: the command and 00.</summary>
        private static void AnswersEveryReport(FakeReportChannel channel)
        {
            int answered = 0;
            channel.Input = () =>
            {
                var writes = channel.Calls.Where(c => c.Kind == "out").ToList();
                if (writes.Count <= answered) return null;
                answered++;
                return new byte[] { 0x00, writes[answered - 1].Bytes[1], 0x00 };
            };
        }

        private static readonly PeripheralPresence GGRuns =
            new PeripheralPresence(false, false, false, true, false) { SteelSeriesGGRunning = true };

        // ── Scan ──

        /// <summary>A unit is the control collection of a product with a
        /// color, keyed by its path exactly as the walk first gave it. It
        /// leaves the list when its collection leaves the walk and for no
        /// other reason, and the scan opens nothing.</summary>
        [Fact]
        public void TheScanListsEachControlCollection_AndAUnitLeavesWithItsCollection()
        {
            var mouse = Control(0x1870, 3);
            var keyboard = Control(0x161A, 3, output: 33, feature: 515, input: 33, tag: "kbd");
            var others = new[]
            {
                // The second vendor collection of the mouse, its mouse
                // collection, a product with one fixed color, a product no
                // table lists, and another vendor's device.
                new HidCollectionInfo { Path = @"\\?\hid#vid_1038&pid_1870&mi_02#a", VendorId = 0x1038, ProductId = 0x1870,
                    UsagePage = 0xFF00, Usage = 1, InterfaceNumber = 2, InputReportLength = 65, OutputReportLength = 65 },
                new HidCollectionInfo { Path = @"\\?\hid#vid_1038&pid_1870&mi_00#a", VendorId = 0x1038, ProductId = 0x1870,
                    UsagePage = 0x0001, Usage = 2, InterfaceNumber = 0, InputReportLength = 9 },
                Control(0x1369, 0, tag: "raw"),
                Control(0x12AA, 3, tag: "new"),
                new HidCollectionInfo { Path = @"\\?\hid#vid_046d&pid_1870&mi_03#a", VendorId = 0x046D, ProductId = 0x1870,
                    UsagePage = 0xFFC0, Usage = 1, InterfaceNumber = 3, InputReportLength = 65, OutputReportLength = 65,
                    FeatureReportLength = 643 },
                null,
            };
            int changes = 0;
            using var backend = Make();
            backend.SnapshotChanged += () => Interlocked.Increment(ref changes);
            _containerOf = path => path == mouse.Path ? Box : OtherBox;

            // Not scanned until the host has handed a walk over.
            backend.Start();
            Passes(backend);
            Assert.Same(DirectBackendSnapshot.NotScanned, backend.Snapshot);
            Assert.Equal(0, Volatile.Read(ref changes));

            backend.Rescan(others.Concat(new[] { mouse, keyboard }).ToArray());
            Passes(backend);
            Assert.True(backend.Snapshot.Scanned);
            Assert.Equal(new[]
                {
                    new DirectUnit(OutputFamily.SteelSeriesUnit, mouse.Path, Box, 0x1038, 0x1870),
                    new DirectUnit(OutputFamily.SteelSeriesUnit, keyboard.Path, OtherBox, 0x1038, 0x161A),
                },
                backend.Snapshot.Units);
            Assert.Equal(1, Volatile.Read(ref changes));

            // The same collections again, the mouse's path in another case:
            // the same units under the same keys, and nothing to publish.
            var before = backend.Snapshot;
            backend.Rescan(new[] { mouse with { Path = mouse.Path.ToUpperInvariant() }, keyboard });
            Passes(backend);
            Assert.Same(before, backend.Snapshot);
            Assert.Equal(mouse.Path, backend.Snapshot.Units[0].Key);

            // A walk that failed hands nothing over, and nothing changes.
            backend.Rescan(null);
            Passes(backend);
            Assert.Same(before, backend.Snapshot);

            // The keyboard is unplugged.
            backend.Rescan(new[] { mouse });
            Passes(backend);
            Assert.Equal(mouse.Path, Assert.Single(backend.Snapshot.Units).Key);
            Assert.Equal(2, Volatile.Read(ref changes));

            // A walk that finds nothing still counts as a scan.
            backend.Rescan(Array.Empty<HidCollectionInfo>());
            Passes(backend);
            Assert.Empty(backend.Snapshot.Units);
            Assert.True(backend.Snapshot.Scanned);
            Assert.Equal(0, Opens);
        }

        /// <summary>The first scan is published even when it finds nothing,
        /// so the host stops waiting for this family.</summary>
        [Fact]
        public void TheFirstScanIsPublished_EvenWhenItFindsNothing()
        {
            int changes = 0;
            using var backend = Make();
            backend.SnapshotChanged += () => Interlocked.Increment(ref changes);
            backend.Start();
            backend.Rescan(Array.Empty<HidCollectionInfo>());
            Passes(backend);
            Assert.True(backend.Snapshot.Scanned);
            Assert.Empty(backend.Snapshot.Units);
            Assert.Equal(1, Volatile.Read(ref changes));
        }

        /// <summary>A unit whose container Windows cannot name yet is
        /// listed with none, which matches no row, and is asked about again
        /// every second until it answers.</summary>
        [Fact]
        public void AUnitWithNoContainerYet_IsAskedAboutAgain_UntilWindowsNamesIt()
        {
            var mouse = Control(0x1870, 3);
            int lookups = 0;
            bool named = false;
            _containerOf = _ =>
            {
                if (Interlocked.Increment(ref lookups) == 2) throw new InvalidOperationException("scripted");
                return Volatile.Read(ref named) ? Box : Guid.Empty;
            };
            using var backend = Started(mouse);
            Assert.Equal(Guid.Empty, Assert.Single(backend.Snapshot.Units).ContainerId);
            Assert.Equal(1, Volatile.Read(ref lookups));

            Advance(SteelSeriesDirectBackend.ContainerRetryMs - 1);
            Passes(backend);
            Assert.Equal(1, Volatile.Read(ref lookups));
            // A lookup that throws is a lookup that did not answer.
            Advance(1);
            Passes(backend);
            Assert.Equal(2, Volatile.Read(ref lookups));
            Assert.Equal(Guid.Empty, Assert.Single(backend.Snapshot.Units).ContainerId);

            Volatile.Write(ref named, true);
            Advance(SteelSeriesDirectBackend.ContainerRetryMs);
            Passes(backend);
            Assert.Equal(3, Volatile.Read(ref lookups));
            Assert.Equal(Box, Assert.Single(backend.Snapshot.Units).ContainerId);

            // Once named, it is not asked about again.
            Advance(10 * SteelSeriesDirectBackend.ContainerRetryMs);
            Passes(backend);
            Assert.Equal(3, Volatile.Read(ref lookups));
        }

        // ── Open, paint, hand back ──

        /// <summary>A Rival 3 Gen 2 is opened when a row links its unit and
        /// a claim resolves on it, gets GG's own frame, a new color no
        /// sooner than 33 ms after the last, and the frame that resumes its
        /// own animation when the claim ends. Then its handle closes.</summary>
        [Fact]
        public void ARival3Gen2_IsOpenedPaintedAndHandedBack_InThatOrder()
        {
            var mouse = Control(0x1870, 3);
            using var backend = Started(mouse);
            long t0 = Tick();

            Link(PathOf(mouse));
            Light(0x123456);
            Passes(backend);
            Assert.Equal(1, Opens);
            var channel = Channel();
            Assert.Equal(mouse.Path, channel.Path);
            Assert.Equal(new[] { ("out", "00 21 07 12 34 56 12 34 56 12 34 56", t0) }, Writes(channel));
            Assert.Equal(mouse.Path + " = 00 22 07", Recorded(backend));

            // A new color waits out the fastest repaint.
            Light(0xFF0000);
            Passes(backend);
            Advance(32);
            Passes(backend);
            Assert.Single(Writes(channel));
            Advance(1);
            Passes(backend);
            Assert.Equal(("out", "00 21 07 FF 00 00 FF 00 00 FF 00 00", t0 + 33), Writes(channel)[1]);

            Unlight();
            Passes(backend);
            Assert.Equal(new[]
                {
                    "00 21 07 12 34 56 12 34 56 12 34 56", "00 21 07 FF 00 00 FF 00 00 FF 00 00", "00 22 07",
                },
                Writes(channel).Select(w => w.Bytes));
            Assert.True(channel.Disposed);
            Assert.Empty(backend.Recorded);
            Assert.Equal(1, Opens);
            // The unit is still there, and nothing was asked of the device.
            Assert.False(Assert.Single(backend.Snapshot.Units).Asleep);
            Assert.DoesNotContain(channel.Calls, c => c.Kind == "in" || c.Kind == "get");
        }

        /// <summary>Nothing is opened while no row links the unit's path or
        /// no claim resolves on it, so a device nobody lights costs nothing,
        /// and the worker then waits its idle tick.</summary>
        [Fact]
        public void NothingIsOpened_WhileNoRowLinksThePath_OrNoClaimResolvesOnIt()
        {
            var mouse = Control(0x1870, 3);
            using var backend = Make(activeTickMs: 1, idleTickMs: 7);
            backend.Start();
            backend.Rescan(new[] { mouse });
            Passes(backend);
            Assert.Equal(7, backend.WaitMs);

            // A claim with no link.
            Light(0x123456);
            Passes(backend);
            Assert.Equal(0, Opens);

            // A link with no claim.
            Unlight();
            Link(PathOf(mouse));
            Passes(backend);
            Assert.Equal(0, Opens);

            // A claim on another row's device, which does not hold the path.
            PeripheralOutputs.SetLighting(OtherDevice, slot: 1, player: 2, 1, 2, 3);
            Passes(backend);
            Assert.Equal(0, Opens);
            Assert.Equal(7, backend.WaitMs);
            Assert.Empty(backend.Recorded);

            Light(0x123456);
            Passes(backend);
            Assert.Equal(1, Opens);
        }

        /// <summary>The crash path learns of a unit before the unit's first
        /// frame goes out. A gated keyboard is asked for its version first,
        /// which takes nothing from the board, and is on record by the time
        /// its claim is written, with the hand-back its version selects.</summary>
        [Fact]
        public void TheCrashRecord_HoldsTheUnit_BeforeItsFirstFrame()
        {
            var mouse = Control(0x1870, 3);
            var keyboard = Control(0x1634, 1, feature: 513, tag: "kbd");
            SteelSeriesDirectBackend backend = null;
            var held = new ConcurrentQueue<(string Path, int Call, string Record)>();
            _script = channel =>
            {
                if (channel.Path == keyboard.Path)
                {
                    var inbox = new Inbox();
                    channel.Input = () =>
                        channel.Calls.Any(c => c.Kind == "out" && c.Bytes[1] == 0x90) && inbox.IsEmpty == false ? inbox.Next() : null;
                    inbox.Add(0x00, 0x90, 0x00, 0x31, 0x2E, 0x31, 0x39, 0x2E, 0x37);
                }
                // The channel reads its clock inside each call, in front of
                // the call's entry in its log.
                channel.Ticks = () =>
                {
                    held.Enqueue((channel.Path, channel.Calls.Count, Recorded(backend)));
                    return Tick();
                };
            };
            using (backend = Started(mouse, keyboard))
            {
                Link(PathOf(mouse), PathOf(keyboard));
                Light(0x123456);
                Passes(backend);

                string RecordAt(FakeReportChannel channel, Func<(string Kind, byte[] Bytes, long Tick), bool> call)
                {
                    var calls = channel.Calls.ToList();
                    int index = calls.FindIndex(c => call(c));
                    Assert.True(index >= 0, "the call was made");
                    return held.Single(h => h.Path == channel.Path && h.Call == index).Record;
                }
                FakeReportChannel Of(HidCollectionInfo collection)
                {
                    lock (_channels) return _channels.Single(c => c.Path == collection.Path);
                }

                Assert.Contains(mouse.Path + " = 00 22 07", RecordAt(Of(mouse), c => c.Kind == "out"));
                // The query went out with the board not on record, and the
                // claim with it there.
                Assert.DoesNotContain(keyboard.Path, RecordAt(Of(keyboard), c => c.Kind == "out" && c.Bytes[1] == 0x90));
                Assert.Contains(keyboard.Path + " = 00 41 00", RecordAt(Of(keyboard), c => c.Kind == "set" && c.Bytes[1] == 0x4B));
            }
        }

        /// <summary>Two colors inside the family's fastest repaint give one
        /// frame, the newer: no color is queued, and the one that goes is
        /// the one that rules when its time comes.</summary>
        [Fact]
        public void TwoColorsInsideTheInterval_GiveOneFrame_TheNewer()
        {
            var mouse = Control(0x1870, 3);
            using var backend = Started(mouse);
            long t0 = Tick();
            Link(PathOf(mouse));
            Light(0x010101);
            Passes(backend);

            Advance(10);
            Light(0x020202);
            Passes(backend);
            Advance(10);
            Light(0x030303);
            Passes(backend);
            Assert.Single(Writes(Channel()));

            Advance(13);
            Passes(backend);
            Assert.Equal(new[]
                {
                    ("out", "00 21 07 01 01 01 01 01 01 01 01 01", t0),
                    ("out", "00 21 07 03 03 03 03 03 03 03 03 03", t0 + 33),
                },
                Writes(Channel()));
        }

        /// <summary>An unchanged color goes out again once the family's
        /// interval has passed since the last paint, however fast the color
        /// changed before that. A paint sets every LED, so it is its own
        /// re-assert and the interval counts from it.</summary>
        [Fact]
        public void AnUnchangedColorIsSentAgainOnItsInterval_CountedFromTheLastPaint()
        {
            var mouse = Control(0x1870, 3);
            using var backend = Started(mouse);
            long t0 = Tick();
            Link(PathOf(mouse));
            Light(0x000001);
            Passes(backend);
            for (int i = 2; i <= 6; i++)
            {
                Advance(40);
                Passes(backend);
                Light(i);
                Passes(backend);
            }
            long last = t0 + 200;
            Assert.Equal(new[] { t0, t0 + 40, t0 + 80, t0 + 120, t0 + 160, last }, Writes(Channel()).Select(w => w.At));

            Advance(SteelSeriesDirectProtocol.ReassertMs - 1);
            Passes(backend);
            Assert.Equal(6, Writes(Channel()).Count);
            Advance(1);
            Passes(backend);
            Assert.Equal(("out", "00 21 07 00 00 06 00 00 06 00 00 06", last + 5000), Writes(Channel())[6]);
            Advance(SteelSeriesDirectProtocol.ReassertMs);
            Passes(backend);
            Assert.Equal(last + 10000, Writes(Channel())[7].At);
            Assert.Equal(8, Writes(Channel()).Count);
        }

        /// <summary>A wired family that keeps a color until it loses power
        /// is painted once per change and never again: a Sensei 310 with one
        /// color for a minute gets its two feature reports, 24 ms apart, and
        /// nothing more.</summary>
        [Fact]
        public void AWiredLegacyFamily_IsNotReasserted()
        {
            var mouse = Control(0x1722, 0, feature: 263);
            using var backend = Started(mouse);
            long t0 = Tick();
            Link(PathOf(mouse));
            Light(0x123456);
            Passes(backend);
            Advance(23);
            Passes(backend);
            Assert.Single(Writes(Channel()));
            Advance(1);
            Passes(backend);
            var expected = SteelSeriesDirectProtocol.Color(SteelSeriesFamily.Rival310, SteelSeriesReport.Feature, false, 0x12, 0x34, 0x56);
            Assert.Equal(Frames(expected), Sent(Channel()));
            Assert.Equal(new[] { t0, t0 + 24 }, Writes(Channel()).Select(w => w.At));
            Assert.StartsWith("00 5B 00 00 10 27", Writes(Channel())[0].Bytes);
            Assert.StartsWith("00 5B 00 01 10 27", Writes(Channel())[1].Bytes);

            foreach (int step in new[] { 5000, 25000, 30000 })
            {
                Advance(step);
                Passes(backend);
            }
            Assert.Equal(2, Writes(Channel()).Count);
            // With nothing owed the worker waits its idle tick.
            Assert.Equal(5, backend.WaitMs);
            // No frame returns this family to its stored lighting: the
            // handle closes with no write, and the crash path never knew it.
            Assert.Empty(backend.Recorded);
            Unlight();
            Passes(backend);
            Assert.Equal(2, Writes(Channel()).Count);
            Assert.True(Channel().Disposed);
        }

        /// <summary>A family two shipped references send as different report
        /// types takes the one its collection has: a Sensei 310 collection
        /// with no feature report gets the same two frames as output
        /// reports.</summary>
        [Fact]
        public void AFeatureFamily_FallsBackToTheOutputReport_WhenItsCollectionLacksTheFeatureReport()
        {
            var mouse = Control(0x1722, 0, output: 65, feature: 0);
            using var backend = Started(mouse);
            Link(PathOf(mouse));
            Light(0x123456);
            Passes(backend);
            Advance(24);
            Passes(backend);
            var expected = SteelSeriesDirectProtocol.Color(SteelSeriesFamily.Rival310, SteelSeriesReport.Output, false, 0x12, 0x34, 0x56);
            Assert.Equal(Frames(expected), Sent(Channel()));
            Assert.All(Writes(Channel()), w => Assert.Equal("out", w.Kind));
        }

        /// <summary>A Rival 100 is set steady once per open, in front of its
        /// first color and 10 ms from it, and keeps what it shows: no
        /// re-assert, and no frame when the claim ends.</summary>
        [Fact]
        public void ARival100_IsSetSteadyOncePerOpen_ThenOnlyGivenColors()
        {
            var mouse = Control(0x1702, 0, output: 33, feature: 515, input: 33);
            using var backend = Started(mouse);
            long t0 = Tick();
            Link(PathOf(mouse));
            Light(0x123456);
            Passes(backend);
            Assert.Equal(new[] { ("out", "00 07 00 01", t0) }, Writes(Channel()));
            Advance(9);
            Passes(backend);
            Assert.Single(Writes(Channel()));
            Advance(1);
            Passes(backend);
            Assert.Equal(("out", "00 05 00 12 34 56", t0 + 10), Writes(Channel())[1]);

            // A second color: 50 ms after the first, with no second claim.
            Light(0xABCDEF);
            Passes(backend);
            Advance(49);
            Passes(backend);
            Assert.Equal(2, Writes(Channel()).Count);
            Advance(1);
            Passes(backend);
            Assert.Equal(("out", "00 05 00 AB CD EF", t0 + 60), Writes(Channel())[2]);

            Advance(60000);
            Passes(backend);
            Unlight();
            Passes(backend);
            Assert.Equal(3, Writes(Channel()).Count);
            Assert.True(Channel().Disposed);
            Assert.Empty(backend.Recorded);

            // A new claim is a new open, and a new open claims again.
            Light(0x010203);
            Passes(backend);
            Assert.Equal(2, Opens);
            Assert.Equal("out 00 07 00 01", Sent(Channel()));
        }

        /// <summary>A Rival 300 gets both steady claims, then both colors,
        /// each report 10 ms after the one before.</summary>
        [Fact]
        public void ARival300_GetsBothSteadyClaims_ThenBothColors_TenMillisecondsApart()
        {
            var mouse = Control(0x1710, 0, output: 33, feature: 515, input: 33);
            using var backend = Started(mouse);
            long t0 = Tick();
            Link(PathOf(mouse));
            Light(0x123456);
            Passes(backend);
            for (int i = 0; i < 3; i++)
            {
                Advance(9);
                Passes(backend);
                Assert.Equal(i + 1, Writes(Channel()).Count);
                Advance(1);
                Passes(backend);
            }
            Assert.Equal(new[]
                {
                    ("out", "00 07 01 01", t0), ("out", "00 07 02 01", t0 + 10),
                    ("out", "00 08 01 12 34 56", t0 + 20), ("out", "00 08 02 12 34 56", t0 + 30),
                },
                Writes(Channel()));
        }

        /// <summary>A Rival 600 takes its eight feature reports 22 ms apart.
        /// A second color starts no sooner than 200 ms after the first, and
        /// never between two reports of the first.</summary>
        [Fact]
        public void ARival600_SpacesItsEightFeatureReports_AndFinishesAColorBeforeTheNext()
        {
            var mouse = Control(0x1724, 0, output: 33, feature: 579, input: 33);
            using var backend = Started(mouse);
            long t0 = Tick();
            Link(PathOf(mouse));
            Light(0x123456);
            Passes(backend);
            // The color changes while the first is in flight.
            Advance(11);
            Passes(backend);
            Light(0xFF0000);
            Passes(backend);
            for (long at = 22; at <= 199; at += 11)
            {
                Advance(11);
                Passes(backend);
            }
            var first = SteelSeriesDirectProtocol.Color(SteelSeriesFamily.Rival600, SteelSeriesReport.Feature, false, 0x12, 0x34, 0x56);
            Assert.Equal(8, first.Length);
            Assert.Equal(Frames(first), Sent(Channel()));
            Assert.Equal(Enumerable.Range(0, 8).Select(led => t0 + led * 22), Writes(Channel()).Select(w => w.At));
            Assert.Equal(t0 + 198, Tick());

            Advance(1);
            Passes(backend);
            Assert.Equal(8, Writes(Channel()).Count);
            Advance(1);
            Passes(backend);
            var second = SteelSeriesDirectProtocol.Color(SteelSeriesFamily.Rival600, SteelSeriesReport.Feature, false, 0xFF, 0x00, 0x00);
            Assert.Equal(("set", Hex(second[0].Buffer), t0 + 200), Writes(Channel())[8]);
        }

        /// <summary>A Rival 650 takes sixteen reports 60 ms apart, so a
        /// color takes about a second. Thirty colors in that second give two
        /// paints, the first and the one that rules when the first is done,
        /// and the second is not changed halfway by the colors that
        /// follow.</summary>
        [Fact]
        public void ARival650_TakesASecondPerColor_AndIsNeverStreamed()
        {
            var mouse = Control(0x172B, 0);
            using var backend = Started(mouse);
            long t0 = Tick();
            Link(PathOf(mouse));
            Light(1);
            Passes(backend);
            // A new color every 20 ms, for just under two seconds.
            for (int step = 1; step <= 99; step++)
            {
                Advance(20);
                Passes(backend);
                Light(step + 1);
                Passes(backend);
            }
            var writes = Writes(Channel());
            var first = SteelSeriesDirectProtocol.Color(SteelSeriesFamily.Rival650, SteelSeriesReport.Output, false, 0, 0, 1);
            // The color that ruled at 1000 ms was the one set at 980 ms:
            // the pass at 1000 ms read it before the test set the next.
            var second = SteelSeriesDirectProtocol.Color(SteelSeriesFamily.Rival650, SteelSeriesReport.Output, false, 0, 0, 50);
            Assert.Equal(16, first.Length);
            Assert.Equal(32, writes.Count);
            Assert.Equal(Frames(first.Concat(second)), Sent(Channel()));
            Assert.Equal(Enumerable.Range(0, 16).Select(i => t0 + i * 60L)
                    .Concat(Enumerable.Range(0, 16).Select(i => t0 + 1000 + i * 60L)),
                writes.Select(w => w.At));
        }

        /// <summary>The crash path's record never holds a family with no
        /// hand-back, and such a unit's handle closes with no write.</summary>
        [Fact]
        public void AFamilyWithNoHandBack_SendsNothingWhenItsClaimEnds()
        {
            var mouse = Control(0x184C, 3, output: 33, feature: 515, input: 33);
            using var backend = Started(mouse);
            Link(PathOf(mouse));
            Light(0x123456);
            Passes(backend);
            Assert.Equal("out 00 0A 00 0F 12 34 56 12 34 56 12 34 56 12 34 56", Sent(Channel()));
            Assert.Empty(backend.Recorded);
            Unlight();
            Passes(backend);
            Assert.Single(Writes(Channel()));
            Assert.True(Channel().Disposed);
        }

        // ── SteelSeries GG ──

        /// <summary>SteelSeries GG starts while a unit is lit: the linker
        /// takes the unit's path away, and the worker lets go without a
        /// frame, since a hand-back that landed after GG's first frame
        /// would clear a color GG just set. A path that goes for any other
        /// reason, and a claim that ends while its path is still linked,
        /// are handed back as ever.</summary>
        [Fact]
        public void WhenSteelSeriesGGStarts_ALitUnitIsLetGoWithNoFrame_AndEveryOtherReleaseHandsBack()
        {
            var mouse = Control(0x1870, 3);
            var throughGG = new LinkTable(new[]
            {
                new DeviceLinks
                {
                    Device = Device, DirectWhenClosed = true,
                    Lighting = new[] { new OutputPath(OutputFamily.GameSenseColor, "mouse") },
                },
            });
            using var backend = Started(mouse);
            Link(PathOf(mouse));
            Light(0x123456);
            Passes(backend);
            Assert.Equal("out 00 21 07 12 34 56 12 34 56 12 34 56", Sent(Channel()));

            // The link pass publishes the presence, then the table without
            // the unit's path.
            PeripheralOutputs.Presence = GGRuns;
            Passes(backend);
            Assert.False(Channel().Disposed);
            PeripheralOutputs.PublishLinks(throughGG);
            Passes(backend);
            Assert.Single(Writes(Channel(0)));
            Assert.True(Channel(0).Disposed);
            Assert.Empty(backend.Recorded);
            Assert.Equal(1, Opens);

            // GG stops and the path comes back: opened and painted again.
            PeripheralOutputs.Presence = PeripheralPresence.None;
            Passes(backend);
            Link(PathOf(mouse));
            Passes(backend);
            Assert.Equal(2, Opens);
            Assert.Equal("out 00 21 07 12 34 56 12 34 56 12 34 56", Sent(Channel(1)));
            Assert.Equal(mouse.Path + " = 00 22 07", Recorded(backend));

            // The path goes with GG not running, a row that went offline:
            // handed back.
            PeripheralOutputs.PublishLinks(LinkTable.Empty);
            Passes(backend);
            Assert.Equal("out 00 21 07 12 34 56 12 34 56 12 34 56 | out 00 22 07", Sent(Channel(1)));
            Assert.True(Channel(1).Disposed);

            // The claim ends while its path is still linked, a switch
            // turned off, with GG running: handed back.
            Link(PathOf(mouse));
            Passes(backend);
            Assert.Equal(3, Opens);
            PeripheralOutputs.Presence = GGRuns;
            Passes(backend);
            Unlight();
            Passes(backend);
            Assert.Equal("out 00 21 07 12 34 56 12 34 56 12 34 56 | out 00 22 07", Sent(Channel(2)));
            Assert.True(Channel(2).Disposed);
            Assert.Empty(backend.Recorded);
        }

        // ── A device that stops answering ──

        /// <summary>A write that fails closes the unit's channel and lists
        /// the unit asleep. It stays on the list, the host is asked for one
        /// walk, the failed report is not sent again, and the unit is tried
        /// every five seconds by opening it, which sends nothing. Once it
        /// opens it is listed awake and the claim that still stands paints
        /// it. It leaves the list when its collection leaves the walk.</summary>
        [Fact]
        public void AFailedWrite_ListsTheUnitAsleep_AsksForOneWalk_AndTheStandingClaimPaintsAgain()
        {
            var mouse = Control(0x1870, 3);
            int changes = 0, asks = 0;
            var order = new ConcurrentQueue<string>();
            using var backend = Make();
            backend.SnapshotChanged += () =>
            {
                Interlocked.Increment(ref changes);
                order.Enqueue(backend.Snapshot.Units.Any(u => u.Asleep) ? "asleep" : "awake");
            };
            backend.RescanRequested += () =>
            {
                Interlocked.Increment(ref asks);
                order.Enqueue("ask");
            };
            backend.Start();
            backend.Rescan(new[] { mouse });
            Passes(backend);
            long t0 = Tick();
            Link(PathOf(mouse));
            Light(0x123456);
            Passes(backend);
            Assert.Single(Writes(Channel(0)));
            Assert.Equal((1, 0), (Volatile.Read(ref changes), Volatile.Read(ref asks)));

            // The device stops taking reports.
            Channel(0).FailWrites = true;
            Advance(40);
            Passes(backend);
            Light(0xFF0000);
            Passes(backend);
            Assert.True(Channel(0).Disposed);
            Assert.Single(Writes(Channel(0)));
            var asleep = Assert.Single(backend.Snapshot.Units);
            Assert.True(asleep.Asleep);
            Assert.Equal(mouse.Path, asleep.Key);
            Assert.Empty(backend.Recorded);
            Assert.Equal((2, 1), (Volatile.Read(ref changes), Volatile.Read(ref asks)));
            // The unit was listed asleep before the walk was asked for.
            Assert.Equal(new[] { "awake", "asleep", "ask" }, order);
            Assert.Equal(1, Opens);

            // Nothing is tried before the interval is over, though the
            // table still links the path and the claim still stands.
            Advance(SteelSeriesDirectBackend.RetryMs - 1);
            Passes(backend);
            Assert.Equal(1, Opens);

            // It does not open yet: still asleep, no second ask, and the
            // next try is a whole interval on.
            _opener = _ => null;
            Advance(1);
            Passes(backend);
            Assert.Equal(2, Opens);
            Assert.True(Assert.Single(backend.Snapshot.Units).Asleep);
            Advance(SteelSeriesDirectBackend.RetryMs - 1);
            Passes(backend);
            Assert.Equal(2, Opens);
            Assert.Equal((2, 1), (Volatile.Read(ref changes), Volatile.Read(ref asks)));

            // It opens: listed awake, and painted with the color that rules
            // now through a new handle. The try itself wrote nothing.
            _opener = null;
            Advance(1);
            Passes(backend);
            Assert.Equal(4, Opens);
            Assert.False(Assert.Single(backend.Snapshot.Units).Asleep);
            Assert.Empty(Channel(1).Calls);
            Assert.True(Channel(1).Disposed);
            Assert.Equal(new[] { ("out", "00 21 07 FF 00 00 FF 00 00 FF 00 00", t0 + 40 + 2 * SteelSeriesDirectBackend.RetryMs) },
                Writes(Channel(2)));
            Assert.Equal(mouse.Path + " = 00 22 07", Recorded(backend));
            Assert.Equal((3, 1), (Volatile.Read(ref changes), Volatile.Read(ref asks)));

            // The mouse is unplugged: the unit leaves the list, and nothing
            // is written to a device that is gone.
            backend.Rescan(Array.Empty<HidCollectionInfo>());
            Passes(backend);
            Assert.Empty(backend.Snapshot.Units);
            Assert.Single(Writes(Channel(2)));
            Assert.True(Channel(2).Disposed);
            Assert.Empty(backend.Recorded);
        }

        /// <summary>A unit that does not open for read and write is listed
        /// asleep without a write: one that does not open at all, and one
        /// that opens with no access right only, which another program
        /// holds. The second is a Sensei 310, whose feature reports such a
        /// handle would carry: they are not sent all the same.</summary>
        [Fact]
        public void AUnitThatDoesNotOpenForReadAndWrite_IsListedAsleep_AndNothingIsWritten()
        {
            var mouse = Control(0x1870, 3);
            var sensei = Control(0x1722, 0, feature: 263, tag: "b");
            int asks = 0;
            _opener = path => path == mouse.Path ? null : NewChannel(path, featureOnly: true);
            using var backend = Make();
            backend.RescanRequested += () => Interlocked.Increment(ref asks);
            backend.Start();
            backend.Rescan(new[] { mouse, sensei });
            Passes(backend);
            Light(0x123456);
            Link(PathOf(mouse), PathOf(sensei));
            Passes(backend);

            Assert.Equal(2, Opens);
            Assert.All(backend.Snapshot.Units, unit => Assert.True(unit.Asleep));
            Assert.Equal(2, backend.Snapshot.Units.Count);
            // The handle that came with no access right was closed unused.
            Assert.True(Channel(0).Disposed);
            Assert.Empty(Channel(0).Calls);
            // One ask covers both units that failed in the pass.
            Assert.Equal(1, Volatile.Read(ref asks));

            // The same again at the next try: still asleep, nothing written.
            Advance(SteelSeriesDirectBackend.RetryMs);
            Passes(backend);
            Assert.Equal(4, Opens);
            Assert.All(backend.Snapshot.Units, unit => Assert.True(unit.Asleep));
            lock (_channels)
            {
                Assert.Equal(2, _channels.Count);
                Assert.All(_channels, channel => Assert.Empty(channel.Calls));
                Assert.All(_channels, channel => Assert.True(channel.Disposed));
            }
            Assert.Equal(1, Volatile.Read(ref asks));
        }

        // ── Dongles ──

        /// <summary>A dongle unit that answers nothing until the test puts
        /// an answer in its inbox.</summary>
        private Inbox DongleWithInbox()
        {
            var inbox = new Inbox();
            _script = channel => channel.Input = inbox.Next;
            return inbox;
        }

        /// <summary>Through a dongle, the next zone's report goes only after
        /// the dongle answered the one before, and 6 ms after that answer.
        /// What the dongle had queued before a report is not its answer.</summary>
        [Fact]
        public void ADongle_WaitsForTheAnswer_BeforeTheNextZone()
        {
            var dongle = Control(0x1852, 3, feature: 513);
            var inbox = DongleWithInbox();
            // Left in the queue from before: an answer to nobody.
            inbox.Add(0x00, 0x61, 0x00);
            using var backend = Started(dongle);
            long t0 = Tick();
            Link(PathOf(dongle));
            Light(0x123456);
            Passes(backend);
            Assert.Equal(new[] { ("out", "00 61 01 00 12 34 56", t0) }, Writes(Channel()));
            Assert.True(inbox.IsEmpty);

            // No answer yet: nothing more goes, however long the gap.
            Advance(10);
            Passes(backend);
            Assert.Single(Writes(Channel()));

            // The answer comes 10 ms after the report. The gap counts from it.
            inbox.Add(0x00, 0x61, 0x00);
            Passes(backend);
            Advance(5);
            Passes(backend);
            Assert.Single(Writes(Channel()));
            Advance(1);
            Passes(backend);
            Assert.Equal(("out", "00 61 01 01 12 34 56", t0 + 16), Writes(Channel())[1]);

            inbox.Add(0x00, 0x61, 0x00);
            Passes(backend);
            Advance(6);
            Passes(backend);
            Assert.Equal(("out", "00 61 01 02 12 34 56", t0 + 22), Writes(Channel())[2]);

            // The last zone's answer ends the color, and nothing follows it.
            inbox.Add(0x00, 0x61, 0x00);
            Passes(backend);
            Advance(100);
            Passes(backend);
            Assert.Equal(3, Writes(Channel()).Count);
            Assert.Equal(dongle.Path + " = 00 62 FF", Recorded(backend));

            // The claim ends: the mouse's own effect is asked for, and the
            // handle closes.
            Unlight();
            Passes(backend);
            Assert.Equal("00 62 FF", Writes(Channel())[3].Bytes);
            Assert.True(Channel().Disposed);
        }

        /// <summary>A report that starts with another byte is not the
        /// answer: the vendor program's own queries and the mouse's status
        /// reports share the endpoint. The worker reads on.</summary>
        [Fact]
        public void AnotherInputReport_IsNotTakenForTheAnswer()
        {
            var dongle = Control(0x1852, 3, feature: 513);
            var inbox = DongleWithInbox();
            using var backend = Started(dongle);
            Link(PathOf(dongle));
            Light(0x123456);
            Passes(backend);

            // A battery report, a report too short to say anything, and the
            // answer to another command.
            inbox.Add(0x00, 0xD2, 0x01);
            inbox.Add(0x00);
            inbox.Add(0x00, 0x62, 0x00);
            Passes(backend);
            Advance(6);
            Passes(backend);
            Assert.Single(Writes(Channel()));
            Assert.True(inbox.IsEmpty);

            inbox.Add(0x00, 0x61, 0x00);
            Passes(backend);
            Advance(6);
            Passes(backend);
            Assert.Equal(2, Writes(Channel()).Count);
        }

        /// <summary>A dongle whose mouse is switched off or asleep answers
        /// 40 FF. The color in flight is dropped and the unit is left alone
        /// for a second, with its handle open, its unit listed awake and
        /// its claim standing. Then the color starts again from its first
        /// report, and once the mouse answers the whole color goes out
        /// though it never changed.</summary>
        [Fact]
        public void ADongleWhoseMouseIsOff_IsLeftAloneForASecond_ThenGetsTheWholeColorAgain()
        {
            var dongle = Control(0x1852, 3, feature: 513);
            var inbox = DongleWithInbox();
            int changes = 0;
            using var backend = Make();
            backend.SnapshotChanged += () => Interlocked.Increment(ref changes);
            backend.Start();
            backend.Rescan(new[] { dongle });
            Passes(backend);
            long t0 = Tick();
            Link(PathOf(dongle));
            Light(0x123456);
            Passes(backend);
            Assert.Equal(dongle.Path + " = 00 62 FF", Recorded(backend));

            inbox.Add(0x00, 0x40, 0xFF);
            Passes(backend);
            // A mouse that is off shows no color of ours: nothing to undo.
            Assert.Empty(backend.Recorded);
            Advance(SteelSeriesDirectBackend.QuietMs - 1);
            Passes(backend);
            Assert.Single(Writes(Channel()));
            Assert.False(Channel().Disposed);
            Assert.False(Assert.Single(backend.Snapshot.Units).Asleep);
            Assert.Equal(1, Volatile.Read(ref changes));
            Assert.Equal(1, Opens);

            // One report a second while the mouse stays off.
            Advance(1);
            Passes(backend);
            Assert.Equal(("out", "00 61 01 00 12 34 56", t0 + 1000), Writes(Channel())[1]);
            inbox.Add(0x00, 0x40, 0xFF);
            Passes(backend);
            Advance(SteelSeriesDirectBackend.QuietMs);
            Passes(backend);
            Assert.Equal(("out", "00 61 01 00 12 34 56", t0 + 2000), Writes(Channel())[2]);
            Assert.Equal(3, Writes(Channel()).Count);

            // The mouse is back: every zone, with the color that never
            // changed.
            foreach (int _ in Enumerable.Range(0, 3))
            {
                inbox.Add(0x00, 0x61, 0x00);
                Passes(backend);
                Advance(6);
                Passes(backend);
            }
            Assert.Equal(new[]
                {
                    "00 61 01 00 12 34 56", "00 61 01 00 12 34 56", "00 61 01 00 12 34 56",
                    "00 61 01 01 12 34 56", "00 61 01 02 12 34 56",
                },
                Writes(Channel()).Select(w => w.Bytes));
            Assert.Equal(dongle.Path + " = 00 62 FF", Recorded(backend));
            Assert.Equal(1, Opens);
        }

        /// <summary>A dongle that says nothing for 250 ms is treated like
        /// one whose mouse is off, but for the hand-back: it never said the
        /// mouse was off, so the mouse may show a color of ours.</summary>
        [Fact]
        public void ADongleThatSaysNothing_IsLeftAloneForASecond()
        {
            var dongle = Control(0x1852, 3, feature: 513);
            DongleWithInbox();
            using var backend = Started(dongle);
            long t0 = Tick();
            Link(PathOf(dongle));
            Light(0x123456);
            Passes(backend);
            Advance(SteelSeriesDirectBackend.EchoTimeoutMs - 1);
            Passes(backend);
            Assert.Single(Writes(Channel()));

            // The limit passes: the pause starts here.
            Advance(1);
            Passes(backend);
            Advance(SteelSeriesDirectBackend.QuietMs - 1);
            Passes(backend);
            Assert.Single(Writes(Channel()));
            Assert.False(Channel().Disposed);
            Assert.Equal(dongle.Path + " = 00 62 FF", Recorded(backend));
            Advance(1);
            Passes(backend);
            Assert.Equal(("out", "00 61 01 00 12 34 56", t0 + 250 + 1000), Writes(Channel())[1]);

            // The claim ends inside the next wait: the answer is waited for
            // until its limit, and then the hand-back goes.
            Unlight();
            Passes(backend);
            Assert.Equal(2, Writes(Channel()).Count);
            Assert.False(Channel().Disposed);
            Advance(SteelSeriesDirectBackend.EchoTimeoutMs);
            Passes(backend);
            Assert.Equal("00 62 FF", Writes(Channel())[2].Bytes);
            Assert.True(Channel().Disposed);
        }

        /// <summary>A dongle that last answered 40 FF gets no hand-back: its
        /// mouse wakes into its own lighting.</summary>
        [Fact]
        public void ADongleWhoseMouseIsOff_GetsNoHandBack()
        {
            var dongle = Control(0x1852, 3, feature: 513);
            var inbox = DongleWithInbox();
            using var backend = Started(dongle);
            Link(PathOf(dongle));
            Light(0x123456);
            Passes(backend);
            inbox.Add(0x00, 0x40, 0xFF);
            Passes(backend);

            Unlight();
            Passes(backend);
            Assert.Equal("out 00 61 01 00 12 34 56", Sent(Channel()));
            Assert.True(Channel().Disposed);
            Assert.Empty(backend.Recorded);
        }

        /// <summary>A mouse that was off may wake on the very report that
        /// asked for it. When the claim ends while that report's answer is
        /// still owed, the answer decides: a mouse that answers took a
        /// color of ours and is handed back, one that is still off, or
        /// still silent, is not.</summary>
        [Theory]
        [InlineData("answers", true)]
        [InlineData("off", false)]
        [InlineData("silent", false)]
        public void ADongleAskedAsItsClaimEnds_IsHandedBackOnlyWhenItsMouseAnswers(string answer, bool handedBack)
        {
            var dongle = Control(0x1852, 3, feature: 513);
            var inbox = DongleWithInbox();
            using var backend = Started(dongle);
            long t0 = Tick();
            Link(PathOf(dongle));
            Light(0x123456);
            Passes(backend);
            inbox.Add(0x00, 0x40, 0xFF);
            Passes(backend);
            Advance(SteelSeriesDirectBackend.QuietMs);
            Passes(backend);
            Assert.Equal(2, Writes(Channel()).Count);
            // The report that asks is out, and the unit is on record again.
            Assert.Equal(dongle.Path + " = 00 62 FF", Recorded(backend));

            Unlight();
            Passes(backend);
            Assert.False(Channel().Disposed);
            if (answer == "answers") inbox.Add(0x00, 0x61, 0x00);
            else if (answer == "off") inbox.Add(0x00, 0x40, 0xFF);
            else Advance(SteelSeriesDirectBackend.EchoTimeoutMs);
            Passes(backend);
            Advance(6);
            Passes(backend);
            Assert.True(Channel().Disposed);
            Assert.Equal(handedBack ? 3 : 2, Writes(Channel()).Count);
            if (handedBack) Assert.Equal(("out", "00 62 FF", t0 + 1006), Writes(Channel())[2]);
            Assert.Empty(backend.Recorded);
        }

        /// <summary>The hand-back keeps the order of the reports: the answer
        /// to the report before it, then the gap. A mouse that goes off in
        /// that moment gets none.</summary>
        [Fact]
        public void ADonglesHandBack_FollowsTheAnswerToTheReportBefore()
        {
            var dongle = Control(0x1852, 3, feature: 513);
            var other = Control(0x1838, 3, feature: 513, tag: "b");
            var inboxes = new Dictionary<string, Inbox> { [dongle.Path] = new Inbox(), [other.Path] = new Inbox() };
            _script = channel => channel.Input = inboxes[channel.Path].Next;
            FakeReportChannel Of(HidCollectionInfo collection)
            {
                lock (_channels) return _channels.Single(c => c.Path == collection.Path);
            }
            using var backend = Started(dongle, other);
            long t0 = Tick();
            Link(PathOf(dongle), PathOf(other));
            Light(0x123456);
            Passes(backend);

            // The claim ends while both wait for the answer to zone 0.
            Unlight();
            Passes(backend);
            Assert.Single(Writes(Of(dongle)));
            Assert.False(Of(dongle).Disposed);

            Advance(8);
            Passes(backend);
            inboxes[dongle.Path].Add(0x00, 0x61, 0x00);
            Passes(backend);
            inboxes[other.Path].Add(0x00, 0x40, 0xFF);
            Passes(backend);
            // The mouse that went off is done with, with no frame.
            Assert.Single(Writes(Of(other)));
            Assert.True(Of(other).Disposed);
            // The one that answered waits out the gap.
            Assert.Single(Writes(Of(dongle)));
            Advance(5);
            Passes(backend);
            Assert.Single(Writes(Of(dongle)));
            Advance(1);
            Passes(backend);
            Assert.Equal(("out", "00 62 FF", t0 + 14), Writes(Of(dongle))[1]);
            Assert.True(Of(dongle).Disposed);
            Assert.Empty(backend.Recorded);
        }

        /// <summary>A Prime Wireless through its dongle takes one report per
        /// color and its answer, repaints no sooner than 50 ms on, and has
        /// no frame that returns it to its own lighting.</summary>
        [Fact]
        public void APrimeWirelessDongle_TakesOneAnsweredReportPerColor()
        {
            var dongle = Control(0x1840, 3, feature: 513);
            _script = AnswersEveryReport;
            using var backend = Started(dongle);
            long t0 = Tick();
            Link(PathOf(dongle));
            Light(0x123456);
            Passes(backend);
            Light(0xFF0000);
            Passes(backend);
            Advance(49);
            Passes(backend);
            Assert.Equal(new[] { ("out", "00 61 01 00 12 34 56", t0) }, Writes(Channel()));
            Advance(1);
            Passes(backend);
            Assert.Equal(("out", "00 61 01 00 FF 00 00", t0 + 50), Writes(Channel())[1]);
            Assert.Empty(backend.Recorded);
            Unlight();
            Passes(backend);
            Assert.Equal(2, Writes(Channel()).Count);
            Assert.True(Channel().Disposed);
        }

        /// <summary>A dongle whose input report cannot be read is a unit
        /// that does not answer: its channel closes and it is listed
        /// asleep.</summary>
        [Fact]
        public void ADongleThatCannotBeRead_IsListedAsleep()
        {
            var dongle = Control(0x1852, 3, feature: 513);
            // The scripted channel fails a read once it is told to fail.
            var failing = new ConcurrentDictionary<string, bool>();
            _opener = path => new ReadFailingChannel(NewChannel(path), () => failing.ContainsKey(path));
            using var backend = Started(dongle);
            Link(PathOf(dongle));
            Light(0x123456);
            Passes(backend);
            Assert.Single(Writes(Channel()));
            Assert.False(Assert.Single(backend.Snapshot.Units).Asleep);

            failing[dongle.Path] = true;
            Passes(backend);
            Assert.True(Assert.Single(backend.Snapshot.Units).Asleep);
            Assert.True(Channel().Disposed);
            Assert.Empty(backend.Recorded);
        }

        /// <summary>A channel whose input reads fail on demand, the way a
        /// real one fails once its device has left.</summary>
        private sealed class ReadFailingChannel : IHidReportChannel
        {
            private readonly FakeReportChannel _inner;
            private readonly Func<bool> _fail;

            public ReadFailingChannel(FakeReportChannel inner, Func<bool> fail)
            {
                _inner = inner;
                _fail = fail;
            }

            public string Path => _inner.Path;
            public bool FeatureOnly => _inner.FeatureOnly;
            public int InputReportLength => _inner.InputReportLength;
            public int OutputReportLength => _inner.OutputReportLength;
            public int FeatureReportLength => _inner.FeatureReportLength;
            public bool SetFeature(byte[] report) => _inner.SetFeature(report);
            public bool GetFeature(byte reportId, byte[] buffer) => _inner.GetFeature(reportId, buffer);
            public bool WriteOutput(byte[] report, int timeoutMs = HidReportChannel.WriteTimeoutMs) => _inner.WriteOutput(report, timeoutMs);
            public int ReadInput(byte[] buffer, int timeoutMs) => _fail() ? -1 : _inner.ReadInput(buffer, timeoutMs);
            public void Dispose() => _inner.Dispose();
        }

        // ── The per-key Apex boards ──

        /// <summary>A keyboard that answers the version query with this
        /// text, once the query is out.</summary>
        private void AnswersVersion(params byte[] reply)
            => _script = channel =>
            {
                bool given = false;
                channel.Input = () =>
                {
                    if (given || !channel.Calls.Any(c => c.Kind == "out" && c.Bytes[1] == 0x90)) return null;
                    given = true;
                    return reply;
                };
            };

        private static string PerKey(byte packet)
            => "set " + Hex(SteelSeriesDirectProtocol.Color(
                packet == 0x3A ? SteelSeriesFamily.ApexPro : packet == 0x40 ? SteelSeriesFamily.Apex9 : SteelSeriesFamily.ApexProWireless,
                SteelSeriesReport.Feature, false, 0x12, 0x34, 0x56)[0].Buffer);

        /// <summary>An Apex 9 on firmware 1.19.7 is of the third
        /// generation: asked for its version first, claimed with the feature
        /// report 4B, painted 66 ms on, and handed back with 41 00.</summary>
        [Fact]
        public void AnApex9OnNewFirmware_ClaimsWith4B_AndHandsBackWith41()
        {
            var keyboard = Control(0x1634, 1, feature: 513);
            AnswersVersion(0x00, 0x90, 0x00, 0x31, 0x2E, 0x31, 0x39, 0x2E, 0x37);
            using var backend = Started(keyboard);
            long t0 = Tick();
            Link(PathOf(keyboard));
            Light(0x123456);
            Passes(backend);
            Assert.Equal(new[] { ("out", "00 90", t0), ("set", "00 4B", t0) }, Writes(Channel()));
            Assert.Equal(keyboard.Path + " = 00 41 00", Recorded(backend));

            Advance(65);
            Passes(backend);
            Assert.Equal(2, Writes(Channel()).Count);
            Advance(1);
            Passes(backend);
            Assert.Equal("out 00 90 | set 00 4B | " + PerKey(0x40), Sent(Channel()));
            Assert.StartsWith("00 40 70 04 12 34 56 05 12 34 56", Writes(Channel())[2].Bytes);
            Assert.Equal(t0 + 66, Writes(Channel())[2].At);

            Unlight();
            Passes(backend);
            // The hand-back waits out the gap after the color.
            Assert.Equal(3, Writes(Channel()).Count);
            Advance(66);
            Passes(backend);
            Assert.Equal(("out", "00 41 00", t0 + 132), Writes(Channel())[3]);
            Assert.True(Channel().Disposed);
            Assert.Empty(backend.Recorded);
        }

        /// <summary>An Apex 9 on an older firmware takes no claim and is
        /// handed back with 3B.</summary>
        [Fact]
        public void AnApex9OnOldFirmware_SendsNoClaim_AndHandsBackWith3B()
        {
            var keyboard = Control(0x1634, 1, feature: 513);
            AnswersVersion(0x00, 0x90, 0x00, 0x31, 0x2E, 0x31, 0x38, 0x2E, 0x30);
            using var backend = Started(keyboard);
            long t0 = Tick();
            Link(PathOf(keyboard));
            Light(0x123456);
            Passes(backend);
            Assert.Equal("out 00 90 | " + PerKey(0x40), Sent(Channel()));
            Assert.Equal(keyboard.Path + " = 00 3B", Recorded(backend));
            Unlight();
            Passes(backend);
            Advance(66);
            Passes(backend);
            Assert.Equal(("out", "00 3B", t0 + 66), Writes(Channel())[2]);
        }

        /// <summary>An Apex Pro TKL changes its packet number with its
        /// firmware: 3A before 1.19.7, 40 from it on.</summary>
        [Theory]
        [InlineData(0x36, "out 00 90 | ", 0x3A, "00 3B")]
        [InlineData(0x37, "out 00 90 | set 00 4B | ", 0x40, "00 41 00")]
        public void AnApexProTkl_TakesThePacketOfItsFirmware(int patch, string before, int packet, string handBack)
        {
            var keyboard = Control(0x1614, 1);
            AnswersVersion(0x00, 0x90, 0x00, 0x31, 0x2E, 0x31, 0x39, 0x2E, (byte)patch);
            using var backend = Started(keyboard);
            Link(PathOf(keyboard));
            Light(0x123456);
            Passes(backend);
            Advance(66);
            Passes(backend);
            Assert.Equal(before + PerKey((byte)packet), Sent(Channel()));
            Assert.Equal(keyboard.Path + " = " + handBack, Recorded(backend));
        }

        /// <summary>A gated keyboard that does not answer the query within
        /// 100 ms gets the frames of its first reading, and is not asked
        /// again. A report that is no version reply is not taken for one.</summary>
        [Fact]
        public void AnApexThatDoesNotAnswer_TakesTheOlderFrames_AndIsNotAskedAgain()
        {
            var keyboard = Control(0x1634, 1, feature: 513);
            var inbox = new Inbox();
            _script = channel => channel.Input = inbox.Next;
            using var backend = Started(keyboard);
            long t0 = Tick();
            Link(PathOf(keyboard));
            Light(0x123456);
            Passes(backend);
            Assert.Equal("out 00 90", Sent(Channel()));
            Assert.Empty(backend.Recorded);

            // The answer to some other request.
            inbox.Add(0x00, 0xA3, 0x00, 0x05);
            Passes(backend);
            Advance(SteelSeriesDirectBackend.QueryTimeoutMs - 1);
            Passes(backend);
            Assert.Equal("out 00 90", Sent(Channel()));
            Advance(1);
            Passes(backend);
            Assert.Equal("out 00 90 | " + PerKey(0x40), Sent(Channel()));
            Assert.Equal(t0 + 100, Writes(Channel())[1].At);
            Assert.Equal(keyboard.Path + " = 00 3B", Recorded(backend));

            // A version that comes too late changes nothing.
            inbox.Add(0x00, 0x90, 0x00, 0x31, 0x2E, 0x31, 0x39, 0x2E, 0x37);
            Advance(SteelSeriesDirectProtocol.ReassertMs);
            Passes(backend);
            Assert.Equal("out 00 90 | " + PerKey(0x40) + " | " + PerKey(0x40), Sent(Channel()));
        }

        /// <summary>A gated keyboard whose claim ends before it answered the
        /// query was asked a question and sent nothing else, so it shows no
        /// color of ours: its handle closes with no hand-back, and the crash
        /// path never knew it.</summary>
        [Fact]
        public void AGatedBoardLetGoBeforeItAnswers_IsSentNothingMore()
        {
            var keyboard = Control(0x1634, 1, feature: 513);
            using var backend = Started(keyboard);
            Link(PathOf(keyboard));
            Light(0x123456);
            Passes(backend);
            Assert.Equal("out 00 90", Sent(Channel()));
            Assert.Empty(backend.Recorded);

            Unlight();
            Passes(backend);
            Assert.Equal("out 00 90", Sent(Channel()));
            Assert.True(Channel().Disposed);
            Assert.Empty(backend.Recorded);

            // A new claim is a new open, and the board is asked again.
            Light(0x123456);
            Passes(backend);
            Assert.Equal(2, Opens);
            Assert.Equal("out 00 90", Sent(Channel()));
        }

        /// <summary>The version query goes out once per open, first, on the
        /// four boards a firmware update moves to the third generation, and
        /// on no other.</summary>
        [Fact]
        public void TheVersionQuery_GoesOutOncePerOpen_First_OnTheGatedBoardsAlone()
        {
            ushort[] gated = { 0x1614, 0x1620, 0x1634, 0x1628 };
            ushort[] others = { 0x1610, 0x1612, 0x1640, 0x1642 };
            var boards = gated.Concat(others).Select(id => Control(id, 1, tag: id.ToString("x4"))).ToArray();
            using var backend = Started(boards);
            Link(boards.Select(PathOf).ToArray());
            Light(0x123456);
            Passes(backend);
            Advance(SteelSeriesDirectBackend.QueryTimeoutMs);
            Passes(backend);
            Advance(66);
            Passes(backend);
            Advance(SteelSeriesDirectProtocol.ReassertMs);
            Passes(backend);

            lock (_channels)
            {
                Assert.Equal(8, _channels.Count);
                foreach (var channel in _channels)
                {
                    var writes = Writes(channel);
                    ushort productId = boards.Single(b => b.Path == channel.Path).ProductId;
                    int queries = writes.Count(w => w.Bytes == "00 90");
                    Assert.True(writes.Count >= 2, $"{productId:X4} was painted");
                    if (gated.Contains(productId))
                    {
                        Assert.Equal(1, queries);
                        Assert.Equal("00 90", writes[0].Bytes);
                    }
                    else
                    {
                        Assert.Equal(0, queries);
                        Assert.DoesNotContain(channel.Calls, c => c.Kind == "in");
                    }
                }
            }
        }

        /// <summary>An Apex Pro Gen 3 is claimed with the output report
        /// 41 01, GG's own, in front of its first 3A frame, and handed back
        /// with 41 00. The claim goes out once per open: two re-asserts on,
        /// there is one claim and three colors.</summary>
        [Fact]
        public void AnApexProGen3_ClaimsWith4101Once_AndHandsBackWith4100()
        {
            var keyboard = Control(0x1640, 1);
            using var backend = Started(keyboard);
            long t0 = Tick();
            Link(PathOf(keyboard));
            Light(0x123456);
            Passes(backend);
            Assert.Equal(new[] { ("out", "00 41 01", t0) }, Writes(Channel()));
            Assert.Equal(keyboard.Path + " = 00 41 00", Recorded(backend));
            Advance(66);
            Passes(backend);
            Advance(SteelSeriesDirectProtocol.ReassertMs);
            Passes(backend);
            Advance(SteelSeriesDirectProtocol.ReassertMs);
            Passes(backend);
            Assert.Equal("out 00 41 01 | " + PerKey(0x3A) + " | " + PerKey(0x3A) + " | " + PerKey(0x3A), Sent(Channel()));
            Assert.Equal(new[] { t0, t0 + 66, t0 + 5066, t0 + 10066 }, Writes(Channel()).Select(w => w.At));

            Unlight();
            Passes(backend);
            Advance(66);
            Passes(backend);
            Assert.Equal(("out", "00 41 00", t0 + 10132), Writes(Channel())[4]);
            Assert.True(Channel().Disposed);
            // Nothing was read from a board that is not asked anything.
            Assert.DoesNotContain(Channel().Calls, c => c.Kind == "in");
        }

        /// <summary>A wireless Apex Pro TKL is of the third generation from
        /// the start: 4B, the per-key frame under packet 61, and 41 00.</summary>
        [Fact]
        public void AWirelessApexPro_ClaimsWith4B_AndPaintsUnderPacket61()
        {
            var keyboard = Control(0x1630, 3);
            using var backend = Started(keyboard);
            Link(PathOf(keyboard));
            Light(0x123456);
            Passes(backend);
            Advance(66);
            Passes(backend);
            Assert.Equal("set 00 4B | " + PerKey(0x61), Sent(Channel()));
            Assert.Equal(keyboard.Path + " = 00 41 00", Recorded(backend));
        }

        /// <summary>An Apex M750 takes its three claims 10 ms apart, then
        /// the whole grid, all as feature reports to a collection that has
        /// no other report, and no frame when the claim ends.</summary>
        [Fact]
        public void AnApexM750_SendsItsThreeClaims_ThenTheGrid_AndNothingWhenTheClaimEnds()
        {
            var keyboard = Control(0x0616, 2, output: 0, feature: 513, input: 0);
            using var backend = Started(keyboard);
            long t0 = Tick();
            Link(PathOf(keyboard));
            Light(0x123456);
            Passes(backend);
            foreach (int _ in Enumerable.Range(0, 3))
            {
                Advance(10);
                Passes(backend);
            }
            var writes = Writes(Channel());
            Assert.Equal(new[] { "00 00 00 00 01 00 85", "00 00 00 00 03 01 00 FF", "00 00 00 00 01 00 85 FF" },
                writes.Take(3).Select(w => w.Bytes));
            // The grid: the color on the first three keys, the vendor's
            // filler in the first slot with none, 132 slots in all.
            Assert.StartsWith("00 00 00 01 8E 01 03 06 16 12 34 56 12 34 56 12 34 56 FF 32 00 12 34 56 FF 32 00", writes[3].Bytes);
            Assert.Equal(Hex(SteelSeriesDirectProtocol.Color(SteelSeriesFamily.ApexM750, SteelSeriesReport.Feature, false, 0x12, 0x34, 0x56)[0].Buffer),
                writes[3].Bytes);
            Assert.Equal(1 + 8 + 132 * 3, writes[3].Bytes.Split(' ').Length);
            Assert.All(writes, w => Assert.Equal("set", w.Kind));
            Assert.Equal(new[] { t0, t0 + 10, t0 + 20, t0 + 30 }, writes.Select(w => w.At));
            Assert.Empty(backend.Recorded);

            // The re-assert sends the grid alone.
            Advance(SteelSeriesDirectProtocol.ReassertMs);
            Passes(backend);
            Assert.Equal(5, Writes(Channel()).Count);
            Assert.Equal(writes[3].Bytes, Writes(Channel())[4].Bytes);

            Unlight();
            Passes(backend);
            Assert.Equal(5, Writes(Channel()).Count);
            Assert.True(Channel().Disposed);
        }

        // ── Stop, the crash path and calls after a stop ──

        /// <summary>Stop hands back every unit the worker holds before it
        /// closes its handles, a color in flight included, and the backend
        /// reads as not scanned afterwards. A start after it runs on a new
        /// walk.</summary>
        [Fact]
        public void Stop_HandsBackEveryUnitItHolds_AndAStartAfterItScansAnew()
        {
            var aerox = Control(0x1836, 3, feature: 515);
            var rival = Control(0x184C, 3, output: 33, feature: 515, input: 33, tag: "b");
            var dongle = Control(0x1852, 3, feature: 513, tag: "c");
            FakeReportChannel Of(HidCollectionInfo collection)
            {
                lock (_channels) return _channels.Last(c => c.Path == collection.Path);
            }
            using var backend = Started(aerox, rival, dongle);
            Link(PathOf(aerox), PathOf(rival), PathOf(dongle));
            Light(0x123456);
            Passes(backend);
            // The Aerox 3 has its first zone, the dongle waits for an answer.
            Assert.Equal("out 00 21 01 12 34 56", Sent(Of(aerox)));
            Assert.Equal("out 00 61 01 00 12 34 56", Sent(Of(dongle)));

            backend.Stop();
            Assert.False(backend.WorkerAlive);
            Assert.Equal("out 00 21 01 12 34 56 | out 00 22 07", Sent(Of(aerox)));
            Assert.Equal("out 00 0A 00 0F 12 34 56 12 34 56 12 34 56 12 34 56", Sent(Of(rival)));
            // The dongle said nothing in the time Stop gave it, and never
            // said its mouse was off.
            Assert.Equal("out 00 61 01 00 12 34 56 | out 00 62 FF", Sent(Of(dongle)));
            lock (_channels) Assert.All(_channels, channel => Assert.True(channel.Disposed));
            Assert.Empty(backend.Recorded);
            Assert.Same(DirectBackendSnapshot.NotScanned, backend.Snapshot);

            // Started again: not scanned until a walk comes, then lit anew.
            int opens = Opens;
            backend.Start();
            Passes(backend);
            Assert.Same(DirectBackendSnapshot.NotScanned, backend.Snapshot);
            Assert.Equal(opens, Opens);
            backend.Rescan(new[] { aerox });
            Passes(backend);
            Assert.Equal(opens + 1, Opens);
            Assert.Equal("out 00 21 01 12 34 56", Sent(Of(aerox)));
        }

        /// <summary>A hook for a scripted channel that holds the worker
        /// inside the call it is making, from the moment a test asks for it
        /// until the test lets go. <see cref="Entered"/> tells the test the
        /// worker is in.</summary>
        private sealed class HeldWorker : IDisposable
        {
            private readonly ManualResetEventSlim _gate = new(false);
            public readonly ManualResetEventSlim Entered = new(false);
            private volatile bool _hold;

            public void HoldTheNextCall() => _hold = true;
            public void LetGo() => _gate.Set();

            public long Stamp(Func<long> tick)
            {
                if (_hold)
                {
                    Entered.Set();
                    _gate.Wait();
                }
                return tick();
            }

            public void Dispose() => _gate.Set();
        }

        /// <summary>The worker's last act sends nothing to a device
        /// SteelSeries GG has taken, as a pass would not: the worker is
        /// stopped before a pass of its own could let go of the unit.</summary>
        [Fact]
        public void TheStoppingWorker_SendsNothingToAUnitSteelSeriesGGHasTaken()
        {
            var mouse = Control(0x1870, 3);
            using var held = new HeldWorker();
            _script = channel => channel.Ticks = () => held.Stamp(Tick);
            using var backend = Started(mouse);
            Link(PathOf(mouse));
            Light(0x123456);
            Passes(backend);
            Advance(33);
            Passes(backend);

            // The worker is held inside its next write.
            held.HoldTheNextCall();
            Light(0xFF0000);
            Assert.True(held.Entered.Wait(30000), "the worker is inside a write");

            // GG starts, the path is withdrawn, and the engine stops.
            PeripheralOutputs.Presence = GGRuns;
            PeripheralOutputs.PublishLinks(LinkTable.Empty);
            backend.SignalStop();
            held.LetGo();
            Assert.True(SpinWait.SpinUntil(() => !backend.WorkerAlive, 30000), "the worker stopped");
            Assert.Equal("out 00 21 07 12 34 56 12 34 56 12 34 56 | out 00 21 07 FF 00 00 FF 00 00 FF 00 00", Sent(Channel()));
            Assert.True(Channel().Disposed);
            Assert.Empty(backend.Recorded);
        }

        private static Thread WorkerThread(SteelSeriesDirectBackend backend)
            => (Thread)typeof(SteelSeriesDirectBackend).GetField("_thread", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(backend);

        /// <summary>A worker that outlives a Stop that gave up on it, held
        /// inside its last hand-back, ends after the next Start's worker
        /// has scanned and painted the same mouse. It takes its own entry
        /// off the crash record, and the list and the new worker's entry
        /// stay.</summary>
        [Fact]
        public void AWorkerThatOutlivesItsStop_LeavesTheNextWorkersListAndRecord()
        {
            var mouse = Control(0x1870, 3);
            using var held = new HeldWorker();
            int opened = 0;
            // The first handle alone holds the worker that writes to it.
            _script = channel =>
            {
                if (Interlocked.Increment(ref opened) == 1) channel.Ticks = () => held.Stamp(Tick);
            };
            using var backend = new SteelSeriesDirectBackend(Open, path => _containerOf(path), Tick, (_, _, _) => true,
                1, 5, predecessorJoinMs: 1, stopJoinMs: 1);
            backend.Start();
            backend.Rescan(new[] { mouse });
            Passes(backend);
            Light(0x123456);
            Link(PathOf(mouse));
            Passes(backend);
            Assert.Equal(mouse.Path + " = 00 22 07", Recorded(backend));
            var first = WorkerThread(backend);

            // Stop gives up on the worker, held inside its hand-back.
            held.HoldTheNextCall();
            backend.Stop();
            Assert.True(held.Entered.Wait(30000), "the stopping worker is inside its hand-back");

            // The next worker does not wait for it either, and paints.
            backend.Start();
            backend.Rescan(new[] { mouse });
            Passes(backend);
            Assert.Equal("out 00 21 07 12 34 56 12 34 56 12 34 56", Sent(Channel(1)));
            var second = WorkerThread(backend);

            held.LetGo();
            Assert.True(first.Join(30000), "the first worker ended");
            Assert.Equal("out 00 21 07 12 34 56 12 34 56 12 34 56 | out 00 22 07", Sent(Channel(0)));
            Assert.Equal(mouse.Path, Assert.Single(backend.Snapshot.Units).Key);
            Assert.Equal(mouse.Path + " = 00 22 07", Recorded(backend));

            backend.Dispose();
            Assert.True(second.Join(30000), "the second worker ended");
        }

        /// <summary>The crash path stops the worker, so a worker a crash
        /// dialog keeps alive never paints again, and the worker's own last
        /// act hands its units back on its own channel. With the deadline
        /// already behind, nothing is waited for and no write of the crash
        /// path's own is started. A second call has nothing left to send.</summary>
        [Fact]
        public void TheCrashPath_StopsTheWorker_WhichHandsBackOnItsOwnChannel()
        {
            var aerox = Control(0x1836, 3, feature: 515);
            var rival = Control(0x184C, 3, output: 33, feature: 515, input: 33, tag: "b");
            FakeReportChannel Of(HidCollectionInfo collection)
            {
                lock (_channels) return _channels.Single(c => c.Path == collection.Path);
            }
            using var backend = Started(aerox, rival);
            Link(PathOf(aerox), PathOf(rival));
            Light(0x123456);
            Passes(backend);
            foreach (int _ in Enumerable.Range(0, 2))
            {
                Advance(16);
                Passes(backend);
            }
            Assert.Equal(3, Writes(Of(aerox)).Count);
            Assert.Equal(aerox.Path + " = 00 22 07", Recorded(backend));

            backend.ReleaseClaimedNow(Tick() - 1);
            Assert.Empty(_oneShots);
            Assert.True(SpinWait.SpinUntil(() => !backend.WorkerAlive, 30000), "the worker stopped");
            Assert.Equal("00 22 07", Writes(Of(aerox)).Last().Bytes);
            Assert.Equal(4, Writes(Of(aerox)).Count);
            Assert.True(Of(aerox).Disposed);
            // A family with no hand-back is written nothing.
            Assert.Single(Writes(Of(rival)));
            Assert.True(Of(rival).Disposed);
            Assert.Empty(backend.Recorded);

            // For good: no pass follows a change, and a second call with
            // time to spare has nothing left to send.
            int passes = backend.PassCount;
            Light(0xFF0000);
            Advance(SteelSeriesDirectProtocol.ReassertMs);
            backend.Wake();
            backend.ReleaseClaimedNow(Tick() + 750);
            Assert.Empty(_oneShots);
            Assert.Equal(passes, backend.PassCount);
            Assert.Equal(2, Opens);
        }

        /// <summary>A worker that does not stop in time, held inside a
        /// write, is not waited for past the join: each unit on record gets
        /// its hand-back through a one-shot write on a handle of its own,
        /// bounded by what is left of the deadline. A unit with no hand-back
        /// gets none, and a second call sends nothing more.</summary>
        [Fact]
        public void TheCrashPath_WritesThroughHandlesOfItsOwn_WhenTheWorkerIsHeld()
        {
            var first = Control(0x1836, 3, feature: 515);
            var second = Control(0x1870, 3, tag: "b");
            var rival = Control(0x184C, 3, output: 33, feature: 515, input: 33, tag: "c");
            using var held = new HeldWorker();
            _script = channel =>
            {
                if (channel.Path == rival.Path) channel.Ticks = () => held.Stamp(Tick);
            };
            using var backend = Started(first, second, rival);
            Light(0x123456);
            Link(PathOf(first), PathOf(second), PathOf(rival));
            Passes(backend);
            Advance(33);
            Passes(backend);
            Assert.Equal(first.Path + " = 00 22 07 | " + second.Path + " = 00 22 07", Recorded(backend));

            // The worker is held inside its next write to the Rival 3.
            held.HoldTheNextCall();
            Light(0xFF0000);
            Assert.True(held.Entered.Wait(30000), "the worker is inside a write");

            // 20 ms are left: the join gives up after them, and each
            // one-shot gets what is left of the deadline.
            long deadline = Tick() + 20;
            backend.ReleaseClaimedNow(deadline);
            Assert.Equal(new[] { (first.Path, "00 22 07", 20), (second.Path, "00 22 07", 20) }, _oneShots);
            Assert.Empty(backend.Recorded);
            backend.ReleaseClaimedNow(deadline);
            Assert.Equal(2, _oneShots.Count);

            // Let go, the held worker ends without another color.
            held.LetGo();
            Assert.True(SpinWait.SpinUntil(() => !backend.WorkerAlive, 30000), "the worker stopped");
            lock (_channels) Assert.All(_channels, channel => Assert.True(channel.Disposed));
        }

        /// <summary>The crash path starts no write once its deadline has
        /// passed: with two units on record and a first one-shot that uses
        /// up the time, the second unit gets none.</summary>
        [Fact]
        public void TheCrashPath_StartsNoOneShot_OnceTheDeadlineHasPassed()
        {
            var first = Control(0x1870, 3);
            var second = Control(0x1870, 3, tag: "b");
            using var held = new HeldWorker();
            _script = channel =>
            {
                if (channel.Path == second.Path) channel.Ticks = () => held.Stamp(Tick);
            };
            using var backend = Started(first, second);
            Light(0x123456);
            Link(PathOf(first), PathOf(second));
            Passes(backend);
            Advance(33);
            Passes(backend);
            Assert.Equal(2, backend.Recorded.Length);
            held.HoldTheNextCall();
            Light(0xFF0000);
            Assert.True(held.Entered.Wait(30000), "the worker is inside a write");

            _onOneShot = () => Advance(30);
            backend.ReleaseClaimedNow(Tick() + 20);
            Assert.Equal(new[] { (first.Path, "00 22 07", 20) }, _oneShots);

            held.LetGo();
            Assert.True(SpinWait.SpinUntil(() => !backend.WorkerAlive, 30000), "the worker stopped");
        }

        /// <summary>A stopped or disposed backend takes every call and does
        /// nothing: it keeps or drops what it is handed, opens nothing,
        /// starts no thread and throws nothing.</summary>
        [Fact]
        public void CallsAfterStop_AndAfterDispose_DoNothing()
        {
            var mouse = Control(0x1870, 3);
            var backend = Started(mouse);
            Link(PathOf(mouse));
            Light(0x123456);
            Passes(backend);
            Assert.Equal(1, Opens);

            backend.Stop();
            int passes = backend.PassCount;
            void EveryCall()
            {
                backend.Rescan(new[] { mouse });
                backend.Rescan(null);
                backend.SignalStop();
                backend.ReleaseClaimedNow(Tick() + 750);
                ((IDirectUnitBackend)backend).WaitForSilence(250);
                backend.Wake();
                backend.Stop();
                Assert.Same(DirectBackendSnapshot.NotScanned, backend.Snapshot);
                Assert.Equal(OutputFamily.SteelSeriesUnit, backend.Family);
            }
            EveryCall();
            Assert.False(backend.WorkerAlive);
            Assert.Equal(passes, backend.PassCount);
            Assert.Equal(1, Opens);
            Assert.Empty(_oneShots);

            backend.Dispose();
            EveryCall();
            backend.Start();
            backend.Dispose();
            Assert.False(backend.WorkerAlive);
            Assert.Equal(passes, backend.PassCount);
            Assert.Equal(1, Opens);
            Assert.Empty(_oneShots);

            // A backend that was never started takes them too.
            using var idle = Make();
            idle.Rescan(new[] { mouse });
            idle.SignalStop();
            idle.ReleaseClaimedNow(Tick() + 750);
            idle.Stop();
            Assert.Same(DirectBackendSnapshot.NotScanned, idle.Snapshot);
            Assert.Equal(1, Opens);
        }

        // ── The worker's waits ──

        /// <summary>The worker asks for the wait that ends when the next
        /// thing is due: a repaint, a gap, and its short tick while it
        /// looks for an answer. It never waits longer than its idle tick,
        /// so a change nobody announced is seen within it.</summary>
        [Fact]
        public void TheWorkerWaitsUntilTheNextThingIsDue_AndNeverLongerThanItsIdleTick()
        {
            var mouse = Control(0x1870, 3);
            var aerox = Control(0x1836, 3, feature: 515, tag: "b");
            var dongle = Control(0x1852, 3, feature: 513, tag: "c");
            using var backend = Make(activeTickMs: 3, idleTickMs: 100);
            backend.Start();
            backend.Rescan(new[] { mouse });
            Passes(backend);
            Assert.Equal(100, backend.WaitMs);

            // Painted: the re-assert is 5000 ms off, further than the idle
            // tick.
            Link(PathOf(mouse));
            Light(0x123456);
            Passes(backend);
            Assert.Equal(100, backend.WaitMs);

            // A new color 10 ms on has 23 ms of the fastest repaint left.
            Advance(10);
            Passes(backend);
            Light(0xFF0000);
            Passes(backend);
            Assert.Equal(23, backend.WaitMs);
            Advance(23);
            Passes(backend);
            Assert.Equal(2, Writes(Channel()).Count);
            Assert.Equal(100, backend.WaitMs);

            // In a gap of 16 ms between two zones.
            backend.Rescan(new[] { aerox });
            Passes(backend);
            Link(PathOf(aerox));
            Passes(backend);
            Assert.Equal(16, backend.WaitMs);
            Advance(10);
            Passes(backend);
            Assert.Equal(6, backend.WaitMs);

            // Waiting for a dongle's answer.
            backend.Rescan(new[] { dongle });
            Passes(backend);
            Link(PathOf(dongle));
            Passes(backend);
            Assert.Equal(3, backend.WaitMs);
        }

        /// <summary>A unit's gap counts from its own report, whatever a
        /// write to another unit took in the same pass: each unit is served
        /// on a clock reading of its own, and the wait the worker asks for
        /// counts from the end of the pass.</summary>
        [Fact]
        public void AUnitsGapCountsFromItsOwnReport_WhateverAWriteToAnotherUnitTook()
        {
            var keyboard = Control(0x1610, 1);
            var aerox = Control(0x1836, 3, feature: 515, tag: "b");
            SteelSeriesDirectBackend worker = null;
            // The wait each finished pass asked for, by the pass's number.
            // The worker reads its clock at the top of every pass, and by
            // then the pass before has left its wait behind.
            var waits = new ConcurrentDictionary<int, int>();
            long Clock()
            {
                var made = Volatile.Read(ref worker);
                if (made != null) waits[made.PassCount] = made.WaitMs;
                return Tick();
            }
            // The keyboard's first frame takes 25 ms.
            int slowPass = 0;
            _script = channel =>
            {
                if (channel.Path != keyboard.Path) return;
                channel.Ticks = () =>
                {
                    if (Interlocked.CompareExchange(ref slowPass, Volatile.Read(ref worker).PassCount + 1, 0) == 0) Advance(25);
                    return Tick();
                };
            };
            FakeReportChannel Of(HidCollectionInfo collection)
            {
                lock (_channels) return _channels.Single(c => c.Path == collection.Path);
            }
            using var backend = Make(activeTickMs: 1, idleTickMs: 100, ticks: Clock);
            Volatile.Write(ref worker, backend);
            backend.Start();
            backend.Rescan(new[] { keyboard, aerox });
            Passes(backend);
            long t0 = Tick();
            Light(0x123456);
            Link(PathOf(keyboard), PathOf(aerox));
            Passes(backend);
            // The slow pass may be the last of those two. One more pass
            // reads the clock at its top and so records the slow pass's
            // wait, and the clock stands still, so it sends nothing.
            Passes(backend, 1);

            // The mouse's first zone went out behind the keyboard's frame,
            // and its second is due 16 ms after that. The pass that took
            // 25 ms asked for those 16 ms, not for 41.
            Assert.Equal(new[] { ("out", "00 21 01 12 34 56", t0 + 25) }, Writes(Of(aerox)));
            Assert.Equal(16, waits[Volatile.Read(ref slowPass)]);
            Assert.Equal(16, backend.WaitMs);
            Advance(15);
            Passes(backend);
            Assert.Single(Writes(Of(aerox)));
            Advance(1);
            Passes(backend);
            Assert.Equal(("out", "00 21 02 00 00 00 12 34 56", t0 + 41), Writes(Of(aerox))[1]);
        }

        // ── The test host and real devices ──

        private static int Count(string text, string part) => text.Split(part).Length - 1;

        /// <summary>The worker has two ways to a device, its opener and the
        /// crash path's one-shot writer, and its own two are the shared
        /// helpers the test host refuses (HidDevicesOffLimits). Neither
        /// file of the lane holds a third: no handle is opened there, no
        /// report is sent there, no collection is walked there, nothing is
        /// asked of SteelSeries GG, and nothing lifts the refusal. The tests
        /// of the lane lift it nowhere either, and walk no real collection.</summary>
        [Fact]
        public void TheWorkerReachesADevice_OnlyThroughTheSharedHelpersTheTestHostRefuses()
        {
            Assert.True(RealHidAccess.Refused);
            using var backend = new SteelSeriesDirectBackend();
            const BindingFlags hidden = BindingFlags.NonPublic | BindingFlags.Instance;
            var open = (Func<string, IHidReportChannel>)typeof(SteelSeriesDirectBackend).GetField("_open", hidden).GetValue(backend);
            var writeOnce = (Func<string, byte[], int, bool>)typeof(SteelSeriesDirectBackend).GetField("_writeOnce", hidden).GetValue(backend);
            Assert.Equal(typeof(HidReportChannel).GetMethod(nameof(HidReportChannel.Open), new[] { typeof(string) }), open.Method);
            Assert.Equal(typeof(HidReportChannel).GetMethod(nameof(HidReportChannel.WriteOutputOnce)), writeOnce.Method);

            // Asked for a collection, here one that names no device, both
            // refuse.
            const string nowhere = @"\\?\hid#vid_1038&pid_1870&mi_03#padforge-test-no-such-collection#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            Assert.Null(open(nowhere));
            Assert.False(writeOnce(nowhere, new byte[] { 0x00, 0x22, 0x07 }, 200));

            string[] otherWays =
            {
                "CreateFile", "HidD_", "ReadFile", "WriteFile", "DeviceIoControl", "DllImport", "SafeFileHandle",
                "RawHidOutput", "VendorHidReader", "VendorHidRuntime", "HidCollectionRuntime", "OpenFeatureOnly",
                "SetFeatureOnce", "ForHandle", "GameSenseClient", "GameSenseBackend", "HttpClient", "Socket", "coreProps",
                "AllowForTest", "SetOffLimitsForTest",
            };
            foreach (string file in new[] { "SteelSeriesDirectBackend.cs", "SteelSeriesDirectProtocol.cs" })
            {
                string source = AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "Input", "Peripherals", file);
                foreach (string other in otherWays) Assert.DoesNotContain(other, source);
            }
            string worker = AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "Input", "Peripherals", "SteelSeriesDirectBackend.cs");
            Assert.Contains("_open = open ?? HidReportChannel.Open;", worker);
            Assert.Contains("_writeOnce = writeOnce ?? HidReportChannel.WriteOutputOnce;", worker);
            Assert.Equal(2, Count(worker, "HidReportChannel" + "."));
            Assert.Equal(0, Count(AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "Input", "Peripherals", "SteelSeriesDirectProtocol.cs"),
                "HidReportChannel" + "."));

            foreach (string file in new[] { "SteelSeriesDirectBackendTests.cs", "SteelSeriesDirectProtocolTests.cs" })
            {
                string tests = AuditDelta20261002Tests.RepoText("PadForge.Tests", file);
                Assert.DoesNotContain("RealHidAccess." + "AllowForTest", tests);
                Assert.DoesNotContain("HidCollectionRuntime." + "Enumerate", tests);
                Assert.DoesNotContain("VendorHidRuntime." + "Enumerate", tests);
            }
            // The one host these tests make is handed its walk.
            string own = AuditDelta20261002Tests.RepoText("PadForge.Tests", "SteelSeriesDirectBackendTests.cs");
            string made = "new PeripheralOutputHost" + "(";
            Assert.Equal(1, Count(own, made));
            int at = own.IndexOf(made, StringComparison.Ordinal);
            Assert.Contains("enumerate: () => new[] { control },", own.Substring(at, own.IndexOf(';', at) - at));
        }

        /// <summary>In the test host a worker left on its own opener and its
        /// own one-shot writer opens nothing and writes nothing: its unit is
        /// listed asleep, as one another program holds would be.</summary>
        [Fact]
        public void InTheTestHost_AWorkerOnItsOwnOpener_OpensNothingAndWritesNothing()
        {
            Assert.True(RealHidAccess.Refused);
            var mouse = Control(0x1870, 3, tag: "padforge-test-no-such-collection");
            using var backend = new SteelSeriesDirectBackend(null, _ => Box, Tick, null, 1, 5);
            backend.Start();
            backend.Rescan(new[] { mouse });
            Passes(backend);
            Link(PathOf(mouse));
            Light(0x123456);
            Passes(backend);
            Assert.True(Assert.Single(backend.Snapshot.Units).Asleep);
            Assert.Empty(backend.Recorded);

            backend.ReleaseClaimedNow(Tick() + 750);
            Assert.True(SpinWait.SpinUntil(() => !backend.WorkerAlive, 30000), "the worker stopped");
            // The bench's scripted opener was never asked either.
            Assert.Equal(0, Opens);
            Assert.Empty(_oneShots);
        }

        // ── Registration and the host ──

        /// <summary>The engine's host makes this backend through the one
        /// guarded line under the SteelSeries heading, and its worker runs
        /// on the thread the design names.</summary>
        [Fact]
        public void TheBackendIsRegisteredUnderTheSteelSeriesHeading_AndNamesItsThread()
        {
            string backends = AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "Input", "Peripherals", "DirectBackends.cs");
            Assert.Contains("            // SteelSeries.\r\n"
                            + "            Add(backends, OutputFamily.SteelSeriesUnit, () => new SteelSeriesDirectBackend());\r\n",
                backends);
            var made = DirectBackends.Create();
            try
            {
                var steelSeries = Assert.Single(made, b => b.Family == OutputFamily.SteelSeriesUnit);
                Assert.IsType<SteelSeriesDirectBackend>(steelSeries);
                // Made, and nothing started.
                Assert.Same(DirectBackendSnapshot.NotScanned, steelSeries.Snapshot);
                Assert.False(((SteelSeriesDirectBackend)steelSeries).WorkerAlive);
            }
            finally
            {
                foreach (var backend in made) backend.Dispose();
            }

            string source = AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "Input", "Peripherals", "SteelSeriesDirectBackend.cs");
            Assert.Contains("Name = \"PeripheralSteelSeries\"", source);
            // The vendor program is read from the published presence and
            // from nothing else, after the table.
            foreach (string other in new[] { "GetProcessesByName", "RunningProcesses", "VendorProgramWatch", "ReadInstalled" })
                Assert.DoesNotContain(other, source);
            int table = source.IndexOf("var links = PeripheralOutputs.Links;\r\n            if (links.ByPath.TryGetValue(path, out var devices))",
                StringComparison.Ordinal);
            int presence = source.IndexOf("return PeripheralOutputs.Presence.SteelSeriesGGRunning;", StringComparison.Ordinal);
            Assert.True(table > 0 && presence > table, "the table is read before the presence");
            Assert.Equal(1, source.Split("PeripheralOutputs.Presence").Length - 1);
        }

        /// <summary>Under the engine's own host, on one clock: the host's
        /// walk gives the worker its unit, a SteelSeries mouse row is
        /// matched to that unit by container and product, the claim paints
        /// it, SteelSeries GG starting takes the path away within a pass,
        /// after which nothing more is written, GG stopping gives it back,
        /// and the host's crash path reaches the worker.</summary>
        [Fact]
        public void UnderTheHost_ARowTakesItsUnit_AndGGStartingTakesItAway()
        {
            const string mousePath = @"\\?\hid#vid_1038&pid_1870&mi_00#a";
            var control = Control(0x1870, 3);
            var installed = new PeripheralPresence(false, false, false, true, false);
            var scripted = installed;
            var mouse = new UserDevice
            {
                InstanceGuid = Guid.NewGuid(), IsOnline = true, CapType = InputDeviceType.Mouse,
                VendorId = 0x1038, ProdId = 0x1870, DevicePath = mousePath,
            };
            lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(mouse);
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = mouse.InstanceGuid, MapTo = 2 });

            var backend = Make();
            using var host = new PeripheralOutputHost(
                new HidppBackend(() => Array.Empty<VendorHidCollection>(), (c, s) => null, _ => Guid.Empty, 1, 5),
                new GameSenseBackend(() => new GameSenseClient(HidppHapticsTests.MissingCoreProps(), 100), _ => 0, 20),
                () => new SensaHapticsService(30000, 16, 1000, () => 0f) { PlatformCanLoadEngine = () => false },
                containerOf: _ => Box,
                readPresence: () => Volatile.Read(ref scripted),
                direct: new IDirectUnitBackend[] { backend },
                enumerate: () => new[] { control },
                readRunning: current => PeripheralPresence.CopyRunning(current, Volatile.Read(ref scripted)),
                ticks: Tick);
            host.SlotHasController = slot => slot == 2;
            void HostPasses(int count = 2)
            {
                for (int i = 0; i < count; i++)
                {
                    int before = host.PassCount;
                    host.Nudge();
                    Assert.True(SpinWait.SpinUntil(() => host.PassCount > before, 30000), "a link pass ran");
                }
            }
            // The worker scans what the host's walk hands over, the host
            // links from what the worker found, and the worker paints from
            // the host's table: each takes passes of its own.
            void Settle()
            {
                for (int i = 0; i < 3; i++)
                {
                    HostPasses();
                    Passes(backend);
                }
            }

            host.Start();
            Settle();
            var unit = new OutputPath(OutputFamily.SteelSeriesUnit, control.Path);
            Assert.Equal(new[] { unit }, PeripheralOutputs.Links.For(mouse.InstanceGuid).Lighting);
            Assert.True(PeripheralOutputs.Links.For(mouse.InstanceGuid).HasVendorProgram);
            Assert.Equal(0, Opens);

            PeripheralOutputs.SetLighting(mouse.InstanceGuid, slot: 2, player: 1, 0x12, 0x34, 0x56);
            Settle();
            Assert.Equal("out 00 21 07 12 34 56 12 34 56 12 34 56", Sent(Channel(0)));

            // GG starts. The host sees it on its next pass, since a claim
            // holds a path GG would take, and withdraws the path.
            Volatile.Write(ref scripted, installed with { SteelSeriesGGRunning = true });
            Settle();
            Assert.Equal(new[] { new OutputPath(OutputFamily.GameSenseColor, "mouse") },
                PeripheralOutputs.Links.For(mouse.InstanceGuid).Lighting);
            Assert.True(PeripheralOutputs.Links.For(mouse.InstanceGuid).DirectWhenClosed);
            Assert.Equal("out 00 21 07 12 34 56 12 34 56 12 34 56", Sent(Channel(0)));
            Assert.True(Channel(0).Disposed);
            Assert.Empty(backend.Recorded);

            // GG stops. No claim holds a path GG would take now, so the
            // host reads the running programs on its slow interval. Then
            // the unit is the row's path again, and the claim that stood
            // all along paints it through a new handle.
            Volatile.Write(ref scripted, installed);
            Advance(PeripheralOutputHost.PresenceMs);
            Settle();
            Assert.Equal(new[] { unit }, PeripheralOutputs.Links.For(mouse.InstanceGuid).Lighting);
            Assert.Equal(2, Opens);
            Assert.Equal("out 00 21 07 12 34 56 12 34 56 12 34 56", Sent(Channel(1)));

            // The crash path reaches the worker through the host.
            host.ReleaseLightingNow();
            Assert.True(SpinWait.SpinUntil(() => !backend.WorkerAlive, 30000), "the worker stopped");
            Assert.Equal("out 00 21 07 12 34 56 12 34 56 12 34 56 | out 00 22 07", Sent(Channel(1)));
        }
    }
}
