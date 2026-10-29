using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Resurgence.Restore;

/// <summary>The caps of a backup zip read for restore, all in one place. A zip over any of them is refused.
/// MaxRatio applies to entries of at least RatioMinBytes (uncompressed bytes per compressed byte).</summary>
public sealed record Limits(int Entries = 20_000, long TotalBytes = 1L << 30, int EntryBytes = 64 << 20, int ManifestBytes = 64 << 20, int JsonDepth = 64,
    int MaxRatio = 100, int RatioMinBytes = 1 << 20)
{
    public static readonly Limits Default = new();
}

/// <summary>A saved JSON reply that passed the checks. Endpoint: from the manifest, else mapped back from the file name ("" when unknown).</summary>
sealed record SavedFile(string Path, string Endpoint, IReadOnlyDictionary<string, string> Params, int? Status, JsonNode? Json);

sealed record BackupContent(JsonObject Manifest, string Uid, int Format, IReadOnlyList<SavedFile> Files, int OtherFiles,
    IReadOnlyList<string> Failed, IReadOnlyList<string> Rejected, IReadOnlyList<string> Missing);

/// <summary>Reads a backup zip once, in memory (nothing is extracted), and checks it in this order: caps and entry names, manifest, uid, claim key,
/// then each listed file's SHA-256. A bad file is rejected and reported; a bad zip throws <see cref="RestoreException"/>.</summary>
static class BackupReader
{
    /// <summary>The newest manifest format this reader knows.</summary>
    public const int KnownFormat = 2;

