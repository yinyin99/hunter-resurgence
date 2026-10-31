using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Restore;

namespace Resurgence.Tests.RestoreTests;

/// <summary>A backup zip built in memory the way the backup tool lays it out: replies named by endpoint and params, a manifest with their SHA-256, claim.key.
/// Every id and value is made up.</summary>
sealed class SyntheticZip
{
    public const string Uid = "1001";
    public static readonly byte[] Key = [.. Enumerable.Range(1, 32).Select(i => (byte)i)];

    /// <summary>Top-level manifest members; files, uid and claim_hash are added on Build.</summary>
    public JsonObject Manifest = new() { ["tool_version"] = "0.0.0-test", ["groups"] = new JsonArray("profile"), ["started_utc"] = "2026-01-02T03:04:05Z" };
    readonly List<(JsonObject Entry, byte[]? Body)> files = [];
    /// <summary>Entries written as they are, outside the manifest (tampering).</summary>
    public readonly List<(string Name, byte[] Body)> Raw = [];
    public string Prefix = "";
    /// <summary>Edits the manifest text after it is serialized (a manifest no JsonObject can hold).</summary>
    public Func<string, string> ManifestText = s => s;
    public byte[] ClaimKey = Key;
    public CompressionLevel Level = CompressionLevel.Optimal;

    public SyntheticZip Reply(string endpoint, string json, params (string K, string V)[] ps) => File(Backup.Engine.FileName(new Backup.R(endpoint, ps)), endpoint, Encoding.UTF8.GetBytes(json), ps);

    public SyntheticZip File(string path, string endpoint, byte[] body, (string K, string V)[]? ps = null, int status = 200, bool derived = false)
    {
        var e = new JsonObject { ["path"] = path, ["endpoint"] = endpoint, ["params"] = new JsonObject([.. (ps ?? []).Select(p => KeyValuePair.Create(p.K, (JsonNode?)p.V))]) };
        files.RemoveAll(f => (string)f.Entry["path"]! == path); // a second reply for the same name replaces the first, as a later run does
        if (!derived) e["status"] = status;
        e["sha256"] = Convert.ToHexStringLower(SHA256.HashData(body));
        if (derived) e["derived"] = true;
        files.Add((e, body));
        return this;
    }

    public SyntheticZip Failed(string path, string endpoint, string error)
    {
        files.Add((new JsonObject { ["path"] = path, ["endpoint"] = endpoint, ["params"] = new JsonObject(), ["error"] = error }, null));
        return this;
    }

    /// <summary>The manifest entry of a file, to change it after Reply.</summary>
    public JsonObject Entry(string path) => files.Single(f => (string)f.Entry["path"]! == path).Entry;

