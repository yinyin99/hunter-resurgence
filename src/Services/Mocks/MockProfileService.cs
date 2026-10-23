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

    public Task<Progress> ProgressAsync() => Task.FromResult(new Progress(74, 100, 12, new("Achievement One", "Placeholder description of the achievement.", new(2026, 7, 9))));
    public Task<int> ExpeditionCountAsync() => Task.FromResult(42);
}
