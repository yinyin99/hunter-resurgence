using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Resurgence.Server.Tests;

/// <summary>A backup zip built in memory the way the backup tool lays it out (manifest with the SHA-256 of each reply, claim.key).
/// Every id and value is made up.</summary>
sealed class TestZip
{
    public string Uid { get; }
    public string? Steam { get; }
    public string Handle { get; }
    const string Email = "nobody@example.invalid";
    readonly bool own;
    readonly List<(string Path, string Endpoint, string Json, Dictionary<string, string> Params)> files = [];

    TestZip(bool own, string uid, string? steam, string handle) => (this.own, Uid, Steam, Handle) = (own, uid, steam, handle);

    public TestZip Reply(string path, string endpoint, string json, params (string K, string V)[] ps)
    {
        files.Add((path, endpoint, json, ps.ToDictionary(p => p.K, p => p.V)));
        return this;
    }

    readonly List<(string Path, byte[] Body)> media = [];

    /// <summary>A saved picture or page (no JSON reply), listed in the manifest with its hash.</summary>
    public TestZip Media(string path, byte[] body)
    {
        media.Add((path, body));
        return this;
    }

    /// <summary>A few bytes that start like a JPEG: a made-up picture.</summary>
    public static byte[] Jpeg(string tag) => [0xff, 0xd8, 0xff, 0xe0, .. Encoding.ASCII.GetBytes(tag)];

    /// <summary>The saved `Gallery/list` page of the account: the entries as JSON, newest first.</summary>
    public TestZip Gallery(string photos) =>
        Reply($"Gallery_list__user_id={Uid}__offset=0__limit=24.json", "/v1/Gallery/list", $$"""{"total":3,"photos":{{photos}}}""", ("user_id", Uid), ("offset", "0"), ("limit", "24"));

    /// <summary>Gallery pictures a and b (b with its kill's score and species) and c whose picture is not in the zip; a hunt of two kills, the first with its photo and its score sheet page, the second with neither;
    /// an avatar. Every link is a made-up one.</summary>
    public TestZip Pictures() =>
        Gallery("""
                [{"id":53,"type":2,"url":"https://photos.example/p/c.jpg","thumbnail":"https://photos.example/p/c-t.jpg","label":"","animal":""},
                 {"id":52,"type":2,"url":"https://photos.example/p/b.jpg","thumbnail":"https://photos.example/p/b-t.jpg","label":"120.5","animal":"Test Deer"},
                 {"id":51,"type":3,"url":"https://photos.example/p/a.jpg","thumbnail":"https://photos.example/p/a-t.jpg","label":"","animal":""}]
                """)
        .Reply($"Public_user_expedition__user_id={Uid}__expedition_id=9100.json", "/v1/Public_user/expedition", """
            {"stats":null,
             "kills":[{"id":777,"speciesName":"Test Deer","weight":64350,"gender":0,"texture":0,
                       "kill":{"score":120.5,"confirmTs":1700000500,"photo":"https://photos.example/k/777.jpg","pos":{"x":10,"y":2,"z":30},"wound_time":1.5,"trophy_integrity":87.5},
                       "hits":[{"distance":82000,"weapon_id":5,"ammo_id":10,"organ":1}],"expedition":[{"id":9100,"reserve":2}]},
                      {"id":778,"speciesName":"Test Deer","weight":50000,"kill":{"score":80,"confirmTs":1700000600,"photo":"https://photos.example/k/778.jpg"},"hits":[]}],
             "expedition":{"id":9100,"reserve":2,"location_id":101,"start_ts":1700000100,"end_ts":1700003700}}
            """, ("user_id", Uid), ("expedition_id", "9100"))
        .Media("photos/photos.example_p_a.jpg", Jpeg("a")).Media("photos/photos.example_p_b.jpg", Jpeg("b"))
        .Media("kills/photos.example_k_777.jpg", Jpeg("kill")).Media("kills/score_777.html", "<div>saved sheet</div>"u8.ToArray())
        .Media("images/avatars.example_a_1.png", [0x89, 0x50, 0x4e, 0x47, 1]);

    public static string Zeros(int n, string v) => string.Join(',', Enumerable.Repeat(v, n));

    /// <summary>My-account zip: the profile (with an e-mail, a name and an address that must not be stored), a wallet, items
    /// (one placed), skills, statistics, achievements, a trophy, a lodge and a few replies kept as extras.</summary>
    public static TestZip Own(string wallet = "\"ems\":12.5,\"gms\":20000.25", string uid = "1001", string? steam = "76561190000000000", string handle = "sample",
        string avatar = "https://avatars.example/a.png") =>
        new TestZip(true, uid, steam, handle).Common()
        .Reply("Me_me.json", "/v1/Me/me", $$"""
            {"id":{{uid}},"handle":"{{handle}}","hostname":"sample","gender":1,"hunterscore":1234,"avatar":"{{avatar}}","membershipType":"freeplay",
             "uuid":"00000000-0000-0000-0000-000000000001","locale":"en_US","timezone":"UTC","country":"XX","memberSince":1500000000,"user_group":7,"rank":3,
             {{wallet}},"records":{"best":9},"worlditems":[],"email":"{{Email}}","first_name":"Test","last_name":"Person","address":"1 Test Street"}
            """)
        .Reply("Skill_list.json", "/v1/Skill/list", $$"""{"weapon":{"300":4.75,"200":10.2},"tracking":[{{Zeros(59, "1.5")}}],"spotting":[{{Zeros(59, "2")}}]}""", ("user_id", uid))
        .Reply("loadout.json", "/game/user/loadout", $$$"""{"slots":[1,2],"contact":{"email":"{{{Email}}}"}}""");

