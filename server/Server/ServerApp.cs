// The server's web host: the game hosts (GameApi, replay fallback) and, on localhost only, the admin API, the
// OpenAPI documents (/openapi/game.json, /openapi/admin.json) and Swagger UI (/swagger).
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.OpenApi;

namespace Resurgence.Server;

/// <summary>Console: request lines and TLS failures on the console. WorkDir: the miss log and the admin status there.
/// TrustedProxies: peers whose HostHeader names the game host the client asked for (a proxy between client and server).
/// MaxBodyBytes: the request body limit of every endpoint but the admin API's. AdminListeners: the only listeners that
/// serve the admin API (null: every listener). SteamHttp and DiscordHttp: how the sign-in asks Steam to confirm an assertion and how Linked Roles talk to
/// Discord (default: a plain client; tests pass a fake one). LicenceHttp: how the licence check asks Steam's Web API (<see cref="SteamLicence"/>).</summary>
public sealed record ServerOptions(Replay.Game Game, int HarCount, string? WorkDir, bool Console,
    IReadOnlyList<IPNetwork>? TrustedProxies = null, string HostHeader = Settings.DefaultHostHeader, long MaxBodyBytes = Settings.DefaultMaxBodyBytes,
    IReadOnlyList<(IPAddress Address, int Port)>? AdminListeners = null,
    HttpClient? SteamHttp = null, HttpClient? DiscordHttp = null, HttpClient? LicenceHttp = null);

public static class ServerApp
{
    public static readonly string[] LocalHosts = ["localhost", "127.0.0.1"];

    static bool IsLocal(HttpContext ctx) => LocalHosts.Contains(ctx.Request.Host.Host, StringComparer.OrdinalIgnoreCase);

