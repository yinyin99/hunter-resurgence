using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Play;

namespace Resurgence.Tests.PlayKit;

public class ForwarderTests
{
    /// <summary>What the community server (a local test server) saw of one request.</summary>
    sealed record Seen(string Method, string Raw, Dictionary<string, string> Headers, byte[] Body);

    /// <summary>A local server that records each request and answers 207 with a fixed reply.</summary>
    sealed class Origin : IAsyncDisposable
    {
        readonly WebApplication app;
        public List<Seen> Log { get; }
        public Uri Url { get; }

        Origin(WebApplication app, List<Seen> log) => (this.app, Log, Url) = (app, log, new Uri(Forwarder.Addresses(app)[0]));

        public static async Task<Origin> Start()
        {
            var log = new List<Seen>();
            var b = WebApplication.CreateSlimBuilder();
            b.Logging.ClearProviders();
            b.WebHost.ConfigureKestrel(k =>
            {
                k.RequestHeaderEncodingSelector = _ => Encoding.Latin1;
                k.ResponseHeaderEncodingSelector = _ => Encoding.Latin1;
                k.Limits.MaxRequestBodySize = null;
                k.Listen(IPAddress.Loopback, 0);
            });
            var app = b.Build();
            app.Run(async ctx =>
            {
                using var ms = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(ms);
                lock (log)
                    log.Add(new(ctx.Request.Method, ctx.Features.Get<IHttpRequestFeature>()!.RawTarget,
                        ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase), ms.ToArray()));
                ctx.Response.StatusCode = 207;
                ctx.Response.Headers["X-Reply"] = "r\u00E9ponse";
                ctx.Response.ContentType = "application/octet-stream";
                if (ctx.Request.Method != "HEAD") await ctx.Response.Body.WriteAsync(Reply);
            });
            await app.StartAsync();
            return new Origin(app, log);
        }

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    static readonly byte[] Reply = Enumerable.Range(0, 70_000).Select(i => (byte)(i * 7)).ToArray();

