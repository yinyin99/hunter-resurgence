using System.Text;
using Resurgence.Services;
using Velopack.Locators;

namespace Resurgence.Launcher;

/// <summary>The game's Steam launch option: the line, the write into <c>localconfig.vdf</c> (docs/launcher.md#start-it-from-steam) and the restore on uninstall. Steam must be closed while these run.</summary>
static class SteamOption
{
    const string BackupSuffix = ".launcher-backup";
    static readonly UTF8Encoding Utf8 = new(false, true); // invalid bytes would be replaced and so changed: refuse them

    public static string Line(string exe) => $"\"{exe}\" %command%";

    /// <summary>The exe at the root of the Velopack install folder (it stays when updates replace <c>current\</c>); null when this launcher was not installed by the Setup.</summary>
    public static string? StableExe() =>
        VelopackLocator.IsCurrentSet && VelopackLocator.Current is { CurrentlyInstalledVersion: not null, RootAppDir: { } root } && Environment.ProcessPath is { } p
            && File.Exists(Path.Combine(root, Path.GetFileName(p))) ? Path.Combine(root, Path.GetFileName(p)) : null;

    static string Read(string vdf)
    {
        try { return Utf8.GetString(File.ReadAllBytes(vdf)); }
        catch (DecoderFallbackException e) { throw new FormatException(e.Message, e); }
    }

    static void Write(string vdf, string text)
    {
        var tmp = vdf + ".part";
        File.WriteAllBytes(tmp, Utf8.GetBytes(text));
        File.Move(tmp, vdf, true);
    }

    /// <summary>The option of <paramref name="appId"/> in <paramref name="vdf"/>; FormatException when the file cannot be parsed.</summary>
    public static string? Current(string vdf, string appId) => SteamVdf.Get(Read(vdf), appId);

    /// <summary>Sets the option to <paramref name="line"/>: the file is parsed and edited in memory first (an unparseable file is not touched), a copy of the original is kept next to it before the first write,
    /// and what was there is noted in <paramref name="prefs"/> (saved by <paramref name="save"/> before the write) unless it was our line.</summary>
    public static void Set(string vdf, string appId, string line, Prefs prefs, Action save)
    {
        var text = Read(vdf);
        var current = SteamVdf.Get(text, appId);
        if (current == line) return;
        var edited = SteamVdf.Set(text, appId, line);
        if (current != prefs.SteamOptionSet) prefs.SteamOptionBefore = current;
        prefs.SteamOptionSet = line;
        save();
        if (!File.Exists(vdf + BackupSuffix)) File.Copy(vdf, vdf + BackupSuffix);
        Write(vdf, edited);
    }

    /// <summary>Puts the previous option back (or removes ours) only while the option still is the line we wrote; either way the note in <paramref name="prefs"/> is cleared.</summary>
    public static void Restore(string vdf, string appId, Prefs prefs, Action save)
    {
        if (prefs.SteamOptionSet is not { } ours) return;
        var text = Read(vdf);
        if (SteamVdf.Get(text, appId) == ours) Write(vdf, SteamVdf.Set(text, appId, prefs.SteamOptionBefore));
        (prefs.SteamOptionSet, prefs.SteamOptionBefore) = (null, null);
        save();
    }

    /// <summary>Velopack's uninstall step. Steam saves its own copy when it exits, so a running Steam (<paramref name="steamRunning"/>) is closed first by <paramref name="closeSteam"/> (true: it is closed); without that the option stays.</summary>
    public static void Uninstall(string prefsPath, Settings settings, string? steamPath, uint account, bool steamRunning, Func<bool> closeSteam)
    {
        var prefs = Prefs.Load(prefsPath);
        if (prefs.SteamOptionSet is null || settings.SteamAppId is not { } id || Steam.LocalConfig(steamPath, account) is not { } vdf) return;
        if (steamRunning && !closeSteam()) return;
        Restore(vdf, id, prefs, () => prefs.Save(prefsPath));
    }
}

/// <summary>The dialog's service on this PC.</summary>
sealed class LauncherSteamOption(LauncherEnv env, Func<string?> stableExe) : ISteamOptionService
{
    string? Vdf() => Steam.LocalConfig(Steam.InstallPath(), Steam.ActiveAccount());

    SteamOptionInfo Info()
    {
        var exe = stableExe();
        var line = SteamOption.Line(exe ?? Environment.ProcessPath!);
        string? current = null;
        var can = false;
        if (exe is not null && env.Settings.SteamAppId is { } id && Vdf() is { } vdf)
            try { (current, can) = (SteamOption.Current(vdf, id), true); }
            catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException) { } // left alone: the dialog says so
        return new(line, exe is not null, can, current, can && current == line, Steam.Running());
    }

    public Task<SteamOptionInfo> ReadAsync() => Task.Run(Info);

    public Task<Result> SetAsync() => Task.Run(() =>
    {
        var info = Info();
        if (!info.CanSet || Vdf() is not { } vdf) return Result.Fail(Kit.S("Steam.Unreadable"));
        if (info.Running && !Steam.Shutdown(TimeSpan.FromSeconds(30))) return Result.Fail(Kit.S("Steam.NoClose"));
        try { SteamOption.Set(vdf, env.Settings.SteamAppId!, info.Line, env.Prefs, env.Save); return Result.Success; }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException) { return Result.Fail(Kit.S("Steam.Unreadable")); }
    });

    public bool StartSteam() => Steam.StartSilent();

    public Task<bool> DueAsync() => Task.Run(() =>
    {
        var p = env.Prefs;
        if (p.SteamOptionNever || p.SteamOptionAsked > DateTimeOffset.UtcNow.AddDays(-1)) return false;
        var info = Info();
        if (!info.CanSet || info.Ours) return false;
        p.SteamOptionAsked = DateTimeOffset.UtcNow;
        env.Save();
        return true;
    });

    public void NotAgain() { env.Prefs.SteamOptionNever = true; env.Save(); }
}
