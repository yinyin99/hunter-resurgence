using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Ui;

namespace Resurgence.Launcher;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
        services.AddUiLocalization(Settings.FilePath);
        services.AddMockServices();
        services.AddSingleton<LauncherEnv>();
        services.AddSingleton(new HostMode { Launcher = true });
        Resources.Add("services", services.BuildServiceProvider());
        InitializeComponent();
        // dev aid: resurgence-launcher.exe --route=/store opens that route first
        // the window is sized so that the page area is exactly 1280x760, as in every reference picture
        Loaded += (_, _) => { Width += 1280 - Blazor.ActualWidth; Height += 760 - Blazor.ActualHeight; };
        Blazor.StartPath = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--route="))?[8..] ?? "/";
    }
}
