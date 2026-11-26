using System.Management;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;

namespace Resurgence.Launcher;

/// <summary>
/// A launcher whose embedded browser (WebView2) does not start or never shows the page is a black window the player cannot do anything with. This decides what to say and do instead (docs/launcher.md, "When the launcher's page fails"):
/// no WebView2 Runtime, or a start that fails with hardware acceleration off: the download page; a start that fails or a page that is not there after <see cref="LoadTimeout"/> with it on: a restart without it,
/// remembered in launcher.json (<see cref="Prefs.DisableGpu"/>); a page that is still not there with it off: only the plain message, never another restart. Each case is also written to launcher.log, which a problem report holds.
/// </summary>
public static class WebViewStart
{
    /// <summary>Microsoft's Evergreen bootstrapper (the Runtime's installer).</summary>
    public const string InstallUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    /// <summary>The browser argument of the restart without hardware acceleration.</summary>
    public const string NoGpuArgument = "--disable-gpu";

    /// <summary>How long the page has to load (its first successful navigation) before the launcher asks about the restart.</summary>
    public static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(20);

    /// <summary><c>Busy</c>: the start failed with ERROR_INVALID_STATE, an older browser process still holds the user data folder with other arguments; it goes away by itself, so the control is built once more, never the download page.</summary>
    public enum Problem { RuntimeMissing, StartFailed, NotLoaded, Busy }

    /// <summary>What the message offers: <see cref="Install"/> opens the download page (Yes) or closes the launcher (No); <see cref="RestartOrClose"/> and <see cref="RestartOrWait"/> restart without hardware acceleration (Yes) or close the launcher / keep waiting (No); <see cref="Last"/> only says it (OK).</summary>
    public enum Notice { Install, RestartOrClose, RestartOrWait, Last }

    /// <summary>What the launcher does after the player's answer.</summary>
    public enum Next { Close, Restart, Wait }

    /// <summary>The command-line argument that names the launcher a restart replaces (<c>--wait-for=&lt;process id&gt;</c>).</summary>
    public const string WaitForPrefix = "--wait-for=";

    /// <summary>How long the old launcher lets its browser process end before it starts the new one.</summary>
    public static readonly TimeSpan BrowserExitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long the new launcher waits for the old one to release the single-instance mutex (more than <see cref="BrowserExitTimeout"/>, which comes first).</summary>
    public static readonly TimeSpan OldLauncherTimeout = TimeSpan.FromSeconds(20);

    /// <summary>The process id <c>--wait-for=</c> names; null when no such argument or no number.</summary>
    public static int? ParseWaitFor(IEnumerable<string> args) =>
        args.FirstOrDefault(a => a.StartsWith(WaitForPrefix, StringComparison.Ordinal)) is { } a && int.TryParse(a[WaitForPrefix.Length..], out var id) ? id : null;

    /// <summary>The arguments a started-again launcher keeps (the update's restart and ours): all but a <c>--wait-for=</c> that an earlier restart left, which is stale by then.</summary>
    public static string[] ForwardedArgs(IEnumerable<string> args) => [.. args.Where(a => !a.StartsWith(WaitForPrefix, StringComparison.Ordinal))];

    /// <summary>The restart without hardware acceleration's arguments: the forwarded ones, then the process to wait for.</summary>
    public static string[] RestartArgs(IEnumerable<string> args, int processId) => [.. ForwardedArgs(args), WaitForPrefix + processId];

    /// <summary>The Runtime is missing, or no version is found.</summary>
    public static bool RuntimeMissing(Func<string?> version)
    {
        try { return string.IsNullOrEmpty(version()); }
        catch (WebView2RuntimeNotFoundException) { return true; }
    }

    /// <summary>ERROR_INVALID_STATE (Windows error 5023) as an HResult: what WebView2 answers while a browser process with other arguments runs on the same user data folder.</summary>
    public const int InvalidState = -2147019873;

    /// <summary>How long the control waits before it is built again after <see cref="Problem.Busy"/>.</summary>
    public static readonly TimeSpan BusyDelay = TimeSpan.FromSeconds(3);

