using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;
using Velopack;

namespace Resurgence.Launcher;

static class Program
{
    const string Instance = "hunter-resurgence-launcher";

    [STAThread]
    static void Main(string[] args)
    {
        // first: Velopack runs the install and uninstall hooks here and exits; a downloaded update is applied at start, but not while the game runs
        VelopackApp.Build()
            .SetAutoApplyOnStartup(!GameFiles.ClientRunning(Settings.Load().ClientExe))
            .OnAfterInstallFastCallback(_ => LinkScheme.Register(Registry.CurrentUser, Environment.ProcessPath!))
            .OnBeforeUninstallFastCallback(_ => LinkScheme.Remove(Registry.CurrentUser))
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
