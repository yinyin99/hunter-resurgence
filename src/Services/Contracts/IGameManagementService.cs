namespace Resurgence.Services;

public sealed record ManagedItem(int Id, string Name);

public interface IGameManagementService
{
    Task<IReadOnlyList<ManagedItem>> TrophyLodgeAsync();
    Task<IReadOnlyList<ManagedItem>> BaitSitesAsync();
}
