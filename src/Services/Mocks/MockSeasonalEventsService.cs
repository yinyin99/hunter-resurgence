namespace Resurgence.Services.Mocks;

/// <summary>The setting is kept in memory. Mock "events:running" has a Halloween 2026 event on the game's reserves 0, 1, 2, 8 and 9.</summary>
public sealed class MockSeasonalEventsService(string? mock = null) : ISeasonalEventsService
{
    bool play = true;

    public Task<SeasonalEvents> GetAsync() => Task.FromResult(mock == "events:running" ? new SeasonalEvents(play, "halloween_2026", [0, 1, 2, 8, 9]) : new SeasonalEvents(play, null, []));

    /// <summary>Made-up numbers; the panel shows only for mock "events:running".</summary>
    public Task<EventPanelData?> PanelAsync() => Task.FromResult<EventPanelData?>(mock == "events:running" && play
        ? new("Halloween 2026", new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero), new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero),
            [new("Harvest 40 Mock Deer", 12, 40, 500, 10), new("Spot 100 Mock Foxes", 100, 100, 200, 0)],
            [new("MockHunter", 9), new("OtherHunter", 4)],
            [new(new(1201, "Mock Pursuits", "active", "", ["Harvest a Mock Deer.", "Harvest a Mock Fox."], "1000 gm$", "", "", ""), 1)],
            [new(962, "Mock Silver Bullets")])
        : null);

    public Task<Result> SetAsync(bool value)
    {
        play = value;
        return Task.FromResult(Result.Success);
    }
}
