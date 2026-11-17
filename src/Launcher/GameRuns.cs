using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Resurgence.Services;

namespace Resurgence.Launcher;

/// <summary>The game's process as the watcher reads it while it runs. The delegates are the seam: a test needs no process.</summary>
public sealed class GameProcess(Func<bool> windowVisible, Func<int?> exitCode, Action? dispose = null) : IDisposable
{
    /// <summary>A visible top-level window of the process exists right now.</summary>
    public bool WindowVisible() => windowVisible();
    /// <summary>The exit code once the process is gone; null while it runs or when Windows does not tell.</summary>
    public int? ExitCode() => exitCode();
    public void Dispose() => dispose?.Invoke();

    /// <summary>The process named like the client exe (without extension), or null; the same match as <see cref="GameFiles.ClientRunning"/>.</summary>
    public static GameProcess? Find(string clientExe)
    {
        var all = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(clientExe));
        foreach (var other in all.Skip(1)) other.Dispose();
        if (all.FirstOrDefault() is not { } p) return null;
        var id = (uint)p.Id;
        return new(() => HasVisibleWindow(id), () =>
        {
            try { return p.HasExited ? p.ExitCode : null; }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception) { return null; } // a process of another privilege level: no exit code
        }, p.Dispose);
    }

    static bool HasVisibleWindow(uint pid)
    {
        var found = false;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var owner);
            if (owner == pid && IsWindowVisible(h)) found = true;
            return !found;
        }, 0);
        return found;
    }

    delegate bool EnumWindowsProc(nint window, nint param);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, nint param);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint window);
}

/// <summary>The last game starts the launcher watched, in runs.json next to launcher.json (at most <see cref="Keep"/>). A missing or damaged file is no history.</summary>
public sealed class RunHistory(string path)
{
    public const int Keep = 10;
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    public static string PathNextTo(string prefsPath) => Path.Combine(Path.GetDirectoryName(prefsPath)!, "runs.json");

    public IReadOnlyList<GameRun> Load()
    {
        try { return JsonSerializer.Deserialize<List<GameRun>>(File.ReadAllBytes(path), Json) ?? []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Appends the run; a write that fails loses only that record.</summary>
    public void Add(GameRun run)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".part";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(Load().Append(run).TakeLast(Keep), Json));
            File.Move(tmp, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
