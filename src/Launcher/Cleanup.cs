using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Resurgence.Services;

namespace Resurgence.Launcher;

/// <summary>
/// The clean-up of an older setup on the first start after the install, and of everything the install changed on the uninstall (docs/launcher.md#install-and-uninstall).
/// Every function looks at fixed places only and returns what it found: nothing is deleted that <see cref="LauncherCleanup"/> did not list for the player first.
/// </summary>
static class Cleanup
{
    // the old zips: folder name and the exe that must be inside, so that a folder of that name with something else in it is left alone
    static readonly (string Folder, string Exe)[] OldZips = [("launcher-win-x64", "resurgence-launcher.exe"), ("play-win-x64", "play.exe")];
    static readonly string[] ProgramNames = ["play", "resurgence-launcher"];

    static bool Same(string? a, string? b) => a is not null && b is not null && string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>An old program: <c>play.exe</c> next to its <c>settings.json</c> (the old kit's zip), or an <c>resurgence-launcher.exe</c> that is not in the folder of <paramref name="self"/> (this install).</summary>
    public static bool IsOld(string exe, string self)
    {
        var name = Path.GetFileName(exe);
        var dir = Path.GetDirectoryName(Path.GetFullPath(exe))!;
        if (name.Equals("play.exe", StringComparison.OrdinalIgnoreCase)) return File.Exists(Path.Combine(dir, "settings.json"));
        return name.Equals("resurgence-launcher.exe", StringComparison.OrdinalIgnoreCase) && !Same(dir, Path.GetDirectoryName(self));
    }

    static string? PathOf(Process p)
    {
        try { return p.MainModule?.FileName; }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { return null; } // another user's or an elevated process: not ours to touch
    }

    static IEnumerable<Process> Candidates() => ProgramNames.SelectMany(Process.GetProcessesByName);

    /// <summary>Full paths of the old programs that run now.</summary>
    public static string[] Running(string self)
    {
        var found = new List<string>();
        foreach (var p in Candidates())
            using (p)
                if (PathOf(p) is { } exe && IsOld(exe, self)) found.Add(exe);
        return [.. found.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Asks the programs at <paramref name="exes"/> to close and stops those that do not within a few seconds. One that cannot be stopped is skipped: its folder then fails to delete and is reported.</summary>
    public static void Stop(IReadOnlyCollection<string> exes)
    {
        foreach (var p in Candidates())
            using (p)
                try
                {
                    if (PathOf(p) is not { } exe || !exes.Contains(exe, StringComparer.OrdinalIgnoreCase) || p.HasExited) continue;
                    if (!p.CloseMainWindow() || !p.WaitForExit(3000)) p.Kill();
                    p.WaitForExit(5000); // its folder is locked until it is gone
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
    }

    /// <summary>Old unpacked zip folders directly in <paramref name="roots"/>, each with its exe still inside; never the folder of <paramref name="self"/>.</summary>
    public static string[] Folders(IEnumerable<string> roots, string self)
    {
        var selfDir = Path.GetDirectoryName(self);
        return [.. roots.Where(Directory.Exists).SelectMany(r => OldZips.Select(z => (Dir: Path.Combine(r, z.Folder), z.Exe)))
            .Where(z => File.Exists(Path.Combine(z.Dir, z.Exe)) && !Same(z.Dir, selfDir)).Select(z => z.Dir).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Where old folders are looked for: Downloads, the desktop and the folder above each running old program's folder.</summary>
    public static string[] Roots(IEnumerable<string> running)
    {
        var roots = new List<string> { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) };
        if (SHGetKnownFolderPath(DownloadsId, 0, 0, out var downloads) == 0) roots.Add(downloads);
        roots.AddRange(running.Select(exe => Path.GetDirectoryName(Path.GetDirectoryName(exe))).OfType<string>());
        return [.. roots];
    }

    static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetKnownFolderPath(in Guid id, uint flags, nint token, out string path);

    /// <summary>Stray game logs (<c>*.netlog</c>, <c>cef.log</c>, an empty <c>icons</c> folder) in the game's install folder itself. Never inside the folder of the game's exe, and nothing without that subfolder.</summary>
    public static string[] Logs(GameInstall? game)
    {
        if (game is null || Same(Path.GetDirectoryName(game.ClientExe), game.Folder) || !Directory.Exists(game.Folder)) return [];
        var found = Directory.EnumerateFiles(game.Folder)
            .Where(f => Path.GetExtension(f).Equals(".netlog", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Equals("cef.log", StringComparison.OrdinalIgnoreCase)).ToList();
        var icons = Path.Combine(game.Folder, "icons");
        if (Directory.Exists(icons) && !Directory.EnumerateFileSystemEntries(icons).Any()) found.Add(icons);
        return [.. found];
    }

    /// <summary>Deletes what <see cref="Folders"/> (<paramref name="recursive"/>) or <see cref="Logs"/> found, a path that is already gone counts as done. A folder of the logs is only ever deleted when empty. The messages of what could not be deleted.</summary>
    public static string[] Delete(IEnumerable<string> paths, bool recursive)
    {
        var failed = new List<string>();
        foreach (var path in paths)
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive);
                else File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failed.Add(e.Message); }
        return [.. failed];
    }

    /// <summary>Null: no setup of the player kit on this PC (no hosts block, no authority, no leaf). True: there is one and it fits <paramref name="hosts"/> (names, valid, trusted): kept. False: it does not: setup replaces it.</summary>
    public static bool? KitKept(string hostsFile, string[] hosts, Pki.Place place, DateTimeOffset now)
    {
        var text = Hosts.Read(hostsFile);
        if (!Hosts.Has(text) && !Pki.Installed(place)) return null;
        return Hosts.Matches(text, hosts) && Pki.Check(hosts, now, place) is null;
    }

    /// <summary>Velopack's uninstall step: the hosts block, the leaf and its key, the authority, the link scheme and, when <paramref name="deleteData"/> says yes, <paramref name="dataDir"/>
    /// (<c>%APPDATA%\hunter-resurgence</c>; the question has a default of No and the hook is stopped after 30 seconds, so no answer keeps the data). Each step alone: one that fails
    /// (a Windows prompt the player refused) does not stop the others.</summary>
    public static void Uninstall(string hostsFile, Pki.Place place, RegistryKey root, string dataDir, Func<bool> deleteData)
    {
        Action[] steps =
        [
            () => { if (Hosts.Has(Hosts.Read(hostsFile))) Kit.SetHosts(false, null, hostsFile); }, // the one place that may ask for administrator rights, and only when there is a block
            () => Pki.Uninstall(place),
            () => LinkScheme.Remove(root),
            () => { if (Directory.Exists(dataDir) && deleteData()) Directory.Delete(dataDir, true); },
        ];
        foreach (var step in steps)
            try { step(); }
            catch (Exception e) when (e is not OutOfMemoryException) { }
    }
}

/// <summary>What the first start after the install does about an older setup: find it once, delete it when the player says so.</summary>
sealed class LauncherCleanup(Func<GameInstall?> game, string? self, bool firstRun) : ICleanupService
{
    bool asked;

    public Task<OldSetup?> FindAsync() => Task.Run<OldSetup?>(() =>
    {
        if (!firstRun || self is null || Interlocked.Exchange(ref asked, true)) return null;
        try
        {
            var running = Cleanup.Running(self);
            bool? kit = null;
            try { kit = Cleanup.KitKept(Hosts.DefaultPath, KitSettings.Load().Hosts, Pki.Place.Default, DateTimeOffset.UtcNow); }
            catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException) { } // no usable server_url (no player kit here), or a hosts file that cannot be read
            var found = new OldSetup(running, Cleanup.Folders(Cleanup.Roots(running), self), Cleanup.Logs(game()), kit);
            return found.Any ? found : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    });

    public Task<Result> CleanAsync(OldSetup found) => Task.Run(() =>
    {
        try
        {
            Cleanup.Stop([.. found.Programs]);
            var failed = Cleanup.Delete(found.Folders, true).Concat(Cleanup.Delete(found.Logs, false)).ToArray();
            return failed.Length == 0 ? Result.Success : Result.Fail(string.Join("\n", failed));
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return Result.Fail(e.Message);
        }
    });
}
