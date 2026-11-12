namespace Resurgence.Services;

/// <summary>The one-time invitation to the Discord, after the player's first sign-in (docs/website.md).</summary>
public interface IDiscordInviteService
{
    /// <summary>True the first time it is asked for the signed-in account, false after; also false signed out or without an answer. Never throws.</summary>
    Task<bool> FirstAsync();
}
