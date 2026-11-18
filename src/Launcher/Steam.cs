using Microsoft.Win32;

namespace Resurgence.Launcher;

/// <summary>Read-only look at the Steam client on this PC (current user's registry keys).</summary>
public static class Steam
{
    /// <summary>The Steam folder, null when Steam is not installed for this user.</summary>
    public static string? InstallPath() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string p && p.Length > 0 ? Path.GetFullPath(p) : null;

    /// <summary>Account id of the signed-in Steam user, 0 = Steam not running or nobody signed in.</summary>
    public static uint ActiveAccount() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", 0) is int v ? (uint)v : 0;

    public static string SteamId64(uint account) => (76561197960265728UL + account).ToString();

    /// <summary>Steam's settings file of the signed-in <paramref name="account"/>, else of the user whose file changed last; null when there is none.</summary>
    public static string? LocalConfig(string? steamPath, uint account)
    {
        if (steamPath is null || !Directory.Exists(Path.Combine(steamPath, "userdata"))) return null;
        string File(string dir) => Path.Combine(dir, "config", "localconfig.vdf");
        var own = File(Path.Combine(steamPath, "userdata", account.ToString()));
        if (account != 0 && System.IO.File.Exists(own)) return own;
        return Directory.EnumerateDirectories(Path.Combine(steamPath, "userdata")).Select(File).Where(System.IO.File.Exists).OrderByDescending(System.IO.File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    public static bool Running() => System.Diagnostics.Process.GetProcessesByName("steam").Length > 0;

    /// <summary>Asks Steam to exit (<c>steam.exe -shutdown</c>) and waits for it up to <paramref name="timeout"/>; never kills it. True when Steam is not running.</summary>
    public static bool Shutdown(TimeSpan timeout)
    {
        if (!Running()) return true;
        if (InstallPath() is not { } dir || Path.Combine(dir, "steam.exe") is var exe && !File.Exists(exe)) return false;
        try { using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "-shutdown") { WorkingDirectory = dir }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
        for (var end = DateTime.UtcNow + timeout; DateTime.UtcNow < end;)
        {
            Thread.Sleep(500);
            if (!Running()) return true;
        }
        return false;
    }

    static System.Diagnostics.Process? started;

    /// <summary>
    /// Starts Steam minimized, false when it is not installed or does not start. The process is kept for <see cref="StopStarted"/> only when no Steam was running before: a Steam the player already had
    /// is never the launcher's to stop.
    /// </summary>
    public static bool StartSilent()
    {
        if (InstallPath() is not { } dir || Path.Combine(dir, "steam.exe") is var exe && !File.Exists(exe)) return false;
        var wasRunning = Running();
        try
        {
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "-silent") { WorkingDirectory = dir });
            started?.Dispose();
            started = wasRunning ? null : p;
            if (wasRunning) p?.Dispose();
            return p is not null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    /// <summary>
    /// Asks the Steam that <see cref="StartSilent"/> started to exit (<c>steam.exe -shutdown</c>, never a kill). Called only when the player presses Start without Steam, never when the wait runs out (the player may be
    /// typing a sign-in code). Only while that very process still runs: when it has gone (Steam restarted itself to update, or handed over to another Steam) nobody can prove which process is the launcher's, and nothing
    /// is stopped. Does not wait.
    /// </summary>
    public static void StopStarted()
    {
        var p = started;
        started = null;
        using (p)
        {
            if (p is null || p.HasExited || InstallPath() is not { } dir || Path.Combine(dir, "steam.exe") is var exe && !File.Exists(exe)) return;
            try { using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "-shutdown") { WorkingDirectory = dir }); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
    }

    /// <summary>True when steam.exe is in the Steam folder of this user.</summary>
    public static bool Installed() => InstallPath() is { } dir && File.Exists(Path.Combine(dir, "steam.exe"));
}

/// <summary>What a start asks of Steam; the seam of <see cref="LauncherBridge"/>: a test passes fakes.</summary>
/// <param name="Installed">Steam is installed for this user.</param>
/// <param name="SignedIn">Somebody is signed in to Steam (does not start it).</param>
/// <param name="Start">Starts Steam minimized; false when it does not start.</param>
/// <param name="StopStarted">Asks the Steam this launcher started for the wait to exit (never one it did not start); null: nothing to stop.</param>
public sealed record SteamProbes(Func<bool> Installed, Func<bool> SignedIn, Func<bool> Start, Action? StopStarted = null)
{
    public static SteamProbes Real { get; } = new(Steam.Installed, () => Steam.ActiveAccount() != 0, Steam.StartSilent, Steam.StopStarted);

    /// <summary>The mode the game starts in if started now: "nosteam" when Steam is not installed, else null (a start with Steam can still fall back to none: 16010, a late Steam).</summary>
    public string? PlannedMode() => !Installed() ? "nosteam" : null;
}
