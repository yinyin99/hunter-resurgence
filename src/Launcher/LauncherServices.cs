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
        var brand = Branding.Load(Settings.FilePath);
        services.AddUiLocalization(brand);
        // the operator's pictures from the website at server_url (docs/images.md): the list is fetched while the window starts; the first slot waits for it
        var images = Task.Run(() => LoadImages(server));
        services.AddSingleton(_ => images.Result);
        services.AddMockServices();
        services.AddSingleton<LauncherEnv>();
        services.AddSingleton<ILauncherBridge, LauncherBridge>();
        services.AddSingleton(new HostMode { Launcher = true, Mock = mock, Live = server is not null });
        // installed with Velopack: the launcher's own update drives the patch screens; started otherwise (a dev run, the tests) the panel always reports ready
        var updater = mock is null ? LauncherUpdater.Create() : null;
        if (updater is not null) { services.AddSingleton(updater); _ = updater.CheckAsync(); }
        services.AddSingleton<IPatchService>(updater ?? (IPatchService)new MockPatchService(server is null ? mock : null));
        // the player kit: setup, removal and the forwarder (live only with an https server_url)
        services.AddSingleton<IKitService>(new LauncherKit(server));
        // the first start after the install looks for what an older setup left (docs/launcher.md#install-and-uninstall)
        services.AddSingleton<ICleanupService>(sp => new LauncherCleanup(() => sp.GetRequiredService<LauncherEnv>().Install, Environment.ProcessPath, Program.FirstRun));
        // Steam's Play button opens the launcher: the launch option, written with the player's OK (docs/launcher.md#start-it-from-steam)
        services.AddSingleton<ISteamOptionService>(sp => mock is null ? new LauncherSteamOption(sp.GetRequiredService<LauncherEnv>(), SteamOption.StableExe) : new MockSteamOptionService());
        services.AddSingleton<IGameSettingsService>(sp => mock == "settings:first" ? new MockGameSettingsService(sp.GetRequiredService<ILogger<MockGameSettingsService>>(), mock)
            : new GameSettingsService(sp.GetRequiredService<LauncherEnv>().Settings, sp.GetRequiredService<LauncherEnv>().Prefs, GameFiles.AppData, sp.GetRequiredService<LauncherEnv>().Save));
        services.AddSingleton<IWalletService>(sp => new MockWalletService(sp.GetRequiredService<MockWalletState>(), mock));
        services.AddSingleton<IStoreService>(sp => new MockStoreService(sp.GetRequiredService<MockWalletState>(), mock));
        services.AddSingleton<IBankService>(sp => new MockBankService(sp.GetRequiredService<MockWalletState>(), mock));
        services.AddSingleton<ISignupService>(new MockSignupService(mock));
        services.AddSingleton<IAccountService>(new MockAccountService(mock));
        services.AddSingleton<ISeasonalEventsService>(new MockSeasonalEventsService(mock));
        services.AddSingleton<IDiscordInviteService>(new MockDiscordInviteService());
        // the feed's Discord frame for link_discord, as on the website (docs/website.md); without the link there is none
        var discord = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        discord.DefaultRequestHeaders.UserAgent.ParseAdd("hunter-resurgence");
        services.AddSingleton<INewsService>(sp => new DiscordNewsService(discord, brand.Link("discord"), new(), new(), sp.GetRequiredService<ILogger<DiscordNewsService>>()));
        services.AddSingleton<IMissionsService>(new MockMissionsService(mock));
        services.AddSingleton<IFriendsService>(new MockFriendsService(mock));
        services.AddSingleton<IMessagesService>(new MockMessagesService(mock));
        services.AddSingleton<IProfileService>(sp => new MockProfileService(mock, sp.GetRequiredService<IFriendsService>()));
        if (server is not null) AddServerServices(services, server);
        services.AddScoped<ModalService>();
        services.AddScoped<ILanguageSwitcher, LauncherLanguageSwitcher>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    /// <summary>Opens the server's sign-in in the player's browser; without a browser the sign-in waits for its time-out or the player's cancel.</summary>
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
        services.AddSingleton<ISeasonalEventsService>(sp => new ServerSeasonalEventsService(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ServerCookie>(), sp.GetRequiredService<ILogger<ServerSeasonalEventsService>>()));
        services.AddSingleton<IDiscordInviteService>(sp => new ServerDiscordInviteService(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ServerCookie>(), sp.GetRequiredService<ILogger<ServerDiscordInviteService>>()));
        // the avatar (settings, sign-up) is the server's; the rest of the account settings is the mock's, which the live pages do not offer
        services.AddSingleton<IAccountService>(sp => new ServerAccountService(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ServerCookie>(),
            new MockAccountService(sp.GetRequiredService<HostMode>().Mock), sp.GetRequiredService<ILogger<ServerAccountService>>()));
        services.AddSingleton<ISignupService>(sp => new ServerSignupService(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ServerCookie>(),
            new MockSignupService(sp.GetRequiredService<HostMode>().Mock), sp.GetRequiredService<ILogger<ServerSignupService>>()));
        services.AddSingleton<IInventoryService>(sp => new ServerInventoryService(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ServerCookie>(), sp.GetRequiredService<ILogger<ServerInventoryService>>()));
        services.AddSingleton<IStoreService>(sp => new ServerStoreService(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ServerCookie>(), sp.GetRequiredService<ServerCache<ServerStore>>(), sp.GetRequiredService<IInventoryService>(), sp.GetRequiredService<ILogger<ServerStoreService>>()));
        // the launch map: the server's reserves and starts, the player's loadouts; a start opens the hunt there and gives the game its launch URL
        services.AddSingleton<ServerCache<ServerLaunchCatalogue>>();
        services.AddSingleton<IServersService>(sp => new ServerServersService(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ServerCookie>(),
            sp.GetRequiredService<ServerCache<ServerLaunchCatalogue>>(), sp.GetRequiredService<ILauncherBridge>(), sp.GetRequiredService<ILogger<ServerServersService>>()));
        // a player's backup upload takes longer than the 5 s of the other calls
        services.AddSingleton<IRestoreService>(sp => new ServerRestoreService(new HttpClient(new SocketsHttpHandler { UseCookies = false }) { BaseAddress = server, Timeout = TimeSpan.FromMinutes(10) },
            sp.GetRequiredService<ServerCookie>(), sp.GetRequiredService<ILogger<ServerRestoreService>>()));
    }

    /// <summary>The pictures the website at <paramref name="server"/> lists at /images.json; none (the labelled boxes) without server_url, on an error, or after <paramref name="timeout"/> (5 s; only a test passes another).</summary>
    internal static async Task<AssetImages> LoadImages(Uri? server, TimeSpan? timeout = null)
    {
        if (server is null)
            return new AssetImages(new Dictionary<string, string>());
        try
        {
            using var http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(5) };
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