    const long AdminMaxBodyBytes = 64 * 1024;
    static readonly Regex NewsPicturePath = new(@"\A/admin/news/\d+/picture\z", RegexOptions.ECMAScript);
    /// <summary>How often the server closes the hunts nobody has sent a heartbeat for, and forgets the places of the addresses it looked up that are too old (<see cref="Places.Expire"/>).</summary>
    static readonly TimeSpan StaleSweep = TimeSpan.FromSeconds(Places.SweepSeconds);
    static readonly string[] ForwardingHeaders = ["X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "Forwarded", "Via", "X-Real-IP"];
    static readonly Regex HostName = new(@"\A[A-Za-z0-9][A-Za-z0-9.\-]*(:\d{1,5})?\z", RegexOptions.ECMAScript);

    static bool InAny(IPAddress? peer, IReadOnlyList<IPNetwork>? nets)
    {
        if (peer is null || nets is null) return false;
        if (peer.IsIPv4MappedToIPv6) peer = peer.MapToIPv4();
        return nets.Any(n => n.Contains(peer));
    }

    /// <summary>The address a request came from, as text: the peer's, or, for a trusted proxy, the last address of its `X-Forwarded-For` (the one that proxy appended; null when it sent none, never the proxy's own). Null when it cannot be told.</summary>
    static string? ClientIp(HttpContext ctx, IReadOnlyList<IPNetwork>? proxies)
    {
        var peer = ctx.Connection.RemoteIpAddress;
        if (InAny(peer, proxies))
            peer = IPAddress.TryParse(ctx.Request.Headers["X-Forwarded-For"].ToString().Split(',')[^1].Trim(), out var client) ? client : null;
        return (peer is { IsIPv4MappedToIPv6: true } ? peer.MapToIPv4() : peer)?.ToString();
    }

    /// <summary>The admin API, Swagger UI and the OpenAPI documents are for a person at this machine. A request qualifies
    /// when it came in on an admin listener, its peer is a loopback address (the peer address, not a header) and nothing
    /// says a proxy relayed it: a proxy on this machine is a loopback peer too, and the listener it connects to is not an
    /// admin listener.</summary>
    public static bool IsDirectLocal(IPAddress? peer, IPAddress? localAddress, int localPort, IHeaderDictionary headers, string hostHeader,
        IReadOnlyList<(IPAddress Address, int Port)>? adminListeners)
    {
        static IPAddress? V4(IPAddress? a) => a is { IsIPv4MappedToIPv6: true } ? a.MapToIPv4() : a;
        return peer is not null && IPAddress.IsLoopback(peer)
            && (adminListeners is null || adminListeners.Any(l => l.Port == localPort && Equals(V4(l.Address), V4(localAddress))))
            && !ForwardingHeaders.Append(hostHeader).Any(headers.ContainsKey);
    }

    /// <summary>Swagger UI's origin (http://localhost or 127.0.0.1, port 80), not other local web content.</summary>
    public static bool IsLocalOrigin(string? origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var u) && u.Scheme == "http" && u.IsDefaultPort && LocalHosts.Contains(u.Host);

    public static WebApplication Build(ServerOptions o, Action<KestrelServerOptions> listen)
    {
        var builder = WebApplication.CreateBuilder();  // no args: the CLI words never reach configuration
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        if (o.Console)
        {
            builder.Logging.AddSimpleConsole(c => c.SingleLine = true);
            builder.Logging.AddProvider(new TlsFailureLog());
            builder.Logging.AddFilter<TlsFailureLog>(TlsFailureLog.Category, LogLevel.Debug);
        }
        // a crash host with player state: its reports go to the account's rows, so they are not appended to telemetry.jsonl; the file of an older server is trimmed once
        var crashHost = o.Game.State is null ? null : o.Game.CrashHost;
        var telemetryFile = o.WorkDir is null ? null : Path.Combine(o.WorkDir, "telemetry.jsonl");
        if (telemetryFile is not null && crashHost is not null)
            CrashReports.TrimTelemetry(telemetryFile, crashHost, o.Game.State!.Store, Store.Now());
        builder.Services.AddSingleton(new RequestLog(o.Console, o.WorkDir is null ? null : Path.Combine(o.WorkDir, "misses.jsonl"), telemetryFile, crashHost));
        builder.Services.AddSingleton(new SteamOpenId(o.SteamHttp ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) }));
        if (o.Game.State is { Online: true } && SteamLicence.FromCfg(o.Game.Cfg, o.LicenceHttp ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = 1 << 20 }) is { } licence)
            builder.Services.AddSingleton(licence);  // docs/server.md#steam-licence
        // Linked Roles (docs/server.md#discord-linked-roles): on only with the Discord application's id and secret in the settings; the role bot only with its token, the guild and both roles too
        var roleBot = LinkedRoles.RoleBot.From(o.Game.Cfg);
        if (o.Game.State is { Online: true } linking && (string?)o.Game.Cfg["discord_client_id"] is { } discordId && (string?)o.Game.Cfg["discord_client_secret"] is { } discordSecret)
            builder.Services.AddSingleton(new LinkedRoles(linking, o.DiscordHttp ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 1 << 20 }, discordId, discordSecret, o.Game.Web, roleBot));
        else roleBot = null;
        if (roleBot is null && new[] { "discord_role_bot_token", "discord_guild_id", "discord_role_verified", "discord_role_veteran" }.Any(k => o.Game.Cfg[k] is not null))  // a half-done setup says so once
            Console.Error.WriteLine("discord roles: off until discord_role_bot_token, discord_guild_id, discord_role_verified and discord_role_veteran are all set, with discord_client_id and discord_client_secret on an online server");
        if (o.Game.State is { } news && o.WorkDir is not null) builder.Services.AddSingleton(new News(news.Store, o.WorkDir));  // the posts of the feed (docs/server.md#news)
        // the account's own pages (/account/) keep and delete files in the work folder; offline or without one they are off
        if (o.Game.State is { Online: true } state && o.WorkDir is not null)
        {
            var up = o.Game.Cfg;
            state.ReadAlert = account => Alerts.ReadLimit(up, account, state.Store.HandleOf(account) ?? "");
            var uploads = new Uploads(state, o.WorkDir)
            {
                Alert = text => Alerts.Review(up, text), Pictures = PictureLinks.FromCfg(up),
                AccountFiles = (int?)(long?)up["upload_account_files"] ?? Uploads.AccountFilesDefault,
                AccountBytes = (long?)up["upload_account_bytes"] ?? Uploads.AccountBytesDefault,
                TotalBytes = (long?)up["upload_total_bytes"] ?? Uploads.TotalBytesDefault,
                MinFreeBytes = (long?)up["upload_min_free_bytes"] ?? Uploads.MinFreeBytesDefault,
            };
            builder.Services.AddSingleton(uploads);
            builder.Services.AddSingleton(new ProblemReports(state, o.WorkDir)
            {
                Alert = (account, handle, reference, trigger, bytes) => Alerts.ProblemReport(up, account, handle, reference, trigger, bytes),
                MinFreeBytes = uploads.MinFreeBytes, FreeBytes = uploads.FreeBytes,
            });
            // the operator's archive of public profiles to claim (Settings sets archive_db; tests leave it out and put archive.db in the work folder)
            builder.Services.AddSingleton(new Claims(state, new Archive((string?)o.Game.Cfg["archive_db"] ?? Path.Combine(o.WorkDir, "archive.db")), o.WorkDir) { Alert = text => Alerts.Review(o.Game.Cfg, text), Signup = text => Alerts.Signup(o.Game.Cfg, text), Pictures = PictureLinks.FromCfg(o.Game.Cfg) });
        }
        builder.Services.AddProblemDetails();
        builder.Services.AddOpenApi("game", d =>
        {
            d.AddDocumentTransformer((doc, _, _) =>
            {
                doc.Info = new OpenApiInfo
                {
                    Title = "Game hosts", Version = "v1",
                    Description = "What the server answers from the player state. Every other request "
                                  + "on these hosts replays the recordings, then the install archives and the web mirror; the rest is "
                                  + "logged to the miss log. Bodies are parsed as the client sends them (form or JSON) "
                                  + "whatever the Content-Type. Each operation's server is its host from the settings.",
                };
                // routing drops a template's trailing slash; the client asks for this page with it
                if (doc.Paths.Remove("/user/{handle}/score", out var scorePage))
                    doc.Paths["/user/{handle}/score/"] = scorePage;
                doc.Servers = [];  // each operation names its own host
                return Task.CompletedTask;
            });
            d.AddOperationTransformer(DescribeGameOperation);
        });
        builder.Services.AddOpenApi("admin", d => d.AddDocumentTransformer((doc, _, _) =>
        {
            doc.Info = new OpenApiInfo
            {
                Title = "Server admin", Version = "v1",
                Description = "Player state and diagnostics, on localhost only. Grants reach a running hunt with its next heartbeat, "
                              + "else the next connect. Errors are RFC 7807 problem details.",
            };
            return Task.CompletedTask;
        }));
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = o.MaxBodyBytes;
            k.ResponseHeaderEncodingSelector = _ => Encoding.Latin1;  // replayed header values are Latin-1, like Python's send_header
            listen(k);
        });

        var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            var direct = IsDirectLocal(ctx.Connection.RemoteIpAddress, ctx.Connection.LocalIpAddress, ctx.Connection.LocalPort, ctx.Request.Headers,
                o.HostHeader, o.AdminListeners);
            ctx.Items[Req.IpKey] = ClientIp(ctx, o.TrustedProxies);
            // behind a proxy the game host arrives in a header; only the proxies of this server are believed
            if (InAny(ctx.Connection.RemoteIpAddress, o.TrustedProxies) && ctx.Request.Headers[o.HostHeader] is { Count: 1 } game
                && HostName.IsMatch(game[0]!))
                ctx.Request.Host = new HostString(game[0]!);
            await NewBuild.Rewrite(ctx, o.Game);  // the current build's one-host requests, before routing
            if (IsLocal(ctx))
            {
                if (!direct)
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                if (ctx.Request.Path.StartsWithSegments("/admin"))
                {
                    // a picture of a news post is the one body of the admin API that is more than a form
                    var limit = HttpMethods.IsPut(ctx.Request.Method) && NewsPicturePath.IsMatch(ctx.Request.Path.Value!) ? News.MaxPictureBytes : AdminMaxBodyBytes;
                    if (ctx.Request.ContentLength > limit)
                    {
                        ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                        return;
                    }
                    ctx.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = limit;  // a chunked body has no length to check
                }
            }
            await next();
        });
        app.UseRouting();  // after the middleware above: routing matches the host it may have rewritten
        // failures, binding failures and unknown admin paths as problem details too; scoped, the replay's empty replies
        // and the game hosts' failures stay as they were
        app.UseWhen(ctx => IsLocal(ctx) && ctx.Request.Path.StartsWithSegments("/admin"), admin => admin.UseExceptionHandler().UseStatusCodePages());
        app.UseWhen(IsLocal, local => local.UseSwaggerUI(ui =>
        {
            ui.SwaggerEndpoint("/openapi/game.json", "Game hosts");
            ui.SwaggerEndpoint("/openapi/admin.json", "Server admin");
            ui.RoutePrefix = "swagger";
            ui.DocumentTitle = "Server API";
        }));
        app.MapOpenApi().RequireHost(LocalHosts);
        app.MapGet("/", () => TypedResults.Redirect("/swagger")).RequireHost(LocalHosts).ExcludeFromDescription();
        app.MapAdmin(o.Game, o.HarCount, o.WorkDir, LocalHosts);
        // localhost is not a game host: without this, the replay's fallback would answer a cross-site CORS preflight for
        // the admin API with the requesting origin echoed, and the browser would then send the POST
        app.MapFallback("/{**path}", () => TypedResults.NotFound()).RequireHost(LocalHosts).ExcludeFromDescription();
        app.MapGame(o.Game);
        if (o.Game.State is { } hunts)
            app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
            {
                using var tick = new PeriodicTimer(StaleSweep);
                try
                {
                    while (await tick.WaitForNextTickAsync(app.Lifetime.ApplicationStopping))
                        try { hunts.CloseStaleHunts(); }
                        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException) { Console.Error.WriteLine("stale hunts: " + e.Message); }  // retried at the next tick
                        finally { try { hunts.Places?.Expire(); } catch (Exception) { } }  // an idle server forgets the places too; a failure here never stops the sweep
                }
                catch (OperationCanceledException) { }
            }));
        if (app.Services.GetService<LinkedRoles>() is { } linked)
            app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
            {
                using var tick = new PeriodicTimer(LinkedRoles.SweepEvery);
                try
                {
                    while (await tick.WaitForNextTickAsync(app.Lifetime.ApplicationStopping))
                    {
                        try { await linked.SweepAsync(); }
                        catch (Exception e) when (e is not OperationCanceledException) { Console.Error.WriteLine("discord sweep: " + e.Message); }  // a failed push is logged and tried again at the next tick
                    }
                }
                catch (OperationCanceledException) { }
            }));
        // crash reports and problem reports are kept 30 days, a Steam id's deletion record 24 h: purged before the server serves and once a day
        if (o.Game.State is { } kept)
        {
            void Purge()
            {
                try
                {
                    CrashReports.Purge(kept.Store, Store.Now());
                    app.Services.GetService<ProblemReports>()?.Purge(Store.Now());
                    kept.Store.PurgeDeletions(Store.Now());
                }
                catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException) { Console.Error.WriteLine("purge of reports and deletion records: " + e.Message); }  // retried the next day
            }
            Purge();
            app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
            {
                using var daily = new PeriodicTimer(TimeSpan.FromDays(1));
                try
                {
                    while (await daily.WaitForNextTickAsync(app.Lifetime.ApplicationStopping))
                        Purge();
                }
                catch (OperationCanceledException) { }
            }));
        }
        return app;
    }

    /// <summary>The game API's documentation-only metadata: request body, query and path parameters, the host as server.</summary>
    static async Task DescribeGameOperation(OpenApiOperation op, OpenApiOperationTransformerContext ctx, CancellationToken ct)
    {
        var metadata = ctx.Description.ActionDescriptor.EndpointMetadata;
        if (metadata.OfType<GameApi.BodyDoc>().LastOrDefault() is { } body)
            op.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    [body.ContentType] = new() { Schema = await ctx.GetOrCreateSchemaAsync(body.Type, null, ct) },
                },
            };
        foreach (var p in metadata.OfType<GameApi.ParamDoc>())
            (op.Parameters ??= []).Add(new OpenApiParameter
            {
                Name = p.Name, In = p.InPath ? ParameterLocation.Path : ParameterLocation.Query, Required = p.InPath,
                Description = p.Description, Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            });
        if (metadata.OfType<IHostMetadata>().LastOrDefault() is { Hosts.Count: > 0 } host)
            op.Servers = [.. host.Hosts.Select(h => new OpenApiServer { Url = $"https://{h}" })];
    }

    /// <summary>Only failed TLS handshakes from Kestrel's HTTPS log (event 1 failed, 2 timed out), one "err" line each;
    /// its successful-handshake lines are dropped.</summary>
    sealed class TlsFailureLog : ILoggerProvider, ILogger
    {
        public const string Category = "Microsoft.AspNetCore.Server.Kestrel.Https.Internal.HttpsConnectionMiddleware";

        public ILogger CreateLogger(string categoryName) => categoryName == Category ? this : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id is 1 or 2)
                Console.WriteLine($"err    tls {formatter(state, exception)} {exception?.Message}".TrimEnd());
        }

        public void Dispose() { }
    }
}

