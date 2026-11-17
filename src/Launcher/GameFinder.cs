using System.Text.RegularExpressions;

namespace Resurgence.Launcher;

/// <summary>An install folder in which the client exe exists, and the full path of that exe.</summary>
public sealed record GameInstall(string Folder, string ClientExe);

/// <summary>The install in use (null when none), how it was found and the other install folders seen.</summary>
public sealed record GameSearch(GameInstall? Install, string? Source, IReadOnlyList<string> Others);

/// <summary>Finds the game's install folder. Read-only: nothing is ever written to it.</summary>
public static partial class GameFinder
{
    /// <summary>Order: the folder chosen by hand, the Steam launch-options command, then Steam's own library data.</summary>
    public static GameInstall? Find(Settings s, string? chosenFolder, IEnumerable<string> args, string? steamPath) => Search(s, chosenFolder, args, steamPath).Install;

    /// <summary>
    /// The install in use, how it was found (manual, steam-option, steam-library) and the other install folders seen (each once, existing only). Source is null when none was found,
    /// and also when a folder chosen by hand no longer holds the client: the automatic install is used then.
    /// </summary>
    public static GameSearch Search(Settings s, string? chosenFolder, IEnumerable<string> args, string? steamPath)
    {
        if (!s.Configured) return new(null, null, []);
        var seen = new List<(GameInstall Game, string Source)>();
        if (Check(s, chosenFolder) is { } m) seen.Add((m, "manual"));
        seen.AddRange(FromArgs(s, args).Select(g => (g, "steam-option")));
        seen.AddRange(FromSteam(s, steamPath).Select(g => (g, "steam-library")));
        if (seen.Count == 0) return new(null, null, []);
        var (first, source) = seen[0];
        var stale = !string.IsNullOrWhiteSpace(chosenFolder) && source != "manual";
        var key = (GameInstall g) => Path.TrimEndingDirectorySeparator(g.Folder);
        var others = seen.Select(x => x.Game).Where(g => !key(g).Equals(key(first), StringComparison.OrdinalIgnoreCase))
            .DistinctBy(key, StringComparer.OrdinalIgnoreCase).Select(g => g.Folder).ToList();
        return new(first, stale ? null : source, others);
    }

    /// <summary>The folder counts only when the client exe exists in it.</summary>
    public static GameInstall? Check(Settings s, string? folder)
    {
        if (s.ClientExe is null || string.IsNullOrWhiteSpace(folder)) return null;
        var exe = Path.Combine(folder, s.ClientExe);
        return File.Exists(exe) ? new(Path.GetFullPath(folder), Path.GetFullPath(exe)) : null;
    }

    /// <summary>The install folder for a folder inside it (the install root itself, or the exe's folder, or deeper), null when no folder up the path holds the client.</summary>
    public static GameInstall? FromDir(Settings s, string? dir)
    {
        for (; !string.IsNullOrWhiteSpace(dir); dir = Path.GetDirectoryName(dir))
            if (Check(s, dir) is { } g) return g;
        return null;
    }

    // Steam started us with launch options `"<our exe>" %command%`: %command% is an exe inside the install folder, possibly in a subfolder
    static IEnumerable<GameInstall> FromArgs(Settings s, IEnumerable<string> args) =>
        args.Where(a => a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && Path.IsPathRooted(a) && File.Exists(a)).Select(a => FromDir(s, Path.GetDirectoryName(a))).OfType<GameInstall>();

    static IEnumerable<GameInstall> FromSteam(Settings s, string? steamPath)
    {
        if (string.IsNullOrWhiteSpace(steamPath)) yield break;
        foreach (var lib in Libraries(steamPath))
        {
            var manifest = Path.Combine(lib, "steamapps", $"appmanifest_{s.SteamAppId}.acf");
            if (!File.Exists(manifest)) continue;
            var dir = VdfValue(File.ReadAllText(manifest), "installdir");
            if (dir is null || dir.Split('\\', '/').Contains("..") || Path.IsPathRooted(dir)) continue;
            if (Check(s, Path.Combine(lib, "steamapps", "common", dir)) is { } g) yield return g;
        }
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
