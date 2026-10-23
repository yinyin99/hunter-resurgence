using System.Text.RegularExpressions;

namespace Resurgence.Launcher;

/// <summary>An install folder in which the client exe exists, and the full path of that exe.</summary>
public sealed record GameInstall(string Folder, string ClientExe);

/// <summary>Finds the game's install folder. Read-only: nothing is ever written to it.</summary>
public static partial class GameFinder
{
    /// <summary>Order: the folder chosen by hand, the Steam launch-options command, then Steam's own library data.</summary>
    public static GameInstall? Find(Settings s, string? chosenFolder, IEnumerable<string> args, string? steamPath)
    {
        if (!s.Configured) return null;
        return Check(s, chosenFolder) ?? FromArgs(s, args) ?? FromSteam(s, steamPath);
    }

    /// <summary>The folder counts only when the client exe exists in it.</summary>
    public static GameInstall? Check(Settings s, string? folder)
    {
        if (s.ClientExe is null || string.IsNullOrWhiteSpace(folder)) return null;
        var exe = Path.Combine(folder, s.ClientExe);
        return File.Exists(exe) ? new(Path.GetFullPath(folder), Path.GetFullPath(exe)) : null;
    }

    // Steam started us with launch options `"<our exe>" %command%`: %command% is an exe inside the install folder, possibly in a subfolder
    static GameInstall? FromArgs(Settings s, IEnumerable<string> args)
    {
        foreach (var a in args.Where(a => a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && Path.IsPathRooted(a) && File.Exists(a)))
            for (var dir = Path.GetDirectoryName(a); dir is not null; dir = Path.GetDirectoryName(dir))
                if (Check(s, dir) is { } g) return g;
        return null;
    }

    static GameInstall? FromSteam(Settings s, string? steamPath)
    {
        if (string.IsNullOrWhiteSpace(steamPath)) return null;
        foreach (var lib in Libraries(steamPath))
        {
            var manifest = Path.Combine(lib, "steamapps", $"appmanifest_{s.SteamAppId}.acf");
            if (!File.Exists(manifest)) continue;
            var dir = VdfValue(File.ReadAllText(manifest), "installdir");
            if (dir is null || dir.Split('\\', '/').Contains("..") || Path.IsPathRooted(dir)) continue;
            if (Check(s, Path.Combine(lib, "steamapps", "common", dir)) is { } g) return g;
        }
        return null;
    }

    /// <summary>Every "path" in steamapps\libraryfolders.vdf plus the Steam folder itself, each once (the vdf spelling of a path wins over the registry's lower-case one).</summary>
    public static IEnumerable<string> Libraries(string steamPath) => Raw(steamPath).Distinct(StringComparer.OrdinalIgnoreCase);

    static IEnumerable<string> Raw(string steamPath)
    {
        var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in VdfPair().Matches(File.ReadAllText(vdf)))
                if (m.Groups[1].Value == "path" && Path.IsPathRooted(Unescape(m.Groups[2].Value))) yield return Path.GetFullPath(Unescape(m.Groups[2].Value));
        yield return Path.GetFullPath(steamPath);
    }

    public static string? VdfValue(string text, string key) =>
        VdfPair().Matches(text).FirstOrDefault(m => m.Groups[1].Value == key) is { } m ? Unescape(m.Groups[2].Value) : null;

    static string Unescape(string v) => v.Replace(@"\\", @"\");

    [GeneratedRegex("\"([^\"]+)\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex VdfPair();
}
