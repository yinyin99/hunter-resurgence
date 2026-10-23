namespace Resurgence.Services.Mocks;

/// <summary>Hunter01 is the signed-in player (empty bars, gallery and trophies, as in the old capture); any other handle is a top player with full bars. Mock "profile:photos" gives the signed-in player a gallery.</summary>
public sealed class MockProfileService(string? mock = null) : IProfileService
{
    const string Own = "Hunter01";

    public Task<Profile?> ProfileAsync(string handle) =>
        Task.FromResult<Profile?>(handle == Own ? new(handle, 74, 238892, "Hunter", true, true, false) : new(handle, 115000, 1, "Hunter", false, false, handle.Length % 2 == 0));

    static readonly string[] WeaponTypes = ["Lever Action Rifles", "Bolt Action Rifles", "Semi-Automatic Rifles", "Air Rifles", "Handguns", "Shotguns", "Bows", "Crossbows", "Muzzleloaders", "Revolvers"];
    static readonly string[] Collectables = ["Common Mushrooms", "Rare Mushrooms", "Treasures", "Feathers", "Fossils", "Pumpkins"];

    public Task<IReadOnlyList<RankBar>> RanksAsync(string handle, string kind)
    {
        var own = handle == Own;
        RankBar Bar(string name, int level, int total) => own ? new(name, kind == "collectables" && name == "Common Mushrooms" ? 1 : 0, 0, total) : new(name, level, total, total);
        IReadOnlyList<RankBar> bars = kind switch
        {
            "animals" => Names.Species.Select((s, i) => Bar(s.Name, 3 + i % 6, 100)).ToList(),
            "weapons" => Enumerable.Range(0, 40).Select(i => Bar($"{WeaponTypes[i % WeaponTypes.Length]} {(char)('A' + i / WeaponTypes.Length)}", 2 + i % 5, 100)).ToList(),
            "collectables" => Collectables.Select((c, i) => Bar(c, 1 + i % 3, 100)).ToList(),
            _ => [Bar("Hunter Score", 0, 115000), Bar("Animals", 0, 54), Bar("Weapons", 0, 40), Bar("Collectables", 0, 6)],
        };
        return Task.FromResult(bars);
    }

    public Task<AchievementSummary> AchievementSummaryAsync(string handle) => Task.FromResult(new AchievementSummary(
        [new("Animals", 0, 4, 54), new("Weapons", 0, 1, 100), new("Exploration", 0, 4, 16)], 29,
        [new("1 Moose", "1 Moose harvested in the reserve.", new(2026, 10, 20)), new("1 Water Buffalo", "1 Water Buffalo harvested in the reserve.", new(2026, 10, 20)),
         new("1 Rifle A (Standard)", "1 Rifle A (Standard) harvests in the reserve.", new(2026, 10, 20)), new("1 Sitka Deer", "1 Sitka Deer harvested in the reserve.", new(2026, 10, 19)),
         new("1 Wild Boar", "1 Wild Boar harvested in the reserve.", new(2026, 10, 19)), new("1 Red Fox", "1 Red Fox harvested in the reserve.", new(2026, 10, 18))]));

    static Milestone M(string title, int value, params int[] steps) => new(title, steps.Count(s => value >= s), steps, value);

    public Task<IReadOnlyList<Milestone>> MilestonesAsync(string handle, string kind)
    {
        int[] six = [1, 25, 100, 250, 500, 1000];
        IReadOnlyList<Milestone> rows = kind switch
        {
            "animals" => [M("Total", 0, 50, 250, 500, 1000, 2000, 4000, 8000, 16000, 32000), .. Names.Species.Select(s => M(s.Name, 0, six))],
            "weapons" => [M("Total", 0, 50, 250, 500, 1000, 2000, 4000, 8000, 16000, 32000), .. Enumerable.Range(0, 40).Select(i => M($"{WeaponTypes[i % WeaponTypes.Length]} {(char)('A' + i / WeaponTypes.Length)}", 0, six))],
            "exploration" => [M("Distance", 80, 10, 42, 233, 522, 1185, 1987, 5851, 40076), M("Duration", 30, 1, 24, 48, 72, 168, 336, 720, 8760)],
            _ => [M("Easy missions", 0, 1, 25, 50, 100, 250), M("Medium missions", 0, 1, 10, 25, 50, 100), M("Hard missions", 0, 1, 5, 10, 25, 50)],
        };
        return Task.FromResult(rows);
    }

