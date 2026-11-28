// Inventory/refillBarrel: the Bait Sites page's Refill of a placed feeder.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed partial class State
{
    const long RefillService = 791, RefillPackType = 38;
    static readonly string[] RefillShown = ["start_attr_ts", "bait_attractiveness", "feeder_status", "bait_amount", "start_bait_ts", "bait_amount_org"];

    /// <summary>The (guid, units) stacks of the bait a feeder's catalogue item takes (its `bait_item` define) that the account owns, oldest first.</summary>
    List<object[]> BaitStock(Db con, JsonObject feeder)
    {
        var baitIds = items.Values.Where(i => Py.IsStr(i["define"]) && Py.Str(i["define"]) == Py.Str(feeder["bait_item"])).Select(i => Py.L(i["id"]));
        return con.Rows($"SELECT guid, units FROM items WHERE account_id = $a AND item_id IN ({string.Join(',', baitIds.Append(-1))}) AND units > 0 AND {Shown} ORDER BY guid");
    }

    /// <summary>The catalogue ids of the refill packs, as an SQL list.</summary>
    string PackIds => string.Join(',', items.Values.Where(i => Is(i["type"], RefillPackType)).Select(i => Py.L(i["id"])).Append(-1));

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
        var stock = BaitStock(con, feeder);
        var left = Feeders.Left(metadata, now);
        var moved = Math.Min(Math.Clamp(Py.L(request["max_amount"]), 0, Feeders.MaxAmount) - left, stock.Sum(r => (long)r[1]));
        if (moved <= 0)
            return BUY_FAILED;
        double price = 0;
        if (currency == "pack")
        {
            if (con.Rows($"SELECT guid FROM items WHERE account_id = $a AND item_id IN ({PackIds}) AND units > 0 ORDER BY guid LIMIT 1").FirstOrDefault() is not { } pack)
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

    /// <summary>The unix second a feeder's bait runs out (the fill's start plus 8,640 s a unit); null for a feeder never filled.</summary>
    static long? EmptyAt(JsonObject md) =>
        md["start_bait_ts"] is { } start && Py.IsNum(start) && md["bait_amount_org"] is { } org && Py.IsNum(org) ? Py.L(start) + Feeders.UnitSeconds * Py.L(org) : null;

    /// <summary>The catalogue item and refreshed metadata of the account's placed feeder `guid` (a placed item whose catalogue item takes bait); null for any other.</summary>
    (long ItemId, JsonObject Item, JsonObject Md)? PlacedFeeder(Db con, long guid)
    {
        if (con.Rows($"SELECT item_id, metadata FROM items WHERE account_id = $a AND guid = ? AND slot = {PLACED_SLOT} AND {Shown}", guid).FirstOrDefault() is not { } r
            || items.GetValueOrDefault((long)r[0]) is not { } item || !Py.IsStr(item["bait_item"]))
            return null;
        var md = Parse(r[1]);
        Feeders.Refresh(md, Clock());
        return ((long)r[0], item, md);
    }

    /// <summary>`GET /account/feeders`: the account's placed feeders by reserve and id with the bait they hold, `{feeders: [...], packs, refill_price: {em, gm}}` (docs/server.md, "Game management").</summary>
    public JsonObject AccountFeeders(long account)
    {
        var reserves = new Dictionary<long, string>();
        foreach (var r in Arr(G(application, "reserves", null)).OfType<JsonObject>())
            if (TryKey(r["id"], out var id))
                reserves.TryAdd(id, Py.IsStr(r["name"]) ? (string)r["name"]! : "");
        using var con = Con(account);
        var now = Clock();
        var feeders = con.Rows($"SELECT guid FROM items WHERE account_id = $a AND slot = {PLACED_SLOT} AND {Shown}").Select(r => (long)r[0])
            .Select(guid => (Guid: guid, Feeder: PlacedFeeder(con, guid))).Where(f => f.Feeder is not null)
            .Select(f => (f.Guid, Feeder: f.Feeder!.Value, Reserve: TryKey(f.Feeder!.Value.Md[RESERVE], out var rs) ? rs : 0L)).OrderBy(f => f.Reserve).ThenBy(f => f.Guid).ToList();
        var owned = new Dictionary<string, long>();  // bait define -> units owned
        var list = new JsonArray();
        foreach (var (guid, (itemId, item, md), reserve) in feeders)
        {
            var define = Py.Str(item["bait_item"]);
            if (!owned.TryGetValue(define, out var have))
                owned[define] = have = BaitStock(con, item).Sum(r => (long)r[1]);
            var at = md[POSITION] as JsonObject;
            list.Add(new JsonObject
            {
                ["id"] = guid.ToString(), ["item_id"] = itemId, ["name"] = Py.IsStr(item["name"]) ? (string)item["name"]! : "", ["reserve"] = reserve, ["reserve_name"] = reserves.GetValueOrDefault(reserve, ""),
                ["x"] = at is null ? 0 : Math.Round(Py.D(at["x"]) / 1000, 1), ["y"] = at is null ? 0 : Math.Round(Py.D(at["z"]) / 1000, 1),  // metres (millimetres stored)
                ["bait_amount"] = Feeders.Left(md, now), ["bait_max"] = Feeders.MaxAmount, ["status"] = (Py.IsStr(md["feeder_status"]) ? (string)md["feeder_status"]! : "Inactive").ToLowerInvariant(),
                ["empty_at"] = EmptyAt(md), ["bait_owned"] = have,
            });
        }
        var service = items.GetValueOrDefault(RefillService);
        long PriceIn(string key) => service is not null && Sellable(service, key) ? Price(service, key) : 0;
        return new JsonObject
        {
            ["feeders"] = list, ["packs"] = Convert.ToInt64(con.Scalar($"SELECT COALESCE(SUM(units), 0) FROM items WHERE account_id = $a AND item_id IN ({PackIds}) AND units > 0")),
            ["refill_price"] = new JsonObject { ["em"] = PriceIn("price"), ["gm"] = PriceIn("silver_price") },
        };
    }

    /// <summary>`POST /account/feeders/&lt;guid&gt;/refill` `{currency: pack|em|gm, max_amount}`: <see cref="RefillBarrelReply"/> for the account's feeder; `{success, price, bait_amount, empty_at}` (the feeder as it is after).
    /// 400 for a currency or amount that is not valid, 404 for a feeder the account has not; a refill that cannot be made is 200 with `success` false.</summary>
    public (int Status, JsonObject? Body) AccountRefill(long account, long guid, byte[] body)
    {
        string? currency;
        long amount;
        try
        {
            var sent = JsonNode.Parse(body) as JsonObject;
            currency = sent?["currency"] is JsonValue c && c.TryGetValue<string>(out var s) ? s : null;
            if (currency is not ("pack" or "em" or "gm") || sent?["max_amount"] is not JsonValue m || !m.TryGetValue(out amount) || amount is < 0 or > Feeders.MaxAmount)
                return (400, null);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        {
            return (400, null);
        }
        using (var con = Con(account))
            if (PlacedFeeder(con, guid) is null)
                return (404, null);
        var reply = (JsonObject)JsonNode.Parse(RefillBarrelReply(account, Py.DumpBytes(new JsonObject
        { ["barrel_id"] = guid, ["currency"] = currency == "em" ? "gold" : currency == "gm" ? "silver" : "pack", ["max_amount"] = amount })))!;
        using var after = Con(account);
        var md = PlacedFeeder(after, guid)!.Value.Md;
        return (200, new JsonObject
        {
            ["success"] = reply["success"]?.GetValue<bool>() == true, ["price"] = (long)Py.D(reply["price"] ?? 0), ["bait_amount"] = Feeders.Left(md, Clock()), ["empty_at"] = EmptyAt(md),
        });
    }
}
