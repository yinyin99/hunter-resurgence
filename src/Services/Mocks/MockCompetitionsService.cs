namespace Resurgence.Services.Mocks;

public sealed class MockCompetitionsService : ICompetitionsService
{
    static readonly Competition[] All = Enumerable.Range(1, 6).Select(i => new Competition(i, $"Competition {i}", new(2026, i, 1), new(2026, i, 8))).ToArray();

    public Task<IReadOnlyList<Competition>> UpcomingAsync() => Task.FromResult<IReadOnlyList<Competition>>(All.Take(3).ToList());
    public Task<IReadOnlyList<Competition>> PreviousAsync() => Task.FromResult<IReadOnlyList<Competition>>(All.Skip(3).ToList());
    public Task<Competition?> DetailsAsync(int id) => Task.FromResult(All.FirstOrDefault(c => c.Id == id));
}
