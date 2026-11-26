using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using System.Windows;
using Resurgence.Services;

namespace Resurgence.Launcher;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        // dev aid: resurgence-launcher.exe --mock=patch:new-patch shows a patch state, --mock=settings:first the first start dialog, --mock=session:signup an account without a username
        var mock = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--mock="))?[7..];
        var services = LauncherServices.Build(mock, Settings.Load().ServerUrl);
        this.services = services;
        log = services.GetRequiredService<ILogger<MainWindow>>();
        env = services.GetRequiredService<LauncherEnv>();
        InitializeComponent();
        startPath = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--route="))?[8..] ?? "/splash";
        Content = NewBlazor();
        // a driver that cannot draw leaves the window black without any failure: no first successful navigation after the timeout is reported too
        var late = new System.Windows.Threading.DispatcherTimer { Interval = WebViewStart.LoadTimeout };
        late.Tick += (_, _) => { late.Stop(); if (!pageLoaded) ShowProblem(WebViewStart.Problem.NotLoaded, null); };
        late.Start();
        Title += " v" + LauncherEnv.AppVersion;
        // dev aid: resurgence-launcher.exe --route=/store opens that route first
        // the window opens where it was closed (on a screen that is still there); else it is sized so that the page area is exactly 1280x760, as in every reference picture
        // ponytail: one rectangle around all monitors, so a corner no monitor covers counts as on screen; test each monitor (EnumDisplayMonitors) if players get lost there
        if (env.Prefs.Window is { } place && place.OnScreen(new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight)))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            (Left, Top, Width, Height) = (place.Left, place.Top, place.Width, place.Height);
            if (place.Maximized) WindowState = WindowState.Maximized;
        }
        else Loaded += (_, _) => { Width += 1280 - Blazor.ActualWidth; Height += 760 - Blazor.ActualHeight; };
        // the close-game answer minimizes before closing: the last state that was not minimized is the one to keep
        maximized = WindowState == WindowState.Maximized;
        StateChanged += (_, _) => { if (WindowState != WindowState.Minimized) maximized = WindowState == WindowState.Maximized; };
        // closing while the game runs would stop the forwarder (it lives as long as the launcher process, not as long as the page) and disconnect the game, so the player is asked; Windows shutdown and sign-out are never held up
        Application.Current.SessionEnding += (_, _) => ending = true;
        Closed += (_, _) => (services.GetService<IKitService>() as LauncherKit)?.LogClosing(); // the forwarder stops with the launcher process
        Closing += (_, e) =>
        {
            if (closing || !NeedsAnswer(ending, GameRunning)) { RememberPlace(); return; }
            e.Cancel = true;
            // the dialog opens after this event: a window cannot own a dialog while it is closing
            if (!asking) Dispatcher.InvokeAsync(Ask);
        };
    }

    readonly LauncherEnv env;
    readonly IServiceProvider services;
    readonly ILogger<MainWindow> log;
    readonly string startPath;
    BlazorWebView Blazor => (BlazorWebView)Content;

    BlazorWebView NewBlazor()
    {
        var b = new BlazorWebView { HostPage = @"wwwroot\index.html", Services = services, StartPath = startPath };
        b.RootComponents.Add(new RootComponent { Selector = "head::after", ComponentType = typeof(Microsoft.AspNetCore.Components.Web.HeadOutlet) });
        b.RootComponents.Add(new RootComponent { Selector = "#app", ComponentType = typeof(Routes) });
        b.BlazorWebViewInitializing += (_, e) =>
        {
            if (env.Prefs.DisableGpu) e.EnvironmentOptions = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = WebViewStart.NoGpuArgument };
            // before the browser starts, so that a failed start is heard
            b.WebView.CoreWebView2InitializationCompleted += (_, c) => { if (!c.IsSuccess) ShowProblem(WebViewStart.Classify(c.InitializationException), c.InitializationException); };
        };
        b.BlazorWebViewInitialized += (_, e) =>
        {
            e.WebView.CoreWebView2.ProcessFailed += (_, f) => OnProcessFailed(f.ProcessFailedKind, f.Reason.ToString(), f.ExitCode);
            e.WebView.CoreWebView2.NavigationCompleted += (_, n) => pageLoaded |= n.IsSuccess;
        };
        return b;
    }

    bool pageLoaded, problemShown;

    /// <summary>The browser did not start or the page did not load: the player is told once (<see cref="WebViewStart"/>) and the answer is carried out.</summary>
    void ShowProblem(WebViewStart.Problem problem, Exception? cause) => Dispatcher.InvokeAsync(() =>
    {
        if (problemShown) return;
        problemShown = true;
        switch (WebViewStart.Report(problem, env.Prefs.DisableGpu, cause))
        {
            case WebViewStart.Next.Restart: RestartWithoutGpu(); break;
            case WebViewStart.Next.Close: closing = true; Close(); break;
        }
    });

    /// <summary>Remembers "no hardware acceleration" and starts the launcher again with the same arguments; the new one waits for this one to end (it holds the single-instance mutex).</summary>
    void RestartWithoutGpu()
    {
        log.LogInformation("Restarting the launcher without hardware acceleration");
        env.Prefs.DisableGpu = true;
        env.Save();
        var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!);
        foreach (var a in Environment.GetCommandLineArgs().Skip(1).Where(a => !a.StartsWith("--wait-for="))) start.ArgumentList.Add(a);
        start.ArgumentList.Add("--wait-for=" + Environment.ProcessId);
        System.Diagnostics.Process.Start(start)?.Dispose();
        closing = true;
        Close();
    }

    public enum Recovery { None, Reload, Recreate }

    /// <summary>What to do when one of WebView2's processes fails: a failed renderer or GPU process only needs the page again; a failed browser process takes the whole control with it. Other kinds (utility, sandbox helper, plug-in) leave the page working.</summary>
    public static Recovery Recover(CoreWebView2ProcessFailedKind kind) => kind switch
    {
        CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive or CoreWebView2ProcessFailedKind.GpuProcessExited => Recovery.Reload,
        CoreWebView2ProcessFailedKind.BrowserProcessExited => Recovery.Recreate,
        _ => Recovery.None,
    };

    /// <summary>Runs the recovery for a failed WebView2 process. Only the page is replaced: the forwarder belongs to the launcher's services (<see cref="LauncherKit"/>), so a recreated control never stops it.</summary>
    public static void Handle(CoreWebView2ProcessFailedKind kind, Action reload, Action recreate)
    {
        switch (Recover(kind))
        {
            case Recovery.Reload: reload(); break;
            case Recovery.Recreate: recreate(); break;
        }
    }

    void OnProcessFailed(CoreWebView2ProcessFailedKind kind, string reason, int exitCode)
    {
        log.LogWarning("WebView2 process failed: {Kind}, {Reason}, exit code {ExitCode}", kind, reason, exitCode);
        // the event may come on any thread
        Dispatcher.InvokeAsync(() => Handle(kind, () => Blazor.WebView.CoreWebView2?.Reload(), () =>
        {
            var old = Blazor;
            Content = NewBlazor();
            // the old page's cleanup may wait for a browser that is gone: it runs on in the background, a failure is only logged
            _ = Dispatcher.InvokeAsync(async () =>
            {
                try { await ((IAsyncDisposable)old).DisposeAsync(); }
                catch (Exception e) when (e is not OutOfMemoryException) { log.LogWarning(e, "the failed WebView2 page did not clean up"); }
            });
        }));
    }

    bool ending, closing, asking, maximized;

    /// <summary>Saves the window's normal bounds and whether it was maximized for the next start, through the launcher's one copy of its preferences (a failed save is ignored there).</summary>
    void RememberPlace()
    {
        var b = RestoreBounds;
        if (b.IsEmpty) return;
        env.Prefs.Window = new(b.Left, b.Top, b.Width, b.Height, maximized);
        env.Save();
    }

    static bool GameRunning() => GameFiles.ClientRunning(Settings.Load().ClientExe);

    public enum CloseChoice { Exit, CloseGame, Minimize }

    /// <summary>Only a running game and no Windows shutdown or sign-out call for a question.</summary>
    public static bool NeedsAnswer(bool sessionEnding, Func<bool> gameRunning) => !sessionEnding && gameRunning();

    /// <summary>Exit at once when there is nothing to ask; else the player's answer: Yes closes the game and then the launcher, No minimizes the launcher and leaves the game running.</summary>
    public static CloseChoice Decide(bool sessionEnding, Func<bool> gameRunning, Func<bool> closeBoth) =>
        !NeedsAnswer(sessionEnding, gameRunning) ? CloseChoice.Exit : closeBoth() ? CloseChoice.CloseGame : CloseChoice.Minimize;

    async Task Ask()
    {
        asking = true;
        try
        {
            var d = Decide(ending, GameRunning, () =>
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; // a dialog centres on a visible owner
                return new CloseDialog { Owner = this }.ShowDialog() == true;
            });
            if (d == CloseChoice.Minimize) { WindowState = WindowState.Minimized; return; }
            if (d == CloseChoice.CloseGame)
            {
                // the forwarder runs until the launcher exits, so the game can still end its hunt on the server while it closes
                WindowState = WindowState.Minimized; // still in the taskbar while the game closes, so no second copy is started by mistake
                try { await GameFiles.StopClientAsync(Settings.Load().ClientExe, TimeSpan.FromSeconds(10)); }
                finally { closing = true; Close(); }
                return;
            }
            closing = true; // the game ended before the dialog opened
            Close();
        }
        finally { asking = false; }
    }

    /// <summary>Settings, Clear launcher cache: the launcher's own browser drops its disk cache and cache storage while it runs; cookies, local storage and settings stay.</summary>
    public static Task ClearBrowserCache() => Application.Current.Dispatcher.InvokeAsync(() =>
        (((MainWindow)Application.Current.MainWindow).Blazor.WebView.CoreWebView2 ?? throw new InvalidOperationException("the browser has not started"))
            .Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage)).Task.Unwrap();
}
