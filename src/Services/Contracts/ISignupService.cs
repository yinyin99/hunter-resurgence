namespace Resurgence.Services;

/// <summary>The signup screens of the launcher: the username and avatar of a new account and its e-mail confirmation.</summary>
public interface ISignupService
{
    Task<bool> UsernameAvailableAsync(string name);
    /// <summary>Avatar picture ids, in carousel order.</summary>
    Task<IReadOnlyList<string>> AvatarsAsync();
    /// <summary>Five free names the dice button offers.</summary>
    Task<IReadOnlyList<string>> SuggestNamesAsync();
    Task<string> EmailAsync();
    Task<bool> EmailConfirmedAsync();
    /// <summary>Sends the confirmation e-mail again.</summary>
    Task<Result> ResendEmailAsync();
    /// <summary>Changes the address and sends the confirmation e-mail to it; fails with a key when the address is taken.</summary>
    Task<Result> ChangeEmailAsync(string email);

    // the pages the old launcher opened in the player's browser (they keep the query of the link they were opened with)
    /// <summary>Logs the browser in for the launcher login with this id.</summary>
    Task<Result> AutoLoginAsync(string id);
    /// <param name="kind">verify (a new account), confirm or change (a new address)</param>
    Task<Result> ConfirmEmailAsync(string kind, string? code, string? uuid, string? src);
    /// <summary>Hands the code of the thank-you link to the launcher.</summary>
    Task<Result> SharedDataAsync(string? code);
}
