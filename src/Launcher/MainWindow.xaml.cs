using Microsoft.Web.WebView2.Core;
using System.Windows;

namespace Resurgence.Launcher;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        // dev aid: resurgence-launcher.exe --mock=patch:new-patch shows a patch state, --mock=settings:first the first start dialog, --mock=session:signup an account without a username
        var mock = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--mock="))?[7..];
        Resources.Add("services", LauncherServices.Build(mock, Settings.Load().ServerUrl));
        InitializeComponent();
        // dev aid: resurgence-launcher.exe --route=/store opens that route first
        // the window is sized so that the page area is exactly 1280x760, as in every reference picture
        Loaded += (_, _) => { Width += 1280 - Blazor.ActualWidth; Height += 760 - Blazor.ActualHeight; };
        // closing while the game runs would stop the forwarder and disconnect the game, so the player is asked; Windows shutdown and sign-out are never held up
        Application.Current.SessionEnding += (_, _) => ending = true;
        Closing += (_, e) =>
        {
            if (closing || !NeedsAnswer(ending, GameRunning)) return;
            e.Cancel = true;
            // the dialog opens after this event: a window cannot own a dialog while it is closing
            if (!asking) Dispatcher.InvokeAsync(Ask);
        };
        Blazor.StartPath = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--route="))?[8..] ?? "/splash";
    }

    bool ending, closing, asking;

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
