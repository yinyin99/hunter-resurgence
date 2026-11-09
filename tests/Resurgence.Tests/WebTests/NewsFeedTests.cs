using Bunit;
using Resurgence.Ui.Pages;

namespace Resurgence.Tests.WebTests;

/// <summary>The feed shows the project's two posts whole, newest first; the carousel has a slide for each, linking to it on the same page.</summary>
public sealed class NewsFeedTests : UiContext
{
    [Fact]
    public void TheFeedShowsTheBackupPostThenTheDevPostEachWithItsLink()
    {
        var cut = Render<Feed>();

        Assert.Equal(["news-backup", "news-dev"], cut.FindAll(".news article").Select(a => a.Id));
        Assert.All(cut.FindAll(".news article"), a => Assert.Single(a.QuerySelectorAll("p.excerpt")));
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
