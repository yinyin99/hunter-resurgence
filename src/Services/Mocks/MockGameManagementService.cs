namespace Resurgence.Services.Mocks;

public sealed class MockGameManagementService : IGameManagementService
{
    // no lodges
    public Task<IReadOnlyList<ManagedItem>> TrophyLodgeAsync() => Task.FromResult<IReadOnlyList<ManagedItem>>([]);

    public Task<IReadOnlyList<BaitSite>> BaitSitesAsync() =>
        Task.FromResult<IReadOnlyList<BaitSite>>([new(1, "Bait Barrel", "Pine Ridge Trails", -5000.0, 1000.0, 11, 50, true)]);

    public Task<Result> RefillAsync(int id, int upTo) => Task.FromResult(Result.Success);
}
