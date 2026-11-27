namespace Resurgence.Services.Mocks;

/// <summary>Fictional items, among them one with the id 0, one that is counted and one that can be sold back (a sale removes it).</summary>
public sealed class MockInventoryService : IInventoryService
{
    readonly List<InventoryItem> items =
    [
        new(0, "Test Rifle", "Blue", "4", "Rifles", null, true, Guid: 1),
        new(2, "Test Refill", "", "5", "Refills", 3, false, Guid: 2),
        new(3, "Test Scope", "", "6", "Sights", null, false, Guid: 3, Sell: new("gm", 40)),
    ];

    public Task<IReadOnlyList<InventoryItem>> ItemsAsync() => Task.FromResult<IReadOnlyList<InventoryItem>>([.. items]);

    public Task<Result> SellAsync(long guid) =>
        Task.FromResult(items.RemoveAll(i => i.Guid == guid && i.Sell is not null) > 0 ? Result.Success : Result.Fail("Inv.SellFailed"));
}
