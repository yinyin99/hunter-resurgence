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

    sealed class ServerStub(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
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

    // the landing page with a fake answer of the server: its post and slide, not the mock's five posts
    [Fact]
    public async Task TheFeedShowsTheServersNewsNotTheMocks()
    {
        const string reply = """{"posts": [{"title": "Test Post", "date": "2026-10-29T14:05:00", "slug": "test-post", "excerpt": "Big news."}], "featured": [{"title": "Test Slide", "lead": "", "linkText": "Go", "target": "/store/sales", "date": "2026-01-01T00:00:00"}]}""";
        using var withServer = site.WithWebHostBuilder(b => b.UseSetting("SERVER_URL", "http://server.test")
            .ConfigureTestServices(s => s.AddHttpClient("server").ConfigurePrimaryHttpMessageHandler(() => new ServerStub(reply))));
        using var client = withServer.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

        var html = await Html(await Get(client, "/feed"));

        Assert.Contains("Test Post", html);
        Assert.Contains("Test Slide", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<article"));
    }

    static object Resolve(WebApplicationFactory<Resurgence.Web.WebLanguageSwitcher> f, Type service)
    {
        using var scope = f.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService(service);
    }
}
