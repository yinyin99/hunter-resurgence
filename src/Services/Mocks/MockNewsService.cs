namespace Resurgence.Services.Mocks;

public sealed class MockNewsService : INewsService
{
    public Task<string?> DiscordGuildAsync() => Task.FromResult<string?>(null);
}
