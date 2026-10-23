namespace Resurgence.Services.Mocks;

public sealed class MockWalletService : IWalletService
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

    static readonly IReadOnlyList<WalletEntry> Ems = Chain(35, 100, i => (2 + i * 7 % 28, EmItems[i % EmItems.Length], "In-game payment.", true));
    static readonly IReadOnlyList<WalletEntry> Gms = Chain(43, 1000, i => (4 + i * 37 % 140, i % 6 == 5 ? EmItems[i % EmItems.Length] : "", i % 6 == 5 ? "In-game payment." : $"Reward (Id: {1110544551 - i * 1873})", i % 6 == 5));

    public Task<Balances> BalancesAsync() => Task.FromResult(new Balances(100, 1000, 150, 0));

    public Task<WalletSummary> SummaryAsync() => Task.FromResult(new WalletSummary(100, 1000, 200, 700));

    public Task<IReadOnlyList<BankPayment>> BankAsync(int offset) => Task.FromResult<IReadOnlyList<BankPayment>>(offset > 0 ? [] : [new(new(2026, 10, 15, 12, 0, 0), "Gift", "Completed", "em$", 10.00m, "Provider", new(2026, 10, 15, 12, 5, 0), null)]);

    public Task<IReadOnlyList<WalletEntry>> EntriesAsync(bool gms, int offset) => Task.FromResult<IReadOnlyList<WalletEntry>>((gms ? Gms : Ems).Skip(offset).Take(IWalletService.PageSize).ToList());
}
