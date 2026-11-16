using System.IO.Compression;

namespace Resurgence.Services.Mocks;

/// <summary>Without a community server there is nothing to restore into, but the restore page can be tried: an upload is judged by the entry names of the zip, and what it
/// came to stays until the site restarts. <c>manifest.json</c>: kept (ours); <c>index.json</c>: kept (another tool's); a <c>.py</c>, <c>.jpg</c> or <c>.zip</c> entry: refused
/// as a program, pictures or a nested backup; <c>mock-restored</c> / <c>mock-rejected</c>: the operator has already decided; a zip of anything else: refused; not a zip: unavailable.</summary>
public sealed class MockRestoreService : IRestoreService
{
    readonly List<KeptZip> review = [];
    readonly List<RestoreDecision> decisions = [];
    long? restoredId;
    ArchiveClaim? claim;

    static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public Task<RestoreStatus?> StatusAsync() => Task.FromResult<RestoreStatus?>(new(restoredId, [.. review], [.. decisions], review.Count < 3));

    public async Task<RestoreResult> UploadAsync(Stream zip, long length, IProgress<long>? progress = null)
    {
        var copy = new MemoryStream();
        await zip.CopyToAsync(copy);
        string[] names;
        try
        {
            using var archive = new ZipArchive(copy, ZipArchiveMode.Read);
            names = [.. archive.Entries.Select(e => e.FullName.ToLowerInvariant())];
        }
        catch (InvalidDataException)
        {
            return new("unavailable", "Restoring a backup needs the community server.");
        }
        for (var i = 1; i <= 5; i++) { await Task.Delay(250); progress?.Report(length * i / 5); }
        bool Has(string end) => names.Any(n => n.EndsWith(end));
        string? why = Has(".zip") ? "Your backup is inside another folder or zip: upload the backup zip itself"
            : Has(".py") ? "This zip holds a program, not a backup: upload the file the program made (it ends in _PRIVATE.zip)"
            : Has(".jpg") ? "This zip holds only pictures, not a backup"
            : Has("manifest.json") || Has("index.json") || Has("mock-restored") || Has("mock-rejected") ? null
            : "This file cannot be read as a backup: not a backup zip. Nothing was changed.";
        if (why is not null)
        {
            decisions.Add(new(Now, "refused", null, null, false, why));
            return new("invalid", why);
        }
        if (Has("mock-restored")) { restoredId = 1001; decisions.Add(new(Now, "restored", 1001, "Hunter01", false, "")); }
        else if (Has("mock-rejected")) decisions.Add(new(Now, "rejected", 7, null, false, "This is not your backup: it was made for another Steam account."));
        else review.Add(new(Now, "waiting for the operator", Has("manifest.json") ? "ours" : "other"));
        return new("review", "Received. An operator will check it. Nothing was restored yet.");
    }

    public Task<bool> DeleteAsync() => Task.FromResult(false);

    public Task<ArchiveStatus?> ArchiveStatusAsync() => Task.FromResult<ArchiveStatus?>(new(true, claim));

    public Task<ArchiveFind> FindArchivedAsync(string name) => Task.FromResult(name.Trim().ToLowerInvariant() == "oldhunter"
        ? new ArchiveFind("found", new("OldHunter", 1234, 40, 12, 3, true, true, false, true))
        : new ArchiveFind("notfound"));

    public Task<RestoreResult> ClaimArchivedAsync(string oldName, bool useOldName)
    {
        claim = new("waiting", oldName, Now);
        return Task.FromResult(new RestoreResult("review", ""));
    }
}
