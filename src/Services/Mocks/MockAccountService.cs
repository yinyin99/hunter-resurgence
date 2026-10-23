using Microsoft.Extensions.Logging;

namespace Resurgence.Services.Mocks;

public sealed class MockAccountService(ILogger<MockAccountService> log) : IAccountService
{
    public Task<Result> SaveAsync(string tab, IReadOnlyDictionary<string, string> values)
    {
        log.LogInformation("save account settings tab {Tab}", tab);
        return Task.FromResult(Result.Success);
    }
}
