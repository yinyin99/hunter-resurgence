using System.Net;
using System.Net.Sockets;
using System.Text;
using Backup;

namespace Resurgence.Tests.BackupTool;

/// <summary>"Send to the community server" against a small local HTTP server that speaks the server's sign-in code link and restore upload (docs/server.md).</summary>
public sealed class SendTests : IDisposable
{
    readonly HttpListener listener = new();
    readonly string root;
    readonly TempDir dir = new("send-");
    readonly List<string> seen = [];
    readonly string zip;
    int polls;
    byte[]? uploaded;
    /// <summary>After this many pending polls the sign-in is confirmed; never when negative.</summary>
    int confirmAfter = 2;
    (int Status, string Json) uploadReply = (200, """{"status":"review","message":"kept","report":null}""");
    /// <summary>The address the server gives for the browser (null = its own); the poll answers 404; the confirmed poll sets no cookie.</summary>
    string? linkUrl;
    bool pollGone, noCookie;

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
            (int, string) reply = (404, """{"status":"expired"}""");
            if (req.HttpMethod == "POST" && path == "/auth/link" && xhr)
                reply = (200, $$"""{"code":"abcd1234abcd1234","userCode":"ABCD-1234","url":"{{linkUrl ?? root + "/auth/steam/login?link=abcd1234abcd1234"}}","expires":300}""");
            else if (req.HttpMethod == "GET" && path == "/auth/link/abcd1234abcd1234" && !pollGone)
            {
                if (confirmAfter >= 0 && polls++ >= confirmAfter)
                {
                    if (!noCookie) res.Headers.Add("Set-Cookie", "hr_session=secret; Path=/; HttpOnly; Secure");
                    reply = (200, """{"status":"ok"}""");
                }
                else reply = (200, """{"status":"pending"}""");
            }
            else if (req.HttpMethod == "POST" && path == "/account/restore")
            {
                if (req.Headers["Cookie"] != "hr_session=secret") reply = (401, """{"status":"signed_out","message":"Sign in first."}""");
                else
                {
                    using var ms = new MemoryStream();
                    await req.InputStream.CopyToAsync(ms);
                    uploaded = ms.ToArray();
                    reply = uploadReply;
                }
            }
            var bytes = Encoding.UTF8.GetBytes(reply.Item2);
            (res.StatusCode, res.ContentType) = (reply.Item1, "application/json");
            await res.OutputStream.WriteAsync(bytes);
            res.Close();
        }
    }

    Task<Send.Result> Run(List<(string Code, string Url)> shown, TimeSpan? giveUp = null) =>
        Send.Run(new HttpClient(), root + "/", zip, (c, u) => shown.Add((c, u)), CancellationToken.None, TimeSpan.FromMilliseconds(20), giveUp);

    [Fact]
    public async Task SignInThenUploadIsKeptForReview()
    {
        var shown = new List<(string, string)>();
        var res = await Run(shown);
        Assert.Equal(new Send.Result("review", "kept"), res);
        Assert.Equal([("ABCD-1234", root + "/auth/steam/login?link=abcd1234abcd1234")], shown);
        Assert.Equal("PK-fake-zip"u8.ToArray(), uploaded); // the zip as saved, with the sign-in's cookie
    }

    [Fact]
    public async Task RefusedUploadGivesTheServersReason()
    {
        uploadReply = (400, """{"status":"invalid","message":"Not a backup zip."}""");
        var res = await Run([]);
        Assert.Equal(new Send.Result("invalid", "Not a backup zip."), res);
    }

    [Fact]
    public async Task SignInNobodyConfirmsTimesOutAndSendsNothing()
    {
        confirmAfter = -1;
        var res = await Run([], TimeSpan.FromMilliseconds(300));
        Assert.Equal("timeout", res.Kind);
        Assert.Null(uploaded);
        Assert.DoesNotContain("POST /account/restore", seen);
    }

    [Fact]
    public async Task ZipOverTheServersLimitIsNotSent()
    {
        using (var f = File.OpenWrite(zip)) f.SetLength(Send.MaxZipBytes + 1);
        var res = await Run([]);
        Assert.Equal("too_large", res.Kind);
        Assert.Empty(seen); // not even the sign-in
    }

    [Fact]
    public async Task UnreachableServerIsAnErrorNotACrash()
    {
        listener.Close();
        Assert.Equal("error", (await Run([])).Kind);
    }

    [Fact]
    public async Task ASignInAddressOnAnotherHostIsNotShown()
    {
        linkUrl = "http://evil.example.test/auth/steam/login?link=abcd1234abcd1234";
        var shown = new List<(string, string)>();
        var res = await Run(shown);
        Assert.Equal(new Send.Result("error", "The server did not give a sign-in code."), res);
        Assert.Empty(shown);
    }

    [Fact]
    public async Task ACodeTheServerForgotEndsAsTimeout()
    {
        pollGone = true;
        Assert.Equal("timeout", (await Run([])).Kind);
    }

    [Fact]
    public async Task ConfirmedWithoutACookieIsAnErrorAndNothingIsUploaded()
    {
        noCookie = true;
        Assert.Equal("error", (await Run([])).Kind);
        Assert.DoesNotContain("POST /account/restore", seen);
    }

    [Theory]
    [InlineData(200, """{"status":"weird"}""")]
    [InlineData(502, "<html>")]
    public async Task AnUploadReplyThatIsNotUnderstoodIsAnError(int status, string body)
    {
        uploadReply = (status, body);
        Assert.Equal("error", (await Run([])).Kind);
    }

    [Fact]
    public async Task ACancelledRunThrows()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Send.Run(new HttpClient(), root + "/", zip, (_, _) => { }, cts.Token, TimeSpan.FromMilliseconds(20)));
    }
}
