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
}
