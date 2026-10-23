namespace Resurgence.Services.Mocks;

/// <summary>Boards computed from fixed formulas: the same names and values on every call. Nobody is ranked except for the hunter score, where the player is far down.</summary>
public sealed class MockLeaderboardsService : ILeaderboardsService
{
    const int PageSize = 25;
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

    static Ranked Row(string kind, int id, int n) => new(n, Names.Handle(id * 5 + n * 3), Value(kind, id, n));

    static IReadOnlyList<(int Id, string Name)> Groups(string kind) => kind switch
    {
        "antlers-single" or "antlers-double" => Antlers.Select((a, i) => (2000 + i * 3, a)).ToList(),
        _ => Names.Species.OrderBy(s => s.Name).ToList(),
    };

    public Task<int> SeasonAsync() => Task.FromResult(46);

    public Task<IReadOnlyList<Board>> BoardsAsync(string kind) => Task.FromResult<IReadOnlyList<Board>>(
        Groups(kind).Select(g => new Board(g.Id, g.Name, Enumerable.Range(1, kind.StartsWith("antlers") ? 8 : 5).Select(n => Row(kind, g.Id, n)).ToList())).ToList());

    public Task<Page<Ranked>> RowsAsync(string kind, int id, int page)
    {
        var pages = kind == "hunterscore" ? 20 : 4;
        page = Math.Clamp(page, 1, pages);
        return Task.FromResult(new Page<Ranked>(Enumerable.Range((page - 1) * PageSize + 1, PageSize).Select(n => Row(kind, id, n)).ToList(), page, pages));
    }

    public Task<Ranked?> MyRankAsync(string kind, int id) => Task.FromResult<Ranked?>(kind == "hunterscore" ? new(238892, "Hunter01", 74) : null);

    public Task<IReadOnlyList<HallEntry>> HallAsync(int season) => Task.FromResult<IReadOnlyList<HallEntry>>(
        Names.Species.OrderBy(s => s.Name).Where(s => season > 1 || s.Name is "Mule Deer (Typical)" or "Turkey" or "Whitetail Deer (Typical)")
            .Select(s => new HallEntry(s.Name, Row("score", s.Id + season, 1), Row("range", s.Id + season, 1))).ToList());
}
