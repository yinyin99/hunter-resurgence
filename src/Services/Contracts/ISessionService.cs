namespace Resurgence.Services;

/// <param name="Membership">The player has a membership: the single player page then leaves out its introduction.</param>
/// <param name="Avatar">The slot or link of the player's picture (<see cref="Avatars.Slot"/>).</param>
/// <param name="DiscordLinked">Whether the hunter has linked a Discord account (Linked Roles): the pages offer the link only for false; null is a server without Linked Roles.</param>
/// <param name="Imperial">The player chose imperial units (yards, pounds): the pages show them as the game does (<see cref="IAccountService.SetImperialAsync"/>).</param>
/// <param name="DiscordVeteranAt">The HunterScore of the Discord server's "Veteran" role (the server's `discord_veteran_at`); null is a server without Linked Roles.</param>
public sealed record Session(string Handle, int HunterScore, bool Membership = false, string Avatar = Avatars.Default, bool Imperial = false, bool? DiscordLinked = null, int? DiscordVeteranAt = null);

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
    /// <summary>Where a player links Discord (the community server's <c>/discord/link</c>): a path of the website, or the whole address when the launcher opens it in the browser. Null: nothing to offer.</summary>
    string? DiscordLinkUrl => null;
    /// <summary>True when signing in is the server's, through the player's browser (<see cref="SignInWithBrowserAsync"/>), and not the e-mail methods below.</summary>
    bool BrowserSignIn => false;
    /// <summary>Signs in through the player's browser and ends when the player has signed in there (a session now exists), cancelled, or ran out of time. A cancel is <c>new Result(false)</c>: no message.</summary>
    Task<Result> SignInWithBrowserAsync() => Task.FromResult(Result.Fail("Signin.LinkFailed"));
    /// <summary>Ends a <see cref="SignInWithBrowserAsync"/> that still waits (the player cancelled or left the sign-in).</summary>
    void CancelBrowserSignIn() { }
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
