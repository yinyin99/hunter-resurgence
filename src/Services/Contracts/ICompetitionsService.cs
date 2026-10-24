namespace Resurgence.Services;

public sealed record Species(int Id, string Name, int Competitions);

/// <param name="Icon">board (score) or weight, the two little pictures of the lists.</param>
/// <param name="Joinable">False shows "Can't join".</param>
/// <param name="Enrolled">The player has activated it: the lists show Deactivate instead of Activate.</param>
/// <remarks>Finished competitions have no dates and no status.</remarks>
public sealed record Competition(int Id, string Name, string Icon, IReadOnlyList<string> Tags, string Description, string? Requirements, int Players, DateTime? Start, DateTime? End, string? Status, bool Joinable, bool Enrolled = false);

/// <summary>A competition the player has activated.</summary>
public sealed record Enrolled(Competition Competition, int Used, int Max, int? Position);

/// <summary>What a place wins: em$ and gm$ amounts (0 for none) and a trophy.</summary>
public sealed record Prize(int Ems, int Gms, bool Trophy);

/// <remarks>How long it is to the start or the end follows from the dates of <see cref="Competition"/>.</remarks>
public sealed record CompetitionDetails(Competition Competition, bool Active, string Tagline, int? Position, string ScoreToBeat, int Used, int Max,
    IReadOnlyList<Prize> Prizes, string Species, string Reserves, string Description, string Weapons, string Scoring, string Special, string Gamemode);

public sealed record Participant(int Position, string Handle, string Result);

/// <param name="Runs">Day of the month and competition id of each earlier run.</param>
public sealed record HistoryMonth(int Year, string Month, IReadOnlyList<(int Day, int Id)> Runs);

/// <summary>Competitions and the community league; a league competition has the same details, leaderboard, activate and leave calls under its own id.</summary>
public interface ICompetitionsService
{
    /// <summary>Rows of a competition's leaderboard page.</summary>
    const int ParticipantsPageSize = 20;
    /// <summary>Id of the synthetic species of the filter for competitions with more than one species.</summary>
    const int Multispecies = 100;

    Task<IReadOnlyList<Enrolled>> EnrolledAsync();
    Task<IReadOnlyList<Species>> SpeciesAsync();
    /// <param name="species">Only the competitions of this species; null for all.</param>
    Task<IReadOnlyList<Competition>> UpcomingAsync(int? species);
    Task<Page<Competition>> PreviousAsync(int page);
    Task<CompetitionDetails?> DetailsAsync(int id);
    Task<Page<Participant>> ParticipantsAsync(int id, int page);
    Task<IReadOnlyList<HistoryMonth>> HistoryAsync(int id);
    /// <summary>Activates (join) or deactivates (leave) the competition. Fake in the mock.</summary>
    Task<Result> JoinAsync(int id);
    Task<Result> LeaveAsync(int id);
    Task<IReadOnlyList<Competition>> LeagueAsync();
    Task<IReadOnlyList<Enrolled>> LeagueEnrolledAsync();
    /// <summary>The best players of the league for the month (or for the whole season).</summary>
    Task<IReadOnlyList<Ranked>> LeagueBoardAsync(bool season);
}
