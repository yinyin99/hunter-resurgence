// Steam's OpenID 2.0 sign-in: the redirect to Steam and the check of what comes back. Steam is asked to confirm the
// assertion (check_authentication), so no key and no shared secret are needed; the result is only the Steam id.
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Resurgence.Server;

public sealed class SteamOpenId(HttpClient http, Func<DateTimeOffset>? clock = null)
{
    public const string Endpoint = "https://steamcommunity.com/openid/login";
    const string Ns = "http://specs.openid.net/auth/2.0";
    const string IdentifierSelect = "http://specs.openid.net/auth/2.0/identifier_select";
    // an assertion older than this (or from this far in the future) is refused, and one nonce is accepted once
    static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    // what the signature must cover, or the rest of the query could be swapped by whoever relays it
    static readonly string[] MustBeSigned = ["op_endpoint", "claimed_id", "identity", "return_to", "response_nonce", "assoc_handle"];
    static readonly Regex ClaimedId = new(@"\Ahttps://steamcommunity\.com/openid/id/(\d{17})\z", RegexOptions.ECMAScript);
    static readonly Regex Nonce = new(@"\A(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ)[\x21-\x7e]{0,100}\z", RegexOptions.ECMAScript);

    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    readonly ConcurrentDictionary<string, DateTimeOffset> seen = new();

    /// <summary>Where to send the browser: Steam asks for the password, then returns to returnTo.</summary>
    public static string LoginUrl(string returnTo, string realm) => Endpoint + "?" + string.Join('&', new (string, string)[]
    {
        ("openid.ns", Ns), ("openid.mode", "checkid_setup"), ("openid.return_to", returnTo), ("openid.realm", realm),
        ("openid.identity", IdentifierSelect), ("openid.claimed_id", IdentifierSelect),
    }.Select(p => p.Item1 + "=" + Uri.EscapeDataString(p.Item2)));

    /// <summary>The 17-digit Steam id of a good assertion, else null. query is the query string of the return URL,
    /// expectedReturnTo the URL this server sent Steam to come back to.</summary>
    public async Task<string?> VerifyAsync(string query, string expectedReturnTo)
    {
        var all = Py.ParseQs(query);
        var p = new Dictionary<string, string>();
        foreach (var (k, v) in all)
            if (k.StartsWith("openid.", StringComparison.Ordinal))
            {
                if (v.Count != 1) return null;  // a repeated field could hide one from the check
                p[k] = v[0];
            }
        string? Get(string key) => p.GetValueOrDefault("openid." + key);
        if (Get("ns") != Ns || Get("mode") != "id_res" || Get("op_endpoint") != Endpoint || Get("return_to") != expectedReturnTo)
            return null;
        if (Get("claimed_id") is not { } claimed || ClaimedId.Match(claimed) is not { Success: true } id || Get("identity") != claimed)
            return null;
        var signed = (Get("signed") ?? "").Split(',');
        if (MustBeSigned.Any(f => !signed.Contains(f)) || string.IsNullOrEmpty(Get("sig")))
            return null;
        var nonce = Get("response_nonce") ?? "";
        if (!Nonce.IsMatch(nonce)
            || !DateTimeOffset.TryParseExact(nonce[..20], "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var issued)
            || (now() - issued).Duration() > Window || seen.ContainsKey(nonce))
            return null;

        p["openid.mode"] = "check_authentication";
        try
        {
            using var reply = await http.PostAsync(Endpoint, new FormUrlEncodedContent(p));
            var body = await reply.Content.ReadAsStringAsync();
            if (!reply.IsSuccessStatusCode || !body.Split('\n').Any(line => line.Trim() == "is_valid:true"))
                return null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
        foreach (var (n, t) in seen)  // forget what no assertion can use any more
            if (now() - t > Window * 2) seen.TryRemove(n, out _);
        return seen.TryAdd(nonce, issued) ? id.Groups[1].Value : null;  // a second use that raced this one loses here
    }
}
