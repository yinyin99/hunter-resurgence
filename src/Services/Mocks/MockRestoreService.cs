namespace Resurgence.Services.Mocks;

/// <summary>Without a community server there is nothing to restore into: every answer says so.</summary>
public sealed class MockRestoreService : IRestoreService
{
    public Task<RestoreStatus?> StatusAsync() => Task.FromResult<RestoreStatus?>(new(null, [], []));

    public Task<RestoreResult> UploadAsync(Stream zip, long length) =>
        Task.FromResult(new RestoreResult("unavailable", "Restoring a backup needs the community server."));

    public Task<bool> DeleteAsync() => Task.FromResult(false);

    public Task<ArchiveStatus?> ArchiveStatusAsync() => Task.FromResult<ArchiveStatus?>(new(true, null));

    public Task<ArchiveFind> FindArchivedAsync(string name) => Task.FromResult(name.Trim().ToLowerInvariant() == "oldhunter"
        ? new ArchiveFind("found", new("OldHunter", 1234, 40, 12, 3, true, true, false, true))
        : new ArchiveFind("notfound"));

    public Task<RestoreResult> ClaimArchivedAsync(string oldName, bool useOldName) => Task.FromResult(new RestoreResult("restored", ""));
}
