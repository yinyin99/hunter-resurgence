namespace Resurgence.Services;

/// <summary>A store category; <see cref="Key"/> is the address part (sales, all, bundles or a number).</summary>
public sealed record StoreCategory(string Key, string Name, IReadOnlyList<StoreCategory> Children);

/// <summary>An item; <see cref="WasEms"/> is the struck-through price of an item on sale, <see cref="Category"/> the key of its (sub)category.</summary>
public sealed record StoreItem(int Id, string Name, string Variant, string Category, int Ems, int? WasEms, int Gms, int Owned, bool Buyable, int Capacity, string Description);

public sealed record BundleLine(int ItemId, string Name, int Count, int Owned, int Value);

public sealed record StoreBundle(int Id, string Name, string Description, int Price, bool Buyable, IReadOnlyList<BundleLine> Lines);

public interface IStoreService
{
    Task<IReadOnlyList<StoreCategory>> CategoriesAsync();
    /// <param name="view">sales, all or a category key</param>
    /// <param name="search">Words that all have to be in the name; empty for no filter.</param>
    Task<Page<StoreItem>> ItemsAsync(string view, string search, int page);
    Task<IReadOnlyList<StoreBundle>> BundlesAsync();
    Task<StoreItem?> ItemAsync(int id);
    Task<StoreBundle?> BundleAsync(int id);
    /// <summary>Buys an item or a bundle with em$ or gm$.</summary>
    Task<Result> BuyAsync(int id, bool bundle, bool gms);
}
