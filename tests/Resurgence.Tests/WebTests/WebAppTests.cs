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
        Assert.IsType<ServerWalletService>(Resolve(withServer, typeof(IWalletService)));
        Assert.IsType<ServerStoreService>(Resolve(withServer, typeof(IStoreService)));
        Assert.IsType<ServerRestoreService>(Resolve(withServer, typeof(IRestoreService)));
        Assert.IsType<ServerSeasonalEventsService>(Resolve(withServer, typeof(ISeasonalEventsService)));
    }

    [Fact]
    public void TheLiveSiteIsOnlyWithServerUrl()
    {
        using var withServer = site.WithWebHostBuilder(b => b.UseSetting("SERVER_URL", "http://127.0.0.1:9"));
        Assert.True(((Resurgence.Ui.HostMode)Resolve(withServer, typeof(Resurgence.Ui.HostMode))).Live);
        Assert.False(((Resurgence.Ui.HostMode)Resolve(site, typeof(Resurgence.Ui.HostMode))).Live);
    }

    sealed class ServerStub(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
    }

    // the live site never shows the mock: a handle the server does not know is the "Page not found" page, a known one the server's header
    [Theory]
    [InlineData(HttpStatusCode.NotFound, """{"error": "not found"}""", "Page not found")]
    [InlineData(HttpStatusCode.OK, """{"handle": "Fox", "hunterscore": 5, "rank": 3, "membership": "", "avatar": null}""", ">Fox</h1>")]
    public async Task AProfilePageIsTheServersOrNotFound(HttpStatusCode server, string reply, string shown)
    {
        using var withServer = site.WithWebHostBuilder(b => b.UseSetting("SERVER_URL", "http://server.test")
            .ConfigureTestServices(s => s.AddHttpClient("server").ConfigurePrimaryHttpMessageHandler(() => new ServerStub(reply, server))));
        using var client = withServer.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

        var r = await Get(client, "/profile/Fox");

        Assert.Equal(server, r.StatusCode);
        Assert.Contains(shown, await Html(r));
    }

    // the store page of the real website, with a fake answer of the server: its items, not the mock's
    [Fact]
    public async Task TheStorePageShowsTheServersItemsNotTheMocks()
    {
        const string reply = """{"categories": [{"key": "0", "name": "Weapons", "children": [{"key": "0", "name": "Rifles"}]}], "items": [{"id": 7, "name": "Test Rifle", "variant": "Blue", "description": "", "category": "0", "ems": 50, "was": null, "gms": 0, "buyable": true}], "bundles": []}""";
        using var withServer = site.WithWebHostBuilder(b => b.UseSetting("SERVER_URL", "http://server.test")
            .ConfigureTestServices(s => s.AddHttpClient("server").ConfigurePrimaryHttpMessageHandler(() => new ServerStub(reply))));
        using var client = withServer.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

        var html = await Html(await Get(client, "/store"));

        Assert.Contains("Test Rifle", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "class=\"card "));  // the one item, nothing of the mock's 46
    }

    // one URL per page and language (docs/website.md#search-engines): only page and lang change what a page shows, /feed is /
    [Theory]
    [InlineData("/feed?ref=news", "http://localhost/")]
    [InlineData("/store?page=1", "http://localhost/store")]
    [InlineData("/store?page=2&lang=fr&next=%2F", "http://localhost/store?page=2&amp;lang=fr")]
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

    // a route that exists for a handle the server does not know: the profile's Nav.NotFound drops the links too
    [Fact]
    public async Task AnUnknownProfileHasNoCanonicalLink()
    {
        using var withServer = site.WithWebHostBuilder(b => b.UseSetting("SERVER_URL", "http://server.test")
            .ConfigureTestServices(s => s.AddHttpClient("server").ConfigurePrimaryHttpMessageHandler(() => new ServerStub("""{"error": "not found"}""", HttpStatusCode.NotFound))));
        using var client = withServer.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

        var r = await Get(client, "/profile/Nobody");

        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        var html = await Html(r);
        Assert.DoesNotContain("rel=\"canonical\"", html);
        Assert.DoesNotContain("hreflang=", html);
    }

    /// <summary>The server's store and hunterscore board, each asked with the website's host (the server answers them for its web host only).</summary>
    sealed class ListsServer : HttpMessageHandler
    {
        public readonly List<string?> Hosts = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Hosts) Hosts.Add(request.Headers.Host);
            var body = request.RequestUri!.AbsolutePath.StartsWith(ServerSessionApi.Hunters, StringComparison.Ordinal)
                ? """{"page": 1, "pages": 1, "total": 1, "rows": [{"rank": 1, "handle": "Fox", "hunterscore": 5}]}"""
                : """{"categories": [{"key": "0", "name": "Weapons", "children": []}], "items": [{"id": 7, "name": "Test Rifle", "variant": "", "description": "", "category": "0", "ems": 50, "was": null, "gms": 0, "buyable": true}], "bundles": [{"id": 3, "name": "Kit", "description": "", "price": 10, "buyable": true, "items": []}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task TheSitemapListsTheOpenPagesTheServersStoreAndItsProfiles()
    {
        var server = new ListsServer();
        using var withServer = site.WithWebHostBuilder(b => b.UseSetting("SERVER_URL", "http://server.test")
            .ConfigureTestServices(s => s.AddHttpClient("server").ConfigurePrimaryHttpMessageHandler(() => server)));
        using var client = withServer.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

        var r = await Get(client, "/sitemap.xml");

        Assert.Equal("application/xml", r.Content.Headers.ContentType?.MediaType);
        var locs = System.Xml.Linq.XDocument.Parse(await Html(r)).Descendants("{http://www.sitemaps.org/schemas/sitemap/0.9}loc").Select(l => l.Value).ToList();
        string[] expected = ["/", "/store", "/store/bundles", "/leaderboards/hunterscore", "/leaderboards/lanebandit", "/leaderboards/bustthrough", "/download", "/help", "/your-data",
            "/store/0", "/store/item/7", "/store/bundle/3", "/profile/Fox"];  // no sales: no item has a struck-through price
        Assert.Equal(expected.Select(p => "http://localhost" + p).Order(), locs.Order());
        Assert.NotEmpty(server.Hosts);
        Assert.All(server.Hosts, h => Assert.Equal("localhost", h));
    }

    static object Resolve(WebApplicationFactory<Resurgence.Web.WebLanguageSwitcher> f, Type service)
    {
        using var scope = f.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService(service);
    }
}
