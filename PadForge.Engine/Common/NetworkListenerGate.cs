using System;
using System.Net;
using System.Net.Sockets;

namespace PadForge.Engine.Common
{
    /// <summary>
    /// The gate in front of every socket and listener the app and the engine
    /// bind: Remote Link's server, discovery and DHT sockets, the head
    /// tracker's UDP port, the telemetry ports and the web controller's HTTP
    /// listener.
    ///
    /// <para>In the app the gate is always open, and passing it is one read
    /// of a flag. It exists for the test process. Windows Firewall asks about
    /// a program the first time it listens beyond loopback, once for every
    /// path it runs from, and a listener on every address is one any device
    /// on the network can reach. The tests bind 127.0.0.1 where a class
    /// takes a bind address and build none of the classes that take none
    /// (TestHostListenerTests). That rule reads the tests. This gate holds
    /// when the code under test changes, as in a build with one of its
    /// checks removed: the test assembly closes it for the life of its
    /// process (ListenerOffLimits in PadForge.Tests), and a bind beyond
    /// loopback then fails as a bind the system refuses does.</para>
    /// </summary>
    public static class NetworkListenerGate
    {
        /// <summary>Set by the test assembly for the life of its process,
        /// never by the app. False in production, where every bind is as it
        /// was.</summary>
        internal static bool OffLimitsForTest;

        /// <summary>Passes in the app. In the test host it throws for an
        /// address that is not loopback, with the socket error of a refused
        /// bind (WSAEACCES).</summary>
        public static void Enter(IPAddress address)
        {
            if (OffLimitsForTest && (address == null || !IPAddress.IsLoopback(address)))
                throw new SocketException((int)SocketError.AccessDenied);
        }

        /// <summary>The same for an HTTP listener prefix. In the test host
        /// it throws for a prefix whose host is not 127.0.0.1, [::1] or
        /// localhost, with the error http.sys gives a prefix it refuses
        /// (5, access denied).</summary>
        public static void EnterPrefix(string prefix)
        {
            if (OffLimitsForTest && !IsLoopbackPrefix(prefix))
                throw new HttpListenerException(5, "The test host listens on no address beyond loopback: " + prefix);
        }

        /// <summary>Whether an HTTP listener prefix names a loopback host. A
        /// prefix on + or * is every address.</summary>
        internal static bool IsLoopbackPrefix(string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return false;
            int scheme = prefix.IndexOf("://", StringComparison.Ordinal);
            if (scheme < 0) return false;
            string rest = prefix.Substring(scheme + 3);
            int end = rest.StartsWith("[", StringComparison.Ordinal) ? rest.IndexOf(']') + 1 : rest.IndexOfAny(new[] { ':', '/' });
            string host = end > 0 ? rest.Substring(0, end) : rest;
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
            return IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
        }
    }
}
