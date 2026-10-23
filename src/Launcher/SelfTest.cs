using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Resurgence.Launcher;

/// <summary>`resurgence-launcher.exe --selftest`: a temp Steam tree and temp zips; nothing on this PC is read or changed. Exit code 0 = all ok.</summary>
static class SelfTest
{
    static void Check(bool ok, string what)
    {
        if (!ok) throw new Exception("FAIL " + what);
        Console.WriteLine("ok   " + what);
    }

    public static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "resurgence-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            Game(root);
            PrefsRoundTrip(root);
            Zips(root);
            Console.WriteLine("selftest ok");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    static void Touch(string path) => Write(path, "x");

    static void Game(string root)
    {
        var steam = Path.Combine(root, "Steam");
        var lib2 = Path.Combine(root, "Other Library");
        var s = new Settings("4711", @"client-dir\client.exe");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        // libraryfolders.vdf as Steam writes it: backslashes doubled, the default library listed too
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), $$"""
            "libraryfolders"
            {
                "0" { "path" "{{steam.Replace(@"\", @"\\")}}" "apps" { "228980" "1" } }
                "1" { "path" "{{lib2.Replace(@"\", @"\\")}}" "apps" { "4711" "123456" } }
            }
            """);
        Check(GameFinder.Libraries(steam).SequenceEqual([steam, lib2]), "libraryfolders.vdf: both library paths, backslashes unescaped");
        Write(Path.Combine(lib2, "steamapps", "appmanifest_4711.acf"), "\"AppState\"\n{\n\t\"appid\"\t\t\"4711\"\n\t\"installdir\"\t\t\"Fake Game\"\n}\n");
        Check(GameFinder.VdfValue("\"AppState\" { \"installdir\" \"A \\\\ B\" }", "installdir") == @"A \ B", "vdf value unescaped");

        var folder = Path.Combine(lib2, "steamapps", "common", "Fake Game");
        Check(GameFinder.Find(s, null, [], steam) is null, "no client exe in the install folder: not found");
        Touch(Path.Combine(folder, s.ClientExe!));
        Check(GameFinder.Find(s, null, [], steam)?.Folder == folder, "appmanifest installdir in the second library gives the install folder");
        Check(GameFinder.Find(s with { SteamAppId = "999" }, null, [], steam) is null, "other app id: not found");
        Check(GameFinder.Find(new Settings(null, null), null, [], steam) is null, "placeholder settings: not configured, no crash");
        Check(GameFinder.Find(s, null, [], null) is null, "no Steam: not found");

        var manual = Path.Combine(root, "Manual");
        Check(GameFinder.Find(s, manual, [], steam)?.Folder == folder, "chosen folder without the client is ignored");
        Touch(Path.Combine(manual, s.ClientExe!));
        Check(GameFinder.Find(s, manual, [], steam)?.Folder == manual, "valid chosen folder wins over Steam");

        var portable = Path.Combine(root, "Portable");
        Touch(Path.Combine(portable, s.ClientExe!));
        Touch(Path.Combine(portable, "sub", "starter.exe"));
        Check(GameFinder.Find(s, null, [Path.Combine(portable, "sub", "starter.exe"), "-flag"], null)?.Folder == portable, "launch-options exe in a subfolder gives its parent install folder");
        Check(Settings.Load(Path.Combine(root, "missing.json")) == new Settings(null, null), "missing settings.json: not configured");
        var bad = Path.Combine(root, "bad.json");
        File.WriteAllText(bad, """{"steam_app_id":"<steam-app-id>","client_exe":"..\\evil.exe"}""");
        Check(Settings.Load(bad) == new Settings(null, null), "placeholder and parent-relative values are not configured");
    }

    static void PrefsRoundTrip(string root)
    {
        var path = Path.Combine(root, "prefs", "launcher.json");
        Check(Prefs.Load(path).Language == "en", "no launcher.json: defaults");
        new Prefs { Language = "de", GameFolder = @"C:\x y", CloseOnGameStart = true }.Save(path);
        var p = Prefs.Load(path);
        Check(p is { Language: "de", GameFolder: @"C:\x y", CloseOnGameStart: true }, "launcher.json round trip");
        File.WriteAllText(path, "{ not json");
        Check(Prefs.Load(path).Language == "en", "damaged launcher.json: defaults");
    }

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

    static void Zips(string root)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        string Manifest(string hash) => $$"""
            {"tool_version":"1.2.3","groups":["profile","hunts"],"uid":"42","handle":"sample","claim_hash":"{{hash}}","started_utc":"2026-10-19T00:47:10Z",
             "files":[{"path":"a.json","endpoint":"/e/one","status":200},{"path":"b.json","endpoint":"/e/one","status":200},{"path":"c.json","endpoint":"/e/two","status":404,"error":"gone"}],
             "steam_id64":"76561190000000000"}
            """;
        var good = Convert.ToHexStringLower(SHA256.HashData(key));

        var r = BackupZip.Read(Zip(root, "good.zip", z => { Add(z, "manifest.json", Encoding.UTF8.GetBytes(Manifest(good))); Add(z, "claim.key", key); }));
        Check(r.Info is { Uid: "42", Handle: "sample", ToolVersion: "1.2.3", Files: 3, Failed: 1, SteamId64: "76561190000000000" } i && i.Groups.SequenceEqual(["profile", "hunts"]), "good zip: uid, handle, version, groups, counts");
        Check(r.Info!.Endpoints.SequenceEqual([("/e/one", 2, 0), ("/e/two", 1, 1)]), "good zip: files per endpoint");
        Check(BackupZip.Read(Zip(root, "nested.zip", z => { Add(z, "backup-42/manifest.json", Encoding.UTF8.GetBytes(Manifest(good.ToUpperInvariant()))); Add(z, "backup-42/claim.key", key); })).Info is not null, "zip with one folder level, upper-case hash");
        Check(BackupZip.Read(Zip(root, "wrong.zip", z => { Add(z, "manifest.json", Encoding.UTF8.GetBytes(Manifest(good))); Add(z, "claim.key", RandomNumberGenerator.GetBytes(32)); })).Error == BackupError.ClaimMismatch, "wrong claim key: claim mismatch");
        Check(BackupZip.Read(Zip(root, "nomanifest.zip", z => Add(z, "claim.key", key))).Error == BackupError.NoManifest, "no manifest.json");
        Check(BackupZip.Read(Zip(root, "nokey.zip", z => Add(z, "manifest.json", Encoding.UTF8.GetBytes(Manifest(good))))).Error == BackupError.NoClaimKey, "no claim.key");
        Check(BackupZip.Read(Zip(root, "badjson.zip", z => { Add(z, "manifest.json", "{ nope"u8.ToArray()); Add(z, "claim.key", key); })).Error == BackupError.BadManifest, "manifest that is not json");
        Check(BackupZip.Read(Zip(root, "nohash.zip", z => { Add(z, "manifest.json", "{}"u8.ToArray()); Add(z, "claim.key", key); })).Error == BackupError.BadManifest, "manifest without uid and claim_hash");
        var notZip = Path.Combine(root, "plain.zip");
        File.WriteAllText(notZip, "this is not a zip");
        Check(BackupZip.Read(notZip).Error == BackupError.NotZip, "not a zip");
        Check(BackupZip.Read(Path.Combine(root, "absent.zip")).Error == BackupError.Unreadable, "file that does not exist");
    }
}
