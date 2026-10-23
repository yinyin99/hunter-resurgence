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
}
