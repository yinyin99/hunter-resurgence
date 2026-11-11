using System.Diagnostics;
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
    public static ServiceProvider Build(string? mock, Uri? server)
    {
        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
        services.AddUiLocalization(Branding.Load(Settings.FilePath));
        // the operator's pictures from the website at server_url (docs/images.md): the list is fetched while the window starts; the first slot waits for it
        var images = Task.Run(() => LoadImages(server));
        services.AddSingleton(_ => images.Result);
        services.AddMockServices();
        services.AddSingleton<LauncherEnv>();
        services.AddSingleton<ILauncherBridge, LauncherBridge>();
        services.AddSingleton(new HostMode { Launcher = true, Mock = mock, Live = server is not null });
        // live: the launcher has no patch source yet, so the panel always reports ready
        services.AddSingleton<IPatchService>(new MockPatchService(server is null ? mock : null));
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
        if (server is not null) AddServerServices(services, server);
        services.AddScoped<ModalService>();
        services.AddScoped<ILanguageSwitcher, LauncherLanguageSwitcher>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    /// <summary>Opens the server's sign-in in the player's browser; without a browser the player still has the code on screen.</summary>
    static void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    /// <summary>With server_url the pages are the community server's, as on the website (src/Web/Program.cs); one client and one cookie for the launcher (sign-in fills the cookie).</summary>
    static void AddServerServices(IServiceCollection services, Uri server)
    {
        services.AddSingleton<ServerCookie>();
        services.AddSingleton(new HttpClient(new SocketsHttpHandler { UseCookies = false }) { BaseAddress = server, Timeout = TimeSpan.FromSeconds(5) });
        services.AddSingleton<ISessionService>(sp => new LauncherSessionService(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ServerCookie>(),
            Path.Combine(Path.GetDirectoryName(Prefs.DefaultPath)!, "session.dat"), OpenBrowser, sp.GetRequiredService<ILogger<ServerSessionService>>()));
        services.AddSingleton<ServerCache<ServerStore>>();
        services.AddSingleton<ILeaderboardsService>(sp => new ServerLeaderboardsService(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ServerCookie>(), sp.GetRequiredService<ILogger<ServerLeaderboardsService>>()));
        services.AddSingleton<IFriendsService>(sp => new ServerFriendsService(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ServerCookie>(), sp.GetRequiredService<ILogger<ServerFriendsService>>()));
        services.AddSingleton<IMessagesService>(sp => new ServerMessagesService(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ServerCookie>(), sp.GetRequiredService<ILogger<ServerMessagesService>>()));
        services.AddSingleton<IProfileService>(sp => new ServerProfileService(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ServerCookie>(), sp.GetRequiredService<ISessionService>(),
            new MockProfileService(sp.GetRequiredService<HostMode>().Mock, sp.GetRequiredService<IFriendsService>()), sp.GetRequiredService<ILogger<ServerProfileService>>(), sp.GetRequiredService<IFriendsService>()));
        services.AddSingleton<IWalletService>(sp => new ServerWalletService(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ServerCookie>(),
            new MockWalletService(sp.GetRequiredService<MockWalletState>(), sp.GetRequiredService<HostMode>().Mock), sp.GetRequiredService<ILogger<ServerWalletService>>()));
        services.AddSingleton<IMissionsService>(sp => new ServerMissionsService(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ServerCookie>(), sp.GetRequiredService<ILogger<ServerMissionsService>>()));
        services.AddSingleton<IStoreService>(sp => new ServerStoreService(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ServerCookie>(), sp.GetRequiredService<ServerCache<ServerStore>>(), sp.GetRequiredService<ILogger<ServerStoreService>>()));
        // a player's backup upload takes longer than the 5 s of the other calls
        services.AddSingleton<IRestoreService>(sp => new ServerRestoreService(new HttpClient(new SocketsHttpHandler { UseCookies = false }) { BaseAddress = server, Timeout = TimeSpan.FromMinutes(10) },
            sp.GetRequiredService<ServerCookie>(), sp.GetRequiredService<ILogger<ServerRestoreService>>()));
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
