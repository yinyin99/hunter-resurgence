namespace Resurgence.Services.Mocks;

/// <summary>The development website's sign-in pages (no server): the name step by default; mock "signin:restore" the name step with the restore offer chosen and its error, "signin:confirm" the step
/// of a launcher that shows a code, "signin:app" the step of an app on this PC, "signin:none" a sign-in that ran out of time.</summary>
public sealed class MockSignInService(string? mock = null) : ISignInService
{
    public Task<SignInStep?> PendingAsync() => Task.FromResult<SignInStep?>(mock switch
    {
        "signin:none" => null,
        "signin:confirm" => new("confirm", "mock", Handle: "Hunter01", Code: "AB12-CD34"),
        "signin:app" => new("app", "mock", Handle: "Hunter01"),
        "signin:restore" => new("handle", "mock", Offer: true, Handle: "Hunter01", Old: "OldHunter", Restore: true, Error: "unknown"),
        _ => new("handle", "mock", Offer: true),
    });
}
