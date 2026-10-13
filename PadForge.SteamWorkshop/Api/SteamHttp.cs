using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PadForge.SteamWorkshop.Api
{
    /// <summary>
    /// Shared HTTP plumbing for the anonymous Steam REST clients: one long-lived
    /// <see cref="HttpClient"/> (avoids socket exhaustion) with a 15 s timeout and a
    /// <c>PadForge/{version}</c> User-Agent, plus the JSON options the DTOs deserialize with.
    /// </summary>
    internal static class SteamHttp
    {
        /// <summary>Set by a test assembly for the life of its process,
        /// never by the app. False in production, where every request goes
        /// out as it did. With it set, the shared client sends nothing and
        /// the Steam client connects to no server (SteamOffLimits in
        /// PadForge.Tests and in PadForge.SteamWorkshop.Tests). A test hands
        /// a client an HttpClient of its own. A test, or a build of the
        /// library with one of its checks removed, that reaches Steam is
        /// answered as an unreachable server answers.</summary>
        internal static bool OffLimitsForTest;

        private static readonly Lazy<HttpClient> LazyClient = new Lazy<HttpClient>(CreateClient);

        public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        public static HttpClient Client => LazyClient.Value;

        private static HttpClient CreateClient()
        {
            var http = OffLimitsForTest ? new HttpClient(new SendsNothing()) : new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            var version = typeof(SteamHttp).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PadForge", version));
            return http;
        }

        /// <summary>Fails every request before it is sent, with the error of
        /// a request that reached no server.</summary>
        private sealed class SendsNothing : HttpMessageHandler
        {
            protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
                => throw Refused(request);

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromException<HttpResponseMessage>(Refused(request));

            private static HttpRequestException Refused(HttpRequestMessage request)
                => new HttpRequestException("The test host sends no request to Steam: " + (request?.RequestUri?.Host ?? "no address"));
        }
    }
}
