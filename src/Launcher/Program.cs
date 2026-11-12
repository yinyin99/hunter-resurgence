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

    [STAThread]
    static void Main(string[] args)
    {
        // first: Velopack runs the install and uninstall hooks here and exits. A downloaded update is applied at start only by the first launcher (a second start, the link or the
        // elevated --hosts copy, must not close the open one) and never while the game runs
        var applyUpdate = !Mutex.TryOpenExisting(Instance, out var other) && args is not ["--hosts", _] && !GameFiles.ClientRunning(Settings.Load().ClientExe);
        other?.Dispose();
        VelopackApp.Build()
            .SetAutoApplyOnStartup(applyUpdate)
            .OnAfterInstallFastCallback(_ => LinkScheme.Register(Registry.CurrentUser, Environment.ProcessPath!))
            .OnFirstRun(_ => FirstRun = true)
            .OnBeforeUninstallFastCallback(_ => Cleanup.Uninstall(Hosts.DefaultPath, Pki.Place.Default, Registry.CurrentUser, Path.GetDirectoryName(Prefs.DefaultPath)!,
                () => MessageBox.Show(Kit.S("Uninstall.DeleteData"), Kit.S("App.Name"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes))
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

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);
}
