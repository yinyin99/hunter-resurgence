using Microsoft.Win32;
using Resurgence.Services;

namespace Resurgence.Launcher;

/// <summary>
/// The Game settings dialog on the game's real files (<see cref="GameFiles"/>). Nothing is written while the client runs. The client reads another graphics file with <c>/steam</c> than without
/// (<see cref="Settings.GraphicsFileNoSteam"/>): the file of the mode the game starts in is read and written, both when <paramref name="plannedMode"/> cannot tell
/// (<see cref="SteamProbes.PlannedMode"/>: "nosteam" or null). With <see cref="Prefs.StartWithSteam"/> off only the no-Steam file counts. The display mode is <see cref="Prefs.DisplayMode"/>: the file only knows fullscreen on or off, and Borderless writes it windowed at the size of
/// <paramref name="monitor"/> (the primary display's mode, where the game starts).
/// </summary>
public sealed class GameSettingsService(Settings settings, Prefs prefs, string appData, Action savePrefs, Func<string?>? plannedMode = null, Func<(int W, int H, int Hz)?>? monitor = null) : IGameSettingsService
{
    static readonly GameOptions None = new(DisplayMode.Borderless, false, new(0, 0, 0), 0, 0, 0, 0, 0, null, null);

    /// <summary>The graphics files the game may start with, the one of <c>/steam</c> first.</summary>
    IEnumerable<string> GraphicsPaths(bool withSteam) => settings.StartGraphicsFiles(withSteam ? plannedMode?.Invoke() : "nosteam").Select(f => Path.Combine(appData, f));

    string? OptionsPath => settings.OptionsFile is { } f && settings.OptionsKeyPrefix is not null ? Path.Combine(appData, f) : null;

    public Task<GameSettings> ReadAsync()
    {
        var modes = GameFiles.DisplayModes().Select(m => new Resolution(m.W, m.H, m.Hz)).ToList();
        var paths = GraphicsPaths(prefs.StartWithSteam).ToList();
        if (paths.Count == 0) return Task.FromResult(new GameSettings(None, modes, 0, "GameSettings.NotConfigured"));
        GraphicsSettings? g = null;
        foreach (var gp in paths)
        {
            // with two files the first that exists is shown (Save writes both)
            var (read, problem) = GameFiles.ReadGraphics(gp);
            if (read is null && problem != FileProblem.Missing) return Task.FromResult(new GameSettings(None, modes, 0, "GameSettings.BadFormat"));
            if ((g = read) is not null) break;
        }
        // before the player's choice a file that says fullscreen shows Borderless: the exclusive full screen only by choice
        var mode = Mode(g);
        g ??= GameFiles.DefaultGraphics();  // the game never writes this file: Save creates it
        // the options file is optional: without it the contextual help switch is simply not offered
        var o = OptionsPath is { } op ? GameFiles.ReadOptions(op, settings.OptionsKeyPrefix!).Value : null;
        var options = Native(ToOptions(g, o, mode)) with { StartWithSteam = prefs.StartWithSteam };
        if (!modes.Contains(options.Resolution)) modes = [.. modes.Append(options.Resolution).OrderBy(m => (m.Width, m.Height, m.Hz))];
        return Task.FromResult(new GameSettings(options, modes, VideoMemoryMb()));
    }

    /// <summary>The player's choice; before one, Windowed for a file that says windowed, else Borderless (no file yet, or a file that says fullscreen).</summary>
    internal DisplayMode Mode(GraphicsSettings? g) => prefs.DisplayMode ?? (g is { Fullscreen: false } ? DisplayMode.Windowed : DisplayMode.Borderless);

    /// <summary>Borderless runs at the monitor's size; the other modes keep the chosen resolution.</summary>
    GameOptions Native(GameOptions o) => o.Display == DisplayMode.Borderless && (monitor ?? GameFiles.CurrentDisplayMode)() is var (w, h, hz) ? o with { Resolution = new(w, h, hz) } : o;

