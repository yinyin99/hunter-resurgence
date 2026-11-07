namespace Resurgence.Services;

public sealed record NewsPost(string Title, DateTime When, string Slug, string Excerpt);

/// <summary>A slide of the feed carousel. <paramref name="Target"/> is an app path (/store/sales) or news:&lt;slug&gt; for a news post; <paramref name="Image"/> the name of the
/// community server's picture for it (the mock has none: its pictures are named after the target).</summary>
public sealed record Featured(string Title, string Lead, string LinkText, string Target, DateTime When, string? Image = null);

public sealed record DiscordMember(string Name, string? Playing);
public sealed record DiscordInfo(int Online, IReadOnlyList<DiscordMember> Members);

public interface INewsService
{
    Task<IReadOnlyList<NewsPost>> RecentAsync();
    Task<IReadOnlyList<Featured>> FeaturedAsync();
    /// <summary>The Discord widget, or null for none.</summary>
    Task<DiscordInfo?> DiscordAsync();
}
