namespace Resurgence.Services.Mocks;

/// <summary>Fixed fictional counts.</summary>
public sealed class MockOnlineService : IOnlineService
{
    public Task<OnlineCounts?> NowAsync() => Task.FromResult<OnlineCounts?>(new(37, 4));
}
