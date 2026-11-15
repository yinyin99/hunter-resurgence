namespace Resurgence.Services;

/// <summary>The one-time offer of the restore page ("Do you have a backup of your profile?"), after the Discord invitation (docs/website.md).</summary>
public interface IRestoreOfferService
{
    /// <summary>True the first time it is asked for the signed-in account, false after or when the account has a restore, an upload or a claim; also false signed out or without an answer. Never throws.</summary>
    Task<bool> FirstAsync();
}
