using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Replay;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The server's starting data without recordings: a fake install (tiny files in a temp folder) and a small facts file.</summary>
public class InstallTests : IDisposable
{
    const string SAMPLE = """
        {"account": {"account_id": 999, "avatar": 5, "avatar_id": 5, "avatar_url": "https://sample.example/a.jpg", "face": 3,
                     "gender": 1, "handle": "sample_person", "hostname": "sample_person", "hunterscore": 6000, "membership": 2,
                     "fullbody_avatar": 888, "user_id": 998},
         "account_info": {"email": "sample@example.test", "steam_id": "steam-sample", "uuid": "u-1", "user_id": 998},
         "network": {"handle": "sample_person", "ip": "203.0.113.9", "host": 998, "user_id": 998},
         "config": {"domains": {"main": "sample.example"}},
         "boost": {"boost": [{"item": {"id": 8}, "own": true}]},
         "user_stats": {"achievement_stats": [{"category_id": 1, "group_id": 2, "id": 3, "triggers": {"a": 41}, "value": 77}],
                        "bust_through_score": 9, "user_stats": {"animal": {"0": {"kills": 5}}, "distance": 12345}},
         "expedition": {"expedition_id": 123, "lodge": 900, "x": -1.0, "y": 2.0, "z": 3.0},
         "inventory": {"items": [{"item_id": 1, "slot": 3, "guid": "55", "metadata": {}, "units": null}],
                       "infos": [{"item_id": 1, "units": -2}], "active_services": {"UNLIMITED_AMMO": 0}},
         "wallet": {"em": 4321, "gm": 25},
         "skills": {"spotting": [6, 3], "tracking": [2, 1], "weapon": {"1000": 4}},
         "spawn": [{"population_id": 1}], "competitions": [{"id": 1}],
         "ethical": [[14, 15], [15]], "species": [{"scale_factor": 1.0}, {"scale_factor": 0.8}]}
        """;

    const string ITEMS = """
        [{"id": 257, "define": "TEST_SHOTGUN", "cat": "WEAPON", "group": "ITEM_GROUP_WEAPON", "type": "WEAPON", "name": "Test Shotgun",
          "price": 0, "units": -2, "inventory": {"weight": 3500}},
         {"id": 14, "define": "TEST_SHELLS", "cat": "AMMO", "group": "ITEM_GROUP_AMMO", "type": "AMMO", "name": "Test Shells",
          "price": 4, "silver_price": 68, "units": 30, "unit_name": "shells", "inventory": {"weight": 850}, "sponsor_image": "brand.jpg"},
         {"id": 15, "define": "TEST_SLUGS", "cat": "AMMO", "group": "ITEM_GROUP_AMMO", "type": "AMMO", "client_type": "SPECIAL",
          "name": "Test Slugs", "price": 9, "units": 10, "unit_name": "slugs"}]
        """;

    const string SPECIES = """[{"id": 0, "name": "Test Deer", "define": "TEST_DEER", "short": "deer", "ethical": {"ammo": ["X"]}}]""";

    const string FACTS = """
        {"format": 1, "animals": ["deer", "turkey"], "families": {"turkey": [1], "bc_deer": [0, 100]},
         "reserves": [{"id": 1, "name": "Test Reserve", "species": [0, 1],
           "populations": [
             {"index": 0, "animal": "deer", "endangered": false, "license": 1, "groups": [{"x": 5, "z": 6, "radius": 7, "males": 8, "females": 9}],
              "random": {"males": 1, "females": 2, "min_size": 3, "max_size": 4, "avg_size": 5, "max_males": 6, "max_females": 7, "radius": 8, "single_gender": 0}},
             {"index": 900, "reserve": -1, "animal": "dog_x", "density": "dog_y", "endangered": true, "license": 0,
              "groups": [{"x": 0, "z": 0, "radius": 0, "males": 1, "females": 0, "enabled": 1}]}]}],
         "locations": [{"id": 900, "reserve": 1, "type": "LODGE", "x": 10, "y": 20, "z": 30}],
         "types": {"WEAPON": {"id": 1, "slots": [0, 2], "capacity": 0}, "AMMO": {"id": 3, "slots": [0, 8], "capacity": 0}},
         "groups": {"ITEM_GROUP_WEAPON": 1, "ITEM_GROUP_AMMO": 3}, "categories": {"WEAPON": 1, "AMMO": 3},
         "client_types": {"SPECIAL": 77}, "labels": {"pose_stand": "Standing"}, "textures": {"2": "albino"},
         "start": {"items": [257, 14, 404], "wallet": {"em": 1000, "gm": 0}}}
        """;

