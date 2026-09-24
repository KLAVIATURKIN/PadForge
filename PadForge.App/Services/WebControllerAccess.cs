using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace PadForge.Services
{
    /// <summary>
    /// The access code that guards the web controller's optional plain-HTTP
    /// address. That address exists for browsers that refuse PadForge's
    /// self-signed certificate and for a tunnel or reverse proxy that brings
    /// its own, so it can be reachable from far outside the LAN. The web
    /// controller has no other admission step, so without a code anyone who
    /// found the address could drive a virtual controller.
    ///
    /// <para>The code rides the address as <c>?code=</c> and is traded for a
    /// cookie on the first request, because every page, script, image, API
    /// call and WebSocket handshake after that uses an absolute path with no
    /// room for it. The main address never asks for a code.</para>
    /// </summary>
    internal static class WebControllerAccess
    {
        /// <summary>Crockford's base-32 alphabet: digits and letters with I,
        /// L, O and U left out, so a code read aloud or typed from a stream
        /// overlay has no look-alike characters.</summary>
        internal const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        /// <summary>Ten characters of 32 each: 50 bits.</summary>
        internal const int CodeLength = 10;

        internal const string CookieName = "PadForgeWebCode";
        internal const string QueryName = "code";

        internal enum Decision
        {
            Allow,
            /// <summary>The query carried the right code: allow, and set the
            /// cookie so the page's later requests pass.</summary>
            AllowAndSetCookie,
            DenyMissingCode,
            DenyNotLocal,
        }

        /// <summary>A fresh code from the cryptographic generator.</summary>
        internal static string Generate()
        {
            var chars = new char[CodeLength];
            for (int i = 0; i < chars.Length; i++)
                chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            return new string(chars);
        }

        /// <summary>True for a code this module could have generated, after
        /// normalization. A stored value that fails this is replaced.</summary>
        internal static bool IsValid(string code)
        {
            string n = Normalize(code);
            if (n == null || n.Length != CodeLength) return false;
            foreach (char c in n)
                if (Alphabet.IndexOf(c) < 0) return false;
            return true;
        }

        /// <summary>Upper case, trimmed, or null when empty.</summary>
        internal static string Normalize(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;
            return code.Trim().ToUpperInvariant();
        }

        /// <summary>Compares in constant time over the expected length, so the
        /// response time says nothing about how much of a guess was right.</summary>
        internal static bool Matches(string offered, string expected)
        {
            string e = Normalize(expected);
            string o = Normalize(offered);
            if (e == null || o == null) return false;
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(o), Encoding.UTF8.GetBytes(e));
        }

        /// <summary>True for a loopback peer, IPv4-mapped IPv6 included.</summary>
        internal static bool IsLoopback(IPAddress address)
        {
            if (address == null) return false;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            return IPAddress.IsLoopback(address);
        }

        /// <summary>The ruling for one request that arrived on the plain
        /// address. The main address never reaches this.</summary>
        internal static Decision Evaluate(string expectedCode, bool localOnly, IPAddress remote,
            string queryCode, string cookieHeader)
        {
            if (localOnly && !IsLoopback(remote)) return Decision.DenyNotLocal;
            if (Matches(ReadCookie(cookieHeader), expectedCode)) return Decision.Allow;
            if (Matches(queryCode, expectedCode)) return Decision.AllowAndSetCookie;
            return Decision.DenyMissingCode;
        }

        /// <summary>The value of <see cref="CookieName"/> in a raw Cookie
        /// header, or null. Parsed by hand: the header is a plain
        /// <c>name=value; name=value</c> list, and HttpListener's own cookie
        /// collection rejects some values browsers send.</summary>
        internal static string ReadCookie(string cookieHeader)
        {
            if (string.IsNullOrEmpty(cookieHeader)) return null;
            foreach (string part in cookieHeader.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                if (part.Substring(0, eq).Trim() == CookieName)
                    return part.Substring(eq + 1).Trim();
            }
            return null;
        }

        /// <summary>The Set-Cookie value that carries the code: HttpOnly so
        /// page script cannot read it, SameSite=Strict so no other site's page
        /// can make the browser send it, and no expiry, so it ends with the
        /// browser session and the address with its code opens a new one.</summary>
        internal static string SetCookieValue(string code)
            => $"{CookieName}={Normalize(code)}; Path=/; HttpOnly; SameSite=Strict";

        private static readonly byte[] StoreEntropy = Encoding.UTF8.GetBytes("PadForge.WebControllerAccessCode.v1");

        /// <summary>The code encrypted for PadForge.xml, or null. Machine
        /// scope, the same protection Remote Link's Secure mode gives its
        /// private key: settings files get attached to bug reports, and a
        /// copy read off this machine must not carry a working code.</summary>
        internal static string ProtectForStorage(string code)
        {
            if (!IsValid(code)) return null;
            try
            {
                return Convert.ToBase64String(ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(Normalize(code)), StoreEntropy, DataProtectionScope.LocalMachine));
            }
            catch (CryptographicException) { return null; }
        }

        /// <summary>The code back from <see cref="ProtectForStorage"/>, or
        /// null when the value is missing, damaged, or was written on another
        /// machine. The caller then keeps a freshly generated code.</summary>
        internal static string UnprotectFromStorage(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return null;
            try
            {
                string code = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                    Convert.FromBase64String(stored), StoreEntropy, DataProtectionScope.LocalMachine));
                return IsValid(code) ? Normalize(code) : null;
            }
            catch (FormatException) { return null; }
            catch (CryptographicException) { return null; }
        }
    }
}
