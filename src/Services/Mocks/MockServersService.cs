using Microsoft.Extensions.Logging;

namespace Resurgence.Services.Mocks;

public sealed class MockServersService(ILogger<MockServersService> log) : IServersService
{
    public Task<IReadOnlyList<Reserve>> ReservesAsync() =>
        Task.FromResult<IReadOnlyList<Reserve>>(Enumerable.Range(1, 12).Select(i => new Reserve(i, $"Reserve {i:00}")).ToList());

    public Task<IReadOnlyList<GameServer>> ServersAsync() =>
        Task.FromResult<IReadOnlyList<GameServer>>(Enumerable.Range(1, 5).Select(i => new GameServer($"Hunter{i:00}'s game", i, 1, 8, $"Hunter{i:00}", i == 1)).ToList());

    public Task<Result> StartAsync(int reserve, bool multiplayer)
    {
        log.LogInformation("start {Mode} game on reserve {Reserve}", multiplayer ? "multiplayer" : "single player", reserve);
        return Task.FromResult(Result.Success);
    }
}
