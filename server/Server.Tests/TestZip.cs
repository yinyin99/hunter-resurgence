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

    public static string Zeros(int n, string v) => string.Join(',', Enumerable.Repeat(v, n));

    /// <summary>My-account zip: the profile (with an e-mail, a name and an address that must not be stored), a wallet, items
    /// (one placed), skills, statistics, achievements, a trophy, a lodge and a few replies kept as extras.</summary>
    public static TestZip Own(string wallet = "\"ems\":12.5,\"gms\":3000.25", string uid = "1001", string? steam = "76561190000000000", string handle = "sample") =>
        new TestZip(true, uid, steam, handle).Common()
        .Reply("Me_me.json", "/v1/Me/me", $$"""
            {"id":{{uid}},"handle":"{{handle}}","hostname":"sample","gender":1,"hunterscore":1234,"avatar":"https://avatars.example/a.png","membershipType":"freeplay",
             "uuid":"00000000-0000-0000-0000-000000000001","locale":"en_US","timezone":"UTC","country":"XX","memberSince":1500000000,"user_group":7,"rank":3,
             {{wallet}},"records":{"best":9},"worlditems":[],"email":"{{Email}}","first_name":"Test","last_name":"Person","address":"1 Test Street"}
            """)
        .Reply("Skill_list.json", "/v1/Skill/list", $$"""{"weapon":{"300":4.75,"200":10.2},"tracking":[{{Zeros(59, "1.5")}}],"spotting":[{{Zeros(59, "2")}}]}""", ("user_id", uid))
        .Reply("loadout.json", "/game/user/loadout", $$$"""{"slots":[1,2],"contact":{"email":"{{{Email}}}"}}""");

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
            })]),
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
        return path;
    }
}