    public byte[] Build()
    {
        var m = (JsonObject)Manifest.DeepClone();
        m["uid"] ??= Uid;
        m["claim_hash"] ??= Convert.ToHexStringLower(SHA256.HashData(Key));
        m["files"] = new JsonArray([.. files.Select(f => f.Entry.DeepClone())]);
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Put(string name, byte[] body) { using var s = z.CreateEntry(name, Level).Open(); s.Write(body); }
            Put(Prefix + "manifest.json", Encoding.UTF8.GetBytes(ManifestText(m.ToJsonString())));
            Put(Prefix + "claim.key", ClaimKey);
            foreach (var (e, body) in files) if (body is not null) Put(Prefix + (string)e["path"]!, body);
            foreach (var (name, body) in Raw) Put(name, body);
        }
        return ms.ToArray();
    }

    public Restored Read(IItemCatalogue? catalogue = null, Limits? limits = null) => Importer.Read(new MemoryStream(Build()), catalogue, limits);

    public string Refused(Limits? limits = null) => Assert.Throws<RestoreException>(() => Read(limits: limits)).Message;

    // ---------- the three kinds of zip ----------

    /// <summary>My-account backup, manifest format 2.</summary>
    public static SyntheticZip Own(int? format = 2)
    {
        var z = new SyntheticZip();
        if (format is { } f) { z.Manifest["format"] = f; z.Manifest["mode"] = "own"; z.Manifest["identity"] = "me"; }
        z.Manifest["handle"] = "sample";
        z.Manifest["steam_id64"] = "76561190000000000";
        z.Manifest["completed_utc"] = "2026-01-02T04:00:00Z";
        z.Reply("/v1/Me/me", $$"""
            {"id":{{Uid}},"handle":"sample","hostname":"sample","gender":1,"hunterscore":1234,"avatar":"https://avatars.example/a.png","membershipType":"freeplay",
             "uuid":"00000000-0000-0000-0000-000000000001","locale":"en_US","timezone":"UTC","country":"XX","memberSince":1500000000,"user_group":7,"rank":3,
             "ems":12.5,"gms":3000.25,"worlditems":[]}
            """);
        z.Public();
        z.Reply("/game/user/trophies", """[{"id":71,"species":5}]""");
        z.File("game_user_harvest_71.json", "/game/user/harvest/71", """{"id":71,"weight":80.5}"""u8.ToArray());
        z.File("game_user_harvest_72.json", "/game/user/harvest/72", """{"message":"not found"}"""u8.ToArray(), status: 404);
        z.Reply("/game/user/loadout", """{"slots":[1,2]}""");
        z.Reply("/v1/Mission/missions", """[{"id":1}]""");
        z.Reply("/v1/Chat/conversations", """[{"user_id":2002,"read":1}]""");
        z.Reply("/v1/Chat/conversation", """{"messages":[],"total":0}""", ("user_id", "2002"));
        return z;
    }

    /// <summary>Public profile by id (no identity reply) with the public replies; byHandle adds Public_user/getByHostname.</summary>
    public static SyntheticZip PublicZip(bool byHandle, int? format = 2)
    {
        var z = new SyntheticZip();
        if (format is { } f) { z.Manifest["format"] = f; z.Manifest["mode"] = "public"; z.Manifest["identity"] = byHandle ? "handle" : "id"; }
        if (byHandle)
        {
            z.Manifest["handle"] = "sample";
            z.Reply("/v1/Public_user/getByHostname", $$"""{"id":{{Uid}},"handle":"sample","hostname":"sample","hunterscore":1234,"membershipType":"freeplay"}""", ("hostname", "sample"));
        }
        return z.Public();
    }

    SyntheticZip Public()
    {
        Reply("/v1/Inventory/dict", """
            {"500":[{"id":9001,"slot":1,"units":3,"metadata":{},"location":null}],
             "610":[{"id":9101,"slot":23,"units":0,"metadata":{},"location":{"pos":{"x":3144.645,"y":-0.999,"z":10.0005},"dir":{"x":0.5,"y":0,"z":-0.5},"reserve":2}}]}
            """, ("user_id", Uid));
        Reply("/v1/Skill/list", """{"weapon":{"300":4.75,"200":10.2},"tracking":[""" + string.Join(',', Enumerable.Repeat("1.5", 59)) + "],\"spotting\":[" + string.Join(',', Enumerable.Repeat("2", 59)) + "]}", ("user_id", Uid));
        Reply("/v1/Statistics/total", """{"duration":3600,"distance":12.5,"data":{"collectables":{},"weapon":{},"animal":{"5":{"kills":2}},"daily_missions":{}}}""", ("user_id", Uid));
        Reply("/v1/Achievement/stats", """[{"id":12,"category_id":1,"group_id":2,"value":3,"triggers":{"1":1600000000,"3":1600000100}},{"id":4,"category_id":1,"group_id":1,"value":0,"triggers":[]}]""", ("user_id", Uid));
        Reply("/v1/Page_content/minigames_list", """{"records":[],"total":10,"position":{"score":420,"rank":7}}""", ("offset", "0"), ("limit", "1"), ("game_id", "0"), ("user_id", Uid));
        Reply("/v1/Page_content/minigames_list", """{"records":[],"total":10,"position":null}""", ("offset", "0"), ("limit", "1"), ("game_id", "1"), ("user_id", Uid));
        Reply("/v1/Trophy/list", """{"total":1,"trophies":[{"id":81,"trophy_id":3,"ts":1600000000}]}""", ("user_id", Uid), ("offset", "0"), ("limit", "100000"));
        Reply($"/game/user/{Uid}/profile", """{"hunterscore":1234,"earned_achievements":1}""");
        Reply($"/game/user/{Uid}/lodges", """[{"id":61,"name":"Lodge"}]""");
        Reply($"/game/user/{Uid}/lodges/61", """{"id":61,"trophies":[]}""");
        Reply("/v1/Expedition/list", """{"total":1,"expeditions":[{"id":42}]}""", ("user_id", Uid), ("limit", "50"), ("offset", "0"));
        Reply("/v1/Public_user/expedition", """{"id":42,"kills":[]}""", ("user_id", Uid), ("expedition_id", "42"));
        Reply("/v1/User/friendslist", """[]""", ("user_id", Uid), ("offset", "0"), ("limit", "0"));
        return this;
    }
}
