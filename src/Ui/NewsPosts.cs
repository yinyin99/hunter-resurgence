namespace Resurgence.Ui;

/// <summary>A post of the feed: its texts are the resx keys <c>News.&lt;Id&gt;.Title</c>, <c>.Text</c> and <c>.Lead</c>; <see cref="LinkKey"/> is the <c>settings.json</c> link its text ends with, <see cref="LinkText"/> the resx key of that link's text; <see cref="Picture"/> the picture slot of its carousel slide.</summary>
public sealed record NewsPost(string Id, DateTime When, string LinkKey, string LinkText, string Picture)
{
    /// <summary>The post's anchor on the feed (<c>news-dev</c>, linked as <c>#news-dev</c>).</summary>
    public string Anchor => "news-" + Id.ToLowerInvariant();
}

/// <summary>The project's own posts, newest first, shown whole on the feed and once each in its carousel. A new post is an entry here, its three resx keys in every translation, and a picture (docs/images.md).</summary>
public static class NewsPosts
{
    // the pictures are two of the operator's competition pictures (a deer each), already on the site
    public static readonly IReadOnlyList<NewsPost> All =
    [
        new("Backup", new(2026, 11, 9, 12, 0, 0), "backup", "Backup.Download", "competition-hero-170031"),
        new("Dev", new(2026, 11, 9, 11, 0, 0), "source", "Feed.DevLink", "competition-hero-170026"),
    ];
}
