using Microsoft.Win32;

namespace Resurgence.Launcher;

/// <summary>The per-user link scheme hunter-resurgence:// (the server's start page opens hunter-resurgence://open): Windows starts the launcher exe with the link as its argument. Written below HKCU, so it needs no admin.</summary>
static class LinkScheme
{
    public const string Name = "hunter-resurgence";
    const string Path = @"Software\Classes\" + Name;

    /// <param name="root">HKCU in the launcher; a temporary key in the tests.</param>
    public static void Register(RegistryKey root, string exe)
    {
        using var k = root.CreateSubKey(Path);
        k.SetValue("", "URL:Hunter Resurgence");
        k.SetValue("URL Protocol", "");
        using var cmd = k.CreateSubKey(@"shell\open\command");
        cmd.SetValue("", $"\"{exe}\" \"%1\"");
    }

    public static void Remove(RegistryKey root) => root.DeleteSubKeyTree(Path, false);
}
