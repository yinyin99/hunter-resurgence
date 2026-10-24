namespace Resurgence.Services.Mocks;

/// <summary>Mock "wallet:many" gives the em$ and gm$ lists more than one page of rows.</summary>
public sealed class MockWalletService(MockWalletState state, string? mock = null) : IWalletService
{
    static readonly string[] EmItems = ["Camp Flag", "Shooting Mat", "Archery Target", "Heated Tent", "Tree Stand", "Bait Barrel", "Decoy Set", "Field Scope"];

    // newest first; every row starts where the next newer one ended
    static IReadOnlyList<WalletEntry> Chain(int count, int end, Func<int, (int Amount, string Item, string Summary, bool Spent)> row)
    {
        var rows = new List<WalletEntry>();
        var after = end;
        for (var i = 0; i < count; i++)
        {
            var (amount, item, summary, spent) = row(i);
            var before = spent ? after + amount : after - amount;
            rows.Add(new(new DateTime(2026, 10, 20, 20, 57, 0).AddMinutes(-(i * 427 + i * i * 3)), amount, before, after, item, summary));
            after = before;
        }
        return rows;
    }

    readonly IReadOnlyList<WalletEntry> emChain = Chain(mock == "wallet:many" ? 130 : 35, 100, i => (2 + i * 7 % 28, EmItems[i % EmItems.Length], "In-game payment.", true));
    readonly IReadOnlyList<WalletEntry> gmChain = Chain(mock == "wallet:many" ? 130 : 43, 1000, i => (4 + i * 37 % 140, i % 6 == 5 ? EmItems[i % EmItems.Length] : "", i % 6 == 5 ? "In-game payment." : $"Reward (Id: {1110544551 - i * 1873})", i % 6 == 5));

    public Task<Balances> BalancesAsync() => Task.FromResult(new Balances(state.Ems, state.Gms, 150, 0));

    public Task<AccountMembership> MembershipAsync() => Task.FromResult(new AccountMembership(state.Membership, state.MembershipEnds));

    public Task<WalletSummary> SummaryAsync() => Task.FromResult(new WalletSummary(state.Ems, state.Gms, 200 + state.EmsSpent, 700 + state.GmsSpent));

    public Task<IReadOnlyList<BankPayment>> BankAsync(int offset) => Task.FromResult<IReadOnlyList<BankPayment>>(offset > 0 ? []
        : [.. state.Payments, new(new(2026, 10, 15, 12, 0, 0), "Gift", "Completed", "em$", 10.00m, "Provider", new(2026, 10, 15, 12, 5, 0), null)]);

    public Task<IReadOnlyList<WalletEntry>> EntriesAsync(bool gms, int offset) =>
        Task.FromResult<IReadOnlyList<WalletEntry>>((gms ? state.GmRows : state.EmRows).Concat(gms ? gmChain : emChain).Skip(offset).Take(IWalletService.PageSize).ToList());
}
