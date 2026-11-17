// The game's crash and error reports: what the crash host receives is read, reduced to what helps an operator and kept per account for
// 30 days (docs/server.md#crash-reports). Everything else of a report (device, user name, machine, install paths) is dropped.
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Resurgence.Server;

public static class CrashReports
{
    public const int KeepDays = 30;
    /// <summary>telemetry.jsonl is cut to its newest lines under this size when the server starts (<see cref="TrimTelemetry"/>).</summary>
    public const long TelemetryCap = 32L << 20;
    const int PerAccount = 200, MaxFrames = 8, MaxText = 300, MaxItems = 8;

    sealed record Image(ulong Start, ulong Size, string Name);
    sealed record Entry(string Id, long Account, string Kind, long? Time, string? Module, string[] Frames, string Message, string? Release);

    // a Windows path (drive or share, folders that may hold spaces, then the file name): only the file name stays
    static readonly Regex InstallPath = new(@"(?:[A-Za-z]:|\\\\[^\\/:*?""<>|\s]+)[\\/](?:[^\\/:*?""<>|\r\n]+[\\/])*([^\\/:*?""<>|\s]*)", RegexOptions.Compiled);
    static readonly Regex Id = new(@"\A[0-9A-Za-z-]{8,64}\z", RegexOptions.Compiled);

    /// <summary>The text with every install path reduced to its file name, cut to 300 characters.</summary>
    public static string Clean(string text)
    {
        var s = InstallPath.Replace(text, "$1");
        return s.Length > MaxText ? s[..MaxText] : s;
    }

