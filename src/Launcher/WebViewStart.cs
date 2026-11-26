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

    public enum Problem { RuntimeMissing, StartFailed, NotLoaded }

    /// <summary>What the message offers: <see cref="Install"/> opens the download page (Yes) or closes the launcher (No); <see cref="RestartOrClose"/> and <see cref="RestartOrWait"/> restart without hardware acceleration (Yes) or close the launcher / keep waiting (No); <see cref="Last"/> only says it (OK).</summary>
    public enum Notice { Install, RestartOrClose, RestartOrWait, Last }

    /// <summary>What the launcher does after the player's answer.</summary>
    public enum Next { Close, Restart, Wait }

    /// <summary>The Runtime is missing, or no version is found.</summary>
    public static bool RuntimeMissing(Func<string?> version)
    {
        try { return string.IsNullOrEmpty(version()); }
        catch (WebView2RuntimeNotFoundException) { return true; }
    }

    /// <summary>The Runtime is missing, or the browser failed to start.</summary>
    public static Problem Classify(Exception? initialization) => initialization is WebView2RuntimeNotFoundException ? Problem.RuntimeMissing : Problem.StartFailed;

    /// <param name="gpuOff">The browser already runs without hardware acceleration: a second restart would not change anything, so there is none.</param>
    public static Notice Decide(Problem problem, bool gpuOff) => (problem, gpuOff) switch
    {
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
    public static Next Report(Problem problem, bool gpuOff, Exception? cause)
    {
        LauncherLog.Default.Write(LogLevel.Error, problem switch
        {
            Problem.RuntimeMissing => "The WebView2 Runtime is not installed",
            Problem.StartFailed => $"WebView2 failed to start (hardware acceleration {(gpuOff ? "off" : "on")})",
            _ => $"The launcher's page did not load within {LoadTimeout.TotalSeconds:0} seconds (hardware acceleration {(gpuOff ? "off" : "on")})",
        }, cause);
        var notice = Decide(problem, gpuOff);
        var buttons = notice == Notice.Last ? MessageBoxButton.OK : MessageBoxButton.YesNo;
        var yes = MessageBox.Show(Kit.S(Key(notice)), Kit.S("App.Name"), buttons, MessageBoxImage.Warning) is MessageBoxResult.Yes or MessageBoxResult.OK;
        if (notice == Notice.Install && yes)
            try { Process.Start(new ProcessStartInfo(InstallUrl) { UseShellExecute = true })?.Dispose(); }
            catch (Win32Exception e) { LauncherLog.Default.Write(LogLevel.Warning, "The WebView2 download page did not open", e); }
        return Answer(notice, yes);
    }
}
