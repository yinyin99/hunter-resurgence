// Inventory/refillBarrel: the Bait Sites page's Refill of a placed feeder.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed partial class State
{
    const long RefillService = 791, RefillPackType = 38;
    static readonly string[] RefillShown = ["start_attr_ts", "bait_attractiveness", "feeder_status", "bait_amount", "start_bait_ts", "bait_amount_org"];

    /// <summary>Inventory/refillBarrel (JSON `barrel_id`, `currency`, `max_amount`): the bait the account owns for the feeder (its item's `bait_item` define) goes into it, up to
    /// `max_amount` (at most 50) and what the feeder still holds, by the rule of <see cref="Feeders.Refill"/>. The service is paid with `currency`: `pack` takes one use of a refill pack the account owns (type 38),
    /// `gold` (em$) or `silver` (gm$) the price of the refill service (item 791), which leaves no item; the new balance (update 15) comes with the next heartbeat. The bait leaves the stacks at once, a running hunt sees it at its next connect.
    /// `{price, success: true, metadata}` with the feeder's new `start_attr_ts`, `bait_attractiveness`, `feeder_status`, `bait_amount`, `start_bait_ts` and `bait_amount_org`; `price` is 0 for a pack.
    /// `{"success": false}` for a request that is not valid, a feeder that is not the account's, no bait or room, no pack, an unknown service or too little currency.</summary>
    public byte[] RefillBarrelReply(long account, byte[] body)
    {
        JsonObject? request;
        try { request = JsonNode.Parse(body) as JsonObject; }
        catch (JsonException) { return BUY_FAILED; }
        if (request is null || !Py.IsNum(request["barrel_id"]) || !Py.IsNum(request["max_amount"]) || request["currency"] is not JsonValue c || !c.TryGetValue<string>(out var currency))
            return BUY_FAILED;
        using var con = Con(account);
        con.Begin();
        if (con.Rows($"SELECT item_id, metadata FROM items WHERE account_id = $a AND guid = ? AND slot = {PLACED_SLOT} AND {Shown}", Py.L(request["barrel_id"])).FirstOrDefault() is not { } barrel
            || items.GetValueOrDefault((long)barrel[0]) is not { } feeder || !Py.IsStr(feeder["bait_item"]))
            return BUY_FAILED;
        var metadata = Parse(barrel[1]);
        var now = Clock();
        var baitIds = items.Values.Where(i => Py.IsStr(i["define"]) && Py.Str(i["define"]) == Py.Str(feeder["bait_item"])).Select(i => Py.L(i["id"])).ToList();
        var stock = con.Rows($"SELECT guid, units FROM items WHERE account_id = $a AND item_id IN ({string.Join(',', baitIds.Append(-1))}) AND units > 0 AND {Shown} ORDER BY guid");
        var left = Feeders.Left(metadata, now);
        var moved = Math.Min(Math.Clamp(Py.L(request["max_amount"]), 0, Feeders.MaxAmount) - left, stock.Sum(r => (long)r[1]));
        if (moved <= 0)
            return BUY_FAILED;
        double price = 0;
        if (currency == "pack")
        {
            var packIds = items.Values.Where(i => Is(i["type"], RefillPackType)).Select(i => Py.L(i["id"]));
            if (con.Rows($"SELECT guid FROM items WHERE account_id = $a AND item_id IN ({string.Join(',', packIds.Append(-1))}) AND units > 0 ORDER BY guid LIMIT 1").FirstOrDefault() is not { } pack)
                return BUY_FAILED;
            con.Exec("UPDATE items SET units = units - 1, slot = CASE WHEN units - 1 <= 0 THEN 0 ELSE slot END WHERE account_id = $a AND guid = ?", pack[0]);
        }
        else
        {
            if (!(currency is "gold" or "silver") || items.GetValueOrDefault(RefillService) is not { } service)
                return BUY_FAILED;
            var (wallet, key) = WALLETS[currency == "gold" ? "GOLD" : "SILVER"];
            if (!Sellable(service, key) || AddCurrency(con, wallet, -(price = Price(service, key)), PAYMENT, [RefillService]) is not { } balance)
                return BUY_FAILED;
            con.Exec("INSERT INTO pending (account_id, body) VALUES ($a, ?)", Py.Dumps(new JsonObject { ["amount"] = C(balance), ["type"] = wallet, ["update"] = 15 }));
        }
        var rest = moved;
        foreach (var r in stock)
        {
            var take = Math.Min(rest, (long)r[1]);
            con.Exec("UPDATE items SET units = units - ?1, slot = CASE WHEN units - ?1 <= 0 THEN 0 ELSE slot END WHERE account_id = $a AND guid = ?2", take, r[0]);
            rest -= take;
        }
        var fields = Feeders.Refill(metadata, left + moved, now)!;  // left + moved is above what is left
        con.Exec("UPDATE items SET metadata = json_patch(metadata, ?) WHERE account_id = $a AND guid = ?", Py.Dumps(fields), Py.L(request["barrel_id"]));
        con.Commit();
        return Py.DumpBytes(new JsonObject
        {
            ["price"] = price, ["success"] = true,
            ["metadata"] = new JsonObject(RefillShown.Select(k => KeyValuePair.Create(k, fields[k]!.DeepClone()))),
        });
    }
}
