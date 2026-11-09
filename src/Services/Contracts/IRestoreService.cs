namespace Resurgence.Services;

/// <param name="Status">What became of an upload: <c>restored</c>, <c>review</c> (kept for the operator, nothing restored), <c>already</c>, <c>taken</c>, <c>invalid</c>, <c>busy</c>, <c>too_large</c>, <c>signed_out</c>, or <c>unavailable</c> (no community server). The page shows a localised headline for it.</param>
/// <param name="Message">The server's own plain words (English), shown under the headline.</param>
/// <param name="Report">The technical report of a restore (what was read, stored and dropped), or null.</param>
public sealed record RestoreResult(string Status, string Message, string? Report = null);

/// <param name="Ts">Unix seconds the zip was kept.</param>
/// <param name="Note">Where it stands, in the server's English: "waiting for the operator", or the reason the operator's approve was refused.</param>
public sealed record KeptZip(long Ts, string Note);

/// <param name="Ts">Unix seconds of the decision.</param>
/// <param name="Kind"><c>restored</c> or <c>rejected</c>.</param>
/// <param name="UserId">The backup's user id.</param>
/// <param name="Handle">Restored: the account's name now.</param>
/// <param name="HandleKept">Restored: the account kept its own name (the backup's was not adopted).</param>
/// <param name="Reason">Rejected: the operator's reason in their words, possibly empty.</param>
public sealed record RestoreDecision(long Ts, string Kind, long? UserId, string? Handle, bool HandleKept, string Reason);

/// <param name="RestoredUserId">The backup's user id this account restored (once), or null.</param>
/// <param name="Review">The zips kept for the operator, oldest first.</param>
/// <param name="Decisions">What the operator decided about the others, oldest first.</param>
public sealed record RestoreStatus(long? RestoredUserId, IReadOnlyList<KeptZip> Review, IReadOnlyList<RestoreDecision> Decisions);

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
