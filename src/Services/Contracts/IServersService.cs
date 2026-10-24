namespace Resurgence.Services;

public sealed record Reserve(int Id, string Name);
public enum MarkerKind { Lodge, Tent, Campsite, Tower, Interest, Bait, Feeder, TreeStand, GroundBlind }
/// <summary>A place on a reserve map; X and Y are percent of the map pane.</summary>
public sealed record MapMarker(int Id, MarkerKind Kind, string Name, int X, int Y);
/// <summary>A hunter in a game; X and Y are percent of the reserve map, where the join map draws the hunter.</summary>
public sealed record GamePlayer(string Handle, int HunterScore, int X = 50, int Y = 50);
/// <summary>A saved equipment set the player can start with. The choice "last used equipment" (id 0) is not listed.</summary>
public sealed record Loadout(int Id, string Name);
public enum Distance { Short, Medium, Long }
public sealed record GameServer(int Id, string Name, string Description, int Reserve, int Players, int Max, string Host, bool Password, string Country, Distance Distance,
    int Seconds, int MinScore, int MaxScore, IReadOnlyList<GamePlayer> Roster)
{
    /// <summary>The host limited the hunter score: the game shows its limits (0 and 99999 mean none).</summary>
    public bool Limited => MinScore > 0 || MaxScore < 99999;
}
public sealed record NewGame(string Name, string Description, bool FriendsOnly, string? Password, int MaxPlayers, int? MinScore, int? MaxScore);
public sealed record Tutorial(int Id, string Title, string Description, int Reserve);

/// <summary>The hunt screens: lobby, create game, server list, join map, tutorials. Starting a game hands the launch address to <see cref="ILauncherBridge"/>.</summary>
/// <remarks>The start methods take the id of a <see cref="Loadout"/>; 0 starts with the last used equipment.</remarks>
public interface IServersService
{
    Task<IReadOnlyList<Reserve>> ReservesAsync();
    Task<IReadOnlyList<MapMarker>> MarkersAsync(int reserve);
    Task<IReadOnlyList<GameServer>> ServersAsync();
    Task<IReadOnlyList<Tutorial>> TutorialsAsync();
    /// <summary>The player's saved loadouts; the pickers only show with at least one.</summary>
    Task<IReadOnlyList<Loadout>> LoadoutsAsync();
    Task<Result> StartSingleAsync(int reserve, int marker, int minutes, int loadout);
    Task<Result> CreateAsync(NewGame game, int reserve, int marker, int minutes, int loadout);
    /// <summary>Joins at a lodge or tent marker of the game's reserve. A wrong password fails with a message key.</summary>
    Task<Result> JoinAsync(int server, int marker, string? password, int loadout);
    Task<Result> StartTutorialAsync(int id);
}