    public Task<IReadOnlyList<Challenge>> ChallengesAsync(string handle) => Task.FromResult<IReadOnlyList<Challenge>>(
    [
        new("Quick Draw", "Kill three animals within 5 minutes"), new("Truffle Hunter", "Find five mushrooms in one hunt"), new("Rainy Day", "Kill three ducks with the same shot"),
        new("Far and Away", "Harvest a rabbit shot from over 250m"), new("Fast Food", "Harvest 10 elk within 5 minutes"), new("Triple Score", "Kill three land animals with the same shot"),
        new("Pincushion", "Hit the same animal five or more times with arrows"), new("Early Bird", "Harvest an animal within a minute of starting"),
    ]);

    public Task<IReadOnlyList<Skill>> SkillsAsync(string handle, string kind) => Task.FromResult<IReadOnlyList<Skill>>(kind == "weapons"
        ? Enumerable.Range(0, 30).Select(i => new Skill($"Weapon {(char)('A' + i % 26)}{i / 26 + 1} (Standard)", WeaponTypes[i / 3], [new("Shooting", 1, 0)])).ToList()
        : Names.Species.Select(s => new Skill(s.Name, null, [new("Spotting", 1, 0), new("Tracking", 1, 0)])).ToList());

    static Page<T> Slice<T>(IReadOnlyList<T> all, int page, int size)
    {
        var pages = Math.Max(1, (all.Count + size - 1) / size);
        var n = Math.Clamp(page, 1, pages);
        return new(all.Skip((n - 1) * size).Take(size).ToList(), n, pages);
    }

    public Task<Page<Photo>> GalleryAsync(string handle, int page) => Task.FromResult(Slice<Photo>(
        handle != Own || mock == "profile:photos" ? Enumerable.Range(0, 30).Select(i => new Photo(5473102 + i, Names.Species[i * 3 % Names.Species.Length].Name, 10.5 + i * 1.25)).ToList() : [], page, 24));

    public Task<Page<Trophy>> TrophiesAsync(string handle, int page) => Task.FromResult(Slice<Trophy>(
        handle == Own ? [] : Enumerable.Range(0, 26).Select(i => i % 3 == 0 ? new Trophy($"Competition Trophy {i}", new(2026, 9, 1 + i), 100 + i, $"Competition {i}") : new Trophy($"Trophy {i}", new(2026, 9, 1 + i), null, null)).ToList(), page, 24));

    public Task<IReadOnlyList<Dog>> DogsAsync(string handle) => Task.FromResult<IReadOnlyList<Dog>>(handle == Own ? []
        : [new("Rex", "Hound (Brown)", 3, ["Scent Tracking", "Retrieve"]), new("Doggie", "Retriever (Golden)", 1, [])]);

    public Task<Page<ProfileFriend>> FriendsAsync(string handle, int page) => Task.FromResult(Slice<ProfileFriend>(handle == Own
        ? [new("Ranger_Test", 181), new("Moss_Walker", 115000), new("Hunter02", 7076)]
        : Enumerable.Range(1, 45).Select(i => new ProfileFriend($"Friend_{i:00}", i * 311 % 9000)).ToList(), page, 20));

    static SheetRow R(string label, double? a, double? b) => new(label, a, b, a is { } x && b is { } y ? Math.Round(Math.Abs(x - y), 4) : null);

    public Task<ScoreSheet?> SheetAsync(string handle, string kind, long id)
    {
        var when = new DateTime(2026, 10, 2, 13, 42, 0);
        if (kind == "antler")
            return Task.FromResult<ScoreSheet?>(new("antler", "Axis Deer", [],
                [new("1. Length of Main Beam", 43.8976, 44.0945, 87.9921), new("2.1 Length of Typical Tines T-1", 13.6220, 13.5827, 27.2047), new("2.2 Length of Typical Tines T-2", 10.7480, 10.7480, 21.4961),
                 new("2.3 Length of Typical Tines T-3", 0.7480, 0.7480, 1.4961), new("3. Circumference of Burr", 6.4961, 6.6142, 13.1102), new("4. Circumference of Main Beam", 6.2205, 5.9449, 12.1654),
                 new("5. Inside Span of Main Beams", null, null, 46.2992)], [], [], "", false, "", 0, "", "", "", "", 0, "Common (M)", 0, 100, "Place B (3906, 11711)", 0, when.AddYears(-1), 131212595, 104.8819, null));
        return Task.FromResult<ScoreSheet?>(new("score", "Mule Deer", [new("A. No. Points on Right Antler", 5), new("No. Points on Left Antler", 5), new("B. Tip to Tip Spread", 19.7244), new("C. Greatest Spread", 23.4646), new("D. Inside Spread of Main Beams", 22.0472)],
            [R("F. Length of Main Beam", 21.8898, 27.2441), R("G-1. Length of First Point, if Present", 4.0551, 4.0551), R("G-2. Length of Second Point", 18.9370, 18.4252), R("G-3. Length of Third Point, if Present", 13.3858, 13.3071),
             R("G-4. Length of Fourth Point, if Present", 14.1732, 14.1732), R("H-1. Circumference at Smallest Place Between Burr and First Point", 4.4094, 4.7244),
             R("H-2. Circumference at Smallest Place Between First and Second Points", 4.2520, 4.6457), R("H-3. Circumference at Smallest Place Between Main Beam and Third Point", 4.4882, 4.2913),
             R("H-4. Circumference at Smallest Place Between Second and Fourth Points", 4.0157, 4.0945), new("Total", 89.6062, 94.9606, 6.9292)],
            [(0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0)],
            [new("Column 1", 22.0472), new("Column 2", 89.6062), new("Column 3", 94.9606), new("Subtotal", 206.6140), new("Subtract Column 4", 6.9292), new("Total", 199.6848)],
            "Rifle A", true, "Ammo Type 1", 63.010, "Idle", "Body (Right Lung, Shoulder Blade)", "Prone", "None", 133.264, "Common (M)", 6.004, 100, "Place A (-9593, 8198)", 1, when, 135160843, 199.6848, 89.993));
    }

