using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Resurgence.Services;
using Resurgence.Services.Mocks;
using Resurgence.Ui;
using Resurgence.Ui.Modals;

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
        // dev aid: resurgence-launcher.exe --mock=patch:new-patch shows a patch state, --mock=settings:first the first start dialog, --mock=session:signup an account without a username
        var mock = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--mock="))?[7..];
        services.AddSingleton(new HostMode { Launcher = true, Mock = mock });
        services.AddSingleton<IPatchService>(new MockPatchService(mock));
        services.AddSingleton<IGameSettingsService>(sp => mock == "settings:first" ? new MockGameSettingsService(sp.GetRequiredService<ILogger<MockGameSettingsService>>(), mock)
            : new GameSettingsService(sp.GetRequiredService<LauncherEnv>().Settings, sp.GetRequiredService<LauncherEnv>().Prefs, GameFiles.AppData, sp.GetRequiredService<LauncherEnv>().Save));
        services.AddSingleton<ISignupService>(new MockSignupService(mock));
        services.AddSingleton<IAccountService>(new MockAccountService(mock));
        services.AddSingleton<INewsService>(new MockNewsService(mock));
        services.AddSingleton<IMissionsService>(new MockMissionsService(mock));
        services.AddSingleton<IProfileService>(new MockProfileService(mock));
        services.AddSingleton<ModalService>();
        services.AddSingleton<ILanguageSwitcher, LauncherLanguageSwitcher>();
        Resources.Add("services", services.BuildServiceProvider());
        InitializeComponent();
        // dev aid: resurgence-launcher.exe --route=/store opens that route first
        // the window is sized so that the page area is exactly 1280x760, as in every reference picture
        Loaded += (_, _) => { Width += 1280 - Blazor.ActualWidth; Height += 760 - Blazor.ActualHeight; };
        Blazor.StartPath = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--route="))?[8..] ?? "/splash";
    }
}
