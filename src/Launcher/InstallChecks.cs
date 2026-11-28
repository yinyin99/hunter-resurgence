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
/// The install check (#631, docs/launcher.md#the-install-check): twelve read-only checks of the game's install, one <see cref="InstallCheck"/> each, in the order game, version, files, exe, path, steam,
/// setup, windows, disk, display, running, dlls. Nothing is ever written into the game folder (the one fix, <see cref="FixDisplay"/>, writes the graphics file in %APPDATA%). A check whose probe fails (no rights, a vanished file, no network) reports ok: a probe never blocks a start.
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
/// <param name="steamInstalled">Whether Steam is installed for this user; null: not asked (a Steam that is not signed in is then the only reading).</param>
/// <param name="portUser">The name of the other program that listens on a port (80, 443: the forwarder of the player kit needs both) or null when it is free; null: the ports are not checked.</param>
/// <param name="cleanDlls">The DLLs of a clean install by name, with size and SHA-256 (check 10); default <see cref="CleanDlls.Files"/>.</param>
/// <param name="screen">What this PC says of the screen (check 11); null: the check reports ok.</param>
/// <param name="startWithSteam">The Game settings' box "Start the game using Steam"; false: the start never uses Steam, so check 4 reports ok. Null: checked.</param>
public sealed class InstallChecks(Func<GameInstall?> install, string? appId, Func<Task<long?>> serverBuild, Func<bool> steamSignedIn, Func<string?> setup, Func<string, bool> isRunning,
    Func<GameInstall, string?>? manifest = null, Func<string, bool>? isNetwork = null, Func<IEnumerable<string>>? oneDrive = null, Func<string, long?>? freeBytes = null, string? temp = null,
    Func<IEnumerable<string>>? blocks = null, Func<int, string?>? portUser = null, Func<bool>? steamInstalled = null, IReadOnlyDictionary<string, (long Size, string Sha256)>? cleanDlls = null, ScreenProbes? screen = null, Func<bool>? startWithSteam = null)
{
    const long LowBytes = 1L << 30;
    const int LongPath = 200, Archives = 12, NamesShown = 5;
    static readonly TimeSpan BuildKept = TimeSpan.FromMinutes(5), BuildMissedKept = TimeSpan.FromMinutes(1);

    sealed record Fetched(long? Build, DateTime At);
    Fetched? fetched;

    /// <summary>The checks for this PC. The server is asked at <c>server_url</c> only; without it, or when it answers nothing, the build is not compared.</summary>
    public static InstallChecks For(LauncherEnv env)
    {
        var s = env.Settings;
        var http = s.ServerUrl is { } server ? LauncherServices.ServerClient(server, TimeSpan.FromSeconds(5)) : null;
        return new(() => env.Install, s.SteamAppId, () => http is null ? Task.FromResult<long?>(null) : FetchBuild(http, env.Clock), () => Steam.ActiveAccount() != 0,
            s.ServerUrl?.Scheme == Uri.UriSchemeHttps ? () => LauncherKit.SetupKey(DateTimeOffset.UtcNow) : () => null, GameFiles.ClientRunning,
            portUser: s.ServerUrl?.Scheme == Uri.UriSchemeHttps ? PortUser : null, steamInstalled: Steam.Installed,
            startWithSteam: () => env.Prefs.StartWithSteam,
            screen: ScreenProbes.Real(s, () => env.Prefs.StartWithSteam ? SteamProbes.Real.PlannedMode() : "nosteam", () => env.Prefs.DisplayMode == DisplayMode.Fullscreen));
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
            Run("exe", x => Exe(x, version.Args?.GetValueOrDefault("build"))),
            Info("path", PathCheck),
            // the game starts without Steam when it is not installed or not signed in: a warning, a block only when there is no game to start
            Safe("steam", () => startWithSteam?.Invoke() == false || steamSignedIn() ? Ok("steam")
                : g is null ? new("steam", "block", "Check.Steam.SignedOut", null, "open-steam")
                : steamInstalled?.Invoke() == false ? new("steam", "warn", "Check.Steam.NotInstalled")
                : new("steam", "warn", "Check.Steam.Fallback", null, "open-steam")),
            Safe("setup", () => setup() is { } key ? new("setup", "block", key, null, "setup") : PortInUse() ?? Ok("setup")),
            Info("windows", Windows),
            Info("disk", Disk),
            Run("display", Display),
            Run("running", x => opening && isRunning(x.ClientExe) ? new("running", "block", "Check.Running", null, "close-game") : Ok("running")),
            Info("dlls", x => Dlls(x, version.Args?.GetValueOrDefault("build"))),
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

    /// <summary>The build the server supports: a positive whole number, else null. The answer's <c>Date</c> header is what <paramref name="clock"/> measures the PC's clock by.</summary>
    internal static async Task<long?> FetchBuild(HttpClient http, ServerClock? clock = null)
    {
        try
        {
            using var answer = await http.GetAsync("/stats/game");
            clock?.Observe(answer);
            using var doc = JsonDocument.Parse(await answer.EnsureSuccessStatusCode().Content.ReadAsStringAsync());
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

    // 12, listed after files: the exe is the game's own program of the supported build. A program of another make put in its place (the one a launcher fix or a mod leaves) has another PE time stamp; the file version is a resource anybody can set (the .NET program that
    // replaced the exe in #1051 was 1.0.0.0, the game's own is 1.0.0.1), so the stamp is what is judged; the version and size go to the report. A file that is not a PE file tells nothing (a probe never blocks a start);
    // a game of another build, or of an unknown one (no Steam manifest: a folder chosen by hand), is not judged (the version check tells of another build; an unknown one has nothing to compare).
    InstallCheck Exe(GameInstall g, string? build)
    {
        if (build is null || !CleanDlls.Judges(build) || ExeStamp(g.ClientExe) is not { } stamp) return Ok("exe");
        var info = FileVersionInfo.GetVersionInfo(g.ClientExe);
        var facts = new Dictionary<string, string>
        {
            ["version"] = info.FileVersion is null ? "none" : $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}",
            ["size"] = new FileInfo(g.ClientExe).Length.ToString(CultureInfo.InvariantCulture),
            ["stamp"] = stamp.ToString(CultureInfo.InvariantCulture),
        };
        return stamp == CleanDlls.ExeStamp ? Ok("exe", facts) : new("exe", "block", "Check.Exe.Other", facts, "open-steam");
    }

    /// <summary>The TimeDateStamp of the PE header of <paramref name="path"/>; null when it is not a PE file.</summary>
    internal static uint? ExeStamp(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> dos = stackalloc byte[0x40], pe = stackalloc byte[12];
        if (s.ReadAtLeast(dos, dos.Length, false) < dos.Length || dos[0] != 'M' || dos[1] != 'Z') return null;
        s.Position = BitConverter.ToUInt32(dos[0x3c..]);
        return s.ReadAtLeast(pe, pe.Length, false) == pe.Length && pe[0] == 'P' && pe[1] == 'E' && pe[2] == 0 && pe[3] == 0 ? BitConverter.ToUInt32(pe[8..]) : null;
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

    // 11, listed after disk: exclusive full screen at a mode the primary display does not offer (the game closes at start), and a primary display device that is no working screen. Probed on a start as well: it can end the start at once.
    // The mode is judged only when the saved display mode is Fullscreen (exclusive): Borderless (also no saved mode) and Windowed starts never use the exclusive full screen, whatever the file says now.
    // An adapter's name never triggers the second warning: a working screen on an adapter named like a virtual one is fine.
    InstallCheck Display(GameInstall g)
    {
        if (screen is null) return Ok("display");
        var modes = screen.Exclusive?.Invoke() == true ? screen.Modes() : [];
        if (modes.Count > 0 && NotOffered(modes).FirstOrDefault().Settings is { } bad)
            return new("display", "warn", "Check.Display.Mode", new Dictionary<string, string> { ["mode"] = $"{bad.Width}x{bad.Height} {bad.Hz} Hz" }, "fix-display");
        var primary = screen.Adapters().FirstOrDefault(a => a.Primary);
        if (primary is not null && (!primary.Attached || !primary.Monitor))
            return new("display", "warn", "Check.Display.Virtual", new Dictionary<string, string> { ["adapter"] = primary.Name });
        return Ok("display", primary is null ? null : new() { ["adapter"] = primary.Name });
    }

    /// <summary>The graphics files the game may start with that say exclusive full screen at a mode that is not in <paramref name="modes"/>, each with its settings.</summary>
    IEnumerable<(string File, GraphicsSettings? Settings)> NotOffered(IReadOnlyList<(int W, int H, int Hz)> modes) =>
        screen!.GraphicsFiles().Select(f => (f, GameFiles.ReadGraphics(f).Value)).Where(r => r.Value is { Fullscreen: true } s && !modes.Contains((s.Width, s.Height, s.Hz)));

    /// <summary>The fix of <c>Check.Display.Mode</c>: the display's current mode goes into each graphics file that is full screen at a mode the display does not offer; every other value stays. Nothing while the game runs.
    /// A failure is a Strings key (<c>GameSettings.*</c>).</summary>
    public Result FixDisplay()
    {
        if (screen is null || install() is not { } g) return Result.Fail("GameSettings.NotConfigured");
        if (isRunning(g.ClientExe)) return Result.Fail("GameSettings.ClientRunning");
        if (screen.Current() is not { } now) return Result.Fail("GameSettings.SaveFailed");
        try
        {
            foreach (var (file, s) in NotOffered(screen.Modes()).ToList()) GameFiles.WriteGraphics(file, s! with { Width = now.W, Height = now.H, Hz = now.Hz });
            return Result.Success;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { return Result.Fail("GameSettings.SaveFailed"); }
    }

    static string Size(long bytes) => bytes >= LowBytes ? $"{(bytes / (double)LowBytes).ToString("0.#", CultureInfo.InvariantCulture)} GB" : $"{bytes >> 20} MB";

    static long? Free(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace; }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }

    // 10: .dll files next to the exe that a clean install of the supported build does not have, or has in another size or content; a warning only, some players use wrappers on purpose.
    // A game of another build is not judged (the version check tells of an old or a new one): the table is of one build.
    InstallCheck Dlls(GameInstall g, string? build)
    {
        if (!CleanDlls.Judges(build)) return Ok("dlls");
        var foreign = CleanDlls.Foreign(Path.GetDirectoryName(g.ClientExe)!, cleanDlls ?? CleanDlls.Files).Where(f => f.State != CleanDlls.State.Unreadable && f.File.Extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)).ToList(); // a file that cannot be read tells nothing: the report names it
        InstallCheck Warn(string text, CleanDlls.State state, string fix)
        {
            var names = foreign.Where(f => f.State == state).Select(f => f.File.Name).ToList();
            return new("dlls", "warn", text, new Dictionary<string, string> { ["files"] = string.Join(", ", names.Take(NamesShown)) + (names.Count > NamesShown ? ", …" : "") }, fix);
        }
        return foreign.Any(f => f.State == CleanDlls.State.Extra) ? Warn("Check.Dlls.Extra", CleanDlls.State.Extra, "open-folder") : foreign.Count > 0 ? Warn("Check.Dlls.Changed", CleanDlls.State.Changed, "verify-files") : Ok("dlls");
    }
}
