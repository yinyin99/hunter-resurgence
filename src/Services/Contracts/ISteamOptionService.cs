namespace Resurgence.Services;

/// <summary>What the launcher knows about the game's Steam launch option. <paramref name="Line"/>: the line that makes Steam open the launcher (the Setup's stable exe, else this exe for the Copy line only);
/// <paramref name="CanSet"/>: installed by the Setup and Steam's settings file for the game is found and readable; <paramref name="Current"/>: the option there now (null: none);
/// <paramref name="Ours"/>: it already is <paramref name="Line"/>; <paramref name="Installed"/> tells a dev run or unpacked launcher (copy only) from a file that cannot be read.</summary>
public sealed record SteamOptionInfo(string Line, bool Installed, bool CanSet, string? Current, bool Ours, bool Running);

/// <summary>Steam's Play button opens the launcher (docs/launcher.md#start-it-from-steam): the launcher writes the launch option itself, with the player's OK.</summary>
public interface ISteamOptionService
{
    Task<SteamOptionInfo> ReadAsync();

    /// <summary>Asks a running Steam to exit and waits (never kills it), then writes the option. A failure's <see cref="Result.Error"/> is a message for the player.</summary>
    Task<Result> SetAsync();

    /// <summary>Starts Steam again after <see cref="SetAsync"/>; false when it does not start.</summary>
    bool StartSteam();

    /// <summary>At the launcher's start: true when the dialog should open now (the option is not ours, the player did not say "don't ask again" and it was not offered in the last day); counts as offered.</summary>
    Task<bool> DueAsync();

    /// <summary>The player chose "Not now" with "Don't ask again".</summary>
    void NotAgain();
}
