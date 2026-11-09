using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Services;
using Resurgence.Ui;

namespace Resurgence.Tests.ServicesTests;

public class ServerRestoreServiceTests
{
    sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];
        /// <summary>False: keep only a body's last three bytes (a part of 90 MiB is not kept as text).</summary>
        public bool KeepBodies { get; init; } = true;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Seen.Add((request, KeepBodies ? body : body[^Math.Min(3, body.Length)..]));
            return answer(request);
        }
    }

    sealed class Down : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new HttpRequestException("no route");
    }

    static ServerRestoreService Make(HttpMessageHandler handler, ServerCookie? cookie = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://server.test:8080") }, cookie ?? new ServerCookie { Value = "abc", Host = "site.test" }, NullLogger<ServerRestoreService>.Instance);

    static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    /// <summary>The server's answers to an upload in parts: started, each part received, the finish's result.</summary>
    static HttpResponseMessage Parts(HttpRequestMessage r) => r.RequestUri!.AbsolutePath switch
    {
        "/account/restore/start" => Json(HttpStatusCode.OK, """{"status":"started","id":"u1","partBytes":94371840}"""),
        "/account/restore/u1/finish" => Json(HttpStatusCode.OK, """{"status":"restored","message":"Your backup was restored.","report":"stored: items 2"}"""),
        _ => Json(HttpStatusCode.OK, """{"status":"received"}"""),
    };

    [Fact]
    public async Task UploadSendsTheZipWithTheCookieHostAndTheHeaderAPageOfOursSends()
    {
        var fake = new Fake(Parts);
        var result = await Make(fake).UploadAsync(new MemoryStream("zipbytes"u8.ToArray()), 8);
        Assert.Equal(new RestoreResult("restored", "Your backup was restored.", "stored: items 2"), result);
        Assert.Equal(["/account/restore/start", "/account/restore/u1/0", "/account/restore/u1/finish"], fake.Seen.Select(s => s.Request.RequestUri!.AbsolutePath));
        foreach (var (request, _) in fake.Seen)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("hr_session=abc", Assert.Single(request.Headers.GetValues("Cookie")));
            Assert.Equal("XMLHttpRequest", Assert.Single(request.Headers.GetValues("X-Requested-With")));
            Assert.Equal("site.test", request.Headers.Host);
        }
        var (part, body) = fake.Seen[1];
        Assert.Equal("application/zip", part.Content!.Headers.ContentType!.MediaType);
        Assert.Equal(8, part.Content.Headers.ContentLength);
        Assert.Equal("zipbytes", body);
    }

    [Fact]
    public async Task AZipOverOnePartIsSentInPartsInOrder()
    {
        var zip = new byte[ServerAccountApi.PartBytes + 3];
        zip[^1] = 7;
        var fake = new Fake(Parts) { KeepBodies = false };
        var result = await Make(fake).UploadAsync(new MemoryStream(zip), zip.Length);
        Assert.Equal("restored", result.Status);
        Assert.Equal(["/account/restore/start", "/account/restore/u1/0", "/account/restore/u1/1", "/account/restore/u1/finish"], fake.Seen.Select(s => s.Request.RequestUri!.AbsolutePath));
        Assert.Equal([ServerAccountApi.PartBytes, 3], fake.Seen[1..3].Select(s => s.Request.Content!.Headers.ContentLength));
        Assert.Equal("\0\0\a", fake.Seen[2].Body);
    }

    [Fact]
    public async Task ARefusedPartEndsTheUploadWithTheServersReason()
    {
        var fake = new Fake(r => r.RequestUri!.AbsolutePath == "/account/restore/u1/0" ? Json(HttpStatusCode.Conflict, """{"status":"invalid","message":"Out of order."}""") : Parts(r));
        Assert.Equal(new RestoreResult("invalid", "Out of order.", null), await Make(fake).UploadAsync(new MemoryStream([1]), 1));
        Assert.DoesNotContain(fake.Seen, s => s.Request.RequestUri!.AbsolutePath.EndsWith("/finish", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, """{"status":"already","message":"Once."}""", "already", "Once.")]
    [InlineData(HttpStatusCode.OK, """{"status":"review","message":"Kept."}""", "review", "Kept.")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "", "too_large", "This file is too large.")]  // the server's own limit answers without JSON
    [InlineData(HttpStatusCode.TooManyRequests, "<html>", "busy", "Try again in a minute.")]
    [InlineData(HttpStatusCode.Unauthorized, "", "signed_out", "Sign in first.")]
    [InlineData(HttpStatusCode.BadGateway, "<html>", "unavailable", "The server did not answer.")]
    [InlineData(HttpStatusCode.OK, "<html>", "unavailable", "The server did not answer.")]
    public async Task UploadAnswersAreTheServersOrMappedFromTheStatus(HttpStatusCode code, string body, string status, string message)
    {
        var result = await Make(new Fake(_ => Json(code, body))).UploadAsync(new MemoryStream([1]), 1);
        Assert.Equal((status, message), (result.Status, result.Message));
    }

    [Fact]
    public async Task NoCookieAndNoServerNeverThrow()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, "{}"));
        var signedOut = Make(fake, new ServerCookie());
        Assert.Equal("signed_out", (await signedOut.UploadAsync(new MemoryStream([1]), 1)).Status);
        Assert.Null(await signedOut.StatusAsync());
        Assert.False(await signedOut.DeleteAsync());
        Assert.Empty(fake.Seen);
        var down = Make(new Down());
        Assert.Equal("unavailable", (await down.UploadAsync(new MemoryStream([1]), 1)).Status);
        Assert.Null(await down.StatusAsync());
        Assert.False(await down.DeleteAsync());
    }

    [Fact]
    public async Task StatusListsTheZipsWaitingAndWhatWasDecided()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """
            {"restoredUserId":1001,"review":[{"ts":1,"note":"a"},{"ts":2,"note":"b"}],
             "decisions":[{"ts":3,"kind":"restored","userId":1001,"handle":"sample","handleKept":false,"reason":""},
                          {"ts":4,"kind":"rejected","userId":7,"handle":null,"handleKept":false,"reason":"duplicate"}]}
            """));
        var status = (await Make(fake).StatusAsync())!;
        Assert.Equal(1001, status.RestoredUserId);
        Assert.Equal([new KeptZip(1, "a"), new KeptZip(2, "b")], status.Review);
        Assert.Equal([new RestoreDecision(3, "restored", 1001, "sample", false, ""), new RestoreDecision(4, "rejected", 7, null, false, "duplicate")], status.Decisions);
        Assert.Equal(HttpMethod.Get, fake.Seen[0].Request.Method);
        var none = (await Make(new Fake(_ => Json(HttpStatusCode.OK, """{"restoredUserId":null,"review":[]}"""))).StatusAsync())!;
        Assert.Null(none.RestoredUserId);
        Assert.Empty(none.Review);
        Assert.Empty(none.Decisions);
        Assert.Null(await Make(new Fake(_ => Json(HttpStatusCode.Unauthorized, "{}"))).StatusAsync());
    }

    [Fact]
    public async Task DeleteConfirmsAndDropsTheCookieOnSuccess()
    {
        var cookie = new ServerCookie { Value = "abc" };
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """{"status":"deleted"}"""));
        Assert.True(await Make(fake, cookie).DeleteAsync());
        var (request, body) = Assert.Single(fake.Seen);
        Assert.Equal("/account/delete", request.RequestUri!.AbsolutePath);
        Assert.Equal("confirm=delete", body);
        Assert.Null(cookie.Value);
        var kept = new ServerCookie { Value = "abc" };
        Assert.False(await Make(new Fake(_ => Json(HttpStatusCode.TooManyRequests, "{}")), kept).DeleteAsync());
        Assert.Equal("abc", kept.Value);
    }

    [Fact]
    public async Task TheMockSaysThereIsNoServer()
    {
        var mock = new Resurgence.Services.Mocks.MockRestoreService();
        Assert.Equal("unavailable", (await mock.UploadAsync(new MemoryStream([1]), 1)).Status);
        Assert.False(await mock.DeleteAsync());
    }

    [Fact]
    public void TheRestorePageNeedsASession()
    {
        Assert.True(LauncherGuard.NeedsSession("restore"));
        Assert.False(LauncherGuard.NeedsSession("your-data"));
    }

    // PL23
    [Fact]
    public async Task ATimeoutIsUnavailable()
    {
        var result = await Make(new Cancelled()).UploadAsync(new MemoryStream([1]), 1);
        Assert.Equal("unavailable", result.Status);
    }

    sealed class Cancelled : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new TaskCanceledException("timeout");
    }

    [Fact]
    public async Task AStatusThatIsNotANumberIsNoStatus() =>
        Assert.Null(await Make(new Fake(_ => Json(HttpStatusCode.OK, """{"restoredUserId":"nope","review":[]}"""))).StatusAsync());

}
