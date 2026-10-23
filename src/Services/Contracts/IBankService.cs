namespace Resurgence.Services;

public sealed record EmPack(int Ems);

/// <summary>A membership or bundle card; <see cref="Name"/> is empty for a plain membership. A bundle has extra weapons and other items.</summary>
public sealed record Membership(string Name, int Months, int ExtraWeapons, int OtherItems);

public sealed record BankCatalog(IReadOnlyList<EmPack> EmPacks, IReadOnlyList<Membership> Starter, IReadOnlyList<Membership> Special, IReadOnlyList<Membership> Basic);

public interface IBankService
{
    Task<BankCatalog> CatalogAsync();
    /// <summary>Checks a discount or gift code; the error key tells what is wrong with it.</summary>
    Task<Result> CheckCodeAsync(string code);
}
