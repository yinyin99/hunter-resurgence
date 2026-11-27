// How many copies of an item a player owns (docs/server.md#ownership-limits): the facts file's `own_limits` table, one function that applies it (<see cref="OwnLimit(long)"/>) to every path that
// puts an item into an inventory (the store, Inventory/buy, bundles, restores and claims, the start kit, admin grants), the offer of a bundle to a player who owns some of its items, and the pass at start
// that brings the inventories made before the rule down to it.
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed partial class State
{
    /// <summary>Vehicle Fuel 25, 50 and 100 units: not sold while <see cref="UnlimitedFuel"/> is on.</summary>
    static readonly long[] FuelItems = [953, 954, 955];

    /// <summary>settings.json `unlimited_fuel`: the connect carries the game's unlimited-fuel service (a jeep never uses up its fuel) and <see cref="FuelItems"/> are not sold. Off: fuel is an item like any other.</summary>
    public bool UnlimitedFuel { get; init; }

    bool IsUnlimitedFuel(long itemId) => UnlimitedFuel && FuelItems.Contains(itemId);

    /// <summary>The most copies of the item a player owns, null for no limit: what the facts file's `own_limits` says of the item or of its `client_type` (a number, 0 for no limit, or the catalogue's own), then the catalogue's
    /// own `ownLimit` as a cap. The catalogue's own is its `ownMultiple` false, which is 1: an item with `units` (a one-use order or refill) is exempt from it, as the original store never showed one as owned.</summary>
    long? LimitOf(JsonObject? item)
    {
        if (item is null)
            return null;
        long? limit = null;
        switch (facts.OwnRule(Py.L(item["id"]), item["client_type"] is { } type && Py.IsNum(type) ? Py.L(type) : -1))
        {
            case null:
                if (item["ownMultiple"] is not null && !Py.Truthy(item["ownMultiple"]) && !Py.Truthy(item["units"]))
                    limit = 1;
                break;
            case 0:
                break;
            case var n:
                limit = n;
                break;
        }
        return Py.Truthy(item["ownLimit"]) ? Math.Min(limit ?? long.MaxValue, Py.L(item["ownLimit"])) : limit;
    }

    /// <summary>The most copies of the item a player owns (<see cref="LimitOf"/>), null for no limit or an item no definition is known of.</summary>
    internal long? OwnLimit(long itemId) => LimitOf(items.GetValueOrDefault(itemId) ?? installItems.GetValueOrDefault(itemId));

    /// <summary>How many more copies of the item the account may own: long.MaxValue for no limit. Call inside a transaction.</summary>
    long Room(Db con, long itemId) =>
        OwnLimit(itemId) is { } limit ? Math.Max(0, limit - (long)con.Scalar("SELECT COUNT(*) FROM items WHERE account_id = $a AND item_id = ?", itemId)!) : long.MaxValue;

    /// <summary>An item no rule of the limits applies to: an ammunition (unlimited ammunition makes its own copies of it), a license (its end is in its row) and a fuel.</summary>
    bool Unlimited(long itemId) => IsAmmo(itemId) || IsLicense(items.GetValueOrDefault(itemId) ?? installItems.GetValueOrDefault(itemId)) || FuelItems.Contains(itemId);

    /// <summary>The rows over their item's limit (<see cref="OwnLimit(long)"/>), the copies in use kept first (<paramref name="inUse"/>: equipped, in a weapon or scope slot, placed in the world, worn by a dog),
    /// then the oldest (<paramref name="guid"/> lowest); the other rows of the same item are returned. An ammunition, a license, a fuel and an item with no limit have none.</summary>
    internal IEnumerable<T> Excess<T>(IEnumerable<T> rows, Func<T, long> item, Func<T, bool> inUse, Func<T, long> guid) =>
        rows.GroupBy(item).SelectMany(g => Unlimited(g.Key) || OwnLimit(g.Key) is not { } limit ? Enumerable.Empty<T>()
                                                                                        : g.OrderByDescending(inUse).ThenBy(guid).Skip((int)Math.Min(limit, int.MaxValue)));

    /// <summary>The copies of the account's items over their limit that the pass removed, by (account, guid, item id): the removed row whole, so an operator can put it back.</summary>
    const string Removed = "own_limit_removals";

    /// <summary>The pass, once (community servers, run at start after the lodge backfill; the operator copies the database before the deploy that brings it, docs/deploy.md): for each account and item over its limit, the
    /// copies in use stay up to the limit, then the oldest; the rest are removed (also an equipped one when more copies are in use than the limit), with no refund, and each removed row is recorded in `own_limit_removals`.
    /// An ammunition, a scope or any other kind with no limit is never touched. It runs once: when it has finished it leaves a row in `own_limit_pass`, and a later start (a tighter `own_limits`, another catalogue)
    /// removes nothing. One line per account that lost copies.</summary>
    public List<string> EnforceOwnLimits()
    {
        var lines = new List<string>();
        using (var shared = Store.Open())
            if (shared.Scalar("SELECT 1 FROM own_limit_pass") is not null)
                return lines;
        foreach (var account in Store.AccountIds())
        {
            using var con = Con(account);
            con.Begin();
            var rows = con.Rows("SELECT guid, item_id, slot, metadata, units FROM items WHERE account_id = $a");
            var removed = Excess(rows, r => (long)r[1], r => (long)r[2] > 0 || ((string)r[3]).Contains(POSITION, StringComparison.Ordinal), r => (long)r[0]).ToList();
            foreach (var r in removed)
            {
                con.Exec("DELETE FROM items WHERE account_id = $a AND guid = ?", r[0]);
                con.Exec($"INSERT INTO {Removed} (account_id, ts, guid, item_id, slot, metadata, units) VALUES ($a, ?, ?, ?, ?, ?, ?)", (long)Clock(), r[0], r[1], r[2], r[3], r[4]);
            }
            if (removed.Count > 0)
                lines.Add($"own limits: account {account} lost {removed.Count} copies ({string.Join(", ", removed.GroupBy(r => (long)r[1]).OrderBy(g => g.Key).Select(g => $"{g.Count()} x item {g.Key}"))})");
            con.Commit();
        }
        using (var shared = Store.Open())
            shared.Exec("INSERT INTO own_limit_pass (done_ts) VALUES (?)", (long)Clock());
        return lines;
    }

    /// <summary>What a bundle gives an account and costs it now (<see cref="Offer"/>): the items it gives (each at most up to its limit, an item already owned up to its limit is skipped), the price, and how many of its
    /// <paramref name="Of"/> items are <paramref name="Owned"/> (skipped). A copy a bundle lists beyond the item's limit is neither: it is not given, not paid for, and not counted in <paramref name="Of"/>.</summary>
    sealed record BundleOffer(long[] Give, long Price, long GmPrice, int Owned, int Of);

    /// <summary>The bundle for this account, null when the store does not sell it at all (<see cref="BundleSale"/>). The items a bundle lists more often than their limit allows, and those the account owns up to their limit, are not given;
    /// the price drops by their share: each item's own em$ list price (the catalogue's, whatever sale is on) over the sum of those of the bundle's items, `price × (1 − share)` rounded up to a whole em$, at least 1; `price` is the bundle's
    /// price under a sale that lists it (<see cref="BundlePrice"/>), so the sale's percentage comes first and the share second.
    /// Nothing given (<c>Give</c> empty): not for sale, at its list price (the sale's, while one is on). Call inside a transaction.</summary>
    BundleOffer? Offer(Db con, JsonNode b)
    {
        if (BundleSale(b) is not { } ids)
            return null;
        var room = new Dictionary<long, long>();
        var listed = new Dictionary<long, long>();
        var give = new List<long>();
        long total = 0, skipped = 0;
        int owned = 0, of = 0;
        foreach (var id in ids)
        {
            var value = Whole(items[id]["price"]);
            total += value;
            var left = room.TryGetValue(id, out var r) ? r : Room(con, id);
            room[id] = left - 1;
            listed[id] = listed.GetValueOrDefault(id) + 1;
            if (left > 0)
            {
                give.Add(id);
                of++;
            }
            else
            {
                skipped += value;
                if (OwnLimit(id) is not { } limit || listed[id] <= limit)  // the first copies up to the limit are the ones the player owns; a listed copy beyond it is only left out
                {
                    owned++;
                    of++;
                }
            }
        }
        long price = BundlePrice(b), gmPrice = BundlePrice(b, true);  // the sale's percentage first, then the share of what the player owns
        if (skipped > 0 && total > 0 && give.Count > 0)
        {
            price = Math.Max(1, (price * (total - skipped) + total - 1) / total);
            gmPrice = Math.Max(1, (gmPrice * (total - skipped) + total - 1) / total);
        }
        return new BundleOffer([.. give], price, gmPrice, owned, of);
    }

    /// <summary>Whether the store leaves a bundle out instead of showing it as not for sale: one made only of ammunition while `unlimited_ammo` is on, or one that lists a fuel while `unlimited_fuel` is.</summary>
    bool LeftOut(JsonNode b)
    {
        long[] ids = [.. Arr(b["items"]).Select(x => Py.L(x))];
        return ids.Any(IsUnlimitedFuel) || UnlimitedAmmo && ids.Length > 0 && ids.All(i => items.GetValueOrDefault(i) is { } item && IsUnlimitedAmmo(item));
    }

    /// <summary>`GET /account/bundles`: what each bundle <see cref="StoreCatalogue"/> lists costs this account now, `{bundles: [{id, price, owned, of, buyable}]}`: `price` is the server's own (<see cref="Offer"/>), `owned` of `of` items are
    /// not given again, `buyable` is false when the store does not sell it or it would give nothing.</summary>
    public JsonObject BundleOffers(long account)
    {
        using var con = Con(account);
        var bundles = new JsonArray();
        foreach (var b in Arr(G(application, "bundles", null)).Where(x => !LeftOut(x!)))
        {
            var offer = Offer(con, b!);
            bundles.Add(new JsonObject
            {
                ["id"] = C(b!["id"]), ["price"] = offer?.Price ?? BundlePrice(b), ["gms"] = offer?.GmPrice ?? BundlePrice(b, true), ["owned"] = offer?.Owned ?? 0, ["of"] = offer?.Of ?? Arr(b["items"]).Count,
                ["buyable"] = offer is { Give.Length: > 0 },
            });
        }
        return new JsonObject { ["bundles"] = bundles };
    }
}
