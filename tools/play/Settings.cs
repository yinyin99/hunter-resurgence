using System.Text.Json;

namespace Play;

/// <summary>settings.json next to the exe: <c>server_url</c> (the community server) and the <c>*_host</c> names the game asks for. Placeholders are rejected.</summary>
sealed record Settings(Uri ServerUrl, string[] Hosts)
{
    // a single-file exe runs from its extraction folder, so AppContext.BaseDirectory is not the exe's folder; `dotnet run` has dotnet.exe as process
    static string FilePath => Path.Combine(Environment.ProcessPath is { } p && !Path.GetFileNameWithoutExtension(p).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
        ? Path.GetDirectoryName(p)! : AppContext.BaseDirectory, "settings.json");

    public static Settings Load() =>
        File.Exists(FilePath) ? Parse(File.ReadAllText(FilePath)) : throw new InvalidOperationException(Loc.F("SettingsMissing", FilePath));

    public static Settings Parse(string json)
    {
        Dictionary<string, string> d;
        try { d = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []; }
        catch (JsonException) { throw new InvalidOperationException(Loc.S("SettingsBroken")); }
        string Get(string k) => d.TryGetValue(k, out var v) && v.Length > 0 && !v.Contains('<') ? v : throw new InvalidOperationException(Loc.F("SettingsKey", k));

        var url = Get("server_url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var server) || server.Scheme != Uri.UriSchemeHttps || server.Query.Length > 0 || server.Fragment.Length > 0)
            throw new InvalidOperationException(Loc.F("SettingsUrl", "server_url"));

        // every name ends up in the hosts file and in a certificate: only plain DNS names pass. The bare name of a www. host is redirected too.
        var hosts = new List<string>();
        foreach (var k in d.Keys.Where(k => k.EndsWith("_host")).Order())
        {
            var h = Get(k).ToLowerInvariant();
            if (Uri.CheckHostName(h) != UriHostNameType.Dns || !h.Contains('.')) throw new InvalidOperationException(Loc.F("SettingsHost", k));
            hosts.Add(h);
            if (h.StartsWith("www.")) hosts.Add(h[4..]);
        }
        if (hosts.Count == 0) throw new InvalidOperationException(Loc.F("SettingsKey", "*_host"));
        return new(server, hosts.Distinct().ToArray());
    }
}