    static HttpClient Client(Action<SocketsHttpHandler>? tune = null)
    {
        var h = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false, RequestHeaderEncodingSelector = (_, _) => Encoding.Latin1, ResponseHeaderEncodingSelector = (_, _) => Encoding.Latin1 };
        tune?.Invoke(h);
        return new HttpClient(h);
    }

    static async Task<WebApplication> StartForwarder(Uri origin, X509Certificate2? tls = null)
    {
        var app = Forwarder.Build(origin, tls, 0, 0);
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task RelaysMethodTargetQueryBodyAndHeadersUnchangedAndSetsTheGameHost()
    {
        await using var origin = await Origin.Start();
        await using var fwd = await StartForwarder(origin.Url);
        using var http = Client();
        var body = RandomNumberGenerator.GetBytes(200_000);
        var target = "/v1/Inventory/move?x=a%2Fb&y=%E2%82%AC&z=1&z=2";
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(Forwarder.Addresses(fwd)[0] + target)) { Content = new ByteArrayContent(body) };
        req.Headers.Host = "api.example.test";
        req.Headers.TryAddWithoutValidation("X-Custom", "caf\u00E9 \u00FF");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer abc.def");
        req.Content.Headers.ContentType = new("application/x-custom");

        using var resp = await http.SendAsync(req);

        var seen = Assert.Single(origin.Log);
        Assert.Equal("POST", seen.Method);
        Assert.Equal(target, seen.Raw);
        Assert.Equal(body, seen.Body);
        Assert.Equal("caf\u00E9 \u00FF", seen.Headers["X-Custom"]);
        Assert.Equal("Bearer abc.def", seen.Headers["Authorization"]);
        Assert.Equal("application/x-custom", seen.Headers["Content-Type"]);
        Assert.Equal("api.example.test", seen.Headers["X-Game-Host"]);
        Assert.Equal($"{origin.Url.Host}:{origin.Url.Port}", seen.Headers["Host"]); // the game's Host is dropped, not forwarded
        // and the reply comes back unchanged
        Assert.Equal(HttpStatusCode.MultiStatus, resp.StatusCode);
        Assert.Equal("r\u00E9ponse", resp.Headers.GetValues("X-Reply").Single());
        Assert.Equal("application/octet-stream", resp.Content.Headers.ContentType!.ToString());
        Assert.Equal(Reply, await resp.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task KeepsTheRawTargetOfOddPaths()
    {
        await using var origin = await Origin.Start();
        await using var fwd = await StartForwarder(origin.Url);
        using var http = Client();
        foreach (var target in new[] { "/", "//double//slash?x=1", "/a/./b/../c?q=%zz&r=%2F", "/p%20q/%C3%A9?" })
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(Forwarder.Addresses(fwd)[0].TrimEnd('/') + target, new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true }));
            using var resp = await http.SendAsync(req);
            Assert.Equal(target, origin.Log[^1].Raw);
        }
    }

    [Fact]
    public async Task RelaysAChunkedBodyAndAHeadWithoutBody()
    {
        await using var origin = await Origin.Start();
        await using var fwd = await StartForwarder(origin.Url);
        using var http = Client();
        var body = RandomNumberGenerator.GetBytes(50_000);
        using (var req = new HttpRequestMessage(HttpMethod.Put, new Uri(Forwarder.Addresses(fwd)[0] + "/up")) { Content = new StreamContent(new NoLengthStream(body)) })
        {
            req.Headers.TransferEncodingChunked = true;
            using var resp = await http.SendAsync(req);
            Assert.Equal(body, origin.Log[^1].Body);
        }
        using var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, Forwarder.Addresses(fwd)[0] + "/h"));
        Assert.Equal(HttpStatusCode.MultiStatus, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task AnUnreachableServerIs502()
    {
        var dead = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        dead.Start();
        var url = new Uri($"http://127.0.0.1:{((IPEndPoint)dead.LocalEndpoint).Port}");
        dead.Stop();
        await using var fwd = await StartForwarder(url);
        using var http = Client();
        using var resp = await http.GetAsync(Forwarder.Addresses(fwd)[0]);
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
    }

    [Fact]
    public async Task AnswersOverTlsWithTheLeafOfTheLocalAuthority()
    {
        var names = new[] { "api.example.test" };
        var keyName = "hunter-resurgence-play-test-" + Guid.NewGuid().ToString("N");
        using var ca = Pki.NewCa(names, DateTimeOffset.UtcNow);
        using var pub = Pki.NewLeaf(ca, Pki.NewLeafKey(keyName), names, DateTimeOffset.UtcNow);
        try
        {
            using var leaf = Pki.WithKey(pub, keyName); // the way `run` loads it: public certificate plus the stored, non-exportable key
            await using var origin = await Origin.Start();
            await using var fwd = await StartForwarder(origin.Url, leaf);
            var https = Forwarder.Addresses(fwd).Single(a => a.StartsWith("https"));
            using var root = X509CertificateLoader.LoadCertificate(ca.RawData);
            using var http = Client(h => h.SslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "api.example.test",
                RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                {
                    if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) || cert is null) return false;
                    using var chain = new X509Chain();
                    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    chain.ChainPolicy.CustomTrustStore.Add(root);
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    return chain.Build(new X509Certificate2(cert));
                },
            });
            using var req = new HttpRequestMessage(HttpMethod.Get, https + "/tls");
            req.Version = HttpVersion.Version11;
            req.Headers.Host = "api.example.test";
            using var resp = await http.SendAsync(req);
            Assert.Equal(HttpStatusCode.MultiStatus, resp.StatusCode);
            Assert.Equal("api.example.test", origin.Log.Single().Headers["X-Game-Host"]);
        }
        finally
        {
            using var k = System.Security.Cryptography.CngKey.Open(keyName);
            k.Delete();
        }
    }

    /// <summary>A stream with no length: HttpClient sends it chunked.</summary>
    sealed class NoLengthStream(byte[] data) : Stream
    {
        readonly MemoryStream inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, 1000));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
