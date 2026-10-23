namespace Resurgence.Services;

public sealed record Session(string Handle, int HunterScore);

/// <summary>Sign-in is e-mail then one-time code, two steps.</summary>
public interface ISessionService
{
    Task<Session?> CurrentAsync();
    Task<Result> RequestCodeAsync(string email);
    Task<Result> SignInAsync(string email, string code);
    Task SignOutAsync();
}
