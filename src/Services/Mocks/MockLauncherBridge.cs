using Microsoft.Extensions.Logging;

namespace Resurgence.Services.Mocks;

public sealed class MockLauncherBridge(ILogger<MockLauncherBridge> log) : ILauncherBridge
{
    public bool IsSteam => true;
    public void OpenFolder(string kind) => log.LogInformation("open folder {Kind}", kind);
    public void ClearCache(string kind) => log.LogInformation("clear {Kind} cache", kind);
    public void OpenExternal(string url) => log.LogInformation("open {Url}", url);
    public void StartGame(string url) => log.LogInformation("start game {Url}", url);
}
