namespace Resurgence.Services.Mocks;

/// <summary>Fictional lodges (one locked) and feeders (one active and half full, one in penalty); a refill fills the feeder and uses a pack or pays.</summary>
public sealed class MockGameManagementService : IGameManagementService
{
    readonly List<LodgeRow> lodges = [new("1", "Test Lodge", false, "Hunting Cabin", 12, 3), new("2", "Test Hideaway", true, "Mountain Lodge", 4, 1)];
    List<FeederRow> feeders = [new("a1", 101, "Test Feeder", 1, "Test Reserve", 41.5, 62.25, 25, 50, "active", DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds(), 30),
                               new("a2", 102, "Test Corn Feeder", 3, "Test Plains", 10, 20, 0, 50, "penalty", null, 0)];
    int packs = 1;

    public Task<IReadOnlyList<LodgeRow>> LodgesAsync() => Task.FromResult<IReadOnlyList<LodgeRow>>([.. lodges]);

    public Task<Result> SaveLodgeAsync(LodgeRow lodge, string name, bool locked)
    {
        var i = lodges.FindIndex(l => l.Id == lodge.Id);
        if (i < 0) return Task.FromResult(Result.Fail("GM.SaveFailed"));
        lodges[i] = lodges[i] with { Name = name, Locked = locked };
        return Task.FromResult(Result.Success);
    }

    public Task<FeederList> FeedersAsync() => Task.FromResult(new FeederList([.. feeders], packs, new(5, 500)));

    public Task<Result<RefillReply>> RefillAsync(string id, string currency, int maxAmount)
    {
        var i = feeders.FindIndex(f => f.Id == id);
        if (i < 0 || feeders[i].BaitOwned == 0 || feeders[i].BaitAmount >= maxAmount || currency == "pack" && packs == 0) return Task.FromResult(Result<RefillReply>.Fail("GM.RefillFailed"));
        var f = feeders[i];
        var added = Math.Min(f.BaitOwned, maxAmount - f.BaitAmount);
        if (currency == "pack") packs--;
        feeders[i] = f with { BaitAmount = f.BaitAmount + added, BaitOwned = f.BaitOwned - added, Status = "active", EmptyAt = DateTimeOffset.UtcNow.AddSeconds(8640L * (f.BaitAmount + added)).ToUnixTimeSeconds() };
        return Task.FromResult(Result<RefillReply>.Success(new(true, currency == "pack" ? 0 : currency == "em" ? 5 : 500, feeders[i].BaitAmount, feeders[i].EmptyAt)));
    }
}
