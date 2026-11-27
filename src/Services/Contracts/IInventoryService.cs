namespace Resurgence.Services;

/// <summary>What selling a copy back pays: <see cref="Currency"/> is <c>em</c> or <c>gm</c>.</summary>
public sealed record SellOffer(string Currency, long Refund);

/// <summary>An owned item: <see cref="Units"/> is null for an item that is not counted; <see cref="Equipped"/> when it is in a slot. <see cref="Category"/> is the store's sub-category key, "" for none. <see cref="Expires"/> is the end of a license or timed item in unix seconds, 0 for an unlimited one, null for an item with none.
/// <see cref="Guid"/> names the copy; <see cref="Sell"/> is what selling it back pays, null when it cannot be sold (docs/server.md#selling-back).</summary>
public sealed record InventoryItem(int Id, string Name, string Variant, string Category, string CategoryName, int? Units, bool Equipped, long? Expires = null, long Guid = 0, SellOffer? Sell = null);

public interface IInventoryService
{
    /// <summary>The signed-in player's items, one per owned copy; never throws: signed out or no answer is none.</summary>
    Task<IReadOnlyList<InventoryItem>> ItemsAsync();

    /// <summary>Sells the copy <paramref name="guid"/> back to the store; <see cref="Result.Error"/> is a localisation key when the server refused or did not answer.</summary>
    Task<Result> SellAsync(long guid);
}
