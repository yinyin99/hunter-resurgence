namespace Resurgence.Services;

public sealed record NewsPost(string Title, DateTime When, string Slug, string Excerpt);

/// <summary>A slide of the feed carousel. <paramref name="Target"/> is an app path (/store/sales) or news:&lt;slug&gt; for a news post.</summary>
public sealed record Featured(string Title, string Lead, string LinkText, string Target, DateTime When);

public sealed record DiscordMember(string Name, string? Playing);
public sealed record DiscordInfo(int Online, IReadOnlyList<DiscordMember> Members);

public interface INewsService
{
    Task<IReadOnlyList<NewsPost>> RecentAsync();
    Task<IReadOnlyList<Featured>> FeaturedAsync();
    Task<DiscordInfo> DiscordAsync();
}
