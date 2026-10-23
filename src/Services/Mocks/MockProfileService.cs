namespace Resurgence.Services.Mocks;

public sealed class MockProfileService : IProfileService
{
    public Task<Profile?> ProfileAsync(string handle) => Task.FromResult<Profile?>(new(handle, 74, 12));
    public Task<int> ExpeditionCountAsync() => Task.FromResult(42);
}
