using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Resurgence.Restore;

public enum BackupError { TooBig, Unreadable, NotZip, NoManifest, BadManifest, NoClaimKey, ClaimMismatch }

/// <summary>What a backup zip says about itself, read from its manifest.json. Endpoints = (endpoint, files, files without a result).</summary>
public sealed record BackupInfo(string Uid, string? Handle, string ToolVersion, DateTime StartedUtc, IReadOnlyList<string> Groups, string? SteamId64,
    int Files, int Failed, IReadOnlyList<(string Endpoint, int Files, int Failed)> Endpoints);

public sealed record BackupResult(BackupInfo? Info, BackupError? Error);

/// <summary>Local, read-only check of a backup zip made by the backup tool: manifest.json parses and claim.key hashes to the manifest's claim_hash. Nothing is sent anywhere.</summary>
public static class BackupZip
{
    public const long MaxZipBytes = 512L << 20;
    const int MaxManifestBytes = 64 << 20;

    static readonly JsonSerializerOptions J = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    sealed record Entry(string? Endpoint, string? Error);
    sealed record Manifest(string? ToolVersion, List<string>? Groups, string? Uid, string? Handle, string? ClaimHash, DateTime? StartedUtc, List<Entry>? Files, [property: JsonPropertyName("steam_id64")] string? SteamId64);

    public static BackupResult Read(string path)
    {
        try
        {
            if (new FileInfo(path).Length > MaxZipBytes) return new(null, BackupError.TooBig);
            using var zip = ZipFile.OpenRead(path);
            // the tool zips the folder's content; a zip made by hand may keep the folder as one level
            var mEntry = zip.Entries.FirstOrDefault(e => IsAt(e, "manifest.json"));
            if (mEntry is null) return new(null, BackupError.NoManifest);
            var prefix = mEntry.FullName[..^"manifest.json".Length];
            var m = Parse(Bytes(mEntry, MaxManifestBytes));
            if (m?.Uid is null || m.ClaimHash is null) return new(null, BackupError.BadManifest);
            var kEntry = zip.GetEntry(prefix + "claim.key");
            if (kEntry is null) return new(null, BackupError.NoClaimKey);
            var key = Bytes(kEntry, 1024);
            if (key is null || !Convert.ToHexStringLower(SHA256.HashData(key)).Equals(m.ClaimHash, StringComparison.OrdinalIgnoreCase)) return new(null, BackupError.ClaimMismatch);

            var files = m.Files ?? [];
            return new(new(m.Uid, m.Handle, m.ToolVersion ?? "", m.StartedUtc ?? default, m.Groups ?? [], m.SteamId64, files.Count, files.Count(f => f.Error is not null),
                [.. files.GroupBy(f => f.Endpoint ?? "").OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => (g.Key, g.Count(), g.Count(f => f.Error is not null)))]), null);
        }
        catch (InvalidDataException) { return new(null, BackupError.NotZip); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(null, BackupError.Unreadable); }
    }

    internal static bool IsAt(ZipArchiveEntry e, string name) =>
        e.FullName == name || (e.FullName.EndsWith("/" + name, StringComparison.Ordinal) && e.FullName.Count(c => c == '/') == 1);

    static Manifest? Parse(byte[]? json)
    {
        try { return json is null ? null : JsonSerializer.Deserialize<Manifest>(json, J); }
        catch (JsonException) { return null; }
    }

    /// <summary>The entry's content, or null when it is larger than the cap (the declared size is not trusted).</summary>
    static byte[]? Bytes(ZipArchiveEntry e, int cap)
    {
        using var s = e.Open();
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        int n;
        while ((n = s.Read(buf)) > 0)
        {
            if (ms.Length + n > cap) return null;
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }
}
