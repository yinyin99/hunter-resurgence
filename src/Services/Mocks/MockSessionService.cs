namespace Resurgence.Services.Mocks;

public sealed class MockSessionService : ISessionService
{
    Session? session = new("Hunter01", 74);

    public event Action? Changed;

    public Task<Session?> CurrentAsync() => Task.FromResult(session);

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

    public Task<Result> SetHandleAsync(string handle)
    {
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
