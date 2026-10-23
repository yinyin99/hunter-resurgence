namespace Resurgence.Services.Mocks;

public sealed class MockGameManagementService : IGameManagementService
{
    // no lodges, as in the old capture
    public Task<IReadOnlyList<ManagedItem>> TrophyLodgeAsync() => Task.FromResult<IReadOnlyList<ManagedItem>>([]);

    public Task<IReadOnlyList<BaitSite>> BaitSitesAsync() =>
        Task.FromResult<IReadOnlyList<BaitSite>>([new(1, "Bait Barrel", "Pine Ridge Trails", -6792.172, 874.903, 11, 50, true)]);

    public Task<Result> RefillAsync(int id, int upTo) => Task.FromResult(Result.Success);
}
