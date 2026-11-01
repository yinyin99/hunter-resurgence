namespace Resurgence.Services;

/// <param name="Status">What became of an upload: <c>restored</c>, <c>review</c> (kept for the operator, nothing restored), <c>already</c>, <c>taken</c>, <c>invalid</c>, <c>busy</c>, <c>too_large</c>, <c>signed_out</c>, or <c>unavailable</c> (no community server). The page shows a localised headline for it.</param>
/// <param name="Message">The server's own plain words (English), shown under the headline.</param>
/// <param name="Report">The technical report of a restore (what was read, stored and dropped), or null.</param>
public sealed record RestoreResult(string Status, string Message, string? Report = null);

/// <param name="RestoredUserId">The backup's user id this account restored (once), or null.</param>
/// <param name="Review">How many zips are kept for the operator to look at.</param>
public sealed record RestoreStatus(long? RestoredUserId, int Review);

/// <summary>The player's own backup and data on the community server (docs/server.md#upload-a-backup-zip).</summary>
public interface IRestoreService
{
    /// <summary>Null: signed out, or the server did not answer.</summary>
    Task<RestoreStatus?> StatusAsync();

    /// <summary>Send the backup zip. Never throws for a refusal or a network failure: the result says.</summary>
    Task<RestoreResult> UploadAsync(Stream zip, long length);

    /// <summary>Delete the account and all its data. True when the server did.</summary>
    Task<bool> DeleteAsync();
}
