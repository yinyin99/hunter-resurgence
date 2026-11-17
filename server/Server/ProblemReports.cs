// The problem reports the launcher sends (POST /account/report): one zip per report, kept for the operator for 30 days
// as <data_dir>/reports/<account>/<reference>.zip with <reference>.json beside it (docs/server.md#problem-reports).
// The reference is p<account>-<first 8 hex of the zip's SHA-256>. Never restored, only read by the operator.
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Resurgence.Server;

public sealed class ProblemReports(State state, string dataDir)
{
    public const long MaxBytes = 6L << 20;
    public const int PerDay = 5, KeepDays = 30;
    public static readonly string[] Triggers = ["manual", "short", "exitcode", "nowindow"];

    /// <summary>Called with the account, handle, reference, trigger and size in bytes when a new report is kept (the server sets it to <see cref="Alerts.ProblemReport"/> on the review webhook).</summary>
    public Action<long, string, string, string, long> Alert { get; init; } = (_, _, _, _, _) => { };
    /// <summary>A report is refused (507) when the data disk would have less than this free after it (the setting `upload_min_free_bytes`).</summary>
    public long MinFreeBytes { get; init; } = Uploads.MinFreeBytesDefault;
    /// <summary>Free bytes of the data disk; a test replaces it.</summary>
    public Func<long> FreeBytes { get; init; } = () => new DriveInfo(Path.GetFullPath(dataDir)).AvailableFreeSpace;

    readonly ConcurrentDictionary<long, byte> inFlight = new();
    static readonly Regex Reference = new(@"\Ap(\d+)-([0-9a-f]{8})\z", RegexOptions.Compiled);

    static UploadOutcome TooLarge => new(413, "too_large", "A problem report is at most 6 MB.");

    string Dir(long account) => Path.Combine(dataDir, "reports", account.ToString(CultureInfo.InvariantCulture));

    /// <summary>One report as the operator lists it.</summary>
    public sealed record Item(string Reference, long Account, string? Handle, long Ts, string Trigger, long Size);

    static (long Ts, string Trigger, long Size)? Sidecar(string json)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(json)) is JsonObject o && o["ts"] is JsonValue ts && o["trigger"] is JsonValue trigger && o["size"] is JsonValue size
                ? (ts.GetValue<long>(), trigger.GetValue<string>(), size.GetValue<long>()) : null;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return null;  // being written, or not ours
        }
    }

    IEnumerable<Item> Of(long account) =>
        (Directory.Exists(Dir(account)) ? Directory.EnumerateFiles(Dir(account), "*.json") : [])
            .Select(f => (File: f, Meta: Sidecar(f))).Where(x => x.Meta is not null)
            .Select(x => new Item(Path.GetFileNameWithoutExtension(x.File), account, null, x.Meta!.Value.Ts, x.Meta.Value.Trigger, x.Meta.Value.Size));

    /// <summary>Start a report of `account` (size: the declared body size, null when unknown), refusing it when the trigger is none of ours, the body is over 6 MiB, one is
    /// in flight already, the account sent 5 in the last 24 h, or the disk is low. Null: go on, and call <see cref="End"/> when done.</summary>
    public UploadOutcome? Begin(long account, string trigger, long? size, long now)
    {
        if (!Triggers.Contains(trigger)) return new(400, "invalid", "Unknown trigger.");
        if (size > MaxBytes) return TooLarge;
        if (!inFlight.TryAdd(account, 0)) return new(429, "busy", "A report of yours is already being received.");
        UploadOutcome? refused =
            Of(account).Count(i => i.Ts > now - 86400) >= PerDay ? new(429, "quota", $"You sent {PerDay} problem reports in the last 24 hours. Try again later.")
            : FreeBytes() - (size ?? MaxBytes) < MinFreeBytes ? new(507, "paused", "The server takes no reports for now, try again later.")
            : null;
        if (refused is not null) End(account);
        return refused;
    }

    /// <summary>Is a report of this account being received (a delete waits for it).</summary>
    public bool Busy(long account) => inFlight.ContainsKey(account);

    public void End(long account) => inFlight.TryRemove(account, out _);

    /// <summary>Keep the zip (a body that is no readable zip is 400, one over 6 MiB 413); the same zip twice is one report. Returns the refusal or the reference.</summary>
    public (UploadOutcome? Refused, string? Reference) Save(long account, string trigger, byte[] zip, long now)
    {
        if (zip.Length > MaxBytes) return (TooLarge, null);
        try
        {
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            return (new(400, "invalid", "The report is not a zip."), null);
        }
        var reference = $"p{account}-{Convert.ToHexStringLower(SHA256.HashData(zip))[..8]}";
        var file = Path.Combine(Dir(account), reference);
        var isNew = !File.Exists(file + ".zip");
        Directory.CreateDirectory(Dir(account));
        File.WriteAllBytes(file + ".zip", zip);
        File.WriteAllText(file + ".json", new JsonObject { ["ts"] = now, ["trigger"] = trigger, ["size"] = zip.Length }.ToJsonString());
        // an account deleted meanwhile: its folder goes now (the delete removes the account first, then the folder, so one of the two always does)
        if (!state.Store.AccountExists(account))
        {
            Directory.Delete(Dir(account), recursive: true);
            return (new(401, "signed_out", "Sign in first."), null);
        }
        if (isNew)
            Alert(account, state.Store.HandleOf(account) ?? "", reference, trigger, zip.Length);
        return (null, reference);
    }

    /// <summary>The reports of every account, newest first.</summary>
    public List<Item> List()
    {
        var root = Path.Combine(dataDir, "reports");
        return [.. (Directory.Exists(root) ? Directory.EnumerateDirectories(root) : [])
            .Select(d => long.TryParse(Path.GetFileName(d), out var a) ? a : (long?)null).Where(a => a is not null)
            .SelectMany(a => Of(a!.Value).Select(i => i with { Handle = state.Store.HandleOf(i.Account) })).OrderByDescending(i => i.Ts).ThenBy(i => i.Reference, StringComparer.Ordinal)];
    }

    /// <summary>The kept zip of a reference, null when the reference is none or the report is gone.</summary>
    public string? PathOf(string reference) =>
        Reference.Match(reference) is { Success: true } m && long.TryParse(m.Groups[1].Value, out var account)
        && Path.Combine(Dir(account), reference + ".zip") is var zip && File.Exists(zip) ? zip : null;

    /// <summary>Delete the reports older than 30 days (a zip without its note is aged by its file time).</summary>
    public void Purge(long now)
    {
        var root = Path.Combine(dataDir, "reports");
        if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            foreach (var report in Directory.EnumerateFiles(dir).GroupBy(f => Path.ChangeExtension(f, null)))
            {
                var ts = Sidecar(report.Key + ".json")?.Ts ?? new DateTimeOffset(File.GetLastWriteTimeUtc(report.First())).ToUnixTimeSeconds();
                if (ts < now - KeepDays * 86400L)
                    foreach (var f in report) File.Delete(f);
            }
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
    }
}
