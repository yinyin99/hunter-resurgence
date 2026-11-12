using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>GET /catalog/reserves (the reserves with their starts, public) and POST /auth/launch with a reserve and a lodge (the hunt opens there, as start_single_player does).</summary>
public class LaunchCatalogueTests : StateTestBase
{
    static readonly JsonObject Cfg = new(Replay.HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));

    // fictional values: reserve 1 has three lodges and a tower, its map the 2000 x 2000 square from (-500, -500); reserve 2 a lodge and a named point of interest, its map empty
    // (so the square around the starts); reserve 3 no start; a start of another type (a stash) is not offered; item 811 is the loadout slot of the store
    static readonly Facts MapFacts = Facts.Parse("""{"format": 1, "reserves": [{"id": 1, "name": "First Reserve", "map": [-500, -500, 1500, 1500]}, {"id": 2, "name": "Test Reserve", "map": [5, 5, 5, 9]}]}""");
    static JsonObject Catalogue()
    {
        var app = APP;
        app["reserves"] = JsonNode.Parse("""[{"id": 2, "name": "Test Reserve"}, {"id": 1, "name": "First Reserve"}, {"id": 3, "name": "Empty Reserve"}]""");
        app["locations"] = JsonNode.Parse("""
            [{"id": 13, "pos": {"x": 333, "y": 1, "z": -77}, "type": "LODGE", "reserve": 1},
             {"id": 12, "pos": {"x": 500, "y": 1, "z": 1000}, "type": "HUNTING_TOWER", "reserve": 1},
             {"id": 11, "pos": {"x": 1000, "y": 1, "z": 500}, "type": "LODGE", "reserve": 1},
             {"id": 10, "pos": {"x": 0, "y": 1, "z": 0}, "type": "LODGE", "reserve": 1},
             {"id": 101, "pos": {"x": -6420, "y": 150, "z": 3310}, "type": "LODGE", "reserve": 2},
             {"id": 102, "pos": {"x": -6000, "y": 150, "z": 3000}, "type": "INTEREST", "reserve": 2, "name": "Old Mill"},
             {"id": 103, "pos": {"x": -6000, "y": 150, "z": 3000}, "type": "STASH", "reserve": 2}]
            """);
        app["items"]!.AsArray().Add(JsonNode.Parse("""
            {"id": 811, "name": "Test Loadout Slot", "description": "", "define": "TEST_ITEM_811", "type": 41, "group": 13, "cat": 41, "client_type": 4,
             "image": "x", "variationname": null, "units": null, "fit": {}, "inventory": {"slots": [0, 1, 8], "capacity": 0, "weight": 1},
             "price": 10, "silver_price": 100, "notbuyable": 0, "ownMultiple": true, "subcat": {"id": 0}, "hidden": 0}
            """));
        return app;
    }

    async Task<Loopback> Start(JsonObject? app = null, bool offline = false, Facts? facts = null)
    {
        state = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, app ?? Catalogue(), LOCALE, TEXTURES, offline: offline, facts: facts);
        var table = new Replay.HarTable();
        table.Entries[new("POST", "gameserver.test", Replay.CONNECT, null)] = new Replay.Hit(2, "", 200, [], "{}"u8.ToArray());
        return await Loopback.StartAsync(new Replay.Game(table, Cfg, state, tmp), tmp);
    }

    async Task<Reply> Reserves(JsonObject? app = null, bool offline = false)
    {
        await using var server = await Start(app, offline, MapFacts);
        return await server.SendAsync("GET", "web.test", "/catalog/reserves", []);
    }

    [Fact]
    public async Task TheReservesAreListedWithTheirStartsAsPercentOfTheirMapPicture()
    {
        var reply = await Reserves();
        Assert.Equal(200, reply.Status);
        var catalogue = JsonNode.Parse(reply.Body)!;
        var reserves = catalogue["reserves"]!.AsArray();
        Assert.Equal(["2:Test Reserve", "1:First Reserve", "3:Empty Reserve"], reserves.Select(r => $"{r!["id"]}:{r["name"]}"));  // the catalogue's order
        // a start is at its percent of the facts file's map square, world x to the right and z down, two decimals; a start without a name is its kind and its number
        Assert.Equal("""[{"id":10,"kind":"lodge","name":"Lodge 1","x":25.0,"y":25.0},{"id":11,"kind":"lodge","name":"Lodge 2","x":75.0,"y":50.0},{"id":12,"kind":"tower","name":"Tower 1","x":50.0,"y":75.0},{"id":13,"kind":"lodge","name":"Lodge 3","x":41.65,"y":21.15}]""",
            reserves[1]!["starts"]!.ToJsonString());
        // a named start keeps its name; the stash is not offered; no usable map square: the square around the starts with a tenth of margin
        Assert.Equal("""[{"id":101,"kind":"lodge","name":"Lodge 1","x":10.0,"y":79.52},{"id":102,"kind":"interest","name":"Old Mill","x":90.0,"y":20.48}]""", reserves[0]!["starts"]!.ToJsonString());
        Assert.Empty(reserves[2]!["starts"]!.AsArray());
        Assert.Equal(["1:Test One:2", "2:Test Two:4", "3:Test Three:1", "4:Test Four:10", "5:Test Five:2"],
            catalogue["tutorials"]!.AsArray().Select(t => $"{t!["id"]}:{t["name"]}:{t["reserve"]}"));
    }

    [Fact]
    public async Task WithoutReservesThereIsNoCatalogue()
    {
        var app = Catalogue();
        app["reserves"] = new JsonArray();
        Assert.Equal(404, (await Reserves(app)).Status);
    }

    [Fact]
    public async Task ALocalServerDoesNotServeIt() => Assert.Equal("miss", (await Reserves(offline: true)).Source);

    /// <summary>POST /auth/launch as the launcher sends it, signed in as ME; prepare runs on the started state first.</summary>
    async Task<Reply> Launch(string? form, bool signedIn = true, bool ownPage = true, Action? prepare = null)
    {
        await using var server = await Start();
        prepare?.Invoke();
        List<(string, string)> headers = [("Content-Type", "application/x-www-form-urlencoded")];
        if (ownPage) headers.Add(("X-Requested-With", "XMLHttpRequest"));
        if (signedIn) headers.Add(("Cookie", $"{SignIn.SessionCookie}={state.Store.CreateWebSession(ME, TimeSpan.FromHours(1))}"));
        return await server.SendAsync("POST", "web.test", "/auth/launch", headers, form is null ? [] : Encoding.UTF8.GetBytes(form));
    }

    /// <summary>"reserve:location" of each hunt ME has open, joined.</summary>
    string Open()
    {
        using var con = state.Store.Open(ME);
        return string.Join(",", con.Rows("SELECT reserve, location_id FROM expeditions WHERE account_id = $a AND end_ts IS NULL").Select(r => $"{r[0]}:{r[1]}"));
    }

    [Fact]
    public async Task AReserveAndALodgeOpenTheHuntThereAndGiveTheLaunchUrl()
    {
        var reply = await Launch("reserve=1&lodge=11&time=480&loadout=0");
        Assert.Equal(200, reply.Status);
        Assert.Matches($@"\A{ProtocolLiterals.LaunchScheme}://game_web\.test/[0-9a-f]{{40}}/lang/en_US/access_token/[\w.-]+/refresh_token/[\w.-]+\z", (string)JsonNode.Parse(reply.Body)!["url"]!);
        Assert.Equal("1:11", Open());
        // the connect of the hunt starts at that lodge, as one after start_single_player
        Assert.Equal(11L, (long)JsonNode.Parse(state.ConnectReply(ME))!["expedition"]!["lodge"]!);
        Assert.Equal(480L, (long)JsonNode.Parse(state.ConnectReply(ME))!["expedition"]!["start_time"]!);  // at the picked hour
    }

    [Fact]
    public async Task TheLoadoutIsPutOnAsAtStartSinglePlayer()
    {
        long id = 0;
        long RifleSlot() => state.Inventory(ME).Where(i => (long)i!["item_id"]! == 5).Select(i => (long)i!["slot"]!).Single();
        var reply = await Launch(null, prepare: () =>
        {
            using (var rifle = state.Store.Open(ME))
            {
                rifle.Begin();
                PlayerRows.AddItem(rifle, 301, 5, 0, null, new JsonObject());
                rifle.Commit();
            }
            state.GrantItems(ME, 811);
            using (var con = state.Store.Open(ME))
                id = (long)con.Rows("SELECT id FROM loadouts WHERE account_id = $a ORDER BY id")[0][0];
            Assert.Equal(200, state.SaveLoadout(ME, id, Encoding.UTF8.GetBytes("""{"name": "My hunt", "color": 3, "items": [{"slot_id": 2, "item_id": 5}]}""")).Status);
            Assert.Equal(0, RifleSlot());
        });
        Assert.Equal(200, reply.Status);
        Assert.Equal(0, RifleSlot());  // no fields: the equipment stays
        reply = await Launch($"reserve=1&lodge=11&loadout={id}");  // a new state over the same database
        Assert.Equal(200, reply.Status);
        Assert.Equal(2, RifleSlot());
    }

    [Fact]
    public async Task WithoutTheFieldsTheLaunchUrlComesAloneAsBefore()
    {
        var reply = await Launch(null);
        Assert.Equal(200, reply.Status);
        Assert.StartsWith(ProtocolLiterals.LaunchScheme + "://", (string)JsonNode.Parse(reply.Body)!["url"]!);
        Assert.Equal("", Open());
    }

    [Theory]
    [InlineData("reserve=1")]                          // no lodge
    [InlineData("lodge=11")]                           // no reserve
    [InlineData("reserve=1&lodge=11&time=soon")]
    [InlineData("reserve=1&lodge=11&loadout=-1")]
    [InlineData("reserve=x&lodge=11")]
    [InlineData("reserve=99999999999999999999&lodge=11")]  // more digits than a whole number holds
    [InlineData("reserve=1&lodge=11&loadout=99999999999999999999")]
    public async Task AnIncompleteOrMalformedStartIsRefusedAndOpensNothing(string form)
    {
        Assert.Equal(400, (await Launch(form)).Status);
        Assert.Equal("", Open());
    }

    [Fact]
    public async Task TheLoadoutsOfThePlayerAreListedForTheSignedInPlayerOnly()
    {
        await using var server = await Start();
        state.GrantItems(ME, 811);  // one loadout per unit
        var cookie = ("Cookie", $"{SignIn.SessionCookie}={state.Store.CreateWebSession(ME, TimeSpan.FromHours(1))}");
        Assert.Equal(401, (await server.SendAsync("GET", "web.test", "/account/loadouts", [])).Status);
        var reply = await server.SendAsync("GET", "web.test", "/account/loadouts", [cookie]);
        Assert.Equal(200, reply.Status);
        var loadouts = JsonNode.Parse(reply.Body)!.AsArray();
        using var con = state.Store.Open(ME);
        Assert.Equal(con.Rows("SELECT id FROM loadouts WHERE account_id = $a ORDER BY id").Select(r => (long)r[0]), loadouts.Select(l => (long)l!["id"]!));
        Assert.NotEmpty(loadouts);
        Assert.All(loadouts, l => Assert.False(string.IsNullOrEmpty((string)l!["name"]!)));
    }

    [Fact]
    public async Task OnlyASignedInPlayerFromAPageOfTheServerStartsAHunt()
    {
        Assert.Equal(401, (await Launch("reserve=1&lodge=11", signedIn: false)).Status);
        Assert.Equal(400, (await Launch("reserve=1&lodge=11", ownPage: false)).Status);
        Assert.Equal("", Open());
    }

    [Fact]
    public async Task ATutorialOpensItsSessionAndNoHunt()
    {
        var reply = await Launch("tutorial=2");
        Assert.Equal(200, reply.Status);
        var session = System.Text.RegularExpressions.Regex.Match((string)JsonNode.Parse(reply.Body)!["url"]!, @"://game_web\.test/([0-9a-f]{40})/").Groups[1].Value;
        Assert.Equal(ME, state.Store.ResolveToken(session));
        var expedition = JsonNode.Parse(state.ConnectReply(ME, session))!["expedition"]!;
        Assert.Equal((1, 2), ((int)expedition["mode"]!, (int)expedition["tutorial"]!));
        Assert.Equal("", Open());
        Assert.Equal(400, (await Launch("tutorial=99")).Status);
    }
}
