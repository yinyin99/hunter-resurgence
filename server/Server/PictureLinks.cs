// Gallery pictures a backup lists by link but does not hold. The links come from a player's zip, so every fetch is held to the same rules: https on the default port,
// a host of the operator's allow-list (`picture_link_hosts`, exact match; a redirect is followed only to such a host), only public addresses (checked when the
// connection is made, so a name that resolves elsewhere later is refused too), a size cap, a timeout, a JPEG start. A picture that fails any rule is skipped.
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed class PictureLinks
{
    /// <summary>The largest picture taken.</summary>
    public const int MaxBytes = 16 << 20;
    /// <summary>The most links fetched for one save (a zip may list any number).</summary>
    public const int MaxPictures = 1000;
    /// <summary>The most bytes fetched for one save: the cap of a backup zip's content.</summary>
    public const long MaxTotalBytes = 1L << 30;
    const int AtOnce = 4;
    const int MaxHops = 3;
    // the whole fetch is part of the player's upload request, which a proxy in front ends after about 100 seconds: links not fetched by then are skipped and counted in the report
    static readonly TimeSpan PerPicture = TimeSpan.FromSeconds(15), Total = TimeSpan.FromSeconds(80);

    readonly HashSet<string> hosts;
    readonly HttpClient http;

    /// <summary>`handler` replaces the network (a test's fake host); by default the connections go only to public addresses.</summary>
    public PictureLinks(IEnumerable<string> hosts, HttpMessageHandler? handler = null)
    {
        this.hosts = new(hosts, StringComparer.OrdinalIgnoreCase);
        http = new(handler ?? Network()) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>The allow-list of the settings (`picture_link_hosts`, empty by default: nothing is fetched).</summary>
    public static PictureLinks FromCfg(JsonObject cfg) => new((cfg["picture_link_hosts"] as JsonArray ?? []).Select(h => (string)h!));

    /// <summary>The name of a link's picture in the pictures zip: the hash of the link as the save wrote it.</summary>
    public static string EntryName(string url) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + ".jpg";

    /// <summary>Fetch what can be fetched of `urls` and write it to the zip `path` (not created when nothing came); returns how many pictures it holds.</summary>
    public async Task<int> Fetch(IEnumerable<string> urls, string path)
    {
        if (hosts.Count == 0) return 0;
        var ok = 0;
        long total = 0;
        var part = path + ".part";
        using var cancel = new CancellationTokenSource(Total);
        try
        {
            await using (var file = File.Create(part))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
                await System.Threading.Tasks.Parallel.ForEachAsync(urls.Take(MaxPictures), new ParallelOptions { MaxDegreeOfParallelism = AtOnce }, async (url, _) =>
                {
                    if (await Get(url, cancel.Token) is not { } bytes) return;
                    lock (zip)  // a zip is written by one thread at a time
                    {
                        if (total + bytes.Length > MaxTotalBytes) return;
                        total += bytes.Length;
                        using var to = zip.CreateEntry(EntryName(url), CompressionLevel.NoCompression).Open();
                        to.Write(bytes.Span);
                        ok++;
                    }
                });
            if (ok > 0) File.Move(part, path, overwrite: true);
        }
        finally
        {
            File.Delete(part);  // nothing came, or the fetch failed half way
        }
        return ok;
    }

    async Task<ReadOnlyMemory<byte>?> Get(string url, CancellationToken total)
    {
        try
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(total);
            cancel.CancelAfter(PerPicture);
            var uri = Uri.TryCreate(url.StartsWith("//", StringComparison.Ordinal) ? "https:" + url : url, UriKind.Absolute, out var first) ? first : null;
            for (var hop = 0; hop <= MaxHops && uri is not null && Allowed(uri); hop++)
            {
                using var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), HttpCompletionOption.ResponseHeadersRead, cancel.Token);
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    uri = response.Headers.Location is { } to ? new Uri(uri, to) : null;
                    continue;
                }
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes) return null;
                await using var body = await response.Content.ReadAsStreamAsync(cancel.Token);
                var bytes = new MemoryStream();
                var buffer = new byte[81920];
                int n;
                while ((n = await body.ReadAsync(buffer, cancel.Token)) > 0)
                {
                    if (bytes.Length + n > MaxBytes) return null;
                    bytes.Write(buffer, 0, n);
                }
                var picture = bytes.GetBuffer().AsMemory(0, (int)bytes.Length);
                if (!IsJpeg(picture.Span)) return null;
                return picture;
            }
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or UriFormatException)
        {
            return null;
        }
    }

    bool Allowed(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 && hosts.Contains(uri.Host);

    static bool IsJpeg(ReadOnlySpan<byte> b) => b.Length > 3 && b[0] == 255 && b[1] == 216 && b[2] == 255;

    /// <summary>An address a picture may be fetched from: a global unicast one. Not loopback, private, link-local, shared, multicast or reserved; an IPv4-mapped IPv6 address is judged as its IPv4 one,
    /// and the IPv6 ranges that embed an IPv4 address (6to4, Teredo) or are documentation are refused.</summary>
    public static bool IsPublic(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        var b = a.GetAddressBytes();
        if (a.AddressFamily == AddressFamily.InterNetworkV6)
            return (b[0] & 224) == 32 && !(b[0] == 32 && ((b[1] == 2) || (b[1] == 1 && (b[2] == 0 && b[3] == 0 || b[2] == 13 && b[3] == 184))));  // 2000::/3 without 2002::/16, 2001::/32 and 2001:db8::/32
        return a.AddressFamily == AddressFamily.InterNetwork
            && b[0] is not (0 or 10 or 127) and < 224                                                                // this network, private, loopback; multicast and reserved start at 224
            && !(b[0] == 100 && b[1] is >= 64 and < 128)                                                              // shared address space
            && !(b[0] == 169 && b[1] == 254)                                                                          // link-local
            && !(b[0] == 172 && b[1] is >= 16 and < 32)
            && !(b[0] == 192 && (b[1] == 168 || b[1] == 0 && b[2] is 0 or 2 || b[1] == 88 && b[2] == 99))             // private, protocol and documentation, 6to4 relay
            && !(b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100))
            && !(b[0] == 203 && b[1] == 0 && b[2] == 113);
    }

    static SocketsHttpHandler Network() => new()
    {
        AllowAutoRedirect = false, UseProxy = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = async (context, token) =>
        {
            var addresses = (await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token)).Where(IsPublic).ToArray();
            if (addresses.Length == 0) throw new HttpRequestException("no public address");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}
