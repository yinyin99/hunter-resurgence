// Selling an unused item back to the store (#928, community server): `POST /account/sell` and the `sell` offer of each row of `GET /account/inventory`. docs/server.md#selling-back.
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed partial class State
{
    /// <summary>The summary of the wallet row of a sale: this text and the item's name.</summary>
    public const string SOLD = "Sold: ";

    /// <summary>The share of the price a sale pays back, in percent (rounded down).</summary>
    const long SellBackPercent = 40;

    /// <summary>What selling a copy pays: the item, the currency of the wallet (em, gm) and the amount.</summary>
    public sealed record SellBackOffer(long ItemId, string Currency, long Refund);

    /// <summary>The offer for one inventory copy, or null and why not (the reason key `POST /account/sell` answers: `unknown`, `hunting`, `equipped`, `placed`, `ammunition`, `consumable`,
    /// `license`, `lodge`, `start_kit`, `not_sold`, `no_proof`, `too_cheap`). Only a copy in the lodge (slot 0, not placed) that a purchase of this server (its price paid, in its currency) or a restore
    /// (the catalogue's gm$ price; never a start item, never an item the store does not sell for gm$) tied to it in `purchases` is sold; no time limit. Call inside the account's connection.</summary>
    (SellBackOffer? Offer, string Why) SellCheck(Db con, long guid, long itemId, long slot, string metadata, object? units, bool hunting)
    {
        if (hunting) return (null, "hunting");
        if (slot != 0) return (null, "equipped");
        if (metadata.Contains(POSITION, StringComparison.Ordinal) || metadata.Contains(CLONE, StringComparison.Ordinal)) return (null, "placed");
        if (items.GetValueOrDefault(itemId) is not { } item) return (null, "not_sold");
        if (IsAmmo(itemId)) return (null, "ammunition");
        if (units is long || Box(itemId) is not null) return (null, "consumable");
        if (IsLicense(item) || metadata.Contains("\"expires\"", StringComparison.Ordinal)) return (null, "license");
        if (facts.LodgeOfItem(itemId) is not null) return (null, "lodge");
        if (con.Rows("SELECT currency, paid, restored FROM purchases WHERE account_id = $a AND guid = ? AND item_id = ?", guid, itemId).FirstOrDefault() is not { } p) return (null, "no_proof");
        var (currency, paid) = ((string)p[0], (long)p[1]);
        if ((long)p[2] == 1)
        {
            if (StartItems().Any(k => Py.L(k!["item_id"]) == itemId)) return (null, "start_kit");
            if (!StoreSells(item, "silver_price")) return (null, "not_sold");
            paid = Whole(item["silver_price"]);
        }
        var refund = paid * SellBackPercent / 100;
        return refund > 0 ? (new SellBackOffer(itemId, currency, refund), "") : (null, "too_cheap");
    }

    /// <summary>The website's and the launcher's sale (`POST /account/sell`): the copy <paramref name="guid"/> of the account's own inventory is removed and its refund paid, with the wallet row `Sold: <item>`;
    /// the running game gets the new balance with its next heartbeat and the inventory at its next connect. The offer and the new balance, or null and the reason key (<see cref="SellCheck"/>; `unknown` for a copy the account does not hold).</summary>
    public (SellBackOffer? Offer, JsonNode? Balance, string Why) WebSell(long account, long guid)
    {
        using var con = Con(account);
        con.Begin();
        if (con.Rows("SELECT item_id, slot, metadata, units FROM items WHERE account_id = $a AND guid = ?", guid).FirstOrDefault() is not { } r)
            return (null, null, "unknown");
        var (offer, why) = SellCheck(con, guid, (long)r[0], (long)r[1], (string)r[2], r[3], HuntAlive(con));
        if (offer is null)
            return (null, null, why);
        con.Exec("DELETE FROM items WHERE account_id = $a AND guid = ?", guid);
        con.Exec("DELETE FROM purchases WHERE account_id = $a AND guid = ?", guid);
        var balance = AddCurrency(con, offer.Currency, offer.Refund, SOLD + Py.Str(items[offer.ItemId]["name"]), [offer.ItemId]);
        if (balance is null)
            return (null, null, "unknown");
        con.Exec("INSERT INTO pending (account_id, body) VALUES ($a, ?)", Py.Dumps(new JsonObject { ["amount"] = C(balance), ["type"] = offer.Currency, ["update"] = 15 }));
        con.Commit();
        return (offer, balance, "");
    }
}
