namespace Resurgence.Services.Mocks;

public sealed class MockNewsService : INewsService
{
    public Task<IReadOnlyList<NewsPost>> RecentAsync() =>
        Task.FromResult<IReadOnlyList<NewsPost>>(Enumerable.Range(1, 5).Select(i => new NewsPost($"News post {i}", new(2026, 8, 20 - i), $"news-post-{i}")).ToList());
}
