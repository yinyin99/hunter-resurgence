using Resurgence.Ui;
using System.Globalization;

namespace Resurgence.Tests.LauncherApp;

/// <summary>{game} tokens: expanded from settings.json values, "the game" and no link without them, placeholders ignored.</summary>
public class BrandingTests
{
    // the UI culture is set for the thread of one test and put back: other tests read languages by name, never the current one
    [Fact]
    public void GameTokensAndLinksAreExpandedByTheBrandingAndTheLocalizer()
    {
        var b = new Resurgence.Ui.Branding(new() { ["game_name"] = "Foo", ["link_support"] = "https://example.test/n", ["link_forum"] = "<forum>" });
        Assert.True(b.Expand("{GAME} NEWS, {game}, {game_full}") == "FOO NEWS, Foo, Foo", "tokens expand to the configured name");
        Assert.True(b.Link("support") == "https://example.test/n" && b.Link("forum") is null && b.Link("wiki") is null, "links: configured, placeholder and missing");
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

    [Fact]
    public void VariableWinsOnlyWhenSet()
    {
        var b = new Resurgence.Ui.Branding(new() { ["game_name"] = "Foo", ["link_backup"] = "https://example.test/old" });
        var set = b.With("link_backup", "https://example.test/new").With("game_full", "Foo Bar");
        Assert.True(set.Link("backup") == "https://example.test/new" && set.GameName == "Foo" && set.GameFull == "Foo Bar", "the variables win, other keys kept");
        Assert.True(b.With("link_backup", "").Link("backup") == "https://example.test/old" && b.With("game_name", null).GameName == "Foo", "unset keeps settings.json");
        Assert.True(new Resurgence.Ui.Branding().With("game_name", "Baz").Expand("{game_full}") == "Baz", "without settings.json");
    }

    static void English(Action a)
    {
        var old = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        try { a(); }
        finally { CultureInfo.CurrentUICulture = old; }
    }

    [Fact]
    public void ATextWithTokensAndArgumentsFormats() => English(() =>
    {
        var loc = Resurgence.Tests.WebTests.TestLoc.Make(new Resurgence.Ui.Branding(new() { ["game_name"] = "Foo" }));
        Assert.Equal("Congratulations Rex, your brand new Foo account has been created!", loc["Welcome.Congrats", "Rex"].Value);
        Assert.True(loc["No.Such.Key", 1].ResourceNotFound);
    });

    [Fact]
    public void HtmlEncodesTheTextAndKeepsTheGivenTags() => English(() =>
    {
        var loc = Resurgence.Tests.WebTests.TestLoc.Make(new Resurgence.Ui.Branding(new() { ["game_name"] = "Foo" }));
        Assert.Equal("Windows 7, Windows 8/8.1 &amp; Windows 10", loc.Html("Download.Req.OsMulti").Value);
        var tutorials = loc.Html("Help.Tut.Intro", "<a href=\"/t\">", "</a>").Value;
        Assert.Contains("Learn the basics of how to play Foo.", tutorials);
        Assert.Contains("<a href=\"/t\">playable tutorials</a>", tutorials);
        Assert.Contains("&#39;", loc.Html("DayHelp.Intro.1", "<b>", "</b>").Value);
    });

    [Fact]
    public void FragRendersStringHolesAsTextAndFragmentsAsMarkup() => English(() =>
    {
        using var ctx = new Bunit.BunitContext();
        var loc = Resurgence.Tests.WebTests.TestLoc.Make();
        Microsoft.AspNetCore.Components.RenderFragment forum = b => b.AddMarkupContent(0, "<b>forum</b>");
        var html = ctx.Render(loc.Frag("Done.InfoText", "<i>wiki</i>", forum)).Markup;
        Assert.Contains("Check out &lt;i&gt;wiki&lt;/i&gt; for more information.", html);
        Assert.Contains("feel free to ask in the <b>forum</b>.", html);
    });
}
