namespace Resurgence.Services;

public sealed record ManagedItem(int Id, string Name);

/// <summary>A bait site: <see cref="Group"/> is the kind of container (the heading), <see cref="Max"/> what it holds.</summary>
public sealed record BaitSite(int Id, string Group, string Name, double X, double Y, int Bait, int Max, bool Active);

public interface IGameManagementService
{
    Task<IReadOnlyList<ManagedItem>> TrophyLodgeAsync();
    Task<IReadOnlyList<BaitSite>> BaitSitesAsync();
    /// <summary>Fills the container up to <paramref name="upTo"/> units. Fake in the mock.</summary>
    Task<Result> RefillAsync(int id, int upTo);
}
