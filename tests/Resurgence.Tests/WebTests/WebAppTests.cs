using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;

namespace Resurgence.Tests.WebTests;

/// <summary>The real website in memory (src/Web/Program.cs): headers, cookies, the language choice, the pictures list and the SERVER_URL wiring.</summary>
public class WebAppTests(WebSite site) : IClassFixture<WebSite>
{
    HttpClient Client() => site.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    static async Task<HttpResponseMessage> Get(HttpClient c, string url, string? acceptLanguage = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (acceptLanguage is not null) req.Headers.Add("Accept-Language", acceptLanguage);
        return await c.SendAsync(req, TestContext.Current.CancellationToken);
    }

    static async Task<string> Html(HttpResponseMessage r) => await r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("pt", "pt-BR")]
    [InlineData("de-AT,en;q=0.5", "de")]
    [InlineData("zz, fr;q=0.8", "fr")]
    public async Task TheBrowsersLanguageChoosesThePageLanguage(string header, string lang)
    {
        var r = await Get(Client(), "/feed", header);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains($"<html lang=\"{lang}\"", await Html(r));
    }

    [Fact]
    public async Task TheLangQueryPicksTheLanguageAndRemembersItInASecureCookie()
    {
        var r = await Get(Client(), "/feed?lang=fr", "de");
        Assert.Contains("<html lang=\"fr\"", await Html(r));
        var cookie = Assert.Single(r.Headers.GetValues("Set-Cookie"), c => c.StartsWith(".AspNetCore.Culture=", StringComparison.Ordinal));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("c%3Dfr", cookie);
    }

    [Fact]
    public async Task AnUnknownLangIsNotRemembered()
    {
        var r = await Get(Client(), "/feed?lang=zz");
        Assert.DoesNotContain(r.Headers.GetValues("Set-Cookie"), c => c.StartsWith(".AspNetCore.Culture=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EveryCookieIsSecure()
    {
        var r = await Get(Client(), "/feed?lang=fr&mode=launcher&mock=session:out");
        var cookies = r.Headers.GetValues("Set-Cookie").ToList();
        Assert.Contains(cookies, c => c.StartsWith("mode=", StringComparison.Ordinal));
        Assert.Contains(cookies, c => c.StartsWith("mock=", StringComparison.Ordinal));
        Assert.All(cookies, c => Assert.Contains("secure", c, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("/feed", HttpStatusCode.OK)]
    [InlineData("/no-such-page", HttpStatusCode.NotFound)]
    public async Task EveryResponseCarriesTheSecurityHeaders(string url, HttpStatusCode status)
    {
        var r = await Get(Client(), url);
        Assert.Equal(status, r.StatusCode);
        Assert.Equal("max-age=31536000", r.Headers.GetValues("Strict-Transport-Security").Single());
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("strict-origin-when-cross-origin", r.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("frame-ancestors 'none'", r.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task WithoutOperatorPicturesTheListIsEmpty() => Assert.Equal("{}", await Html(await Get(Client(), "/images.json")));

    [Fact]
    public async Task TheMissingListHoldsEverySlotBeforeAnyPageIsVisited()
    {
        var response = await Get(Client(), "/images-missing.json");
        Assert.Equal("public, max-age=300", response.Headers.CacheControl?.ToString()); // public, so Cloudflare may keep it (docs/images.md)
        using var json = System.Text.Json.JsonDocument.Parse(await Html(response));
        var root = json.RootElement;
        Assert.Equal(0, root.GetProperty("present").GetInt32());
        Assert.True(root.GetProperty("total").GetInt32() > 100);
        var missing = root.GetProperty("missing");
        Assert.Contains(missing.GetProperty("decoration").EnumerateArray(), m => m.GetProperty("key").GetString() == "logo-rating-2" && m.GetProperty("page").GetString() == "Footer");
        Assert.Contains(missing.GetProperty("reserves and maps").EnumerateArray(), m => m.GetProperty("key").GetString() == "reserve-12"); // a launcher page's slot
        Assert.Equal(root.GetProperty("total").GetInt32(), root.GetProperty("areas").EnumerateObject().Sum(a => a.Value.GetProperty("total").GetInt32()));
        Assert.Equal(System.Text.Json.JsonValueKind.Object, root.GetProperty("seenWithoutFile").ValueKind);
    }

    [Fact]
    public void WithoutServerUrlTheSessionIsAMock() =>
        Assert.IsType<Resurgence.Services.Mocks.MockSessionService>(Resolve(site, typeof(ISessionService)));

    [Fact]
    public void WithoutServerUrlTheStoreIsAMock() =>
        Assert.IsType<Resurgence.Services.Mocks.MockStoreService>(Resolve(site, typeof(IStoreService)));

    [Fact]
    public void WithServerUrlTheServicesAreTheServers()
    {
        using var withServer = site.WithWebHostBuilder(b => b.UseSetting("SERVER_URL", "http://127.0.0.1:9"));
        Assert.IsType<ServerSessionService>(Resolve(withServer, typeof(ISessionService)));
        Assert.IsType<ServerLeaderboardsService>(Resolve(withServer, typeof(ILeaderboardsService)));
        Assert.IsType<ServerProfileService>(Resolve(withServer, typeof(IProfileService)));
        Assert.IsType<ServerFriendsService>(Resolve(withServer, typeof(IFriendsService)));
        Assert.IsType<ServerMessagesService>(Resolve(withServer, typeof(IMessagesService)));
        Assert.IsType<ServerReferralService>(Resolve(withServer, typeof(IReferralService)));
        Assert.IsType<ServerWalletService>(Resolve(withServer, typeof(IWalletService)));
        Assert.IsType<ServerStoreService>(Resolve(withServer, typeof(IStoreService)));
        Assert.IsType<ServerRestoreService>(Resolve(withServer, typeof(IRestoreService)));
    }

    [Fact]
    public void TheLiveSiteIsOnlyWithServerUrl()
    {
        using var withServer = site.WithWebHostBuilder(b => b.UseSetting("SERVER_URL", "http://127.0.0.1:9"));
        Assert.True(((Resurgence.Ui.HostMode)Resolve(withServer, typeof(Resurgence.Ui.HostMode))).Live);
        Assert.False(((Resurgence.Ui.HostMode)Resolve(site, typeof(Resurgence.Ui.HostMode))).Live);
    }

    /// <summary>Answers every request with <paramref name="body"/>; with <paramref name="path"/> a signed-in player's server: <c>/auth/me</c> names the player, that path answers
    /// <paramref name="body"/> and any other 404.</summary>
    sealed class ServerStub(string body, HttpStatusCode status = HttpStatusCode.OK, string? path = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var asked = request.RequestUri!.AbsolutePath;
            var (code, reply) = path is null || asked == path ? (status, body)
                : asked == ServerSessionApi.Me ? (HttpStatusCode.OK, """{"userId": 1, "handle": "Me", "hunterscore": 1}""") : (HttpStatusCode.NotFound, "{}");
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(reply, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    /// <summary>A client of the live site signed in as the player of <see cref="ServerStub"/>.</summary>
    static HttpClient SignedIn(WebApplicationFactory<Resurgence.Web.WebLanguageSwitcher> f)
    {
        var client = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"{ServerSessionApi.Cookie}=abc");
        return client;
    }

    // the live site never shows the mock: a handle the server does not know is the "Page not found" page, a known one the server's header (to a signed-in player)
    [Theory]
    [InlineData(HttpStatusCode.NotFound, """{"error": "not found"}""", "Page not found")]
    [InlineData(HttpStatusCode.OK, """{"handle": "Fox", "hunterscore": 5, "rank": 3, "membership": "", "avatar": null}""", ">Fox</h1>")]
    public async Task AProfilePageIsTheServersOrNotFound(HttpStatusCode server, string reply, string shown)
    {
        using var withServer = site.WithWebHostBuilder(b => b.UseSetting("SERVER_URL", "http://server.test")
            .ConfigureTestServices(s => s.AddHttpClient("server").ConfigurePrimaryHttpMessageHandler(() => new ServerStub(reply, server, "/hunters/Fox"))));
        using var client = SignedIn(withServer);

        var r = await Get(client, "/profile/Fox");

        Assert.Equal(server, r.StatusCode);
        Assert.Contains(shown, await Html(r));
    }

    // the store page of the real website, with a fake answer of the server: its items, not the mock's (to a signed-in player)
    [Fact]
    public async Task TheStorePageShowsTheServersItemsNotTheMocks()
    {
        const string reply = """{"categories": [{"key": "0", "name": "Weapons", "children": [{"key": "0", "name": "Rifles"}]}], "items": [{"id": 7, "name": "Test Rifle", "variant": "Blue", "description": "", "category": "0", "ems": 50, "was": null, "gms": 0, "buyable": true}], "bundles": []}""";
        using var withServer = site.WithWebHostBuilder(b => b.UseSetting("SERVER_URL", "http://server.test")
            .ConfigureTestServices(s => s.AddHttpClient("server").ConfigurePrimaryHttpMessageHandler(() => new ServerStub(reply, path: ServerCatalogApi.Store))));
        using var client = SignedIn(withServer);

        var html = await Html(await Get(client, "/store"));

        Assert.Contains("Test Rifle", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "class=\"card "));  // the one item, nothing of the mock's 46
    }

    // one URL per page and language (docs/website.md#search-engines): only page and lang change what a page shows, /feed is /
    [Theory]
    [InlineData("/feed?ref=news", "http://localhost/")]
    [InlineData("/help?page=1", "http://localhost/help")]
    [InlineData("/help?page=2&lang=fr&next=%2F", "http://localhost/help?page=2&amp;lang=fr")]
    [InlineData("/help?lang=en", "http://localhost/help")]
    public async Task TheCanonicalLinkKeepsOnlyThePageAndTheLanguage(string url, string canonical) =>
        Assert.Contains($"<link rel=\"canonical\" href=\"{canonical}\" />", await Html(await Get(Client(), url)));

    [Fact]
    public async Task APageNamesEveryLanguageVersionAndEnglishIsTheDefault()
    {
        var html = await Html(await Get(Client(), "/help", "fr"));
        Assert.Contains("<link rel=\"canonical\" href=\"http://localhost/help?lang=fr\" />", html);
        Assert.Equal(Resurgence.Ui.Languages.All.Length + 1, System.Text.RegularExpressions.Regex.Matches(html, "hreflang=").Count);
        Assert.Contains("hreflang=\"pt-BR\" href=\"http://localhost/help?lang=pt-BR\"", html);
        Assert.Contains("hreflang=\"en\" href=\"http://localhost/help\"", html);
        Assert.Contains("hreflang=\"x-default\" href=\"http://localhost/help\"", html);
    }

    [Fact]
    public async Task APageNotFoundHasNoCanonicalLink()
    {
        var r = await Get(Client(), "/no-such-page");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.DoesNotContain("rel=\"canonical\"", await Html(r));
    }

    // a profile is for signed-in players: signed out the page is the sign-in prompt, never indexed and with no canonical link
    [Fact]
    public async Task ASignedOutProfileIsTheSignInPromptAndNotIndexed()
    {
        using var withServer = site.WithWebHostBuilder(b => b.UseSetting("SERVER_URL", "http://server.test")
            .ConfigureTestServices(s => s.AddHttpClient("server").ConfigurePrimaryHttpMessageHandler(() => new ServerStub("""{"error": "signed out"}""", HttpStatusCode.Unauthorized))));
        using var client = withServer.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

        var r = await Get(client, "/profile/Fox");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var html = await Html(r);
        Assert.Contains("Sign in to see this page", html);
        Assert.DoesNotContain("rel=\"canonical\"", html);
        Assert.DoesNotContain("hreflang=", html);
        Assert.Equal("noindex", r.Headers.GetValues("X-Robots-Tag").Single());
        Assert.False((await Get(client, "/download")).Headers.Contains("X-Robots-Tag"));  // a public page is indexed
    }

    [Fact]
    public async Task TheSitemapListsThePublicPagesOnly()
    {
        using var client = site.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

        var r = await Get(client, "/sitemap.xml");

        Assert.Equal("application/xml", r.Content.Headers.ContentType?.MediaType);
        var locs = System.Xml.Linq.XDocument.Parse(await Html(r)).Descendants("{http://www.sitemaps.org/schemas/sitemap/0.9}loc").Select(l => l.Value).ToList();
        string[] expected = ["/", "/download", "/help", "/your-data", "/terms"];  // no profile, no store, no board
        Assert.Equal(expected.Select(p => "http://localhost" + p).Order(), locs.Order());
    }

    [Theory]
    [InlineData("/", true)]
    [InlineData("/feed", true)]
    [InlineData("/help/support", true)]
    [InlineData("/terms/", true)]
    [InlineData("/profile/Fox", false)]
    [InlineData("/store/item/7", false)]
    [InlineData("/leaderboards/score", false)]
    [InlineData("/missions", false)]
    [InlineData("/signin/handle", false)]
    [InlineData("/helpx", false)]
    public void OnlyThePublicPagesAreIndexed(string path, bool indexed) => Assert.Equal(indexed, Resurgence.Web.Seo.Indexed(path));

    static object Resolve(WebApplicationFactory<Resurgence.Web.WebLanguageSwitcher> f, Type service)
    {
        using var scope = f.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService(service);
    }
}
