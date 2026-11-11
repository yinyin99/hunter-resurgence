using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Resurgence.Services;

namespace Resurgence.Launcher;

/// <summary>The launcher's session at the community server: signed in by the server's code link (docs/server.md, the launcher's start page): <c>POST /auth/link</c> gives a code that the player compares
/// in the browser, which is opened on the server's sign-in; the launcher polls <c>GET /auth/link/&lt;code&gt;</c> and keeps the session cookie it sets. The cookie is kept in <paramref name="file"/>
/// protected by DPAPI for this Windows user and read again at start; <see cref="ServerCookie"/> carries it to the other <c>Server*</c> services. Who is signed in, and signing out, are <see cref="ServerSessionService"/>'s.</summary>
/// <param name="http">Base address is the server, <c>UseCookies</c> off.</param>
public sealed class LauncherSessionService : ISessionService
{
    sealed record Link(string Code, string UserCode, string Url, int Expires = 300);

    readonly ServerSessionService inner;
    readonly HttpClient http;
    readonly ServerCookie cookie;
    readonly string file;
    readonly Action<string> open;
    readonly TimeSpan interval;
    CancellationTokenSource? running;

    public event Action? Changed;

    /// <param name="open">Opens an address in the player's browser.</param>
    public LauncherSessionService(HttpClient http, ServerCookie cookie, string file, Action<string> open, ILogger<ServerSessionService> log, TimeSpan? interval = null)
    {
        (this.http, this.cookie, this.file, this.open, this.interval) = (http, cookie, file, open, interval ?? TimeSpan.FromSeconds(2));
        inner = new(http, cookie, log);
        inner.Changed += () => Changed?.Invoke();
        try { cookie.Value = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser)); }
        catch (Exception e) when (e is IOException or CryptographicException or UnauthorizedAccessException) { } // none, another Windows user's or damaged: signed out
    }

    public bool CodeLink => true;

    public Task<Session?> CurrentAsync() => inner.CurrentAsync();

    public async Task SignOutAsync()
    {
        running?.Cancel();
        try { File.Delete(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        await inner.SignOutAsync();
    }

    public void CancelLink() => running?.Cancel();

    public async Task<Result> SignInWithLinkAsync(Action<string> showCode)
    {
        running?.Cancel();
        var ct = (running = new CancellationTokenSource()).Token; // no timer: nothing to dispose
        const string Failed = "Signin.LinkFailed", Expired = "Signin.LinkExpired";
        try
        {
            using var start = new HttpRequestMessage(HttpMethod.Post, "/auth/link");
            start.Headers.Add("X-Requested-With", "launcher"); // a state-changing request must show it is not a cross-site form
            using var started = await http.SendAsync(start, ct);
            var link = await started.Content.ReadFromJsonAsync<Link>(new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);
            // the address to open must be the server's own
            if (link is not { Code.Length: > 0 } || !Uri.TryCreate(link.Url, UriKind.Absolute, out var url) || url.Scheme != http.BaseAddress!.Scheme || url.Authority != http.BaseAddress.Authority) return Result.Fail(Failed);
            showCode(link.UserCode);
            open(link.Url);

            for (var until = DateTime.UtcNow.AddSeconds(link.Expires); DateTime.UtcNow < until;)
            {
                await Task.Delay(interval, ct);
                using var poll = await http.GetAsync("/auth/link/" + Uri.EscapeDataString(link.Code), ct);
                if ((int)poll.StatusCode == 404) break; // the code ran out on the server
                Dictionary<string, JsonElement>? body;
                try { body = await poll.Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>(ct); }
                catch (JsonException) { continue; } // a proxy's page: ask again
                if (body is null || !body.TryGetValue("status", out var status) || status.ValueKind != JsonValueKind.String || status.GetString() != "ok") continue;
                var prefix = ServerSessionApi.Cookie + "=";
                var set = poll.Headers.TryGetValues("Set-Cookie", out var all) ? all.FirstOrDefault(c => c.StartsWith(prefix, StringComparison.Ordinal)) : null;
                if (set is null) return Result.Fail(Failed);
                var value = set.Split(';')[0][prefix.Length..];
                Save(value);
                cookie.Value = value; // only a kept session counts as signed in
                Changed?.Invoke();
                return Result.Success;
            }
            return Result.Fail(Expired);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Result.Fail(Expired); } // the player left
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or NotSupportedException or IOException or UnauthorizedAccessException) { return Result.Fail(Failed); }
    }

    void Save(string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }

    public Task<Result> RequestCodeAsync(string email) => inner.RequestCodeAsync(email);
    public Task<Result> SignInAsync(string email, string code, bool register = false) => inner.SignInAsync(email, code, register);
    public Task<Result> SetHandleAsync(string handle) => inner.SetHandleAsync(handle);
    public Task<StartOutcome> StartAsync() => inner.StartAsync();
    public Task<bool> LicenseAcceptedAsync() => inner.LicenseAcceptedAsync();
    public Task AcceptLicenseAsync() => inner.AcceptLicenseAsync();
}
