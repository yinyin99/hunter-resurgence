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
        cfg["nat_host"] = Text(root, "nat_host");  // optional: the punch-through server's name (Install.PtServer)
        cfg["install_dir"] = Path_("install_dir");
        // true: a request that no token resolves is the local account's (a player's own PC); false: a community server refuses it
        cfg["offline_mode"] = root["offline_mode"] switch
        {
            null => true,
            JsonValue flag when flag.TryGetValue<bool>(out var offline) => offline,
            _ => throw new InvalidDataException("offline_mode: true or false"),
        };
        // true: every license of the game is unlimited for every player (docs/server.md#licenses)
        cfg["all_licenses"] = root["all_licenses"] switch
        {
            null => true,
            JsonValue flag when flag.TryGetValue<bool>(out var all) => all,
            _ => throw new InvalidDataException("all_licenses: true or false"),
        };
        // true: every player has unlimited ammunition (docs/server.md#unlimited-ammunition)
        cfg["unlimited_ammo"] = root["unlimited_ammo"] switch
        {
            null => true,
            JsonValue flag when flag.TryGetValue<bool>(out var unlimited) => unlimited,
            _ => throw new InvalidDataException("unlimited_ammo: true or false"),
        };
        // true: every player has unlimited vehicle fuel and none is sold (docs/server.md#unlimited-fuel)
        cfg["unlimited_fuel"] = root["unlimited_fuel"] switch
        {
            null => true,
            JsonValue flag when flag.TryGetValue<bool>(out var unlimitedFuel) => unlimitedFuel,
            _ => throw new InvalidDataException("unlimited_fuel: true or false"),
        };
        // true: a hunt's reply places the animals the player's missions name (docs/server.md#named-animals); off until the game is seen to accept them
        cfg["spawn_named_animals"] = root["spawn_named_animals"] switch
        {
            null => false,
            JsonValue flag when flag.TryGetValue<bool>(out var placed) => placed,
            _ => throw new InvalidDataException("spawn_named_animals: true or false"),
        };
        // a time-limited gm$ multiplier for harvests (docs/server.md#harvest-gm-bonus): {multiplier above 0, until an ISO UTC time}, stored as Unix seconds
        cfg["harvest_gm_bonus"] = root["harvest_gm_bonus"] switch
        {
            null => null,
            JsonObject b when b["multiplier"] is JsonValue factor && factor.TryGetValue<double>(out var times) && times > 0 && double.IsFinite(times)
                              && b["until"] is JsonValue u && u.TryGetValue<string>(out var text)
                              && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var until)
                => new JsonObject { ["multiplier"] = times, ["until"] = until.ToUnixTimeSeconds() },
            _ => throw new InvalidDataException("harvest_gm_bonus: {\"multiplier\": a number above 0, \"until\": an ISO time such as \"2026-11-23T05:00:00Z\"}"),
        };
        // a seasonal event (docs/server.md#seasonal-events): checked here, read again from cfg by Replay.LoadState
        cfg["event"] = SeasonalEvent.Parse(root["event"]) is null ? null : root["event"]!.DeepClone();
        cfg["event_community_divisor"] = root["event_community_divisor"] switch
        {
            null => 20,
            JsonValue n when n.TryGetValue<int>(out var divisor) && divisor >= 1 => divisor,
            _ => throw new InvalidDataException("event_community_divisor: a whole number, at least 1"),
        };
        cfg["default_inventory_capacity"] = Whole(root, "default_inventory_capacity", State.DefaultInventoryCapacity, 1);  // grams every player carries; docs/server.md#backpack-capacity
        cfg["game_build"] = root["game_build"] is null ? null : Whole(root, "game_build", 0, 1);  // the game's Steam build id the server supports, served by GET /stats/game; unset: null
        // the day the server opened (Unix seconds): hunts that started before it are no server playtime (docs/server.md#server-playtime-and-hunting-statistics); an ISO time, no zone means UTC
        cfg["playtime_since"] = root["playtime_since"] switch
        {
            null => State.DefaultPlaytimeSince,
            JsonValue iso when iso.TryGetValue<string>(out var text) && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var since) => since.ToUnixTimeSeconds(),
            _ => throw new InvalidDataException("playtime_since: an ISO time such as \"2026-11-12T00:00:00Z\""),
        };
        cfg["link_discord"] = (Environment.GetEnvironmentVariable("LINK_DISCORD")?.Trim() is { Length: > 0 } env ? env : Text(root, "link_discord")) is { } discord && discord.StartsWith("https://", StringComparison.Ordinal) ? discord : null;  // an https invite shown on the launcher's start page (AuthPages.cs), LINK_DISCORD (set by the deploy) winning over the file; anything else: none
        cfg["review_webhook"] = Text(root, "review_webhook");  // an https URL; unset: no alert (Alerts.cs)
        cfg["signup_webhook"] = Text(root, "signup_webhook");  // an https URL; unset: no alert (Alerts.cs)
        cfg["news_webhook"] = Text(root, "news_webhook");  // an https URL of the announcements channel's webhook, for `admin news add --discord` (NewsCommand.cs); unset: --discord is refused
        // Discord Linked Roles (LinkedRoles.cs): the Discord application's id and client secret, both or neither; the secret is the operator's, never committed. Unset: /discord/link and /discord/callback are off
        string? discordId = Text(root, "discord_client_id"), discordSecret = Text(root, "discord_client_secret");
        if ((discordId is null) != (discordSecret is null))
            throw new InvalidDataException("discord_client_id and discord_client_secret go together");
        if (discordId is not null && !LinkedRoles.IsDiscordId(discordId))
            throw new InvalidDataException("discord_client_id: the application id, digits only");
        cfg["discord_client_id"] = discordId;
        cfg["discord_client_secret"] = discordSecret;
        // the role bot (LinkedRoles.RoleBot): a second Discord application's bot with Manage Roles only that gives the linked players the Verified hunter and Veteran roles; the token is the operator's, never committed.
        // Off unless the token, the guild and both roles are set (with Linked Roles on)
        cfg["discord_role_bot_token"] = Text(root, "discord_role_bot_token");
        foreach (var key in new[] { "discord_guild_id", "discord_role_verified", "discord_role_veteran" })
            cfg[key] = Text(root, key) is not { } id ? null : LinkedRoles.IsDiscordId(id) ? id : throw new InvalidDataException($"{key}: a Discord id, digits only");
        cfg["discord_veteran_at"] = Whole(root, "discord_veteran_at", LinkedRoles.RoleBot.DefaultVeteranAt, 0);
        // what the server stores for the operator (Uploads.cs): zips and bytes an account may have waiting or refused, bytes on the whole server, and the data disk's free bytes below which uploads stop
        cfg["upload_account_files"] = Whole(root, "upload_account_files", Uploads.AccountFilesDefault, 1);
        cfg["upload_account_bytes"] = Whole(root, "upload_account_bytes", Uploads.AccountBytesDefault, 1);
        cfg["upload_total_bytes"] = Whole(root, "upload_total_bytes", Uploads.TotalBytesDefault, 1);
        cfg["upload_min_free_bytes"] = Whole(root, "upload_min_free_bytes", Uploads.MinFreeBytesDefault, 0);
        cfg["picture_link_hosts"] = PictureHosts(root["picture_link_hosts"]);  // the hosts a backup's gallery links are fetched from (PictureLinks.cs); none: nothing is fetched
        cfg["avatar_uploads"] = AvatarUploads(root);  // where a player's uploaded avatar is fetched from by user id (State.AvatarFetchUrl); unset: the avatar list's own base

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
        var dataDir = Path_("data_dir", "data");
        cfg["archive_db"] = Path_("archive_db", Path.Combine(dataDir, "archive.db"));  // the operator's archive of public profiles (Archive.cs); a missing file turns claims off
        cfg["avatar_uploads_dir"] = Path_("avatar_uploads_dir", "avatar-uploads");  // the saved copies of the players' uploaded avatars, `242/<uid>.jpg`: a restore reads one before it asks `avatar_uploads` (PictureLinks.LocalAvatar)
        cfg["recordings_dir"] = Path_("recordings_dir", "recordings");  // the operator's recordings, and beside the catalogue's its `mission_rules.json` (Replay.LoadState)
        cfg["images_dir"] = Path_("images_dir", "images");  // the operator's pictures: the game's avatars are `avatar-<id>.png` there (Replay.Game.AvatarPicture), the website's slots too (docs/images.md)
        return new Settings
        {
            Cfg = cfg,
            DataDir = dataDir,
            RecordingsDir = (string)cfg["recordings_dir"]!,
            MirrorDir = Path_("mirror_dir", "mirror"),
            VariantsFile = Text(root, "variants_file") is { } v ? Path.GetFullPath(v, baseDir) : null,
            Listeners = listeners,
            TrustedProxies = proxies,
            HostHeader = hostHeader,
            MaxBodyBytes = maxBody,
        };
    }

    /// <summary>`avatar_uploads`: an https base URL (no query or fragment; a trailing `/` is dropped); absent: null.</summary>
    static string? AvatarUploads(JsonObject o) =>
        Text(o, "avatar_uploads") is not { } s ? null
        : Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.UserInfo.Length == 0 && u.Query.Length == 0 && u.Fragment.Length == 0 ? s.TrimEnd('/')
        : throw new InvalidDataException("avatar_uploads: an https URL (no credentials, query or fragment)");

    static long Whole(JsonObject o, string key, long fallback, long min) => o[key] switch
    {
        null => fallback,
        JsonValue n when n.TryGetValue<long>(out var v) && v >= min => v,
        _ => throw new InvalidDataException($"{key}: a whole number, at least {min}"),
    };

    static string? Text(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    /// <summary>`picture_link_hosts`: a list of host names (lower-cased; no scheme, port, path or address); absent: none.</summary>
    static JsonArray PictureHosts(JsonNode? node)
    {
        const string Bad = "picture_link_hosts: a list of host names (no scheme, port, path or IP address)";
        var hosts = new JsonArray();
        foreach (var h in node as JsonArray ?? (node is null ? new JsonArray() : throw new InvalidDataException(Bad)))
            hosts.Add(h is JsonValue v && v.TryGetValue<string>(out var s) && Uri.CheckHostName(s) == UriHostNameType.Dns ? s.ToLowerInvariant() : throw new InvalidDataException(Bad));
        return hosts;
    }

    /// <summary>"10.0.0.0/8", or a single address (a /32 or /128).</summary>
    public static bool TryNetwork(string text, out IPNetwork net)
    {
        if (IPNetwork.TryParse(text, out net)) return true;
        if (!IPAddress.TryParse(text, out var address)) return false;
        net = new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
        return true;
    }
}
