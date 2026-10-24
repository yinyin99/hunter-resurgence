namespace Resurgence.Services.Mocks;

/// <summary>Boards computed from fixed formulas: the same names and values on every call. The player is ranked at the hunter score (far down), in the two games and on some boards.</summary>
public sealed class MockLeaderboardsService : ILeaderboardsService
{
    static readonly string[] Antlers = ["Axis Deer Antler", "Blacktail Deer Antler", "Blacktail Deer Non-Typical Antler", "Fallow Deer Antler", "Moose Antler", "Mule Deer Antler", "Red Deer Antler", "Whitetail Deer Antler"];

    // value of the n-th place (n from 1): falls a little more than 0.2 per place
    static double Value(string kind, int id, int n)
    {
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

    static Ranked Row(string kind, int id, int n) => new(n, Names.Handle(id * 5 + n * 3), Value(kind, id, n), kind is "hunterscore" or "lanebandit" or "bustthrough" ? 0 : 1_100_000_000 + id * 1000 + n);

    static IReadOnlyList<(int Id, string Name)> Groups(string kind) => kind switch
    {
        "antlers-single" or "antlers-double" => Antlers.Select((a, i) => (2000 + i * 3, a)).ToList(),
        // the species ids are the old ones; 0 is taken by the boards without groups, so the species with id 0 moves to 99
        _ => Names.Species.OrderBy(s => s.Name).Select(s => (s.Id == 0 ? 99 : s.Id, s.Name)).ToList(),
    };

    public Task<int> SeasonAsync() => Task.FromResult(46);

    public Task<IReadOnlyList<Board>> BoardsAsync(string kind) => Task.FromResult<IReadOnlyList<Board>>(
        Groups(kind).Select(g => new Board(g.Id, g.Name, Enumerable.Range(1, kind.StartsWith("antlers") ? 10 : 5).Select(n => Row(kind, g.Id, n)).ToList())).ToList());

    public Task<Page<Ranked>> RowsAsync(string kind, int id, int page)
    {
        // the tables of the hunter score and of the two games show 30 rows, those of a species or an antler 50
        var (size, pages) = kind is "hunterscore" or "lanebandit" or "bustthrough" ? (30, kind == "hunterscore" ? 20 : 4) : (50, 4);
        page = Math.Clamp(page, 1, pages);
        return Task.FromResult(new Page<Ranked>(Enumerable.Range((page - 1) * size + 1, size).Select(n => Row(kind, id, n)).ToList(), page, pages));
    }

    // the player is ranked at the hunter score, in the two games and on every fourth species or antler board (the other boards show "nobody ranked")
    public Task<Ranked?> MyRankAsync(string kind, int id) => Task.FromResult<Ranked?>(kind switch
    {
        "hunterscore" => new(200000, "Hunter01", 50),
        "lanebandit" => new(310, "Hunter01", 480),
        "bustthrough" => new(96, "Hunter01", 3210),
        _ when id % 4 == 1 => new(12 + id % 30, "Hunter01", Value(kind, id, 12 + id % 30), 1_100_000_000 + id * 1000),
        _ => null,
    });

    public Task<IReadOnlyList<HallEntry>> HallAsync(int season) => Task.FromResult<IReadOnlyList<HallEntry>>(
        Names.Species.OrderBy(s => s.Name).Where(s => season > 1 || s.Name is "Mule Deer (Typical)" or "Turkey" or "Whitetail Deer (Typical)")
            .Select(s => new HallEntry(s.Name, Anonymous(Row("score", s.Id + season, 1), season), Anonymous(Row("range", s.Id + season, 1), season))).ToList());

    // the first season has the values but not the players
    static Ranked Anonymous(Ranked r, int season) => season > 1 ? r : r with { Handle = "", Sheet = 0 };
}
