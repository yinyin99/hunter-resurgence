using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;
using Resurgence.Ui;
using Resurgence.Ui.Components;
using Resurgence.Ui.Pages;

namespace Resurgence.Tests.WebTests;

/// <summary>The feed shows the server's posts whole, in the order the server sent them; the carousel has a slide for each, linking to it on the same page.</summary>
public sealed class NewsFeedTests : UiContext
{
    sealed class Fixed(params NewsPost[] posts) : INewsFeedService
    {
        public string? Asked { get; private set; }

        public Task<IReadOnlyList<NewsPost>> PostsAsync(string language)
        {
            Asked = language;
            return Task.FromResult<IReadOnlyList<NewsPost>>(posts);
        }
    }

    static NewsPost Post(long id, string title, string body, string? link = null, string? linkText = null, string? picture = null) =>
        new(id, new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.Zero), false, title, body, link, linkText, picture);

    [Fact]
    public void TheFeedShowsTheMockPostsNewestFirstEachWithItsLink()
    {
        Services.AddSingleton(new Branding(new() { ["link_backup"] = "https://example.test/backup.zip", ["link_source"] = "https://example.test/repo" }));
        var cut = Render<Feed>();

        var posts = cut.FindAll(".news article");
        Assert.Equal(["news-3", "news-2", "news-1"], posts.Select(a => a.Id));
        Assert.Equal(["https://example.test/repo", "https://example.test/backup.zip", "https://example.test/repo"], posts.Select(a => a.QuerySelector("p.excerpt a")!.GetAttribute("href")));
        Assert.Empty(cut.FindAll(".archive"));
    }

    [Fact]
    public void ThePostsAreAskedInTheLanguageOfTheReaderAndTheirTextsAreDrawnWithoutHtml()
    {
        var service = new Fixed(Post(7, "Event {game}", "First **bold** and *italic* with [a link](https://example.test/x).\n\n- one\n- two\n\n<script>alert(1)</script>", "https://example.test/event", "Join in"));
        Services.AddSingleton<INewsFeedService>(service);
        Services.AddSingleton(new Branding(new() { ["game_name"] = "Testgame" }));
        var cut = Render<Feed>();

        Assert.Equal(System.Globalization.CultureInfo.CurrentUICulture.Name, service.Asked);
        var article = cut.Find("#news-7");
        Assert.Equal("Event Testgame", article.QuerySelector("h4")!.TextContent);
        Assert.Equal("bold", article.QuerySelector("strong")!.TextContent);
        Assert.Equal(["one", "two"], article.QuerySelectorAll("li").Select(li => li.TextContent));
        Assert.Equal(["https://example.test/x", "https://example.test/event"], article.QuerySelectorAll("a").Select(a => a.GetAttribute("href")));
        Assert.Empty(article.QuerySelectorAll("script"));
        Assert.Contains("<script>alert(1)</script>", article.QuerySelectorAll("p")[2].TextContent);
    }

    [Fact]
    public void ALinkOfTheProjectIsPlainTextWithoutItsSettingAndAnUnsafeLinkIsNotDrawn()
    {
        Services.AddSingleton<INewsFeedService>(new Fixed(Post(1, "A", "a", "link:nothing", "Own link"), Post(2, "B", "b", "javascript:alert(1)", "Bad link")));
        var cut = Render<Feed>();

        Assert.Empty(cut.FindAll(".news article a"));
        Assert.Equal("Own link", cut.Find("#news-1 p.excerpt").TextContent.Trim());
        Assert.Empty(cut.FindAll("#news-2 p.excerpt").Where(p => p.TextContent.Contains("Bad link")));
    }

    [Fact]
    public void WithoutPostsTheFeedHasNoCarouselAndNoHeading()
    {
        Services.AddSingleton<INewsFeedService>(new Fixed());
        var cut = Render<Feed>();

        Assert.Empty(cut.FindAll(".carousel"));
        Assert.Empty(cut.FindAll(".news article"));
    }

    [Fact]
    public async Task TheCarouselHasOneSlideForEachPostLinkingToIt()
    {
        var posts = await Services.GetRequiredService<INewsFeedService>().PostsAsync("en");
        var cut = Render<FeedCarousel>(p => p.Add(c => c.Posts, posts));

        Assert.Equal("#news-3", cut.Find(".caption a").GetAttribute("href"));
        Assert.Equal(3, cut.FindAll(".thumb").Count);

        cut.FindAll(".thumb")[2].Click();

        Assert.Equal("#news-1", cut.Find(".caption a").GetAttribute("href"));
    }

    [Fact]
    public void ASlideShowsItsPictureAndAPostWithoutOneTheDefaultSlidePicture()
    {
        var cut = Render<FeedCarousel>(p => p.Add(c => c.Posts, new[] { Post(1, "A", "One. Two.", picture: "/news/1/picture?v=5"), Post(2, "B", "b") }));

        Assert.Equal("/news/1/picture?v=5", cut.Find(".slide img").GetAttribute("src"));
        Assert.Contains("One.", cut.Find(".caption p").TextContent);
        Assert.DoesNotContain("Two.", cut.Find(".caption p").TextContent);  // the lead is the first sentence
        Assert.Equal(["/news/1/picture?v=5"], cut.FindAll(".thumb img").Select(i => i.GetAttribute("src")));
        Assert.Equal(1, cut.FindAll(".thumb span.asset-slot").Count);  // the second post: the box of the slot news-default

        cut.FindAll(".thumb")[1].Click();

        Assert.Empty(cut.FindAll(".slide img"));
        Assert.Equal("news-default", cut.Find(".slide .asset-slot").GetAttribute("aria-label"));
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

/// <summary>The body of a post: paragraphs, bold, italic, links and lists, and nothing else; no HTML ever reaches the page.</summary>
public sealed class NewsMarkdownTests : UiContext
{
    string Html(string markdown) => Render<NewsBody>(p => p.Add(c => c.Text, markdown)).Markup;

    [Fact]
    public void ParagraphsAreSplitByBlankLinesAndALineBreakInsideOneIsASpace()
    {
        var blocks = NewsMarkdown.Parse("one\ntwo\r\n\r\n\r\nthree");

        Assert.Equal(2, blocks.Count);
        Assert.Equal("one two", ((NewsMarkdown.Plain)((NewsMarkdown.Paragraph)blocks[0]).Content.Single()).Text);
    }

    [Fact]
    public void BoldItalicAndLinksAreRead()
    {
        var html = Html("**bold** *it* _it2_ [site](https://example.test/a?b=1&c=2)");

        Assert.Contains("<strong>bold</strong>", html);
        Assert.Contains("<em>it</em>", html);
        Assert.Contains("<em>it2</em>", html);
        Assert.Contains("href=\"https://example.test/a?b=1&amp;c=2\"", html);
        Assert.Contains("rel=\"noopener\"", html);
    }

    [Fact]
    public void ListsAreBulletedOrNumberedAndEndAtABlankLineOrAParagraph()
    {
        var cut = Render<NewsBody>(p => p.Add(c => c.Text, "- a\n* b\ntext\n\n1. x\n2) y"));

        Assert.Equal(["a", "b"], cut.FindAll("ul li").Select(li => li.TextContent));
        Assert.Equal(["text"], cut.FindAll("p").Select(p => p.TextContent));
        Assert.Equal(["x", "y"], cut.FindAll("ol li").Select(li => li.TextContent));
    }

    [Fact]
    public void NoScriptAndNoRawHtmlReachThePage()
    {
        var html = Html("<script>alert(1)</script> <b onclick=\"x()\">bold</b>\n\n<img src=x onerror=alert(1)>\n\n[click](javascript:alert(1)) [data](data:text/html,x) [rel](/relative) [odd]( https://example.test )");

        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<b ", html);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("href=\"javascript", html);
        Assert.DoesNotContain("href=\"data:", html);
        Assert.DoesNotContain("href=\"/relative", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.Contains("[click](javascript:alert(1))", html);
    }

    [Fact]
    public void ABackslashMakesTheNextCharacterPlainAndWordsKeepTheirUnderscores()
    {
        var html = Html(@"\*not bold\* snake_case_name 2 * 3 * 4 **unclosed");

        Assert.DoesNotContain("<em>", html);
        Assert.DoesNotContain("<strong>", html);
        Assert.Contains("*not bold* snake_case_name 2 * 3 * 4 **unclosed", html);
    }

    [Fact]
    public void TheLeadIsTheFirstSentenceAsPlainTextAndLongOnesAreCut()
    {
        Assert.Equal("The launcher now checks.", NewsMarkdown.Lead("The launcher **now** checks. It also reports.\n\nSecond."));
        Assert.Equal("Version 0.31 is out", NewsMarkdown.Lead("Version 0.31 is out"));
        Assert.Equal("one.", NewsMarkdown.Lead("- one. two\n- three"));
        var cut = NewsMarkdown.Lead(string.Join(' ', Enumerable.Repeat("word", 60)), 40);
        Assert.True(cut.Length <= 40 && cut.EndsWith("word…"), cut);
        Assert.Equal("", NewsMarkdown.Lead(""));
    }
}
