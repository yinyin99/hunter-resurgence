using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
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
        // the operator's pictures from the website at server_url (docs/images.md): the list is fetched while the window starts; the first slot waits for it
        var images = Task.Run(() => LoadImages(Settings.Load().ServerUrl));
        services.AddSingleton(_ => images.Result);
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
        services.AddSingleton<INewsService>(new MockNewsService());
        services.AddSingleton<IMissionsService>(new MockMissionsService(mock));
        services.AddSingleton<IFriendsService>(new MockFriendsService(mock));
        services.AddSingleton<IMessagesService>(new MockMessagesService(mock));
        services.AddSingleton<IProfileService>(sp => new MockProfileService(mock, sp.GetRequiredService<IFriendsService>()));
        services.AddScoped<ModalService>();
        services.AddScoped<ILanguageSwitcher, LauncherLanguageSwitcher>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    /// <summary>The pictures the website at <paramref name="server"/> lists at /images.json; none (the labelled boxes) without server_url, on an error, or after 5 s.</summary>
    internal static async Task<AssetImages> LoadImages(Uri? server)
    {
        if (server is null)
            return new AssetImages(new Dictionary<string, string>());
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var index = await http.GetFromJsonAsync<Dictionary<string, string>>(new Uri(server, "/images.json"));
            return new AssetImages(index ?? new Dictionary<string, string>(), server.GetLeftPart(UriPartial.Authority));
        }
        // IOException: the connection broke after the headers; InvalidOperationException: an unknown charset
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or JsonException or NotSupportedException or InvalidOperationException)
        {
            return new AssetImages(new Dictionary<string, string>());
        }
    }
}
