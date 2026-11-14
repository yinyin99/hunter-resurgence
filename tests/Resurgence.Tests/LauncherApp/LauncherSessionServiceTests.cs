using System.Buffers.Text;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Launcher;
using Resurgence.Services;

namespace Resurgence.Tests.LauncherApp;

/// <summary>The launcher's sign-in by the loopback redirect and PKCE against a small local HTTP server that checks the verifier as the real one does: the cookie it sets is kept protected, reused at the next start and deleted by Sign out.</summary>
public sealed class LauncherSessionServiceTests : IAsyncLifetime
{
    const string Code = "one-time-code";
    WebApplication app = null!;
    string root = "";
    readonly TempDir dir = new("session-");
    readonly List<string> seen = [];
    /// <summary>The challenge the launcher sent: the exchange succeeds for the verifier that hashes to it.</summary>
    string challenge = "";
    /// <summary>The exchange answers a proxy's page.</summary>
    bool exchangeBroken;
    /// <summary>The server never answers: only the client's timeout ends a request.</summary>
    bool slow;
    /// <summary>The browser's requests of the last sign-in: awaited before the answers it got are read.</summary>
    Task browser = Task.CompletedTask;

    public async ValueTask InitializeAsync()
    {
        // Kestrel binds port 0 and reports the port it got: a port probed and freed first could be taken, between the two, by a parallel test's server (#520)
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        app = b.Build();
        app.Run(Serve);
        await app.StartAsync(TestContext.Current.CancellationToken);
        root = app.Urls.Single();
    }

    public async ValueTask DisposeAsync() { await app.DisposeAsync(); dir.Dispose(); }

    string File_ => Path.Combine(dir.Path, "session.dat");

