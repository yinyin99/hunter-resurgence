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

    /// <summary>Starts the client; null when Windows does not start it. This process is the launcher's own child, so its exit code is known however fast it quit.</summary>
    public static GameProcess? Start(ProcessStartInfo info)
    {
        try
        {
            if (Process.Start(info) is not { } p) return null;
            return new(() => !p.HasExited && HasVisibleWindow((uint)p.Id), () => p.HasExited ? p.ExitCode : null, p.Dispose);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    /// <summary>The process named like the client exe (without extension), or null; the same match as <see cref="GameFiles.ClientRunning"/>.</summary>
    public static GameProcess? Find(string clientExe)
    {
        var all = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(clientExe));
        foreach (var other in all.Skip(1)) other.Dispose();
        if (all.FirstOrDefault() is not { } p) return null;
        var id = (uint)p.Id;
        // a handle held from now on: the exit code is read from it after the process is gone (a process looked up by name holds none)
        var handle = OpenProcess(QueryLimited, false, id);
        return new(() => HasVisibleWindow(id), () => handle != 0 && GetExitCodeProcess(handle, out var code) && code != StillActive ? unchecked((int)code) : null, () =>
        {
            if (handle != 0) CloseHandle(handle);
            p.Dispose();
        });
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

    const uint QueryLimited = 4096, StillActive = 259; // PROCESS_QUERY_LIMITED_INFORMATION; the "has no exit code yet" value

    delegate bool EnumWindowsProc(nint window, nint param);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, nint param);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint window);
    [DllImport("kernel32.dll", SetLastError = true)] static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(nint process, out uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
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
