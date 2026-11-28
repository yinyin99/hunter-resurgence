using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Resurgence.Launcher;

/// <summary>
/// Answers the game on this PC (HTTPS with the local certificate, plain HTTP) and relays every request, unchanged, to the community server over normal HTTPS.
/// The name the game asked for travels in <see cref="HostHeader"/>; the server routes by it (only from its proxy, see the server's docs).
/// </summary>
static class Forwarder
{
    public const string HostHeader = "X-Game-Host";

    static readonly HashSet<string> Hop = new(StringComparer.OrdinalIgnoreCase)
        { "Connection", "Keep-Alive", "Proxy-Connection", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Host", "Alt-Svc" };

    /// <summary>Not started. <paramref name="tls"/> null: no HTTPS listener. A port of 0 lets Windows choose (the tests). <paramref name="log"/> (launcher.log): what the game's connect was answered (<see cref="FirstBytes"/>) and every request that could not be relayed.</summary>
    public static WebApplication Build(Uri server, X509Certificate2? tls, int httpsPort, int httpPort, ILogger? log = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = null;
            k.RequestHeaderEncodingSelector = _ => Encoding.Latin1;
            k.ResponseHeaderEncodingSelector = _ => Encoding.Latin1;
            // HTTP/1.1 only: ALPN would move the game's embedded browser to HTTP/2.
            // Only this PC's game connects, so Kestrel's 10 s handshake limit (made against slow strangers) only drops a handshake on a busy PC.
            if (tls is not null) k.Listen(IPAddress.Loopback, httpsPort, l => { l.Protocols = HttpProtocols.Http1; l.UseHttps(tls, o => o.HandshakeTimeout = TimeSpan.FromMinutes(1)); });
            k.Listen(IPAddress.Loopback, httpPort, l => l.Protocols = HttpProtocols.Http1);
        });
        var app = builder.Build();

        var http = new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = (c, ct) => ConnectIPv4First(c.DnsEndPoint.Host, c.DnsEndPoint.Port, ct),
            AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None, UseProxy = false,
            RequestHeaderEncodingSelector = (_, _) => Encoding.Latin1, ResponseHeaderEncodingSelector = (_, _) => Encoding.Latin1,
        })
        { Timeout = TimeSpan.FromMinutes(5) };
        app.Lifetime.ApplicationStopped.Register(http.Dispose);
        var baseUrl = server.AbsoluteUri.TrimEnd('/');

        app.Run(async ctx =>
        {
            var req = ctx.Request;
            var raw = ctx.Features.Get<IHttpRequestFeature>()!.RawTarget; // the server keys on the raw target; Uri.ToString() would double the leading slash
            using var msg = new HttpRequestMessage(new HttpMethod(req.Method), new Uri(baseUrl + raw, new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true }));
            if (req.ContentLength is > 0 || req.Headers.ContainsKey("Transfer-Encoding")) msg.Content = new StreamContent(req.Body);
            foreach (var h in req.Headers)
                if (!Hop.Contains(h.Key) && !msg.Headers.TryAddWithoutValidation(h.Key, (IEnumerable<string>)h.Value!))
                    (msg.Content ??= new ByteArrayContent([])).Headers.TryAddWithoutValidation(h.Key, (IEnumerable<string>)h.Value!); // a content header (Content-Type, Content-Length...)
            msg.Headers.TryAddWithoutValidation(HostHeader, req.Headers.Host.ToString());
            try
            {
                using var resp = await http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
                ctx.Response.StatusCode = (int)resp.StatusCode;
                // The game's V1 reader needs a Content-Length (the official server sends one); a proxy in front of the server (Cloudflare) turns the reply into a chunked one.
                // So buffer the body and send its length. HEAD has no body: its headers, Content-Length included, go through.
                var head = HttpMethods.IsHead(req.Method);
                var body = head ? Array.Empty<byte>() : await resp.Content.ReadAsByteArrayAsync(ctx.RequestAborted);
                foreach (var h in resp.Headers.Concat(resp.Content.Headers))
                    if (!Hop.Contains(h.Key) && (head || !h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))) ctx.Response.Headers.Append(h.Key, h.Value.ToArray());
                if (!head && ctx.Response.StatusCode is not (204 or 304)) ctx.Response.ContentLength = body.Length;
                // before the write: a problem report must hold what the game was about to get (a connect it cannot read ends the start with 16002, #1052); only the length and the first bytes, never the body
                if (IsConnect(req.Path)) log?.LogInformation("connect reply to the game: status {Status}, {Length} bytes, starts {Start}", ctx.Response.StatusCode, body.Length, FirstBytes(body));
                if (body.Length > 0) await ctx.Response.Body.WriteAsync(body, ctx.RequestAborted); // a 204 or 304 has none, and writing even an empty body to one throws
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
            {
                if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 502;
                if (!ctx.RequestAborted.IsCancellationRequested) log?.LogWarning("{Method} {Host}{Path} not relayed ({Status}): {Error}", req.Method, req.Headers.Host.ToString(), req.Path.Value, ctx.Response.StatusCode, e.Message); // the game going away is not an error
            }
        });
        return app;
    }

    /// <summary>The game's connect call: no other call of the game ends in <c>/connect</c> (<c>/disconnect</c> does not match, the slash is part of the suffix).</summary>
    static bool IsConnect(PathString path) => path.Value?.EndsWith("/connect", StringComparison.Ordinal) == true;

    /// <summary>The first 32 bytes of a body as one line of text: printable ASCII as it is, a backslash, CR, LF and tab as <c>\\ \r \n \t</c>, any other byte (a control character, a byte of a multi-byte character, a gzip header) as <c>\xNN</c>. Nothing of the rest.</summary>
    internal static string FirstBytes(byte[] body)
    {
        var text = new StringBuilder();
        foreach (var b in body.AsSpan(0, Math.Min(body.Length, 32)))
            text.Append(b switch { (byte)'\\' => @"\\", (byte)'\r' => @"\r", (byte)'\n' => @"\n", (byte)'\t' => @"\t", >= 0x20 and < 0x7F => ((char)b).ToString(), _ => $@"\x{b:x2}" });
        return text.ToString();
    }

    /// <summary>A TCP connection to the server, over IPv4 first: the server records the address a connect came from and gives it to the players who join that host, and the game's networking cannot use an IPv6 one.
    /// The addresses of the name are tried one after the other, the IPv4 ones before the IPv6 ones, so a PC with no IPv4 route (IPv6 only, NAT64) still reaches the server; the last failure is the error.</summary>
    internal static async ValueTask<Stream> ConnectIPv4First(string host, int port, CancellationToken ct) => await ConnectIPv4First(await Dns.GetHostAddressesAsync(host, ct), port, ct);

    internal static async ValueTask<Stream> ConnectIPv4First(IPAddress[] addresses, int port, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var address in addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
        {
            ct.ThrowIfCancellationRequested();
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(address, port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception e)
            {
                socket.Dispose();
                last = e;
            }
        }
        ct.ThrowIfCancellationRequested();
        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }

    /// <summary>The URLs a started forwarder listens on (a port 0 resolved).</summary>
    public static string[] Addresses(WebApplication app) => [.. app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses];
}