    static readonly string[] Reserves = ["Reserve 01", "Reserve 02", "Reserve 03"], Lodges = ["Lodge A", "Lodge B", "Lodge C"];
    const long Latest = 135232661;

    static readonly WeaponStat[] WeaponStats =
    [
        new("Rifle A", "Ammo Type 1", 29, 79.31, 17, 73.91, 124.53), new("Rifle B", "Ammo Type 2", 21, 95.24, 18, 90.00, 145.40), new("Rifle C", "Ammo Type 3", 1, 100, 0, 0, null),
        new("Rifle D", "Ammo Type 4", 2, 50, 1, 100, 86.59), new("Shotgun A", "Ammo Type 5", 1, 100, 1, 100, 88.81),
    ];

    public Task<Totals> TotalsAsync(string handle) => Task.FromResult(new Totals(WeaponStats,
        Names.Species.Take(24).Select((s, i) => new SpeciesStat(s.Name, i * 7 % 6, i * 5 % 21, i * 3 % 11)).ToList(),
        [new("Common Mushroom", 1, 12, 12)], 102.89, 1 * 86400 + 16 * 3600 + 5 * 60 + 32));

    static Expedition Make(long id, int n)
    {
        // the latest expedition is empty, as in the old capture; the others have shots and kills
        var start = new DateTime(2026, 10, 22, 11, 16, 0).AddHours(-n * 9).AddMinutes(-n * 7);
        if (id == Latest) return new(id, Reserves[0], Lodges[0], start, start.AddMinutes(2), [], [], [], [], 0, 90, [], []);
        var species = Names.Species[(n * 5) % Names.Species.Length].Name;
        return new(id, Reserves[n % 3], Lodges[n % 3], start, start.AddMinutes(38 + n), [WeaponStats[n % 5]], [new(species, 2, 1, 1)], [new("Common Mushroom", 1, 12, 12)], ["1 " + species],
            3.2 + n, (38 + n) * 60 + n * 17 % 60, [], [new(1109872882 + n, species, WeaponStats[n % 5].Weapon, WeaponStats[n % 5].Ammo, 124.53 + n, 12.3456 + n, 85.5 + n, start.AddMinutes(20))]);
    }

    public Task<Expedition?> ExpeditionAsync(string handle, long? id) => Task.FromResult<Expedition?>(Make(id ?? Latest, id is null ? 0 : (int)(Latest - id.Value)));

    public Task<Page<HistoryRow>> HistoryAsync(string handle, int page)
    {
        var all = Enumerable.Range(0, 55).Select(n => Make(Latest - n, n)).Select(e => new HistoryRow(e.Id, e.Start, e.Seconds, e.Reserve, e.Lodge, e.Collectables.Count, e.Kills.Count)).ToList();
        var n = Math.Clamp(page, 1, 2);
        return Task.FromResult(new Page<HistoryRow>(all.Skip((n - 1) * 40).Take(40).ToList(), n, 2));
    }

    // the old capture of the own bests was blocked and shows only the disabled box; another hunter's bests are built from the source notes
    public Task<PersonalBest> BestAsync(string handle, int season) => Task.FromResult(handle == Own ? new PersonalBest("disabled", [])
        : season == 3 ? new("queued", []) : season == 4 ? new("building", []) : season == 5 ? new("empty", [])
        : new("done", Names.Species.Take(12).Select((s, i) => new BestRow(s.Name, 8.5 + i * 1.3, 60 + i * 11.7)).ToList()));

    public Task<Progress> ProgressAsync() => Task.FromResult(new Progress(74, 100, 12, new("Achievement One", "Placeholder description of the achievement.", new(2026, 7, 9))));
    public Task<int> ExpeditionCountAsync() => Task.FromResult(42);
}
