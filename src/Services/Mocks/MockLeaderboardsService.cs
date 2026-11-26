namespace Resurgence.Services.Mocks;

/// <summary>Boards computed from fixed formulas: the same names and values on every call. The player is ranked at the hunter score (far down), in the two games and on some boards.</summary>
public sealed class MockLeaderboardsService : ILeaderboardsService
{
    static readonly string[] Antlers = ["Axis Deer Antler", "Blacktail Deer Antler", "Blacktail Deer Non-Typical Antler", "Fallow Deer Antler", "Moose Antler", "Mule Deer Antler", "Red Deer Antler", "Whitetail Deer Antler"];

    static readonly string[] Weapons = [".270 Winchester", ".300 Winchester Magnum", "Compound Bow", "12 Gauge Shotgun", ".243 Bolt Action", "7mm Remington Magnum", "Recurve Bow", ".30-06 Rifle"];
    static readonly DateTimeOffset Community = new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);

    // season 2 is the community server's, 1 the restored harvests (the first one's end is the second one's start less a second)
    static readonly Season[] SeasonList = [new(1, DateTimeOffset.UnixEpoch, Community.AddSeconds(-1)), new(2, Community, null)];

    // value of the n-th place (n from 1): falls a little more than 0.2 per place; the seasons' boards differ, all time takes the best of both
    static double Value(string kind, int id, int n, int season = 0)
    {
        id += season == 1 ? 3 : 0;
        var start = kind switch
        {
            "range" => 330.0 + id * 7 % 190,
            "hunterscore" => 115000,
            "lanebandit" => 1105,
            "bustthrough" => 10567,
            "antlers-single" => 98 + id % 10,
            "antlers-double" => 190 + id % 25,
            _ => 100 + id * 37 % 1500,
        };
        return kind is "hunterscore" or "lanebandit" or "bustthrough" ? Math.Round(start - (n - 1) * (kind == "hunterscore" ? 0 : 37)) : Math.Round(start - n * 0.2373 - n * n % 7 * 0.01, 4);
    }

    // a kill is dated in the season: 2025 for the first, November 2026 for the second, either for all time
    static Ranked Row(string kind, int id, int n, int season = 0) => kind is "hunterscore" or "lanebandit" or "bustthrough"
        ? new(n, Names.Handle(id * 5 + n * 3), Value(kind, id, n))
        : new(n, Names.Handle(id * 5 + n * 3 + season), Value(kind, id, n, season), 1_100_000_000 + id * 1000 + n, Weapon: Weapons[(id + n) % Weapons.Length],
            When: (season == 1 || season == 0 && n % 2 == 0 ? new DateTimeOffset(2025, 3, 1, 8, 15, 0, TimeSpan.Zero) : Community.AddHours(7)).AddHours(n * 29 + id));

    static IReadOnlyList<(int Id, string Name)> Groups(string kind) => kind switch
    {
        "antlers-single" or "antlers-double" => Antlers.Select((a, i) => (2000 + i * 3, a)).ToList(),
        _ => Names.Species.OrderBy(s => s.Name).Select(s => (s.Id, s.Name)).ToList(),
    };

    public Task<(int Current, IReadOnlyList<Season> Seasons)> SeasonsAsync() => Task.FromResult<(int, IReadOnlyList<Season>)>((2, SeasonList));

    public Task<IReadOnlyList<Board>> BoardsAsync(string kind, int season) => Task.FromResult<IReadOnlyList<Board>>(
        Groups(kind).Select(g => new Board(g.Id, g.Name, Enumerable.Range(1, kind.StartsWith("antlers") ? 10 : 3).Select(n => Row(kind, g.Id, n, season)).ToList())).ToList());

    public async Task<Page<Ranked>> RowsAsync(string kind, int id, int page, int season)
    {
        // the tables of the hunter score and of the two games show 30 rows, those of a species 25, those of an antler 50: the top 100, then the player's own place and the 50 above and below it past the top
        var size = kind is "hunterscore" or "lanebandit" or "bustthrough" ? 30 : kind.StartsWith("antlers") ? 50 : 25;
        var pages = (99 + size) / size;
        page = Math.Clamp(page, 1, pages);
        var mine = await MyRankAsync(kind, id, season);
        var from = Math.Max(101, (mine?.Rank ?? 0) - 50);
        return new Page<Ranked>([.. Enumerable.Range((page - 1) * size + 1, Math.Min(size, 100 - (page - 1) * size)).Select(n => Row(kind, id, n, season))], page, pages,
            Around: mine is null ? [] : [.. Enumerable.Range(from, Math.Max(0, mine.Rank + 51 - from)).Select(n => n == mine.Rank ? mine : Row(kind, id, n, season))]);
    }

    // the player is ranked at the hunter score, in the two games and on every fourth species or antler board (the other boards show "nobody ranked")
    public Task<Ranked?> MyRankAsync(string kind, int id, int season) => Task.FromResult<Ranked?>(kind switch
    {
        "hunterscore" => new(200000, "Hunter01", 50),
        "lanebandit" => new(310, "Hunter01", 480),
        "bustthrough" => new(96, "Hunter01", 3210),
        _ when id % 4 == 1 => Row(kind, id, 12 + id % 30, season) with { Handle = "Hunter01" },
        _ => null,
    });

    // an ended season's best of each species; the first season's winners are the restored accounts' hunters
    public Task<IReadOnlyList<HallEntry>> HallAsync(int season) => Task.FromResult<IReadOnlyList<HallEntry>>(
        season != 1 ? [] : [.. Names.Species.OrderBy(s => s.Name).Select(s => new HallEntry(s.Name, Row("score", s.Id, 1, season), Row("range", s.Id, 1, season)))]);

    // three reserves and eight species, each built from more than 5 hunters (the server leaves a smaller row out)
    public Task<HuntingStats> HuntingAsync() => Task.FromResult(new HuntingStats(
        [.. new[] { "Reserve A", "Reserve B", "Reserve C" }.Select((r, i) => new ReserveHunting(r, 2400.5 - i * 700, 5.8 - i * 0.7, 120 - i * 30, 1500 + i * 380.5, 260 + i * 34.25))],
        [.. Names.Species.Take(8).Select((s, i) => new SpeciesHunting(s.Name, 150.25 + i * 20, 6.1 - i * 0.4, 20 + i * 9, 760 + i * 180.5, 128 + i * 30.25, "Reserve " + (char)('A' + i % 3)))],
        [new("weekend", new(2026, 11, 21, 0, 0, 0, TimeSpan.Zero), new(2026, 11, 24, 0, 0, 0, TimeSpan.Zero), 2), new("bonus", new(2026, 11, 24, 0, 0, 0, TimeSpan.Zero), new(2026, 11, 28, 0, 0, 0, TimeSpan.Zero), 1.25)]));
}
