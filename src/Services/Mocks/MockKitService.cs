namespace Resurgence.Services.Mocks;

/// <summary>No community server to set up for: the website and the launcher without server_url.</summary>
public sealed class MockKitService : IKitService
{
    public bool Available => false;
    public string? Problem => null;
    public Task<KitPlan?> StartAsync() => Task.FromResult<KitPlan?>(null);
    public KitPlan Plan() => throw new NotSupportedException();
    public Task<Result> SetUpAsync() => Task.FromResult(Result.Success);
    public Task<Result> RemoveAsync() => Task.FromResult(Result.Success);
}
