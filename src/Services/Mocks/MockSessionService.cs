namespace Resurgence.Services.Mocks;

/// <param name="mock">Development state "membership": the signed-in player has a membership.</param>
public sealed class MockSessionService(string? mock = null) : ISessionService
{
    Session? session = new("Hunter01", 74, mock == "membership");

    public event Action? Changed;

    public Task<Session?> CurrentAsync() => Task.FromResult(session);

    public Task<Result> RequestCodeAsync(string email) =>
        Task.FromResult(email.Contains('@') ? Result.Success : Result.Fail("Validation.Email.Invalid"));

    public Task<Result> SignInAsync(string email, string code, bool register = false)
    {
        if (code.Length != 6 || !code.All(char.IsAsciiDigit)) return Task.FromResult(Result.Fail("Validation.Code.Invalid"));
        session = register ? new("", 0) : new("Hunter01", 74, mock == "membership");
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
