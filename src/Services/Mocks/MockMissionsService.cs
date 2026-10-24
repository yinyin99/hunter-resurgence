namespace Resurgence.Services.Mocks;

/// <summary>One active and 19 available missions, 71 packs; nothing changes when something is activated.</summary>
public sealed class MockMissionsService : IMissionsService
{
    static readonly string[] Reserves = ["Reserve 01", "Reserve 02", "Reserve 03", "Reserve 04", "Reserve 05"];
    static readonly string[] Weapons = ["Rifle Model A", "Shotgun Model B", "Bow Model C"];

    static Mission Make(int id, string name, string state, int i) => new(id, name, state, $"A short briefing for {name}: find the animal, take your time and make a clean shot.",
        [$"Harvest a {Names.Species[i % Names.Species.Length].Name}"], "100 gm$", Reserves[i % Reserves.Length], Names.Species[i % Names.Species.Length].Name, Weapons[i % Weapons.Length]);

    static readonly Mission[] Missions =
    [
        Make(1, "Active Mission 01", "active", 0),
        .. Enumerable.Range(1, 19).Select(n => Make(10 + n, $"Available Mission {n:00}", "available", n)),
    ];

    static readonly MissionPack[] Packs = Names.Species.Select(s => s.Name + " Missions").Concat(Enumerable.Range(1, 20).Select(n => $"Special Pack {n:00}")).OrderBy(n => n)
        .Select((n, i) => Pack(i + 1, n, i % 7 == 0 ? 5 : 10)).ToArray();

    // the first mission of a pack can be activated, the others wait for it
    static MissionPack Pack(int id, string name, int total) => new(id, name, 0, total, false, $"Complete every mission of {name} to finish the pack.",
        Enumerable.Range(1, total).Select(n => Make(1000 + id * 20 + n, $"{name} {n:00}", n == 1 ? "available" : "locked", id + n)).ToList());

    public Task<IReadOnlyList<Mission>> ActiveAsync() => Task.FromResult<IReadOnlyList<Mission>>(Missions.Where(m => m.State == "active").ToList());

    public Task<DailyCalendar> DailyAsync() => Task.FromResult(new DailyCalendar(1,
        Enumerable.Range(1, 7).Select(d => new DailyDay(d, d == 4 ? 2 : 1, d == 4 ? 500 : 100)).ToList(),
        new("Spot a male Rusa Deer", "Hello, I need a hand with a small job. Help me and you will be rewarded.", ["Spot a male Rusa Deer"], "Reserve 05", ["100 gm$"], true)));

    public Task<RegularMissions> RegularAsync() => Task.FromResult(new RegularMissions(Missions, Packs, 1, 692, 0, 71, false, false));

    public Task<Result> ActivateAsync(int id) => Task.FromResult(Result.Success);

    public Task<Result> SetFavouriteAsync(int pack, bool favourite) => Task.FromResult(Result.Success);

    public Task<Result> SetAutomaticAsync(string setting, bool on) => Task.FromResult(Result.Success);
}
