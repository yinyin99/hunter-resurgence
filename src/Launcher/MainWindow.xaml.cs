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
        // closing while the game runs would stop the forwarder and disconnect the game; Windows shutdown and sign-out are never held up
        var ending = false;
        Application.Current.SessionEnding += (_, _) => ending = true;
        Closing += (_, e) =>
        {
            var d = Decide(ending, () => GameFiles.ClientRunning(Settings.Load().ClientExe), AskKeepOpen);
            if (d == CloseChoice.Exit) return;
            e.Cancel = true;
            if (d == CloseChoice.Minimize) WindowState = WindowState.Minimized;
        };
        Blazor.StartPath = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--route="))?[8..] ?? "/splash";
    }

    public enum CloseChoice { Exit, KeepOpen, Minimize }

    /// <summary>The window exits at a Windows shutdown or sign-out and when the game is not running; else the player's answer (keepOpen false: Minimize), and the launcher stays up either way.</summary>
    public static CloseChoice Decide(bool sessionEnding, Func<bool> gameRunning, Func<bool> keepOpen) =>
        sessionEnding || !gameRunning() ? CloseChoice.Exit : keepOpen() ? CloseChoice.KeepOpen : CloseChoice.Minimize;

    static bool AskKeepOpen() =>
        MessageBox.Show(Kit.S("Launcher.CloseWhileRunning"), Kit.S("App.Name"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;
}