    async Task Serve(HttpContext c)
    {
        var (req, res) = (c.Request, c.Response);
        var path = req.Path.Value;
        lock (seen) seen.Add($"{req.Method} {path}{(req.Headers.TryGetValue("X-Requested-With", out var x) ? " " + x : "")}");
        if (slow) await Task.Delay(Timeout.Infinite, c.RequestAborted); // never answers: the client's timeout ends the request
        (int, string) reply = (404, "{}");
        if (req.Method == "POST" && path == "/auth/app/exchange")
        {
            var form = HttpUtility.ParseQueryString(await new StreamReader(req.Body).ReadToEndAsync());
            var hashed = form["verifier"] is { } v ? Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(v))) : "";
            if (exchangeBroken) reply = (502, "<html>");
            else if (form["code"] == Code && hashed == challenge)
            {
                res.Headers.Append("Set-Cookie", "hr_session=secret; Path=/; HttpOnly; Secure");
                reply = (200, """{"status":"ok"}""");
            }
            else reply = (400, """{"status":"invalid"}""");
        }
        else if (req.Method == "GET" && path == "/auth/me")
            reply = req.Headers.Cookie == "hr_session=secret" ? (200, """{"userId":7,"handle":"Tester","hunterscore":12}""") : (401, "{}");
        else if (req.Method == "POST" && path == "/auth/signout") reply = (204, "");
        (res.StatusCode, res.ContentType) = (reply.Item1, "application/json");
        await res.WriteAsync(reply.Item2);
    }

    /// <summary>The player's browser: called with the address the launcher opens, it comes back to 127.0.0.1 as the server's redirect does, after the stray requests in <paramref name="first"/> (query strings).</summary>
    Action<string> Browser(List<string> opened, List<string> answers, params string[] first) => url =>
    {
        opened.Add(url);
        var q = HttpUtility.ParseQueryString(new Uri(url).Query);
        challenge = q["challenge"]!;
        browser = Task.Run(async () =>
        {
            using var http = new HttpClient();
            foreach (var query in first.Append($"?code={Code}&state={q["state"]}"))
            {
                using var back = await http.GetAsync($"http://127.0.0.1:{q["port"]}/{query}");
                answers.Add($"{(int)back.StatusCode} {await back.Content.ReadAsStringAsync()}");
            }
        });
    };

    LauncherSessionService Make(ServerCookie cookie, Action<string>? open = null, TimeSpan? wait = null) =>
        new(new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(root) }, cookie, File_, open ?? (_ => { }), NullLogger<ServerSessionService>.Instance, wait);

    [Fact]
    public async Task SignInOpensTheBrowserAnswersItAndKeepsTheCookieProtected()
    {
        var (cookie, opened, answers) = (new ServerCookie(), new List<string>(), new List<string>());
        var service = Make(cookie, Browser(opened, answers));
        Assert.Null(await service.CurrentAsync());

        Assert.True((await service.SignInWithBrowserAsync()).Ok);
        await browser;

        var url = Assert.Single(opened);
        Assert.StartsWith(root + "/auth/app/start?challenge=", url);
        var q = HttpUtility.ParseQueryString(new Uri(url).Query);
        Assert.Equal(43, q["challenge"]!.Length);
        Assert.InRange(int.Parse(q["port"]!), 1024, 65535);
        Assert.True(q["state"]!.Length >= 16);
        Assert.Single(answers);
        Assert.StartsWith("200 ", answers[0]);
        Assert.Contains("Signed in, you can close this tab", answers[0]);
        Assert.Equal("secret", cookie.Value);
        Assert.Equal(new Session("Tester", 12), await service.CurrentAsync());
        Assert.DoesNotContain("secret", Encoding.UTF8.GetString(File.ReadAllBytes(File_)));
        Assert.Contains("POST /auth/app/exchange launcher", seen);

        // the next start is signed in without the browser
        var again = new ServerCookie();
        Assert.Equal(new Session("Tester", 12), await Make(again).CurrentAsync());
        Assert.Equal("secret", again.Value);
    }

    [Fact]
    public async Task ARequestWithAnotherStateSignsNothingInAndTheWaitGoesOn()
    {
        var (cookie, opened, answers) = (new ServerCookie(), new List<string>(), new List<string>());
        var service = Make(cookie, Browser(opened, answers, "?code=" + Code + "&state=wrong", "?code=" + Code, "favicon.ico", "?state=" + "x"));

        Assert.True((await service.SignInWithBrowserAsync()).Ok);
        await browser;

        Assert.Equal(["400", "400", "400", "400", "200"], answers.Select(a => a[..3]));
        Assert.Single(seen.Where(s => s.StartsWith("POST /auth/app/exchange")));  // only the right state reached the server
        Assert.Equal("secret", cookie.Value);
    }

    [Fact]
    public async Task AConnectionThatDropsBeforeTheAnswerDoesNotEndTheWait()
    {
        var service = Make(new ServerCookie(), url =>
        {
            var q = HttpUtility.ParseQueryString(new Uri(url).Query);
            challenge = q["challenge"]!;
            var port = int.Parse(q["port"]!);
            browser = Task.Run(async () =>
            {
                for (var i = 0; i < 20; i++)
                {
                    using var probe = new TcpClient { LingerState = new LingerOption(true, 0) };
                    await probe.ConnectAsync(IPAddress.Loopback, port);
                    await probe.GetStream().WriteAsync("GET /favicon.ico HTTP/1.1\r\nHost: x\r\n\r\n"u8.ToArray());
                } // each one is closed with a reset before the answer
                using var http = new HttpClient();
                (await http.GetAsync($"http://127.0.0.1:{port}/?code={Code}&state={q["state"]}")).Dispose();
            });
        });
        Assert.True((await service.SignInWithBrowserAsync()).Ok);
        await browser;
    }

    [Fact]
    public async Task ARefusedExchangeFailsAndKeepsNoCookie()
    {
        var cookie = new ServerCookie();
        var (opened, answers) = (new List<string>(), new List<string>());
        exchangeBroken = true;
        Assert.Equal(Result.Fail("Signin.LinkFailed"), await Make(cookie, Browser(opened, answers)).SignInWithBrowserAsync());
        Assert.Null(cookie.Value);
        Assert.False(File.Exists(File_));
    }

    [Fact]
    public async Task ABrowserThatNeverComesBackTimesOutWithAMessage()
    {
        var cookie = new ServerCookie();
        var service = Make(cookie, wait: TimeSpan.FromMilliseconds(200));
        Assert.Equal(Result.Fail("Signin.LinkExpired"), await service.SignInWithBrowserAsync());
        Assert.Null(cookie.Value);
        Assert.False(File.Exists(File_));
        Assert.DoesNotContain(seen, s => s.StartsWith("POST /auth/app/exchange"));
    }

    [Fact]
    public async Task SignOutDeletesTheCookieAndTellsTheServer()
    {
        var cookie = new ServerCookie();
        var service = Make(cookie, Browser([], []));
        await service.SignInWithBrowserAsync();
        var changed = 0;
        service.Changed += () => changed++;

        await service.SignOutAsync();

        Assert.False(File.Exists(File_));
        Assert.Null(cookie.Value);
        Assert.Contains("POST /auth/signout XMLHttpRequest", seen);
        Assert.Equal(1, changed);
        Assert.Null(await Make(new ServerCookie()).CurrentAsync());
    }

    // #520: it failed under load. The fake server answered after a fixed 500 ms and the client's 100 ms timeout raced that answer: a machine stall of over 400 ms let the answer in first (Ok instead of LinkFailed).
    // The server now never answers, so only the timeout can end the request, however late it runs. Before: 10 failures in 1200 runs of this test (6 processes at once, busy threads; 9 early answers and 1 port clash, see InitializeAsync); after: none in 1200 such runs,
    // none in 1800 runs of the class's tests, and 10 rounds of 8 full-suite runs at once beside 8 busy processes all passed.
    [Fact]
    public async Task AServerThatAnswersTooSlowlyFailsAsUnreachable()
    {
        slow = true;
        var service = new LauncherSessionService(new HttpClient { BaseAddress = new Uri(root), Timeout = TimeSpan.FromMilliseconds(100) }, new ServerCookie(), File_, Browser([], []), NullLogger<ServerSessionService>.Instance);
        Assert.Equal(Result.Fail("Signin.LinkFailed"), await service.SignInWithBrowserAsync());
    }

    [Fact]
    public async Task ADamagedFileIsSignedOutAndACancelEndsTheWaitWithoutAMessage()
    {
        File.WriteAllText(File_, "not protected");
        var cookie = new ServerCookie();
        var service = Make(cookie);
        Assert.Null(cookie.Value);

        var wait = service.SignInWithBrowserAsync();
        await Task.Delay(200, TestContext.Current.CancellationToken);
        service.CancelBrowserSignIn();
        Assert.Equal(new Result(false), await wait);
        Assert.Null(cookie.Value);
    }
}
