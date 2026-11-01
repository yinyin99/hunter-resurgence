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

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
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

    [Fact]
    public async Task UploadSendsTheZipWithTheCookieHostAndTheHeaderAPageOfOursSends()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """{"status":"restored","message":"Your backup was restored.","report":"stored: items 2"}"""));
        var result = await Make(fake).UploadAsync(new MemoryStream("zipbytes"u8.ToArray()), 8);
        Assert.Equal(new RestoreResult("restored", "Your backup was restored.", "stored: items 2"), result);
        var (request, body) = Assert.Single(fake.Seen);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/account/restore", request.RequestUri!.AbsolutePath);
        Assert.Equal("hr_session=abc", Assert.Single(request.Headers.GetValues("Cookie")));
        Assert.Equal("XMLHttpRequest", Assert.Single(request.Headers.GetValues("X-Requested-With")));
        Assert.Equal("site.test", request.Headers.Host);
        Assert.Equal("application/zip", request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("zipbytes", body);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, """{"status":"already","message":"Once."}""", "already", "Once.")]
    [InlineData(HttpStatusCode.OK, """{"status":"review","message":"Kept."}""", "review", "Kept.")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "", "too_large", "This file is too large.")]  // the server's own limit answers without JSON
    [InlineData(HttpStatusCode.TooManyRequests, "<html>", "busy", "Try again in a minute.")]
    [InlineData(HttpStatusCode.Unauthorized, "", "signed_out", "Sign in first.")]
    [InlineData(HttpStatusCode.BadGateway, "<html>", "unavailable", "The server did not answer.")]
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
    public async Task StatusCountsTheZipsWaitingForReview()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """{"restoredUserId":1001,"review":[{"ts":1,"note":"a"},{"ts":2,"note":"b"}]}"""));
        Assert.Equal(new RestoreStatus(1001, 2), await Make(fake).StatusAsync());
        Assert.Equal(HttpMethod.Get, fake.Seen[0].Request.Method);
        Assert.Equal(new RestoreStatus(null, 0), await Make(new Fake(_ => Json(HttpStatusCode.OK, """{"restoredUserId":null,"review":[]}"""))).StatusAsync());
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
}
