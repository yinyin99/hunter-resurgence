using System.Text.Json.Serialization;

namespace Resurgence.Services;

/// <summary>A trophy lodge of the player (<c>GET /account/lodges</c>): <see cref="LodgeType"/> is the lodge type's display name, null when unknown.</summary>
public sealed record LodgeRow(string Id, string Name, bool Locked, [property: JsonPropertyName("lodge_type")] string? LodgeType, int Trophies, int Weapons);
public sealed record LodgeList(LodgeRow[] Lodges);

/// <summary>A placed feeder (<c>GET /account/feeders</c>): <see cref="Status"/> is active, inactive or penalty; <see cref="EmptyAt"/> unix seconds, null when it is not running; <see cref="BaitOwned"/> the matching bait in the inventory.</summary>
public sealed record FeederRow(
    string Id, [property: JsonPropertyName("item_id")] int ItemId, string Name, int Reserve, [property: JsonPropertyName("reserve_name")] string ReserveName, double X, double Y,
    [property: JsonPropertyName("bait_amount")] int BaitAmount, [property: JsonPropertyName("bait_max")] int BaitMax, string Status,
    [property: JsonPropertyName("empty_at")] long? EmptyAt, [property: JsonPropertyName("bait_owned")] int BaitOwned);
public sealed record RefillPrice(int Em, int Gm);
public sealed record FeederList(FeederRow[] Feeders, int Packs, [property: JsonPropertyName("refill_price")] RefillPrice RefillPrice);
public sealed record RefillReply(bool Success, int Price, [property: JsonPropertyName("bait_amount")] int BaitAmount, [property: JsonPropertyName("empty_at")] long? EmptyAt);

public interface IGameManagementService
{
    /// <summary>The player's own lodges; never throws: signed out or no answer is none.</summary>
    Task<IReadOnlyList<LodgeRow>> LodgesAsync();

    /// <summary>Renames and locks or unlocks a lodge; <see cref="Result.Error"/> is a localisation key.</summary>
    Task<Result> SaveLodgeAsync(LodgeRow lodge, string name, bool locked);

    /// <summary>The player's placed feeders, the refills in the inventory and the price of a paid refill; never throws: signed out or no answer is none.</summary>
    Task<FeederList> FeedersAsync();

    /// <summary>Refills a feeder up to <paramref name="maxAmount"/> paying with <paramref name="currency"/> (pack, em or gm); <see cref="Result{T}.Error"/> is a localisation key.</summary>
    Task<Result<RefillReply>> RefillAsync(string id, string currency, int maxAmount);
}
