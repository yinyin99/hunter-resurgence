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

    /// <summary>Runs the forwarded headers middleware for a request from <paramref name="peer"/> claiming https from 203.0.113.7.</summary>
    static async Task<HttpContext> Forward(string? trusted, string peer)
    {
        var o = new ForwardedHeadersOptions();
        ForwardedProxy.Configure(o, trusted);
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        ctx.Request.Scheme = "http";
        ctx.Request.Headers["X-Forwarded-For"] = "203.0.113.7";
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
}