/// <summary>The server on an ephemeral loopback port, reached under any host name: coverage replays the recordings
/// through the real HTTP pipeline, tests call it like the client.</summary>
public sealed class Loopback : IAsyncDisposable
{
    readonly WebApplication app;
    Reply? last;

    public HttpClient Http { get; }

    public IServiceProvider Services => app.Services;

    Loopback(WebApplication app, HttpClient http) => (this.app, Http) = (app, http);

    public static async Task<Loopback> StartAsync(Replay.Game game, string? workDir = null, IReadOnlyList<IPNetwork>? trustedProxies = null, long maxBodyBytes = Settings.DefaultMaxBodyBytes,
        IReadOnlyList<(IPAddress Address, int Port)>? adminListeners = null, HttpClient? steamHttp = null, HttpClient? discordHttp = null, HttpClient? licenceHttp = null)
    {
        var app = ServerApp.Build(new ServerOptions(game, 0, workDir, Console: false, trustedProxies, MaxBodyBytes: maxBodyBytes, AdminListeners: adminListeners, SteamHttp: steamHttp, DiscordHttp: discordHttp, LicenceHttp: licenceHttp), k => k.Listen(IPAddress.Loopback, 0));
        await app.StartAsync();
        var port = new Uri(app.Urls.First()).Port;
        var http = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false, UseCookies = false, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None,
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(IPAddress.Loopback, port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        });
        var loopback = new Loopback(app, http);
        app.Services.GetRequiredService<RequestLog>().Replied += (_, reply) => loopback.last = reply;
        return loopback;
    }

    /// <summary>A game request as the client sends it (raw path and query kept); the reply the handlers built (HEAD
    /// replies keep their body here).</summary>
    public async Task<Reply> SendAsync(string method, string host, string pathAndQuery, IEnumerable<(string Name, string Value)>? headers = null, byte[]? body = null)
    {
        last = null;
        var url = new Uri($"http://{host}{pathAndQuery}", new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
        using var request = new HttpRequestMessage(new HttpMethod(method), url) { Content = new ByteArrayContent(body ?? []) };
        foreach (var (name, value) in headers ?? [])
            if (!request.Headers.TryAddWithoutValidation(name, value))
                request.Content.Headers.TryAddWithoutValidation(name, value);
        using var response = await Http.SendAsync(request);
        await response.Content.ReadAsByteArrayAsync();
        return last ?? throw new InvalidOperationException($"{method} {host}{pathAndQuery}: no game reply (HTTP {(int)response.StatusCode})");
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
