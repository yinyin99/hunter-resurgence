using System.Globalization;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Ui;
using Resurgence.Ui.Pages;

namespace Resurgence.Tests.WebTests;

public sealed class PageTitleTests : UiContext
{
    sealed class NoSwitch : ILanguageSwitcher { public void Set(string code) { } }

    IRenderedComponent<Settings> RenderSettings() { Services.AddSingleton<ILanguageSwitcher>(new NoSwitch()); return Render<Settings>(); }

    // thread-local cultures only (no Default*): nothing leaks to other tests
    static void In(string culture, string uiCulture, Action a)
    {
        var (c, u) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (new(culture), new(uiCulture));
            a();
        }
        finally { (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (c, u); }
    }

    [Fact]
    public void SettingsTitleUsesTheCulturesUpperCase() =>
        In("tr-TR", "en", () => Assert.Equal("ACCOUNT SETTİNGS", RenderSettings().Find("h2.page-title").TextContent));

    [Fact]
    public void WalletTitleUsesTheCulturesUpperCase() =>
        In("tr-TR", "en", () => Assert.EndsWith("PURCHASE HİSTORY", Render<Wallet>().Find("h2.page-title").TextContent));

    [Fact]
    public void WelcomeTitleUsesTheCulturesUpperCase() =>
        In("tr-TR", "tr", () => Assert.Contains("GELDİNİZ", Render<Welcome>().Find("h2.page-title").TextContent));

    [Fact]
    public void TheDetailsButtonOfABundleUsesTheCulturesUpperCase() =>
        In("tr-TR", "en", () => Assert.Equal("DETAİLS", Render<Store>(p => p.Add(s => s.Key, "bundles")).Find(".card button").TextContent));
}
