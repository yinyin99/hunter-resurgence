namespace Resurgence.Services.Mocks;

/// <summary>Hunter01 is the signed-in player (empty bars, gallery and trophies); any other handle is a top player with full bars. Mock "profile:photos" gives the signed-in player a gallery.</summary>
public sealed class MockProfileService(string? mock = null, IFriendsService? friends = null) : IProfileService
{
    const string Own = "Hunter01";

    // friend and request state come from the shared friends mock, so the dropdown, this page and the friends tab agree
    public async Task<Profile?> ProfileAsync(string handle) =>
        handle == Own ? new(handle, 50, 200000, "Hunter", true, true, false)
        : friends is null ? new(handle, 115000, 1, "Hunter", false, false, handle.Length % 2 == 0)
        : new(handle, 115000, 1, "Hunter", false, false, (await friends.FriendsAsync()).Any(f => f.Handle == handle), (await friends.PendingAsync()).Contains(handle));

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

    // the own gallery starts empty (as captured), with the hook "profile:photos" it has 30 pictures; deleting changes the list
    readonly List<Photo> own = mock == "profile:photos" ? [.. Photos()] : [];

    static IEnumerable<Photo> Photos() => Enumerable.Range(0, 30).Select(i => new Photo(5000000 + i, Names.Species[i * 3 % Names.Species.Length].Name, 10.5 + i * 1.25, $"https://example.test/gallery/{5000000 + i}.jpg"));

    public Task<Page<Photo>> GalleryAsync(string handle, int page) => Task.FromResult(Slice<Photo>(handle == Own ? own : [.. Photos()], page, 24));

    public Task<Result> DeleteGalleryImageAsync(long id) => Task.FromResult(own.RemoveAll(p => p.Id == id) > 0 ? Result.Success : Result.Fail("Mis.Failed"));

