namespace Resurgence.Services;

public sealed record Reserve(int Id, string Name);
public enum MarkerKind { Lodge, Tent, Campsite, Tower, Interest, Bait }
/// <summary>A place on a reserve map; X and Y are percent of the map pane.</summary>
public sealed record MapMarker(int Id, MarkerKind Kind, string Name, int X, int Y);
public sealed record GamePlayer(string Handle, int HunterScore);
public enum Distance { Short, Medium, Long }
public sealed record GameServer(int Id, string Name, string Description, int Reserve, int Players, int Max, string Host, bool Password, string Country, Distance Distance,
    int Seconds, int MinScore, int MaxScore, IReadOnlyList<GamePlayer> Roster);
public sealed record NewGame(string Name, string Description, bool FriendsOnly, string? Password, int MaxPlayers, int? MinScore, int? MaxScore);
public sealed record Tutorial(int Id, string Title, string Description, int Reserve);

/// <summary>The hunt screens: lobby, create game, server list, join map, tutorials. Starting a game hands the launch address to <see cref="ILauncherBridge"/>.</summary>
public interface IServersService
{
    Task<IReadOnlyList<Reserve>> ReservesAsync();
    Task<IReadOnlyList<MapMarker>> MarkersAsync(int reserve);
    Task<IReadOnlyList<GameServer>> ServersAsync();
    Task<IReadOnlyList<Tutorial>> TutorialsAsync();
    Task<Result> StartSingleAsync(int reserve, int marker, int minutes);
    Task<Result> CreateAsync(NewGame game, int reserve, int marker, int minutes);
    /// <summary>Joins at a lodge or tent marker of the game's reserve.</summary>
    Task<Result> JoinAsync(int server, int marker, string? password);
    Task<Result> StartTutorialAsync(int id);
}
