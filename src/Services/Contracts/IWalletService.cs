namespace Resurgence.Services;

public sealed record Balances(int Ems, int Gms, int AmmoDays, int FuelDays);

/// <summary>The wallet header: balances and totals spent, shown with two decimals.</summary>
public sealed record WalletSummary(decimal Ems, decimal Gms, decimal EmsSpent, decimal GmsSpent);

/// <summary>A purchase with real money; a gift has a code or the time it was redeemed.</summary>
public sealed record BankPayment(DateTime When, string Type, string Status, string Item, decimal Amount, string Provider, DateTime? Redeemed, string? GiftCode);

/// <summary>A purchase or reward in em$ or gm$ with the balance around it; <see cref="Item"/> may be empty.</summary>
public sealed record WalletEntry(DateTime When, int Amount, int Before, int After, string Item, string Summary);

public interface IWalletService
{
    /// <summary>Rows a list call returns at most; a full list may have more.</summary>
    const int PageSize = 50;

    Task<Balances> BalancesAsync();
    Task<WalletSummary> SummaryAsync();
    Task<IReadOnlyList<BankPayment>> BankAsync(int offset);
    Task<IReadOnlyList<WalletEntry>> EntriesAsync(bool gms, int offset);
}
