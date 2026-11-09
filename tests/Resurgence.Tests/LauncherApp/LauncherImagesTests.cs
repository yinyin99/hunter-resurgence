using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Resurgence.Launcher;

namespace Resurgence.Tests.LauncherApp;

/// <summary>The launcher's pictures come from the website's /images.json; any failure leaves the labelled boxes (docs/images.md).</summary>
public class LauncherImagesTests
{
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
        async Task<string?> LogoBar(Uri? server) => (await LauncherServices.LoadImages(server)).Url("logo-bar");

        // only the scheme, host and port of server_url count
        Assert.Equal(origin.GetLeftPart(UriPartial.Authority) + "/images/logo-bar.png?v=1f", await LogoBar(new Uri(origin, "/some/path")));
        Assert.Null(await LogoBar(null));
        foreach (var bad in new[] { (404, "application/json", "{}", false), (200, "application/json", "<html>", false), (200, "application/json; charset=no-such-charset", "{}", false), (200, "application/json", """{"logo-bar.png":"1f"}""", true) })
        {
            (status, type, body, cut) = bad;
            Assert.Null(await LogoBar(origin));
        }

        var dead = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        dead.Start();
        var port = ((IPEndPoint)dead.LocalEndpoint).Port;
        dead.Stop();
        Assert.Null(await LogoBar(new Uri($"http://127.0.0.1:{port}"))); // nothing listens
    }
}
