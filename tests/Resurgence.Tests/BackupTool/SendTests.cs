using System.Buffers.Text;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Backup;

namespace Resurgence.Tests.BackupTool;

/// <summary>"Send to the community server" against a small local HTTP server that speaks the server's loopback sign-in (it checks the PKCE verifier as the real one does) and restore upload (docs/server.md).</summary>
public sealed class SendTests : IDisposable
{
    readonly HttpListener listener = new();
    readonly string root;
    readonly TempDir dir = new("send-");
    readonly List<string> seen = [];
    readonly string zip;
    byte[]? uploaded;
    readonly List<byte[]> parts = [];
    /// <summary>This part's first try answers with a proxy's error page.</summary>
    int failOnce = -1;
    const string Code = "one-time-code";
    /// <summary>The challenge the tool sent: the exchange succeeds for the verifier that hashes to it.</summary>
    string challenge = "";
    /// <summary>The browser's requests of the last sign-in: awaited before the answers it got are read.</summary>
    Task browser = Task.CompletedTask;
    (int Status, string Json) uploadReply = (200, """{"status":"review","message":"kept","report":null}""");
    /// <summary>The exchange answers 400; the good exchange sets no cookie.</summary>
    bool exchangeRefused, noCookie;

    public SendTests()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        root = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add(root + "/");
        listener.Start();
        _ = Serve();
        zip = Path.Combine(dir.Path, "backup.zip");
        File.WriteAllBytes(zip, "PK-fake-zip"u8.ToArray());
    }

    public void Dispose() { listener.Close(); dir.Dispose(); }

    async Task Serve()
    {
        while (listener.IsListening)
        {
            HttpListenerContext c;
            try { c = await listener.GetContextAsync(); } catch { return; }
            var (req, res) = (c.Request, c.Response);
            lock (seen) seen.Add($"{req.HttpMethod} {req.Url!.AbsolutePath}");
            var path = req.Url!.AbsolutePath;
            var xhr = req.Headers["X-Requested-With"] != null;
            (int, string) reply = (404, "{}");
            if (req.HttpMethod == "POST" && path == "/auth/app/exchange" && xhr)
            {
                var form = HttpUtility.ParseQueryString(await new StreamReader(req.InputStream).ReadToEndAsync());
                var hashed = form["verifier"] is { } v ? Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(v))) : "";
                if (!exchangeRefused && form["code"] == Code && hashed == challenge)
                {
                    if (!noCookie) res.Headers.Add("Set-Cookie", "hr_session=secret; Path=/; HttpOnly; Secure");
                    reply = (200, """{"status":"ok"}""");
                }
                else reply = (400, """{"status":"invalid"}""");
            }
            else if (req.HttpMethod == "POST" && path.StartsWith("/account/restore/", StringComparison.Ordinal))
            {
                if (req.Headers["Cookie"] != "hr_session=secret") reply = (401, """{"status":"signed_out","message":"Sign in first."}""");
                else if (path == "/account/restore/start") reply = (200, """{"status":"started","id":"up1","partBytes":4}""");
                else if (path == "/account/restore/up1/finish")
                {
                    uploaded = [.. parts.SelectMany(b => b)];
                    reply = uploadReply;
                }
                else if (int.TryParse(path["/account/restore/up1/".Length..], out var part))
                {
                    using var ms = new MemoryStream();
                    await req.InputStream.CopyToAsync(ms);
                    if (part == failOnce) { failOnce = -1; reply = (502, "<html>"); }
                    else
                    {
                        if (part == parts.Count - 1) parts.RemoveAt(part);  // sent again
                        if (part == parts.Count) { parts.Add(ms.ToArray()); reply = (200, """{"status":"received"}"""); }
                        else reply = (409, """{"status":"invalid","message":"out of order"}""");
                    }
                }
            }
            var bytes = Encoding.UTF8.GetBytes(reply.Item2);
            (res.StatusCode, res.ContentType) = (reply.Item1, "application/json");
            await res.OutputStream.WriteAsync(bytes);
            res.Close();
        }
    }

    /// <summary>The player's browser: called with the address the tool opens, it comes back to 127.0.0.1 as the server's redirect does, after the stray requests in <paramref name="first"/>.</summary>
    Action<string> Browser(List<string> opened, List<string>? answers = null, params string[] first) => url =>
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
                answers?.Add($"{(int)back.StatusCode} {await back.Content.ReadAsStringAsync()}");
            }
        });
    };

    Task<Send.Result> Run(Action<string>? open = null, TimeSpan? giveUp = null, Action<int, int>? sending = null, int partBytes = Send.PartBytes) =>
        Send.Run(new HttpClient(), root + "/", zip, open ?? Browser([]), CancellationToken.None, TimeSpan.FromMilliseconds(20), giveUp, sending, partBytes);

    [Fact]
    public async Task SignInThenUploadIsKeptForReview()
    {
        var (opened, answers) = (new List<string>(), new List<string>());
        var res = await Run(Browser(opened, answers));
        await browser;
        Assert.Equal(new Send.Result("review", "kept"), res);
        var url = Assert.Single(opened);
        Assert.StartsWith(root + "/auth/app/start?challenge=", url);
        var q = HttpUtility.ParseQueryString(new Uri(url).Query);
        Assert.Equal(43, q["challenge"]!.Length);
        Assert.InRange(int.Parse(q["port"]!), 1024, 65535);
        Assert.True(q["state"]!.Length >= 16);
        Assert.Contains("Signed in, you can close this tab", Assert.Single(answers));
        Assert.Contains("POST /auth/app/exchange", seen);
        Assert.Equal("PK-fake-zip"u8.ToArray(), uploaded); // the zip as saved, with the sign-in's cookie
        Assert.Single(parts);
    }

    [Fact]
    public async Task ARequestWithAnotherStateSignsNothingInAndTheWaitGoesOn()
    {
        var answers = new List<string>();
        var res = await Run(Browser([], answers, "?code=" + Code + "&state=wrong", "?code=" + Code, "favicon.ico"));
        await browser;
        Assert.Equal("review", res.Kind);
        Assert.Equal(["400", "400", "400", "200"], answers.Select(a => a[..3]));
        Assert.Single(seen.Where(s => s == "POST /auth/app/exchange"));  // only the right state reached the server
    }

    [Fact]
    public async Task ALargerZipIsSentInPartsAndAFailedPartIsSentAgain()
    {
        failOnce = 1;
        var progress = new List<(int, int)>();
        var res = await Run(sending: (i, n) => progress.Add((i, n)), partBytes: 4);
        Assert.Equal(new Send.Result("review", "kept"), res);
        Assert.Equal([(1, 3), (2, 3), (3, 3)], progress);
        Assert.Equal(["PK-f", "ake-", "zip"], parts.Select(b => Encoding.ASCII.GetString(b)));
        Assert.Equal("PK-fake-zip"u8.ToArray(), uploaded);
        Assert.Equal(2, seen.Count(s => s == "POST /account/restore/up1/1"));
    }

    [Fact]
    public async Task RefusedUploadGivesTheServersReason()
    {
        uploadReply = (400, """{"status":"invalid","message":"Not a backup zip."}""");
        var res = await Run();
        Assert.Equal(new Send.Result("invalid", "Not a backup zip."), res);
    }

    [Fact]
    public async Task ABrowserThatNeverComesBackTimesOutAndSendsNothing()
    {
        var res = await Run(_ => { }, TimeSpan.FromMilliseconds(300));
        Assert.Equal("timeout", res.Kind);
        Assert.Null(uploaded);
        Assert.DoesNotContain("POST /account/restore/start", seen);
    }

    [Fact]
    public async Task ZipOverTheServersLimitIsNotSent()
    {
        using (var f = File.OpenWrite(zip)) f.SetLength(Send.MaxZipBytes + 1);
        var res = await Run();
        Assert.Equal("too_large", res.Kind);
        Assert.Empty(seen); // not even the sign-in
    }

    [Fact]
    public async Task UnreachableServerIsAnErrorNotACrash()
    {
        listener.Close();
        Assert.Equal("error", (await Run()).Kind);
    }

    [Fact]
    public async Task ARefusedExchangeIsAnErrorAndNothingIsUploaded()
    {
        exchangeRefused = true;
        Assert.Equal("error", (await Run()).Kind);
        Assert.DoesNotContain("POST /account/restore/start", seen);
    }

    [Fact]
    public async Task ConfirmedWithoutACookieIsAnErrorAndNothingIsUploaded()
    {
        noCookie = true;
        Assert.Equal("error", (await Run()).Kind);
        Assert.DoesNotContain("POST /account/restore/start", seen);
    }

    [Theory]
    [InlineData(200, """{"status":"weird"}""")]
    [InlineData(502, "<html>")]
    public async Task AnUploadReplyThatIsNotUnderstoodIsAnError(int status, string body)
    {
        uploadReply = (status, body);
        Assert.Equal("error", (await Run()).Kind);
    }

    [Fact]
    public async Task ACancelledRunThrows()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Send.Run(new HttpClient(), root + "/", zip, _ => { }, cts.Token, TimeSpan.FromMilliseconds(20)));
    }
}
