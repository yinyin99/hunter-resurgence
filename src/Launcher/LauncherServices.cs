using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Resurgence.Services;
using Resurgence.Services.Mocks;
using Resurgence.Ui;
using Resurgence.Ui.Modals;

namespace Resurgence.Launcher;

/// <summary>The launcher's services. Blazor resolves them from one scope per page; scopes are validated so that a singleton never holds a page's service.</summary>
static class LauncherServices
{
    public static ServiceProvider Build(string? mock)
    {
        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
        services.AddUiLocalization(Branding.Load(Settings.FilePath));
        services.AddMockServices();
        services.AddSingleton<LauncherEnv>();
        services.AddSingleton(new HostMode { Launcher = true, Mock = mock });
        services.AddSingleton<IPatchService>(new MockPatchService(mock));
        services.AddSingleton<IGameSettingsService>(sp => mock == "settings:first" ? new MockGameSettingsService(sp.GetRequiredService<ILogger<MockGameSettingsService>>(), mock)
            : new GameSettingsService(sp.GetRequiredService<LauncherEnv>().Settings, sp.GetRequiredService<LauncherEnv>().Prefs, GameFiles.AppData, sp.GetRequiredService<LauncherEnv>().Save));
        services.AddSingleton<IWalletService>(sp => new MockWalletService(sp.GetRequiredService<MockWalletState>(), mock));
        services.AddSingleton<IStoreService>(sp => new MockStoreService(sp.GetRequiredService<MockWalletState>(), mock));
        services.AddSingleton<IBankService>(sp => new MockBankService(sp.GetRequiredService<MockWalletState>(), mock));
        services.AddSingleton<ISignupService>(new MockSignupService(mock));
        services.AddSingleton<IAccountService>(new MockAccountService(mock));
        services.AddSingleton<INewsService>(new MockNewsService(mock));
        services.AddSingleton<IMissionsService>(new MockMissionsService(mock));
        services.AddSingleton<IFriendsService>(new MockFriendsService(mock));
        services.AddSingleton<IMessagesService>(new MockMessagesService(mock));
        services.AddSingleton<IProfileService>(sp => new MockProfileService(mock, sp.GetRequiredService<IFriendsService>()));
        services.AddSingleton<ModalService>();
        services.AddScoped<ILanguageSwitcher, LauncherLanguageSwitcher>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}
