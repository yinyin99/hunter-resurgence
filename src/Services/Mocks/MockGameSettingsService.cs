using Microsoft.Extensions.Logging;

namespace Resurgence.Services.Mocks;

/// <param name="mock">"settings:first" makes the first game start ask for the settings.</param>
public sealed class MockGameSettingsService(ILogger<MockGameSettingsService> log, string? mock = null) : IGameSettingsService
{
    static readonly Resolution[] Modes = [new(640, 480, 60), new(800, 600, 60), new(1024, 768, 60), new(1280, 720, 60), new(1366, 768, 60), new(1600, 900, 60), new(1920, 1080, 60), new(1920, 1080, 144), new(2560, 1440, 60)];

    GameOptions options = new(false, false, new(1920, 1080, 60), 2, 0, 0, 0, 0, false, false);
    bool first = mock == "settings:first";

    public Task<GameSettings> ReadAsync() => Task.FromResult(new GameSettings(options, Modes, 8192));

    public Task<Result> SaveAsync(GameOptions o)
    {
        log.LogInformation("save game settings {Options}", o);
        (options, first) = (o, false);
        return Task.FromResult(Result.Success);
    }

    public Task<bool> FirstStartAsync() => Task.FromResult(first);
}
