namespace Resurgence.Services;

/// <summary>A store category; <see cref="Key"/> is the address part (sales, all, bundles or a number).</summary>
public sealed record StoreCategory(string Key, string Name, IReadOnlyList<StoreCategory> Children);

/// <summary>Attachments that fit an item, under the title of their category; <see cref="Key"/> is the store's key of that category (the text `Store.Cat.{key}` translates), "" when unknown.</summary>
public sealed record ItemFit(string Title, IReadOnlyList<StoreFit> Items, string Key = "");

public sealed record StoreFit(int Id, string Name, string Variant = "");

/// <summary>Which ammunition is permitted for which species: <see cref="Rows"/> have one flag per ammunition column.</summary>
public sealed record EthicalMatrix(IReadOnlyList<StoreFit> Columns, IReadOnlyList<EthicalRow> Rows);

public sealed record EthicalRow(int Id, string Species, IReadOnlyList<bool> Permitted);

/// <summary>The sale an item is on: <see cref="Percent"/> off until <see cref="Until"/> (the end of the sale period).</summary>
public sealed record StoreSale(string Name, int Percent, DateTimeOffset Until);

/// <summary>
/// An item; <see cref="WasEms"/> and <see cref="WasGms"/> are the struck-through prices of an item on sale (null for a price that is not), <see cref="Sale"/> the sale (null without one), <see cref="Category"/> the key of its (sub)category.
/// <see cref="DaysLeft"/> is set for an item that is owned for a time (less than one day is 0.5); <see cref="OwnLimit"/> is 0 for no limit;
/// <see cref="Banner"/> is premium, limited, new or community; <see cref="MultiBuy"/> items (ammunition) are bought in amounts.
/// <see cref="Capacity"/> is the inventory weight as the store shows it, with no unit (0 for none); <see cref="Contents"/> is what an item that has units holds. <see cref="Owned"/> is a count of copies, or the units of a <see cref="Counted"/> item; <see cref="Expires"/> is the end of an owned license in unix seconds, 0 when unlimited; <see cref="Unlimited"/> is an ammunition the server gives every player without limit (not sold); <see cref="Fuel"/> is a vehicle fuel the server gives every player without limit (not sold).
/// </summary>
public sealed record StoreItem(int Id, string Name, string Variant, string Category, int Ems, int? WasEms, int Gms, int Owned, bool Buyable, double Capacity, string Description,
    double? DaysLeft = null, int OwnLimit = 0, bool DisabledByServices = false, string? Banner = null, bool MultiBuy = false,
    IReadOnlyList<ItemFit>? Fits = null, EthicalMatrix? Ethics = null,
    bool Counted = false, long? Expires = null, StoreContents? Contents = null, bool Unlimited = false, bool Fuel = false, int? WasGms = null, StoreSale? Sale = null)
{
    /// <summary>One of its prices is on sale.</summary>
    public bool OnSale => WasEms is not null || WasGms is not null;

    /// <summary>No more to buy: the own limit is reached, or the player has a license without end.</summary>
    public bool LimitReached => Expires == 0 || (OwnLimit > 0 && Owned >= OwnLimit);
}

/// <summary>What an item holds: <see cref="Count"/> of <see cref="Unit"/>, the catalogue's word for it (cartridges, arrows, uses, ...).</summary>
public sealed record StoreContents(int Count, string Unit);

/// <summary>A bundle line; <see cref="OwnLimit"/> is 0 for unlimited.</summary>
public sealed record BundleLine(int ItemId, string Name, int Count, int Owned, int Value, string Variant = "", int OwnLimit = 0);

/// <summary><see cref="Price"/> is what the signed-in player pays now; <see cref="WasPrice"/> the list price when it is higher (the player owns some of the items up to their limit), else 0; <see cref="OwnedItems"/> of its <see cref="OfItems"/> items are not given again (a copy listed beyond an item's limit is not counted).
/// <see cref="PreSale"/> is the list price before the bundle's <see cref="Sale"/> (null when it is not on sale). <see cref="Gms"/> is the same price in gm$ (0 for none), <see cref="PreSaleGms"/> the gm$ list price before the sale and <see cref="WasGms"/> the gm$ list price when the player's own items make it lower (else 0).</summary>
public sealed record StoreBundle(int Id, string Name, string Description, int Price, bool Buyable, IReadOnlyList<BundleLine> Lines, int WasPrice = 0, int OwnedItems = 0, int OfItems = 0, int? PreSale = null, StoreSale? Sale = null, int Gms = 0, int? PreSaleGms = null, int WasGms = 0)
{
    public bool OnSale => PreSale is not null || PreSaleGms is not null;

    /// <summary>The price to strike through next to <see cref="Price"/>: the list price before the sale when it is on sale (before the player's own items save anything too), else what the player's items save; 0 for none.</summary>
    public int StruckPrice => PreSale ?? WasPrice;

    /// <summary>The same for <see cref="Gms"/>.</summary>
    public int StruckGms => PreSaleGms ?? WasGms;
}

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
    /// <summary>Forget what the player owns, so the next read asks again; a page does this when it opens (a purchase does it itself).</summary>
    void RefreshOwned();
    /// <summary>Buys <paramref name="count"/> of an item, or a bundle, with em$ or gm$ (bundles only with em$).</summary>
    Task<Result> BuyAsync(int id, bool bundle, bool gms, int count = 1);
}
