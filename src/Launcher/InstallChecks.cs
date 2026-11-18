using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Resurgence.Services;

namespace Resurgence.Launcher;

/// <summary>
/// The install check (#631, docs/launcher.md#the-install-check): nine read-only checks of the game's install, one <see cref="InstallCheck"/> each, in the order game, version, files, path, steam,
/// setup, windows, disk, running. Nothing is ever written into the game folder. A check whose probe fails (no rights, a vanished file, no network) reports ok: a probe never blocks a start.
/// The probes after <paramref name="isRunning"/> are what this PC says; a test passes fakes.
/// </summary>
/// <param name="install">The game in use (check 1); null or without its exe: none.</param>
/// <param name="appId">The Steam app id of the manifest (<c>appmanifest_&lt;id&gt;.acf</c>); null: no version check.</param>
/// <param name="serverBuild">The build the community server supports (<c>GET /stats/game</c>); null when unset or unreachable: the build is not compared.</param>
/// <param name="steamSignedIn">Whether somebody is signed in to Steam (does not start Steam).</param>
/// <param name="setup">The player kit's problem as a Strings key (<c>Kit.*</c>), null when setup is in place or the launcher has no kit.</param>
/// <param name="isRunning">Whether the game's exe runs.</param>
/// <param name="manifest">The text of the game's app manifest; null when it has none (not a Steam install).</param>
/// <param name="isNetwork">Whether a path is on a network drive.</param>
/// <param name="oneDrive">The OneDrive folders of this user.</param>
/// <param name="freeBytes">Free bytes on the drive of a path; null when unknown.</param>
/// <param name="temp">The temp folder.</param>
/// <param name="blocks">The Windows events of the last 7 days that tell of a blocked file, each formatted.</param>
/// <param name="portUser">The name of the other program that listens on a port (80, 443: the forwarder of the player kit needs both) or null when it is free; null: the ports are not checked.</param>
public sealed class InstallChecks(Func<GameInstall?> install, string? appId, Func<Task<long?>> serverBuild, Func<bool> steamSignedIn, Func<string?> setup, Func<string, bool> isRunning,
    Func<GameInstall, string?>? manifest = null, Func<string, bool>? isNetwork = null, Func<IEnumerable<string>>? oneDrive = null, Func<string, long?>? freeBytes = null, string? temp = null,
    Func<IEnumerable<string>>? blocks = null, Func<int, string?>? portUser = null)
{
    const long LowBytes = 1L << 30;
    const int LongPath = 200, Archives = 12;
    static readonly TimeSpan BuildKept = TimeSpan.FromMinutes(5), BuildMissedKept = TimeSpan.FromMinutes(1);

    sealed record Fetched(long? Build, DateTime At);
    Fetched? fetched;

    /// <summary>The checks for this PC. The server is asked at <c>server_url</c> only; without it, or when it answers nothing, the build is not compared.</summary>
    public static InstallChecks For(LauncherEnv env)
    {
        var s = env.Settings;
        var http = s.ServerUrl is { } server ? new HttpClient(new SocketsHttpHandler { UseCookies = false }) { BaseAddress = server, Timeout = TimeSpan.FromSeconds(5) } : null;
        return new(() => env.Install, s.SteamAppId, () => http is null ? Task.FromResult<long?>(null) : FetchBuild(http), () => Steam.ActiveAccount() != 0,
            s.ServerUrl?.Scheme == Uri.UriSchemeHttps ? () => LauncherKit.SetupKey(DateTimeOffset.UtcNow) : () => null, GameFiles.ClientRunning,
            portUser: s.ServerUrl?.Scheme == Uri.UriSchemeHttps ? PortUser : null);
    }

    /// <summary>Runs the checks off the calling thread.</summary>
    /// <param name="opening">The launcher has just opened: the game must not run yet (check 9); later it is the launcher's own start that runs it.</param>
    /// <param name="blocksOnly">Only what can block a start is probed: path, Windows and disk report ok.</param>
    public Task<IReadOnlyList<InstallCheck>> RunAsync(bool opening, bool blocksOnly = false) => Task.Run(async () =>
    {
        var g = install() is { } i && File.Exists(i.ClientExe) ? i : null;
        InstallCheck Run(string id, Func<GameInstall, InstallCheck> check) => Safe(id, () => g is null ? Ok(id) : check(g));
        InstallCheck Info(string id, Func<GameInstall, InstallCheck> check) => blocksOnly ? Ok(id) : Run(id, check);
        var version = Ok("version");
        try { if (g is not null) version = await Version(g); }
        catch (Exception e) when (e is not OutOfMemoryException) { } // a probe never blocks a start
        return (IReadOnlyList<InstallCheck>)
        [
            g is null ? new("game", "block", "Check.Game.NotFound", null, "choose-folder") : Ok("game"),
            version,
            Run("files", Files),
            Info("path", PathCheck),
            // the game starts without Steam when it is not signed in: a warning, a block only when there is no game to start
            Safe("steam", () => steamSignedIn() ? Ok("steam") : new("steam", g is null ? "block" : "warn", g is null ? "Check.Steam.SignedOut" : "Check.Steam.Fallback", null, "open-steam")),
            Safe("setup", () => setup() is { } key ? new("setup", "block", key, null, "setup") : PortInUse() ?? Ok("setup")),
            Info("windows", Windows),
            Info("disk", Disk),
            Run("running", x => opening && isRunning(x.ClientExe) ? new("running", "block", "Check.Running", null, "close-game") : Ok("running")),
        ];
    });

    static InstallCheck Ok(string id, Dictionary<string, string>? facts = null) => new(id, "ok", "Check.Ok", facts);
    static InstallCheck Safe(string id, Func<InstallCheck> check)
    {
        try { return check(); }
        catch (Exception e) when (e is not OutOfMemoryException) { return Ok(id); }
    }

    // 2: Steam's manifest says which build is installed and whether Steam is still working on it (StateFlags 4: fully installed)
    async Task<InstallCheck> Version(GameInstall g)
    {
        var text = manifest is null ? ReadManifest(g.Folder, appId) : manifest(g);
        if (text is null) return Ok("version");
        var (flags, local) = (Number(text, "StateFlags"), Number(text, "buildid"));
        var facts = new Dictionary<string, string>();
        if (local is { } l) facts["build"] = l.ToString(CultureInfo.InvariantCulture);
        if (flags is not null and not 4) return new("version", "block", "Check.Version.Updating", facts, "open-steam");
        if (local is not { } b || await ServerBuild() is not { } s) return Ok("version", facts);
        facts["server"] = s.ToString(CultureInfo.InvariantCulture);
        return b == s ? Ok("version", facts) : b < s ? new("version", "block", "Check.Version.Old", facts, "open-steam") : new("version", "warn", "Check.Version.Newer", facts);
    }

    static long? Number(string text, string key) => GameFinder.VdfValue(text, key) is { } v && long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>The manifest sits in the steamapps folder above <c>common\&lt;install&gt;</c>; a folder elsewhere (chosen by hand) has none.</summary>
    static string? ReadManifest(string folder, string? id)
    {
        var common = Directory.GetParent(folder.TrimEnd('\\', '/'));
        if (id is null || common is not { Name: "common", Parent: { Name: "steamapps" } steamapps }) return null;
        var file = Path.Combine(steamapps.FullName, $"appmanifest_{id}.acf");
        return File.Exists(file) ? File.ReadAllText(file) : null;
    }

    // ponytail: remembered for 5 minutes (the server's own cache time), a miss for 1: a start on a server that does not answer waits for the client's timeout once, not every time
    async Task<long?> ServerBuild()
    {
        if (fetched is { } f && DateTime.UtcNow - f.At < (f.Build is null ? BuildMissedKept : BuildKept)) return f.Build;
        var build = await serverBuild();
        fetched = new(build, DateTime.UtcNow);
        return build;
    }

    /// <summary>The build the server supports: a positive whole number, else null.</summary>
    internal static async Task<long?> FetchBuild(HttpClient http)
    {
        try
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync("/stats/game"));
            return doc.RootElement.TryGetProperty("build", out var b) && b.ValueKind == JsonValueKind.Number && b.TryGetInt64(out var v) && v > 0 ? v : null;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException) { return null; }
    }

    // 6, after the certificate and the hosts block: the forwarder needs ports 80 and 443 (the launcher's own forwarder does not count)
    InstallCheck? PortInUse()
    {
        foreach (var port in new[] { 80, 443 })
            if (portUser?.Invoke(port) is { } who)
                return new("setup", "block", "Check.Setup.Port", new Dictionary<string, string> { ["port"] = port.ToString(CultureInfo.InvariantCulture), ["process"] = who });
        return null;
    }

    static readonly uint Loopback = BitConverter.ToUInt32(IPAddress.Loopback.GetAddressBytes());

    /// <summary>The name of the other program that listens on the IPv4 <paramref name="port"/> on all addresses or on 127.0.0.1 (Windows' TCP table with the owning process), null when none or when the table cannot be read.</summary>
    internal static string? PortUser(int port)
    {
        const int Ipv4 = 2, ListenersWithOwner = 3, Row = 24; // MIB_TCPROW_OWNER_PID: state, local address, local port, remote address, remote port, pid
        var size = 0;
        GetExtendedTcpTable(nint.Zero, ref size, false, Ipv4, ListenersWithOwner, 0); // answers how much room it needs
        var table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(table, ref size, false, Ipv4, ListenersWithOwner, 0) != 0) return null;
            for (int i = 0, rows = Marshal.ReadInt32(table); i < rows; i++)
            {
                var row = table + 4 + i * Row;
                var address = (uint)Marshal.ReadInt32(row, 4);
                var local = (ushort)IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(row, 8));
                var pid = Marshal.ReadInt32(row, 20);
                if (local == port && (address == 0 || address == Loopback) && pid != Environment.ProcessId) return NameOf(pid);
            }
            return null;
        }
        finally { Marshal.FreeHGlobal(table); }
    }

    static string NameOf(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return "pid " + pid; }
    }

    [DllImport("iphlpapi.dll")]
    static extern uint GetExtendedTcpTable(nint table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool sorted, int family, int tableClass, uint reserved);

    // 3: the archive set; a file Steam did not finish or lost is missing or empty
    static InstallCheck Files(GameInstall g)
    {
        var dir = Path.Combine(g.Folder, "game", "archives");
        for (var i = 0; i < Archives; i++)
            foreach (var ext in new[] { ".arc", ".tab" })
            {
                var file = $"pc{i}{ext}";
                if (new FileInfo(Path.Combine(dir, file)) is not { Exists: true, Length: > 0 })
                    return new("files", "block", "Check.Files.Missing", new Dictionary<string, string> { ["file"] = file }, "verify-files");
            }
        return Ok("files", new() { ["archives"] = $"pc0..pc{Archives - 1} present" });
    }

    // 4: a path the game or Steam handles badly; a warning only
    InstallCheck PathCheck(GameInstall g)
    {
        var p = g.Folder;
        var named = new Dictionary<string, string> { ["path"] = p };
        if ((isNetwork ?? IsNetwork)(p)) return new("path", "warn", "Check.Path.Network", named, "choose-folder");
        if ((oneDrive ?? OneDriveRoots)().Any(r => Under(p, r))) return new("path", "warn", "Check.Path.OneDrive", named, "choose-folder");
        if (p.Any(c => c > 127)) return new("path", "warn", "Check.Path.NonAscii", named, "choose-folder");
        if (p.Length > LongPath) return new("path", "warn", "Check.Path.Long", new Dictionary<string, string> { ["length"] = p.Length.ToString(CultureInfo.InvariantCulture) }, "choose-folder");
        return Ok("path", new() { ["length"] = p.Length.ToString(CultureInfo.InvariantCulture) });
    }

    static bool Under(string path, string root) =>
        Path.GetFullPath(path).TrimEnd('\\') is var p && Path.GetFullPath(root).TrimEnd('\\') is var r && r.Length > 0 && (p.Equals(r, StringComparison.OrdinalIgnoreCase) || p.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase));

    static IEnumerable<string> OneDriveRoots() => new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }.Select(Environment.GetEnvironmentVariable).OfType<string>().Where(v => v.Length > 0);

    static bool IsNetwork(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (root is null) return false;
        if (root.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        try { return new DriveInfo(root).DriveType == DriveType.Network; }
        catch (ArgumentException) { return false; }
    }

    // 7: Code Integrity (Smart App Control) and Defender events that name a file under the game's folder; the file is shown relative to the folder
    InstallCheck Windows(GameInstall g)
    {
        // event texts name a file as \Device\HarddiskVolume3\dir\file or file:_C:\dir\file: the folder is looked for without its drive
        if (Path.GetPathRoot(g.Folder) is not { Length: 3 } root || root[1] != ':' || g.Folder[2..].TrimEnd('\\') is not { Length: > 1 } inside) return Ok("windows");
        var named = new Regex(Regex.Escape(inside) + @"\\([^\r\n]*?\.(?:dll|exe|sys))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var row in (blocks ?? ReadBlocks)())
            if (named.Match(row) is { Success: true } m)
                return new("windows", "warn", "Check.Windows.Blocked", new Dictionary<string, string> { ["file"] = m.Groups[1].Value });
        return Ok("windows");
    }

    /// <summary>The last 7 days of Code Integrity blocks (3033, 3077) and Defender detections and blocks (1116, 1121), at most 200 of each log. A log that is missing or not readable for this user adds nothing.</summary>
    internal static IEnumerable<string> ReadBlocks()
    {
        var age = (long)TimeSpan.FromDays(7).TotalMilliseconds;
        var found = new List<string>();
        foreach (var (log, ids) in new[] { ("Microsoft-Windows-CodeIntegrity/Operational", "EventID=3033 or EventID=3077"), ("Microsoft-Windows-Windows Defender/Operational", "EventID=1116 or EventID=1121") })
            try
            {
                using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, $"*[System[({ids}) and TimeCreated[timediff(@SystemTime) <= {age}]]]"));
                for (var n = 0; n < 200 && reader.ReadEvent() is { } e; n++)
                    using (e)
                        if (e.FormatDescription() is { } text) found.Add(text);
            }
            catch (Exception e) when (e is EventLogException or UnauthorizedAccessException) { }
        return found;
    }

    // 8: the game's drive and the temp folder (the launcher's downloads and the report go there)
    InstallCheck Disk(GameInstall g)
    {
        var facts = new Dictionary<string, string>();
        foreach (var (name, place) in new[] { ("game", g.Folder), ("temp", temp ?? Path.GetTempPath()) })
        {
            if ((freeBytes ?? Free)(place) is not { } free) continue;
            facts[name] = Size(free);
            if (free < LowBytes) return new("disk", "warn", "Check.Disk.Low", new Dictionary<string, string> { ["drive"] = Path.GetPathRoot(Path.GetFullPath(place)) ?? place, ["free"] = Size(free) });
        }
        return Ok("disk", facts);
    }

    static string Size(long bytes) => bytes >= LowBytes ? $"{(bytes / (double)LowBytes).ToString("0.#", CultureInfo.InvariantCulture)} GB" : $"{bytes >> 20} MB";

    static long? Free(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace; }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }
}
