namespace Resurgence.Services;

public sealed record NewsPost(string Title, DateTime When, string Slug);

public interface INewsService
{
    Task<IReadOnlyList<NewsPost>> RecentAsync();
}
