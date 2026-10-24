namespace Resurgence.Services.Mocks;

/// <summary>
/// Codes: GIFT is a gift of 500 em$ (once), SAVE20 takes 20% off. Payments go through the shared wallet state.
/// Mock "bank:steam206" answers the Steam internal error, "bank:steamerror" the other Steam error. A payment reference with "fail" in it did not complete.
/// </summary>
public sealed class MockBankService(MockWalletState state, string? mock = null) : IBankService
{
    static readonly BankCatalog Catalog = new("EUR",
        [new(500, 4.99m, 0), new(1000, 9.69m, 3), new(2000, 18.69m, 6), new(3000, 26.99m, 10), new(5000, 43.99m, 12)],
        [new("Starter Bundle A", 3, 14.99m, 2, 6, ["Rifle A", "Handgun A"], ["Heated Tent A", "Binoculars A", "Lure A", "Jacket A", "Blind A", "Bait Refill"])],
        [new("Special Bundle A", 3, 24.99m, 3, 8, ["Rifle B", "Shotgun A", "Bow A"], ["Heated Tent B", "Binoculars B", "Lure B", "Jacket B", "Blind B", "Bait Refill", "Scope A", "Camp Flag"]),
         new("Special Bundle B", 3, 24.99m, 3, 9, ["Rifle C", "Handgun B", "Crossbow A"], ["Heated Tent C", "Binoculars C", "Lure C", "Jacket C", "Blind C", "Bait Refill", "Scope B", "Camp Flag", "Tree Stand"])],
        [new("", 3, 11.99m, 0, 0), new("", 6, 19.99m, 0, 0), new("", 12, 34.99m, 0, 0)]);

    public Task<BankCatalog> CatalogAsync() => Task.FromResult(Catalog);

    BankCode? Find(string code) => code.Trim().ToUpperInvariant() switch
    {
        "GIFT" when !state.UsedCodes.Contains("GIFT") => new BankCode("GIFT", true, 0, "500 em$"),
        "SAVE20" => new BankCode("SAVE20", false, 20, ""),
        _ => null,
    };

    public Task<BankCode?> CheckCodeAsync(string code) => Task.FromResult(Find(code));

    public Task<Result> RedeemAsync(string code)
    {
        if (Find(code) is not { Gift: true } gift) return Task.FromResult(Result.Fail("Bank.Invalid"));
        state.UsedCodes.Add(gift.Name);
        state.Ems += 500;
        state.Payments.Insert(0, new(DateTime.UtcNow, "Gift", "Completed", gift.Product, 0, "Code", DateTime.UtcNow, null));
        return Task.FromResult(Result.Success);
    }

    // the platform call takes a moment, so a second click while it runs can be tried
    public async Task<Result> BuyAsync(string kind, int index, bool gift = false, string? code = null)
    {
        await Task.Delay(300);
        if (mock == "bank:steam206") return Result.Fail("Bank.SteamInternal");
        if (mock == "bank:steamerror") return Result.Fail("Bank.SteamError");
        var percent = code is null ? 0 : Find(code) is { Gift: false } d ? d.Percent : 0;
        decimal Price(decimal p) => Math.Round(p * (100 - percent) / 100, 2);
        var (item, price, ems, months) = kind switch
        {
            "ems" => (Catalog.EmPacks[index].Ems + " em$", Price(Catalog.EmPacks[index].Price), Catalog.EmPacks[index].Ems, 0),
            "starter" => (Catalog.Starter[index].Name, Price(Catalog.Starter[index].Price), 0, Catalog.Starter[index].Months),
            "special" => (Catalog.Special[index].Name, Price(Catalog.Special[index].Price), 0, Catalog.Special[index].Months),
            _ => ($"{Catalog.Basic[index].Months} months membership", Price(Catalog.Basic[index].Price), 0, Catalog.Basic[index].Months),
        };
        if (!gift)
        {
            state.Ems += ems;
            if (months > 0) state.MembershipEnds = (state.MembershipEnds is { } e && e > DateTime.UtcNow ? e : DateTime.UtcNow.Date).AddMonths(months);
        }
        state.Payments.Insert(0, new(DateTime.UtcNow, gift ? "Gift" : "Purchase", "Completed", item, price, "Steam", null, gift ? Guid.NewGuid().ToString("N")[..12].ToUpperInvariant() : null));
        return Result.Success;
    }

    public Task<BankTransaction> VerifyTransactionAsync(string reference) =>
        Task.FromResult(reference.Contains("fail", StringComparison.OrdinalIgnoreCase) ? new BankTransaction(false, "") : new BankTransaction(true, "Standard 500 em$"));

    public Task<string?> OfferwallUrlAsync() => Task.FromResult<string?>("about:blank");
}