    /// <summary>The Runtime is missing, the browser is still busy with another start, or it failed to start.</summary>
    public static Problem Classify(Exception? initialization) => initialization switch
    {
        WebView2RuntimeNotFoundException => Problem.RuntimeMissing,
        not null when HasHResult(initialization, InvalidState) => Problem.Busy,
        _ => Problem.StartFailed,
    };

    static bool HasHResult(Exception e, int hResult) => e.HResult == hResult || (e.InnerException is { } inner && HasHResult(inner, hResult));

    /// <summary>No WebView2 browser process started by <paramref name="parent"/> is left. Used after a failed start, when no environment was handed out to tell it (a started browser is a direct child of the launcher); a failed query counts as gone.</summary>
    public static bool BrowserChildrenGone(int parent)
    {
        try
        {
            using var s = new ManagementObjectSearcher($"SELECT ProcessId FROM Win32_Process WHERE Name = 'msedgewebview2.exe' AND ParentProcessId = {parent}") { Options = { Timeout = TimeSpan.FromSeconds(5) } };
            using var found = s.Get();
            return found.Count == 0;
        }
        catch (Exception e) when (e is ManagementException or COMException or UnauthorizedAccessException) { return true; }
    }

    /// <param name="gpuOff">The browser already runs without hardware acceleration: a second restart would not change anything, so there is none.</param>
    public static Notice Decide(Problem problem, bool gpuOff) => (problem, gpuOff) switch
    {
        (Problem.Busy, _) => Notice.Last,
        (Problem.RuntimeMissing, _) or (Problem.StartFailed, true) => Notice.Install,
        (Problem.StartFailed, false) => Notice.RestartOrClose,
        (_, false) => Notice.RestartOrWait,
        _ => Notice.Last,
    };

    /// <param name="yes">Yes (OK for <see cref="Notice.Last"/>) was pressed.</param>
    public static Next Answer(Notice notice, bool yes) => notice switch
    {
        Notice.Install => Next.Close,
        Notice.RestartOrClose => yes ? Next.Restart : Next.Close,
        Notice.RestartOrWait => yes ? Next.Restart : Next.Wait,
        _ => Next.Wait,
    };

    /// <summary>The resx key of the message's text (<c>WebView.*</c> in src/Ui/Strings.resx).</summary>
    public static string Key(Notice notice) => notice switch
    {
        Notice.Install => "WebView.Install",
        Notice.RestartOrClose => "WebView.StartFailed",
        Notice.RestartOrWait => "WebView.NotLoaded",
        _ => "WebView.Last",
    };

    /// <summary>Writes the problem to launcher.log, shows the plain Windows message and carries out the install answer (opens the download page).</summary>
    public static Next Report(Problem problem, bool gpuOff, Exception? cause, Window? owner)
    {
        LauncherLog.Default.Write(LogLevel.Error, problem switch
        {
            Problem.Busy => "WebView2 is still busy with an earlier start (ERROR_INVALID_STATE) after the launcher built it again",
            Problem.RuntimeMissing => "The WebView2 Runtime is not installed",
            Problem.StartFailed => $"WebView2 failed to start (hardware acceleration {(gpuOff ? "off" : "on")})",
            _ => $"The launcher's page did not load within {LoadTimeout.TotalSeconds:0} seconds (hardware acceleration {(gpuOff ? "off" : "on")})",
        }, cause);
        var notice = Decide(problem, gpuOff);
        var buttons = notice == Notice.Last ? MessageBoxButton.OK : MessageBoxButton.YesNo;
        var text = Kit.S(Key(notice));
        var answer = owner is null
            ? MessageBox.Show(text, Kit.S("App.Name"), buttons, MessageBoxImage.Warning)
            : MessageBox.Show(owner, text, Kit.S("App.Name"), buttons, MessageBoxImage.Warning);
        var yes = answer is MessageBoxResult.Yes or MessageBoxResult.OK;
        if (notice == Notice.Install && yes)
            try { Process.Start(new ProcessStartInfo(InstallUrl) { UseShellExecute = true })?.Dispose(); }
            catch (Win32Exception e) { LauncherLog.Default.Write(LogLevel.Warning, "The WebView2 download page did not open", e); }
        return Answer(notice, yes);
    }
}
