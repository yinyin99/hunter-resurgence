namespace Resurgence.Services.Mocks;

public sealed class MockStoreService : IStoreService
{
    static readonly StoreItem[] Items = Enumerable.Range(1, 12).Select(i => new StoreItem(i, $"Item {i:00}", (i % 3 + 1).ToString(), i * 25)).ToArray();

    public Task<IReadOnlyList<StoreItem>> ItemsAsync(string category) =>
        Task.FromResult<IReadOnlyList<StoreItem>>(Items.Where(i => category is "all" or "sales" or "bundles" || i.Category == category).ToList());

    public Task<StoreItem?> ItemAsync(int id) => Task.FromResult(Items.FirstOrDefault(i => i.Id == id));
}
