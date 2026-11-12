using System.Web;
using System.Buffers.Text;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Resurgence.Services;

namespace Resurgence.Launcher;

/// <summary>The launcher's session at the community server: signed in through the player's own browser by the native-app flow (RFC 8252 loopback redirect, RFC 7636 PKCE; docs/server.md, "The loopback sign-in"):
/// the launcher listens once on 127.0.0.1, opens the server's sign-in with the SHA-256 <c>challenge</c> of a random verifier, the port and a random <c>state</c>, and the browser comes back to 127.0.0.1 with a one-time code
/// that only this launcher (it alone knows the verifier) can trade (<c>POST /auth/app/exchange</c>) for the session cookie. The cookie is kept in <paramref name="file"/>
/// protected by DPAPI for this Windows user and read again at start; <see cref="ServerCookie"/> carries it to the other <c>Server*</c> services. Who is signed in, and signing out, are <see cref="ServerSessionService"/>'s.</summary>
/// <param name="http">Base address is the server, <c>UseCookies</c> off.</param>
public sealed class LauncherSessionService : ISessionService
{
    readonly ServerSessionService inner;
    readonly HttpClient http;
    readonly ServerCookie cookie;
    readonly string file;
    readonly Action<string> open;
    readonly TimeSpan wait;
    CancellationTokenSource? running;

    public event Action? Changed;

    /// <param name="open">Opens an address in the player's browser.</param>
    /// <param name="wait">How long the player has to sign in in the browser.</param>
    public LauncherSessionService(HttpClient http, ServerCookie cookie, string file, Action<string> open, ILogger<ServerSessionService> log, TimeSpan? wait = null)
    {
        (this.http, this.cookie, this.file, this.open, this.wait) = (http, cookie, file, open, wait ?? TimeSpan.FromMinutes(5));
        inner = new(http, cookie, log);
        inner.Changed += () => Changed?.Invoke();
        try { cookie.Value = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser)); }
        catch (Exception e) when (e is IOException or CryptographicException or UnauthorizedAccessException) { } // none, another Windows user's or damaged: signed out
    }

    public bool BrowserSignIn => true;

    public Task<Session?> CurrentAsync() => inner.CurrentAsync();

    public async Task SignOutAsync()
    {
        running?.Cancel();
        try { File.Delete(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        await inner.SignOutAsync();
    }

    public void CancelBrowserSignIn() => running?.Cancel();

    public async Task<Result> SignInWithBrowserAsync()
    {
        running?.Cancel();
        var left = (running = new CancellationTokenSource()).Token; // the player left: no message
        const string Failed = "Signin.LinkFailed", Expired = "Signin.LinkExpired";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(left);
        timeout.CancelAfter(wait);
        var ct = timeout.Token;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
            var state = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
            var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            open(new Uri(http.BaseAddress!, $"/auth/app/start?challenge={challenge}&port={((IPEndPoint)listener.LocalEndpoint).Port}&state={state}").AbsoluteUri);

            var code = await WaitForBrowser(listener, state, ct);
            using var exchange = new HttpRequestMessage(HttpMethod.Post, "/auth/app/exchange") { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = code, ["verifier"] = verifier }) };
            exchange.Headers.Add("X-Requested-With", "launcher"); // a state-changing request must show it is not a cross-site form
            using var answer = await http.SendAsync(exchange, ct);
            var prefix = ServerSessionApi.Cookie + "=";
            var set = answer.IsSuccessStatusCode && answer.Headers.TryGetValues("Set-Cookie", out var all) ? all.FirstOrDefault(c => c.StartsWith(prefix, StringComparison.Ordinal)) : null;
            if (set is null) return Result.Fail(Failed);
            var value = set.Split(';')[0][prefix.Length..];
            Save(value);
            cookie.Value = value; // only a kept session counts as signed in
            Changed?.Invoke();
            return Result.Success;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return left.IsCancellationRequested ? new Result(false) : Result.Fail(Expired); }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or SocketException or IOException or NotSupportedException or UnauthorizedAccessException or UriFormatException) { return Result.Fail(Failed); }
        finally { listener.Stop(); }
    }

    /// <summary>The browser's way back: serves 127.0.0.1 until a request carries our <paramref name="state"/> and a code (any other request, a stray one or a page that guesses the port, gets a refusal and changes nothing).</summary>
    static async Task<string> WaitForBrowser(TcpListener listener, string state, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            string? code = null;
            try
            {
                var stream = client.GetStream();
                var buf = new byte[4096];
                var n = 0;
                using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    limit.CancelAfter(TimeSpan.FromSeconds(5));
                    try
                    {
                        while (n < buf.Length && !Encoding.ASCII.GetString(buf, 0, n).Contains("\r\n\r\n", StringComparison.Ordinal))
                            if (await stream.ReadAsync(buf.AsMemory(n), limit.Token) is var read and > 0) n += read; else break;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { } // a silent connection (a browser's spare one)
                }
                var line = Encoding.ASCII.GetString(buf, 0, n).Split("\r\n")[0].Split(' ');
                if (line is ["GET", var target, _] && Uri.TryCreate("http://127.0.0.1" + target, UriKind.Absolute, out var url) && url.AbsolutePath == "/")
                {
                    var q = HttpUtility.ParseQueryString(url.Query);
                    if (q["state"] is { } given && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(given), Encoding.ASCII.GetBytes(state))) code = q["code"];
                }
                var (status, text) = code is { Length: > 0 } ? ("200 OK", "Signed in, you can close this tab.") : ("400 Bad Request", "This is not the sign-in the app started.");
                var page = Encoding.UTF8.GetBytes($"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Hunter Resurgence</title></head><body><p>{text}</p></body></html>");
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {page.Length}\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n"), ct);
                await stream.WriteAsync(page, ct);
            }
            catch (IOException) { } // a connection that dropped before the answer (a probe, an aborted request): the wait goes on, unless the request already gave the code
            if (code is { Length: > 0 }) return code;
        }
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
