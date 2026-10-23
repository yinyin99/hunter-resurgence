namespace Resurgence.Services;

public sealed record Competition(int Id, string Name, DateTime Start, DateTime End);

public interface ICompetitionsService
{
    Task<IReadOnlyList<Competition>> UpcomingAsync();
    Task<IReadOnlyList<Competition>> PreviousAsync();
    Task<Competition?> DetailsAsync(int id);
}
