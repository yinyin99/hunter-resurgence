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

/// <summary>A shared objective of the event: all players who play events count toward it; <paramref name="Target"/> is already divided by the server's setting.</summary>
/// <param name="Gm">The reward in gm$ every contributor gets when it is done.</param>
public sealed record CommunityMission(string Title, int Progress, int Target, int Gm, int Em);

public sealed record Contributor(string Handle, int Count);

/// <param name="Done">Objectives of the mission finished.</param>
public sealed record EventMission(Mission Mission, int Done);

public sealed record EventItem(int Id, string Name);

/// <summary>What the event panel shows while an event runs for a player who plays events (<see cref="ServerAccountApi.EventPanel"/>).</summary>
public sealed record EventPanelData(string Title, DateTimeOffset Start, DateTimeOffset End, IReadOnlyList<CommunityMission> Community, IReadOnlyList<Contributor> Contributors,
    IReadOnlyList<EventMission> Missions, IReadOnlyList<EventItem> Items);

public interface ISeasonalEventsService
{
    /// <summary>The panel of the event that runs now; null when none runs, the player opted out, or there is no answer. Never throws.</summary>
    Task<EventPanelData?> PanelAsync();

    /// <summary>Never throws: signed out or no answer is the default, no event and the player plays.</summary>
    Task<SeasonalEvents> GetAsync();

    /// <summary>Stores the setting; <see cref="Result.Error"/> is a localisation key.</summary>
    Task<Result> SetAsync(bool play);
}
