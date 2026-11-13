using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Services;
using Resurgence.Services.Mocks;
using Resurgence.Ui;
using Resurgence.Ui.Components;
using Resurgence.Ui.Pages;
using Resurgence.Ui.Shell;

namespace Resurgence.Tests.WebTests;

/// <summary>Every player is drawn with their own avatar: a slot of the game's list, or the picture imported from a backup as an image; a player without one shows the default.</summary>
public sealed class AvatarPagesTests : UiContext
{
    const string Imported = "https://site.test/photos/7/avatar.jpg";

    sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return answer(request);
        }
    }

    static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public void AnImportedPictureIsDrawnAsAnImageAndAGameAvatarAsItsSlot()
    {
        var link = Render<AssetSlot>(p => p.Add(s => s.Name, Imported).Add(s => s.Width, 40).Add(s => s.Height, 40));
        Assert.Equal(Imported, link.Find("img").GetAttribute("src"));
        Assert.Equal("", link.Find("img").GetAttribute("alt"));  // the link is no name
        var slot = Render<AssetSlot>(p => p.Add(s => s.Name, "avatar-1000001").Add(s => s.Width, 40).Add(s => s.Height, 40));
        Assert.Equal("avatar-1000001", slot.Find("span").GetAttribute("aria-label"));  // no picture file: the labelled box
    }

    [Fact]
    public void ARowOfTheBoardShowsThePlayersAvatar()
    {
        var cut = Render<RankRow>(p => p.Add(r => r.R, new Ranked(1, "Fox", 5, Avatar: Imported)).Add(r => r.Kind, "hunterscore"));
        Assert.Equal(Imported, cut.Find("td.avatar img").GetAttribute("src"));
        var plain = Render<RankRow>(p => p.Add(r => r.R, new Ranked(1, "Fox", 5)).Add(r => r.Kind, "hunterscore"));
        Assert.Equal("avatar-default", plain.Find("td.avatar .asset-slot").GetAttribute("aria-label"));
    }

    [Fact]
    public void TheStatusBarShowsTheSignedInPlayersAvatarLive()
    {
        Services.GetRequiredService<HostMode>().Live = true;
        var cut = Render<StatusBar>(p => p.Add(b => b.Session, new Session("Fox", 5, Avatar: Imported)));
        Assert.Equal(Imported, cut.Find("a[href='/profile/Fox'] img").GetAttribute("src"));
    }

    [Fact]
    public void TheFriendsListShowsEachFriendsAvatar()
    {
        Services.AddSingleton<IFriendsService>(new Friends([new("Fern", true, Imported), new("Elm", false, "avatar-1000001"), new("Oak", false)]));
        var cut = Render<FriendsDropdown>();
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("ul.list li").Count));
        var drawn = cut.FindAll("ul.list li").Select(li => li.QuerySelector("img, span.asset-slot")!).Select(e => e.GetAttribute("src") ?? e.GetAttribute("aria-label")).ToList();
        Assert.Equal([Imported, "avatar-1000001", "avatar-default"], drawn);
    }

    sealed class NoSwitch : ILanguageSwitcher { public void Set(string code) { } }

    sealed class Friends(IReadOnlyList<Friend> list) : IFriendsService
    {
        public Task<IReadOnlyList<Friend>> FriendsAsync() => Task.FromResult(list);
        public Task<IReadOnlyList<Friend>> SearchAsync(string text) => Task.FromResult<IReadOnlyList<Friend>>([]);
        public Task<IReadOnlyList<Friend>> RequestsAsync() => Task.FromResult<IReadOnlyList<Friend>>([]);
        public Task<IReadOnlyList<string>> PendingAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<Result> SendRequestAsync(string handle) => Task.FromResult(Result.Success);
        public Task<Result> AcceptAsync(string handle) => Task.FromResult(Result.Success);
        public Task<Result> DeclineAsync(string handle) => Task.FromResult(Result.Success);
        public Task<Result> RemoveAsync(string handle) => Task.FromResult(Result.Success);
    }

    [Fact]
    public void TheLiveSettingsOfferTheServersAvatarsOnly()
    {
        var fake = new Fake(_ => Json($$"""{"current":"{{Imported}}","ids":[1000000,1000001],"imported":"{{Imported}}"}"""));
        Services.AddSingleton<IAccountService>(new ServerAccountService(new HttpClient(fake) { BaseAddress = new Uri("http://server.test") }, new ServerCookie { Value = "abc" }, new MockAccountService(), NullLogger<ServerAccountService>.Instance));
        Services.AddSingleton<ILanguageSwitcher>(new NoSwitch());
        Services.GetRequiredService<HostMode>().Live = true;
        var cut = Render<Settings>(p => p.Add(s => s.Tab, "email"));  // another tab is the avatar's: the rest is the mock's
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("button.pick").Count));
        Assert.Single(cut.FindAll("nav.menu li"));
        Assert.Equal(Imported, cut.Find(".cur img").GetAttribute("src"));
        Assert.Equal(["pick", "pick", "pick on"], cut.FindAll("button.pick").Select(b => b.ClassName!.Trim()));  // the current one is marked

        cut.FindAll("button.pick")[1].Click();  // a game avatar
        cut.WaitForAssertion(() => Assert.Contains(fake.Seen, s => s.Request.Method == HttpMethod.Post && s.Body == "id=1000001"));
    }
}
