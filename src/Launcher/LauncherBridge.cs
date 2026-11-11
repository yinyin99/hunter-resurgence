using System.Diagnostics;
using System.Windows;
using Microsoft.Extensions.Logging;
using Resurgence.Services;
using Resurgence.Services.Mocks;

namespace Resurgence.Launcher;

/// <summary>The launcher's bridge: StartGame is real, the rest is still the mock's.</summary>
public sealed class LauncherBridge(Func<GameInstall?> install, ILauncherBridge rest, Func<string, bool> isRunning, Func<ProcessStartInfo, bool> start, Action<string> show) : ILauncherBridge
{
    public LauncherBridge(LauncherEnv env, ILogger<MockLauncherBridge> log) : this(() => env.Install, new MockLauncherBridge(log),
        name => Process.GetProcessesByName(name).Length > 0, Start, Message)
    { }

    /// <summary>The client's arguments: the server, a random number with bit 128 clear, and the Steam flag.</summary>
    public static string Arguments(string url, int param) => $"/connect:{url} /param:{param & int.MaxValue & ~128} /steam";

    public bool IsSteam => rest.IsSteam;
    public void OpenFolder(string kind) => rest.OpenFolder(kind);
    public void ClearCache(string kind) => rest.ClearCache(kind);
    public void OpenExternal(string url) => rest.OpenExternal(url);
    public void ToggleDevTools() => rest.ToggleDevTools();
    public void RestartLauncher() => rest.RestartLauncher();

    public void StartGame(string url)
    {
        if (install() is not { } g || !File.Exists(g.ClientExe)) { show("The game was not found. Check its folder in the settings."); return; }
        if (url.Any(c => char.IsWhiteSpace(c) || c == '"')) { show("The server address is not valid."); return; }
        if (isRunning(Path.GetFileNameWithoutExtension(g.ClientExe))) { show("The game is already running."); return; }
        if (!start(new(g.ClientExe, Arguments(url, Random.Shared.Next())) { WorkingDirectory = g.Folder }))
            show("The game could not be started.");
    }

    static bool Start(ProcessStartInfo i)
    {
        try { return Process.Start(i) is not null; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    static void Message(string text) =>
        Application.Current.Dispatcher.Invoke(() => MessageBox.Show(Application.Current.MainWindow, text, "Launcher"));
}
