namespace Resurgence.Services.Mocks;

/// <summary>Competitions generated from the species list: three tiers per species at most. Dates are relative to the start of the host, so the countdowns run.
/// Ids 179583, 179580 and 179577 are the activated ones.</summary>
public sealed class MockCompetitionsService : ICompetitionsService
{
    static readonly DateTime Now = new(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    static readonly DateTime Starts = Now.AddHours(26), Ends = Now.AddDays(3).AddHours(2);
    static readonly (string Tier, string Needs)[] Tiers = [("Elite", "Over 500"), ("Intermediate", "51 to 500"), ("Starter", "Max 50")];
    // the first species have several competitions, the last ones none (greyed in the filter)
    static readonly int[] Counts = [1, 2, 4, 2, 4, 5, 2, 5, 2, 1, 1, 1, 1, 6, 1, 1, 2, 1, 1, 3, 2, 4, 1, 1, 1, 2, 3, 1, 1, 2, 3, 0, 0, 2, 1, 1, 0, 0, 3, 2, 1, 2, 3, 1, 1, 2, 1, 2, 1, 2, 1];
    static readonly string[] NotSpecies = ["Single player", "Multiplayer"];

    static Competition Make(int id, string sp, int i, int j, bool active) => Make(id, sp, i % 2 == 0 ? "board" : "weight", Tiers[j % 3], (i * 3 + j) % 4 == 0, 2 + (i * 7 + j * 3) % 20, active);

    static Competition Make(int id, string sp, string icon, (string Tier, string Needs) tier, bool joinable, int players, bool active) => new(id,
        $"{(icon == "board" ? "Top" : "Biggest")} {sp} - {tier.Tier}", icon, [sp, "Single player"],
        icon == "board" ? $"Harvest the highest scoring {sp} to win." : $"The average weight of your top 3 heaviest {sp} will count as your competition entry. Highest average wins.",
        active ? null : $"{tier.Needs} {sp} harvests", players, active ? Now.AddDays(-2) : Starts, active ? Now.AddSeconds(17407) : Ends, active ? "Started" : "Signup", joinable);

    static readonly Competition[] Active = [Make(179583, "Bighorn Sheep", "board", Tiers[2], false, 2, true), Make(179580, "Grey Wolf", "weight", Tiers[2], false, 2, true), Make(179577, "Reindeer", "board", Tiers[2], false, 2, true)];

    static readonly Competition[] Upcoming =
    [
        new(180100, "Rocking Boat", "board", ["Mallard", "Gadwall", "Northern Pintail", "American Black Duck", "Single player"], "Harvest a Mallard, Gadwall, Northern Pintail and American Black Duck swimming in the water from the longest distance using any compound bow. Longest combined distance wins.", null, 15, Starts, Ends, "Signup", true),
        new(180101, "Handgun Challenge", "board", ["Coyote", "Red Fox", "Single player"], "The combined score of the top scoring Coyote and Red Fox harvested with a handgun is your score. Highest score wins.", null, 14, Starts, Ends.AddDays(1), "Signup", true),
        .. Names.Species.SelectMany((s, i) => Enumerable.Range(0, i < Counts.Length ? Counts[i] : 0).Select(j => Make(180200 + i * 10 + j, s.Name, i, j, false))),
    ];

    static readonly Competition[] Previous = Enumerable.Range(0, 70).Select(n => Make(170000 + n, Names.Species[n % Names.Species.Length].Name, n % 2 == 0 ? "board" : "weight", Tiers[n % 3], false, 2 + n * 7 % 30, false) with { Start = null, End = null, Status = null }).ToArray();

    // the weekly hunts of the community league: two running, the rest not started (those cannot be joined)
    static readonly Competition[] League = Enumerable.Range(0, 6).Select(n => new Competition(190001 + n, $"Weekly Hunt {n + 1} - {Names.Species[n * 7 % Names.Species.Length].Name}", n % 2 == 0 ? "board" : "weight",
        [Names.Species[n * 7 % Names.Species.Length].Name, "Single player"], $"Hunt a {Names.Species[n * 7 % Names.Species.Length].Name} on the reserve of the week. Best result of your attempts counts.", null, 40 + n * 13,
        Now.AddDays(n * 3 - 4), Now.AddDays(n * 3 + 3), n < 2 ? "Started" : "Signup", n < 2)).ToArray();

    // activating and leaving move a competition in and out of this list (the mock lives as long as the host)
    readonly List<Competition> enrolled = [.. Active];

    // attempts left, attempts, position: fixed numbers per competition, so every id has other ones
    static int Max(int id) => 5 + id % 6;
    static int Left(int id) => id % (Max(id) + 1);
    static int? Position(int id) => id % 4 == 0 ? null : 1 + id % 23;

    Competition Flag(Competition c) => c with { Enrolled = enrolled.Any(e => e.Id == c.Id) };

    Enrolled Row(Competition c) => new(c, Left(c.Id), Max(c.Id), Position(c.Id));

    public Task<IReadOnlyList<Enrolled>> EnrolledAsync() => Task.FromResult<IReadOnlyList<Enrolled>>(enrolled.Where(c => !League.Any(l => l.Id == c.Id)).Select(Row).ToList());

    public Task<IReadOnlyList<Species>> SpeciesAsync() => Task.FromResult<IReadOnlyList<Species>>(
        [.. Names.Species.OrderBy(s => s.Name).Select(s => new Species(s.Id, s.Name, Upcoming.Count(c => c.Tags.Contains(s.Name)))), new(ICompetitionsService.Multispecies, "Multispecies", Upcoming.Count(IsMulti))]);

    static bool IsMulti(Competition c) => c.Tags.Count(t => !NotSpecies.Contains(t)) > 1;

    public Task<IReadOnlyList<Competition>> UpcomingAsync(int? species)
    {
        var name = Names.Species.FirstOrDefault(s => s.Id == species).Name;
        var rows = species is null ? Upcoming : species == ICompetitionsService.Multispecies ? Upcoming.Where(IsMulti) : Upcoming.Where(c => c.Tags.Contains(name));
        return Task.FromResult<IReadOnlyList<Competition>>(rows.Select(Flag).ToList());
    }

    public Task<Page<Competition>> PreviousAsync(int page)
    {
        var pages = (Previous.Length + 9) / 10;
        page = Math.Clamp(page, 1, pages);
        return Task.FromResult(new Page<Competition>(Previous.Skip((page - 1) * 10).Take(10).ToList(), page, pages));
    }

    static readonly Prize[][] Prizes =
    [
        [new(500, 0, true), new(0, 300, true), new(0, 100, false)],
        [new(0, 0, true), new(250, 0, false), new(0, 100, false)],
        [new(0, 0, true), new(0, 300, true), new(50, 0, false)],
    ];

    public Task<CompetitionDetails?> DetailsAsync(int id)
    {
        var c = Active.Concat(Upcoming).Concat(Previous).Concat(League).FirstOrDefault(c => c.Id == id);
        if (c is null) return Task.FromResult<CompetitionDetails?>(null);
        var sp = c.Tags[0];
        // a competition the player is not in shows full attempts and no position, one that is shows its own numbers (activating changes them)
        var active = enrolled.Any(a => a.Id == id);
        var ran = active || Previous.Any(p => p.Id == id);
        return Task.FromResult<CompetitionDetails?>(new CompetitionDetails(Flag(c), active, c.Description.Split(". ")[0].TrimEnd('.') + ".", ran ? Position(id) : null, "160.0000", ran ? Left(id) : Max(id), Max(id), Prizes[id % 3],
            sp, "Reserve 01", "Harvest the highest scoring " + sp + " to win.", "Any permitted.", "Highest score.", "None.", "Singleplayer only."));
    }

    // 45 entrants in the order of their results
    public Task<Page<Participant>> ParticipantsAsync(int id, int page)
    {
        var all = Enumerable.Range(1, 45).Select(n => new Participant(n, Names.Handle(17 + n * 8 % 64), (160 - (n - 1) * 0.9 - id % 7 * 0.01).ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture))).ToList();
        var pages = (all.Count + ICompetitionsService.ParticipantsPageSize - 1) / ICompetitionsService.ParticipantsPageSize;
        page = Math.Clamp(page, 1, pages);
        return Task.FromResult(new Page<Participant>(all.Skip((page - 1) * ICompetitionsService.ParticipantsPageSize).Take(ICompetitionsService.ParticipantsPageSize).ToList(), page, pages, all.Count));
    }

