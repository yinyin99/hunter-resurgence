using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

public class AdminApiTests : IAsyncLifetime
{
    static readonly JsonObject Cfg = new(Replay.HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));

    readonly string tmp = Directory.CreateTempSubdirectory("admin").FullName;
    State state = null!;
    Loopback server = null!;

    public async ValueTask InitializeAsync()
    {
        var ammo = O("""{"item_id": 13, "name": "Test Cartridge", "units": 10}""");
        state = new State(Path.Combine(tmp, "state.db"), Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, sampleInfos: [ammo]);
        server = await Loopback.StartAsync(new Replay.Game(new Replay.HarTable(), Cfg, state, tmp), tmp);
    }

    public async ValueTask DisposeAsync()
    {
        await server.DisposeAsync();
        DeleteDir(tmp);
    }

    Task<HttpResponseMessage> Post(string path, object body) => server.Http.PostAsJsonAsync($"http://localhost{path}", body);

    [Fact]
    public async Task GrantsMapToStatusCodesAndReachTheNextHeartbeat()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Post("/admin/grants/items", new { itemId = 99999 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("/admin/grants/items", new { itemId = 13, count = 0 })).StatusCode);
        var granted = await Post("/admin/grants/items", new { itemId = 13, count = 2 });
        Assert.Equal("""{"itemId":13,"count":2,"name":"Test Cartridge"}""", await granted.Content.ReadAsStringAsync());
        var refused = await Post("/admin/grants/currency", new { currency = "gm", amount = -1000 });
        Assert.Equal((HttpStatusCode.Conflict, "application/problem+json"), (refused.StatusCode, refused.Content.Headers.ContentType?.MediaType));
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("/admin/grants/currency", new { currency = "silver", amount = 1 })).StatusCode);
        Assert.Equal("""{"currency":"gm","amount":100}""", await (await Post("/admin/grants/currency", new { currency = "gm", amount = 67 })).Content.ReadAsStringAsync());
        var updates = JsonNode.Parse(state.HeartbeatReply(ME, "{\"events\": []}"u8.ToArray()))!["updates"]!.AsArray();
        Eq("[10, 15]", new JsonArray(updates.Select(u => u!["update"]!.DeepClone()).ToArray()));
        var leak = await server.Http.GetAsync("http://api.test/admin/wallet");  // game hosts never reach the admin API
        Assert.Equal((HttpStatusCode.NotFound, 0L), (leak.StatusCode, leak.Content.Headers.ContentLength ?? 0));
    }

    [Fact]
    public async Task AccountParameterTargetsOneAccountAndUnknownIs404()
    {
        const long other = 8;
        state.CreateAccount(other, "second");
        var before = state.Wallet(ME)["gm"]!.GetValue<double>();
        Assert.Equal("""{"currency":"gm","amount":38}""", await (await Post("/admin/grants/currency?account=second", new { currency = "gm", amount = 5 })).Content.ReadAsStringAsync());
        Assert.Equal(before, state.Wallet(ME)["gm"]!.GetValue<double>());
        Assert.Equal(state.Wallet(other)["gm"]!.GetValue<double>(), (await server.Http.GetFromJsonAsync<Dictionary<string, double>>("http://localhost/admin/wallet?account=8"))!["gm"]);
        var items = state.Inventory(ME).Count;
        Assert.Equal(HttpStatusCode.OK, (await Post("/admin/grants/items?account=second", new { itemId = 13 })).StatusCode);
        Assert.Equal(items, state.Inventory(ME).Count);
        Assert.Equal(HttpStatusCode.NotFound, (await Post("/admin/grants/items?account=nobody", new { itemId = 13 })).StatusCode);
        Assert.Null(AdminApi.ResolveAccount(state, "999"));
        Assert.Equal(ME, AdminApi.ResolveAccount(state, null));
    }

    [Fact]
    public async Task TelemetrySinkRequestsAreKeptInTelemetryJsonl()
    {
        var body = new StringContent("death_type=25&player_x=1", System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
        Assert.Equal(HttpStatusCode.NoContent, (await server.Http.PostAsync("http://telemetry.test/api/x/v1/trackPlayerDeath", body)).StatusCode);
        var line = JsonNode.Parse(File.ReadAllLines(Path.Combine(tmp, "telemetry.jsonl")).Single())!;
        Assert.Equal(("telemetry.test", "POST", "/api/x/v1/trackPlayerDeath", "death_type=25&player_x=1"),
            ((string)line["host"]!, (string)line["method"]!, (string)line["path"]!, (string)line["body"]!));
        Assert.NotNull(line["ts"]);
    }

    [Fact]
    public async Task BadAdminRequestsGetProblemDetails()
    {
        async Task<(HttpStatusCode, string?, string)> Problem(HttpResponseMessage r) =>
            (r.StatusCode, r.Content.Headers.ContentType?.MediaType, await r.Content.ReadAsStringAsync());
        var (status, type, body) = await Problem(await Post("/admin/grants/items", new { count = 5000 }));  // no itemId, too many
        Assert.Equal((HttpStatusCode.BadRequest, "application/problem+json"), (status, type));
        Assert.Contains("itemId", body);
        Assert.Contains("count", body);
        (status, type, body) = await Problem(await Post("/admin/grants/currency", new { currency = "gm" }));  // no amount
        Assert.Equal((HttpStatusCode.BadRequest, "application/problem+json"), (status, type));
        Assert.Contains("amount", body);
        (status, type, _) = await Problem(await server.Http.PostAsync("http://localhost/admin/grants/items",
            new StringContent("{", System.Text.Encoding.UTF8, "application/json")));  // not JSON
        Assert.Equal((HttpStatusCode.BadRequest, "application/problem+json"), (status, type));
        (status, type, _) = await Problem(await server.Http.GetAsync("http://localhost/admin/items/abc"));
        Assert.Equal((HttpStatusCode.NotFound, "application/problem+json"), (status, type));
        Assert.Equal(33.0, Py.D(state.Wallet(ME)["gm"]));  // nothing granted
    }

    [Fact]
    public async Task NonFiniteOrHugeAmountsAreRefusedAndLeaveTheWallet()
    {
        foreach (var amount in new[] { "\"NaN\"", "\"Infinity\"", "1e309", "1e308" })
        {
            var r = await server.Http.PostAsync("http://localhost/admin/grants/currency",
                new StringContent($$"""{"currency": "gm", "amount": {{amount}}}""", System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal((HttpStatusCode.BadRequest, "application/problem+json"), (r.StatusCode, r.Content.Headers.ContentType?.MediaType));
        }
        Assert.Equal(33.0, Py.D(state.Wallet(ME)["gm"]));
    }

    [Fact]
    public async Task AdminFailuresAreProblemDetails()
    {
        File.WriteAllText(Path.Combine(tmp, "misses.jsonl"), "{\"host\": \n");  // a line cut by a crash mid-append
        var r = await server.Http.GetAsync("http://localhost/admin/misses");
        Assert.Equal((HttpStatusCode.InternalServerError, "application/problem+json"), (r.StatusCode, r.Content.Headers.ContentType?.MediaType));
    }

    [Fact]
    public async Task CrossSitePreflightGetsNoCorsFromLocalhost()
    {
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "http://localhost/admin/grants/currency");
        preflight.Headers.Add("Origin", "https://evil.test");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        var r = await server.Http.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.False(r.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task OpenApiDocumentsDescribeBothApis()
    {
        var game = JsonNode.Parse(await server.Http.GetStringAsync("http://localhost/openapi/game.json"))!;
        var heartbeat = game["paths"]![Replay.HEARTBEAT]!["post"]!;
        Assert.Equal("Heartbeat", (string)heartbeat["operationId"]!);
        Assert.Equal("https://gameserver.test", (string)heartbeat["servers"]![0]!["url"]!);
        Assert.NotNull(game["paths"]![Replay.BUY]!["post"]!["requestBody"]!["content"]!["application/x-www-form-urlencoded"]);
        var admin = JsonNode.Parse(await server.Http.GetStringAsync("http://localhost/openapi/admin.json"))!;
        Assert.Equal("GrantItems", (string)admin["paths"]!["/admin/grants/items"]!["post"]!["operationId"]!);
        Assert.Null(admin["paths"]![Replay.HEARTBEAT]);
    }

    [Fact]
    public async Task SpawnRoutesValidateStoreAndWake()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("/admin/spawn", new { reserve = 2, animal = "unicorn", males = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("/admin/spawn", new { reserve = 2, animal = "mule_deer", males = 1, minWeight = 5 })).StatusCode);
        var added = await Post("/admin/spawn", new { reserve = 2, animal = "mule_deer", males = 5, x = -7200, z = 1200, minWeight = 120, maxWeight = 120, dormant = true });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var list = JsonNode.Parse(await server.Http.GetStringAsync("http://localhost/admin/spawn"))!;
        Assert.Equal("mule_deer", (string)list["entries"]![0]!["animal"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("/admin/spawn/wake", new { populationId = 101 })).StatusCode);  // no group
        Assert.Equal(HttpStatusCode.OK, (await Post("/admin/spawn/wake", new { populationId = 101, group = 1 })).StatusCode);
        var removed = await server.Http.DeleteAsync("http://localhost/admin/spawn");
        Assert.Equal(1, (int)JsonNode.Parse(await removed.Content.ReadAsStringAsync())!["removed"]!);
    }

    [Fact]
    public async Task MissesComeNewestFirstClampedAndItemsAndSingleSpawnsAreServed()
    {
        File.WriteAllLines(Path.Combine(tmp, "misses.jsonl"), ["""{"n": 1}""", """{"n": 2}""", """{"n": 3}"""]);
        async Task<int[]> Misses(string query) =>
            [.. JsonNode.Parse(await server.Http.GetStringAsync("http://localhost/admin/misses" + query))!.AsArray().Select(m => (int)m!["n"]!)];
        Assert.Equal(new[] { 3, 2 }, await Misses("?limit=2"));
        Assert.Equal(new[] { 3 }, await Misses("?limit=0"));
        Assert.Equal(new[] { 3, 2, 1 }, await Misses(""));
        var item = JsonNode.Parse(await server.Http.GetStringAsync("http://localhost/admin/items/13"))!;
        Assert.Equal("Test Cartridge", (string)item["name"]!);
        var first = JsonNode.Parse(await (await Post("/admin/spawn", new { reserve = 2, animal = "mule_deer", males = 1 })).Content.ReadAsStringAsync())!;
        await Post("/admin/spawn", new { reserve = 2, animal = "mule_deer", males = 2 });
        var removed = await server.Http.DeleteAsync($"http://localhost/admin/spawn?id={(int)first["id"]!}");
        Assert.Equal(1, (int)JsonNode.Parse(await removed.Content.ReadAsStringAsync())!["removed"]!);
        Assert.Equal(2, Assert.Single(state.Spawns.List()).Males);
    }
}
