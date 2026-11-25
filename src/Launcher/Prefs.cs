using System.Text.Json;
using System.Text.Json.Serialization;
using Resurgence.Services;

namespace Resurgence.Launcher;

/// <summary>The player's choices in %APPDATA%\hunter-resurgence\launcher.json. A missing or damaged file means the defaults.</summary>
public sealed class Prefs
{
    static readonly JsonSerializerOptions J = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hunter-resurgence", "launcher.json");

    /// <summary>Language code (en, fr, de, es, pt-BR, ru); empty until the player chooses, then the Windows display language is used.</summary>
    public string Language { get; set; } = "";
    /// <summary>Install folder chosen by hand; wins over detection while the client is found in it.</summary>
    public string? GameFolder { get; set; }
    public bool CloseOnGameStart { get; set; }
    /// <summary>The Game settings' "Start the game using Steam" box: unchecked, every start goes without Steam (no <c>/steam</c>, Steam is neither started nor waited for).</summary>
    public bool StartWithSteam { get; set; } = true;
    /// <summary>The Game settings were saved once: the first game start no longer asks for them.</summary>
    public bool GameSettingsSaved { get; set; }
    /// <summary>The Game settings' display mode as stored (<c>borderless</c>, <c>fullscreen</c>, <c>windowed</c>): text, so a value this launcher does not know reads as no choice instead of damaging the whole file.</summary>
    [JsonPropertyName("display_mode")] public string? DisplayModeText { get; set; }
    /// <summary>The Game settings' display mode; null until chosen or until the first start (<see cref="GameSettingsService.Mode"/>). The game's own file only knows fullscreen on or off.</summary>
    [JsonIgnore]
    public DisplayMode? DisplayMode
    {
        get => Enum.TryParse<DisplayMode>(DisplayModeText, true, out var m) && Enum.IsDefined(m) ? m : null;
        set => DisplayModeText = value?.ToString().ToLowerInvariant();
    }
    /// <summary>The launch option line the launcher wrote into Steam, and what was there before (null: nothing). The uninstall puts it back only while the line is still there.</summary>
    public string? SteamOptionSet { get; set; }
    public string? SteamOptionBefore { get; set; }
    /// <summary>The localconfig.vdf that was written: the uninstall restores in that file, whoever is signed in to Steam then.</summary>
    public string? SteamOptionFile { get; set; }
    /// <summary>When the Steam dialog last opened by itself (at most once a day) and whether the player said "don't ask again".</summary>
    public DateTimeOffset? SteamOptionAsked { get; set; }
    public bool SteamOptionNever { get; set; }
    /// <summary>Problem reports: "unset" (never asked), "auto" (sent by itself after a failed start) or "ask" (only offered).</summary>
    public string ReportConsent { get; set; } = "unset";
    /// <summary>Where the launcher window was when it last closed (its normal size, and whether it was maximized); null: centred at the default size.</summary>
    public WindowPlace? Window { get; set; }

    public static Prefs Load(string path)
    {
        try { return JsonSerializer.Deserialize<Prefs>(File.ReadAllBytes(path), J) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".part";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(this, J));
        File.Move(tmp, path, true); // a crash never leaves a half file
    }
}

/// <summary>The launcher window's normal bounds in WPF units, and whether it was maximized.</summary>
public sealed record WindowPlace(double Left, double Top, double Width, double Height, bool Maximized)
{
    /// <summary>A real size, and at least 100 x 50 units of the window's top (its title bar) on the screen: a damaged value or a monitor that is gone never hides the launcher or stops it from starting.</summary>
    public bool OnScreen(System.Windows.Rect screen) =>
        double.IsFinite(Left + Top + Width + Height) && Width > 0 && Height > 0
        && Math.Min(Left + Width, screen.Right) - Math.Max(Left, screen.Left) >= 100 && Math.Min(Top + 50, screen.Bottom) - Math.Max(Top, screen.Top) >= 50;
}
