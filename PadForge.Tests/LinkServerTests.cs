using System;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Tasks;
using PadForge.Engine;
using PadForge.Engine.RemoteLink;

namespace PadForge.Tests
{
    /// <summary>Runs with suite parallelism disabled (round six): these
    /// three drive REAL loopback sockets with wall-clock deadlines, and
    /// under the full suite's parallel load they intermittently failed
    /// as a trio (twice on 2026-07-25, green in isolation six of six)
    /// while every other run was clean. Deadline blowout and
    /// ephemeral-port churn are both load-shaped; taking the class out
    /// of the parallel pool removes the load from the window instead of
    /// guessing at a longer deadline.</summary>
    [CollectionDefinition("RemoteLinkSockets", DisableParallelization = true)]
    public sealed class RemoteLinkSocketsCollection { }

    [Collection("RemoteLinkSockets")]
    public partial class LinkServerTests
    {
        /// <summary>An ephemeral port number free on BOTH protocols for a
        /// server on 127.0.0.1, the one address these tests bind and dial
        /// (<see cref="StartOnFreePort"/>).
        ///
        /// <para>Round nine captured half the mechanism: the probe tested TCP
        /// only, so candidates were unverified on UDP. Round twelve captured
        /// the rest: candidates came from a TCP port-0 bind, and Windows
        /// assigns ephemeral TCP ports SEQUENTIALLY, while this machine
        /// reserves a contiguous 900-port UDP exclusion block inside the
        /// dynamic range (netsh excludedportrange: 63832-64732). Whenever the
        /// rotor sat inside the block, forty CONSECUTIVE candidates were all
        /// UDP-dead and the trio failed as a burst, healing minutes later
        /// when the rotor walked out. Random candidates make probe failures
        /// independent, so an exclusion band can only ever eat single
        /// probes, never the loop. The residual TOCTOU gap between the
        /// probe's release and the caller's real bind is what the caller's
        /// retry loop is for.</para>
        ///
        /// <para>A hammer on 2026-10-11 captured the last blind spot. The
        /// servers bound every address then, as the app's do, and so did the
        /// probe, which never tried 127.0.0.1 by itself. Windows lets a bind
        /// of every address stand beside another socket's bind of one
        /// address on the same port, and that socket then takes everything
        /// sent to its address. Three services on this machine hold a UDP
        /// port on 127.0.0.1 inside the range, so about one server start in
        /// 5,500 landed on one, shook hands over TCP and heard no datagram,
        /// and the first assignment query timed out after its five seconds.
        /// A listener on 127.0.0.1 takes the dialed connection the same
        /// way.</para>
        ///
        /// <para>The servers bind 127.0.0.1 alone now, and so does the probe
        /// (<see cref="LoopbackIsFree"/>). The rule above then runs the
        /// other way: a server here would take what this PC sends to a port
        /// another program holds on every address. So a port any socket
        /// holds on any address is passed over as well
        /// (<see cref="HeldOnAnyAddress"/>).</para></summary>
        /// <param name="candidate">The port numbers to try, for the tests of
        /// the probe. Random ones in the dynamic range by default.</param>
        private static int FreePort(Func<int> candidate = null)
        {
            for (int probe = 0; probe < 40; probe++)
            {
                int p = candidate?.Invoke() ?? Random.Shared.Next(49152, 65536);
                if (!HeldOnAnyAddress(p) && LoopbackIsFree(p)) return p;
            }
            throw new InvalidOperationException(
                "no ephemeral port was free on both TCP and UDP after 40 probes");
        }

        /// <summary>True when the system's tables show a TCP listener or a
        /// UDP socket on this port, on any address of either family. The
        /// tables are read: finding out by a bind would take a bind of
        /// every address, which the test host never makes.</summary>
        private static bool HeldOnAnyAddress(int port)
        {
            var net = IPGlobalProperties.GetIPGlobalProperties();
            return net.GetActiveTcpListeners().Any(held => held.Port == port)
                || net.GetActiveUdpListeners().Any(held => held.Port == port);
        }

