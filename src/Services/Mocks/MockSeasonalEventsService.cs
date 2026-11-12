namespace Resurgence.Services.Mocks;

/// <summary>The setting is kept in memory. Mock "events:running" has a Halloween 2026 event on the game's reserves 0, 1, 2, 8 and 9.</summary>
public sealed class MockSeasonalEventsService(string? mock = null) : ISeasonalEventsService
{
    bool play = true;

    public Task<SeasonalEvents> GetAsync() => Task.FromResult(mock == "events:running" ? new SeasonalEvents(play, "halloween_2026", [0, 1, 2, 8, 9]) : new SeasonalEvents(play, null, []));

    public Task<Result> SetAsync(bool value)
    {
        play = value;
        return Task.FromResult(Result.Success);
    }
}
