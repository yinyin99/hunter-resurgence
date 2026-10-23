namespace Resurgence.Services.Mocks;

public sealed class MockProfileService : IProfileService
{
    public Task<Profile?> ProfileAsync(string handle) => Task.FromResult<Profile?>(new(handle, 74, 12));
    public Task<Progress> ProgressAsync() => Task.FromResult(new Progress(74, 100, 12, new("Achievement One", "Placeholder description of the achievement.", new(2026, 7, 9))));
    public Task<int> ExpeditionCountAsync() => Task.FromResult(42);
}
