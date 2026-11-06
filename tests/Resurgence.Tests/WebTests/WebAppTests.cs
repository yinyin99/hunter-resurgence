using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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
    public void WithServerUrlTheServicesAreTheServers()
    {
        using var withServer = site.WithWebHostBuilder(b => b.UseSetting("SERVER_URL", "http://127.0.0.1:9"));
        Assert.IsType<ServerSessionService>(Resolve(withServer, typeof(ISessionService)));
        Assert.IsType<ServerLeaderboardsService>(Resolve(withServer, typeof(ILeaderboardsService)));
        Assert.IsType<ServerProfileService>(Resolve(withServer, typeof(IProfileService)));
        Assert.IsType<ServerWalletService>(Resolve(withServer, typeof(IWalletService)));
        Assert.IsType<ServerRestoreService>(Resolve(withServer, typeof(IRestoreService)));
    }

    [Fact]
    public void BuyingIsOffOnlyWithServerUrl()
    {
        using var withServer = site.WithWebHostBuilder(b => b.UseSetting("SERVER_URL", "http://127.0.0.1:9"));
        Assert.False(((Resurgence.Ui.HostMode)Resolve(withServer, typeof(Resurgence.Ui.HostMode))).Purchases);
        Assert.True(((Resurgence.Ui.HostMode)Resolve(site, typeof(Resurgence.Ui.HostMode))).Purchases);
    }

    static object Resolve(WebApplicationFactory<Resurgence.Web.WebLanguageSwitcher> f, Type service)
    {
        using var scope = f.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService(service);
    }
}
