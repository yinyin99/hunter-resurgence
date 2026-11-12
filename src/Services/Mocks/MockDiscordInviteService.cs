namespace Resurgence.Services.Mocks;

/// <summary>The first call is the first time; kept in memory.</summary>
public sealed class MockDiscordInviteService : IDiscordInviteService
{
    bool asked;

    public Task<bool> FirstAsync() => Task.FromResult(!Interlocked.Exchange(ref asked, true));
}
