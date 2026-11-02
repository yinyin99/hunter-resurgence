using System.Text.Json;

namespace Backup;

/// <summary>settings.json next to the exe: client_exe, oauth_consumer_key and the *_host keys. Placeholders are rejected.</summary>
sealed record Settings(string Apiv2Host, string WebHost, string ClientExe, HashSet<string> Hosts, string? ConsumerKey = null, string? ServerUrl = null, string? AvatarHost = null)
{
    // a single-file exe runs from its extraction folder, so AppContext.BaseDirectory is not the exe's folder; `dotnet run` has dotnet.exe as process
    static string FilePath => Path.Combine(Environment.ProcessPath is { } p && !Path.GetFileNameWithoutExtension(p).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
        ? Path.GetDirectoryName(p)! : AppContext.BaseDirectory, "settings.json");

    public static Settings Load() => Parse(File.ReadAllText(FilePath));

    /// <summary>The settings in <paramref name="json"/>; throws InvalidOperationException for a required key that is missing or still a placeholder.</summary>
    public static Settings Parse(string json)
    {
        var d = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        string Get(string k) => d.TryGetValue(k, out var v) && v.Length > 0 && !v.Contains('<')
            ? v : throw new InvalidOperationException($"set \"{k}\" (missing or still a placeholder)"); // for the log in the Details box (which adds "settings.json:" and stays English); the window says what to do in the chosen language
        return new(Get("apiv2_host"), Get("web_host"), Get("client_exe"),
            d.Where(kv => kv.Key.EndsWith("_host") && !kv.Value.Contains('<')).Select(kv => kv.Value).ToHashSet(),
            d.TryGetValue("oauth_consumer_key", out var ck) && ck.Length > 0 && !ck.Contains('<') ? ck : null, // public key of the website, optional
            d.TryGetValue("server_url", out var su) && Uri.TryCreate(su, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? su : null, // the community server for "Send", optional; a placeholder is no URL
            d.TryGetValue("avatar_host", out var ah) && ah.Length > 0 && !ah.Contains('<') ? ah : null); // where avatar pictures are served, optional
    }
}
