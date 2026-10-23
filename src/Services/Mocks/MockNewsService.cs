namespace Resurgence.Services.Mocks;

public sealed class MockNewsService : INewsService
{
    public Task<IReadOnlyList<NewsPost>> RecentAsync() =>
        Task.FromResult<IReadOnlyList<NewsPost>>(Enumerable.Range(1, 5)
            .Select(i => new NewsPost($"News post {i}", new(2026, 8, 20 - i, 15, 26, 35), $"news-post-{i}",
                "Placeholder news text for the summary of this post. The real posts come from the news site of the operator and are cut after a few lines. This text only fills the space of three lines in the news list, so that the page has the same height as the original …"))
            .ToList());

    public Task<IReadOnlyList<Featured>> FeaturedAsync() =>
        Task.FromResult<IReadOnlyList<Featured>>([
            new("Featured post", "Message from the team", "Read it here", "news:featured-post", new(2026, 7, 9)),
            new("SALES", "Placeholder sales text!", "Check out the full list!", "/store/sales", new(2026, 7, 10)),
        ]);

    public Task<DiscordInfo> DiscordAsync() =>
        Task.FromResult(new DiscordInfo(952, Enumerable.Range(1, 14).Select(i => new DiscordMember($"Member{i:00}", i % 3 == 0 ? "Some game" : null)).ToList()));
}
