using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Restore;

namespace Resurgence.Tests.RestoreTests;

/// <summary>A zip in the layout of a third tool's account export: ARCHIVE_MANIFEST.json with a SHA-256 per file, `authenticated/`, `profile/resume/` and the gallery pictures. Every id and value is made up.</summary>
sealed class ArchiveZip
{
    public readonly Dictionary<string, byte[]> Files = new(StringComparer.Ordinal);
    /// <summary>Entries the manifest lists with this path's hash replaced (a file changed after it was listed).</summary>
    public readonly HashSet<string> Tampered = [];
    public readonly HashSet<string> Unlisted = [];

    public ArchiveZip Add(string name, string json) => Add(name, Encoding.UTF8.GetBytes(json));
    public ArchiveZip Add(string name, byte[] body) { Files[name] = body; return this; }

    public byte[] Build()
    {
        var listed = Files.Where(f => !Unlisted.Contains(f.Key)).Select(f => (JsonNode?)new JsonObject
        {
            ["path"] = f.Key, ["size"] = f.Value.Length,
            ["sha256"] = Tampered.Contains(f.Key) ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(f.Value)),
        });
        var manifest = new JsonObject { ["schema"] = "example-account-file-manifest-v1", ["files"] = new JsonArray([.. listed]) };
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Put(string name, byte[] body) { using var s = z.CreateEntry(name).Open(); s.Write(body); }
            Put("ARCHIVE_MANIFEST.json", Encoding.UTF8.GetBytes(manifest.ToJsonString()));
            foreach (var (name, body) in Files) Put(name, body);
        }
        return ms.ToArray();
    }

    public Restored Read() => Importer.Read(new MemoryStream(Build()));
}

