using System.Text.Json.Nodes;

namespace Resurgence.Restore;

/// <summary>The item catalogue of the game's Application/application reply (a server's own copy, never one taken from an upload):
/// an item has units when its <c>units</c> is an object (a count), not null.</summary>
public sealed class ApplicationCatalogue : IItemCatalogue
{
    readonly HashSet<int> withUnits;

    ApplicationCatalogue(HashSet<int> withUnits) => this.withUnits = withUnits;

    public bool HasUnits(int itemId) => withUnits.Contains(itemId);

    /// <exception cref="InvalidDataException">No <c>items</c> list.</exception>
    public static ApplicationCatalogue Read(Stream json)
    {
        var items = JsonNode.Parse(json)?["items"] as JsonArray ?? throw new InvalidDataException("not an item catalogue: no items list");
        return new([.. items.OfType<JsonObject>().Where(i => i["units"] is JsonObject && i["id"] is JsonValue v && v.TryGetValue<int>(out _)).Select(i => i["id"]!.GetValue<int>())]);
    }
}
