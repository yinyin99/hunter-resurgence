namespace Resurgence.Services;

/// <summary>An owned item: <see cref="Units"/> is null for an item that is not counted; <see cref="Equipped"/> when it is in a slot. <see cref="Category"/> is the store's sub-category key, "" for none. <see cref="Expires"/> is the end of a license or timed item in unix seconds, 0 for an unlimited one, null for an item with none.</summary>
public sealed record InventoryItem(int Id, string Name, string Variant, string Category, string CategoryName, int? Units, bool Equipped, long? Expires = null);

public interface IInventoryService
{
    /// <summary>The signed-in player's items, one per owned copy; never throws: signed out or no answer is none.</summary>
    Task<IReadOnlyList<InventoryItem>> ItemsAsync();
}
