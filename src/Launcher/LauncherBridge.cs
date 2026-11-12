using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Resurgence.Services;
using Resurgence.Services.Mocks;

namespace Resurgence.Launcher;

/// <summary>The launcher's bridge: StartGame is real, the rest is still the mock's.</summary>
public sealed class LauncherBridge(Func<GameInstall?> install, ILauncherBridge rest, Func<string, bool> isRunning, Func<ProcessStartInfo, bool> start, Func<bool>? restart = null) : ILauncherBridge
{
    public LauncherBridge(LauncherEnv env, ILogger<MockLauncherBridge> log, LauncherUpdater? updater = null) : this(() => env.Install, new MockLauncherBridge(log),
        GameFiles.ClientRunning, Start, updater is null ? null : updater.ApplyAndRestart)
    { }

    /// <summary>The client's arguments: the server, a random number with bit 128 clear, and the Steam flag.</summary>
    public static string Arguments(string url, int param) => $"/connect:{url} /param:{param & int.MaxValue & ~128} /steam";

    public bool IsSteam => rest.IsSteam;
    public void OpenFolder(string kind) => rest.OpenFolder(kind);
    public void ClearCache(string kind) => rest.ClearCache(kind);
    public void OpenExternal(string url) => rest.OpenExternal(url);
    public void ToggleDevTools() => rest.ToggleDevTools();
    /// <summary>The launcher has updated itself: apply the downloaded update and start the new one (never while the game runs).</summary>
    public void RestartLauncher() { if (restart?.Invoke() != true) rest.RestartLauncher(); }

    public Result StartGame(string url)
    {
        if (install() is not { } g || !File.Exists(g.ClientExe)) return Result.Fail("Launch.NoGame");
        if (url.Any(c => char.IsWhiteSpace(c) || c == '"')) return Result.Fail("Launch.Failed");
        if (isRunning(g.ClientExe)) return Result.Fail("Launch.AlreadyRunning");
        // the exe's own folder: the client finds its data and writes its logs relative to the working folder
        return start(new(g.ClientExe, Arguments(url, Random.Shared.Next())) { WorkingDirectory = Path.GetDirectoryName(g.ClientExe) }) ? Result.Success : Result.Fail("Launch.StartFailed");
    }

    static bool Start(ProcessStartInfo i)
    {
        try { using var p = Process.Start(i); return p is not null; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }
}
