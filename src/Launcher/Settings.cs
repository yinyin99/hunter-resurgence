using System.Text.Json;

namespace Resurgence.Launcher;

/// <summary>
/// settings.json next to the exe: steam_app_id and client_exe (path of the game client relative to its install folder).
/// A missing file, a missing key or a placeholder (&lt;...&gt;) gives null: the feature that needs it says "not configured", the launcher still starts.
/// </summary>
public sealed record Settings(string? SteamAppId, string? ClientExe)
{
    // dotnet run has dotnet.exe as process: fall back to the base directory
    public static string FilePath => Path.Combine(Environment.ProcessPath is { } p && !Path.GetFileNameWithoutExtension(p).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
        ? Path.GetDirectoryName(p)! : AppContext.BaseDirectory, "settings.json");

    public bool Configured => SteamAppId is not null && ClientExe is not null;

    public static Settings Load(string? path = null)
    {
        Dictionary<string, string> d;
        try { d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path ?? FilePath)) ?? []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { d = []; }
        string? Get(string k) => d.TryGetValue(k, out var v) && v.Length > 0 && !v.Contains('<') ? v : null;
        var id = Get("steam_app_id");
        var exe = Get("client_exe");
        // both end up in file paths below the Steam library: no ids with letters, no absolute or parent-relative exe path
        return new(id is not null && id.All(char.IsAsciiDigit) ? id : null,
            exe is not null && !Path.IsPathRooted(exe) && !exe.Split('\\', '/').Contains("..") ? exe : null);
    }
}
