using System.Collections;
using System.Globalization;
using System.IO.Compression;
using System.Resources;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

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
            Translations();
            GameSettingsFiles(root);
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
        Check(Prefs.Load(path).Language == "", "no launcher.json: defaults");
        new Prefs { Language = "de", GameFolder = @"C:\x y", CloseOnGameStart = true }.Save(path);
        var p = Prefs.Load(path);
        Check(p is { Language: "de", GameFolder: @"C:\x y", CloseOnGameStart: true }, "launcher.json round trip");
        File.WriteAllText(path, "{ not json");
        Check(Prefs.Load(path).Language == "", "damaged launcher.json: defaults");
        Check(App.Pick("de", "fr") == "de" && App.Pick("", "fr") == "fr" && App.Pick("xx", "ru") == "ru" && App.Pick("", "ja") == "en" && App.Pick(null, "pt") == "pt", "language: saved choice, else Windows language, else English");
    }

    /// <summary>Every language resx has exactly the keys of Strings.resx and the same {0}/{1}... placeholders in each string (a mismatch is a FormatException at runtime).</summary>
    static void Translations()
    {
        var rm = new ResourceManager("Resurgence.Ui.Strings", typeof(Resurgence.Ui.Strings).Assembly);
        Dictionary<string, string> Read(CultureInfo c) =>
            (rm.GetResourceSet(c, true, false) ?? throw new Exception($"FAIL no resources for '{c.Name}'")).Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
        static string Holes(string s) => string.Join(",", Regex.Matches(s, @"\{(\d+)").Select(m => m.Groups[1].Value).Order());
        var en = Read(CultureInfo.InvariantCulture);
        var bad = new List<string>();
        foreach (var (_, culture) in App.Languages.Where(l => l.Code != "en"))
        {
            var tr = Read(CultureInfo.GetCultureInfo(culture));
            bad.AddRange(en.Keys.Except(tr.Keys).Select(k => $"{culture}: missing {k}"));
            bad.AddRange(tr.Keys.Except(en.Keys).Select(k => $"{culture}: extra {k}"));
            foreach (var (k, v) in tr.Where(p => en.ContainsKey(p.Key)))
            {
                if (Holes(v) != Holes(en[k])) bad.Add($"{culture}: {k} has placeholders {{{Holes(v)}}}, English has {{{Holes(en[k])}}}");
                try { string.Format(v, "", "", "", ""); } catch (FormatException) { bad.Add($"{culture}: {k} is not a valid format string"); }
            }
        }
        if (bad.Count > 0) throw new Exception("FAIL translations: " + string.Join("; ", bad));
        Check(true, $"{App.Languages.Length - 1} translations have the {en.Count} keys and placeholders of Strings.resx");
    }

    static string Bytes(string path) => System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(path));

    static void GameSettingsFiles(string root)
    {
        var s = Settings.Load(Path.Combine(root, "gs.json").Also(p => File.WriteAllText(p, """{"graphics_file":"g.cfg","options_file":"sub\\o.bin","options_key_prefix":"fake_"}""")));
        Check(s is { GraphicsFile: "g.cfg", OptionsFile: @"sub\o.bin", OptionsKeyPrefix: "fake_" }, "settings.json: graphics file, options file and key prefix");
        Check(Settings.Load(Path.Combine(root, "bad.json")) is { GraphicsFile: null, OptionsFile: null, OptionsKeyPrefix: null }, "placeholders: those parts are not configured");
        Check(!GameFiles.ClientRunning(null) && !GameFiles.ClientRunning("no-such-client.exe"), "client not running: false");
        Check(GameFiles.ClientRunning(Path.GetFileName(Environment.ProcessPath)), "a running process of that name: true");

        // graphics file: odd spacing on the mode line, unknown lines 10+, CRLF
        var g = Path.Combine(root, "gfx", "g.cfg");
        const string gfx = "129\r\n0\r\n1920  1080   60\r\n2\r\n0\r\n0\r\n0\r\n0\r\n0\r\n5 6 7\r\nnote  line\r\n\r\n";
        Write(g, gfx);
        var (gs, problem) = GameFiles.ReadGraphics(g);
        Check(problem == FileProblem.None && gs is { Fullscreen: false, Width: 1920, Height: 1080, Hz: 60, Scene: 2, Texture: 0, Msaa: 0, Aniso: 0, Vsync: false }, "graphics file read");
        gs!.Scene = 3; gs.Width = 1280; gs.Vsync = true;
        GameFiles.WriteGraphics(g, gs);
        Check(Bytes(g) == gfx.Replace("1920  1080   60", "1280  1080   60").Replace("\r\n2\r\n", "\r\n3\r\n").Replace("0\r\n5 6 7", "1\r\n5 6 7"), "graphics write changes only the edited values and keeps spacing, line ends and lines 10+");
        Check(Bytes(g + GameFiles.BackupSuffix) == gfx, "first write made a copy of the original");
        gs.Scene = 0;
        GameFiles.WriteGraphics(g, gs);
        Check(Bytes(g + GameFiles.BackupSuffix) == gfx && !File.Exists(g + ".resurgence-tmp"), "later writes never overwrite the copy and leave no temp file");
        var stamp = File.GetLastWriteTimeUtc(g);
        Thread.Sleep(30);
        GameFiles.WriteGraphics(g, GameFiles.ReadGraphics(g).Value!);
        Check(File.GetLastWriteTimeUtc(g) == stamp, "writing unchanged values touches nothing");

        var bad = Path.Combine(root, "gfx", "bad.cfg");
        const string badText = "130\r\n0\r\n1920 1080 60\r\n2\r\n0\r\n0\r\n0\r\n0\r\n0\r\n";
        Write(bad, badText);
        Check(GameFiles.ReadGraphics(bad).Problem == FileProblem.BadFormat, "wrong format marker: not read");
        var refused = false;
        try { GameFiles.WriteGraphics(bad, gs); } catch (InvalidDataException) { refused = true; }
        Check(refused && Bytes(bad) == badText && !File.Exists(bad + GameFiles.BackupSuffix), "wrong format marker: write refused, file and folder untouched");
        Check(GameFiles.ReadGraphics(Path.Combine(root, "gfx", "absent.cfg")).Problem == FileProblem.Missing, "missing graphics file");

        // options file: a key map object and a same-named key under another prefix must stay as they are
        var o = Path.Combine(root, "opt", "o.bin");
        const string opt = "<value name=\"fake_Version\" type=\"int\">9</value>\n<value name=\"fake_MasterVolume\" type=\"int\">100</value>\n<value name=\"other_Gamma\" type=\"int\">50</value>\n" +
            "<value name=\"fake_Gamma\" type=\"int\">50</value>\n<value name=\"fake_ShowChat\" type=\"int\">1</value>\n<object name=\"Bindings\">\n  <value name=\"Jump\" type=\"int\">32</value>\n</object>\n";
        Write(o, opt);
        var (os, oproblem) = GameFiles.ReadOptions(o, "fake_");
        Check(oproblem == FileProblem.None && os is { } && os.Get("MasterVolume") == 100 && os.Get("Gamma") == 50 && os.Get("ShowChat") == 1 && !os.Has("LookSpeed") && !os.Has("Version"), "options read: only known keys that exist");
        os!.Set("MasterVolume", 99); os.Set("ShowChat", 0); os.Set("LookSpeed", 7);
        GameFiles.WriteOptions(o, "fake_", os);
        Check(Bytes(o) == opt.Replace("fake_MasterVolume\" type=\"int\">100", "fake_MasterVolume\" type=\"int\">99").Replace("fake_ShowChat\" type=\"int\">1", "fake_ShowChat\" type=\"int\">0"), "options write changes only the targeted value lines, adds none, keeps the key map");
        Check(Bytes(o + GameFiles.BackupSuffix) == opt, "options: first write made a copy of the original");
        os.Set("Gamma", 10);
        GameFiles.WriteOptions(o, "fake_", os);
        Check(Bytes(o + GameFiles.BackupSuffix) == opt, "options: the copy is not overwritten");
        Check(GameFiles.ReadOptions(o, "zz_").Problem == FileProblem.BadFormat && GameFiles.ReadOptions(Path.Combine(root, "opt", "absent.bin"), "fake_").Problem == FileProblem.Missing, "options: wrong prefix is a bad format, absent file is missing");
    }

    static string Also(this string path, Action<string> act)
    {
        act(path);
        return path;
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
