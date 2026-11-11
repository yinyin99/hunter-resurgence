namespace Resurgence.Services;

/// <param name="Membership">The player has a membership: the single player page then leaves out its introduction.</param>
public sealed record Session(string Handle, int HunterScore, bool Membership = false);

/// <summary>Where the launcher goes at start: on to the game, to the username step of a new account, or to the sign-in.</summary>
public enum StartOutcome { LoggedIn, NeedsSignup, LoggedOut }

/// <summary>Sign-in is e-mail then one-time code, two steps. A session whose Handle is empty is an account that has not picked its username yet.</summary>
public interface ISessionService
{
    Task<Session?> CurrentAsync();
    /// <summary>Raised after a sign-in or sign-out so the shell can switch between the public and the signed-in bar.</summary>
    event Action? Changed;
    /// <summary>Set when signing in is a page load of the community server (the website with SERVER_URL): where to go, to come back to the local path <paramref name="next"/>. Null: sign in with the e-mail methods below.</summary>
    string? SignInUrl(string next) => null;
    /// <summary>True when signing in is the server's code link (<see cref="SignInWithLinkAsync"/>) and not the e-mail methods below.</summary>
    bool CodeLink => false;
    /// <summary>Signs in through the player's browser: <paramref name="showCode"/> gets the code to compare there, the call ends when the player has confirmed (a session now exists), or fails.</summary>
    Task<Result> SignInWithLinkAsync(Action<string> showCode) => Task.FromResult(Result.Fail("Signin.LinkFailed"));
    /// <summary>Ends a <see cref="SignInWithLinkAsync"/> that still waits (the player left the sign-in).</summary>
    void CancelLink() { }
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
