using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Engine.Common
{
    /// <summary>
    /// The gate in front of what the app and the engine send beyond this PC:
    /// Remote Link's calls to a peer, to the relays and to the STUN servers,
    /// the names it looks up for them and for the DHT, and the pictures the
    /// workshop browser fetches. The Steam Workshop library keeps a flag of
    /// its own (SteamHttp).
    ///
    /// <para>In the app the gate is always open, and passing it is one read
    /// of a flag. The test assembly closes it for the life of its process
    /// (OutboundOffLimits in PadForge.Tests). A test talks to a server of
    /// its own on 127.0.0.1, and those calls pass. A test, or a build of the
    /// code with one of its checks removed, that reaches a server on the
    /// network is answered as an unreachable one answers, and nothing leaves
    /// the PC.</para>
    /// </summary>
    public static class OutboundNetworkGate
    {
        /// <summary>Set by the test assembly for the life of its process,
        /// never by the app. False in production, where every call goes out
        /// as it did.</summary>
        internal static bool OffLimitsForTest;

        /// <summary>True in the test host for an address that is not
        /// loopback.</summary>
        public static bool Refuses(IPAddress address)
            => OffLimitsForTest && (address == null || !IPAddress.IsLoopback(address));

        /// <summary>True in the test host for a host that is not loopback
        /// by its own text: localhost, or a loopback address written out.
        /// Any other name is looked up first, and the lookup is itself a
        /// request to a server.</summary>
        public static bool RefusesHost(string host) => OffLimitsForTest && !IsLoopbackHost(host);

        /// <summary>A client for requests that leave this PC. A plain
        /// HttpClient in the app. In the test host, one whose requests
        /// beyond loopback fail before they are sent.</summary>
        public static HttpClient NewHttpClient()
            => OffLimitsForTest ? new HttpClient(new LoopbackOnly()) : new HttpClient();

        /// <summary>Whether a host is this PC by its own text.</summary>
        internal static bool IsLoopbackHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
            return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
        }

        /// <summary>Fails a request to any host but this PC, with the error
        /// of a request that reached no server.</summary>
        private sealed class LoopbackOnly : DelegatingHandler
        {
            internal LoopbackOnly() : base(new SocketsHttpHandler()) { }

            protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                ThrowIfBeyondLoopback(request);
                return base.Send(request, cancellationToken);
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                ThrowIfBeyondLoopback(request);
                return base.SendAsync(request, cancellationToken);
            }

            private static void ThrowIfBeyondLoopback(HttpRequestMessage request)
            {
                Uri uri = request?.RequestUri;
                if (uri != null && uri.IsAbsoluteUri && IsLoopbackHost(uri.IdnHost)) return;
                throw new HttpRequestException("The test host sends no request beyond this PC: " + (uri?.Host ?? "no address"));
            }
        }
    }
}
