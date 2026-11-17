using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO.Compression;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Resurgence.Services;

namespace Resurgence.Launcher;

/// <summary>Builds the problem report zip (docs/launcher.md, "Report a problem"): what the game wrote, its settings, the Windows crash events, what runs beside it and the launcher's last starts.
/// Nothing that carries a sign-in goes in: <c>jwt.json</c> is never read and every line that looks like a credential is replaced (<see cref="Scrub"/>). A file that is missing or locked is
/// listed in <see cref="ProblemReport.Missing"/>, never an error. The probes are delegates (the default is this PC) so that a test needs no Windows.</summary>
/// <param name="install">The game's install; null: no game folder to read.</param>
/// <param name="settingsFiles">The game's settings files (the graphics and options files in %APPDATA%).</param>
/// <param name="outFolder">Where the zip is written; default a folder in the temp folder.</param>
/// <param name="processes">Names of the running processes (without .exe).</param>
/// <param name="antivirus">Antivirus products as Windows reports them.</param>
/// <param name="gpu">Graphics card, driver and screen lines.</param>
/// <param name="events">Application events of the last 7 days naming the exe, each one formatted.</param>
public sealed class Diagnostics(Func<GameInstall?> install, IReadOnlyList<string> settingsFiles, string version, Func<IReadOnlyList<GameRun>> runs, string? outFolder = null,
    Func<IEnumerable<string>>? processes = null, Func<IEnumerable<string>>? antivirus = null, Func<IEnumerable<string>>? gpu = null, Func<string, IEnumerable<string>>? events = null)
{
    public const long Limit = 5 * 1024 * 1024;
    const int LogBytes = 2 * 1024 * 1024, NetLogs = 5, EventDays = 7, EventsMax = 20, EventChars = 2000;
    static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(10);
    const string Removed = "[line removed: it may hold a credential]";

    /// <summary>Game launchers, boosters and overlays that hook into or squeeze games; running ones are named in system.txt. Data: a new one is a new line.</summary>
    static readonly (string Process, string Name)[] Troublemakers =
    [
        ("RazerCortex", "Razer Cortex"), ("Razer Cortex", "Razer Cortex"), ("WiseGameBooster", "Wise Game Booster"), ("GameFire", "Game Fire"), ("ProcessLasso", "Process Lasso"),
        ("MSIAfterburner", "MSI Afterburner"), ("RTSS", "RivaTuner Statistics Server"), ("RTSSHooksLoader64", "RivaTuner Statistics Server"),
        ("GameBar", "Xbox Game Bar"), ("GameBarFTServer", "Xbox Game Bar"), ("NVIDIA Overlay", "NVIDIA overlay"), ("NVIDIA Share", "NVIDIA overlay"),
        ("RadeonSoftware", "AMD Radeon software"), ("Overwolf", "Overwolf"), ("OverwolfBrowser", "Overwolf"), ("Discord", "Discord (overlay)"),
        ("obs64", "OBS Studio"), ("bdcam", "Bandicam"), ("Fraps", "Fraps"), ("Medal", "Medal"), ("wallpaper64", "Wallpaper Engine"), ("wallpaper32", "Wallpaper Engine"),
        ("GameOverlayUI", "Steam overlay"), ("ReShade", "ReShade"), ("AutoHotkey", "AutoHotkey"), ("MsiDragonCenter", "MSI Center"), ("ArmouryCrate.Service", "ASUS Armoury Crate"),
    ];

    /// <summary>A line that looks like it carries a credential (a JWT, a token, cookie, bearer or authorization header, or a run of 32 or more hex digits such as the game's session id and refresh token) is replaced as a whole.</summary>
    static readonly Regex Secret = new(@"eyJ[\w-]{8,}\.[\w-]{8,}|token|cookie|bearer|authorization|\bjwt\b|secret|password|[0-9a-f]{32,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Scrub(string text) => string.Join('\n', text.Split('\n').Select(l => Secret.IsMatch(l) ? Removed : l));

    /// <summary>Builds the zip; never throws for what it reads. ponytail: a zip over <see cref="Limit"/> is rebuilt with each file cut to a quarter, until it fits.</summary>
    public ProblemReport Build(string trigger)
    {
        var missing = new List<string>();
        var exe = install() is { } g ? Path.GetFileName(g.ClientExe) : null;
        var system = SystemText(missing, exe);
        var events = exe is null ? "no game folder" : EventsText(missing, exe);
        var run = JsonSerializer.Serialize(Try(() => runs(), missing, "runs.json") ?? [], RunHistory.Json);
        ProblemReport r;
        for (var cap = LogBytes; ; cap /= 4)
            if ((r = Zip(trigger, cap, new(missing), system, events, run)).Bytes <= Limit || cap < 4096) return r;
    }

    ProblemReport Zip(string trigger, int cap, List<string> missing, string system, string events, string runsJson)
    {
        var files = new List<string>();
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Add(string name, string text, Encoding enc)
            {
                using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                s.Write(enc.GetBytes(Scrub(text)));
                if (!files.Contains(name)) files.Add(name);
            }
            void AddFile(string name, string path)
            {
                if (Tail(path, cap) is { } read) Add(name, read.Text, read.Encoding);
                else missing.Add(name);
            }

            Add("system.txt", system, Encoding.UTF8);
            Add("events.txt", events, Encoding.UTF8);
            Add("runs.json", runsJson, Encoding.UTF8);
            if (install() is { } g && Path.GetDirectoryName(g.ClientExe) is { } folder && Directory.Exists(folder))
            {
                AddFile("game/debug.log", Path.Combine(folder, "debug.log"));
                AddFile("game/console.log", Path.Combine(folder, "console.log"));
                AddFile("game/cef.log", Path.Combine(folder, "cef.log"));
                AddFile("game/.sentry-native/last_crash", Path.Combine(folder, ".sentry-native", "last_crash"));
                // netlogs carry tokens: only through the line scrub, and only the newest
                var logs = Try(() => Directory.EnumerateFiles(folder, "*.netlog").OrderByDescending(File.GetLastWriteTimeUtc).Take(NetLogs).ToList(), missing, "game/*.netlog") ?? [];
                if (logs.Count == 0 && !missing.Contains("game/*.netlog")) missing.Add("game/*.netlog");
                foreach (var f in logs) AddFile("game/" + Path.GetFileName(f), f);
            }
            else missing.Add("game folder");
            foreach (var f in settingsFiles) AddFile("settings/" + Path.GetFileName(f), f);
            files.Add("report.json");
            Add("report.json", JsonSerializer.Serialize(new { trigger, created = DateTimeOffset.UtcNow, launcher = version, files, missing }, RunHistory.Json), Encoding.UTF8);
        }
        var folderOut = outFolder ?? Path.Combine(Path.GetTempPath(), "hunter-resurgence");
        try
        {
            Directory.CreateDirectory(folderOut);
            // one report at a time: the older ones are the player's already saved or sent
            foreach (var old in Try(() => Directory.GetFiles(folderOut, "hunter-resurgence-report-*.zip"), [], "") ?? []) Try(() => { File.Delete(old); return old; }, [], "");
            var path = Path.Combine(folderOut, $"hunter-resurgence-report-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
            File.WriteAllBytes(path, ms.ToArray());
            return new(path, files, missing, ms.Length, trigger);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // no place for the zip (the temp folder is full, locked or not allowed): a report without a file, which Send and Save refuse (ProblemReporter)
            return new("", files, [.. missing, "report zip"], 0, trigger);
        }
    }

    /// <summary>The last <paramref name="cap"/> bytes of a file (from the start of a line), read although the game still writes it, and the encoding to write it back with: Latin-1 keeps every byte of a one-byte
    /// text, a UTF-16 text (a byte order mark, or the zero bytes of mostly ASCII text) is decoded so that the scrub sees its lines, and goes into the zip as UTF-8. Null when it is missing or locked.</summary>
    static (string Text, Encoding Encoding)? Tail(string path, int cap)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var head = new byte[(int)Math.Min(f.Length, 4096)];
            f.ReadExactly(head);
            var utf16 = Utf16Of(head);
            var n = Math.Min(f.Length, cap);
            var start = f.Length - n;
            if (utf16 is not null) // whole characters, counted from the start of the file
            {
                start += start % 2;
                n = (f.Length - start) & ~1L;
            }
            var buf = new byte[n];
            f.Seek(start, SeekOrigin.Begin);
            f.ReadExactly(buf);
            var text = (utf16 ?? Encoding.Latin1).GetString(buf);
            if (start == 0 && text.StartsWith('\uFEFF')) text = text[1..];
            if (start > 0 && text.IndexOf('\n') is >= 0 and var i) text = text[(i + 1)..];
            return (text, utf16 is null ? Encoding.Latin1 : Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>UTF-16 by its byte order mark, or by a quarter or more of the first bytes being zero (ASCII text in UTF-16 is half zeros); null for a one-byte text.</summary>
    static Encoding? Utf16Of(byte[] head)
    {
        if (head is [255, 254, ..]) return Encoding.Unicode;
        if (head is [254, 255, ..]) return Encoding.BigEndianUnicode;
        int even = 0, odd = 0;
        for (var i = 0; i < head.Length; i++)
            if (head[i] == 0) { if (i % 2 == 0) even++; else odd++; }
        return head.Length >= 4 && even + odd >= head.Length / 4 ? odd >= even ? Encoding.Unicode : Encoding.BigEndianUnicode : null;
    }

    string SystemText(List<string> missing, string? exe)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Launcher: {version}");
        sb.AppendLine($"Windows: {RuntimeInformation.OSDescription} ({Environment.OSVersion.Version})");
        sb.AppendLine($"Windows language: {CultureInfo.InstalledUICulture.Name}");
        foreach (var line in Try(() => (gpu ?? Gpu)().ToList(), missing, "graphics card") ?? []) sb.AppendLine(line);
        var av = Try(() => (antivirus ?? Antivirus)().Distinct().ToList(), missing, "antivirus");
        sb.AppendLine(av is null ? "Antivirus: unknown" : av.Count == 0 ? "Antivirus: none reported" : "Antivirus: " + string.Join(", ", av));
        var running = Try(() => (processes ?? Processes)().ToHashSet(StringComparer.OrdinalIgnoreCase), missing, "running programs");
        var names = running is null ? [] : Troublemakers.Where(t => running.Contains(t.Process)).Select(t => $"{t.Name} ({t.Process})").Distinct().ToList();
        sb.AppendLine(running is null ? "Boosters and overlays: unknown" : names.Count == 0 ? "Boosters and overlays: none running" : "Boosters and overlays running: " + string.Join(", ", names));
        return sb.ToString();
    }

    string EventsText(List<string> missing, string exe)
    {
        Func<string, IEnumerable<string>> read = events ?? ReadEvents;
        var list = Try(() => read(exe).ToList(), missing, "events.txt");
        return list is null ? "The Windows event log could not be read." : list.Count == 0 ? $"No Application Error or Windows Error Reporting event of the last {EventDays} days names {exe}." : string.Join("\n\n", list);
    }

    /// <summary>Runs a probe; a probe that fails (Windows services off, no rights, a vanished file) is listed as missing instead of failing the report.</summary>
    static T? Try<T>(Func<T> probe, List<string> missing, string name) where T : class
    {
        try { return probe(); }
        catch (Exception) { if (name.Length > 0) missing.Add(name); return null; } // a report builds whatever a probe does
    }

    static IEnumerable<string> Processes()
    {
        var all = Process.GetProcesses();
        try { return all.Select(p => p.ProcessName).ToList(); }
        finally { foreach (var p in all) p.Dispose(); }
    }

    static IEnumerable<string> Antivirus()
    {
        using var s = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT displayName FROM AntiVirusProduct") { Options = { Timeout = WmiTimeout } };
        foreach (var o in s.Get()) using (o) if (o["displayName"] is string n) yield return n;
    }

    static IEnumerable<string> Gpu()
    {
        using var s = new ManagementObjectSearcher("SELECT Name, DriverVersion, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController") { Options = { Timeout = WmiTimeout } };
        foreach (var o in s.Get())
            using (o) yield return $"Graphics: {o["Name"]}, driver {o["DriverVersion"]}, screen {o["CurrentHorizontalResolution"]}x{o["CurrentVerticalResolution"]} at {o["CurrentRefreshRate"]} Hz";
    }

    /// <summary>The last <see cref="EventDays"/> days of Application Error (1000) and Windows Error Reporting (1001) events whose text names the exe, newest first, at most <see cref="EventsMax"/>.</summary>
    static IEnumerable<string> ReadEvents(string exe)
    {
        var age = (long)TimeSpan.FromDays(EventDays).TotalMilliseconds;
        using var reader = new EventLogReader(new EventLogQuery("Application", PathType.LogName, $"*[System[(EventID=1000 or EventID=1001) and TimeCreated[timeDiff(@SystemTime) <= {age}]]]") { ReverseDirection = true });
        var found = new List<string>();
        while (found.Count < EventsMax && reader.ReadEvent() is { } e)
            using (e)
                if (e.FormatDescription() is { } text && text.Contains(exe, StringComparison.OrdinalIgnoreCase))
                    found.Add($"{e.TimeCreated:u}  event {e.Id}  {e.ProviderName}\n{(text.Length > EventChars ? text[..EventChars] + " [cut]" : text)}");
        return found;
    }
}
