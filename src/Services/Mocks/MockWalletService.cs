namespace Resurgence.Services.Mocks;

public sealed class MockWalletService : IWalletService
{
    public Task<Balances> BalancesAsync() => Task.FromResult(new Balances(4, 1078, 159, 0));

    public Task<IReadOnlyList<WalletEntry>> HistoryAsync(string kind) =>
        Task.FromResult<IReadOnlyList<WalletEntry>>(Enumerable.Range(1, 5).Select(i => new WalletEntry(new(2026, 1, i), $"Purchase {i}", i * 10)).ToList());
}
