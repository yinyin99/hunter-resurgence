using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Resurgence.Services;

/// <summary>The browser's session cookie, the cookie of a sign-in in progress and host of the request that started the circuit; the website fills it from the root component's parameters (the documented way to carry request state into a circuit), never from HttpContext later.</summary>
public sealed class ServerCookie
{
    string? session;
    /// <summary>The session; a missing or empty cookie is no session.</summary>
    public string? Value { get => session; set => session = string.IsNullOrEmpty(value) ? null : value; }
    /// <summary>The sign-in in progress (<see cref="ServerSessionApi.LoginCookie"/>), while there is one.</summary>
    public string? Login { get; set; }
    public string? Host { get; set; }

    /// <summary>The server refused the session <paramref name="sent"/> (401: expired or revoked): it is dropped from this holder (the website's circuit, the launcher's run; the launcher's
    /// <c>session.dat</c> keeps it until the next sign-in or sign-out), so the services stop asking. A newer session, signed in while the request was in flight, stays.</summary>
    public void Refused(HttpResponseMessage response, string? sent)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && sent is not null) Interlocked.CompareExchange(ref session, null, sent);
    }

    /// <summary>A request to the server as this browser: its session cookie and the cookie of its sign-in when it has them, and the host it used.</summary>
    public HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        var cookies = new List<string>(2);
        if (Value is not null) cookies.Add($"{ServerSessionApi.Cookie}={Value}");
        if (Login is not null) cookies.Add($"{ServerSessionApi.LoginCookie}={Login}");
        if (cookies.Count > 0) request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies));
        if (Host is not null) request.Headers.Host = Host;
        return request;
    }
}

/// <summary>The session of the community server behind the same domain: the player is whoever the browser's cookie names at <see cref="ServerSessionApi.Me"/>.
/// Sign-in is a page load of <see cref="SignInUrl"/>, so the e-mail, handle and license methods have nothing to do here: they fail with a key every language has.</summary>
/// <param name="http">Base address is the server, <c>UseCookies</c> off (the cookie is the player's, sent by hand); every failure of the network counts as signed out.</param>
public sealed class ServerSessionService(HttpClient http, ServerCookie cookie, ILogger<ServerSessionService> log) : ISessionService
{
    public event Action? Changed;

    public string? DiscordLinkUrl => "/discord/link";

    public string? SignInUrl(string next) => $"{ServerSessionApi.SteamLogin}?next={Uri.EscapeDataString(next)}";

    public async Task<Session?> CurrentAsync()
    {
        if (cookie.Value is null) return null;
        try
        {
            using var request = cookie.Request(HttpMethod.Get, ServerSessionApi.Me);
            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;
            var me = await response.Content.ReadFromJsonAsync<ServerAccount>();
            return me is { Handle.Length: > 0 } ? new Session(me.Handle, me.Hunterscore, Avatar: Avatars.Slot(me.Avatar), Imperial: me.Imperial, DiscordLinked: me.DiscordLinked) : null;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason}): signed out", ServerSessionApi.Me, e.Message);
            return null;
        }
    }

    public async Task SignOutAsync()
    {
        if (cookie.Value is not null)
        {
            try
            {
                using var request = cookie.Request(HttpMethod.Post, ServerSessionApi.SignOut);
                request.Headers.Add("X-Requested-With", "XMLHttpRequest");
                (await http.SendAsync(request)).Dispose();
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
            {
                log.LogWarning("The server did not answer {Path} ({Reason}): signed out here only", ServerSessionApi.SignOut, e.Message);
            }
        }
        cookie.Value = null;
        Changed?.Invoke();
    }

    public Task<Result> RequestCodeAsync(string email) => Task.FromResult(Result.Fail("Validation.Email.Invalid"));
    public Task<Result> SignInAsync(string email, string code, bool register = false) => Task.FromResult(Result.Fail("Validation.Code.Invalid"));
    public Task<Result> SetHandleAsync(string handle) => Task.FromResult(Result.Fail("Signup.P1.Taken"));
    public async Task<StartOutcome> StartAsync() => await CurrentAsync() is null ? StartOutcome.LoggedOut : StartOutcome.LoggedIn;
    /// <summary>The agreements are not asked on the website for now.</summary>
    public Task<bool> LicenseAcceptedAsync() => Task.FromResult(true);
    public Task AcceptLicenseAsync() => Task.CompletedTask;
}
