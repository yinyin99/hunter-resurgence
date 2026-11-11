using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Launcher;
using Resurgence.Services;

namespace Resurgence.Tests.LauncherApp;

/// <summary>The launcher's sign-in by the server's code link against a small local HTTP server: the cookie it sets is kept protected, reused at the next start and deleted by Sign out.</summary>
public sealed class LauncherSessionServiceTests : IDisposable
{
    readonly HttpListener listener = new();
    readonly string root;
    readonly TempDir dir = new("session-");
    readonly List<string> seen = [];
    int polls;
    /// <summary>After this many pending polls the sign-in is confirmed; never when negative.</summary>
    int confirmAfter = 1;

    public LauncherSessionServiceTests()
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

    public void Dispose() { listener.Close(); dir.Dispose(); }

    string File_ => Path.Combine(dir.Path, "session.dat");

    async Task Serve()
    {
        while (listener.IsListening)
        {
            HttpListenerContext c;
            try { c = await listener.GetContextAsync(); } catch { return; }
            var (req, res) = (c.Request, c.Response);
            var path = req.Url!.AbsolutePath;
            lock (seen) seen.Add($"{req.HttpMethod} {path}{(req.Headers["X-Requested-With"] is { } x ? " " + x : "")}");
            (int, string) reply = (404, "{}");
            if (req.HttpMethod == "POST" && path == "/auth/link")
                reply = (200, $$"""{"code":"abcd1234abcd1234","userCode":"ABCD-1234","url":"{{root}}/auth/steam/login?link=abcd1234abcd1234","expires":300}""");
            else if (req.HttpMethod == "GET" && path == "/auth/link/abcd1234abcd1234")
            {
                if (confirmAfter >= 0 && polls++ >= confirmAfter)
                {
                    res.Headers.Add("Set-Cookie", "hr_session=secret; Path=/; HttpOnly; Secure");
                    reply = (200, """{"status":"ok"}""");
                }
                else reply = (200, """{"status":"pending"}""");
            }
            else if (req.HttpMethod == "GET" && path == "/auth/me")
                reply = req.Headers["Cookie"] == "hr_session=secret" ? (200, """{"userId":7,"handle":"Tester","hunterscore":12}""") : (401, "{}");
            else if (req.HttpMethod == "POST" && path == "/auth/signout") reply = (204, "");
            var bytes = Encoding.UTF8.GetBytes(reply.Item2);
            (res.StatusCode, res.ContentType) = (reply.Item1, "application/json");
            await res.OutputStream.WriteAsync(bytes);
            res.Close();
        }
    }

    LauncherSessionService Make(ServerCookie cookie, List<string>? opened = null) =>
        new(new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(root) }, cookie, File_, u => opened?.Add(u), NullLogger<ServerSessionService>.Instance, TimeSpan.FromMilliseconds(20));

    [Fact]
    public async Task SignInShowsTheCodeOpensTheBrowserAndKeepsTheCookieProtected()
    {
        var (cookie, shown, opened) = (new ServerCookie(), new List<string>(), new List<string>());
        var service = Make(cookie, opened);
        Assert.Null(await service.CurrentAsync());

        Assert.True((await service.SignInWithLinkAsync(shown.Add)).Ok);

        Assert.Equal(["ABCD-1234"], shown);
        Assert.Equal([root + "/auth/steam/login?link=abcd1234abcd1234"], opened);
        Assert.Equal("secret", cookie.Value);
        Assert.Equal(new Session("Tester", 12), await service.CurrentAsync());
        Assert.DoesNotContain("secret", Encoding.UTF8.GetString(File.ReadAllBytes(File_)));
        Assert.Contains("POST /auth/link launcher", seen);

        // the next start is signed in without the browser
        var again = new ServerCookie();
        Assert.Equal(new Session("Tester", 12), await Make(again).CurrentAsync());
        Assert.Equal("secret", again.Value);
    }

    [Fact]
    public async Task SignOutDeletesTheCookieAndTellsTheServer()
    {
        var cookie = new ServerCookie();
        var service = Make(cookie);
        await service.SignInWithLinkAsync(_ => { });
        var changed = 0;
        service.Changed += () => changed++;

        await service.SignOutAsync();

        Assert.False(File.Exists(File_));
        Assert.Null(cookie.Value);
        Assert.Contains("POST /auth/signout XMLHttpRequest", seen);
        Assert.Equal(1, changed);
        Assert.Null(await Make(new ServerCookie()).CurrentAsync());
    }

    [Fact]
    public async Task ADamagedFileIsSignedOutAndANotConfirmedLinkFails()
    {
        File.WriteAllText(File_, "not protected");
        var cookie = new ServerCookie();
        var service = Make(cookie);
        Assert.Null(cookie.Value);

        confirmAfter = -1;
       
        var wait = service.SignInWithLinkAsync(_ => { });
        await Task.Delay(200);
        await service.SignOutAsync(); // ends the wait
        Assert.False((await wait).Ok);
        Assert.Null(cookie.Value);
    }
}
