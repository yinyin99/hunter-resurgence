using System.Text.Json;

namespace Resurgence.Launcher;

/// <summary>
/// settings.json next to the exe: steam_app_id and client_exe (path of the game client relative to its install folder), and for Game settings
/// graphics_file, options_file (names relative to %APPDATA%) and options_key_prefix (what precedes each key name in the options file).
/// A missing file, a missing key or a placeholder (&lt;...&gt;) gives null: the feature that needs it says "not configured", the launcher still starts.
/// </summary>
public sealed record Settings(string? SteamAppId, string? ClientExe, string? GraphicsFile = null, string? OptionsFile = null, string? OptionsKeyPrefix = null)
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
        // all of these end up in file paths: no ids with letters, no absolute or parent-relative paths
        string? Relative(string k) => Get(k) is { } v && !Path.IsPathRooted(v) && !v.Split('\\', '/').Contains("..") ? v : null;
        var id = Get("steam_app_id");
        return new(id is not null && id.All(char.IsAsciiDigit) ? id : null, Relative("client_exe"), Relative("graphics_file"), Relative("options_file"), Get("options_key_prefix"));
    }
}
