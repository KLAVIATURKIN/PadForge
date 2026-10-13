using System;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PadForge.SteamWorkshop;
using PadForge.SteamWorkshop.Api;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>Keeps this test process off Steam's servers for its whole
    /// life. The suite has no live-network test, and a client is handed an
    /// HttpClient of the test's own where its requests are tested. With the
    /// flag set, a test, or a build of the library with one of its checks
    /// removed, that reaches Steam is answered as an unreachable server
    /// answers.</summary>
    internal static class SteamOffLimits
    {
#pragma warning disable CA2255 // Only the test host loads this assembly.
        [ModuleInitializer]
        internal static void Set() => SteamHttp.OffLimitsForTest = true;
#pragma warning restore CA2255
    }

    /// <summary>The flag is set here and nothing in the library sets it, and
    /// the two ways the library reaches Steam ask it: the shared HTTP client
    /// and the Steam client's connect. The source is read before a call is
    /// made, so the request behind a guard that was removed is never
    /// sent.</summary>
    public class SteamOffLimitsTests
    {
        private static string Root()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir.FullName;
        }

        private static string Read(string relative)
            => File.ReadAllText(Path.Combine(Root(), relative)).Replace("\r\n", "\n");

        private static ISteamWorkshopGate Enabled() => new DelegateSteamWorkshopGate(() => true);

        [Fact]
        public void TheFlag_IsSetHere_AndNothingInTheLibrarySetsIt()
        {
            Assert.True(SteamHttp.OffLimitsForTest);

            int read = 0;
            string library = Path.Combine(Root(), "PadForge.SteamWorkshop");
            foreach (string file in Directory.EnumerateFiles(library, "*.cs", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(library, file);
                if (relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;
                read++;
                Assert.Empty(Regex.Matches(File.ReadAllText(file), @"\bOffLimitsForTest\s*(=(?!=)|\|=|&=|\^=)"));
            }
            Assert.True(read > 20, "the library's sources were not found");
            Assert.Single(Regex.Matches(Read("PadForge.SteamWorkshop/Api/SteamHttp.cs"),
                @"^\s*internal static bool OffLimitsForTest;$", RegexOptions.Multiline));
        }

        /// <summary>The host named resolves on no network, so a request that
        /// got through would fail on the lookup, in other words than the
        /// refusal's.</summary>
        [Fact]
        public async Task ARequestToSteam_IsNotSent()
        {
            string source = Read("PadForge.SteamWorkshop/Api/SteamHttp.cs");
            Assert.Contains("private static HttpClient CreateClient()\n        {\n"
                + "            var http = OffLimitsForTest ? new HttpClient(new SendsNothing()) : new HttpClient();\n", source);
            Assert.Equal(2, Regex.Matches(source, @"new HttpClient\b").Count);
            Assert.Contains("=> throw Refused(request);", source);
            Assert.Contains("=> Task.FromException<HttpResponseMessage>(Refused(request));", source);
            Assert.True(SteamHttp.OffLimitsForTest);

            var downloader = new SteamUgcDownloader(Enabled());
            var refused = await Assert.ThrowsAsync<HttpRequestException>(
                () => downloader.DownloadVdfAsync("https://padforge-off-limits-check.invalid/config.vdf", 0));
            Assert.Equal("The test host sends no request to Steam: padforge-off-limits-check.invalid", refused.Message);
        }

        [Fact]
        public async Task TheSteamClient_ConnectsToNoServer()
        {
            string source = Read("PadForge.SteamWorkshop/Api/SteamWorkshopClient.cs");
            Assert.Contains("public async Task EnsureLoggedOnAsync(CancellationToken ct = default)\n        {\n"
                + "            if (SteamHttp.OffLimitsForTest)\n"
                + "                throw new SteamWorkshopException(\"The test host connects to no Steam server.\");\n", source);
            int member = source.IndexOf("public async Task EnsureLoggedOnAsync(CancellationToken ct = default)", StringComparison.Ordinal);
            int connect = source.IndexOf("_client.Connect();", StringComparison.Ordinal);
            Assert.True(member > 0 && connect > member);
            Assert.Equal(connect, source.LastIndexOf("_client.Connect();", StringComparison.Ordinal));
            Assert.True(SteamHttp.OffLimitsForTest);

            await using var client = new SteamWorkshopClient(Enabled());
            var refused = await Assert.ThrowsAsync<SteamWorkshopException>(() => client.EnsureLoggedOnAsync());
            Assert.Equal("The test host connects to no Steam server.", refused.Message);
        }
    }
}
