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

    /// <summary>Starts Steam minimized, false when it is not installed or does not start.</summary>
    public static bool StartSilent()
    {
        if (InstallPath() is not { } dir || Path.Combine(dir, "steam.exe") is var exe && !File.Exists(exe)) return false;
        try { using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "-silent") { WorkingDirectory = dir }); return p is not null; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    /// <summary>True when somebody is signed in to Steam; when Steam is not running it is started first, and waited for up to the timeout.</summary>
    public static bool SignedIn(Func<uint> account, Func<bool> startSteam, TimeSpan timeout, TimeSpan poll)
    {
        if (account() != 0) return true;
        if (!startSteam()) return false;
        for (var end = DateTime.UtcNow + timeout; DateTime.UtcNow < end;)
        {
            Thread.Sleep(poll);
            if (account() != 0) return true;
        }
        return false;
    }
}
