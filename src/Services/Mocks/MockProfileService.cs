namespace Resurgence.Services.Mocks;

/// <summary>Hunter01 is the signed-in player (empty bars, as in the old capture); any other handle is a top player with full bars.</summary>
public sealed class MockProfileService : IProfileService
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

    static Milestone M(string title, int value, params int[] steps) => new($"{title} ({steps.Count(s => value >= s)}/{steps.Length})", steps.Count(s => value >= s), steps, value);

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

    public Task<Progress> ProgressAsync() => Task.FromResult(new Progress(74, 100, 12, new("Achievement One", "Placeholder description of the achievement.", new(2026, 7, 9))));
    public Task<int> ExpeditionCountAsync() => Task.FromResult(42);
}
