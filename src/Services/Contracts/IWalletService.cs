namespace Resurgence.Services;

public sealed record Balances(int Ems, int Gms, int AmmoDays, int FuelDays);
public sealed record WalletEntry(DateTime When, string What, int Amount);

public interface IWalletService
{
    Task<Balances> BalancesAsync();
    /// <param name="kind">bank, ems or gms</param>
    Task<IReadOnlyList<WalletEntry>> HistoryAsync(string kind);
}
