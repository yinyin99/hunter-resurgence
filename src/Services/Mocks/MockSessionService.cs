namespace Resurgence.Services.Mocks;

public sealed class MockSessionService : ISessionService
{
    Session? session = new("Hunter01", 74);

    public Task<Session?> CurrentAsync() => Task.FromResult(session);

    public Task<Result> RequestCodeAsync(string email) =>
        Task.FromResult(email.Contains('@') ? Result.Success : Result.Fail("Validation.Email.Invalid"));

    public Task<Result> SignInAsync(string email, string code)
    {
        if (code.Length != 6 || !code.All(char.IsAsciiDigit)) return Task.FromResult(Result.Fail("Validation.Code.Invalid"));
        session = new("Hunter01", 74);
        return Task.FromResult(Result.Success);
    }

    public Task SignOutAsync()
    {
        session = null;
        return Task.CompletedTask;
    }
}
