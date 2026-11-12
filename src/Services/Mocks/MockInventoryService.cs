namespace Resurgence.Services.Mocks;

/// <summary>Fictional items, among them one with the id 0 and one that is counted.</summary>
public sealed class MockInventoryService : IInventoryService
{
    static readonly InventoryItem[] Items =
    [
        new(0, "Test Rifle", "Blue", "4", "Rifles", null, true),
        new(2, "Test Refill", "", "5", "Refills", 3, false),
        new(3, "Test Scope", "", "6", "Sights", null, false),
    ];

    public Task<IReadOnlyList<InventoryItem>> ItemsAsync() => Task.FromResult<IReadOnlyList<InventoryItem>>(Items);
}
