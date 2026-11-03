using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Resurgence.Web;

namespace Resurgence.Tests.WebTests;

public class ForwardedProxyTests
{
    const string Trusted = "172.30.10.0/24,10.0.0.5";

    /// <summary>Runs the forwarded headers middleware for a request from <paramref name="peer"/> claiming https and the client <paramref name="xff"/>.</summary>
    static async Task<HttpContext> Forward(string? trusted, string peer, string xff = "203.0.113.7")
    {
        var o = new ForwardedHeadersOptions();
        ForwardedProxy.Configure(o, trusted);
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        ctx.Request.Scheme = "http";
        ctx.Request.Headers["X-Forwarded-For"] = xff;
        ctx.Request.Headers["X-Forwarded-Proto"] = "https";
        await new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, Options.Create(o)).Invoke(ctx);
        return ctx;
    }

    [Theory]
    [InlineData("172.30.10.3")] // in the network
    [InlineData("10.0.0.5")]    // the single address
    public async Task TrustedPeerSetsClientAndScheme(string peer)
    {
        var ctx = await Forward(Trusted, peer);
        Assert.Equal("203.0.113.7", ctx.Connection.RemoteIpAddress!.ToString());
        Assert.Equal("https", ctx.Request.Scheme);
    }

    [Theory]
    [InlineData(Trusted, "172.30.11.3")]
    [InlineData(Trusted, "127.0.0.1")] // the default loopback entries are gone
    [InlineData("", "127.0.0.1")]
    [InlineData(null, "172.30.10.3")]
    public async Task OtherPeersAreIgnored(string? trusted, string peer)
    {
        var ctx = await Forward(trusted, peer);
        Assert.Equal(peer, ctx.Connection.RemoteIpAddress!.ToString());
        Assert.Equal("http", ctx.Request.Scheme);
    }

    [Fact]
    public async Task OnlyTheLastHopIsTakenEvenWhenAnEarlierOneIsTrustedToo()
    {
        // 198.51.100.9 is what the client claimed; 172.30.10.8 is the hop the trusted proxy saw
        Assert.Equal("172.30.10.8", (await Forward(Trusted, "10.0.0.5", "198.51.100.9, 172.30.10.8")).Connection.RemoteIpAddress!.ToString());
        Assert.Equal("203.0.113.7", (await Forward(Trusted, "10.0.0.5", "198.51.100.9, 203.0.113.7")).Connection.RemoteIpAddress!.ToString());
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("172.30.10.3")]
    public async Task EntriesMayHaveSpacesAround(string peer) =>
        Assert.Equal("203.0.113.7", (await Forward(" 10.0.0.5 , 172.30.10.0/24 ", peer)).Connection.RemoteIpAddress!.ToString());

    [Fact]
    public void AnEntryThatIsNoAddressRefusesTheStart() => Assert.Throws<FormatException>(() => ForwardedProxy.Configure(new ForwardedHeadersOptions(), "not-an-ip"));
}
