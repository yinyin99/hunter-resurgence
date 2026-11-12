using Resurgence.Launcher;

namespace Resurgence.Tests.LauncherApp;

public class SteamOptionTests
{
    const string Id = "4242", Line = "\"C:\\Apps\\Launcher\\launcher.exe\" %command%";

    // Steam's own layout: tabs, CRLF, other apps with options of their own
    static string Vdf(string? ours) => string.Join("\r\n",
        "\"UserLocalConfigStore\"", "{", "\t\"Software\"", "\t{", "\t\t\"Valve\"", "\t\t{", "\t\t\t\"Steam\"", "\t\t\t{", "\t\t\t\t\"apps\"", "\t\t\t\t{",
        "\t\t\t\t\t\"111\"", "\t\t\t\t\t{", "\t\t\t\t\t\t\"LaunchOptions\"\t\t\"-other \\\"x\\\"\"", "\t\t\t\t\t}",
        ours is null ? "" : $"\t\t\t\t\t\"{Id}\"\r\n\t\t\t\t\t{{\r\n\t\t\t\t\t\t\"Playtime\"\t\t\"5\"\r\n{ours}\t\t\t\t\t}}",
        "\t\t\t\t}", "\t\t\t}", "\t\t}", "\t}", "\t\"WebStorage\"", "\t{", "\t\t\"x\"\t\t\"y\"", "\t}", "}", "").Replace("\r\n\r\n", "\r\n");

    static string Pair(string v) => $"\t\t\t\t\t\t\"LaunchOptions\"\t\t\"{v.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"\r\n";

    static (string Vdf, Prefs Prefs, Action Save, string PrefsPath) Setup(TempDir temp, string text)
    {
        var vdf = Path.Combine(temp.Path, "localconfig.vdf");
        File.WriteAllText(vdf, text);
        var prefsPath = Path.Combine(temp.Path, "launcher.json");
        var prefs = new Prefs();
        return (vdf, prefs, () => prefs.Save(prefsPath), prefsPath);
    }

    [Fact]
    public void WritesOneValueAndKeepsEveryOtherByteAndTheBackup()
    {
        using var temp = new TempDir("resurgence-test-");
        var original = Vdf(Pair("-mine"));
        var (vdf, prefs, save, _) = Setup(temp, original);
        SteamOption.Set(vdf, Id, Line, prefs, save);
        Assert.Equal(original.Replace(Pair("-mine"), Pair(Line)), File.ReadAllText(vdf)); // the player's other app (111), the other keys and the line ends are as they were
        Assert.Equal(Line, SteamOption.Current(vdf, Id));
        Assert.Equal(original, File.ReadAllText(vdf + ".launcher-backup"));
        Assert.Equal("-mine", prefs.SteamOptionBefore);
        // a second write changes nothing and keeps the first backup
        var after = File.ReadAllText(vdf);
        SteamOption.Set(vdf, Id, Line, prefs, save);
        Assert.Equal(after, File.ReadAllText(vdf));
        Assert.Equal(original, File.ReadAllText(vdf + ".launcher-backup"));
    }

    [Fact]
    public void InsertsTheKeyWhenAbsentAndTheAppBlockWhenMissing()
    {
        using var temp = new TempDir("resurgence-test-");
        var original = Vdf("");
        var (vdf, prefs, save, _) = Setup(temp, original);
        SteamOption.Set(vdf, Id, Line, prefs, save);
        Assert.Equal(original.Replace("\t\t\t\t\t}\r\n\t\t\t\t}", Pair(Line) + "\t\t\t\t\t}\r\n\t\t\t\t}"), File.ReadAllText(vdf));
        Assert.Null(prefs.SteamOptionBefore);

        var (vdf2, prefs2, save2, _) = Setup(temp, Vdf(null));
        SteamOption.Set(vdf2, Id, Line, prefs2, save2);
        Assert.Equal(Line, SteamOption.Current(vdf2, Id));
        Assert.Equal("-other \"x\"", SteamOption.Current(vdf2, "111"));
    }

