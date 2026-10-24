namespace Resurgence.Services.Mocks;

public sealed class MockSessionService : ISessionService
{
    Session? session = new("Hunter01", 74);

    public event Action? Changed;

    public Task<Session?> CurrentAsync() => Task.FromResult(session);

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