    public static BackupContent Read(Stream stream, Limits lim)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException) { throw new RestoreException("not a zip"); }
        using (zip)
        {
            try { return Read(zip, lim); }
            catch (InvalidDataException e) { throw new RestoreException("damaged zip: " + e.Message); }
        }
    }

    static BackupContent Read(ZipArchive zip, Limits lim)
    {
        if (zip.Entries.Count > lim.Entries) throw new RestoreException($"too many entries: {zip.Entries.Count} (cap {lim.Entries})");
        foreach (var e in zip.Entries)
            if (!SafeName(e.FullName)) throw new RestoreException($"unsafe entry name: {e.FullName}");
        var entries = zip.Entries.Where(e => !e.FullName.EndsWith('/')).ToList();
        long total = 0;

        // the tool zips the folder's content; a zip made by hand may keep the folder as one level (as BackupZip accepts)
        var mEntry = entries.FirstOrDefault(e => BackupZip.IsAt(e, "manifest.json")) ?? throw new RestoreException("no manifest.json");
        var prefix = mEntry.FullName[..^"manifest.json".Length];
        var m = Parse(Bytes(mEntry, lim.ManifestBytes, lim, ref total, null) ?? throw new RestoreException($"manifest.json is over {lim.ManifestBytes} bytes"), lim) as JsonObject
            ?? throw new RestoreException("manifest.json is not a JSON object (or nested too deep)");

        var uid = Text(m["uid"]) ?? throw new RestoreException("manifest has no uid");
        if (uid.Length == 0 || !uid.All(char.IsAsciiDigit)) throw new RestoreException("manifest uid is not digits only");
        var format = 1; // no format: every release before format 2
        if (m["format"] is { } f)
        {
            format = f is JsonValue fv && fv.TryGetValue<int>(out var n) ? n : throw new RestoreException("manifest format is not a number");
            if (format > KnownFormat) throw new RestoreException($"made by a newer backup tool (format {format}); this reader knows formats up to {KnownFormat}");
            if (format < 1) throw new RestoreException($"unknown manifest format {format}");
        }

        var hash = Text(m["claim_hash"]) ?? throw new RestoreException("manifest has no claim_hash");
        var kEntry = entries.FirstOrDefault(e => e.FullName == prefix + "claim.key") ?? throw new RestoreException("no claim.key");
        var key = Bytes(kEntry, 1024, lim, ref total, null);
        if (key is null || !Convert.ToHexStringLower(SHA256.HashData(key)).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new RestoreException("claim.key does not match the manifest's claim_hash");

        var failed = new List<string>();
        var rejected = new List<string>();
        var missing = new List<string>();
        var files = new List<SavedFile>();
        var listed = new HashSet<string>(StringComparer.Ordinal) { mEntry.FullName, kEntry.FullName };
        var byName = entries.GroupBy(e => e.FullName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        int other = 0;
        foreach (var fe in (m["files"] as JsonArray ?? []).OfType<JsonObject>().OrderBy(x => Text(x["path"]), StringComparer.Ordinal))
        {
            if (Text(fe["path"]) is not { Length: > 0 } path) continue;
            listed.Add(prefix + path);
            if (Text(fe["error"]) is { } err) { failed.Add($"{path}: {err}"); continue; }
            if (Text(fe["sha256"]) is not { } sha) { failed.Add($"{path}: no sha256"); continue; }
            if (!byName.TryGetValue(prefix + path, out var ze)) { missing.Add($"{path} (listed in the manifest, not in the zip)"); continue; }
            var json = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var bytes = Bytes(ze, lim.EntryBytes, lim, ref total, h) ?? throw new RestoreException($"entry over {lim.EntryBytes} bytes: {path}");
            if (!Convert.ToHexStringLower(h.GetHashAndReset()).Equals(sha, StringComparison.OrdinalIgnoreCase)) { rejected.Add($"{path}: sha256 does not match the manifest"); continue; }
            if (fe["derived"] is JsonValue d && d.TryGetValue<bool>(out var isDerived) && isDerived) continue; // made by the tool: the raw replies decide
            if (!json) { other++; continue; }
            JsonNode? node;
            try { node = JsonNode.Parse(bytes, documentOptions: new() { MaxDepth = lim.JsonDepth }); }
            catch (JsonException) { rejected.Add($"{path}: not JSON or nested deeper than {lim.JsonDepth}"); continue; }
            var (ep, ps) = Text(fe["endpoint"]) is { } e && e.StartsWith('/')
                ? (e, (fe["params"] as JsonObject ?? []).ToDictionary(p => p.Key, p => Text(p.Value) ?? "", StringComparer.Ordinal))
                : Endpoints.FromName(path) ?? ("", new Dictionary<string, string>());
            files.Add(new(path, ep, ps, fe["status"] is JsonValue s && s.TryGetValue<int>(out var st) ? st : null, node));
        }
        foreach (var e in entries.Where(e => !listed.Contains(e.FullName)).OrderBy(e => e.FullName, StringComparer.Ordinal))
            rejected.Add($"{e.FullName}: not listed in the manifest");
        return new(m, uid, format, files, other, failed, rejected, missing);
    }

    /// <summary>Zip-slip: no "..", no rooted path, no drive letter, no backslash.</summary>
    static bool SafeName(string n) =>
        n.Length > 0 && !n.Contains('\\') && !n.Contains(':') && !n.StartsWith('/') && n.Split('/').All(p => p != ".." && p != ".");

    static JsonNode? Parse(byte[] b, Limits lim)
    {
        try { return JsonNode.Parse(b, documentOptions: new() { MaxDepth = lim.JsonDepth }); }
        catch (JsonException) { return null; }
    }

    /// <summary>A string or a number as text; null for anything else.</summary>
    internal static string? Text(JsonNode? n) => n is JsonValue v ? v.TryGetValue<string>(out var s) ? s : v.GetValueKind() == JsonValueKind.Number ? v.ToJsonString() : null : null;

    /// <summary>The entry's bytes (hashed into h when given), or null over the cap. The declared sizes are not trusted: the bytes are counted as they come.</summary>
    static byte[]? Bytes(ZipArchiveEntry e, int cap, Limits lim, ref long total, IncrementalHash? h)
    {
        using var s = e.Open();
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = s.Read(buf)) > 0)
        {
            if (ms.Length + n > cap) return null;
            if ((total += n) > lim.TotalBytes) throw new RestoreException($"zip content over {lim.TotalBytes} bytes");
            ms.Write(buf, 0, n);
            if (ms.Length >= lim.RatioMinBytes && ms.Length > Math.Max(1, e.CompressedLength) * lim.MaxRatio)
                throw new RestoreException($"entry compressed more than {lim.MaxRatio}:1: {e.FullName}");
        }
        h?.AppendData(ms.GetBuffer(), 0, (int)ms.Length);
        return ms.ToArray();
    }
}
