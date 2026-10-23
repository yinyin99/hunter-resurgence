namespace Resurgence.Services.Mocks;

public sealed class MockBankService : IBankService
{
    static readonly BankCatalog Catalog = new(
        [new(500), new(1000), new(2000), new(3000), new(5000)],
        [new("Starter Bundle A", 3, 2, 6)],
        [new("Special Bundle A", 3, 3, 8), new("Special Bundle B", 3, 3, 9)],
        [new("", 3, 0, 0), new("", 6, 0, 0), new("", 12, 0, 0)]);

    public Task<BankCatalog> CatalogAsync() => Task.FromResult(Catalog);

    // every code is unknown in the mock
    public Task<Result> CheckCodeAsync(string code) => Task.FromResult(Result.Fail("Bank.Invalid"));
}
