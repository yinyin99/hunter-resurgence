using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;
using Resurgence.Ui;
using Resurgence.Ui.Pages;

namespace Resurgence.Tests.WebTests;

/// <summary>The feed shows the project's three posts whole, newest first; the carousel has a slide for each, linking to it on the same page.</summary>
public sealed class NewsFeedTests : UiContext
{
    [Fact]
    public void TheFeedShowsTheLauncherPostThenTheBackupPostThenTheDevPostEachWithItsLink()
    {
        Services.AddSingleton(new Branding(new() { ["link_backup"] = "https://example.test/backup.zip", ["link_source"] = "https://example.test/repo" }));
        var cut = Render<Feed>();

        var posts = cut.FindAll(".news article");
        Assert.Equal(["news-launcher031", "news-backup", "news-dev"], posts.Select(a => a.Id));
        Assert.Equal(["https://example.test/repo", "https://example.test/backup.zip", "https://example.test/repo"], posts.Select(a => a.QuerySelector("p.excerpt a")!.GetAttribute("href")));
        Assert.Empty(cut.FindAll(".archive"));
    }

    [Fact]
    public void TheCarouselHasOneSlideForEachPostLinkingToIt()
    {
        var cut = Render<FeedCarousel>();

        Assert.Equal("#news-launcher031", cut.Find(".caption a").GetAttribute("href"));
        Assert.Equal(3, cut.FindAll(".thumb").Count);

        cut.FindAll(".thumb")[2].Click();

        Assert.Equal("#news-dev", cut.Find(".caption a").GetAttribute("href"));
    }

    [Fact]
    public async Task ThePlayButtonOfThePublicHeroOpensTheLauncherAndFallsBackToTheDownloadPage()
    {
        await Services.GetRequiredService<ISessionService>().SignOutAsync();
        var play = Render<Feed>().Find("a.hero-button");

        Assert.Equal("/download", play.GetAttribute("href"));
        Assert.Equal("hunter-resurgence://open", play.GetAttribute("data-launch"));
    }
}