    static readonly JsonObject Cfg = new(HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));

    readonly string tmp = Directory.CreateTempSubdirectory("install").FullName;
    readonly string install;
    readonly JsonObject cfg;
    readonly Facts facts = Facts.Parse(FACTS);

    public InstallTests()
    {
        install = Path.Combine(tmp, "install");
        Directory.CreateDirectory(Path.Combine(install, "game", "archives"));
        File.WriteAllText(Path.Combine(install, "game", Install.ConnectSample), SAMPLE);
        var items = Encoding.UTF8.GetBytes(ITEMS);
        var species = Encoding.UTF8.GetBytes(SPECIES);
        File.WriteAllBytes(Path.Combine(install, "game", "archives", "pc0.arc"), [.. items, .. species]);
        using (var w = new BinaryWriter(File.Create(Path.Combine(install, "game", "archives", "pc0.tab"))))
        {
            w.Write(0x800u);
            w.Write(ArcList.NameHash("item.json")); w.Write(0u); w.Write((uint)items.Length); w.Write(0u);
            w.Write(ArcList.NameHash("species.json")); w.Write((uint)items.Length); w.Write((uint)species.Length); w.Write(0u);
        }
        cfg = (JsonObject)Cfg.DeepClone();
        cfg["install_dir"] = install;
    }

    public void Dispose() => DeleteDir(tmp);

    JsonObject Template() => JsonNode.Parse(Install.Connect(cfg, facts, Install.ItemDefinitions(install))!)!.AsObject();

    [Fact]
    public void TheSampleAccountIsReplacedByANeutralOne()
    {
        var text = Encoding.UTF8.GetString(Install.Connect(cfg, facts, Install.ItemDefinitions(install))!);
        foreach (var leak in new[] { "sample", "203.0.113.9", "steam-sample", "u-1", "998", "999", "888", "80271", "6000" })
            Assert.DoesNotContain(leak, text);
        var c = Template();
        Eq($$"""{"user_id": {{Install.LocalUserId}}, "handle": "{{Install.LocalHandle}}", "hunterscore": 0, "membership": 0, "face": 3, "gender": 1}""",
           new JsonObject { ["user_id"] = c["account"]!["user_id"]!.DeepClone(), ["handle"] = c["account"]!["handle"]!.DeepClone(),
               ["hunterscore"] = c["account"]!["hunterscore"]!.DeepClone(), ["membership"] = c["account"]!["membership"]!.DeepClone(),
               ["face"] = c["account"]!["face"]!.DeepClone(), ["gender"] = c["account"]!["gender"]!.DeepClone() });
        Assert.Equal("https://avatar.test/256/1.png", (string)c["account"]!["avatar_url"]!);
        Eq("""{"domains": {"main": "web.test", "subdomains": {"avatar": "https://avatar.test/", "static": "https://static.test/"}}, "enable_logging": false, "nat": {"pt_server": {"ip": "127.0.0.1", "port": 9090}}}""", c["config"]);
        Eq("""{"achievement_stats": [{"category_id": 1, "group_id": 2, "id": 3, "triggers": {"a": 0}, "value": 0}], "bust_through_score": 0, "user_stats": {"animal": {}, "collectables": {}, "daily_missions": {}, "distance": 0, "duration": 0, "weapon": {}}}""", c["user_stats"]);
        Eq("""[{"item": {"id": 8}, "own": false}]""", c["boost"]!["boost"]);
        Eq("[]", c["spawn"]);
        Eq("[]", c["competitions"]);
        Eq("""{"UNLIMITED_AMMO": 0}""", c["inventory"]!["active_services"]);
        Eq("""[{"item_id": 1, "units": -2}]""", c["inventory"]!["infos"]);  // game data stays
        Eq("[[14, 15], [15]]", c["ethical"]);
    }

    [Fact]
    public void ANewAccountStartsWithTheFactsStartKit()
    {
        var c = Template();
        // 404 has no definition: no units; 14 comes as a full stack; the stack of -2 units is none
        Eq("""[{"item_id": 257, "slot": 0, "guid": "1", "metadata": {}, "units": null}, {"item_id": 14, "slot": 0, "guid": "2", "metadata": {}, "units": 30}, {"item_id": 404, "slot": 0, "guid": "3", "metadata": {}, "units": null}]""", c["inventory"]!["items"]);
        Eq("""{"em": 1000, "gm": 0}""", c["wallet"]);
        Eq("""{"spotting": [1, 1], "tracking": [1, 1], "weapon": {}}""", c["skills"]);
    }

    [Fact]
    public void NoSampleInTheInstallIsNoTemplate()
    {
        File.Delete(Path.Combine(install, "game", Install.ConnectSample));
        Assert.Null(Install.Connect(cfg, facts, Install.ItemDefinitions(install)));
    }

    [Fact]
    public void TheCatalogueComesFromTheArchivesAndTheFacts()
    {
        var app = Install.Application(install, facts, Install.ItemDefinitions(install));
        Eq("""[{"id": 0, "name": "Test Deer", "define": "TEST_DEER", "short": "deer"}]""", app["species"]);
        Eq("""[{"id": 1, "name": "Test Reserve"}]""", app["reserves"]);
        Eq("""[{"id": 900, "type": "LODGE", "reserve": 1, "pos": {"x": 10, "y": 20, "z": 30}}]""", app["locations"]);
        var items = app["items"]!.AsArray();
        Eq("""{"id": 257, "type": 1, "group": 1, "cat": 1, "client_type": 1, "price": 0, "notbuyable": 1, "units": null, "inventory": {"weight": 3500, "slots": [0, 2], "capacity": 0, "inbackpack": true, "slotUnlock": null}}""",
           Pick(items[0]!, "id", "type", "group", "cat", "client_type", "price", "notbuyable", "units", "inventory"));
        Eq("""{"name": "Test Shells", "notbuyable": 0, "units": {"count": 30, "name": "shells"}, "sponsor_image": "brand.jpg", "silver_price": 68}""",
           Pick(items[1]!, "name", "notbuyable", "units", "sponsor_image", "silver_price"));
        Assert.Equal(77, (int)items[2]!["client_type"]!);  // an explicit client type comes from the table, else it is the type
    }

    static JsonObject Pick(JsonNode n, params string[] keys) =>
        new(keys.Select(k => KeyValuePair.Create(k, n[k]?.DeepClone())));

    [Fact]
    public void ReservePopulationsAndAmmoAreTheClientsRows()
    {
        var ethical = (JsonArray)JsonNode.Parse("[[14, 15], [15]]")!;
        var body = JsonNode.Parse(facts.ReserveConnect(1, ethical)!)!;
        Eq("""[{"species_id": "0", "ammo": [14, 15]}, {"species_id": "1", "ammo": [15]}]""", body["species_ammo"]);
        var deer = body["populations"]![0]!;
        Eq("""{"key": "facts-0", "index": 0, "animal": "deer", "density_map": "deer", "endangered": false, "spawn_license": 1, "active_start": null, "active_end": null, "reserve": 1, "groups": [{"pos_x": 5, "pos_z": 6, "radius": 7, "males": 8, "females": 9, "enabled": null, "feature": -1, "texture_id": -1, "min_weight": null, "max_weight": null, "health": null, "wounded": null, "smell_sense_scale": null, "hearing_sense_scale": null, "vision_sense_scale": null}], "random_groups": {"males": 1, "females": 2, "min_group_size": 3, "max_group_size": 4, "average_group_size": 5, "max_males_in_group": 6, "max_females_in_group": 7, "radius": 8, "single_gender_group": 0}}""", deer);
        var dog = body["populations"]![1]!;  // a row of every reserve keeps reserve -1 and its own density map
        Assert.Equal((-1, "dog_y", 1), ((int)dog["reserve"]!, (string)dog["density_map"]!, (int)dog["groups"]![0]!["enabled"]!));
        Assert.Null(dog["random_groups"]);
        Assert.Null(facts.ReserveConnect(2, ethical));
    }

    [Fact]
    public void TheFactsFileOfTheRepositoryLoadsAndCoversEveryReserve()
    {
        var f = Facts.Default;
        Assert.NotEmpty(f.Animals);
        Assert.Equal(Enumerable.Range(1, 12), f.Reserves);
        Assert.Equal("weight_lb", f.Families[4]);
        var ethical = new JsonArray(Enumerable.Range(0, 100).Select(_ => (JsonNode?)new JsonArray(1)).ToArray());
        foreach (var r in f.Reserves)
        {
            var body = JsonNode.Parse(f.ReserveConnect(r, ethical)!)!;
            Assert.NotEmpty(body["populations"]!.AsArray());
            Assert.All(body["populations"]!.AsArray(), p => Assert.Contains((int)p!["reserve"]!, new[] { r, -1 }));
            Assert.All(body["species_ammo"]!.AsArray(), s => Assert.Equal("[1]", Py.Dumps(s!["ammo"])));
        }
    }

    HarTable Recorded(JsonObject connect)
    {
        var table = new HarTable();
        table.Entries[new("POST", "gameserver.test", Replay.CONNECT, null)] = new Hit(2, "2030-01-01", 200, [("X-Rec", "1")], Encoding.UTF8.GetBytes(Py.Dumps(connect)));
        return table;
    }

    [Fact]
    public async Task ServerStartsWithoutRecordingsAndConnectsANewAccount()
    {
        var table = new HarTable();
        var state = LoadState(cfg, table, tmp)!;
        Assert.Equal(Install.LocalUserId, state.LocalAccount);
        await using var server = await Loopback.StartAsync(new Game(table, cfg, state, Path.Combine(tmp, "mirror")));
        var form = new[] { ("Content-Type", "application/x-www-form-urlencoded") };
        await server.SendAsync("POST", "api.test", LAUNCH_START, form, Encoding.ASCII.GetBytes("start=900&reserve=3&loctype=LODGE"));
        var r = await server.SendAsync("POST", "gameserver.test", Replay.CONNECT);
        Assert.Equal((200, "state"), (r.Status, r.Source));
        Assert.Contains(("Content-Type", "application/json"), r.Headers);
        var reply = JsonNode.Parse(r.Body)!;
        Assert.Equal(Install.LocalHandle, (string)reply["account"]!["handle"]!);
        Assert.NotEmpty(reply["inventory"]!["items"]!.AsArray());  // the facts file's start kit
        Assert.NotNull(reply["wallet"]!["em"]);
        Assert.All(reply["populations"]!.AsArray(), p => Assert.Contains((int)p!["reserve"]!, new[] { 3, -1 }));  // the facts file's reserve 3
        Assert.NotEmpty(reply["species_ammo"]!.AsArray());
        Assert.NotNull(state.ItemInfo(14));  // the catalogue is built from the install's archives
    }

    [Fact]
    public void NoRecordingAndNoSampleIsNoState()
    {
        File.Delete(Path.Combine(install, "game", Install.ConnectSample));
        Assert.Null(LoadState(cfg, new HarTable(), tmp));
    }

    [Fact]
    public void ARecordedConnectWinsOverTheInstall()
    {
        var connect = Fixtures.CONNECT;
        var state = LoadState(cfg, Recorded(connect), tmp)!;
        Assert.Equal(Fixtures.ME, state.LocalAccount);
        Assert.Equal(Py.Dumps(connect["wallet"]), Py.Dumps(JsonNode.Parse(state.ConnectReply(Fixtures.ME))!["wallet"]));
    }

    [Fact]
    public void ANewAccountNeverStartsFromARecordedConnect()
    {
        var connect = Fixtures.CONNECT;
        connect["account"]!["avatar_url"] = "https://operator.test/me.png";
        var state = LoadState(cfg, Recorded(connect), tmp)!;
        state.CreateAccount(5000, "newcomer");
        var reply = JsonNode.Parse(state.ConnectReply(5000))!;
        Eq("""{"em": 1000, "gm": 0}""", reply["wallet"]);  // the facts file's start, not the recording's
        Assert.NotEqual(Py.Dumps(connect["inventory"]!["items"]), Py.Dumps(reply["inventory"]!["items"]));
        Assert.Equal("https://avatar.test/256/1.png", (string)reply["account"]!["avatar_url"]!);
        Assert.NotEqual((string)connect["account"]!["avatar_url"]!, (string)reply["account"]!["avatar_url"]!);
    }
}
