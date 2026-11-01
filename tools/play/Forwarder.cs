using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Play;

/// <summary>
/// Answers the game on this PC (HTTPS with the local certificate, plain HTTP) and relays every request, unchanged, to the community server over normal HTTPS.
/// The name the game asked for travels in <see cref="HostHeader"/>; the server routes by it (only from its proxy, see the server's docs).
/// </summary>
static class Forwarder
{
    public const string HostHeader = "X-Game-Host";

    static readonly HashSet<string> Hop = new(StringComparer.OrdinalIgnoreCase)
        { "Connection", "Keep-Alive", "Proxy-Connection", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Host" };

    /// <summary>Not started. <paramref name="tls"/> null: no HTTPS listener. A port of 0 lets Windows choose (the tests).</summary>
    public static WebApplication Build(Uri server, X509Certificate2? tls, int httpsPort, int httpPort)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = null;
            k.RequestHeaderEncodingSelector = _ => Encoding.Latin1;
            k.ResponseHeaderEncodingSelector = _ => Encoding.Latin1;
            // HTTP/1.1 only: ALPN would move the game's embedded browser to HTTP/2
            if (tls is not null) k.Listen(IPAddress.Loopback, httpsPort, l => { l.Protocols = HttpProtocols.Http1; l.UseHttps(tls); });
            k.Listen(IPAddress.Loopback, httpPort, l => l.Protocols = HttpProtocols.Http1);
        });
        var app = builder.Build();

        var http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None, UseProxy = false,
            RequestHeaderEncodingSelector = (_, _) => Encoding.Latin1, ResponseHeaderEncodingSelector = (_, _) => Encoding.Latin1,
        }) { Timeout = TimeSpan.FromMinutes(5) };
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
                foreach (var h in resp.Headers.Concat(resp.Content.Headers))
                    if (!Hop.Contains(h.Key)) ctx.Response.Headers.Append(h.Key, h.Value.ToArray());
                if (!HttpMethods.IsHead(req.Method)) await resp.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
            {
                if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 502;
                if (!ctx.RequestAborted.IsCancellationRequested) Console.WriteLine($"{req.Method} {req.Headers.Host}{raw}: {e.Message}"); // the game going away is not an error
            }
        });
        return app;
    }

    /// <summary>The URLs a started forwarder listens on (a port 0 resolved).</summary>
    public static string[] Addresses(WebApplication app) => [.. app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses];
}