        /// <summary>True when 127.0.0.1 binds on this port on TCP and on
        /// UDP: a bind of that one address fails while another socket has
        /// it, and inside a port range Windows keeps back.</summary>
        private static bool LoopbackIsFree(int port)
        {
            Socket tcp = null, udp = null;
            try
            {
                tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                tcp.Bind(new IPEndPoint(IPAddress.Loopback, port));
                udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                udp.Bind(new IPEndPoint(IPAddress.Loopback, port));
                return true;
            }
            catch (SocketException) { return false; }
            finally
            {
                try { udp?.Close(); } catch { }
                try { tcp?.Close(); } catch { }
            }
        }

        /// <summary>Starts the server on 127.0.0.1 alone. The app's start
        /// binds every address, and a listener there makes Windows Firewall
        /// ask about the test host once for every folder the tests run from.
        /// These tests dial 127.0.0.1 and nothing else.
        ///
        /// <para>Probe-then-bind keeps a residual TOCTOU gap: the OS can
        /// hand the probed port to another socket between the release and
        /// LinkServer's own bind. Retry on a fresh doubly-probed port
        /// until the bind lands. Twenty-five attempts because the observed
        /// failures came in bursts under heavy machine load, where a
        /// handful of consecutive losses is ordinary. A socket handed
        /// 127.0.0.1 on the port inside that gap fails the server's bind,
        /// so a server that started holds the address it is dialed
        /// on.</para></summary>
        /// <param name="candidate">The ports to start on, for the test of
        /// the retry. <see cref="FreePort"/> by default.</param>
        private static int StartOnFreePort(LinkServer s, int avoid = -1, Func<int> candidate = null)
        {
            for (int attempt = 0; attempt < 25; attempt++)
            {
                int p = candidate?.Invoke() ?? FreePort();
                if (p == avoid) continue;
                if (!s.Start(p, IPAddress.Loopback)) continue;
                // Every server these tests start is read back here.
                var (tcp, udp) = Bound(s);
                Assert.Equal(new IPEndPoint(IPAddress.Loopback, p), tcp);
                Assert.Equal(new IPEndPoint(IPAddress.Loopback, p), udp);
                return p;
            }
            throw new InvalidOperationException("no ephemeral port would bind after 25 attempts");
        }

        /// <summary>The addresses a started server's two sockets hold.</summary>
        private static (IPEndPoint Tcp, IPEndPoint Udp) Bound(LinkServer s)
        {
            const BindingFlags field = BindingFlags.NonPublic | BindingFlags.Instance;
            var tcp = (TcpListener)typeof(LinkServer).GetField("_tcp", field).GetValue(s);
            var udp = (Socket)typeof(LinkServer).GetField("_udp", field).GetValue(s);
            return ((IPEndPoint)tcp.LocalEndpoint, (IPEndPoint)udp.LocalEndPoint);
        }

        private static async Task<bool> WaitUntil(Func<bool> cond, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (cond()) return true;
                await Task.Delay(20);
            }
            return cond();
        }

        private static RemotePeerDeviceInfo PadInfo() => new()
        {
            PeerLocalDeviceId = "pad0", Name = "A Pad",
            VendorId = 0x054C, ProductId = 0x0CE6,
            NumAxes = 6, NumButtons = 17, NumHats = 1,
            HasRumble = true, InputDeviceType = InputDeviceType.Gamepad,
        };

        [Fact]
        public async Task LocalhostLoopback_PairsAndStreamsInputOverRealSockets()
        {
            Func<PendingPairing, PairingApproval> approve = _ => true;
            using var host = new LinkServer(PeerIdentity.Generate(), new PeerTrustStore(), approve);     // consumer
            using var holder = new LinkServer(PeerIdentity.Generate(), new PeerTrustStore(), approve);   // device holder

            RemotePeerDevice received = null;
            host.DeviceConnected += d => received = d;

            int pA = StartOnFreePort(host);
            StartOnFreePort(holder, avoid: pA);

            bool connected = await holder.ConnectAsync("127.0.0.1", pA, new[] { PadInfo() });
            Assert.True(connected);

            Assert.True(await WaitUntil(() => received != null, 5000), "host never saw the remote device");
            Assert.StartsWith("A Pad", received.Name);
            Assert.StartsWith("peer://", received.DevicePath);

            // The holder streams its device's input over real UDP (a few frames —
            // a heartbeat would cover a drop; here loopback is lossless).
            var state = CustomInputStateCodec.CreateNeutral();
            state.Buttons[2] = true;
            state.Axis[0] = 4000;
            var caps = new CustomInputStateCodec.Caps(false, false);
            for (int i = 0; i < 5; i++)
            {
                holder.PushLocalFrame(0, state, caps, (ulong)(i + 1));
                await Task.Delay(10);
            }

            Assert.True(await WaitUntil(() => received.GetCurrentState()?.Buttons[2] == true, 5000),
                $"input never arrived. holder sent={holder.DiagDatagramsSent} holderErr={holder.DiagLastError} | host recv={host.DiagDatagramsReceived} opened={host.DiagDatagramsOpened} hostErr={host.DiagLastError}");
            var s = received.GetCurrentState();
            Assert.True(s.Buttons[2]);
            Assert.Equal(4000, s.Axis[0]);
        }

