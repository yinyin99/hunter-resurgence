// The taxidermy of a harvest made here (#666): the game's purchase in the harvest window and the player's own page both come here, with the same
// rules and price (facts `taxidermy`). A harvest that a restore brought is never offered: only the harvests in the `harvests` table are.
using System.Globalization;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed partial class State
{
    /// <summary>The window after the harvest in which it can be taxidermized (the original's was 24).</summary>
    public const int TaxidermyHours = 72;
    public const string TAXIDERMY = "taxidermy", SKULL = "skull";
    const long TaxidermyItem = 744, SkullItem = 1111;  // the store items the wallet row names

    /// <summary>When a harvest's taxidermized page was made (Unix seconds); null for a page without a time.</summary>
    static long? Harvested(JsonObject page) =>
        DateTimeOffset.TryParse((string?)page["timestamp"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at.ToUnixTimeSeconds() : null;

    /// <summary>When the window of a harvest page ends (Unix seconds): 72 hours after the harvest, but for a harvest made before the taxidermy went live (<see cref="Store.TaxidermyGrace"/>) the end of
    /// the grace window, 72 hours after that day. Null for a page without a time.</summary>
    static long? TaxidermyEnd(JsonObject page, (long Start, long End) grace) =>
        Harvested(page) is { } at ? at < grace.Start ? grace.End : at + TaxidermyHours * 3600L : null;

    /// <summary>Whether a harvest is in the lodge editor's list: once taxidermized, or while the grace window of a harvest made before the taxidermy went live is open.</summary>
    bool InEditor(JsonObject page, (long Start, long End) grace) =>
        TaxidermyDone(page) is not null || Harvested(page) < grace.Start && Clock() <= grace.End;

    /// <summary>What a harvest page has become: <see cref="SKULL"/> or <see cref="TAXIDERMY"/> once done (also a harvest the game marked before the price existed), else null.</summary>
    static string? TaxidermyDone(JsonObject page) => !Py.Truthy(page["taxidermized"]) ? null : Py.Truthy(page["is_skull"]) ? SKULL : TAXIDERMY;

    /// <summary>Buy the taxidermy or the skull mount (<paramref name="product"/>) of a harvest made here with `em` or `gm`: the price is taken, the wallet row is written (the store item's) and the
    /// new balance reaches the game with its next heartbeat, all in one transaction. 200; 404 for a harvest that is unknown, a restored one or another player's; 409 `already` (once only);
    /// 403 `disqualified` (today's ammunition rule judges the kill unethical, <see cref="Disqualified"/>: no mount, as the original's harvest window offered none);
    /// 410 `too_late` (more than <see cref="TaxidermyHours"/> after the harvest); 400 `no_skull` (the species has none); 402 `not_enough`. The caller has checked the product and the currency.</summary>
    public (int Status, string? Error) Taxidermize(long account, long killId, string product, string currency)
    {
        using var con = Con(account);
        con.Begin();
        if (con.Rows("SELECT h.body, k.individual, k.harvest FROM harvests h LEFT JOIN kills k ON k.account_id = $a AND k.kill_id = h.kill_id WHERE h.account_id = $a AND h.kill_id = ?", killId).FirstOrDefault() is not { } row
            || JsonNode.Parse((string)row[0]) is not JsonObject page)
            return (404, "unknown");
        if (TaxidermyDone(page) is not null)
            return (409, "already");
        if (row[2] is string harvest && Disqualified(con, row[1], harvest, page))
            return (403, "disqualified");
        if (TaxidermyEnd(page, Store.TaxidermyGrace()) is not { } end || Clock() > end)
            return (410, "too_late");
        var skull = product == SKULL;
        if (skull && !facts.HasSkull((string?)AsTypical((JsonObject)page.DeepClone())["species_define"]))
            return (400, "no_skull");
        var (ems, gms) = facts.TaxidermyPrice();
        var price = currency == "gm" ? gms : ems;
        if (price > 0)
        {
            if (AddCurrency(con, currency, -price, PAYMENT, [skull ? SkullItem : TaxidermyItem]) is not { } balance)
                return (402, "not_enough");
            con.Exec("INSERT INTO pending (account_id, body) VALUES ($a, ?)", Py.Dumps(new JsonObject { ["amount"] = C(balance), ["type"] = currency, ["update"] = 15 }));
        }
        page["taxidermized"] = true;
        if (skull)
            page["is_skull"] = true;  // a skull mount is a skull trophy of the lodge editor
        page["taxidermy"] = new JsonObject { ["currency"] = currency, ["price"] = price, ["ts"] = (long)Clock() };  // what the operator's undo gives back
        con.Exec("UPDATE harvests SET body = ? WHERE account_id = $a AND kill_id = ?", Py.Dumps(page), killId);
        con.Commit();
        return (200, null);
    }

    /// <summary>The player's own page of a hunt: per harvest made here `{end, ems, gms, skull, done}` (<c>end</c> ISO UTC, the window's end; <c>skull</c> whether the species has a skull to mount),
    /// keyed by kill id. A restored harvest has none, nor has a kill today's ammunition rule disqualifies while its window is open and it is not done (<see cref="Taxidermize"/> refuses it).</summary>
    public Dictionary<long, JsonObject> TaxidermyOffers(long account)
    {
        var (ems, gms) = facts.TaxidermyPrice();
        var grace = Store.TaxidermyGrace();
        using var con = Con(account);
        var offers = new Dictionary<long, JsonObject>();
        foreach (var r in con.Rows("SELECT h.kill_id, h.body, k.individual, k.harvest FROM harvests h LEFT JOIN kills k ON k.account_id = $a AND k.kill_id = h.kill_id WHERE h.account_id = $a"))
            if (JsonNode.Parse((string)r[1]) is JsonObject page && TaxidermyEnd(page, grace) is { } end
                && (TaxidermyDone(page) is not null || Clock() > end || r[3] is not string harvest || !Disqualified(con, r[2], harvest, page)))
                offers[(long)r[0]] = new JsonObject
                {
                    ["end"] = DateTimeOffset.FromUnixTimeSeconds(end).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), ["ems"] = ems, ["gms"] = gms,
                    ["skull"] = facts.HasSkull((string?)AsTypical(page)["species_define"]), ["done"] = TaxidermyDone(page),
                };
        return offers;
    }

    /// <summary>Admin: the account's taxidermized harvests, newest first: `{kill, species, harvested, done, currency, paid, ts}`; `currency`, `paid` and `ts` (Unix seconds) are null for one the game
    /// marked before the price existed.</summary>
    public JsonArray Taxidermies(long account)
    {
        using var con = Con(account);
        var rows = new JsonArray();
        foreach (var r in con.Rows("SELECT kill_id, body FROM harvests WHERE account_id = $a ORDER BY kill_id DESC"))
            if (JsonNode.Parse((string)r[1]) is JsonObject page && TaxidermyDone(page) is { } done)
                rows.Add(new JsonObject
                {
                    ["kill"] = (long)r[0], ["species"] = C(page["species_define"]), ["harvested"] = C(page["timestamp"]), ["done"] = done,
                    ["currency"] = C(page["taxidermy"]?["currency"]), ["paid"] = C(page["taxidermy"]?["price"]), ["ts"] = C(page["taxidermy"]?["ts"]),
                });
        return rows;
    }

    /// <summary>Admin: take a taxidermy back: the harvest is not taxidermized (nor a skull) any more, so it leaves the lodge editor's list and can be bought again within its window; the price is given back
    /// (a wallet row, the new balance with the next heartbeat) when <paramref name="refund"/>. The refunded `(currency, amount)`, or null when the harvest is not taxidermized.</summary>
    public (string? Currency, long Amount)? UndoTaxidermy(long account, long killId, bool refund)
    {
        using var con = Con(account);
        con.Begin();
        if (con.Rows("SELECT body FROM harvests WHERE account_id = $a AND kill_id = ?", killId).FirstOrDefault() is not { } row || JsonNode.Parse((string)row[0]) is not JsonObject page
            || TaxidermyDone(page) is null)
            return null;
        var paid = page["taxidermy"] is { } record ? Py.L(record["price"]) : 0;
        var currency = (string?)page["taxidermy"]?["currency"];
        var given = 0L;
        if (refund && paid > 0 && currency is not null && AddCurrency(con, currency, paid, "Taxidermy undone by the operator.") is { } balance)
        {
            given = paid;
            con.Exec("INSERT INTO pending (account_id, body) VALUES ($a, ?)", Py.Dumps(new JsonObject { ["amount"] = C(balance), ["type"] = currency, ["update"] = 15 }));
        }
        page["taxidermized"] = false;
        page["is_skull"] = false;
        page.Remove("taxidermy");
        con.Exec("UPDATE harvests SET body = ? WHERE account_id = $a AND kill_id = ?", Py.Dumps(page), killId);
        con.Commit();
        return (currency, given);
    }
}
