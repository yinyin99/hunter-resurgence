namespace Resurgence.Services.Mocks;

public sealed class MockGameManagementService : IGameManagementService
{
    public Task<IReadOnlyList<ManagedItem>> TrophyLodgeAsync() =>
        Task.FromResult<IReadOnlyList<ManagedItem>>([new(1, "Trophy One"), new(2, "Trophy Two")]);

    public Task<IReadOnlyList<ManagedItem>> BaitSitesAsync() =>
        Task.FromResult<IReadOnlyList<ManagedItem>>([new(1, "Bait Site One")]);
}