        [Fact]
        public async Task UnknownPeerRejected_NoDeviceReachesPipeline()
        {
            // Host rejects the pairing; holder approves. No device must appear on the host.
            using var host = new LinkServer(PeerIdentity.Generate(), new PeerTrustStore(), approve: _ => false);
            using var holder = new LinkServer(PeerIdentity.Generate(), new PeerTrustStore(), approve: _ => true);

            bool deviceAppeared = false;
            host.DeviceConnected += _ => deviceAppeared = true;

            int pA = StartOnFreePort(host);
            StartOnFreePort(holder, avoid: pA);

            await holder.ConnectAsync("127.0.0.1", pA, new[] { PadInfo() });
            await Task.Delay(300); // give any (erroneous) registration a chance to fire

            Assert.False(deviceAppeared);
        }

        [Fact]
        public void StartStop_IsClean()
        {
            var s = new LinkServer(PeerIdentity.Generate(), new PeerTrustStore(), _ => false);
            StartOnFreePort(s);
            Assert.True(s.IsRunning);
            s.Stop();
            Assert.False(s.IsRunning);
            s.Dispose();
        }

        /// <summary>A port another socket holds on 127.0.0.1 is no port for
        /// a server here, on either protocol: the probe passes it over, and
        /// a server offered one fails its bind and starts on the next. The
        /// third socket is bound and does not listen, so the system's
        /// tables do not list it and only the probe's own bind finds
        /// it.</summary>
        [Fact]
        public void APortAnotherSocketHoldsOnLoopback_IsNoServersPort()
        {
            using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            int heldUdp = ((IPEndPoint)udp.LocalEndPoint).Port;
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var (silent, heldSilent) = HoldFreshPort(AddressFamily.InterNetwork, SocketType.Stream, listen: false);
            try
            {
                int heldTcp = ((IPEndPoint)listener.LocalEndpoint).Port;
                Assert.False(LoopbackIsFree(heldUdp));
                Assert.False(LoopbackIsFree(heldTcp));
                Assert.False(LoopbackIsFree(heldSilent));
                Assert.True(HeldOnAnyAddress(heldUdp));
                Assert.True(HeldOnAnyAddress(heldTcp));

                var offered = new Queue<int>(new[] { heldUdp, heldTcp, heldSilent });
                int probed = FreePort(() => offered.Count > 0 ? offered.Dequeue() : Random.Shared.Next(49152, 65536));
                Assert.Empty(offered);
                Assert.DoesNotContain(probed, new[] { heldUdp, heldTcp, heldSilent });

                using var server = new LinkServer(PeerIdentity.Generate(), new PeerTrustStore(), _ => false);
                offered = new Queue<int>(new[] { heldUdp, heldTcp, heldSilent });
                int port = StartOnFreePort(server, candidate: () => offered.Count > 0 ? offered.Dequeue() : FreePort());
                Assert.Empty(offered);
                Assert.True(server.IsRunning);
                Assert.DoesNotContain(port, new[] { heldUdp, heldTcp, heldSilent });
            }
            finally
            {
                listener.Stop();
                silent.Dispose();
            }
        }

