namespace Resurgence.Services;

/// <summary>A store category; <see cref="Key"/> is the address part (sales, all, bundles or a number).</summary>
public sealed record StoreCategory(string Key, string Name, IReadOnlyList<StoreCategory> Children);

/// <summary>Attachments that fit an item, under the title of their category.</summary>
public sealed record ItemFit(string Title, IReadOnlyList<StoreFit> Items);

public sealed record StoreFit(int Id, string Name);

/// <summary>Which ammunition is permitted for which species: <see cref="Rows"/> have one flag per ammunition column.</summary>
public sealed record EthicalMatrix(IReadOnlyList<string> Columns, IReadOnlyList<EthicalRow> Rows);

public sealed record EthicalRow(string Species, IReadOnlyList<bool> Permitted);

/// <summary>
/// An item; <see cref="WasEms"/> is the struck-through price of an item on sale, <see cref="Category"/> the key of its (sub)category.
/// <see cref="DaysLeft"/> is set for an item that is owned for a time (less than one day is 0.5); <see cref="OwnLimit"/> is 0 for no limit;
/// <see cref="Banner"/> is premium, limited, new or community; <see cref="MultiBuy"/> items (ammunition) are bought in amounts.
/// <see cref="Capacity"/> is the inventory weight.
/// </summary>
public sealed record StoreItem(int Id, string Name, string Variant, string Category, int Ems, int? WasEms, int Gms, int Owned, bool Buyable, int Capacity, string Description,
    double? DaysLeft = null, int OwnLimit = 0, bool DisabledByServices = false, string? Banner = null, bool MultiBuy = false,
    IReadOnlyList<ItemFit>? Fits = null, EthicalMatrix? Ethics = null)
{
    public bool LimitReached => OwnLimit > 0 && Owned >= OwnLimit;
}

/// <summary>A bundle line; <see cref="OwnLimit"/> is 0 for unlimited.</summary>
public sealed record BundleLine(int ItemId, string Name, int Count, int Owned, int Value, string Variant = "", int OwnLimit = 0);

public sealed record StoreBundle(int Id, string Name, string Description, int Price, bool Buyable, IReadOnlyList<BundleLine> Lines);

public interface IStoreService
{
    Task<IReadOnlyList<StoreCategory>> CategoriesAsync();
    /// <param name="view">sales, all or a category key</param>
    /// <param name="search">Words that all have to be in the name; empty for no filter.</param>
    Task<Page<StoreItem>> ItemsAsync(string view, string search, int page);
    Task<IReadOnlyList<StoreBundle>> BundlesAsync();
    /// <summary>Whether any item is on sale; the Sales entry of the store menu exists only then.</summary>
    Task<bool> HasSalesAsync();
    Task<StoreItem?> ItemAsync(int id);
    Task<StoreBundle?> BundleAsync(int id);
    /// <summary>Buys <paramref name="count"/> of an item, or a bundle, with em$ or gm$ (bundles only with em$).</summary>
    Task<Result> BuyAsync(int id, bool bundle, bool gms, int count = 1);
}
