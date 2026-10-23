using Microsoft.Win32;
using Resurgence.Services;

namespace Resurgence.Launcher;

/// <summary>The Game settings dialog on the game's real files (<see cref="GameFiles"/>). Nothing is written while the client runs.</summary>
public sealed class GameSettingsService(Settings settings, Prefs prefs, string appData, Action savePrefs) : IGameSettingsService
{
    static readonly GameOptions None = new(false, false, new(0, 0, 0), 0, 0, 0, 0, 0, null, null);

    string? GraphicsPath => settings.GraphicsFile is { } f ? Path.Combine(appData, f) : null;
    string? OptionsPath => settings.OptionsFile is { } f && settings.OptionsKeyPrefix is not null ? Path.Combine(appData, f) : null;

    public Task<GameSettings> ReadAsync()
    {
        var modes = GameFiles.DisplayModes().Select(m => new Resolution(m.W, m.H, m.Hz)).ToList();
        if (GraphicsPath is not { } gp) return Task.FromResult(new GameSettings(None, modes, 0, "GameSettings.NotConfigured"));
        var (g, problem) = GameFiles.ReadGraphics(gp);
        if (g is null) return Task.FromResult(new GameSettings(None, modes, 0, problem == FileProblem.Missing ? "GameSettings.Missing" : "GameSettings.BadFormat"));
        // the options file is optional: without it the contextual help switch is simply not offered
        var o = OptionsPath is { } op ? GameFiles.ReadOptions(op, settings.OptionsKeyPrefix!).Value : null;
        var current = new Resolution(g.Width, g.Height, g.Hz);
        if (!modes.Contains(current)) modes = [.. modes.Append(current).OrderBy(m => (m.Width, m.Height, m.Hz))];
        return Task.FromResult(new GameSettings(ToOptions(g, o), modes, VideoMemoryMb()));
    }

    public Task<Result> SaveAsync(GameOptions options)
    {
        // the client reads the files once at start and rewrites them itself
        if (GameFiles.ClientRunning(settings.ClientExe)) return Task.FromResult(Result.Fail("GameSettings.ClientRunning"));
        try
        {
            if (GraphicsPath is not { } gp || GameFiles.ReadGraphics(gp).Value is not { } g) return Task.FromResult(Result.Fail("GameSettings.Missing"));
            GameFiles.WriteGraphics(gp, ToGraphics(g, options));
            if (OptionsPath is { } op && GameFiles.ReadOptions(op, settings.OptionsKeyPrefix!).Value is { } o && options.ContextualHelp is { } help && o.Has("ContextualHelp"))
            {
                o.Set("ContextualHelp", help ? 1 : 0);
                GameFiles.WriteOptions(op, settings.OptionsKeyPrefix!, o);
            }
            prefs.GameSettingsSaved = true;
            savePrefs();
            return Task.FromResult(Result.Success);
        }
        catch (InvalidDataException) { return Task.FromResult(Result.Fail("GameSettings.BadFormat")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Task.FromResult(Result.Fail("GameSettings.SaveFailed")); }
    }

    public Task<bool> FirstStartAsync() => Task.FromResult(!prefs.GameSettingsSaved);

    // the file's anisotropic filtering has Default before Off: the dialog has no Default, it shows Off
    public static GameOptions ToOptions(GraphicsSettings g, OptionsSettings? o) =>
        new(g.Fullscreen, g.Vsync, new(g.Width, g.Height, g.Hz), g.Scene, g.Texture, g.Shadow, g.Msaa, Math.Max(g.Aniso - 1, 0), o?.Has("ContextualHelp") == true ? o.Get("ContextualHelp") != 0 : null, null);

    /// <summary>Untouched anisotropic filtering stays as the file has it (Default is not turned into Off).</summary>
    public static GraphicsSettings ToGraphics(GraphicsSettings g, GameOptions o) => g with
    {
        Fullscreen = o.Fullscreen, Vsync = o.Vsync, Width = o.Resolution.Width, Height = o.Resolution.Height, Hz = o.Resolution.Hz,
        Scene = o.Scene, Texture = o.Texture, Shadow = o.Shadow, Msaa = o.Msaa, Aniso = o.Aniso == Math.Max(g.Aniso - 1, 0) ? g.Aniso : o.Aniso + 1,
    };

    /// <summary>Dedicated video memory of the biggest display adapter, from the display driver's registry entries; 0 when unknown.</summary>
    static int VideoMemoryMb()
    {
        long best = 0;
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            foreach (var name in cls?.GetSubKeyNames() ?? [])
            {
                using var key = cls!.OpenSubKey(name);
                best = Math.Max(best, key?.GetValue("HardwareInformation.qwMemorySize") switch { long l => l, byte[] { Length: 8 } b => BitConverter.ToInt64(b), _ => 0 });
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return (int)(best / (1024 * 1024));
    }
}
