// The operator's settings.json: the host names the server answers for, where its files are, how it listens and which
// proxies it trusts. Relative paths are relative to the settings file.
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

/// <summary>One address and port to listen on. Cert and Key (PEM files) make it HTTPS; without them it is plain HTTP.
/// Admin: the admin API, Swagger UI and the OpenAPI documents are served on this listener (loopback addresses only).</summary>
public sealed record Listener(IPAddress Address, int Port, string? Cert, string? Key, bool Admin = false);

public sealed class Settings
{
    public const string DefaultHostHeader = "X-Game-Host";
    public const long DefaultMaxBodyBytes = 16L << 20;

    /// <summary>The host keys of Replay.HostKeys and install_dir, as Replay.Game, ArchiveFile and Scoring read them.</summary>
    public required JsonObject Cfg { get; init; }
    public required string DataDir { get; init; }
    public required string RecordingsDir { get; init; }
    public required string MirrorDir { get; init; }
    public string? VariantsFile { get; init; }
    public required IReadOnlyList<Listener> Listeners { get; init; }
    /// <summary>The address and port of every listener that serves the admin API.</summary>
    public IReadOnlyList<(IPAddress Address, int Port)> AdminListeners => [.. Listeners.Where(l => l.Admin).Select(l => (l.Address, l.Port))];
    /// <summary>Peers whose HostHeader is believed: the proxies of this server.</summary>
    public IReadOnlyList<IPNetwork> TrustedProxies { get; init; } = [];
    public string HostHeader { get; init; } = DefaultHostHeader;
    public long MaxBodyBytes { get; init; } = DefaultMaxBodyBytes;

    /// <summary>Reads and checks the file; InvalidDataException says what is wrong.</summary>
    public static Settings Load(string path)
    {
        path = Path.GetFullPath(path);
        JsonObject root;
        try { root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException("not a JSON object"); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new InvalidDataException($"{path}: {e.Message}");
        }
        var baseDir = Path.GetDirectoryName(path)!;
        string Path_(string key, string? fallback = null)
        {
            var v = Text(root, key) ?? fallback ?? throw new InvalidDataException($"{key}: missing");
            return Path.GetFullPath(v, baseDir);
        }

        var cfg = new JsonObject();
        // web_host (our website: sign-in, account) and game_web_host (where the current build sends everything) are required; a name left out is a placeholder that no client can ask for
        foreach (var key in Replay.HostKeys)
            cfg[key] = Text(root, key) ?? (key is "web_host" or "game_web_host" ? throw new InvalidDataException($"{key}: missing") : key[..^"_host".Length] + ".invalid");
        cfg["install_dir"] = Path_("install_dir");
        // true: a request that no token resolves is the local account's (a player's own PC); false: a community server refuses it
        cfg["offline_mode"] = root["offline_mode"] switch
        {
            null => true,
            JsonValue flag when flag.TryGetValue<bool>(out var offline) => offline,
            _ => throw new InvalidDataException("offline_mode: true or false"),
        };
        cfg["review_webhook"] = Text(root, "review_webhook");  // an https URL; unset: no alert (Alerts.cs)

        if (root["listeners"] is not JsonArray { Count: > 0 } rows)
            throw new InvalidDataException("listeners: needs at least one {address, port}");
        var listeners = new List<Listener>();
        foreach (var row in rows)
        {
            var o = row as JsonObject ?? throw new InvalidDataException("listeners: each entry is an object");
            if (!IPAddress.TryParse(Text(o, "address"), out var address))
                throw new InvalidDataException("listeners: address is not an IP address");
            if (o["port"] is not JsonValue p || !p.TryGetValue<int>(out var port) || port is < 1 or > 65535)
                throw new InvalidDataException("listeners: port is not a number from 1 to 65535");
            string? cert = Text(o, "cert"), key = Text(o, "key");
            if ((cert is null) != (key is null))
                throw new InvalidDataException("listeners: cert and key go together");
            var admin = o["admin"] is null ? false
                : o["admin"] is JsonValue a && a.TryGetValue<bool>(out var flag) ? flag : throw new InvalidDataException("listeners: admin is true or false");
            if (admin && !IPAddress.IsLoopback(address))
                throw new InvalidDataException("listeners: admin needs a loopback address");
            listeners.Add(new(address, port, cert is null ? null : Path.GetFullPath(cert, baseDir), key is null ? null : Path.GetFullPath(key, baseDir), admin));
        }

        var proxies = new List<IPNetwork>();
        foreach (var n in root["trusted_proxies"] as JsonArray ?? [])
        {
            var text = (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
            if (text is null || !TryNetwork(text, out var net))
                throw new InvalidDataException("trusted_proxies: each entry is an IP address or an address/prefix");
            proxies.Add(net);
        }

        var hostHeader = Text(root, "host_header") ?? DefaultHostHeader;
        var maxBody = root["max_body_bytes"] is JsonValue m ? (m.TryGetValue<long>(out var bytes) && bytes > 0 ? bytes : throw new InvalidDataException("max_body_bytes: a positive number")) : DefaultMaxBodyBytes;
        return new Settings
        {
            Cfg = cfg,
            DataDir = Path_("data_dir", "data"),
            RecordingsDir = Path_("recordings_dir", "recordings"),
            MirrorDir = Path_("mirror_dir", "mirror"),
            VariantsFile = Text(root, "variants_file") is { } v ? Path.GetFullPath(v, baseDir) : null,
            Listeners = listeners,
            TrustedProxies = proxies,
            HostHeader = hostHeader,
            MaxBodyBytes = maxBody,
        };
    }

    static string? Text(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    /// <summary>"10.0.0.0/8", or a single address (a /32 or /128).</summary>
    public static bool TryNetwork(string text, out IPNetwork net)
    {
        if (IPNetwork.TryParse(text, out net)) return true;
        if (!IPAddress.TryParse(text, out var address)) return false;
        net = new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
        return true;
    }
}
