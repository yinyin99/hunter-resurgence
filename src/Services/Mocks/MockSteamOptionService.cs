namespace Resurgence.Services.Mocks;

/// <summary>The website and the mock launcher change nothing in Steam: the dialog shows a sample line and Set succeeds.</summary>
public sealed class MockSteamOptionService : ISteamOptionService
{
    public Task<SteamOptionInfo> ReadAsync() => Task.FromResult(new SteamOptionInfo("\"C:\\Launcher\\launcher.exe\" %command%", true, true, null, false, false));
    public Task<(Result Result, bool Restarted)> SetAsync() => Task.FromResult((Result.Success, false));
    public Task<bool> DueAsync() => Task.FromResult(false);
    public void NotAgain() { }
}
