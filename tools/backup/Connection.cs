using System.Net;
using System.Net.Sockets;

namespace Backup;

/// <summary>"Check connection": one anonymous GET per server, no token. Kinds: ok, down, redirected (official only: a host name of settings.json resolves to this PC or a private network, e.g. the player kit's hosts block), unset (community only).</summary>
static class Connection
{
    public sealed record Result(string Official, string Community);

    /// <summary>resolve is for tests; the default asks the system resolver (which reads the hosts file). Any HTTP answer below 500 counts as reachable: the server answered.</summary>
    public static async Task<Result> Run(Settings s, HttpClient http, Func<string, Task<IPAddress[]>>? resolve = null, CancellationToken ct = default)
    {
        resolve ??= h => Dns.GetHostAddressesAsync(h, ct);
        var official = Official(s, http, resolve, ct);
        var community = s.ServerUrl == null ? Task.FromResult("unset") : Reach(http, s.ServerUrl, ct);
        return new(await official, await community);
    }

    static async Task<string> Official(Settings s, HttpClient http, Func<string, Task<IPAddress[]>> resolve, CancellationToken ct)
    {
        var names = s.Hosts.Append(s.Apiv2Host).Append(s.WebHost).Distinct().ToList();
        foreach (var n in names)
        {
            try { if ((await resolve(n)).Any(Local)) return "redirected"; }
            catch (Exception e) when (e is SocketException or ArgumentException) { } // no such name: the request below says "down"
        }
        var ok = await Task.WhenAll(new[] { s.Apiv2Host, s.WebHost }.Distinct().Select(h => Reach(http, $"https://{h}/", ct)));
        return ok.All(r => r == "ok") ? "ok" : "down";
    }

    static async Task<string> Reach(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            return (int)res.StatusCode < 500 ? "ok" : "down";
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or TaskCanceledException or InvalidOperationException) { return "down"; }
    }

    /// <summary>Loopback, link-local or private (10/8, 172.16/12, 192.168/16, fc00::/7): where no official server lives.</summary>
    static bool Local(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any) || a.IsIPv6LinkLocal) return true;
        var b = a.GetAddressBytes();
        return a.AddressFamily == AddressFamily.InterNetwork
            ? b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254
            : (b[0] & 0xFE) == 0xFC;
    }
}
