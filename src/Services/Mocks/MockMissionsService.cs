namespace Resurgence.Services.Mocks;

public sealed class MockMissionsService : IMissionsService
{
    public Task<IReadOnlyList<Mission>> DailyAsync() =>
        Task.FromResult<IReadOnlyList<Mission>>([new(1, "Daily Mission One", 0, 3), new(2, "Daily Mission Two", 1, 2)]);

    public Task<IReadOnlyList<Mission>> ActiveAsync() =>
        Task.FromResult<IReadOnlyList<Mission>>([new(3, "Mission One", 2, 5), new(4, "Mission Two", 0, 1)]);
}
