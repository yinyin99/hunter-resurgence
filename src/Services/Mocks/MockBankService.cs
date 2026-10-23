namespace Resurgence.Services.Mocks;

public sealed class MockBankService : IBankService
{
    static readonly BankCatalog Catalog = new("EUR",
        [new(500, 4.99m, 0), new(1000, 9.69m, 3), new(2000, 18.69m, 6), new(3000, 26.99m, 10), new(5000, 43.99m, 12)],
        [new("Starter Bundle A", 3, 14.99m, 2, 6)],
        [new("Special Bundle A", 3, 24.99m, 3, 8), new("Special Bundle B", 3, 24.99m, 3, 9)],
        [new("", 3, 11.99m, 0, 0), new("", 6, 19.99m, 0, 0), new("", 12, 34.99m, 0, 0)]);

    public Task<BankCatalog> CatalogAsync() => Task.FromResult(Catalog);

    // every code is unknown in the mock
    public Task<Result> CheckCodeAsync(string code) => Task.FromResult(Result.Fail("Bank.Invalid"));

    // nothing is bought in the mock
    public Task<Result> BuyAsync(string kind, int index) => Task.FromResult(Result.Success);
}
