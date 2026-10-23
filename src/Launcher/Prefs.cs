using System.Text.Json;

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
    /// <summary>The Game settings were saved once: the first game start no longer asks for them.</summary>
    public bool GameSettingsSaved { get; set; }

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
