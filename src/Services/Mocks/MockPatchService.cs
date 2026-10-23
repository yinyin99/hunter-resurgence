namespace Resurgence.Services.Mocks;

public sealed class MockPatchService : IPatchService
{
    public Task<PatchStatus> StatusAsync() => Task.FromResult(new PatchStatus(PatchState.Ready, 100));
}
