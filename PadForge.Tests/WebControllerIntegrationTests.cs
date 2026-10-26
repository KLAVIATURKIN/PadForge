using System.Collections;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Resources;
using System.Text;
using System.Text.Json;
using PadForge.Services;

namespace PadForge.Tests;

public sealed class WebControllerIntegrationTests
{
    [Theory]
    [InlineData("http://192.168.1.20:8080", "http://192.168.1.20:8080/integration.html")]
    [InlineData("http://192.168.1.20:8080/?code=ABCDEFGHJK", "http://192.168.1.20:8080/integration.html?code=ABCDEFGHJK")]
    [InlineData("https://padforge.local:9123", "https://padforge.local:9123/integration.html")]
    [InlineData(null, null)]
    public void IntegrationQrUsesTheSameEndpointAndAccessCode(string main, string integration)
        => Assert.Equal(integration, WebControllerServer.BuildIntegrationUrl(main));

    [Theory]
    [InlineData("  Chat pad  ", "Chat pad")]
    [InlineData("\n\r", "Default")]
    [InlineData(null, "Default")]
    [InlineData("Pad\nName", "PadName")]
    public void DeviceNamesAreTrimmedAndControlCharactersRemoved(string requested, string expected)
        => Assert.Equal(expected, WebControllerServer.ResolveClientName(requested, "Default"));

    [Fact]
    public void DeviceNamesHaveABoundedLength()
        => Assert.Equal(80, WebControllerServer.ResolveClientName(new string('a', 1000), "Default").Length);

    [Fact]
    public async Task IntegrationPagesAndModulesAreEmbeddedAndServedWithBrowserMimeTypes()
    {
        using var run = new ServerRun();
        using var http = new HttpClient();
        foreach (var path in new[] { "integration.html", "js/integration/setup.mjs", "js/integration/runtime.mjs",
                     "js/integration/core.mjs", "js/integration/view.mjs", "js/integration/input/CommandParser.mjs",
                     "js/integration/input/CommandExecutor.mjs", "js/integration/command/ClaimControl.mjs", "js/comfy.min.js" })
        {
            using var response = await http.GetAsync(run.Url + path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(path.EndsWith(".html") ? "text/html" : "application/javascript", response.Content.Headers.ContentType.MediaType);
            Assert.NotEmpty(await response.Content.ReadAsStringAsync());
        }
        string index = await http.GetStringAsync(run.Url);
        Assert.Contains("Choose a controller layout", index);
        Assert.Contains("/integration.html", index);
        string controller = await http.GetStringAsync(run.Url + "controller.html");
        Assert.Contains("/js/controller_client.js", controller);
        Assert.Contains("/js/integration/runtime.mjs", controller);
    }

    [Theory]
    [InlineData("dualsense")]
    [InlineData("xbox360")]
    [InlineData("xboxseries")]
    public async Task WlgFinishesUseTheSuppliedImagesAndKeepCurrentInputDefinitions(string type)
    {
        using var run = new ServerRun(loadWlg: true);
        using var http = new HttpClient();
        using var catalog = JsonDocument.Parse(await http.GetStringAsync(run.Url + "api/layouts"));
        var entry = catalog.RootElement.EnumerateArray().Single(l => l.GetProperty("type").GetString() == type);
        Assert.Contains("wlg", entry.GetProperty("finishes").EnumerateArray().Select(f => f.GetString()));
        using var json = JsonDocument.Parse(await http.GetStringAsync(run.Url + $"api/layout?type={type}&finish=wlg"));
        var layout = json.RootElement;
        Assert.EndsWith("_wlg.png", layout.GetProperty("basePath").GetString());
        using var image = await http.GetAsync(run.Url + "img/" + layout.GetProperty("basePath").GetString());
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/png", image.Content.Headers.ContentType.MediaType);
        Assert.Contains(layout.GetProperty("overlays").EnumerateArray(), overlay =>
            overlay.TryGetProperty("inputKind", out var kind) && kind.GetString() == "button");
    }

    [Fact]
    public async Task CustomNameReachesTheRegisteredPadForgeDevice()
    {
        using var run = new ServerRun();
        string name = null;
        run.Server.DeviceConnected += device => name = device.Name;
        using var socket = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(new Uri(run.Url.Replace("http:", "ws:") + "ws?layout=xbox360&id=integration-test&name=Chat%20Pad"), timeout.Token);
        var bytes = new byte[4096];
        var received = await socket.ReceiveAsync(bytes, timeout.Token);
        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(bytes, 0, received.Count));
        Assert.Equal("connected", json.RootElement.GetProperty("type").GetString());
        Assert.Equal("Xbox 360 Web Controller: Chat Pad", json.RootElement.GetProperty("name").GetString());
        Assert.Equal("Xbox 360 Web Controller: Chat Pad", name);
        socket.Abort();
    }

    private sealed class ServerRun : IDisposable
    {
        public WebControllerServer Server { get; }
        public string Url { get; }

        public ServerRun(bool loadWlg = false)
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Server = new WebControllerServer(_ => null, () => new Listener());
            if (loadWlg)
            {
                var cache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                using var resources = typeof(WebControllerServer).Assembly.GetManifestResourceStream("PadForge.g.resources");
                using var reader = new ResourceReader(resources);
                foreach (DictionaryEntry entry in reader)
                {
                    string key = (string)entry.Key;
                    if (!key.EndsWith("_wlg.png")) continue;
                    using var memory = new MemoryStream();
                    ((Stream)entry.Value).CopyTo(memory);
                    cache[key] = memory.ToArray();
                }
                typeof(WebControllerServer).GetField("_imageCache", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(Server, cache);
            }
            Assert.True(Server.Start(port));
            Url = $"http://127.0.0.1:{port}/";
        }
        public void Dispose() => Server.Dispose();
    }

    private sealed class Listener : IWebControllerListener
    {
        private readonly HttpListener _listener = new();
        public bool IsListening => _listener.IsListening;
        public void Start(string prefix) { _listener.Prefixes.Add(prefix.Replace("://+:", "://127.0.0.1:")); _listener.Start(); }
        public void AddPrefix(string prefix) => throw new InvalidOperationException("The web controller uses one endpoint.");
        public HttpListenerContext GetContext() => _listener.GetContext();
        public void Stop() => _listener.Stop();
        public void Close() => _listener.Close();
    }
}