    /// <summary>
    /// Before a start (<c>/steam</c> or not): the mode before the player's choice becomes his choice (a file written windowed below must not read as Windowed next time), and in Borderless the start's graphics file is
    /// written windowed at the monitor's size (created when missing). True when the game's window is to be made borderless (<see cref="Borderless"/>). A file that could not be written gives its error and false: the game
    /// then starts as that file says (perhaps in its exclusive full screen), which is left alone; the start goes on.
    /// </summary>
    public (bool Borderless, string? Error) PrepareStart(bool withSteam)
    {
        if ((withSteam ? settings.GraphicsFile : settings.GraphicsFileNoSteam ?? settings.GraphicsFile) is not { } file) return (false, null);
        var path = Path.Combine(appData, file);
        try
        {
            var (g, problem) = GameFiles.ReadGraphics(path);
            var mode = Mode(g);
            if (prefs.DisplayMode is null)
            {
                prefs.DisplayMode = mode;
                savePrefs();
            }
            if (mode != DisplayMode.Borderless) return (false, null);
            var b = g ?? GameFiles.DefaultGraphics();
            b = ToGraphics(b, Native(ToOptions(b, null, mode)));
            if (problem == FileProblem.Missing) GameFiles.CreateGraphics(path, b);
            else GameFiles.WriteGraphics(path, b);
            return (true, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return (false, e.Message); }
    }

    public Task<Result> SaveAsync(GameOptions options)
    {
        // the client reads the files once at start and rewrites them itself
        if (GameFiles.ClientRunning(settings.ClientExe)) return Task.FromResult(Result.Fail("GameSettings.ClientRunning"));
        options = Native(options);
        try
        {
            var paths = GraphicsPaths(options.StartWithSteam).ToList();
            if (paths.Count == 0) return Task.FromResult(Result.Fail("GameSettings.NotConfigured"));
            // every file the game may start with gets the choices, so they apply either way; each keeps what the dialog does not show
            var read = paths.Select(gp => (gp, GameFiles.ReadGraphics(gp))).ToList();
            if (read.Any(r => r.Item2.Value is null && r.Item2.Problem != FileProblem.Missing)) return Task.FromResult(Result.Fail("GameSettings.BadFormat"));
            foreach (var (gp, (g, _)) in read)
                if (g is null) GameFiles.CreateGraphics(gp, ToGraphics(GameFiles.DefaultGraphics(), options));
                else GameFiles.WriteGraphics(gp, ToGraphics(g, options));
            if (OptionsPath is { } op && GameFiles.ReadOptions(op, settings.OptionsKeyPrefix!).Value is { } o && options.ContextualHelp is { } help && o.Has("ContextualHelp"))
            {
                o.Set("ContextualHelp", help ? 1 : 0);
                GameFiles.WriteOptions(op, settings.OptionsKeyPrefix!, o);
            }
            prefs.GameSettingsSaved = true;
            prefs.DisplayMode = options.Display;
            prefs.StartWithSteam = options.StartWithSteam;
            savePrefs();
            return Task.FromResult(Result.Success);
        }
        catch (InvalidDataException) { return Task.FromResult(Result.Fail("GameSettings.BadFormat")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Task.FromResult(Result.Fail("GameSettings.SaveFailed")); }
    }

    public Task<bool> FirstStartAsync() => Task.FromResult(!prefs.GameSettingsSaved);

    public Task<string> LanguageAsync() =>
        Task.FromResult(GameLanguages.Has(prefs.GameLanguage) ? prefs.GameLanguage! : GameLanguages.For(prefs.Language.Length > 0 ? prefs.Language : System.Globalization.CultureInfo.CurrentUICulture.Name));

    public Task SetLanguageAsync(string code)
    {
        prefs.GameLanguage = GameLanguages.Has(code) ? code : null;
        savePrefs();
        return Task.CompletedTask;
    }

    public Task<string?> HostLanguageAsync() => Task.FromResult(Resurgence.Ui.Languages.Find(prefs.HostLanguage)?.Code);

    public Task SetHostLanguageAsync(string code)
    {
        prefs.HostLanguage = Resurgence.Ui.Languages.Find(code)?.Code;
        savePrefs();
        return Task.CompletedTask;
    }

    // the file's anisotropic filtering has Default before Off: the dialog has no Default, it shows Off
    public static GameOptions ToOptions(GraphicsSettings g, OptionsSettings? o, DisplayMode display) =>
        new(display, g.Vsync, new(g.Width, g.Height, g.Hz), g.Scene, g.Texture, g.Shadow, g.Msaa, Math.Max(g.Aniso - 1, 0), o?.Has("ContextualHelp") == true ? o.Get("ContextualHelp") != 0 : null, g.ExtendedAudio is { } a ? a != 0 : null);

    /// <summary>Only Fullscreen writes the file's fullscreen flag; Borderless runs windowed. Untouched anisotropic filtering stays as the file has it (Default is not turned into Off).</summary>
    public static GraphicsSettings ToGraphics(GraphicsSettings g, GameOptions o) => g with
    {
        Fullscreen = o.Display == DisplayMode.Fullscreen,
        Vsync = o.Vsync,
        Width = o.Resolution.Width,
        Height = o.Resolution.Height,
        Hz = o.Resolution.Hz,
        Scene = o.Scene,
        Texture = o.Texture,
        Shadow = o.Shadow,
        Msaa = o.Msaa,
        Aniso = o.Aniso == Math.Max(g.Aniso - 1, 0) ? g.Aniso : o.Aniso + 1,
        // only a real on/off change is written: a file holding 2 keeps 2 while the switch stays on
        ExtendedAudio = o.ExtendedAudio is { } on && g.ExtendedAudio is { } a && (a != 0) != on ? (on ? 1 : 0) : g.ExtendedAudio,
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
