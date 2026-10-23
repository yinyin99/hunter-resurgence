namespace Resurgence.Services;

public sealed record Species(int Id, string Name, int Competitions);

/// <param name="Icon">board (score) or weight, the two little pictures of the lists.</param>
/// <param name="Joinable">False shows "Can't join".</param>
/// <remarks>Finished competitions have no dates and no status.</remarks>
public sealed record Competition(int Id, string Name, string Icon, IReadOnlyList<string> Tags, string Description, string? Requirements, int Players, DateTime? Start, DateTime? End, string? Status, bool Joinable);

/// <summary>A competition the player has activated.</summary>
public sealed record Enrolled(Competition Competition, int Used, int Max, int? Position);

/// <param name="EndsIn">Seconds left.</param>
public sealed record CompetitionDetails(Competition Competition, bool Active, string Tagline, int EndsIn, int? Position, string ScoreToBeat, int Used, int Max,
    IReadOnlyList<string> Prizes, string Species, string Reserves, string Description, string Weapons, string Scoring, string Special, string Gamemode);

public sealed record Participant(int Position, string Handle, string Result);

/// <param name="Runs">Day of the month and competition id of each earlier run.</param>
public sealed record HistoryMonth(int Year, string Month, IReadOnlyList<(int Day, int Id)> Runs);

public interface ICompetitionsService
{
    Task<IReadOnlyList<Enrolled>> EnrolledAsync();
    Task<IReadOnlyList<Species>> SpeciesAsync();
    /// <param name="species">Only the competitions of this species; null for all.</param>
    Task<IReadOnlyList<Competition>> UpcomingAsync(int? species);
    Task<Page<Competition>> PreviousAsync(int page);
    Task<CompetitionDetails?> DetailsAsync(int id);
    Task<IReadOnlyList<Participant>> ParticipantsAsync(int id);
    Task<IReadOnlyList<HistoryMonth>> HistoryAsync(int id);
    /// <summary>Activates (join) or deactivates (leave) the competition. Fake in the mock.</summary>
    Task<Result> JoinAsync(int id);
    Task<Result> LeaveAsync(int id);
}
