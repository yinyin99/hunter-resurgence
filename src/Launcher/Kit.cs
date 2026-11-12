using System.ComponentModel;
using System.Diagnostics;
using System.Resources;

namespace Resurgence.Launcher;

/// <summary>The player kit's shared parts: its texts (the <c>Kit.*</c> keys of src/Ui/Strings.resx), the hosts edit that needs administrator rights and the command line that asks for it.</summary>
static class Kit
{
    static readonly ResourceManager Rm = new(typeof(Resurgence.Ui.Strings).FullName!, typeof(Resurgence.Ui.Strings).Assembly);

    public static string S(string key) => Rm.GetString(key) ?? throw new KeyNotFoundException("no string " + key);

    public static string F(string key, params object[] args) => string.Format(S(key), args);

    /// <summary>The elevated copy's command line: exactly <c>--hosts on</c> or <c>--hosts off</c> (true, false), any other line is null. Only this reaches the hosts edit as administrator.</summary>
    public static bool? HostsArgument(string[] args) => args is ["--hosts", "on"] ? true : args is ["--hosts", "off"] ? false : null;

    /// <summary>What the elevated copy does: edits the hosts file and nothing else. The exit code.</summary>
    public static int EditHosts(bool on)
    {
        try { Hosts.ApplyAndFlush(Hosts.DefaultPath, on ? KitSettings.Load().Hosts : null); return 0; }
        catch (Exception e) when (e is not OutOfMemoryException) { return 1; }
    }

    /// <summary>Edits the hosts file. Without administrator rights, starts this exe again with "--hosts on|off" through the UAC prompt: that copy only edits the file, so the certificate steps stay in the user's own session.</summary>
    public static void SetHosts(bool on, string[]? hosts, string? hostsFile = null)
    {
        var path = hostsFile ?? Hosts.DefaultPath;
        try { Hosts.ApplyAndFlush(path, hosts); return; }
        catch (UnauthorizedAccessException) { }
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, on ? "--hosts on" : "--hosts off") { UseShellExecute = true, Verb = "runas" });
            p?.WaitForExit();
        }
        catch (Win32Exception) { } // the user said No
        if (Hosts.Has(File.ReadAllText(path)) != on) throw new InvalidOperationException(S("Kit.HostsFailed"));
    }
}
