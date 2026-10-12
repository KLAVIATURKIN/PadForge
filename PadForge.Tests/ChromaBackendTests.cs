using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Razer Chroma worker's decisions (#494): which category shows
    /// which color, and what the session tells Synapse. The worker's loop
    /// only carries these decisions to the REST server.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public class ChromaBackendTests : IDisposable
    {
        private static readonly Guid Mouse = new("00000000-0000-0000-0000-0000000000a1");
        private static readonly Guid Keyboard = new("00000000-0000-0000-0000-0000000000b2");
        private static readonly Guid ChromaRow = new("00000000-0000-0000-0000-0000000000c3");

        public ChromaBackendTests() => PeripheralOutputs.ResetForTest();
        public void Dispose() => PeripheralOutputs.ResetForTest();

        private static void Link(params DeviceLinks[] rows) => PeripheralOutputs.PublishLinks(new LinkTable(rows));

        private static OutputPath Category(string name) => new(OutputFamily.ChromaCategory, name);

        private static List<KeyValuePair<string, int>> Wanted(Func<int, ISet<Guid>> devicesOnSlot = null,
            long? now = null, Func<int, int> slotNumber = null)
            => ChromaBackend.Wanted(PeripheralOutputs.Links, now ?? Environment.TickCount64,
                devicesOnSlot ?? (_ => new HashSet<Guid>()), slotNumber ?? (slot => slot + 1));

        [Fact]
        public void TheColorIntegerIsBgr_AndTheInitListsTheSessionsCategories()
        {
            Assert.Equal(0x0000FF, ChromaBackend.ToBgr(0xFF0000));
            Assert.Equal(0x00FF00, ChromaBackend.ToBgr(0x00FF00));
            Assert.Equal(0xFF0000, ChromaBackend.ToBgr(0x0000FF));
            Assert.Equal(0x563412, ChromaBackend.ToBgr(0x123456));

            using var doc = JsonDocument.Parse(ChromaBackend.InitBody(new[] { "mouse", "keypad" }));
            var root = doc.RootElement;
            Assert.Equal("PadForge", root.GetProperty("title").GetString());
            Assert.Equal("application", root.GetProperty("category").GetString());
            Assert.Equal(new[] { "mouse", "keypad" },
                root.GetProperty("device_supported").EnumerateArray().Select(e => e.GetString()));
        }

        [Fact]
        public void AnEffectIsAcceptedOnlyByItsResult()
        {
            Assert.True(ChromaBackend.TryReadResult("{\"result\":0}", out int ok));
            Assert.Equal(0, ok);
            Assert.True(ChromaBackend.TryReadResult("{\"result\":1167}", out int absent));
            Assert.Equal(ChromaBackend.ResultDeviceNotConnected, absent);
            Assert.False(ChromaBackend.TryReadResult("[]", out _));
            Assert.False(ChromaBackend.TryReadResult("not json", out _));
            Assert.False(ChromaBackend.TryReadResult("{\"result\":\"0\"}", out _));
        }

        /// <summary>Nothing claimed, nothing wanted, so no session holds
        /// Synapse's lighting. Each claimed category carries its own color,
        /// in the canonical order the init lists them.</summary>
        [Fact]
        public void OnlyClaimedCategoriesAreWanted_EachInItsOwnColor()
        {
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Category("mouse") } },
                 new DeviceLinks { Device = Keyboard, Lighting = new[] { Category("keyboard") } });
            Assert.Empty(Wanted());

            PeripheralOutputs.SetLighting(Mouse, slot: 0, player: 1, 0x11, 0x22, 0x33);
            Assert.Equal(new[] { new KeyValuePair<string, int>("mouse", 0x112233) }, Wanted());

            PeripheralOutputs.SetLighting(Keyboard, slot: 1, player: 2, 0x00, 0x00, 0xFF);
            Assert.Equal(new[]
            {
                new KeyValuePair<string, int>("keyboard", 0x0000FF),
                new KeyValuePair<string, int>("mouse", 0x112233),
            }, Wanted());

            PeripheralOutputs.ReleaseLighting(Mouse, 0);
            PeripheralOutputs.ReleaseLighting(Keyboard, 1);
            Assert.Empty(Wanted());
        }

        /// <summary>The Razer Chroma row fills every category no Razer device
        /// claims, and a device's own claim keeps its category whatever the
        /// player numbers.</summary>
        [Fact]
        public void TheChromaRowFillsTheRest_AndADeviceKeepsItsOwnCategory()
        {
            Link(new DeviceLinks
            {
                Device = ChromaRow,
                CatchAll = true,
                Lighting = PeripheralLinker.ChromaCategories.Select(Category).ToArray(),
            }, new DeviceLinks { Device = Mouse, Lighting = new[] { Category("mouse") } });

            PeripheralOutputs.SetLighting(ChromaRow, slot: 0, player: 1, 0xFF, 0x00, 0x00);
            PeripheralOutputs.SetLighting(Mouse, slot: 3, player: 4, 0x00, 0xFF, 0x00);
            var wanted = Wanted().ToDictionary(p => p.Key, p => p.Value);
            Assert.Equal(PeripheralLinker.ChromaCategories.Length, wanted.Count);
            Assert.Equal(0x00FF00, wanted["mouse"]);
            Assert.All(wanted.Where(p => p.Key != "mouse"), p => Assert.Equal(0xFF0000, p.Value));
        }

        /// <summary>Set Chroma Color (#468) paints the categories of the Razer
        /// devices assigned to its controller, whether or not their tabs take
        /// control, over any claim. Two controllers' macros on one category:
        /// the smaller displayed number rules, which here runs against the
        /// slot order. A slot with no Razer device of a category leaves that
        /// category alone. The assertions and the reads share one clock, so a
        /// stalled test thread never ages a macro out.</summary>
        [Fact]
        public void SetChromaColor_PaintsTheRazerDevicesOnItsController()
        {
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Category("mouse") } },
                 new DeviceLinks { Device = Keyboard, Lighting = new[] { Category("keyboard") } });
            var onSlot = new Dictionary<int, ISet<Guid>>
            {
                [2] = new HashSet<Guid> { Mouse },
                [5] = new HashSet<Guid> { Mouse, Keyboard },
            };
            ISet<Guid> DevicesOn(int slot) => onSlot.TryGetValue(slot, out var set) ? set : new HashSet<Guid>();
            // Slot 5 shows Virtual Controller 1 and slot 2 shows 2.
            int Shown(int slot) => slot switch { 5 => 1, 2 => 2, _ => slot + 10 };

            // No tab claims either device, and no macro is live.
            Assert.Empty(Wanted(DevicesOn, slotNumber: Shown));

            long now = Environment.TickCount64;
            PeripheralOutputs.AssertChromaMacro(2, 0x0A, 0x0B, 0x0C, now);
            var wanted = Wanted(DevicesOn, now, Shown).ToDictionary(p => p.Key, p => p.Value);
            Assert.Equal(0x0A0B0C, wanted["mouse"]);
            Assert.False(wanted.ContainsKey("keyboard"));

            // Slot 5 has the larger index and the smaller displayed number.
            PeripheralOutputs.AssertChromaMacro(5, 0x01, 0x02, 0x03, now);
            wanted = Wanted(DevicesOn, now, Shown).ToDictionary(p => p.Key, p => p.Value);
            Assert.Equal(0x010203, wanted["mouse"]);
            Assert.Equal(0x010203, wanted["keyboard"]);

            // A macro outranks a claim on its category.
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x77, 0x77, 0x77);
            Assert.Equal(0x010203, Wanted(DevicesOn, now, Shown).Single(p => p.Key == "keyboard").Value);

            // Past the window the claim shows again.
            long later = now + PeripheralOutputs.MacroAssertWindowMs + 50;
            Assert.Equal(0x777777, Wanted(DevicesOn, later, Shown).Single(p => p.Key == "keyboard").Value);

            // A macro on a slot without a Razer device paints nothing.
            PeripheralOutputs.ResetForTest();
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Category("mouse") } });
            PeripheralOutputs.AssertChromaMacro(7, 0x01, 0x02, 0x03, now);
            Assert.Empty(Wanted(DevicesOn, now, Shown));
        }

        /// <summary>The lapse the worker waits for is the first tick past a
        /// live macro's window, and nothing while none is live.</summary>
        [Fact]
        public void TheNextLapseIsTheFirstTickPastAMacrosWindow()
        {
            long now = 1_000_000;
            Assert.Equal(long.MaxValue, PeripheralOutputs.NextChromaMacroLapse(now));
            PeripheralOutputs.AssertChromaMacro(2, 1, 2, 3, now);
            PeripheralOutputs.AssertChromaMacro(4, 1, 2, 3, now + 30);
            long lapse = now + PeripheralOutputs.MacroAssertWindowMs + 1;
            Assert.Equal(lapse, PeripheralOutputs.NextChromaMacroLapse(now + 10));
            Assert.True(PeripheralOutputs.TryGetChromaMacro(2, lapse - 1, out _));
            Assert.False(PeripheralOutputs.TryGetChromaMacro(2, lapse, out _));
            Assert.Equal(lapse + 30, PeripheralOutputs.NextChromaMacroLapse(lapse));
            Assert.Equal(long.MaxValue, PeripheralOutputs.NextChromaMacroLapse(lapse + 30));
        }

        // ─────────────────────────────────────────────
        //  The worker against a local Chroma REST server
        // ─────────────────────────────────────────────

        /// <summary>A local stand-in for Synapse's REST server: the init
        /// answers with a session URI, each category PUT with the result its
        /// script gives, and every request is kept with its arrival tick.</summary>
        private sealed class FakeChromaServer : IDisposable
        {
            private readonly HttpListener _listener;
            private volatile bool _running = true;
            public readonly ConcurrentQueue<(long Tick, string Method, string Path)> Requests = new();
            /// <summary>Each category PUT and the result it was answered with.</summary>
            public readonly ConcurrentQueue<(string Category, int Result)> Puts = new();
            public string Endpoint { get; }
            public volatile int InitStatus = 200;
            public volatile int HeartbeatStatus = 200;
            public Func<string, int> PutResult = _ => 0;

            public FakeChromaServer()
            {
                var rng = new Random();
                for (int attempt = 0; ; attempt++)
                {
                    int port = 20000 + rng.Next(20000);
                    var l = new HttpListener();
                    l.Prefixes.Add($"http://127.0.0.1:{port}/");
                    try { l.Start(); }
                    catch (HttpListenerException) when (attempt < 10) { continue; }
                    _listener = l;
                    Endpoint = $"http://127.0.0.1:{port}";
                    break;
                }
                new Thread(Serve) { IsBackground = true }.Start();
            }

            private void Serve()
            {
                while (_running)
                {
                    HttpListenerContext ctx;
                    try { ctx = _listener.GetContext(); }
                    catch { break; }
                    using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) reader.ReadToEnd();
                    string path = ctx.Request.Url.AbsolutePath;
                    string method = ctx.Request.HttpMethod;
                    Requests.Enqueue((Environment.TickCount64, method, path));
                    int status = 200;
                    string body = "{\"result\":0}";
                    if (method == "POST" && path == "/razer/chromasdk")
                    {
                        status = InitStatus;
                        body = "{\"sessionid\":7,\"uri\":\"" + Endpoint + "/chromasdk\"}";
                    }
                    else if (method == "PUT" && path.StartsWith("/chromasdk/", StringComparison.Ordinal)
                             && path != "/chromasdk/heartbeat")
                    {
                        string category = path.Substring("/chromasdk/".Length);
                        int result = PutResult(category);
                        Puts.Enqueue((category, result));
                        body = "{\"result\":" + result + "}";
                    }
                    else if (method == "PUT" && path == "/chromasdk/heartbeat")
                    {
                        status = HeartbeatStatus;
                    }
                    try
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(body);
                        ctx.Response.StatusCode = status;
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = bytes.Length;
                        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                        ctx.Response.OutputStream.Close();
                    }
                    catch { }
                }
            }

            public int Count(string method, string path) => Requests.Count(r => r.Method == method && r.Path == path);

            public void Dispose()
            {
                _running = false;
                try { _listener.Stop(); } catch { }
                try { _listener.Close(); } catch { }
            }
        }

        private static ChromaBackend Backend(FakeChromaServer server, int retryMs = 30000, int pollMs = 20,
            Func<int, ISet<Guid>> devicesOnSlot = null, int heartbeatMs = 1000, Func<long> clock = null)
            => new(server.Endpoint, heartbeatMs, retryMs, pollMs, 2000, devicesOnSlot ?? (_ => new HashSet<Guid>()), slot => slot + 1,
                clock);

        private static int Lines(string text) => SdlDiagLog.Snapshot().Split('\n').Count(l => l.Contains(text));

        /// <summary>Two categories refused on every poll log one line each,
        /// not a pair per poll.</summary>
        [Fact]
        public void TwoRefusedCategories_LogOnceEach()
        {
            using var server = new FakeChromaServer { PutResult = _ => 87 };
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Category("mouse") } },
                 new DeviceLinks { Device = Keyboard, Lighting = new[] { Category("keyboard") } });
            int keyboard = Lines("PERIPHERAL Chroma effect rejected: keyboard"), mouse = Lines("PERIPHERAL Chroma effect rejected: mouse");
            using var backend = Backend(server);
            backend.Start();
            PeripheralOutputs.SetLighting(Mouse, slot: 0, player: 1, 1, 2, 3);
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 4, 5, 6);

            Assert.True(SpinWait.SpinUntil(() => server.Count("PUT", "/chromasdk/mouse") >= 6, 5000), "the refused color was retried");
            Assert.Equal(keyboard + 1, Lines("PERIPHERAL Chroma effect rejected: keyboard"));
            Assert.Equal(mouse + 1, Lines("PERIPHERAL Chroma effect rejected: mouse"));
        }

        /// <summary>A category refused again after a color was accepted there
        /// logs again.</summary>
        [Fact]
        public void ARefusalAfterAnAcceptedColor_IsLoggedAgain()
        {
            int refuse = 1;
            using var server = new FakeChromaServer { PutResult = _ => Volatile.Read(ref refuse) != 0 ? 87 : 0 };
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Category("mouse") } });
            const string line = "PERIPHERAL Chroma effect rejected: mouse";
            int lines = Lines(line);
            using var backend = Backend(server);
            backend.Start();
            PeripheralOutputs.SetLighting(Mouse, slot: 0, player: 1, 1, 2, 3);
            Assert.True(SpinWait.SpinUntil(() => server.Count("PUT", "/chromasdk/mouse") >= 3, 5000), "the refused color was retried");
            Assert.Equal(lines + 1, Lines(line));

            Volatile.Write(ref refuse, 0);
            Assert.True(SpinWait.SpinUntil(() => server.Puts.Any(p => p.Category == "mouse" && p.Result == 0), 3000),
                "the color was accepted");
            Volatile.Write(ref refuse, 1);
            PeripheralOutputs.SetLighting(Mouse, slot: 0, player: 1, 7, 8, 9);
            Assert.True(SpinWait.SpinUntil(() => Lines(line) == lines + 2, 3000), "the refusal after the accepted color was logged");
        }

        /// <summary>A category refused again in a new session logs again.</summary>
        [Fact]
        public void ARefusalInANewSession_IsLoggedAgain()
        {
            using var server = new FakeChromaServer { PutResult = _ => 87 };
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Category("mouse") } });
            const string line = "PERIPHERAL Chroma effect rejected: mouse";
            int lines = Lines(line);
            using var backend = Backend(server);
            backend.Start();
            PeripheralOutputs.SetLighting(Mouse, slot: 0, player: 1, 1, 2, 3);
            Assert.True(SpinWait.SpinUntil(() => server.Count("PUT", "/chromasdk/mouse") >= 3, 5000), "the refused color was retried");
            Assert.Equal(lines + 1, Lines(line));

            // The claim goes and its session ends. The next claim opens a
            // new one.
            PeripheralOutputs.ReleaseLighting(Mouse, 0);
            Assert.True(SpinWait.SpinUntil(() => server.Count("DELETE", "/chromasdk") >= 1, 3000), "the session ended");
            PeripheralOutputs.SetLighting(Mouse, slot: 0, player: 1, 1, 2, 3);
            Assert.True(SpinWait.SpinUntil(() => Lines(line) == lines + 2, 3000), "the refusal in the new session was logged");
        }

        /// <summary>A Synapse that keeps refusing the session logs the wait
        /// once, not at every retry.</summary>
        [Fact]
        public void ARefusedSession_LogsTheWaitOnce()
        {
            using var server = new FakeChromaServer { InitStatus = 500 };
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Category("mouse") } });
            const string line = "PERIPHERAL Chroma waiting for Synapse";
            int lines = Lines(line);
            using var backend = Backend(server, retryMs: 100);
            backend.Start();
            PeripheralOutputs.SetLighting(Mouse, slot: 0, player: 1, 1, 2, 3);

            Assert.True(SpinWait.SpinUntil(() => server.Count("POST", "/razer/chromasdk") >= 4, 5000), "the session was retried");
            Assert.Equal(lines + 1, Lines(line));
        }

        /// <summary>The wait is logged again once the worker has moved on:
        /// after a spell with nothing claimed, and after a session that
        /// opened was lost.</summary>
        [Fact]
        public void ARefusedSession_LogsTheWaitAgain_AfterTheWorkerMovedOn()
        {
            using var server = new FakeChromaServer { InitStatus = 500 };
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Category("mouse") } });
            const string line = "PERIPHERAL Chroma waiting for Synapse";
            int lines = Lines(line);
            using var backend = Backend(server, retryMs: 100, heartbeatMs: 50);
            backend.Start();
            PeripheralOutputs.SetLighting(Mouse, slot: 0, player: 1, 1, 2, 3);
            Assert.True(SpinWait.SpinUntil(() => server.Count("POST", "/razer/chromasdk") >= 2, 5000), "the session was retried");
            Assert.Equal(lines + 1, Lines(line));

            // Nothing claimed, then claimed again: a new wait.
            PeripheralOutputs.ReleaseLighting(Mouse, 0);
            Assert.True(SpinWait.SpinUntil(
                () => PeripheralOutputs.StateOf(OutputFamily.ChromaCategory) == BackendState.Idle, 3000), "the worker went idle");
            PeripheralOutputs.SetLighting(Mouse, slot: 0, player: 1, 1, 2, 3);
            Assert.True(SpinWait.SpinUntil(() => Lines(line) == lines + 2, 3000), "the wait after the idle spell was logged");

            // A session opens, is lost, and the next one is refused: a new
            // wait.
            server.InitStatus = 200;
            Assert.True(SpinWait.SpinUntil(
                () => PeripheralOutputs.StateOf(OutputFamily.ChromaCategory) == BackendState.Connected, 3000), "the session opened");
            server.InitStatus = 500;
            server.HeartbeatStatus = 500;
            Assert.True(SpinWait.SpinUntil(() => Lines(line) == lines + 3, 3000), "the wait after the lost session was logged");
        }

        /// <summary>Set Chroma Color lets go within its window of the action
        /// ending: the worker's wait ends at the lapse, so the session ends
        /// then, not at the next poll. The worker reads this test's clock and
        /// its poll is ten minutes, so the test reads the wait the worker
        /// chose and times nothing, and a session that ends here ended on
        /// the lapse. A bound on the time the release took failed whenever
        /// the worker was held off.</summary>
        [Fact]
        public void SetChromaColor_LetsGoAtItsLapse_NotAtTheNextPoll()
        {
            using var server = new FakeChromaServer();
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Category("mouse") } });
            long now = 1_000_000;
            using var backend = Backend(server, retryMs: 0, pollMs: 600_000, devicesOnSlot: slot => slot == 0
                ? new HashSet<Guid> { Mouse } : new HashSet<Guid>(), clock: () => Volatile.Read(ref now));
            backend.Start();

            PeripheralOutputs.AssertChromaMacro(0, 0x10, 0x20, 0x30, now);
            Assert.True(SpinWait.SpinUntil(() => server.Count("PUT", "/chromasdk/mouse") >= 1, 5000), "the macro color was painted");
            // With the macro live the worker waits for its lapse, the first
            // tick past the window, and with the window's last tick left it
            // waits that tick.
            Assert.Equal(PeripheralOutputs.MacroAssertWindowMs + 1,
                WorkerCadence.WaitAfterAFullPass(() => backend.PassCount, () => backend.WaitMs));
            Volatile.Write(ref now, now + PeripheralOutputs.MacroAssertWindowMs);
            Assert.Equal(1, WorkerCadence.WaitAfterAFullPass(() => backend.PassCount, () => backend.WaitMs));
            Assert.Equal(0, server.Count("DELETE", "/chromasdk"));

            Volatile.Write(ref now, now + 1);
            Assert.True(SpinWait.SpinUntil(() => server.Count("DELETE", "/chromasdk") >= 1, 5000), "the session ended at the lapse");
        }

        /// <summary>A macro that lapses while its color is on the way to
        /// Synapse is let go on the next tick. The worker took the lapse from
        /// the tick the send ended at, found no macro live by then, and held
        /// the color for a whole poll.</summary>
        [Fact]
        public void SetChromaColor_ThatLapsesWhileItsColorIsSent_IsLetGoOnTheNextTick()
        {
            long now = 1_000_000;
            long lapse = now + PeripheralOutputs.MacroAssertWindowMs + 1;
            // The action ends while Synapse takes the color.
            using var server = new FakeChromaServer { PutResult = _ => { Volatile.Write(ref now, lapse); return 0; } };
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Category("mouse") } });
            using var backend = Backend(server, retryMs: 0, pollMs: 600_000, devicesOnSlot: slot => slot == 0
                ? new HashSet<Guid> { Mouse } : new HashSet<Guid>(), clock: () => Volatile.Read(ref now));
            backend.Start();
            // The worker is in its idle wait before the macro starts, so the
            // macro's wake ends that wait and none is left over to end the
            // wait after the color.
            Assert.True(SpinWait.SpinUntil(() => backend.WaitMs == 600_000, 5000), "the worker went idle");

            PeripheralOutputs.AssertChromaMacro(0, 0x10, 0x20, 0x30, now);
            Assert.True(SpinWait.SpinUntil(() => server.Count("DELETE", "/chromasdk") >= 1, 5000),
                "the session ended without a poll passing");
            Assert.True(server.Count("PUT", "/chromasdk/mouse") >= 1, "the macro color was painted first");
        }
    }
}
