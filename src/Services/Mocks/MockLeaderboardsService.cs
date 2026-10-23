namespace Resurgence.Services.Mocks;

public sealed class MockLeaderboardsService : ILeaderboardsService
{
    public Task<IReadOnlyList<LeaderRow>> RowsAsync(string board, int offset) =>
        Task.FromResult<IReadOnlyList<LeaderRow>>(Enumerable.Range(offset + 1, 25).Select(r => new LeaderRow(r, $"Hunter{r:00}", 1000.0 / r)).ToList());
}
