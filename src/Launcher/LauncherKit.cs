using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Resurgence.Services;

namespace Resurgence.Launcher;

/// <summary>
/// The player kit inside the launcher: setup and removal (what <c>play setup [--remove]</c> did), and the forwarder that answers the game on 127.0.0.1:80/443
/// for as long as the launcher is open (it lives in this process, so closing the launcher stops it). Needs an https <c>server_url</c>.
/// </summary>
public sealed class LauncherKit(Uri? server) : IKitService
{
    WebApplication? app;
    X509Certificate2? leaf;

    public bool Available => server?.Scheme == Uri.UriSchemeHttps;

    public string? Problem { get; private set; }

    public KitPlan Plan() => Plan(KitSettings.Load(), null);

    static KitPlan Plan(KitSettings s, string? notice) => new(s.Hosts, Pki.CaSubject[3..], Pki.Place.Default.Dir, Hosts.DefaultPath, notice);

    public async Task<KitPlan?> StartAsync()
    {
        if (!Available || app is not null) return null; // the forwarder already runs
        try
        {
            var s = KitSettings.Load();
            var now = DateTimeOffset.UtcNow;
            // no certificate, an expired one, one for other names or an authority that is not trusted (the player said No at the prompt): setup makes new ones
            var notice = Pki.Check(s.Hosts, now);
            if (notice is null)
            {
                leaf = Pki.LoadLeaf(now);
                notice = HostsKey(s) is { } key ? Kit.S(key) : null;
                await Run(s);
            }
            return notice is null ? null : Plan(s, notice);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Problem = e.Message;
            return null;
        }
    }

    static string? HostsKey(KitSettings s)
    {
        var text = Hosts.Read(Hosts.DefaultPath);
        if (!Hosts.Has(text)) return "Kit.NoHosts";
        return Hosts.Matches(text, s.Hosts) ? null : "Kit.OtherNames"; // a block of an older setup for other names
    }

    /// <summary>The Strings key of the reason setup is needed now (what <see cref="StartAsync"/> shows), null when setup is in place. A setup that cannot be read (settings.json wrong, the hosts file locked) is not a reason: <see cref="Problem"/> tells.</summary>
    internal static string? SetupKey(DateTimeOffset now)
    {
        try
        {
            var s = KitSettings.Load();
            return Pki.CheckKey(s.Hosts, now) ?? HostsKey(s);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
    }

    async Task Run(KitSettings s)
    {
        app = Forwarder.Build(s.ServerUrl, leaf!, 443, 80);
        try { await app.StartAsync(); }
        catch (IOException e)
        {
            await Stop();
            throw new InvalidOperationException(Kit.F("Kit.PortBusy", e.Message));
        }
        Problem = null;
    }

    async Task Stop()
    {
        if (app is not null) await app.DisposeAsync();
        leaf?.Dispose();
        (app, leaf) = (null, null);
    }

    // off the UI thread: Windows asks for the certificate and for administrator rights while this runs
    public Task<Result> SetUpAsync() => Task.Run(async () =>
    {
        try
        {
            var s = KitSettings.Load();
            await Stop();
            Pki.Install(s.Hosts, DateTimeOffset.UtcNow);
            Kit.SetHosts(true, s.Hosts);
            leaf = Pki.LoadLeaf(DateTimeOffset.UtcNow);
            await Run(s);
            return Result.Success;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Problem = e.Message;
            return Result.Fail(e.Message);
        }
    });

    public Task<Result> RemoveAsync() => Task.Run(async () =>
    {
        try
        {
            await Stop();
            Kit.SetHosts(false, null);
            Pki.Uninstall();
            Problem = null;
            return Result.Success;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Result.Fail(e.Message);
        }
    });
}
