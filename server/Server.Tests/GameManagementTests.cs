using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>Settings, Game Management on the website's host: GET /account/lodges, PUT /account/lodges/{id}/settings, GET /account/feeders, POST /account/feeders/{id}/refill.</summary>
public class GameManagementTests : StateTestBase
{
    static readonly JsonObject Cfg = new(Replay.HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));
    const long Start = 1_800_000_000, Barrel = 300, Bait = 301, Pack = 302;

    static JsonObject Catalogue()
    {
        var app = APP;
        app["reserves"]!.AsArray().Add(O("""{"id": 77, "name": "Test Reserve"}"""));
        app["items"]!.AsArray().Add(O("""{"id": 136, "name": "Test Barrel", "define": "TEST_BARREL", "type": 12, "bait_item": "TEST_BAIT", "units": null}"""));
        app["items"]!.AsArray().Add(O("""{"id": 137, "name": "Test Bait", "define": "TEST_BAIT", "type": 4, "units": {"count": 50, "name": "uses"}}"""));
        app["items"]!.AsArray().Add(O("""{"id": 787, "name": "Test Refill Pack", "define": "TEST_PACK", "type": 38, "units": {"count": 25, "name": "units"}, "price": 105, "silver_price": 1785, "notbuyable": 0}"""));
        app["items"]!.AsArray().Add(O("""{"id": 791, "name": "Bait Site Refill", "define": "FEEDER_REFILL", "type": 37, "units": {"count": 1, "name": "uses"}, "price": 5, "silver_price": 85, "notbuyable": 0, "sellable": null}"""));
        foreach (var item in app["items"]!.AsArray().OfType<JsonObject>().Where(i => (long)i["id"]! is 136 or 137 or 787 or 791))
        {
            item["fit"] = new JsonObject();
            item["inventory"] = O("""{"slots": [0, 1], "capacity": 0, "weight": 1}""");
            item["group"] = 1;
            item["cat"] = 4;
            item["client_type"] = 4;
            item["image"] = "test_image";
            item["description"] = "Test.";
            item["variationname"] = null;
        }
        return app;
    }

    async Task<Loopback> Start_()
    {
        state = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, Catalogue(), LOCALE, TEXTURES, offline: false) { Clock = () => Start };
        var table = new Replay.HarTable();
        table.Entries[new("POST", "gameserver.test", Replay.CONNECT, null)] = new Replay.Hit(2, "", 200, [], "{}"u8.ToArray());
        return await Loopback.StartAsync(new Replay.Game(table, Cfg, state, tmp), tmp);
    }

    string Cookie(long id) => $"{SignIn.SessionCookie}={state.Store.CreateWebSession(id, TimeSpan.FromHours(1))}";

    static async Task<(int Status, string Text)> Call(Loopback server, string method, string path, string? cookie, string body = "")
    {
        var reply = await server.SendAsync(method, "web.test", path, [("X-Requested-With", "test"), .. cookie is null ? [] : new[] { ("Cookie", cookie) }], Encoding.UTF8.GetBytes(body));
        return (reply.Status, Encoding.UTF8.GetString(reply.Body));
    }

    /// <summary>The numbers the contract calls int are written without a fraction (`12.0` is refused by an int field).</summary>
    static void AssertIntegers(string text) =>
        Assert.DoesNotMatch(new Regex("\"(id|style_id|trophies|weapons|item_id|reserve|bait_amount|bait_max|bait_owned|empty_at|packs|em|gm|price)\": ?-?\\d+\\.\\d"), text);

    [Fact]
    public async Task LodgesAreListedWithCountsAndEditedByTheirOwner()
    {
        await using var server = await Start_();
        using (var con = state.Store.Open(ME))
        {
            con.Begin();
            var three = O("""
                {"id": 3, "name": "lodge 3", "locked": false, "style_id": 1, "lodge_type_id": 780,
                 "animal_slots": [{"slot_id": 0, "trophy_id": 21}, {"slot_id": 1, "trophy_id": null}, {"slot_id": 2, "trophy_id": 22}],
                 "weapon_slots": [{"slot_id": 0, "weapon_id": 5}, {"slot_id": 1, "weapon_id": null}]}
                """);
            var four = O("""{"id": 4, "name": "lodge 4", "locked": true, "style_id": 0, "animal_slots": [], "weapon_slots": []}""");
            foreach (var (id, lodge) in new[] { ("3", three), ("4", four) })
            {
                PlayerRows.AddLodge(con, "/game/user/lodges", id, lodge);
                PlayerRows.AddLodge(con, "/game/user/lodge", id, lodge.DeepClone());
            }
            con.Commit();
        }
        var other = state.Store.NextServerId();
        state.CreateAccount(other, "Other", "76561190000001000");
        var (me, theirs) = (Cookie(ME), Cookie(other));

        Assert.Equal(401, (await Call(server, "GET", "/account/lodges", null)).Status);
        Assert.Equal(401, (await Call(server, "PUT", "/account/lodges/3/settings", null, "{}")).Status);
        var (status, text) = await Call(server, "GET", "/account/lodges", me);
        Assert.Equal(200, status);
        AssertIntegers(text);
        Assert.Equal(["3 lodge 3 false 1 2 1", "4 lodge 4 true 0 0 0"],
            JsonNode.Parse(text)!["lodges"]!.AsArray().Select(l => $"{l!["id"]} {l["name"]} {l["locked"]} {l["style_id"]} {l["trophies"]} {l["weapons"]}"));

        Assert.Equal((200, """{"ok": true}"""), await Call(server, "PUT", "/account/lodges/3/settings", me, """{"name": "My Cabin", "locked": true, "style_id": 2}"""));
        var game = JsonNode.Parse(Encoding.UTF8.GetString(state.LodgesReply(ME)))!.AsArray().Single(l => (long)l!["id"]! == 3)!;
        Assert.Equal("My Cabin true 2", $"{game["name"]} {game["locked"]} {game["style_id"]}");
        Assert.Equal("My Cabin", JsonNode.Parse((await Call(server, "GET", "/account/lodges", me)).Text)!["lodges"]![0]!["name"]!.GetValue<string>());

        Assert.Equal(404, (await Call(server, "PUT", "/account/lodges/3/settings", theirs, """{"name": "x", "locked": false, "style_id": 0}""")).Status);  // not the other's lodge
        Assert.Equal(404, (await Call(server, "PUT", "/account/lodges/99/settings", me, """{"name": "x", "locked": false, "style_id": 0}""")).Status);
        Assert.Equal(400, (await Call(server, "PUT", "/account/lodges/3/settings", me, """{"name": 5}""")).Status);
        Assert.Equal("My Cabin", JsonNode.Parse((await Call(server, "GET", "/account/lodges", me)).Text)!["lodges"]![0]!["name"]!.GetValue<string>());
    }

    void Place(long amount = 20)
    {
        var md = new JsonObject();
        foreach (var (key, value) in Feeders.Refill(md, amount, Start)!)
            md[key] = value!.DeepClone();
        md[State.RESERVE] = 77;
        md[State.POSITION] = O("""{"x": 12345, "y": 0, "z": -2500}""");
        using var con = state.Store.Open(ME);
        PlayerRows.AddItem(con, Barrel, 136, 23, null, md);
    }

    void Stock(long units = 20, long gm = 500, bool pack = false)
    {
        using var con = state.Store.Open(ME);
        PlayerRows.AddItem(con, Bait, 137, 0, units, new JsonObject());
        if (pack) PlayerRows.AddItem(con, Pack, 787, 0, 2, new JsonObject());
        con.Exec("UPDATE wallet SET amount = ? WHERE account_id = $a AND currency = 'gm'", gm);
        con.Exec("UPDATE wallet SET amount = 100 WHERE account_id = $a AND currency = 'em'");
    }

    void Restock(long units)
    {
        using var con = state.Store.Open(ME);
        con.Exec("UPDATE items SET units = ? WHERE account_id = $a AND guid = 301", units);
    }

    [Fact]
    public async Task FeedersAreListedAndRefilledLikeTheGameDoes()
    {
        await using var server = await Start_();
        Place();
        Stock(pack: true);
        var me = Cookie(ME);
        Assert.Equal(401, (await Call(server, "GET", "/account/feeders", null)).Status);
        Assert.Equal(401, (await Call(server, "POST", $"/account/feeders/{Barrel}/refill", null, "{}")).Status);

        var (status, text) = await Call(server, "GET", "/account/feeders", me);
        Assert.Equal(200, status);
        AssertIntegers(text);
        var list = JsonNode.Parse(text)!;
        var f = list["feeders"]!.AsArray().Single()!;
        Assert.Equal($"300 136 Test Barrel 77 Test Reserve 12.3 -2.5 20 50 inactive {Start + 8640 * 20} 20",
            $"{f["id"]} {f["item_id"]} {f["name"]} {f["reserve"]} {f["reserve_name"]} {f["x"]} {f["y"]} {f["bait_amount"]} {f["bait_max"]} {f["status"]} {f["empty_at"]} {f["bait_owned"]}");
        Assert.Equal("2 5 85", $"{list["packs"]} {list["refill_price"]!["em"]} {list["refill_price"]!["gm"]}");

        // gm$: the bait owned (20) fills the feeder from 20 to 40 and the service costs 85 gm$
        var (ok, reply) = await Call(server, "POST", $"/account/feeders/{Barrel}/refill", me, """{"currency": "gm", "max_amount": 50}""");
        Assert.Equal(200, ok);
        AssertIntegers(reply);
        Assert.Equal($"true 85 40 {Start + 8640 * 40}", $"{JsonNode.Parse(reply)!["success"]!.ToString().ToLowerInvariant()} {JsonNode.Parse(reply)!["price"]} {JsonNode.Parse(reply)!["bait_amount"]} {JsonNode.Parse(reply)!["empty_at"]}");
        Assert.Equal("415", state.Wallet(ME)["gm"]!.ToString());
        Assert.Equal("40 0", $"{JsonNode.Parse((await Call(server, "GET", "/account/feeders", me)).Text)!["feeders"]![0]!["bait_amount"]} {JsonNode.Parse((await Call(server, "GET", "/account/feeders", me)).Text)!["feeders"]![0]!["bait_owned"]}");
        Assert.Contains("\"success\": false", (await Call(server, "POST", $"/account/feeders/{Barrel}/refill", me, """{"currency": "gm", "max_amount": 50}""")).Text);  // no bait left

        // a pack pays with one use of it and no currency; an em$ refill costs the service's em$ price
        Restock(10);
        var packed = JsonNode.Parse((await Call(server, "POST", $"/account/feeders/{Barrel}/refill", me, """{"currency": "pack", "max_amount": 45}""")).Text)!;
        Assert.Equal("true 0 45", $"{packed["success"]} {packed["price"]} {packed["bait_amount"]}");
        using (var con = state.Store.Open(ME))
            Assert.Equal(1L, Convert.ToInt64(con.Scalar("SELECT units FROM items WHERE account_id = $a AND guid = 302")));
        Assert.Equal("415", state.Wallet(ME)["gm"]!.ToString());
        Restock(10);
        var gold = JsonNode.Parse((await Call(server, "POST", $"/account/feeders/{Barrel}/refill", me, """{"currency": "em", "max_amount": 50}""")).Text)!;
        Assert.Equal("true 5 50 95", $"{gold["success"]} {gold["price"]} {gold["bait_amount"]} {state.Wallet(ME)["em"]}");
    }

    [Fact]
    public async Task ABadRefillRequestIsRefusedAndAnotherFeederIsNotFound()
    {
        await using var server = await Start_();
        Place();
        Stock();
        var me = Cookie(ME);
        Assert.Equal(400, (await Call(server, "POST", $"/account/feeders/{Barrel}/refill", me, """{"currency": "bitcoin", "max_amount": 50}""")).Status);
        Assert.Equal(400, (await Call(server, "POST", $"/account/feeders/{Barrel}/refill", me, """{"currency": "gm", "max_amount": 51}""")).Status);
        Assert.Equal(400, (await Call(server, "POST", $"/account/feeders/{Barrel}/refill", me, "nope")).Status);
        Assert.Equal(404, (await Call(server, "POST", $"/account/feeders/{Bait}/refill", me, """{"currency": "gm", "max_amount": 50}""")).Status);  // not a feeder
        var other = state.Store.NextServerId();
        state.CreateAccount(other, "Other", "76561190000001000");
        Assert.Equal(404, (await Call(server, "POST", $"/account/feeders/{Barrel}/refill", Cookie(other), """{"currency": "gm", "max_amount": 50}""")).Status);
        Assert.Empty(JsonNode.Parse((await Call(server, "GET", "/account/feeders", Cookie(other))).Text)!["feeders"]!.AsArray());
    }

    /// <summary>Sends a service's requests through the loopback server, whatever host they name.</summary>
    sealed class Via(HttpClient server) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var copy = new HttpRequestMessage(request.Method, request.RequestUri) { Content = request.Content };
            foreach (var h in request.Headers) copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
            return server.SendAsync(copy, ct);
        }
    }

    [Fact]
    public async Task TheLauncherReadsTheGameManagementReplies()
    {
        await using var server = await Start_();
        using (var con = state.Store.Open(ME))
        {
            con.Begin();
            var lodge = O("""{"id": 3, "name": "lodge 3", "locked": true, "style_id": 4, "animal_slots": [{"slot_id": 0, "trophy_id": 21}], "weapon_slots": []}""");
            PlayerRows.AddLodge(con, "/game/user/lodges", "3", lodge);
            PlayerRows.AddLodge(con, "/game/user/lodge", "3", lodge.DeepClone());
            con.Commit();
        }
        Place();
        Stock(pack: true);
        var browser = new Resurgence.Services.ServerCookie { Value = state.Store.CreateWebSession(ME, TimeSpan.FromHours(1)), Host = "web.test" };
        var service = new Resurgence.Services.ServerGameManagementService(new HttpClient(new Via(server.Http)) { BaseAddress = new Uri("http://web.test/") }, browser,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Resurgence.Services.ServerGameManagementService>.Instance);

        var lodgeRow = Assert.Single(await service.LodgesAsync());
        Assert.Equal(new Resurgence.Services.LodgeRow("3", "lodge 3", true, 4, null, 1, 0), lodgeRow);
        Assert.True((await service.SaveLodgeAsync(lodgeRow, "My Cabin", false)).Ok);
        Assert.Equal(new Resurgence.Services.LodgeRow("3", "My Cabin", false, 4, null, 1, 0), Assert.Single(await service.LodgesAsync()));

        var feeders = await service.FeedersAsync();
        Assert.Equal((2, 5, 85), (feeders.Packs, feeders.RefillPrice.Em, feeders.RefillPrice.Gm));
        Assert.Equal(new Resurgence.Services.FeederRow("300", 136, "Test Barrel", 77, "Test Reserve", 12.3, -2.5, 20, 50, "inactive", Start + 8640 * 20, 20), Assert.Single(feeders.Feeders));

        var refill = await service.RefillAsync("300", "gm", 50);
        Assert.True(refill.Ok);
        Assert.Equal(new Resurgence.Services.RefillReply(true, 85, 40, Start + 8640 * 40), refill.Value);
    }
}