    // a small real zip of the pictures' names, so the browser has a file to save
    public Task<byte[]> GalleryZipAsync(string handle)
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
        using (var w = new StreamWriter(zip.CreateEntry("gallery.txt").Open()))
            foreach (var p in handle == Own ? own : Photos()) w.WriteLine($"{p.Id} {p.Animal} {p.Score}");
        return Task.FromResult(ms.ToArray());
    }

    public Task<Page<Trophy>> TrophiesAsync(string handle, int page) => Task.FromResult(Slice<Trophy>(
        handle == Own ? [] : Enumerable.Range(0, 26).Select(i => i % 3 == 0 ? new Trophy($"Competition Trophy {i}", new(2026, 9, 1 + i), 100 + i, $"Competition {i}") : new Trophy($"Trophy {i}", new(2026, 9, 1 + i), null, null)).ToList(), page, 24));

    public Task<IReadOnlyList<Dog>> DogsAsync(string handle) => Task.FromResult<IReadOnlyList<Dog>>(handle == Own ? []
        : [new("Rex", "Hound (Brown)", 3, 64, [new("Blood Tracking", "This skill governs how well a dog can find blood tracks.", 2), new("Detection", "This skill governs how well a dog can detect different species of animals.", 1)], ["Competition Trophy 3", "Trophy 1"]),
           new("", "Retriever (Golden)", 1, 10, [], [])]);

    public async Task<Page<ProfileFriend>> FriendsAsync(string handle, int page) => Slice<ProfileFriend>(handle == Own
        ? (friends is null ? ["Ranger_Test", "Moss_Walker", "Hunter02"] : (await friends.FriendsAsync()).Select(f => f.Handle)).Select(h => new ProfileFriend(h, h switch { "Ranger_Test" => 100, "Moss_Walker" => 115000, "Hunter02" => 7000, _ => h.Length * 311 })).ToList()
        : Enumerable.Range(1, 45).Select(i => new ProfileFriend($"Friend_{i:00}", i * 311 % 9000)).ToList(), page, 20);

    static SheetRow R(string label, double? a, double? b) => new(label, a, b, a is { } x && b is { } y ? Math.Round(Math.Abs(x - y), 4) : null);

    public Task<ScoreSheet?> SheetAsync(string handle, string kind, long id)
    {
        var when = new DateTime(2026, 10, 2, 13, 42, 0);
        if (kind == "antler")
            return Task.FromResult<ScoreSheet?>(new("antler", "Axis Deer", [],
                [new("1. Length of Main Beam", 40.0, 40.0, 80.0), new("2.1 Length of Typical Tines T-1", 12.0, 12.0, 24.0), new("2.2 Length of Typical Tines T-2", 10.0, 10.0, 20.0),
                 new("2.3 Length of Typical Tines T-3", 1.0, 1.0, 2.0), new("3. Circumference of Burr", 6.0, 6.0, 12.0), new("4. Circumference of Main Beam", 6.0, 6.0, 12.0),
                 new("5. Inside Span of Main Beams", null, null, 45.0)], [], [], "", false, "", 0, "", "", "", "", 0, "Common (M)", 0, 100, "Place B (2000, 3000)", 0, when.AddYears(-1), 100000050, 97.5, null));
        return Task.FromResult<ScoreSheet?>(new("score", "Mule Deer", [new("A. No. Points on Right Antler", 5), new("No. Points on Left Antler", 5), new("B. Tip to Tip Spread", 20.0), new("C. Greatest Spread", 24.0), new("D. Inside Spread of Main Beams", 22.0)],
            [R("F. Length of Main Beam", 22.0, 24.0), R("G-1. Length of First Point, if Present", 4.0, 4.0), R("G-2. Length of Second Point", 18.0, 17.0), R("G-3. Length of Third Point, if Present", 13.0, 13.0),
             R("G-4. Length of Fourth Point, if Present", 14.0, 14.0), R("H-1. Circumference at Smallest Place Between Burr and First Point", 4.5, 4.5),
             R("H-2. Circumference at Smallest Place Between First and Second Points", 4.0, 4.5), R("H-3. Circumference at Smallest Place Between Main Beam and Third Point", 4.5, 4.0),
             R("H-4. Circumference at Smallest Place Between Second and Fourth Points", 4.0, 4.0), new("Total", 88.0, 89.0, 4.0)],
            [(0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0)],
            [new("Column 1", 22.0), new("Column 2", 88.0), new("Column 3", 89.0), new("Subtotal", 199.0), new("Subtract Column 4", 4.0), new("Total", 195.0)],
            "Rifle A", true, "Ammo Type 1", 60.0, "Idle", "Body (Right Lung, Shoulder Blade)", "Prone", "None", 130.0, "Common (M)", 6.0, 100, "Place A (1000, 2000)", 1, when, 100000060, 195.0, 90.0));
    }

    static readonly string[] Reserves = ["Reserve 01", "Reserve 02", "Reserve 03"], Lodges = ["Lodge A", "Lodge B", "Lodge C"];
    const long Latest = 100000100;

    static readonly WeaponStat[] WeaponStats =
    [
        new("Rifle A", "Ammo Type 1", 20, 80, 16, 100, 120.0), new("Rifle B", "Ammo Type 2", 10, 50, 5, 100, 150.0), new("Rifle C", "Ammo Type 3", 1, 100, 0, 0, null),
        new("Rifle D", "Ammo Type 4", 2, 50, 1, 100, 90.0), new("Shotgun A", "Ammo Type 5", 1, 100, 1, 100, 80.0),
    ];

    public Task<Totals> TotalsAsync(string handle) => Task.FromResult(new Totals(WeaponStats,
        Names.Species.Take(24).Select((s, i) => new SpeciesStat(s.Name, i * 7 % 6, i * 5 % 21, i * 3 % 11)).ToList(),
        [new("Common Mushroom", 1, 12, 12)], 100.0, 2 * 86400));

    Expedition Make(long id, int n)
    {
        // the latest expedition is empty; the others have shots and kills
        var start = new DateTime(2026, 10, 22, 11, 16, 0).AddHours(-n * 9).AddMinutes(-n * 7);
        if (id == Latest) return new(id, Reserves[0], Lodges[0], start, mock == "profile:ongoing" ? null : start.AddMinutes(2), [], [], [], [], 0, 90, [], []);
        var species = Names.Species[(n * 5) % Names.Species.Length].Name;
        return new(id, Reserves[n % 3], Lodges[n % 3], start, start.AddMinutes(38 + n), [WeaponStats[n % 5]], [new(species, 2, 1, 1)], [new("Common Mushroom", 1, 12, 12)], ["1 " + species],
            3.2 + n, (38 + n) * 60 + n * 17 % 60, [], [new(100000200 + n, species, WeaponStats[n % 5].Weapon, WeaponStats[n % 5].Ammo, 120.0 + n, 100.0 + n, 85.0 + n, start.AddMinutes(20))]);
    }

    // the kills of the player's last two expeditions can still be mounted: the offer runs out a day after the kill
    public Task<Expedition?> ExpeditionAsync(string handle, long? id)
    {
        // an id outside the made-up history (the captured one is a real id) shows the nearest entry
        var n = id is null ? 0 : (int)Math.Clamp(Latest - id.Value, 0, 54);
        var e = Make(id ?? Latest, n);
        if (handle == Own && n is 1 or 2) e = e with { Kills = e.Kills.Select(k => k with { Taxidermy = new(DateTime.UtcNow.AddHours(24 - n * 5), 120, 2000) }).ToList() };
        return Task.FromResult<Expedition?>(e);
    }

    public Task<Page<HistoryRow>> HistoryAsync(string handle, int page)
    {
        var all = Enumerable.Range(0, 55).Select(n => Make(Latest - n, n)).Select(e => new HistoryRow(e.Id, e.Start, e.Seconds, e.Reserve, e.Lodge, e.Collectables.Count, e.Kills.Count)).ToList();
        var n = Math.Clamp(page, 1, 2);
        return Task.FromResult(new Page<HistoryRow>(all.Skip((n - 1) * 40).Take(40).ToList(), n, 2));
    }

    // the own bests show only the disabled box; another hunter's bests are made up. Season 3 is queued then building then done, season 4 building then done: each read goes a step on
    readonly Dictionary<int, int> reads = [];

    public Task<PersonalBest> BestAsync(string handle, int season)
    {
        if (handle == Own) return Task.FromResult(new PersonalBest("disabled", []));
        var step = reads[season] = reads.GetValueOrDefault(season) + 1;
        if (season == 5) return Task.FromResult(new PersonalBest("empty", []));
        if (season == 3 && step == 1) return Task.FromResult(new PersonalBest("queued", []));
        if (season == 3 && step == 2 || season == 4 && step == 1) return Task.FromResult(new PersonalBest("building", []));
        return Task.FromResult(new PersonalBest("done", Names.Species.Take(12).Select((s, i) => new BestRow(s.Name, 8.5 + i * 1.3, 60 + i * 11.7, WeaponStats[i % 5].Weapon, new DateTime(2026, 10, 2, 13, 42, 0).AddDays(-i * 3), 100000200 + i)).ToList()));
    }

    public Task<Progress> ProgressAsync() => Task.FromResult(new Progress(50, 100, 12, new("Achievement One", "Placeholder description of the achievement.", new(2026, 7, 9))));
    public Task<int> ExpeditionCountAsync() => Task.FromResult(42);
}
