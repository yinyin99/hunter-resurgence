namespace Resurgence.Services;

/// <summary>The sign-in in progress in this browser, as the community server keeps it (<see cref="ServerSessionApi.Pending"/>): the website draws the pages of its steps, the server does the sign-in.</summary>
/// <param name="Step">handle (the first sign-in picks a name), confirm (a launcher shows <paramref name="Code"/>) or app (an app on this PC asked).</param>
/// <param name="Csrf">The token the step's form posts back; the server refuses a post without it.</param>
/// <param name="Offer">handle: the restore of an old profile is offered.</param>
/// <param name="Handle">handle: the name typed last, the account's own name at the other steps.</param>
/// <param name="Old">handle: the old hunter name typed last.</param>
/// <param name="Restore">handle: the player chose "I played before" last.</param>
/// <param name="Error">handle: why the last form was refused (invalid, taken, unknown, old-taken); null without one.</param>
public sealed record SignInStep(string Step, string Csrf, bool Offer = false, string Handle = "", string Old = "", bool Restore = false, string? Error = null, string? Code = null);

public interface ISignInService
{
    /// <summary>The step of this browser's sign-in, or null when it ran out of time, never started or the server does not answer.</summary>
    Task<SignInStep?> PendingAsync();
}
