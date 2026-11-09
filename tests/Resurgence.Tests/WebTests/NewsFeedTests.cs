using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Ui;
using Resurgence.Ui.Pages;

namespace Resurgence.Tests.WebTests;

/// <summary>The feed shows the project's two posts whole, newest first; the carousel has a slide for each, linking to it on the same page.</summary>
public sealed class NewsFeedTests : UiContext
{
    [Fact]
    public void TheFeedShowsTheBackupPostThenTheDevPostEachWithItsLink()
    {
        Services.AddSingleton(new Branding(new() { ["link_backup"] = "https://example.test/backup.zip", ["link_source"] = "https://example.test/repo" }));
        var cut = Render<Feed>();

        var posts = cut.FindAll(".news article");
        Assert.Equal(["news-backup", "news-dev"], posts.Select(a => a.Id));
        Assert.Equal(["https://example.test/backup.zip", "https://example.test/repo"], posts.Select(a => a.QuerySelector("p.excerpt a")!.GetAttribute("href")));
        Assert.Empty(cut.FindAll(".archive"));
    }

    [Fact]
    public void TheCarouselHasOneSlideForEachPostLinkingToIt()
    {
        var cut = Render<FeedCarousel>();

        Assert.Equal("#news-backup", cut.Find(".caption a").GetAttribute("href"));
        Assert.Equal(2, cut.FindAll(".thumb").Count);

        cut.FindAll(".thumb")[1].Click();

        Assert.Equal("#news-dev", cut.Find(".caption a").GetAttribute("href"));
    }
}