    [Fact]
    public void RestoresThePreviousValueOrRemovesOursOnlyWhileItIsStillOurs()
    {
        using var temp = new TempDir("resurgence-test-");
        var withOld = Vdf(Pair("-mine"));
        var (vdf, prefs, save, _) = Setup(temp, withOld);
        SteamOption.Set(vdf, Id, Line, prefs, save);
        SteamOption.Restore(Id, prefs, save);
        Assert.Equal(withOld, File.ReadAllText(vdf));
        Assert.Null(prefs.SteamOptionSet);

        var none = Vdf("");
        var (vdf2, prefs2, save2, _) = Setup(temp, none);
        SteamOption.Set(vdf2, Id, Line, prefs2, save2);
        SteamOption.Restore(Id, prefs2, save2);
        Assert.Equal(none, File.ReadAllText(vdf2)); // ours removed again, byte for byte

        // Steam or the player changed it meanwhile: left as it is
        var (vdf3, prefs3, save3, _) = Setup(temp, none);
        SteamOption.Set(vdf3, Id, Line, prefs3, save3);
        File.WriteAllText(vdf3, File.ReadAllText(vdf3).Replace(Line.Replace("\\", "\\\\").Replace("\"", "\\\""), "-theirs"));
        var changed = File.ReadAllText(vdf3);
        SteamOption.Restore(Id, prefs3, save3);
        Assert.Equal(changed, File.ReadAllText(vdf3));
    }

    [Fact]
    public void UninstallRestoresFromTheSavedNote()
    {
        using var temp = new TempDir("resurgence-test-");
        var steam = Path.Combine(temp.Path, "Steam");
        var vdf = Path.Combine(steam, "userdata", "7", "config", "localconfig.vdf");
        Directory.CreateDirectory(Path.GetDirectoryName(vdf)!);
        var original = Vdf(Pair("-mine"));
        File.WriteAllText(vdf, original);
        var prefsPath = Path.Combine(temp.Path, "launcher.json");
        var prefs = new Prefs();
        SteamOption.Set(vdf, Id, Line, prefs, () => prefs.Save(prefsPath));
        SteamOption.Uninstall(prefsPath, new Settings(Id, "x.exe"), () => false, () => false)!();
        Assert.Equal(original, File.ReadAllText(vdf));
        Assert.Null(Prefs.Load(prefsPath).SteamOptionSet);
        Assert.Equal(vdf, Steam.LocalConfig(steam, 0));
        // a running Steam that is not closed: the option stays, and so does the note
        SteamOption.Set(vdf, Id, Line, prefs, () => prefs.Save(prefsPath));
        SteamOption.Uninstall(prefsPath, new Settings(Id, "x.exe"), () => true, () => false)!();
        Assert.Equal(Line, SteamOption.Current(vdf, Id));
        Assert.NotNull(Prefs.Load(prefsPath).SteamOptionSet);
    }

    [Theory]
    [InlineData("not vdf at all")]
    [InlineData("\"UserLocalConfigStore\"\r\n{\r\n\t\"Software\"\r\n")]
    [InlineData("\"a\" \"b")]
    [InlineData("\"a\" { } }")]
    [InlineData("\"UserLocalConfigStore\" { \"Software\" \"x\" }")] // well-formed, but no apps block: nothing to edit
    public void AFileItCannotParseIsLeftAlone(string text)
    {
        using var temp = new TempDir("resurgence-test-");
        var (vdf, prefs, save, prefsPath) = Setup(temp, text);
        Assert.Throws<FormatException>(() => SteamOption.Set(vdf, Id, Line, prefs, save));
        Assert.Equal(text, File.ReadAllText(vdf));
        Assert.False(File.Exists(vdf + ".launcher-backup"));
        Assert.False(File.Exists(prefsPath));
    }

    [Fact]
    public void InvalidUtf8IsRefused()
    {
        using var temp = new TempDir("resurgence-test-");
        var (vdf, prefs, save, _) = Setup(temp, "");
        var bytes = new byte[] { 34, 255, 34 };
        File.WriteAllBytes(vdf, bytes);
        Assert.Throws<FormatException>(() => SteamOption.Set(vdf, Id, Line, prefs, save));
        Assert.Equal(bytes, File.ReadAllBytes(vdf));
    }
}
