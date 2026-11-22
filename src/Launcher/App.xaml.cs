using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Resurgence.Launcher;

public partial class App : System.Windows.Application
{
    public App()
    {
        // dates and numbers follow the UI language too. Only the process defaults are set, so that a language chosen later
        // (LauncherLanguageSwitcher) shows after the reload: assigning CurrentCulture/CurrentUICulture here would pin this thread
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture =
            CultureInfo.GetCultureInfo(Pick(Prefs.Load(Prefs.DefaultPath).Language, CultureInfo.CurrentUICulture.Name));
    }

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        // the elevated copy that player kit setup starts (--hosts on|off): it edits the hosts file and exits, without a window
        if (Kit.HostsArgument(e.Args) is { } on) { Shutdown(Kit.EditHosts(on)); return; }
        started = true;
        LauncherLog.Default.WatchUi(Dispatcher);
        LauncherLog.Default.Write(LogLevel.Information, $"Launcher {LauncherEnv.AppVersion} started");
        (MainWindow = new MainWindow()).Show();
    }

    bool started;

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        if (started) LauncherLog.Default.Write(LogLevel.Information, $"Launcher closed, exit code {e.ApplicationExitCode}");
        base.OnExit(e);
    }

    /// <summary>The saved choice when it is one of the languages, else the closest to the Windows display language (de-AT: German), else English.</summary>
    public static string Pick(string? saved, string windows) =>
        (Resurgence.Ui.Languages.Find(saved) ?? Resurgence.Ui.Languages.Match(windows))?.Code ?? "en";
}
