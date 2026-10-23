namespace Resurgence.Services;

public sealed record Reserve(int Id, string Name);
public sealed record GameServer(string Name, int Reserve, int Players, int Max, string Host, bool Password);

public interface IServersService
{
    Task<IReadOnlyList<Reserve>> ReservesAsync();
    Task<IReadOnlyList<GameServer>> ServersAsync();
    Task<Result> StartAsync(int reserve, bool multiplayer);
}
