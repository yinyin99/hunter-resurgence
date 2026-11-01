namespace Resurgence.Services.Mocks;

/// <summary>Without a community server there is nothing to restore into: every answer says so.</summary>
public sealed class MockRestoreService : IRestoreService
{
    public Task<RestoreStatus?> StatusAsync() => Task.FromResult<RestoreStatus?>(new(null, 0));

    public Task<RestoreResult> UploadAsync(Stream zip, long length) =>
        Task.FromResult(new RestoreResult("unavailable", "Restoring a backup needs the community server."));

    public Task<bool> DeleteAsync() => Task.FromResult(false);
}