    /// <summary>Three hunts in the saved list (two pages, newest first); only the newest and the oldest were saved with their detail.</summary>
    public TestZip Hunts() =>
        Reply($"Expedition_list__user_id={Uid}__offset=0__limit=2.json", "/v1/Expedition/list",
              """{"total":3,"expeditions":[{"id":9003,"kills":1,"start_ts":1700000300},{"id":9002,"kills":0,"start_ts":1700000200}]}""", ("offset", "0"), ("limit", "2"))
        .Reply($"Expedition_list__user_id={Uid}__offset=2__limit=2.json", "/v1/Expedition/list",
               """{"total":3,"expeditions":[{"id":9001,"kills":2,"start_ts":1700000100}]}""", ("offset", "2"), ("limit", "2"))
        .Reply($"Public_user_expedition__user_id={Uid}__expedition_id=9003.json", "/v1/Public_user/expedition",
               """{"stats":null,"kills":[],"expedition":{"id":9003}}""", ("user_id", Uid), ("expedition_id", "9003"))
        .Reply($"Public_user_expedition__user_id={Uid}__expedition_id=9001.json", "/v1/Public_user/expedition",
               """{"stats":null,"kills":[],"expedition":{"id":9001}}""", ("user_id", Uid), ("expedition_id", "9001"));

    /// <summary>Public zip by id: no Me/me, wallet or skills.</summary>
    public static TestZip Public(string uid = "1001") => new TestZip(false, uid, null, "sample").Common();

    TestZip Common() => Reply("Inventory_dict.json", "/v1/Inventory/dict", """
            {"500":[{"id":9001,"slot":1,"units":3,"metadata":{},"location":null}],
             "610":[{"id":9101,"slot":23,"units":0,"metadata":{"dog_name":"Rex"},"location":{"pos":{"x":3144.645,"y":-0.999,"z":10.0005},"dir":{"x":0.5,"y":0,"z":-0.5},"reserve":2}}]}
            """, ("user_id", Uid))
        .Reply("Statistics_total.json", "/v1/Statistics/total", """{"duration":3600,"distance":12.5,"data":{"collectables":{},"weapon":{},"animal":{"5":{"kills":2}},"daily_missions":{}}}""", ("user_id", Uid))
        .Reply("Achievement_stats.json", "/v1/Achievement/stats",
            """[{"id":12,"category_id":1,"group_id":2,"value":3,"triggers":{"1":1600000000,"3":1600000100}},{"id":4,"category_id":1,"group_id":1,"value":0,"triggers":[]}]""", ("user_id", Uid))
        .Reply("minigames_0.json", "/v1/Page_content/minigames_list", """{"records":[],"total":10,"position":{"score":420,"rank":7}}""", ("game_id", "0"), ("user_id", Uid))
        .Reply("minigames_1.json", "/v1/Page_content/minigames_list", """{"records":[],"total":10,"position":null}""", ("game_id", "1"), ("user_id", Uid))
        .Reply("Trophy_list.json", "/v1/Trophy/list", """{"total":1,"trophies":[{"id":81,"trophy_id":3,"ts":1600000000}]}""", ("user_id", Uid))
        .Reply($"game_user_{Uid}_lodges.json", $"/game/user/{Uid}/lodges", """[{"id":61,"name":"Lodge"}]""")
        .Reply("Mission.json", "/v1/Mission/missions", """[{"id":1}]""");

    public string Write(string dir, string name = "backup.zip")
    {
        var key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var manifest = new JsonObject
        {
            ["tool_version"] = "0.0.0-test", ["format"] = 2, ["mode"] = own ? "own" : "public", ["identity"] = own ? "me" : "id", ["uid"] = Uid,
            ["claim_hash"] = Convert.ToHexStringLower(SHA256.HashData(key)), ["started_utc"] = "2026-01-02T03:04:05Z", ["completed_utc"] = "2026-01-02T04:00:00Z",
            ["files"] = new JsonArray([.. files.Select(f => (JsonNode?)new JsonObject
            {
                ["path"] = f.Path, ["endpoint"] = f.Endpoint, ["params"] = new JsonObject([.. f.Params.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))]),
                ["status"] = 200, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(f.Json))),
            }).Concat(media.Select(m => (JsonNode?)new JsonObject { ["path"] = m.Path, ["status"] = 200, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(m.Body)) }))]),
        };
        if (own)
        {
            manifest["handle"] = Handle;
            if (Steam is not null) manifest["steam_id64"] = Steam;
        }
        var path = Path.Combine(dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Put(string entry, byte[] body)
        {
            using var s = zip.CreateEntry(entry).Open();
            s.Write(body);
        }
        Put("manifest.json", Encoding.UTF8.GetBytes(manifest.ToJsonString()));
        Put("claim.key", key);
        foreach (var f in files)
            Put(f.Path, Encoding.UTF8.GetBytes(f.Json));
        foreach (var m in media)
            Put(m.Path, m.Body);
        return path;
    }
}