        /// <summary>A port held on another address only is passed over too,
        /// though 127.0.0.1 binds on it: the server would take what this PC
        /// sends to that port. ::1 stands in for the other address, since
        /// the test host binds nothing beyond loopback.</summary>
        [Fact]
        public void APortHeldOnAnotherAddressOnly_IsPassedOverByTheProbe()
        {
            if (!Socket.OSSupportsIPv6) return;
            var (udp, heldUdp) = HoldFreshPort(AddressFamily.InterNetworkV6, SocketType.Dgram, listen: false);
            var (tcp, heldTcp) = HoldFreshPort(AddressFamily.InterNetworkV6, SocketType.Stream, listen: true);
            try
            {
                Assert.True(LoopbackIsFree(heldUdp));
                Assert.True(LoopbackIsFree(heldTcp));
                Assert.True(HeldOnAnyAddress(heldUdp));
                Assert.True(HeldOnAnyAddress(heldTcp));
                // The read of this process's own sockets finds IPv6 rows too.
                Assert.Contains(new IPEndPoint(IPAddress.IPv6Loopback, heldUdp), TestHostListeners.Udp());
                Assert.Contains(new IPEndPoint(IPAddress.IPv6Loopback, heldTcp), TestHostListeners.Tcp());

                var offered = new Queue<int>(new[] { heldUdp, heldTcp });
                int probed = FreePort(() => offered.Count > 0 ? offered.Dequeue() : Random.Shared.Next(49152, 65536));
                Assert.Empty(offered);
                Assert.DoesNotContain(probed, new[] { heldUdp, heldTcp });
            }
            finally
            {
                udp.Dispose();
                tcp.Dispose();
            }
        }

        /// <summary>A socket on its family's loopback address, on a port
        /// nothing held on any address before it. So the check under test
        /// is the only one that can find the port taken.</summary>
        private static (Socket Socket, int Port) HoldFreshPort(AddressFamily family, SocketType type, bool listen)
        {
            bool v6 = family == AddressFamily.InterNetworkV6;
            for (int attempt = 0; attempt < 40; attempt++)
            {
                int port = Random.Shared.Next(49152, 65536);
                if (HeldOnAnyAddress(port) || !LoopbackIsFree(port)) continue;
                var socket = new Socket(family, type, type == SocketType.Stream ? ProtocolType.Tcp : ProtocolType.Udp);
                try
                {
                    socket.Bind(new IPEndPoint(v6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, port));
                    if (listen) socket.Listen();
                    return (socket, port);
                }
                catch (SocketException) { socket.Dispose(); }
            }
            throw new InvalidOperationException("no port was free on every address after 40 tries");
        }

        /// <summary>The test host holds no listener beyond loopback while
        /// two servers are up and paired. Read from the system's own tables,
        /// which is where a listener on another address shows whichever
        /// class opened it. Both servers appear in the same read, on
        /// 127.0.0.1, so an empty answer cannot be a read that saw
        /// nothing.</summary>
        [Fact]
        public async Task TheTestHostHoldsNoListenerBeyondLoopback()
        {
            using var host = new LinkServer(PeerIdentity.Generate(), new PeerTrustStore(), _ => true);
            using var holder = new LinkServer(PeerIdentity.Generate(), new PeerTrustStore(), _ => true);
            var status = TraceStatus(("host", host), ("holder", holder));
            int hostPort = StartOnFreePort(host);
            int holderPort = StartOnFreePort(holder, avoid: hostPort);
            Assert.True(await holder.ConnectAsync("127.0.0.1", hostPort, new[] { PadInfo() }), Why(holder, status));

            var tcp = TestHostListeners.Tcp();
            var udp = TestHostListeners.Udp();
            foreach (int port in new[] { hostPort, holderPort })
            {
                Assert.Contains(new IPEndPoint(IPAddress.Loopback, port), tcp);
                Assert.Contains(new IPEndPoint(IPAddress.Loopback, port), udp);
            }
            var beyond = tcp.Where(held => !IPAddress.IsLoopback(held.Address)).Select(held => "TCP " + held)
                .Concat(udp.Where(held => !IPAddress.IsLoopback(held.Address)).Select(held => "UDP " + held)).ToList();
            Assert.True(beyond.Count == 0, "the test host listens beyond loopback: " + string.Join(", ", beyond));
        }
    }
}