    static readonly string[] Months = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    // one run a month, two now and then, newest first: 2026 up to September, then 2025
    public Task<IReadOnlyList<HistoryMonth>> HistoryAsync(int id) => Task.FromResult<IReadOnlyList<HistoryMonth>>(
        Enumerable.Range(0, 15).Select(n => (Year: 2026 - (n + 3) / 12, Month: (8 - n % 12 + 12) % 12)).Select((m, n) =>
            new HistoryMonth(m.Year, Months[m.Month], n % 4 == 0 ? [(29 - n % 9, id - 100 * n), (5 + n % 7, id - 100 * n - 50)] : [(8 + n * 7 % 20, id - 100 * n)])).ToList());

    public Task<Result> JoinAsync(int id)
    {
        if (Upcoming.Concat(League).FirstOrDefault(c => c.Id == id) is { } c && enrolled.All(e => e.Id != id)) enrolled.Add(c);
        return Task.FromResult(Result.Success);
    }

    public Task<Result> LeaveAsync(int id)
    {
        enrolled.RemoveAll(c => c.Id == id);
        return Task.FromResult(Result.Success);
    }

    public Task<IReadOnlyList<Competition>> LeagueAsync() => Task.FromResult<IReadOnlyList<Competition>>(League.Select(Flag).ToList());

    public Task<IReadOnlyList<Enrolled>> LeagueEnrolledAsync() => Task.FromResult<IReadOnlyList<Enrolled>>(enrolled.Where(c => League.Any(l => l.Id == c.Id)).Select(Row).ToList());

    public Task<IReadOnlyList<Ranked>> LeagueBoardAsync(bool season) => Task.FromResult<IReadOnlyList<Ranked>>(
        Enumerable.Range(1, 10).Select(n => new Ranked(n, Names.Handle(n * (season ? 11 : 5)), (season ? 1800 : 600) - n * (season ? 90 : 35))).ToList());
}
