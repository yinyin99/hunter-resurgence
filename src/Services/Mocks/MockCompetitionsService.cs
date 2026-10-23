namespace Resurgence.Services.Mocks;

/// <summary>Competitions generated from the species list: three tiers per species at most, fixed dates, no clock. Ids 179583, 179580 and 179577 are the activated ones.</summary>
public sealed class MockCompetitionsService : ICompetitionsService
{
    static readonly DateTime Starts = new(2026, 10, 23, 7, 0, 0), Ends = new(2026, 10, 25, 7, 0, 0);
    static readonly (string Tier, string Needs)[] Tiers = [("Elite", "Over 500"), ("Intermediate", "51 to 500"), ("Starter", "Max 50")];
    // the first species have several competitions, the last ones none (greyed in the filter)
    static readonly int[] Counts = [1, 2, 4, 2, 4, 5, 2, 5, 2, 1, 1, 1, 1, 6, 1, 1, 2, 1, 1, 3, 2, 4, 1, 1, 1, 2, 3, 1, 1, 2, 3, 0, 0, 2, 1, 1, 0, 0, 3, 2, 1, 2, 3, 1, 1, 2, 1, 2, 1, 2, 1];

    static Competition Make(int id, string sp, int i, int j, bool active) => Make(id, sp, i % 2 == 0 ? "board" : "weight", Tiers[j % 3], (i * 3 + j) % 4 == 0, 2 + (i * 7 + j * 3) % 20, active);

    static Competition Make(int id, string sp, string icon, (string Tier, string Needs) tier, bool joinable, int players, bool active) => new(id,
        $"{(icon == "board" ? "Top" : "Biggest")} {sp} - {tier.Tier}", icon, [sp, "Single player"],
        icon == "board" ? $"Harvest the highest scoring {sp} to win." : $"The average weight of your top 3 heaviest {sp} will count as your competition entry. Highest average wins.",
        active ? null : $"{tier.Needs} {sp} harvests", players, active ? new(2026, 10, 21, 7, 0, 0) : Starts, active ? Starts : Ends, active ? "Started" : "Signup", joinable);

    static readonly Competition[] Active = [Make(179583, "Bighorn Sheep", "board", Tiers[2], false, 2, true), Make(179580, "Grey Wolf", "weight", Tiers[2], false, 2, true), Make(179577, "Reindeer", "board", Tiers[2], false, 2, true)];

    static readonly Competition[] Upcoming =
    [
        new(180100, "Rocking Boat", "board", ["Mallard", "Gadwall", "Northern Pintail", "American Black Duck", "Single player"], "Harvest a Mallard, Gadwall, Northern Pintail and American Black Duck swimming in the water from the longest distance using any compound bow. Longest combined distance wins.", null, 15, Starts, Ends, "Signup", true),
        new(180101, "Handgun Challenge", "board", ["Coyote", "Red Fox", "Single player"], "The combined score of the top scoring Coyote and Red Fox harvested with a handgun is your score. Highest score wins.", null, 14, Starts, Ends.AddDays(1), "Signup", true),
        .. Names.Species.SelectMany((s, i) => Enumerable.Range(0, i < Counts.Length ? Counts[i] : 0).Select(j => Make(180200 + i * 10 + j, s.Name, i, j, false))),
    ];

    static readonly Competition[] Previous = Enumerable.Range(0, 70).Select(n => Make(170000 + n, Names.Species[n % Names.Species.Length].Name, n % 2 == 0 ? "board" : "weight", Tiers[n % 3], false, 2 + n * 7 % 30, false) with { Start = null, End = null, Status = null }).ToArray();

    // activating and leaving move a competition in and out of this list (the mock lives as long as the host)
    readonly List<Competition> enrolled = [.. Active];

    public Task<IReadOnlyList<Enrolled>> EnrolledAsync() => Task.FromResult<IReadOnlyList<Enrolled>>(enrolled.Select(c => new Enrolled(c, 10, 10, null)).ToList());

    public Task<IReadOnlyList<Species>> SpeciesAsync() => Task.FromResult<IReadOnlyList<Species>>(Names.Species.OrderBy(s => s.Name).Select(s => new Species(s.Id, s.Name, Upcoming.Count(c => c.Tags.Contains(s.Name)))).ToList());

    public Task<IReadOnlyList<Competition>> UpcomingAsync(int? species)
    {
        var name = Names.Species.FirstOrDefault(s => s.Id == species).Name;
        return Task.FromResult<IReadOnlyList<Competition>>(species is null ? Upcoming : Upcoming.Where(c => c.Tags.Contains(name)).ToList());
    }

    public Task<Page<Competition>> PreviousAsync(int page)
    {
        var pages = (Previous.Length + 9) / 10;
        page = Math.Clamp(page, 1, pages);
        return Task.FromResult(new Page<Competition>(Previous.Skip((page - 1) * 10).Take(10).ToList(), page, pages));
    }

    public Task<CompetitionDetails?> DetailsAsync(int id)
    {
        var c = Active.Concat(Upcoming).Concat(Previous).FirstOrDefault(c => c.Id == id);
        var sp = c?.Tags[0] ?? "";
        return Task.FromResult(c is null ? null : new CompetitionDetails(c, enrolled.Any(a => a.Id == id), c.Description.Split(". ")[0].TrimEnd('.') + ".", 17000, null, "160.0000", 10, 10, ["Trophy", "Trophy", "Trophy"],
            sp, "Reserve 01", "Harvest the highest scoring " + sp + " to win.", "Any permitted.", "Highest score.", "None.", "Singleplayer only."));
    }

    public Task<IReadOnlyList<Participant>> ParticipantsAsync(int id) =>
        Task.FromResult<IReadOnlyList<Participant>>([new(1, Names.Handle(17), "160.0000"), new(2, Names.Handle(25), "150.0000")]);

    static readonly string[] Months = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    // one run a month, two now and then, newest first: 2026 up to September, then 2025
    public Task<IReadOnlyList<HistoryMonth>> HistoryAsync(int id) => Task.FromResult<IReadOnlyList<HistoryMonth>>(
        Enumerable.Range(0, 15).Select(n => (Year: 2026 - (n + 3) / 12, Month: (8 - n % 12 + 12) % 12)).Select((m, n) =>
            new HistoryMonth(m.Year, Months[m.Month], n % 4 == 0 ? [(29 - n % 9, id - 100 * n), (5 + n % 7, id - 100 * n - 50)] : [(8 + n * 7 % 20, id - 100 * n)])).ToList());

    public Task<Result> JoinAsync(int id)
    {
        if (Upcoming.FirstOrDefault(c => c.Id == id) is { } c && enrolled.All(e => e.Id != id)) enrolled.Add(c);
        return Task.FromResult(Result.Success);
    }

    public Task<Result> LeaveAsync(int id)
    {
        enrolled.RemoveAll(c => c.Id == id);
        return Task.FromResult(Result.Success);
    }
}
