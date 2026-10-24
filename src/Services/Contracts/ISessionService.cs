namespace Resurgence.Services;

public sealed record Session(string Handle, int HunterScore);

/// <summary>Where the launcher goes at start: on to the game, to the username step of a new account, or to the sign-in.</summary>
public enum StartOutcome { LoggedIn, NeedsSignup, LoggedOut }

/// <summary>Sign-in is e-mail then one-time code, two steps. A session whose Handle is empty is an account that has not picked its username yet.</summary>
public interface ISessionService
{
    Task<Session?> CurrentAsync();
    /// <summary>Raised after a sign-in or sign-out so the shell can switch between the public and the signed-in bar.</summary>
    event Action? Changed;
    Task<Result> RequestCodeAsync(string email);
    /// <param name="register">The account is new: the session has no handle yet.</param>
    Task<Result> SignInAsync(string email, string code, bool register = false);
    Task<Result> SetHandleAsync(string handle);
    Task<StartOutcome> StartAsync();
    Task SignOutAsync();
    /// <summary>Accounts older than the current agreements have to accept them once before the feed shows; false until they have.</summary>
    Task<bool> LicenseAcceptedAsync();
    Task AcceptLicenseAsync();
}
