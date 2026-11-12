using System.Text.Json;

namespace Resurgence.Launcher;

/// <summary>What setup and the forwarder read from the launcher's settings.json: <c>server_url</c> (the community server) and the <c>*_host</c> names the game asks for. Placeholders are rejected.</summary>
sealed record KitSettings(Uri ServerUrl, string[] Hosts)
{
    public static KitSettings Load() => Parse(File.ReadAllText(Settings.FilePath));

    public static KitSettings Parse(string json)
    {
        Dictionary<string, string> d;
        try { d = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []; }
        catch (JsonException) { throw new InvalidOperationException(Kit.S("Kit.SettingsBroken")); }
        string Get(string k) => d.TryGetValue(k, out var v) && v.Length > 0 && !v.Contains('<') ? v : throw new InvalidOperationException(Kit.F("Kit.SettingsKey", k));

        var url = Get("server_url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var server) || server.Scheme != Uri.UriSchemeHttps || server.Query.Length > 0 || server.Fragment.Length > 0)
            throw new InvalidOperationException(Kit.F("Kit.SettingsUrl", "server_url"));

        // every name ends up in the hosts file and in a certificate: only plain DNS names pass. The bare name of a www. host is redirected too.
        var hosts = new List<string>();
        foreach (var k in d.Keys.Where(k => k.EndsWith("_host")).Order())
        {
            var h = Get(k).ToLowerInvariant();
            if (Uri.CheckHostName(h) != UriHostNameType.Dns || !h.Contains('.')) throw new InvalidOperationException(Kit.F("Kit.SettingsHost", k));
            hosts.Add(h);
            if (h.StartsWith("www.")) hosts.Add(h[4..]);
        }
        if (hosts.Count == 0) throw new InvalidOperationException(Kit.F("Kit.SettingsKey", "*_host"));
        return new(server, hosts.Distinct().ToArray());
    }
}
