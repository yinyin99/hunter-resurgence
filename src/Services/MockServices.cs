using Resurgence.Services;
using Resurgence.Services.Mocks;

namespace Microsoft.Extensions.DependencyInjection;

public static class MockServices
{
    /// <summary>Every service of the interface layer, backed by fixed fictional data. A host replaces single registrations with real ones later.</summary>
    public static IServiceCollection AddMockServices(this IServiceCollection s) => s
        .AddSingleton<ISessionService, MockSessionService>()
        .AddSingleton<ISignupService, MockSignupService>()
        .AddSingleton<IWalletService, MockWalletService>()
        .AddSingleton<IStoreService, MockStoreService>()
        .AddSingleton<IBankService, MockBankService>()
        .AddSingleton<IMissionsService, MockMissionsService>()
        .AddSingleton<ICompetitionsService, MockCompetitionsService>()
        .AddSingleton<ILeaderboardsService, MockLeaderboardsService>()
        .AddSingleton<IProfileService>(sp => new MockProfileService(null, sp.GetRequiredService<IFriendsService>()))
        .AddSingleton<IFriendsService, MockFriendsService>()
        .AddSingleton<IMessagesService, MockMessagesService>()
        .AddSingleton<IAccountService, MockAccountService>()
        .AddSingleton<IGameManagementService, MockGameManagementService>()
        .AddSingleton<INewsService, MockNewsService>()
        .AddSingleton<IHelpService, MockHelpService>()
        .AddSingleton<IServersService, MockServersService>()
        .AddSingleton<IPatchService, MockPatchService>()
        .AddSingleton<IGameSettingsService, MockGameSettingsService>()
        .AddSingleton<ILauncherBridge, MockLauncherBridge>();
}
