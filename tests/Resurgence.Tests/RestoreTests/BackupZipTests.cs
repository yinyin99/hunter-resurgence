using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Resurgence.Restore;

namespace Resurgence.Tests.RestoreTests;

/// <summary>The check of a backup zip made by the backup tool, on temp zips.</summary>
public class BackupZipTests
{
    static string Zip(string root, string name, Action<ZipArchive> fill)
    {
        var path = Path.Combine(root, name);
        using (var z = ZipFile.Open(path, ZipArchiveMode.Create)) fill(z);
        return path;
    }

    static void Add(ZipArchive z, string name, byte[] data)
    {
        using var s = z.CreateEntry(name).Open();
        s.Write(data);
    }

    [Fact]
    public void ReadsGoodZipsAndNamesWhatIsWrongWithTheOthers()
    {
        using var temp = new TempDir("resurgence-test-");
        var root = temp.Path;
        var key = RandomNumberGenerator.GetBytes(32);
        string Manifest(string hash) => $$"""
            {"tool_version":"1.2.3","groups":["profile","hunts"],"uid":"42","handle":"sample","claim_hash":"{{hash}}","started_utc":"2026-10-19T00:47:10Z",
             "files":[{"path":"a.json","endpoint":"/e/one","status":200},{"path":"b.json","endpoint":"/e/one","status":200},{"path":"c.json","endpoint":"/e/two","status":404,"error":"gone"}],
             "steam_id64":"76561190000000000"}
            """;
        var good = Convert.ToHexStringLower(SHA256.HashData(key));

        var r = BackupZip.Read(Zip(root, "good.zip", z => { Add(z, "manifest.json", Encoding.UTF8.GetBytes(Manifest(good))); Add(z, "claim.key", key); }));
        Assert.True(r.Info is { Uid: "42", Handle: "sample", ToolVersion: "1.2.3", Files: 3, Failed: 1, SteamId64: "76561190000000000" } i && i.Groups.SequenceEqual(["profile", "hunts"]), "good zip: uid, handle, version, groups, counts");
        Assert.True(r.Info!.Endpoints.SequenceEqual([("/e/one", 2, 0), ("/e/two", 1, 1)]), "good zip: files per endpoint");
        var format2 = Manifest(good).Replace("\"tool_version\"", "\"format\":2,\"mode\":\"own\",\"identity\":\"me\",\"checks\":{\"hunts\":{\"total\":3,\"listed\":2,\"saved\":2}},\"tool_version\"")
            .Replace("\"status\":200}", "\"status\":200,\"date\":\"2026-10-19T00:47:11Z\"}");
        var r2 = BackupZip.Read(Zip(root, "format2.zip", z => { Add(z, "manifest.json", Encoding.UTF8.GetBytes(format2)); Add(z, "claim.key", key); }));
        Assert.True(format2.Contains("\"date\"") && r2.Error is null && r2.Info is { Uid: "42", Files: 3, Failed: 1 } && r2.Info.Endpoints.SequenceEqual(r.Info.Endpoints), "format 2 manifest (format, mode, identity, date, checks): same reading");
        Assert.True(BackupZip.Read(Zip(root, "nested.zip", z => { Add(z, "backup-42/manifest.json", Encoding.UTF8.GetBytes(Manifest(good.ToUpperInvariant()))); Add(z, "backup-42/claim.key", key); })).Info is not null, "zip with one folder level, upper-case hash");
        Assert.True(BackupZip.Read(Zip(root, "wrong.zip", z => { Add(z, "manifest.json", Encoding.UTF8.GetBytes(Manifest(good))); Add(z, "claim.key", RandomNumberGenerator.GetBytes(32)); })).Error == BackupError.ClaimMismatch, "wrong claim key: claim mismatch");
        Assert.True(BackupZip.Read(Zip(root, "nomanifest.zip", z => Add(z, "claim.key", key))).Error == BackupError.NoManifest, "no manifest.json");
        Assert.True(BackupZip.Read(Zip(root, "nokey.zip", z => Add(z, "manifest.json", Encoding.UTF8.GetBytes(Manifest(good))))).Error == BackupError.NoClaimKey, "no claim.key");
        Assert.True(BackupZip.Read(Zip(root, "badjson.zip", z => { Add(z, "manifest.json", "{ nope"u8.ToArray()); Add(z, "claim.key", key); })).Error == BackupError.BadManifest, "manifest that is not json");
        Assert.True(BackupZip.Read(Zip(root, "nohash.zip", z => { Add(z, "manifest.json", "{}"u8.ToArray()); Add(z, "claim.key", key); })).Error == BackupError.BadManifest, "manifest without uid and claim_hash");
        var notZip = Path.Combine(root, "plain.zip");
        File.WriteAllText(notZip, "this is not a zip");
        Assert.True(BackupZip.Read(notZip).Error == BackupError.NotZip, "not a zip");
        Assert.True(BackupZip.Read(Path.Combine(root, "absent.zip")).Error == BackupError.Unreadable, "file that does not exist");
    }
}
