using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using Resurgence.Services;

namespace Resurgence.Launcher;

/// <summary>The player's problem report: built by <see cref="Diagnostics"/>, saved to Downloads or sent to the community server (<c>POST /account/report</c>, docs/server.md), and whether the player agreed to automatic sends.</summary>
/// <param name="http">Base address is the server, <c>UseCookies</c> off, a timeout for the upload; null without <c>server_url</c> (a send then fails).</param>
/// <param name="downloads">The folder Save copies to; default the player's Downloads folder.</param>
public sealed class ProblemReporter(Diagnostics diagnostics, Prefs prefs, Action savePrefs, HttpClient? http = null, ServerCookie? cookie = null, string? downloads = null)
{
    public const string Route = "/account/report";

    /// <summary>"unset", "auto" or "ask"; anything else in launcher.json reads as unset, so nothing is ever sent without a yes.</summary>
    public string Consent
    {
        get => prefs.ReportConsent is "auto" or "ask" ? prefs.ReportConsent : "unset";
        set
        {
            if (value is not ("unset" or "auto" or "ask")) return;
            prefs.ReportConsent = value;
            savePrefs();
        }
    }

    public Task<ProblemReport> BuildAsync(string trigger) => Task.Run(() => diagnostics.Build(trigger));

    /// <summary>The Error is a Strings key: Report.ErrSignIn (401 or not signed in), ErrFormat (400), ErrSize (413), ErrLimit (429), ErrDisk (507), ErrNetwork (no answer or anything else), ErrSave (the zip file is not there).</summary>
    public async Task<ReportSent> SendAsync(ProblemReport report)
    {
        if (http is null) return new(null, "Report.ErrNetwork");
        if (cookie?.Value is null) return new(null, "Report.ErrSignIn");
        if (!File.Exists(report.Path)) return new(null, "Report.ErrSave"); // never written (see Diagnostics.Build), or replaced by a newer report
        try
        {
            using var request = cookie.Request(HttpMethod.Post, Route);
            request.Headers.Add("X-Requested-With", "launcher");
            request.Headers.Add("X-Report-Trigger", report.Trigger);
            await using var zip = File.OpenRead(report.Path);
            request.Content = new StreamContent(zip);
            request.Content.Headers.ContentType = new("application/zip");
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return json.RootElement.TryGetProperty("reference", out var r) && r.GetString() is { Length: > 0 } reference ? new(reference, null) : new(null, "Report.ErrNetwork");
            }
            return new(null, response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Report.ErrSignIn",
                HttpStatusCode.BadRequest => "Report.ErrFormat",
                HttpStatusCode.RequestEntityTooLarge => "Report.ErrSize",
                HttpStatusCode.TooManyRequests => "Report.ErrLimit",
                HttpStatusCode.InsufficientStorage => "Report.ErrDisk",
                _ => "Report.ErrNetwork",
            });
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return new(null, "Report.ErrNetwork");
        }
    }

    /// <summary>Copies the zip to the Downloads folder (the one Windows says, which the player may have moved); an IOException (FileNotFoundException when the zip was never written or a newer report replaced it) or UnauthorizedAccessException is the caller's to show.</summary>
    public string Save(ProblemReport report)
    {
        if (!File.Exists(report.Path)) throw new FileNotFoundException("the report zip is not there", report.Path);
        var folder = downloads ?? (SHGetKnownFolderPath(DownloadsId, 0, 0, out var path) == 0 ? path : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        Directory.CreateDirectory(folder);
        var to = Path.Combine(folder, Path.GetFileName(report.Path));
        File.Copy(report.Path, to, true);
        return to;
    }

    static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath(in Guid id, uint flags, nint token, [MarshalAs(UnmanagedType.LPWStr)] out string path);
}
