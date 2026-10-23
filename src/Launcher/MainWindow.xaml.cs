using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace Resurgence.Launcher;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
        services.AddLocalization();
        services.AddSingleton<LauncherEnv>();
        Resources.Add("services", services.BuildServiceProvider());
        InitializeComponent();
        // dev aid: resurgence-launcher.exe --route=/store opens that route first
        Blazor.StartPath = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--route="))?[8..] ?? "/";
    }
}
