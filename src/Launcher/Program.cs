using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;
using Velopack;

namespace Resurgence.Launcher;

static class Program
{
    const string Instance = "hunter-resurgence-launcher";

    /// <summary>True in the one start Velopack makes right after the install: the clean-up of an older setup (<see cref="LauncherCleanup"/>) runs then.</summary>
    public static bool FirstRun { get; private set; }

    /// <summary>True in the start Velopack makes after it applied an update (the restart of <see cref="LauncherUpdater.ApplyAndRestart"/>): the new window is brought forward then.</summary>
    public static bool Restarted { get; private set; }

    [STAThread]
    static void Main(string[] args)
    {
        // before anything that can fail, so that a failure in the start-up is in launcher.log; not the elevated --hosts copy, which only edits the hosts file
        if (args is not ["--hosts", _]) LauncherLog.Default.WatchProcess();
        // the restart without hardware acceleration (MainWindow.RestartWithoutGpu) names the launcher it replaces: it still holds the single-instance mutex until it has ended
        if (args.FirstOrDefault(a => a.StartsWith("--wait-for="))?[11..] is { } old && int.TryParse(old, out var oldId))
            try { System.Diagnostics.Process.GetProcessById(oldId).WaitForExit(10_000); }
            catch (ArgumentException) { } // already ended
        // first: Velopack runs the install and uninstall hooks here and exits. A downloaded update is applied at start only by the first launcher (a second start, the link or the
        // elevated --hosts copy, must not close the open one) and never while the game runs
        var applyUpdate = !Mutex.TryOpenExisting(Instance, out var other) && args is not ["--hosts", _] && !GameFiles.ClientRunning(Settings.Load().ClientExe);
        other?.Dispose();
        VelopackApp.Build()
            .SetAutoApplyOnStartup(applyUpdate)
            .OnAfterInstallFastCallback(_ => LinkScheme.Register(Registry.CurrentUser, Environment.ProcessPath!))
            .OnFirstRun(_ => FirstRun = true)
            .OnRestarted(_ => Restarted = true)
            .OnBeforeUninstallFastCallback(_ =>
            {
                // the hook is stopped after 30 seconds: the hosts block, the certificate and the link first, Steam's launch option last (it may wait for the player and for Steam)
                var steam = SteamOption.Uninstall(Prefs.DefaultPath, Settings.Load(), Steam.Running,
                    () => MessageBox.Show(Kit.S("Steam.UninstallClose"), Kit.S("App.Name"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes
                        && Steam.Shutdown(TimeSpan.FromSeconds(20)));
                Cleanup.Uninstall(Hosts.DefaultPath, Pki.Place.Default, Registry.CurrentUser, Path.GetDirectoryName(Prefs.DefaultPath)!,
                    () => MessageBox.Show(Kit.S("Uninstall.DeleteData"), Kit.S("App.Name"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes);
                try { steam?.Invoke(); }
                catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException) { }
            })
            .Run();

        var app = new App();
        app.InitializeComponent();

        // the elevated hosts edit (--hosts on|off) is its own short process, started while the normal launcher is often open: no single instance for it
        if (args is ["--hosts", _])
        {
            app.Run();
            return;
        }

        // one launcher: a second start (the link hunter-resurgence://open, a shortcut, Steam) brings the running one forward and exits
        using var one = new Mutex(true, Instance, out var first);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, Instance + ".show");
        if (!first)
        {
            AllowSetForegroundWindow(-1); // any process: the running launcher may take the foreground
            show.Set();
            return;
        }

        new Thread(() => { while (show.WaitOne()) app.Dispatcher.Invoke(() => Forward(app.MainWindow)); }) { IsBackground = true }.Start();
        app.Run();
    }

    static void Forward(Window? w)
    {
        if (w is null) return;
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
    }

    /// <summary>Blinks the window's taskbar button until the player brings it forward.</summary>
    public static void Flash(nint window)
    {
        var info = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = window, Flags = 3 | 12 }; // FLASHW_ALL | FLASHW_TIMERNOFG
        FlashWindowEx(ref info);
    }

    struct FlashInfo { public uint Size; public nint Window; public uint Flags, Count, Timeout; }

    [DllImport("user32.dll")]
    static extern bool FlashWindowEx(ref FlashInfo info);

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);
}
