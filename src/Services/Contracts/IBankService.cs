namespace Resurgence.Services;

public sealed record EmPack(int Ems, decimal Price, int Save);

/// <summary>A membership or bundle card; <see cref="Name"/> is empty for a plain membership. A bundle has extra weapons and other items, listed by name in <see cref="Weapons"/> and <see cref="Items"/>.</summary>
public sealed record Membership(string Name, int Months, decimal Price, int ExtraWeapons, int OtherItems, IReadOnlyList<string>? Weapons = null, IReadOnlyList<string>? Items = null);

public sealed record BankCatalog(string Currency, IReadOnlyList<EmPack> EmPacks, IReadOnlyList<Membership> Starter, IReadOnlyList<Membership> Special, IReadOnlyList<Membership> Basic);

/// <summary>A valid code: a gift holds the product it gives, a discount lowers every price by <see cref="Percent"/>.</summary>
public sealed record BankCode(string Name, bool Gift, int Percent, string Product);

/// <summary>The outcome of a payment the platform reports back: the product bought, or none when the purchase failed.</summary>
public sealed record BankTransaction(bool Completed, string Product);

public interface IBankService
{
    Task<BankCatalog> CatalogAsync();
    /// <summary>Checks a discount or gift code; null when it is invalid or expired.</summary>
    Task<BankCode?> CheckCodeAsync(string code);
    /// <summary>Adds the gift of a gift code to the account.</summary>
    Task<Result> RedeemAsync(string code);
    /// <summary>Buys a pack or membership with real money: <paramref name="kind"/> is ems, starter, special or basic. The error key tells what the platform answered (<c>Bank.SteamInternal</c> or <c>Bank.SteamError</c>).</summary>
    Task<Result> BuyAsync(string kind, int index, bool gift = false, string? code = null);
    /// <summary>What the platform says about a payment, by the reference it called back with.</summary>
    Task<BankTransaction> VerifyTransactionAsync(string reference);
    /// <summary>The address of the earn-em$ offer frame; null when the provider is unavailable.</summary>
    Task<string?> OfferwallUrlAsync();
}
