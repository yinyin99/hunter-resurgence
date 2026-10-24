namespace Resurgence.Services.Mocks;

/// <summary>names starting with "taken" are in use and taken@... the one address. Mock "signup:confirmed" is an account whose e-mail is already confirmed.</summary>
public sealed class MockSignupService(string? mock = null) : ISignupService
{
    string email = "hunter@example.com";

    public Task<bool> UsernameAvailableAsync(string name) => Task.FromResult(!name.StartsWith("taken", StringComparison.OrdinalIgnoreCase));

    public Task<IReadOnlyList<string>> AvatarsAsync() =>
        Task.FromResult<IReadOnlyList<string>>(Enumerable.Range(1, 8).Select(i => $"avatar-{i:00}").ToList());

    public Task<IReadOnlyList<string>> SuggestNamesAsync() =>
        Task.FromResult<IReadOnlyList<string>>(["QuietBuck_71", "WildFalcon", "GreyFox_09", "SilentOak", "NorthWind_3"]);

    public Task<string> EmailAsync() => Task.FromResult(email);

    public Task<bool> EmailConfirmedAsync() => Task.FromResult(mock == "signup:confirmed");

    public Task<Result> ResendEmailAsync() => Task.FromResult(Result.Success);

    // the code or id "bad" is the failing one, whatever else is asked
    public Task<Result> AutoLoginAsync(string id) => Task.FromResult(id == "bad" ? Result.Fail("Browser.Failed") : Result.Success);

    public Task<Result> ConfirmEmailAsync(string kind, string? code, string? uuid, string? src) => Task.FromResult(code == "bad" ? Result.Fail("Browser.Failed") : Result.Success);

    public Task<Result> SharedDataAsync(string? code) => Task.FromResult(code == "bad" ? Result.Fail("Browser.Failed") : Result.Success);

    public Task<Result> ChangeEmailAsync(string address)
    {
        if (address.StartsWith("taken@", StringComparison.OrdinalIgnoreCase)) return Task.FromResult(Result.Fail("Dialog.EmailTaken"));
        email = address;
        return Task.FromResult(Result.Success);
    }
}
