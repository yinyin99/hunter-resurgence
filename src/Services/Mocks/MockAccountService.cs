namespace Resurgence.Services.Mocks;

public sealed class MockAccountService : IAccountService
{
    string avatar = "avatar-05";
    public event Action? Changed;

    public Task<Account> AccountAsync() => Task.FromResult(new Account(avatar));

    // 54 pictures and one locked one
    public Task<IReadOnlyList<AvatarChoice>> AvatarsAsync() =>
        Task.FromResult<IReadOnlyList<AvatarChoice>>([.. Enumerable.Range(1, 54).Select(i => new AvatarChoice($"avatar-{i:00}", $"Avatar {i}", false)), new("avatar-locked", "Locked Avatar", true)]);

    public Task<Result> SetAvatarAsync(string id) { avatar = id; Changed?.Invoke(); return Task.FromResult(Result.Success); }

    static RewardItem I(string text, int id) => new(text, id);
    static RewardItem Em(int n) => new($"{n} Em$", null);

    // no recruits yet
    public Task<Recruiting> RecruitingAsync() => Task.FromResult(new Recruiting("https://example.com/signup?ref=Hunter01",
    [
        new("500 Em$", [Em(50)], [I("Scent Spray - 10 Uses", 161)]),
        new("1000 Em$", [Em(100), I("Camp Supplies - 10 Pack", 75)], [I("Scent Spray - 10 Uses", 161)]),
        new("2000 Em$", [Em(200), I("Camp Supplies - 15 Pack", 76)], [I("Scent Spray - 10 Uses", 161), I("Carved Call", 77)]),
        new("3000 Em$", [Em(300), I("Camp Supplies - 50 Pack", 78)], [I("Scent Spray - 10 Uses", 161), I("Deer Caller", 79), I("Tree Stand - Forest Camo", 80)]),
        new("5000 Em$", [Em(500), I("Camp Supplies - 50 Pack x 2", 78)], [I("Scent Spray - 10 Uses", 161), I("Deer Rattle", 81), I("Tree Stand - Forest Camo", 80), I("Rifle - Engraved", 82), I("Hunting Tower - Woodland", 83)]),
    ], []));
}
