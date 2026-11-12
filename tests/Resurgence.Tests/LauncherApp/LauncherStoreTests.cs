using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;

namespace Resurgence.Tests.LauncherApp;

/// <summary>A purchase through the launcher's own service provider (its client, its cookie) against a small local server that checks a purchase as the community server does.</summary>
public sealed class LauncherStoreTests : IDisposable
{
    readonly HttpListener listener = new();
    readonly string root;
    readonly List<(string Line, string Cookie, string Form)> seen = [];

    public LauncherStoreTests()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        root = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add(root + "/");
        listener.Start();
        _ = Serve();
    }

    public void Dispose() => listener.Close();

    async Task Serve()
    {
        while (listener.IsListening)
        {
            HttpListenerContext c;
            try { c = await listener.GetContextAsync(); } catch { return; }
            var (req, res) = (c.Request, c.Response);
            var form = await new StreamReader(req.InputStream).ReadToEndAsync();
            lock (seen) seen.Add(($"{req.HttpMethod} {req.Url!.AbsolutePath}", req.Headers["Cookie"] ?? "", form));
            // the server's checks: a page of its own (the header a cross-site form cannot set), the session; item 1 is for sale, the others are not
            (int, string) reply = req.Url.AbsolutePath != "/account/buy" ? (404, "{}")
                : req.Headers["X-Requested-With"] is null ? (400, """{"status":"invalid"}""")
                : req.Headers["Cookie"] != "hr_session=secret" ? (401, """{"status":"signed_out"}""")
                : form.StartsWith("id=1&", StringComparison.Ordinal) ? (200, """{"status":"bought","ems":15}""")
                : (409, """{"status":"refused"}""");
            var bytes = Encoding.UTF8.GetBytes(reply.Item2);
            (res.StatusCode, res.ContentType) = (reply.Item1, "application/json");
            await res.OutputStream.WriteAsync(bytes);
            res.Close();
        }
    }

    IStoreService SignedIn(out ServiceProvider services)
    {
        services = Resurgence.Launcher.LauncherServices.Build(null, new Uri(root));
        services.GetRequiredService<ServerCookie>().Value = "secret";
        return services.GetRequiredService<IStoreService>();
    }

    [Fact]
    public async Task BuyingSendsTheSessionAndTheHeaderOfAPageOfTheServer()
    {
        var store = SignedIn(out var services);
        using var _ = services;

        Assert.True((await store.BuyAsync(1, false, false)).Ok);

        var (line, cookie, form) = Assert.Single(seen, r => r.Line.EndsWith("/account/buy"));
        Assert.Equal(("POST /account/buy", "hr_session=secret", "id=1&count=1&bundle=0&wallet=GOLD"), (line, cookie, form));
    }

    [Fact]
    public async Task ARefusedPurchaseFails()
    {
        var store = SignedIn(out var services);
        using var _ = services;

        Assert.Equal("Store.Oops", (await store.BuyAsync(2, false, false)).Error);
    }
}
