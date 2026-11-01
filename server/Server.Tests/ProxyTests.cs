using System.Net;
using System.Text.Json.Nodes;

namespace Resurgence.Server.Tests;

/// <summary>Behind a proxy: the game host in a header (trusted peers only), the admin API by peer address, request size limits.</summary>
public class ProxyTests
{
    static readonly JsonObject Cfg = new(Replay.HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));

    static Replay.Game Game() => new(new Replay.HarTable(), Cfg, null, Path.Combine(Path.GetTempPath(), "no-mirror"));

    static IReadOnlyList<IPNetwork> Nets(params string[] nets) => [.. nets.Select(n => Settings.TryNetwork(n, out var net) ? net : throw new ArgumentException(n))];

    static HttpRequestMessage Post(string url, string? header = null, string? value = null, int bytes = 0)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(new byte[bytes]) };
        if (header != null) request.Headers.TryAddWithoutValidation(header, value);
        return request;
    }

    [Fact]
    public async Task TrustedProxyNamesTheGameHostInItsHeader()
    {
        await using var server = await Loopback.StartAsync(Game(), trustedProxies: Nets("127.0.0.1"));
        // telemetry.test is a sink host: 204 only when the request is routed as that host
        using var viaHeader = await server.Http.SendAsync(Post("http://proxy.test/api/x", "X-Game-Host", "telemetry.test:443"));
        Assert.Equal(HttpStatusCode.NoContent, viaHeader.StatusCode);
        using var plain = await server.Http.SendAsync(Post("http://proxy.test/api/x"));
        Assert.NotEqual(HttpStatusCode.NoContent, plain.StatusCode);
        using var junk = await server.Http.SendAsync(Post("http://proxy.test/api/x", "X-Game-Host", "telemetry.test/../x"));
        Assert.NotEqual(HttpStatusCode.NoContent, junk.StatusCode);
    }

    [Fact]
    public async Task HeaderFromAnUntrustedPeerIsIgnored()
    {
        await using var server = await Loopback.StartAsync(Game(), trustedProxies: Nets("10.0.0.0/8"));
        using var response = await server.Http.SendAsync(Post("http://proxy.test/api/x", "X-Game-Host", "telemetry.test"));
        Assert.NotEqual(HttpStatusCode.NoContent, response.StatusCode);
        await using var none = await Loopback.StartAsync(Game());
        using var response2 = await none.Http.SendAsync(Post("http://proxy.test/api/x", "X-Game-Host", "telemetry.test"));
        Assert.NotEqual(HttpStatusCode.NoContent, response2.StatusCode);
    }

    [Theory]
    [InlineData("X-Forwarded-For", "203.0.113.9")]
    [InlineData("Forwarded", "for=203.0.113.9")]
    [InlineData("Via", "1.1 proxy")]
    [InlineData("X-Game-Host", "localhost")]
    public async Task AdminRefusesARequestThatAProxyRelayed(string header, string value)
    {
        await using var server = await Loopback.StartAsync(Game(), trustedProxies: Nets("127.0.0.1"));
        using var direct = await server.Http.GetAsync("http://localhost/admin/status");
        Assert.Equal(HttpStatusCode.OK, direct.StatusCode);
        foreach (var path in new[] { "/admin/status", "/swagger/index.html", "/openapi/admin.json", "/" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost" + path);
            request.Headers.TryAddWithoutValidation(header, value);
            using var refused = await server.Http.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }
    }

    [Fact]
    public async Task BodiesOverTheLimitAreRefused()
    {
        await using var server = await Loopback.StartAsync(Game(), maxBodyBytes: 100);
        using var small = await server.Http.SendAsync(Post("http://telemetry.test/api/x", bytes: 100));
        Assert.Equal(HttpStatusCode.NoContent, small.StatusCode);
        using var big = await server.Http.SendAsync(Post("http://telemetry.test/api/x", bytes: 101));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.StatusCode);
    }

    [Fact]
    public async Task AdminBodiesAreLimitedToSixtyFourKilobytes()
    {
        await using var server = await Loopback.StartAsync(Game());
        using var big = await server.Http.SendAsync(Post("http://localhost/admin/grants/items", bytes: 64 * 1024 + 1));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.StatusCode);
        // a chunked body carries no length: the server stops reading at the limit, and nothing is granted
        using var chunked = new HttpRequestMessage(HttpMethod.Post, "http://localhost/admin/grants/items") { Content = new StreamContent(new MemoryStream(new byte[64 * 1024 + 1])) };
        chunked.Headers.TransferEncodingChunked = true;
        using var response = await server.Http.SendAsync(chunked);
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