    static string? T(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static JsonObject? Obj(JsonNode? n, string key) => (n as JsonObject)?[key] as JsonObject;

    static long? Account(JsonNode? n) =>
        (n is JsonValue v && v.TryGetValue<long>(out var l) ? l.ToString(CultureInfo.InvariantCulture) : T(n)) is { } s
        && long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

    static ulong? Hex(string? s)
    {
        if (s is null) return null;
        var digits = s.Length > 2 && s[0] == '0' && (s[1] | 32) == 'x' ? s.AsSpan(2) : s;
        return ulong.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>The time of a report's own clock: epoch seconds or an RFC 3339 text.</summary>
    static long? Time(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<double>(out var d) ? (long)d
        : DateTimeOffset.TryParse(T(n), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t.ToUnixTimeSeconds() : null;

    /// <summary>The items of an envelope (header line, then per item a header line and its payload, `length` bytes or the rest of the line):
    /// the fatal and error events and the abnormal sessions with an account id; everything else is noise.</summary>
    static List<Entry> Parse(byte[] body)
    {
        var found = new List<Entry>();
        var pos = Array.IndexOf(body, (byte)'\n') + 1;
        for (var n = 0; pos > 0 && pos < body.Length && n < MaxItems; n++)
        {
            var eol = Array.IndexOf(body, (byte)'\n', pos);
            if (eol < 0) eol = body.Length;
            try
            {
                var head = JsonNode.Parse(body.AsSpan(pos, eol - pos)) as JsonObject;
                pos = eol + 1;
                if (head is null) break;
                int end;
                if (head["length"] is JsonValue l && l.TryGetValue<int>(out var length) && length >= 0)
                    end = pos + length;
                else
                    end = pos < body.Length && Array.IndexOf(body, (byte)'\n', pos) is var next and >= 0 ? next : body.Length;
                if (end > body.Length || end < pos) break;
                var type = T(head["type"]);
                if (type is "event" or "session" && JsonNode.Parse(body.AsSpan(pos, end - pos)) is JsonObject payload
                    && (type == "event" ? FromEvent(payload) : FromSession(payload)) is { } entry)
                    found.Add(entry);
                pos = end + 1;
            }
            catch (System.Text.Json.JsonException)
            {
                break;  // a cut or garbled item: the rest of the body can't be told apart
            }
        }
        return found;
    }

    static Entry? FromEvent(JsonObject e)
    {
        var level = T(e["level"])?.ToLowerInvariant() ?? "error";
        if (level is not ("fatal" or "error") || T(e["event_id"]) is not { } id || !Id.IsMatch(id) || Account(Obj(e, "user")?["id"]) is not { } account)
            return null;
        var images = (Obj(e, "debug_meta")?["images"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(i => (Start: Hex(T(i["image_addr"])), Size: Hex(T(i["image_size"])) ?? (i["image_size"] is JsonValue v && v.TryGetValue<ulong>(out var s) ? s : 0), Name: T(i["code_file"])))
            .Where(i => i.Start is not null && i.Name is not null).Select(i => new Image(i.Start!.Value, i.Size, Clean(i.Name!).Split('\\', '/')[^1])).ToList();
        var thrown = (Obj(e, "exception") is { } ex ? ex["values"] : e["exception"]) is JsonArray values ? values.OfType<JsonObject>().LastOrDefault() : null;
        var stack = Obj(thrown, "stacktrace") ?? (Obj(e, "threads")?["values"] as JsonArray)?.OfType<JsonObject>().Select(t => Obj(t, "stacktrace")).FirstOrDefault(s => s is not null);
        var frames = (stack?["frames"] as JsonArray ?? []).OfType<JsonObject>().Reverse().Take(MaxFrames).Select(f => Frame(f, images)).ToArray();
        var message = thrown is not null ? $"{T(thrown["type"])}: {T(thrown["value"])}".Trim(' ', ':')
            : T(e["message"]) ?? T(Obj(e, "message")?["formatted"]) ?? T(Obj(e, "logentry")?["formatted"]) ?? T(Obj(e, "logentry")?["message"]) ?? "";
        return new(id, account, level, Time(e["timestamp"]), frames.Length > 0 ? frames[0].Module : null, frames.Select(f => f.Text).ToArray(), Clean(message), Release(e));
    }

    static string? Release(JsonObject o) => (T(o["release"]) ?? T(Obj(o, "attrs")?["release"])) is { } r ? Clean(r) : null;

    // a frame as "module!function" or "module+offset" (the module found by the address; the offset in hex), else what the report names it
    static (string Text, string? Module) Frame(JsonObject f, List<Image> images)
    {
        var addr = Hex(T(f["instruction_addr"]));
        var image = addr is { } a ? images.FirstOrDefault(i => a >= i.Start && a - i.Start < i.Size) : null;
        var function = T(f["function"]) is { } fn ? Clean(fn) : null;
        if (image is not null)
            return (function is not null ? $"{image.Name}!{function}" : $"{image.Name}+{addr!.Value - image.Start:x}", image.Name);
        return (function ?? ((T(f["module"]) ?? T(f["package"])) is { } m ? Clean(m).Split('\\', '/')[^1] : T(f["instruction_addr"]) ?? "?"), null);
    }

    // an abnormal session is a run that ended without a crash report or a clean exit: the account is its `did` when that is a user id
    static Entry? FromSession(JsonObject s) =>
        T(s["status"]) == "abnormal" && T(s["sid"]) is { } id && Id.IsMatch(id) && Account(s["did"]) is { } account
            ? new(id, account, "abnormal", Time(s["timestamp"]) ?? Time(s["started"]), null, [], "the game ended without a crash report or a clean exit", Release(s))
            : null;

    /// <summary>Keep the reports of a crash-host request (`received`: epoch seconds): one row per event id, for the account in the event
    /// (an unknown account or an older report than 30 days is dropped; an account keeps its newest 200). Returns the rows added.</summary>
    public static int Keep(Store store, byte[] body, long received)
    {
        var added = 0;
        foreach (var e in Parse(body))
        {
            var ts = Math.Min(e.Time ?? received, received);
            if (ts < received - KeepDays * 86400L) continue;
            using var con = store.Open();
            added += con.Exec("INSERT OR IGNORE INTO crash_reports (account_id, event_id, ts, kind, module, frames, message, release)"
                              + " SELECT ?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8 WHERE EXISTS (SELECT 1 FROM accounts WHERE id = ?1)",
                              e.Account, e.Id, ts, e.Kind, e.Module, Py.Dumps(new JsonArray([.. e.Frames.Select(f => (JsonNode?)f)]), compact: true), e.Message, e.Release);
            con.Exec("DELETE FROM crash_reports WHERE account_id = ?1 AND n <= (SELECT n FROM crash_reports WHERE account_id = ?1 ORDER BY n DESC LIMIT 1 OFFSET ?2)", e.Account, PerAccount);
        }
        return added;
    }

    /// <summary>Delete the reports older than 30 days.</summary>
    public static void Purge(Store store, long now)
    {
        using var con = store.Open();
        con.Exec("DELETE FROM crash_reports WHERE ts < ?", now - KeepDays * 86400L);
    }

    /// <summary>One kept report as the operator sees it.</summary>
    public sealed record Row(long Ts, string Kind, string? Module, string[] Frames, string Message, string? Release);

    /// <summary>The account's reports since `since` (epoch seconds), newest first.</summary>
    public static List<Row> Recent(Store store, long account, long since)
    {
        using var con = store.Open();
        return [.. con.Rows("SELECT ts, kind, module, frames, message, release FROM crash_reports WHERE account_id = ? AND ts >= ? ORDER BY ts DESC, n DESC", account, since)
            .Select(r => new Row((long)r[0], (string)r[1], r[2] as string, JsonNode.Parse((string)r[3])!.AsArray().Select(f => (string)f!).ToArray(), (string)r[4], r[5] as string))];
    }

    /// <summary>When telemetry.jsonl is over `cap` bytes: rewrite it with the newest lines of every host but the crash host that fit, once, at start. The
    /// crash host's lines (the whole body of every report, install paths and device included) go through <see cref="Keep"/> first when they are younger than
    /// 30 days and there is a store, so what the old file holds is not lost.</summary>
    public static void TrimTelemetry(string path, string crashHost, Store? store, long now, long cap = TelemetryCap)
    {
        if (!File.Exists(path) || new FileInfo(path).Length <= cap) return;
        var kept = new Queue<string>();
        long size = 0;
        foreach (var line in File.ReadLines(path))
        {
            JsonObject? o;
            try { o = JsonNode.Parse(line) as JsonObject; }
            catch (System.Text.Json.JsonException) { continue; }
            if (o is null) continue;
            if (T(o["host"]) == crashHost)
            {
                var received = DateTimeOffset.TryParse(T(o["ts"]), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t.ToUnixTimeSeconds() : now;
                if (store is not null && received >= now - KeepDays * 86400L && T(o["body"]) is { } body)
                    Keep(store, Encoding.UTF8.GetBytes(body), received);
                continue;
            }
            kept.Enqueue(line);
            size += line.Length + Environment.NewLine.Length;
            while (size > cap && kept.Count > 0)
                size -= kept.Dequeue().Length + Environment.NewLine.Length;
        }
        var tmp = path + ".trim";
        File.WriteAllLines(tmp, kept, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
