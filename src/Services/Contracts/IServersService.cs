namespace Resurgence.Services;

public sealed record Reserve(int Id, string Name);
public enum MarkerKind { Lodge, Tent, Campsite, Tower, Interest, Bait, Feeder, TreeStand, GroundBlind, Caller, Tripod, DeployableTower, Climbing }
/// <summary>A place on a reserve map; X and Y are percent of the map pane (the reserve's map picture). A start's Id is the catalogue's location number; a placed item (tent, caller, blind, stand, tripod, tower, climbing rope)
/// has the negated guid of the item, so no two markers of a map share an Id. <c>Only</c>: the marker is shown, never picked as a start (the host's tent on a join map).</summary>
public sealed record MapMarker(int Id, MarkerKind Kind, string Name, double X, double Y, bool Only = false);
/// <summary>A hunter in a game; X and Y are percent of the reserve map from the left and the top, where the join map and the server list's preview draw the hunter (not drawn without them).</summary>
public sealed record GamePlayer(string Handle, int HunterScore, double? X = 50, double? Y = 50, string Avatar = Avatars.Default);
/// <summary>A saved equipment set the player can start with. The choice "last used equipment" (id 0) is not listed.</summary>
public sealed record Loadout(int Id, string Name);
public enum Distance { Short, Medium, Long }
/// <param name="Why">The server's reason this player cannot join: <c>own</c>, <c>friends</c>, <c>hunterscore</c>, <c>starting</c>, <c>password</c>, <c>full</c>; null when nothing stops the player.</param>
public sealed record GameServer(long Id, string Name, string Description, int Reserve, int Players, int Max, string Host, bool Password, string Country, Distance Distance,
    int Seconds, int MinScore, int MaxScore, IReadOnlyList<GamePlayer> Roster, string? Why = null)
{
    /// <summary>The host limited the hunter score: the game shows its limits (0 and 99999 mean none).</summary>
    public bool Limited => MinScore > 0 || MaxScore < 99999;
    /// <summary>The filter "games I'm allowed to join" lists these: a game with a password is not one.</summary>
    public bool Joinable => Why is null && !Password;
    /// <summary>The player cannot join and a password would not help: the Join button is off.</summary>
    public bool Refused => Why is not (null or "password");
}
public sealed record NewGame(string Name, string Description, bool FriendsOnly, string? Password, int MaxPlayers, int? MinScore, int? MaxScore);
public sealed record Tutorial(int Id, string Title, string Description, int Reserve);

/// <summary>The hunt screens: lobby, create game, server list, join map, tutorials. Starting a game hands the launch address to <see cref="ILauncherBridge"/>.</summary>
/// <remarks>The start methods take the id of a <see cref="Loadout"/>; 0 starts with the last used equipment.</remarks>
public interface IServersService
{
    Task<IReadOnlyList<Reserve>> ReservesAsync();
    Task<IReadOnlyList<MapMarker>> MarkersAsync(int reserve);
    /// <summary>The join map of a game: the starts of its reserve and the host's placed items there (shown only, <see cref="MapMarker.Only"/>: a start at a host's tent is not offered). Null when the server does not
    /// show the map: the player may not join the game, or it has a password and none, or a wrong one, was given.</summary>
    Task<IReadOnlyList<MapMarker>?> JoinMarkersAsync(long server, int reserve, string? password);
    Task<IReadOnlyList<GameServer>> ServersAsync();
    Task<IReadOnlyList<Tutorial>> TutorialsAsync();
    /// <summary>The player's saved loadouts; the pickers only show with at least one.</summary>
    Task<IReadOnlyList<Loadout>> LoadoutsAsync();
    Task<Result> StartSingleAsync(int reserve, int marker, int minutes, int loadout);
    Task<Result> CreateAsync(NewGame game, int reserve, int marker, int minutes, int loadout);
    /// <summary>Joins at a lodge or tent marker of the game's reserve. A refusal (wrong password, full, not allowed, host not ready) fails with its message key.</summary>
    Task<Result> JoinAsync(long server, int marker, string? password, int loadout);
    Task<Result> StartTutorialAsync(int id);
}
