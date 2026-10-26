using System.Globalization;

namespace Resurgence.Tests.LauncherApp;

/// <summary>{game} tokens: expanded from settings.json values, "the game" and no link without them, placeholders ignored.</summary>
public class BrandingTests
{
    // the UI culture is set for the thread of one test and put back: other tests read languages by name, never the current one
    [Fact]
    public void GameTokensAndLinksAreExpandedByTheBrandingAndTheLocalizer()
    {
        var b = new Resurgence.Ui.Branding(new() { ["game_name"] = "Foo", ["link_news"] = "https://example.test/n", ["link_forum"] = "<forum>" });
        Assert.True(b.Expand("{GAME} NEWS, {game}, {game_full}") == "FOO NEWS, Foo, Foo", "tokens expand to the configured name");
        Assert.True(b.Link("news") == "https://example.test/n" && b.Link("forum") is null && b.Link("wiki") is null, "links: configured, placeholder and missing");
        Assert.True(new Resurgence.Ui.Branding().Expand("FOLLOW {GAME} / {game}") == "FOLLOW THE GAME / the game", "no settings: the game");
        var old = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        try
        {
            var loc = new Resurgence.Ui.Loc(new Microsoft.Extensions.Localization.ResourceManagerStringLocalizerFactory(
                Microsoft.Extensions.Options.Options.Create(new Microsoft.Extensions.Localization.LocalizationOptions()), Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance), b);
            Assert.True(loc["Feed.News"].Value == "FOO NEWS" && loc["Menu.News"].Value == "NEWS" && loc["No.Such.Key"].ResourceNotFound, "localizer expands the tokens and reports missing keys");
        }
        finally { CultureInfo.CurrentUICulture = old; }
    }
}
