using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Resurgence.Launcher;

namespace Resurgence.Tests.LauncherApp;

/// <summary>The launcher's pictures come from the website's /images.json; any failure leaves the labelled boxes (docs/images.md).</summary>
public class LauncherImagesTests
{
    // #209: it failed in full runs when the product's 5 s timeout expired under load (and a freed port could be bound by a parallel test); since the two fixes below it passed 10 full solution test runs in a row.
    [Fact]
    public async Task TheWebsitesListGivesItsPicturesAndAFailureTheBoxes()
    {
        var (status, type, body, cut) = (200, "application/json", """{"logo-bar.png":"1f"}""", false);
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        await using var app = b.Build();
        app.MapGet("/images.json", async ctx =>
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = type;
            if (cut)
                ctx.Response.ContentLength = body.Length + 100; // the client waits for more than it gets
            await ctx.Response.WriteAsync(body);
            if (cut)
            {
                await ctx.Response.Body.FlushAsync();
                ctx.Abort(); // the connection breaks after the headers and part of the body
            }
        });
        await app.StartAsync(TestContext.Current.CancellationToken);
        var origin = new Uri(app.Urls.Single());
        // a loaded machine can take longer than the product's 5 s for the first request (#209); a refusal or a break ends the call without waiting for the timeout, so only a hang waits this long
        async Task<string?> LogoBar(Uri? server) => (await LauncherServices.LoadImages(server, TimeSpan.FromMinutes(2))).Url("logo-bar");

        // only the scheme, host and port of server_url count
        Assert.Equal(origin.GetLeftPart(UriPartial.Authority) + "/images/logo-bar.png?v=1f", await LogoBar(new Uri(origin, "/some/path")));
        Assert.Null(await LogoBar(null));
        foreach (var bad in new[] { (404, "application/json", "{}", false), (200, "application/json", "<html>", false), (200, "application/json; charset=no-such-charset", "{}", false), (200, "application/json", """{"logo-bar.png":"1f"}""", true) })
        {
            (status, type, body, cut) = bad;
            Assert.Null(await LogoBar(origin));
        }

        // bound and never listening: the port stays ours (a parallel test cannot bind it) and a connect is refused
        using var dead = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        dead.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        Assert.Null(await LogoBar(new Uri($"http://127.0.0.1:{((IPEndPoint)dead.LocalEndPoint!).Port}"))); // nothing listens
    }
}
