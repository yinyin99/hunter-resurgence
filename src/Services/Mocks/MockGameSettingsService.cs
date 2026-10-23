using Microsoft.Extensions.Logging;

namespace Resurgence.Services.Mocks;

public sealed class MockGameSettingsService(ILogger<MockGameSettingsService> log) : IGameSettingsService
{
    GameOptions options = new(false, true, 1920, 1080, 60);

    public Task<GameOptions> ReadAsync() => Task.FromResult(options);

    public Task<Result> SaveAsync(GameOptions o)
    {
        log.LogInformation("save game settings {Options}", o);
        options = o;
        return Task.FromResult(Result.Success);
    }
}
