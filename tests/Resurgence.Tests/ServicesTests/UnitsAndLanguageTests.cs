using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Services;
using Resurgence.Ui;

namespace Resurgence.Tests.ServicesTests;

/// <summary>#825: the game's language and the player's units, on the contract side (the dialog and the launch form are tested with their own classes).</summary>
public class UnitsAndLanguageTests
{
    sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Cookie, string? Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request.Method, request.RequestUri!.AbsolutePath, request.Headers.TryGetValues("Cookie", out var c) ? c.Single() : null, request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            return answer(request);
        }
    }

    static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    static HttpClient Client(Fake fake) => new(fake) { BaseAddress = new Uri("http://server.test:8080") };

    [Fact]
    public void TheGameHas22LanguagesAndEachLauncherLanguageMapsToOne()
    {
        Assert.Equal(22, GameLanguages.All.Count);
        Assert.Equal(22, GameLanguages.All.Select(l => l.Code).Distinct().Count());
        // every language of the launcher's list has a game language of its own, only English is en_US: the default is the launcher's when the game has it
        Assert.All(Languages.All, l => Assert.Equal(l.Code == "en", GameLanguages.For(l.Code) == GameLanguages.Default));
    }

    [Theory]
    [InlineData("en", "en_US")]
    [InlineData("fr", "fr_FR")]
    [InlineData("fr-CA", "fr_FR")]
    [InlineData("de-AT", "de_DE")]
    [InlineData("pt-BR", "pt_BR")]
    [InlineData("pt-PT", "pt_PT")]
    [InlineData("pt", "pt_BR")]
    [InlineData("pt_PT", "pt_PT")]
    [InlineData("nb", "nb_NO")]
    [InlineData("no", "nb_NO")]
    [InlineData("nn-NO", "nb_NO")]
    [InlineData("ru", "ru_RU")]
    [InlineData("ka", "ka_KA")]
    [InlineData("et", "et_EE")]
    [InlineData("zh-CN", "en_US")]  // not a game language: English
    [InlineData("", "en_US")]
    [InlineData(null, "en_US")]
    public void ALanguageTagGivesTheGamesLanguageOrEnglish(string? tag, string expected) => Assert.Equal(expected, GameLanguages.For(tag));

    [Fact]
    public void ImperialPutsYardsAndPoundsFirstAndMetricIsAsBefore()
    {
        Assert.Equal("124.53m (408.56ft)", Fmt.Metres(124.53));
        Assert.Equal("136.19yd (124.53m)", Fmt.Metres(124.53, imperial: true));
        Assert.Equal("508.8m (1669.3ft)", Fmt.Board("range", 508.8));
        Assert.Equal("556.4yd (508.8m)", Fmt.Board("range", 508.8, imperial: true));
        Assert.Equal("12.7000", Fmt.Board("score", 12.7, imperial: true));  // only a distance changes
        Assert.Equal("100.000 kg (220.5 lbs)", Fmt.Weight(100));
        Assert.Equal("220.5 lbs (100.000 kg)", Fmt.Weight(100, imperial: true));
    }

    [Fact]
    public async Task TheSessionCarriesTheUnitsOfTheAccount()
    {
        static ServerSessionService Make(string me) =>
            new(Client(new Fake(_ => Json(HttpStatusCode.OK, me))), new ServerCookie { Value = "abc", Host = "site.test" }, NullLogger<ServerSessionService>.Instance);
        Assert.True((await Make("""{"userId": 1, "handle": "Fox", "hunterscore": 5, "imperial": true}""").CurrentAsync())!.Imperial);
        Assert.False((await Make("""{"userId": 1, "handle": "Fox", "hunterscore": 5, "imperial": false}""").CurrentAsync())!.Imperial);
        Assert.False((await Make("""{"userId": 1, "handle": "Fox", "hunterscore": 5}""").CurrentAsync())!.Imperial);  // a server that does not say: metric
    }

    [Theory]
    [InlineData(true, "use_imperial=1")]
    [InlineData(false, "use_imperial=0")]
    public async Task TheUnitsAreSavedOnTheServerAndTheChangeIsAnnounced(bool imperial, string form)
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """{"use_imperial": true}"""));
        var changes = 0;
        var service = new ServerAccountService(Client(fake), new ServerCookie { Value = "abc", Host = "site.test" }, new Resurgence.Services.Mocks.MockAccountService(), NullLogger<ServerAccountService>.Instance);
        service.Changed += () => changes++;
        Assert.True((await service.SetImperialAsync(imperial)).Ok);
        Assert.Equal((HttpMethod.Post, ServerAccountApi.Settings, "hr_session=abc", form), Assert.Single(fake.Seen));
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task RefusedOrSignedOutTheUnitsAreNotSaved()
    {
        var changes = 0;
        var refused = new ServerAccountService(Client(new Fake(_ => Json(HttpStatusCode.BadRequest, "{}"))), new ServerCookie { Value = "abc" }, new Resurgence.Services.Mocks.MockAccountService(), NullLogger<ServerAccountService>.Instance);
        refused.Changed += () => changes++;
        Assert.Equal("Store.Oops", (await refused.SetImperialAsync(true)).Error);
        var fake = new Fake(_ => Json(HttpStatusCode.OK, "{}"));
        var signedOut = new ServerAccountService(Client(fake), new ServerCookie(), new Resurgence.Services.Mocks.MockAccountService(), NullLogger<ServerAccountService>.Instance);
        Assert.False((await signedOut.SetImperialAsync(true)).Ok);
        Assert.Empty(fake.Seen);
        Assert.Equal(0, changes);
    }
}
