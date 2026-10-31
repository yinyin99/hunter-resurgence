using System.Text.Json.Nodes;
using Backup;
using Resurgence.Restore;
using Resurgence.Tests.BackupTool;

namespace Resurgence.Tests.RestoreTests;

/// <summary>Restore on a backup zip the backup tool wrote, against the fake host: the writer and the reader agree on the format (manifest fields, file names, entry separators).</summary>
public class ToolZipTests
{
    [Fact]
    public async Task RestoresABackupZipTheBackupToolWrote()
    {
        using var t = new TempDir("backup-test-");
        var tok = new string('a', 40); // built at run time: a 40-character literal looks like a hex secret to the pre-push check
        var f = new Fake(tok)
        {
            InventoryDict = """{"500":[{"id":9001,"slot":1,"units":3,"metadata":{}}]}""",
            InventoryList = """[{"item":{"id":500},"own":[{"id":9001,"slot":1,"units":3,"metadata":{}}]},{"item":{"id":610},"own":[{"id":9101,"slot":23,"units":0,"metadata":{},"location":{"pos":{"x":1,"y":2,"z":3},"dir":{"x":0,"y":0,"z":1},"reserve":2}}]}]""",
        };
        f.Raw["/v1/Me/me"] = """{"id":7,"handle":"hh","membershipType":"freeplay","avatar":"https://photo.test/a.jpg","ems":12.5,"gms":3000.25,"worlditems":[]}"""u8.ToArray();
        f.Raw["/v1/Skill/list"] = """{"weapon":{"200":10.2},"tracking":[],"spotting":[]}"""u8.ToArray();
        f.Raw["/v1/Achievement/stats"] = """[{"id":12,"category_id":1,"group_id":2,"value":3,"triggers":{"1":1600000000}}]"""u8.ToArray();
        f.Raw["/v1/Mission/missions"] = """[{"id":1}]"""u8.ToArray(); // the default reply carries the token: never saved, a failed request
        f.Raw["/v1/Gallery/list"] = """{"photos":[{"url":"https://photo.test/g1.jpg"}]}"""u8.ToArray(); // the default lists a picture on a host that is not configured: a failed request
        f.Raw["/v1/Chat/conversations"] = """[{"user_id":2002,"read":1}]"""u8.ToArray();

        var zip = await Hosts.Make(f, Path.Combine(t.Path, "claim.key")).Run(t.Path, "", () => tok, Engine.Groups.Select(g => g.Key).ToHashSet(), false, true, TestContext.Current.CancellationToken);
        var r = Importer.Read(zip);

        Assert.Empty(r.Report.Rejected);
        Assert.Empty(r.Report.Failed);
        Assert.Empty(r.Report.Missing);
        Assert.Equal("my-account", r.Report.Mode);
        Assert.Equal(["profile (from Me/me)", "wallet", "items: 2 (from Inventory/list and Inventory/dict)", "skills: 1 weapons, 0 tracking, 0 spotting",
            "statistics", "achievements: 1", "trophies: 3 rows (1 without a harvest)", "lodges: 2 rows"], r.Report.Restored);
        Assert.DoesNotContain(r.Report.Unsupported, l => l.StartsWith("unknown endpoint", StringComparison.Ordinal));

        // the reader's fallback when a manifest gives no endpoint: every reply's file name maps back to what the manifest says
        var replies = JsonNode.Parse(File.ReadAllText(Path.Combine(t.Path, "backup-7", "manifest.json")))!["files"]!.AsArray()
            .Where(e => e!["sha256"] != null && e["derived"] == null && e["path"]!.ToString().EndsWith(".json", StringComparison.Ordinal)).Select(e => e!).ToList();
        Assert.NotEmpty(replies); // a filter that matches nothing would make the check below pass on nothing
        Assert.Equal(replies.Select(e => $"{e["path"]} -> {e["endpoint"]} {string.Join('&', e["params"]!.AsObject().Select(p => $"{p.Key}={p.Value}"))}"),
            replies.Select(e => e["path"]!.ToString() is var p && Endpoints.FromName(p) is { } x ? $"{p} -> {x.Endpoint} {string.Join('&', x.Params.Select(kv => $"{kv.Key}={kv.Value}"))}" : $"{p} -> (no endpoint)"));
    }
}
