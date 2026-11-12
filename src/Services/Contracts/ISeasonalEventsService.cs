namespace Resurgence.Services;

/// <summary>The player's seasonal events setting and the event that runs now (docs/server.md#seasonal-events).</summary>
/// <param name="Play">The player plays seasonal events (on by default, also when unknown).</param>
/// <param name="Event">The name of the event that runs now, null for none.</param>
/// <param name="Reserves">The game's reserve numbers the event covers (the launch map's reserve id minus 1); empty: every reserve.</param>
public sealed record SeasonalEvents(bool Play, string? Event, IReadOnlyList<int> Reserves)
{
    /// <summary>The player sees the event's picture of the launch map's reserve <paramref name="reserve"/>: the event runs, covers it and the player did not opt out.</summary>
    public bool Shows(int reserve) => Play && Event is not null && (Reserves.Count == 0 || Reserves.Contains(reserve - 1));
}

public interface ISeasonalEventsService
{
    /// <summary>Never throws: signed out or no answer is the default, no event and the player plays.</summary>
    Task<SeasonalEvents> GetAsync();

    /// <summary>Stores the setting; <see cref="Result.Error"/> is a localisation key.</summary>
    Task<Result> SetAsync(bool play);
}
