using System.IO;
using System.Text.RegularExpressions;
using Jint;
using Xunit.Abstractions;

namespace PadForge.Tests;

public sealed class TouchpadConnectionLifetimeTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("close", "https:")]
    [InlineData("error", "http:")]
    public void RetiredCallbacksCannotReplaceAHealthyConnection(string callback, string protocol)
    {
        using var page = new TouchpadPage(protocol);
        page.Run("advance(3000); emit(1, 'open'); touch('touchstart', 50);");
        Assert.Equal(1, page.Number("sockets[1].sent.length"));
        Assert.Equal("touchpad", page.Text("sockets[1].sent[0].type"));
        Assert.StartsWith(protocol == "https:" ? "wss://" : "ws://", page.Text("sockets[1].url"));
        page.Run($"emit(0, '{callback}'); advance(500); touch('touchmove', 80);");
        output.WriteLine(page.Trace());
        Assert.Equal(2, page.Number("sockets.length"));
        Assert.Equal(0, page.Number("sockets[1].closeCalls"));
        Assert.Equal(2, page.Number("sockets[1].sent.length"));
        Assert.Equal(.8, page.Number("sockets[1].sent[1].x"), 3);
        Assert.Equal("Connected", page.Text("nodes.status.textContent"));
    }

    [Fact]
    public void RetiredCloseCannotCancelTheCurrentConnectionTimeout()
    {
        using var page = new TouchpadPage();
        page.Run("advance(3000); emit(0, 'close'); advance(3000);");
        output.WriteLine(page.Trace());
        Assert.Equal(3, page.Number("sockets.length"));
        Assert.True(page.Number("sockets[1].closeCalls") > 0);
        page.Run("emit(2, 'open'); touch('touchstart', 50);");
        Assert.Equal(1, page.Number("sockets[2].sent.length"));
    }

    [Fact]
    public void OpeningAConnectionCancelsItsTimeout()
    {
        using var page = new TouchpadPage();
        page.Run("emit(0, 'open'); touch('touchstart', 50); advance(10000); touch('touchmove', 80);");
        Assert.Equal(1, page.Number("sockets.length"));
        Assert.Equal(0, page.Number("sockets[0].closeCalls"));
        Assert.Equal(2, page.Number("sockets[0].sent.length"));
        Assert.Equal(0, page.Number("Object.keys(timers).length"));
    }

    [Fact]
    public void RepeatedRetirementSchedulesOnlyOneRetry()
    {
        using var page = new TouchpadPage();
        page.Run("emit(0, 'open'); touch('touchstart', 50);");
        Assert.Equal(1, page.Number("sockets[0].sent.length"));
        page.Run("emit(0, 'close'); emit(0, 'close'); advance(500);");
        output.WriteLine(page.Trace());
        Assert.Equal(2, page.Number("sockets.length"));
        Assert.Equal(0, page.Number("sockets[1].closeCalls"));
        page.Run("emit(1, 'open'); touch('touchmove', 80);");
        Assert.Equal(1, page.Number("sockets[1].sent.length"));
    }

    [Fact]
    public void AStaleOpenCannotAnnounceTheCurrentAttemptAsConnected()
    {
        using var page = new TouchpadPage();
        page.Run("advance(3000); emit(0, 'open'); var staleAnnounced = nodes.status.textContent === 'Connected'; emit(1, 'open');");
        Assert.Equal("Connected", page.Text("nodes.status.textContent"));
        Assert.False(page.Boolean("staleAnnounced"));
        Assert.True(page.Number("sockets[0].closeCalls") > 0);
    }

    [Theory]
    [InlineData("controller_client.js")]
    [InlineData("custom_client.js")]
    public void SiblingConnectGuardsKeepAHealthySocket(string script)
    {
        using var page = new TouchpadPage(peerScript: script);
        page.Run("emit(0, 'open'); connect(); connect();");
        Assert.Equal(1, page.Number("sockets.length"));
        Assert.Equal(0, page.Number("sockets[0].closeCalls"));
        Assert.Equal("caps", page.Text("sockets[0].sent[0].type"));
        page.Run("emit(0, 'close'); connect(); emit(1, 'open');");
        Assert.Equal(2, page.Number("sockets.length"));
        Assert.Equal("caps", page.Text("sockets[1].sent[0].type"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedConnectKeepsTheCurrentAttempt(bool open)
    {
        using var page = new TouchpadPage();
        if (open) page.Run("emit(0, 'open');");
        page.Run("requestConnect(); requestConnect(); var attempts=sockets.length; emit(sockets.length-1, 'open'); touch('touchstart', 50);");
        Assert.Equal(1, page.Number("sockets[sockets.length-1].sent.length"));
        Assert.Equal(1, page.Number("attempts"));
        Assert.Equal(0, page.Number("sockets[0].closeCalls"));
    }
}

internal sealed class TouchpadPage : IDisposable
{
    readonly Jint.Engine engine;
    internal TouchpadPage(string protocol = "https:", string peerScript = null)
    {
        engine = new Jint.Engine(options =>
        {
            options.TimeoutInterval(TimeSpan.FromSeconds(5));
            options.MaxStatements(100_000);
        });
        engine.Execute(Browser);
        engine.SetValue("protocol", protocol).Execute("location.protocol = protocol;");
        if (peerScript == null)
        {
            string html = File.ReadAllText(AuditDelta20260823Tests.FindRepoFile(Path.Combine("PadForge.App", "WebAssets", "touchpad.html")));
            var scripts = Regex.Matches(html, @"<script>\s*(.*?)</script>", RegexOptions.Singleline);
            Assert.Single(scripts.Cast<Match>());
            string script = scripts[0].Groups[1].Value;
            int startCall = script.LastIndexOf("        connect();", StringComparison.Ordinal);
            Assert.True(startCall >= 0);
            // Expose the private entry point without changing its body or startup call.
            script = script.Insert(startCall, "globalThis.requestConnect = connect;\n");
            engine.Execute(script, "touchpad.html");
        }
        else
        {
            string source = File.ReadAllText(AuditDelta20260823Tests.FindRepoFile(Path.Combine("PadForge.App", "WebAssets", "js", peerScript)));
            int start = source.IndexOf("    function connect() {", StringComparison.Ordinal);
            Assert.True(start >= 0);
            int end = source.IndexOf("    function scheduleReconnect()", start, StringComparison.Ordinal);
            Assert.True(end > start);
            engine.Execute("""
                var ws=null, clientId='test-client', layoutType='xbox360', layout={id:'saved',name:'Saved',overlays:[]};
                var vibrate=null, navigator={}, resyncFns=[], reconnects=0;
                var console={log:()=>{},error:()=>{}};
                function setStatus(text) { element('status').textContent=text; }
                function scheduleReconnect() { reconnects++; }
                function send(value) { if (ws && ws.readyState===WebSocket.OPEN) ws.send(JSON.stringify(value)); }
                """);
            engine.Execute(source.Substring(start, end - start), peerScript);
            engine.Execute("connect();");
        }
        Assert.Equal(1, Number("sockets.length"));
    }
    internal void Run(string script) => engine.Execute(script);
    internal double Number(string expression) => engine.Evaluate(expression).AsNumber();
    internal string Text(string expression) => engine.Evaluate(expression).AsString();
    internal bool Boolean(string expression) => engine.Evaluate(expression).AsBoolean();
    internal string Trace() => Text("JSON.stringify({status:nodes.status.textContent, sockets:sockets.map(s=>({state:s.readyState, closes:s.closeCalls, sent:s.sent.length})), timers:Object.keys(timers).length})");
    public void Dispose() => engine.Dispose();

    const string Browser = """
        var nodes = {}, timers = {}, timerId = 0, clock = 0, sockets = [];
        function element(id) {
            if (!nodes[id]) {
                var classes = {};
                nodes[id] = {
                    style: {}, listeners: {}, textContent: '', className: '',
                    classList: {add: c=>classes[c]=true, remove: c=>delete classes[c], contains: c=>!!classes[c]},
                    addEventListener: function(name, fn) { this.listeners[name] = fn; },
                    getBoundingClientRect: ()=>({left:0, top:0, width:100, height:100})
                };
            }
            return nodes[id];
        }
        var document = {getElementById:element};
        var performance = {getEntriesByType:()=>[]};
        var storage = {};
        var sessionStorage = {getItem:k=>storage[k] || null, setItem:(k,v)=>storage[k]=v, removeItem:k=>delete storage[k]};
        var crypto = {randomUUID:()=> 'test-client'};
        var location = {host:'padforge.test', protocol:'https:', reload:()=>{throw new Error('Unexpected reload');}};
        function setTimeout(fn, delay) { timers[++timerId] = {fn:fn, at:clock+delay}; return timerId; }
        function clearTimeout(id) { delete timers[id]; }
        function advance(delay) {
            var until = clock + delay, count = 0;
            while (true) {
                var ids = Object.keys(timers).sort((a,b)=>timers[a].at-timers[b].at || a-b);
                if (!ids.length || timers[ids[0]].at > until) break;
                if (++count > 100) throw new Error('Timer loop did not settle');
                var next = timers[ids[0]]; delete timers[ids[0]];
                clock = next.at; next.fn();
            }
            clock = until;
        }
        function WebSocket(url) { this.url=url; this.readyState=0; this.closeCalls=0; this.sent=[]; sockets.push(this); }
        WebSocket.CONNECTING=0; WebSocket.OPEN=1; WebSocket.CLOSING=2; WebSocket.CLOSED=3;
        WebSocket.prototype.close = function() { this.closeCalls++; if (this.readyState < 2) this.readyState=2; };
        WebSocket.prototype.send = function(text) {
            if (this.readyState !== 1) throw new Error('Send on a closed socket');
            this.sent.push(JSON.parse(text));
        };
        function emit(index, kind) {
            var socket = sockets[index];
            if (kind === 'open') socket.readyState=1;
            if (kind === 'close' || kind === 'error') socket.readyState=3;
            if (socket['on'+kind]) socket['on'+kind]({code:1006});
        }
        function touch(kind, x) {
            nodes.touchpad.listeners[kind]({preventDefault:()=>{}, changedTouches:[{identifier:7, clientX:x, clientY:50}]});
        }
        """;
}
