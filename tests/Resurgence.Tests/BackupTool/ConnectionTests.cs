using System.Net;
using Backup;

namespace Resurgence.Tests.BackupTool;

/// <summary>"Check connection": invented host names, a stub handler and a stub resolver; no real host is contacted.</summary>
public sealed class ConnectionTests
{
    sealed class Stub(Func<Uri, HttpStatusCode> answer, List<string>? seen = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            seen?.Add(r.RequestUri!.Host + " auth=" + (r.Headers.Authorization != null));
            var code = answer(r.RequestUri!);
            return code == 0 ? throw new HttpRequestException("no route") : Task.FromResult(new HttpResponseMessage(code));
        }
    }

    static Settings Cfg(string? server) => new("api.example.test", "www.example.test", "game.exe", ["api.example.test", "www.example.test", "crash.example.test"], ServerUrl: server);
    static Task<IPAddress[]> Public(string _) => Task.FromResult(new[] { IPAddress.Parse("203.0.113.7") });

    [Fact]
    public async Task ReachableAndNoServerUrl()
    {
        var seen = new List<string>();
        var r = await Connection.Run(Cfg(null), new(new Stub(_ => HttpStatusCode.OK, seen)), Public);
        Assert.Equal(new("ok", "unset"), r);
        Assert.Equal(2, seen.Count);
        Assert.All(seen, s => Assert.EndsWith("auth=False", s));
    }

    [Fact]
    public async Task OfficialDownOrCommunityDown()
    {
        var r = await Connection.Run(Cfg("https://community.example.test"), new(new Stub(u => u.Host == "community.example.test" ? HttpStatusCode.OK : 0)), Public);
        Assert.Equal(new("down", "ok"), r);
        r = await Connection.Run(Cfg("https://community.example.test"), new(new Stub(u => u.Host == "community.example.test" ? HttpStatusCode.BadGateway : HttpStatusCode.NotFound)), Public);
        Assert.Equal(new("ok", "down"), r); // a 404 is an answer, a 502 is not
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("192.168.1.20")]
    [InlineData("::1")]
    [InlineData("10.1.2.3")]
    public async Task RedirectedWhenANameResolvesToThisPc(string ip)
    {
        var seen = new List<string>();
        var r = await Connection.Run(Cfg(null), new(new Stub(_ => HttpStatusCode.OK, seen)),
            h => Task.FromResult(new[] { IPAddress.Parse(h == "crash.example.test" ? ip : "203.0.113.7") })); // only a side host is redirected
        Assert.Equal("redirected", r.Official);
        Assert.Empty(seen);
    }

    [Theory]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("169.254.1.1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    [InlineData("0.0.0.0")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task RangeEdgesThatAreThisPcOrPrivate(string ip) =>
        Assert.Equal("redirected", (await Connection.Run(Cfg(null), new(new Stub(_ => HttpStatusCode.OK)), h => Task.FromResult(new[] { IPAddress.Parse(h == "crash.example.test" ? ip : "203.0.113.7") }))).Official);

    [Theory]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.1")]
    [InlineData("192.169.0.1")]
    [InlineData("11.0.0.1")]
    [InlineData("2001:db8::1")]
    public async Task AddressesOutsideThoseRangesAreNotRedirected(string ip) =>
        Assert.Equal("ok", (await Connection.Run(Cfg(null), new(new Stub(_ => HttpStatusCode.OK)), h => Task.FromResult(new[] { IPAddress.Parse(h == "crash.example.test" ? ip : "203.0.113.7") }))).Official);

    [Fact]
    public async Task AResolverThatFailsLeavesTheVerdictToTheRequests()
    {
        var r = await Connection.Run(Cfg(null), new(new Stub(_ => 0)), _ => throw new System.Net.Sockets.SocketException());
        Assert.Equal("down", r.Official);
    }
}
