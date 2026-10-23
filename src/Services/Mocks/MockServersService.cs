using Microsoft.Extensions.Logging;

namespace Resurgence.Services.Mocks;

public sealed class MockServersService(ILogger<MockServersService> log, ILauncherBridge bridge) : IServersService
{
    static readonly (string Host, string Country, Distance Distance, int Reserve, int Players, int Max, bool Password)[] Rows =
    [
        ("Hunter05", "se", Distance.Short, 2, 1, 3, false), ("Ranger_Test", "ca", Distance.Short, 11, 1, 8, false), ("Moss_Walker", "us", Distance.Short, 4, 1, 8, false),
        ("Hunter08", "de", Distance.Short, 4, 1, 8, false), ("Hunter09", "us", Distance.Short, 1, 1, 8, false), ("Pine_Walker", "de", Distance.Short, 10, 1, 8, false),
        ("River_Test", "us", Distance.Short, 4, 1, 8, true), ("Hunter12", "br", Distance.Medium, 12, 2, 3, false), ("Hunter13", "au", Distance.Medium, 10, 1, 8, true),
        ("Hunter14", "br", Distance.Medium, 6, 2, 8, false), ("Hunter15", "br", Distance.Medium, 5, 1, 4, false), ("Hunter16", "ca", Distance.Medium, 9, 1, 8, false),
        ("Hunter17", "se", Distance.Medium, 1, 1, 8, false), ("Hunter18", "us", Distance.Medium, 6, 1, 2, false), ("Hunter19", "de", Distance.Medium, 12, 1, 8, false),
        ("Hunter20", "au", Distance.Long, 10, 1, 8, false),
    ];

    static readonly IReadOnlyList<GameServer> Servers = Rows.Select((r, i) => new GameServer(i + 1, $"{r.Host}'s game", i == 0 ? "Placeholder description of the game. The host writes a short text here for the other players." : "",
        r.Reserve, r.Players, r.Max, r.Host, r.Password, r.Country, r.Distance, 1299 + i * 61, i == 0 ? 1000 : 0, 99999,
        Enumerable.Range(0, r.Players).Select(p => new GamePlayer(p == 0 ? r.Host : $"Hunter{40 + p}", 15300 - p * 4200)).ToList())).ToList();

    static readonly Tutorial[] Tutorials = Enumerable.Range(1, 5).Select(i => new Tutorial(i, new[] { "The Basics", "Using Towers", "Bow/Xbow + Tree Stands Usage", "Waterfowling + Retriever Dogs", "Pheasants + Pointer Dogs" }[i - 1],
        new[] { "Join the warden on your very first hunting expedition and learn the basics of {game}.", "Make good use of hunting towers. Use a scent spray to bring an animal right up to the tower for a prime shot.",
            "Bows, crossbows and tree stands, find out all about them here.", "Pick up the basics of waterfowling with some geese.", "Having trouble with your pointer or just wondering whether it is the right puppy for you? Find out right here." }[i - 1],
        new[] { 2, 4, 1, 12, 2 }[i - 1])).ToArray();

    public Task<IReadOnlyList<Reserve>> ReservesAsync() =>
        Task.FromResult<IReadOnlyList<Reserve>>(Enumerable.Range(1, 12).Select(i => new Reserve(i, $"Reserve {i:00}")).ToList());

    /// <summary>Two lodges (ids 1 and 2), a tent-capable campsite, towers and points of interest, at fixed spots.</summary>
    public Task<IReadOnlyList<MapMarker>> MarkersAsync(int reserve) =>
        Task.FromResult<IReadOnlyList<MapMarker>>(
        [
            new(1, MarkerKind.Lodge, reserve == 2 ? "Field Lodge" : "Lodge One", 60, 62), new(2, MarkerKind.Lodge, reserve == 2 ? "Border Lodge" : "Lodge Two", 26, 30),
            new(3, MarkerKind.Tower, "Tower", 44, 20), new(4, MarkerKind.Tower, "Tower", 70, 34), new(5, MarkerKind.Tower, "Tower", 36, 55), new(6, MarkerKind.Tower, "Tower", 78, 72),
            new(7, MarkerKind.Campsite, "Campsite", 52, 44), new(8, MarkerKind.Interest, "Point of interest", 18, 66), new(9, MarkerKind.Bait, "Bait barrel", 66, 82),
            new(10, MarkerKind.Interest, "Point of interest", 84, 24),
        ]);

    public Task<IReadOnlyList<GameServer>> ServersAsync() => Task.FromResult(Servers);
    public Task<IReadOnlyList<Tutorial>> TutorialsAsync() => Task.FromResult<IReadOnlyList<Tutorial>>(Tutorials);

    public Task<Result> StartSingleAsync(int reserve, int marker, int minutes) => Launch($"single reserve {reserve} marker {marker} at {minutes} min");
    public Task<Result> CreateAsync(NewGame game, int reserve, int marker, int minutes) => Launch($"multiplayer \"{game.Name}\" reserve {reserve} marker {marker} at {minutes} min, max {game.MaxPlayers}");
    public Task<Result> JoinAsync(int server, int marker, string? password) => Launch($"join game {server} at marker {marker}");
    public Task<Result> StartTutorialAsync(int id) => Launch($"tutorial {id}");

    async Task<Result> Launch(string what)
    {
        log.LogInformation("start {What}", what);
        await Task.Delay(300);
        bridge.StartGame("hunt://mock/" + Uri.EscapeDataString(what));
        return Result.Success;
    }
}
