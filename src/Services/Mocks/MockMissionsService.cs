namespace Resurgence.Services.Mocks;

/// <summary>
/// One active and 19 available missions and 71 packs; activating, favouriting, the two automatic settings and the broken daily streak are kept for as long as the
/// service lives. Mock "missions:resume" offers the resume item (one owned), "missions:noresume" offers it with none owned, "missions:soon" ends the day in a few seconds.
/// </summary>
public sealed class MockMissionsService(string? mock = null) : IMissionsService
{
    static readonly string[] Reserves = ["Reserve 01", "Reserve 02", "Reserve 03", "Reserve 04", "Reserve 05"];
    static readonly string[] Weapons = ["Rifle Model A", "Shotgun Model B", "Bow Model C"];

    static Mission Make(int id, string name, string state, int i, string? species = null) => new(id, name, state, $"A short briefing for {name}: find the animal, take your time and make a clean shot.",
        [$"Harvest a {species ?? Names.Species[i % Names.Species.Length].Name}"], "100 gm$", Reserves[i % Reserves.Length], species ?? Names.Species[i % Names.Species.Length].Name, Weapons[i % Weapons.Length]);

    // a pack of a species has missions of that species; every fifth pack is finished
    static MissionPack Pack(int id, string name, int total, string? species)
    {
        var done = id % 5 == 3;
        return new(id, name, done ? total : 0, total, false, $"Complete every mission of {name} to finish the pack.",
            Enumerable.Range(1, total).Select(n => Make(1000 + id * 20 + n, $"{name} {n:00}", done ? "completed" : n == 1 ? "available" : "locked", id + n, species)).ToList());
    }

    readonly List<Mission> missions =
    [
        Make(1, "Active Mission 01", "active", 0),
        .. Enumerable.Range(1, 19).Select(n => Make(10 + n, $"Available Mission {n:00}", "available", n)),
    ];

    readonly List<MissionPack> packs = Names.Species.Select(s => (Name: s.Name + " Missions", Species: (string?)s.Name)).Concat(Enumerable.Range(1, 20).Select(n => (Name: $"Special Pack {n:00}", Species: (string?)null))).OrderBy(n => n.Name)
        .Select((n, i) => Pack(i + 1, n.Name, i % 7 == 0 ? 5 : 10, n.Species)).ToList();

    bool auto, onlyFavourite, hideCompletedFavourite, frozen = true;
    int resumeOwned = mock == "missions:resume" ? 1 : 0;
    readonly bool offerResume = mock is "missions:resume" or "missions:noresume";
    DateTime dayEnd = DateTime.UtcNow.AddSeconds(mock == "missions:soon" ? 8 : 17407);

    public Task<IReadOnlyList<Mission>> ActiveAsync() => Task.FromResult<IReadOnlyList<Mission>>(missions.Where(m => m.State == "active").ToList());

    static readonly DailyMission[] Days =
    [
        new("Spot a male Rusa Deer", "Hello, I need a hand with a small job. Help me and you will be rewarded.", ["Spot a male Rusa Deer"], "Reserve 05", ["100 gm$"], false),
        new("Harvest a Red Fox", "The foxes are after the hens again. Thin them out a little.", ["Harvest a Red Fox"], "Reserve 02", ["100 gm$"], false),
        new("Track a Moose", "A big one was seen near the lake. Find its tracks.", ["Track a Moose"], "Reserve 03", ["100 gm$"], false),
        new("Harvest two Mallards", "Supper for the camp: two ducks will do.", ["Harvest two Mallards"], "Reserve 01", ["500 gm$"], false),
        new("Spot a Bison", "I lost a bison from my herd. Tell me where it is.", ["Spot a Bison"], "Reserve 04", ["100 gm$"], false),
        new("Harvest a Wild Boar", "The boars ruined the field again.", ["Harvest a Wild Boar"], "Reserve 02", ["100 gm$"], false),
        new("Track a Grey Wolf", "The pack is close. Be careful.", ["Track a Grey Wolf"], "Reserve 05", ["100 gm$"], false),
    ];

    public Task<DailyCalendar> DailyAsync()
    {
        // the day rolls over when the clock runs out
        if (dayEnd <= DateTime.UtcNow) dayEnd = dayEnd.AddDays(1);
        return Task.FromResult(new DailyCalendar(1, Enumerable.Range(1, 7).Select(d => new DailyDay(d, d == 4 ? 2 : 1, d == 4 ? 500 : 100)).ToList(),
            Days.Select((m, i) => i == 0 ? m with { Frozen = frozen } : m).ToList(), (int)(dayEnd - DateTime.UtcNow).TotalSeconds, offerResume ? new(1051, "Resume Streak", resumeOwned) : null));
    }

    public Task<RegularMissions> RegularAsync()
    {
        var done = packs.Count(p => p.Done == p.Total);
        return Task.FromResult(new RegularMissions([.. missions], [.. packs], 1 + packs.Sum(p => p.Done), 692, done, 71, auto, onlyFavourite, hideCompletedFavourite));
    }

    public Task<Result> ActivateAsync(int id)
    {
        if (id == -1) frozen = false;
        else
            for (var i = 0; i < missions.Count; i++)
                if (missions[i].State == "available" && (id == 0 || missions[i].Id == id)) missions[i] = missions[i] with { State = "active" };
        return Task.FromResult(Result.Success);
    }

    public Task<Result> SetFavouriteAsync(int pack, bool favourite)
    {
        var i = packs.FindIndex(p => p.Id == pack);
        if (i >= 0) packs[i] = packs[i] with { Favourite = favourite };
        return Task.FromResult(Result.Success);
    }

    // turning one automatic setting on turns the other one off
    public Task<Result> SetAutomaticAsync(string setting, bool on)
    {
        if (setting == "auto") (auto, onlyFavourite) = (on, on ? false : onlyFavourite);
        else (onlyFavourite, auto) = (on, on ? false : auto);
        return Task.FromResult(Result.Success);
    }

    public Task<Result> SetHideCompletedFavouriteAsync(bool hide)
    {
        hideCompletedFavourite = hide;
        return Task.FromResult(Result.Success);
    }

    public Task<Result> ResumeStreakAsync()
    {
        if (!offerResume || resumeOwned == 0) return Task.FromResult(Result.Fail("Mis.Failed"));
        resumeOwned--;
        frozen = false;
        return Task.FromResult(Result.Success);
    }
}