/// <summary>Restore layer A on the third tool's export: the same restore as our own zip, the checksums, the identity and the pictures it holds.</summary>
public class ArchiveBackupTests
{
    static readonly byte[] Jpg1 = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3], Jpg2 = [0xFF, 0xD8, 0xFF, 0xE0, 4, 5, 6];

    static string Zeros(int n, string v) => string.Join(',', Enumerable.Repeat(v, n));

    static ArchiveZip Fixture() => new ArchiveZip()
        .Add("EXPORT_REPORT.json", """{"captured_at":"2026-01-02T03:04:05Z","status":"NEEDS_REVIEW","missing":["profile_history_media"]}""")
        .Add("export_state.json", """{"handle":"sample"}""")
        .Add("account/connect.json", $$$"""
            {"account":{"user_id":1001,"handle":"sample"},"achievements":[{"id":1,"description":"a definition"}],
             "inventory":{"items":[{"guid":"9001","item_id":500,"slot":1,"units":3,"metadata":{}},{"guid":"9002","item_id":500,"slot":2,"units":null,"metadata":{}},{"guid":"9003","item_id":610,"slot":1,"units":null,"metadata":{}}]},
             "skills":{"weapon":{"300":9},"tracking":[{{{Zeros(59, "9")}}}],"spotting":[{{{Zeros(59, "9")}}}]},
             "user_stats":{"user_stats":{"animal":{"5":{"kills":2}},"collectables":{},"daily_missions":{},"weapon":{},"distance":12,"duration":3600}}
             }
            """)
        .Add("authenticated/account.json", """{"id":1001,"handle":"sample","hostname":"sample","hunterscore":1234,"ems":12.5,"gms":3000.25,"membershipType":"freeplay","avatar":"https://avatars.example/a.png"}""")
        .Add("authenticated/conversation_abc.json", """[{"user_id":2002,"content":"hi"}]""")
        .Add("authenticated/pages/conversation_abc/response.json", """{"total":1,"messages":[{"user_id":2002,"content":"hi","ts":1}]}""")
        .Add("authenticated/pages/wallet_em_history/0.json", """[{"id":1,"amount":5}]""")
        .Add("authenticated/pages/wallet_em_history/50.json", """[{"id":2,"amount":6}]""")
        .Add("authenticated/pages/wallet_gm_history/0.json", """[{"id":3,"amount":7}]""")
        .Add("authenticated/wallet_gm_history.json", """[{"id":3,"amount":7}]""")
        .Add("authenticated/lodge_rules_animal.json", """{"x":1}""")
        .Add("profile/resume/sections/achievements.json", """[{"id":12,"category_id":1,"group_id":2,"value":3,"triggers":{"1":1600000000}}]""")
        .Add("profile/resume/sections/skills.json", $$"""{"weapon":{"300":4.75},"tracking":[{{Zeros(59, "1.5")}}],"spotting":[{{Zeros(59, "2")}}]}""")
        .Add("profile/resume/sections/owned_trophies.json", """[{"id":700,"species":5,"score":12.5}]""")
        .Add("profile/resume/sections/loadouts.json", """[{"id":1,"name":"set","items":[500]}]""")
        .Add("profile/resume/sections/lodges.json", """[{"id":61,"name":"Lodge"}]""")
        .Add("profile/resume/sections/lodge_details.json", """{"61":{"id":61,"name":"Lodge","weapon_slots_unlocked":true}}""")
        .Add("profile/resume/sections/dogs.json", """{"432":[{"id":1}]}""")
        .Add("profile/resume/pages/trophies/000000000.json", """{"total":2,"trophies":[{"id":81}]}""")
        .Add("profile/resume/pages/trophies/000000024.json", """{"total":2,"trophies":[{"id":82}]}""")
        .Add("profile/resume/pages/gallery/000000000.json", """
            {"total":2,"photos":[{"id":52,"type":2,"url":"https://photos.example/p/b.jpg","label":null,"animal":null},{"id":53,"type":1,"url":"https://photos.example/p/c.jpg","label":null,"animal":null}]}
            """)
        .Add("profile/resume/expeditions/9100.json", """{"expedition":{"id":9100,"reserve":2},"kills":[],"stats":null}""")
        .Add("profile/resume/expeditions/9101.json", """{"expedition":{"id":9101,"reserve":3},"kills":[],"stats":null}""")
        .Add("profile/media/gallery/52_full.jpg", Jpg1).Add("profile/media/gallery/52_full.jpg.mime", "image/jpeg").Add("profile/media/gallery/52_thumb.jpg", Jpg2)
        .Add("profile/resume/media/gallery/52_full.jpg", Jpg1)
        .Add("profile/media/gallery/53_full.jpg", Jpg2)
        .Add("launcher/capture_report.json", """{"private":true}""").Add("capture.log", "private");

    [Fact]
    public void AnExportRestoresLikeOurOwnZip()
    {
        var r = Fixture().Read();
        var m = r.Model;
        Assert.Empty(r.Report.Rejected);
        Assert.Equal("1001", m.Identity.Uid);
        Assert.Equal("sample", m.Identity.Handle);
        Assert.Null(m.Identity.SteamId64);
        Assert.Null(m.Identity.ClaimHash);
        Assert.Equal(1234, m.Profile.Hunterscore);
        Assert.Equal(new Wallet(12.5, 3000.25), m.Wallet);
        Assert.Equal(3, m.Items.Count);                                   // from the connect reply's flat rows
        Assert.Equal(4.75, m.Skills!.Weapon[300]);                        // the sections' skills, not the connect reply's
        Assert.Equal(2, m.Stats!.UserStats["animal"]!["5"]!["kills"]!.GetValue<int>());
        Assert.Equal(3600, m.Stats.Duration);
        Assert.Single(m.Achievements);
        Assert.Equal(3, m.Trophies.Count);                                // two pages of Trophy/list and one own trophy
        Assert.Equal(2, m.Lodges.Count);                                  // the list and the lodge's own reply
        Assert.Contains(m.Extras.Keys, k => k.StartsWith("game_user_loadout", StringComparison.Ordinal));
        Assert.Equal(2, m.Extras["Wallet_transactions__lang=en__offset=0.json"]!.AsArray().Count);
        Assert.Single(m.Extras["Wallet_silver_transactions__lang=en__offset=0.json"]!.AsArray());
        Assert.Contains("Public_user_expedition__user_id=1001__expedition_id=9100.json", m.Extras.Keys);
        Assert.Contains("Public_user_expedition__user_id=1001__expedition_id=9101.json", m.Extras.Keys);
        Assert.Contains("Chat_conversation__user_id=2002.json", m.Extras.Keys);
        Assert.Contains("profile/resume/sections/dogs.json", m.Extras.Keys);
        Assert.DoesNotContain(m.Extras.Keys, k => k.Contains("launcher") || k.Contains("capture") || k.Contains("connect.json") || k.StartsWith("authenticated/conversation_abc.json"));
        Assert.Contains("profile_history_media (EXPORT_REPORT.json says it was not saved)", r.Report.Missing);
        // the gallery pictures come from the zip, under the path our backups give a link's picture
        Assert.Equal(["photos/photos.example_p_b.jpg", "photos/photos.example_p_c.jpg"], r.Media.Select(x => x.Path).Order());
        Assert.Equal(["profile/media/gallery/52_full.jpg", "profile/media/gallery/53_full.jpg"], r.Media.Select(x => x.Entry).Order());
        Assert.Equal(2, m.Extras["Gallery_list__user_id=1001__offset=0__limit=24.json"]!["photos"]!.AsArray().Count);
    }

    [Fact]
    public void AFileWhoseChecksumDiffersIsRejected()
    {
        var z = Fixture();
        z.Tampered.Add("profile/resume/sections/lodges.json");
        z.Tampered.Add("profile/media/gallery/53_full.jpg");
        z.Unlisted.Add("profile/resume/sections/dogs.json");
        var r = z.Read();
        Assert.Contains("profile/resume/sections/lodges.json: sha256 does not match the manifest", r.Report.Rejected);
        Assert.Contains("profile/media/gallery/53_full.jpg: sha256 does not match the manifest", r.Report.Rejected);
        Assert.Contains("profile/resume/sections/dogs.json: not listed in ARCHIVE_MANIFEST.json", r.Report.Rejected);
        Assert.Single(r.Model.Lodges);                                    // only the lodge's own reply
        Assert.Single(r.Media);
        Assert.DoesNotContain("profile/resume/sections/dogs.json", r.Model.Extras.Keys);
    }

    [Fact]
    public void TheConnectReplyIsOnlyUsedForWhatTheOtherFilesLack()
    {
        var z = Fixture();
        z.Files.Remove("profile/resume/sections/skills.json");
        z.Add("authenticated/inventory.json", """[{"item":{"id":500},"own":[{"id":9001,"slot":1,"units":3,"metadata":{},"location":null}]}]""");
        var r = z.Read();
        Assert.Single(r.Model.Items);                            // the inventory file, not the connect reply's three rows
        Assert.Equal(9, r.Model.Skills!.Weapon[300]);                      // no skills section: the connect reply's
        // a shape the mapper cannot read stays in Extras and is reported missing
        var odd = Fixture();
        odd.Add("account/connect.json", """{"account":{"user_id":1001},"inventory":{"items":[{"item_id":"x"}]},"user_stats":{"user_stats":{}}}""");
        var o = odd.Read();
        Assert.Empty(o.Model.Items);
        Assert.Null(o.Model.Stats);
        Assert.Contains("items (not in the zip)", o.Report.Missing);
        Assert.Contains("account/connect.json#inventory", o.Model.Extras.Keys);
    }

    [Fact]
    public void AnAccountTheConnectReplyDisagreesWithIsRefused()
    {
        var z = Fixture().Add("account/connect.json", """{"account":{"user_id":1002}}""");
        Assert.Contains("1002", Assert.Throws<RestoreException>(() => z.Read()).Message);
    }
}
