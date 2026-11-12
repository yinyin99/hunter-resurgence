namespace Resurgence.Services.Mocks;

/// <summary>Nothing is installed by a setup: the website and the launcher run from a mock.</summary>
public sealed class MockCleanupService : ICleanupService
{
    public Task<OldSetup?> FindAsync() => Task.FromResult<OldSetup?>(null);
    public Task<Result> CleanAsync(OldSetup found) => Task.FromResult(Result.Success);
}
