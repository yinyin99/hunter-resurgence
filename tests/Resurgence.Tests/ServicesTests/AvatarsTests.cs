using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Services;
using Resurgence.Services.Mocks;

namespace Resurgence.Tests.ServicesTests;

public class AvatarsTests
{
    [Theory]
    [InlineData("https://web.test/media/avatar/256/1000005.png", "avatar-1000005")]
    [InlineData("https://site.test/photos/7/avatar.jpg", "https://site.test/photos/7/avatar.jpg")]  // a picture the player imported is a link
    [InlineData("http://site.test/photos/7/avatar.jpg", "avatar-default")]  // only https is drawn as a link
    [InlineData("https://avatars.test/uploads/a.jpg", "avatar-default")]
    [InlineData("https://avatars.test/uploads/thumb/242x242/123.jpg?v=1", "avatar-default")]
    [InlineData("not a url", "avatar-default")]
    [InlineData(null, "avatar-default")]
    public void TheServersAddressIsASlotOrALink(string? url, string drawn) => Assert.Equal(drawn, Avatars.Slot(url));

    [Theory]
    [InlineData("avatar-1000005", "1000005")]
    [InlineData("avatar-default", null)]
    [InlineData("https://site.test/photos/7/avatar.jpg", null)]
    public void AGameAvatarsSlotNamesItsId(string slot, string? id) => Assert.Equal(id, Avatars.IdOf(slot));

    sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return answer(request);
        }
    }

    static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    static ServerAccountService Account(HttpMessageHandler handler, string? session = "abc") =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://server.test:8080") }, new ServerCookie { Value = session, Host = "site.test" }, new MockAccountService(), NullLogger<ServerAccountService>.Instance);

    const string Choices = """{"current":"https://site.test/photos/7/avatar.jpg","ids":[1000000,1000001],"imported":"https://site.test/photos/7/avatar.jpg"}""";

    [Fact]
    public async Task TheGamesAvatarsAndTheImportedOneAreTheChoices()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, Choices));
        var service = Account(fake);
        Assert.Equal([new AvatarChoice("avatar-1000000", "1000000", false), new AvatarChoice("avatar-1000001", "1000001", false), new AvatarChoice("https://site.test/photos/7/avatar.jpg", "", false)], await service.AvatarsAsync());
        Assert.Equal("https://site.test/photos/7/avatar.jpg", (await service.AccountAsync()).Avatar);
        var request = fake.Seen[0].Request;
        Assert.Equal("/account/avatars", request.RequestUri!.AbsolutePath);
        Assert.Equal("hr_session=abc", request.Headers.GetValues("Cookie").Single());
        Assert.Equal("site.test", request.Headers.Host);
    }

    [Theory]
    [InlineData("avatar-1000001", "id=1000001")]
    [InlineData("https://site.test/photos/7/avatar.jpg", "id=imported")]
    public async Task AChoiceIsPostedWithTheServersId(string slot, string form)
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, Choices));
        var service = Account(fake);
        var changed = 0;
        service.Changed += () => changed++;
        Assert.True((await service.SetAvatarAsync(slot)).Ok);
        var (request, body) = Assert.Single(fake.Seen);
        Assert.Equal((HttpMethod.Post, "/account/avatar", form), (request.Method, request.RequestUri!.AbsolutePath, body));
        Assert.True(request.Headers.Contains("X-Requested-With"));
        Assert.Equal(1, changed);  // the status bar reads the avatar again
    }

    [Fact]
    public async Task ARefusedChoiceChangesNothing()
    {
        var service = Account(new Fake(_ => Json(HttpStatusCode.NotFound, """{"status":"invalid"}""")));
        var changed = false;
        service.Changed += () => changed = true;
        Assert.False((await service.SetAvatarAsync("avatar-1")).Ok);
        Assert.False(changed);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{"status":"signed_out"}""")]
    [InlineData(HttpStatusCode.OK, "<html>")]
    public async Task AFailedAnswerIsNoChoiceAndTheDefaultAvatar(HttpStatusCode code, string body)
    {
        var service = Account(new Fake(_ => Json(code, body)));
        Assert.Empty(await service.AvatarsAsync());
        Assert.Equal("avatar-default", (await service.AccountAsync()).Avatar);
    }

    [Fact]
    public async Task WithoutASessionThereIsNoRequest()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, Choices));
        var service = Account(fake, session: null);
        Assert.Empty(await service.AvatarsAsync());
        Assert.False((await service.SetAvatarAsync("avatar-1000000")).Ok);
        Assert.Empty(fake.Seen);
    }

    [Fact]
    public async Task TheSignUpCarouselIsTheGamesAvatars()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, Choices));
        var signup = new ServerSignupService(new HttpClient(fake) { BaseAddress = new Uri("http://server.test:8080") }, new ServerCookie { Value = "abc" }, new MockSignupService(), NullLogger<ServerSignupService>.Instance);
        Assert.Equal(["avatar-1000000", "avatar-1000001"], await signup.AvatarsAsync());  // the imported picture is not one of a new account's
    }
}
