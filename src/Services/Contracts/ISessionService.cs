namespace Resurgence.Services;

public sealed record Session(string Handle, int HunterScore);

/// <summary>Sign-in is e-mail then one-time code, two steps.</summary>
public interface ISessionService
{
    Task<Session?> CurrentAsync();
    /// <summary>Raised after a sign-in or sign-out so the shell can switch between the public and the signed-in bar.</summary>
    event Action? Changed;
    Task<Result> RequestCodeAsync(string email);
    Task<Result> SignInAsync(string email, string code);
    Task SignOutAsync();
}
