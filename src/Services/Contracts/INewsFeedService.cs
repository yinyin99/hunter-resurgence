namespace Resurgence.Services;

/// <summary>A post of the feed, in one language. <see cref="Body"/> is Markdown (<c>NewsMarkdown</c> of src/Ui draws it, never as HTML). <see cref="Link"/> is an https address or <c>link:&lt;key&gt;</c>, a link of the project's own in settings.json; it comes with its
/// <see cref="LinkText"/>. <see cref="Picture"/> is the address of the slide's picture, or <c>slot:&lt;name&gt;</c> for a picture slot of the website and the launcher (docs/images.md); null: the default slide picture.</summary>
public sealed record NewsPost(long Id, DateTimeOffset Published, bool Pinned, string Title, string Body, string? Link, string? LinkText, string? Picture)
{
    /// <summary>The post's anchor on the feed (<c>news-3</c>, linked as <c>#news-3</c>).</summary>
    public string Anchor => "news-" + Id;

    /// <summary>The start of a <see cref="Picture"/> that names a picture slot.</summary>
    public const string SlotPrefix = "slot:";
}

/// <summary>The feed of the main page and the launcher's start page: the posts the operator published on the community server (docs/server.md#news).</summary>
public interface INewsFeedService
{
    /// <summary>The posts that show now, pinned first, then newest first, in <paramref name="language"/> (a code such as <c>fr</c> or <c>pt-BR</c>; English where a post has no text in it). Empty when there are none or the server does not answer. Never throws.</summary>
    Task<IReadOnlyList<NewsPost>> PostsAsync(string language);
}
