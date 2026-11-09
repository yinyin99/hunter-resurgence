namespace Resurgence.Services.Mocks;

public sealed class MockNewsService : INewsService
{
    public Task<DiscordInfo?> DiscordAsync() =>
        Task.FromResult<DiscordInfo?>(new DiscordInfo(952, Enumerable.Range(1, 14).Select(i => new DiscordMember($"Member{i:00}", i % 3 == 0 ? "Some game" : null)).ToList()));
}
