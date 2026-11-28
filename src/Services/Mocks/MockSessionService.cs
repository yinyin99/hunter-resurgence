namespace Resurgence.Services.Mocks;

/// <param name="mock">The development state, read when asked (the page reads it after this service is made): "membership" gives the signed-in player a membership, "discord:linked" a linked Discord account with 7080 hunterscore, "discord:veteran" one with 25000, "licence:owned", "licence:not_owned" and "licence:unknown" the server's Steam licence check result (<see cref="Session.SteamLicence"/>; the mock launcher has no game then).
/// The mock server has Linked Roles on, with the default Veteran threshold of 20000.</param>
public sealed class MockSessionService(Func<string?>? mock = null) : ISessionService
{
    Session? session = new("Hunter01", 74);

    public event Action? Changed;

    public string? DiscordLinkUrl => "/discord/link";

    public Task<Session?> CurrentAsync() => Task.FromResult(session is { Handle.Length: > 0 } ? session with { Membership = mock?.Invoke() == "membership", SteamLicence = mock?.Invoke() is { } m && m.StartsWith("licence:") ? m[8..] : null } with { DiscordLinked = mock?.Invoke() is "discord:linked" or "discord:veteran", DiscordVeteranAt = 20000 } with { HunterScore = mock?.Invoke() switch { "discord:linked" => 7080, "discord:veteran" => 25000, _ => session.HunterScore } } : session);

    // the mock account has accepted; development hook ?mock=eula (HostMode) takes the acceptance back
    bool licenseAccepted = true;

    public void RequireLicense() => licenseAccepted = false;

    public Task<bool> LicenseAcceptedAsync() => Task.FromResult(licenseAccepted);

    public Task AcceptLicenseAsync()
    {
        licenseAccepted = true;
        return Task.CompletedTask;
    }

    public Task<Result> RequestCodeAsync(string email) =>
        Task.FromResult(email.Contains('@') ? Result.Success : Result.Fail("Validation.Email.Invalid"));

    public Task<Result> SignInAsync(string email, string code, bool register = false)
    {
        if (code.Length != 6 || !code.All(char.IsAsciiDigit)) return Task.FromResult(Result.Fail("Validation.Code.Invalid"));
        session = register ? new("", 0) : new("Hunter01", 74);
        Changed?.Invoke();
        return Task.FromResult(Result.Success);
    }

    public Task SignOutAsync()
    {
        session = null;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>A name that starts with "race" is taken by someone else just before it is saved.</summary>
    public Task<Result> SetHandleAsync(string handle)
    {
        if (handle.StartsWith("race", StringComparison.OrdinalIgnoreCase)) return Task.FromResult(Result.Fail("Signup.P1.Taken"));
        session = new(handle, 0);
        Changed?.Invoke();
        return Task.FromResult(Result.Success);
    }

    public async Task<StartOutcome> StartAsync()
    {
        await Task.Delay(1200);
        return session is null ? StartOutcome.LoggedOut : session.Handle.Length == 0 ? StartOutcome.NeedsSignup : StartOutcome.LoggedIn;
    }
}
