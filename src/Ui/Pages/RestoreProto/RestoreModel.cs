using System.Globalization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Localization;
using Resurgence.Services;

namespace Resurgence.Ui.Pages.RestoreProto;

/// <summary>PROTOTYPE (restore page variants). One state holder for the page: the data the page loaded, the player's inputs and the actions, so the
/// variants only differ in markup. <see cref="Fake"/> (dev switch <c>?state=</c> / <c>?claim=</c>) fills it with stub data and turns every action into a
/// local simulation: no service call is made, so nothing is read from or written to a server.</summary>
public sealed class RestoreModel(IRestoreService service, IStringLocalizer<Strings> L, Action changed)
{
    // ---- what the page loaded (unchanged from the current page) ----
    public RestoreStatus? Status { get; private set; }
    public ArchiveStatus? Archive { get; private set; }
    public RestoreResult? Result { get; private set; }
    public ArchiveFind? Found { get; private set; }
    public RestoreResult? ClaimResult { get; private set; }

    // ---- the player's inputs ----
    public IBrowserFile? File { get; private set; }
    public string FileName { get; private set; } = "";
    public long FileSize { get; private set; }
    public string OldName { get; set; } = "";
    public bool UseOldName { get; set; }
    public bool ConfirmDelete { get; set; }
    /// <summary>Any action runs (inputs are disabled).</summary>
    public bool Busy { get; private set; }
    /// <summary>The zip is being sent.</summary>
    public bool Uploading { get; private set; }
    public bool DeleteFailed { get; private set; }
    /// <summary>0..1 while uploading, null = unknown (the real service reports no progress yet; the real fix would pass the parts sent).</summary>
    public double? Progress { get; private set; }
    public bool Fake { get; private set; }
    /// <summary>Stub only: the zip waiting was read as another tool's format (the real contract carries no format yet; the real fix would add one to the upload result).</summary>
    public bool OtherTool { get; private set; }

    public bool HasFile => File is not null || (Fake && FileName.Length > 0);

    // ---- loading ----
    public async Task LoadAsync()
    {
        Fake = false;
        Status = await service.StatusAsync();
        Archive = await service.ArchiveStatusAsync();
    }

    public static string At(long ts) => DateTimeOffset.FromUnixTimeSeconds(ts).ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    public static readonly string[] States = ["empty", "uploading", "waiting", "restored", "rejected", "refused", "quota", "paused", "history", "waiting-other", "refused-program", "refused-pictures"];
    public static readonly string[] Claims = ["open", "found", "waiting", "restored", "rejected", "off"];

    /// <summary>Stub data for one of <see cref="States"/> (the upload side) and one of <see cref="Claims"/> (the archive claim).</summary>
    public void LoadFake(string? state, string? claim)
    {
        Fake = true;
        (OtherTool, Result, Found, ClaimResult, File, Busy, Uploading, Progress, FileName, FileSize, DeleteFailed) = (false, null, null, null, null, false, false, null, "", 0, false);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        const int Day = 86400;
        var rejected = new RestoreDecision(now - 9 * Day, "rejected", 7, null, false, "This is not your backup: it was made for another Steam account. Make a new one in My account mode while signed in to Steam with yours.");
        var refused = new RestoreDecision(now - 120, "refused", null, null, false, "The zip has no backup in it: backup.json is missing. Upload the zip the backup tool made, without unzipping it.");
        var restored = new RestoreDecision(now - 2 * Day, "restored", 1001, "Hunter01", false, "");
        KeptZip Kept(long ago, string note) => new(now - ago, note);
        const string Waiting = "Waiting for the operator.";
        var (review, decisions, restoredId) = (new List<KeptZip>(), new List<RestoreDecision>(), (long?)null);
        switch (state)
        {
            case "uploading":
                (Busy, Uploading, Progress, FileName, FileSize) = (true, true, .42, "backup-2026-11-12.zip", 412L << 20);
                break;
            case "waiting":
                review.AddRange([Kept(2 * Day, Waiting), Kept(3600, "The operator could not approve it: this backup has a user id this server does not accept.")]);
                break;
            case "waiting-other":
                (OtherTool, Result) = (true, new("review", ""));
                review.Add(Kept(120, Waiting));
                break;
            case "refused-program":
            case "refused-pictures":
                var why = state == "refused-program"
                    ? "This zip holds a program, not a backup: upload the file the program made (it ends in _PRIVATE.zip)"
                    : "This zip holds only pictures, not a backup";
                decisions.Add(refused with { Reason = why });
                Result = new("invalid", why);
                break;
            case "restored":
                (restoredId, decisions) = (1001, [restored with { HandleKept = true }]);
                break;
            case "rejected":
                decisions.Add(rejected with { Ts = now - 3 * 3600 });
                break;
            case "refused":
                decisions.AddRange([rejected, refused]);
                Result = new("invalid", refused.Reason);
                break;
            case "quota":
                review.AddRange([Kept(5 * Day, Waiting), Kept(2 * Day, Waiting), Kept(3600, Waiting)]);
                Result = new("quota", "You have 3 backups waiting for the operator. This is the limit.");
                break;
            case "paused":
                Result = new("paused", "The operator has paused uploads.");
                break;
            case "history":
                (restoredId, review) = (1001, [Kept(1800, Waiting)]);
                decisions.AddRange([rejected, refused with { Ts = now - 6 * Day }, restored]);
                break;
        }
        Status = new(restoredId, review, decisions);
        Archive = claim == "off" ? new(false, null) : new(true, claim switch
        {
            "waiting" => new("waiting", "OldHunter", now - 3600),
            "restored" => new("restored", "OldHunter", now - 4 * Day),
            "rejected" => new("rejected", "OldHunter", now - 4 * Day),
            _ => null,
        });
        if (claim == "found") Found = new("found", FakeProfile);
    }

    static readonly ArchivedProfile FakeProfile = new("OldHunter", 1234, 40, 12, 3, true, true, false, true);

    // ---- actions: real service calls, or a local simulation in fake mode ----
    public void Pick(IBrowserFile f)
    {
        (File, FileName, FileSize, Result) = (f, f.Name, f.Size, null);
    }

    public async Task Send()
    {
        if (!HasFile) return;
        (Busy, Uploading, Result, Progress) = (true, true, null, Fake ? 0 : null);
        changed();
        if (Fake)
        {
            for (var i = 1; i <= 5; i++) { await Task.Delay(300); Progress = i / 5.0; changed(); }
            Result = new("review", "");
            Status = Status! with { Review = [.. Status.Review, new(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "Waiting for the operator.")] };
        }
        else
        {
            if (File!.Size > ServerAccountApi.MaxZipBytes)
                Result = new("too_large", "");
            else
            {
                await using var zip = File.OpenReadStream(ServerAccountApi.MaxZipBytes);
                Result = await service.UploadAsync(zip, File.Size);
            }
            Status = await service.StatusAsync();
        }
        (File, FileName, FileSize, Busy, Uploading, Progress) = (null, "", 0, false, false, null);
        changed();
    }

    public async Task Find()
    {
        (Busy, ClaimResult, UseOldName) = (true, null, false);
        changed();
        Found = Fake
            ? OldName.Trim().Equals("oldhunter", StringComparison.OrdinalIgnoreCase) ? new("found", FakeProfile) : new("notfound")
            : await service.FindArchivedAsync(OldName.Trim());
        Busy = false;
        changed();
    }

    public async Task Claim()
    {
        if (Found?.Profile is not { } p) return;
        Busy = true;
        changed();
        ClaimResult = Fake ? new("review", "") : await service.ClaimArchivedAsync(p.Handle, UseOldName);
        if (ClaimResult.Status is "restored" or "review")
        {
            Found = null;
            if (Fake) Archive = new(true, new("waiting", p.Handle, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            else (Archive, Status) = (await service.ArchiveStatusAsync(), await service.StatusAsync());
        }
        Busy = false;
        changed();
    }

    /// <summary>True when the account was deleted (the page then leaves).</summary>
    public async Task<bool> Delete()
    {
        Busy = true;
        changed();
        DeleteFailed = !Fake && !await service.DeleteAsync();
        Busy = false;
        changed();
        return !Fake && !DeleteFailed;
    }

    // ---- what the player should see, decided once for every variant ----

    /// <summary>The tone of a state, which the variants colour: ok (green), wait (blue), warn (amber), bad (red), idle, busy.</summary>
    public sealed record Headline(string Tone, string Icon, string Title, string Detail, string Next);

    /// <summary>One past or pending event: an upload kept for review, a decision on one, or the archive claim.</summary>
    public sealed record Entry(string Tone, string Icon, string What, string Label, long Ts, string Sentence, string Detail, string Short = "");

    const string OtherReceived = "Received: a backup from another tool. An operator will check it."; // TODO resx

    const string RedoBackup = "Upload the zip your backup tool made, as it is. If you have none, make a new backup first.";

    /// <summary>The present: what happened to the upload just sent, else what is going on with the account's uploads now.</summary>
    public Headline Now
    {
        get
        {
            if (Uploading) return new("busy", "arrow-up", L["Restore.Working"].Value, FileName.Length > 0 ? $"{FileName} ({L.Size(FileSize)})" : "", "Keep this page open until it is done."); // TODO resx
            if (Result is { } r) return ForResult(r);
            if (Status is { RestoredUserId: { } id }) return new("ok", "check", L["Restore.Restored", id].Value, "", "Nothing more to do: your profile is on this account. A backup can be restored only once."); // TODO resx
            if (Status is { Review.Count: > 0 } s)
                return new("wait", "clock", OtherTool ? OtherReceived : L["Restore.Waiting", s.Review.Count].Value, "", "Nothing to do now. An operator checks each zip by hand, so it can take a while. You can still upload another (up to 3 wait at a time)."); // TODO resx
            if (Status?.Decisions is [.., { } last])
                return last.Kind switch
                {
                    "restored" => new("ok", "check", L["Restore.Done.restored", At(last.Ts), last.UserId ?? 0, last.Handle ?? ""].Value, L[last.HandleKept ? "Restore.Done.nameKept" : "Restore.Done.nameBackup"].Value, "Nothing more to do."), // TODO resx
                    "refused" => new("bad", "xmark", L["Restore.Done.refused", At(last.Ts)].Value, last.Reason, RedoBackup),
                    _ => new("bad", "xmark", L["Restore.Done.rejected", At(last.Ts)].Value, last.Reason, "Read the reason, then upload a different backup."), // TODO resx
                };
            return new("idle", "download", "No backup uploaded yet", "", "Choose your backup zip and upload it."); // TODO resx
        }
    }

    Headline ForResult(RestoreResult r)
    {
        var title = L["Restore.Result." + r.Status].Value;
        return r.Status switch
        {
            "restored" => new("ok", "check", title, r.Message, "Nothing more to do: your profile is on this account."), // TODO resx
            "already" => new("ok", "check", title, r.Message, "Nothing more to do."), // TODO resx
            "review" => new("wait", "clock", OtherTool ? OtherReceived : title, r.Message, "Nothing to do now. An operator checks it by hand, so it can take a while."), // TODO resx
            "quota" => new("warn", "clock", title, r.Message, "Wait until the operator has reviewed one of the zips waiting, then upload again."), // TODO resx
            "paused" or "busy" or "unavailable" => new("warn", "clock", title, r.Message, "Nothing is lost. Try again later."), // TODO resx
            "too_large" => new("bad", "xmark", title, r.Message, "A zip is at most 1 GiB. Make a smaller backup."), // TODO resx
            // the server also records an unreadable upload as a decision: that one has the date the sentence needs
            "invalid" => new("bad", "xmark", Status?.Decisions.LastOrDefault(d => d.Kind == "refused") is { } d ? L["Restore.Done.refused", At(d.Ts)].Value : title, r.Message, RedoBackup),
            "signed_out" => new("bad", "xmark", title, r.Message, "Sign in, then upload again."), // TODO resx
            _ => new("bad", "xmark", title, r.Message, "Check the message, then upload again."), // TODO resx
        };
    }

    /// <summary>The past, newest first: zips waiting for review, then decisions, then the claim. The newest refusal is left out while the result of the upload that made it is
    /// shown as <see cref="Now"/> (the server records every unreadable upload, so both would say the same thing).</summary>
    public IReadOnlyList<Entry> Entries
    {
        get
        {
            var list = new List<Entry>();
            if (Status is null) return list;
            list.AddRange(Status.Review.Select(k => new Entry("wait", "clock", "Backup zip", "Waiting for review", k.Ts, L["Restore.Kept", At(k.Ts)].Value, k.Note))); // TODO resx for What/Label
            var skip = Result?.Status == "invalid" ? Status.Decisions.LastOrDefault(d => d.Kind == "refused") : null;
            list.AddRange(Status.Decisions.Where(d => d != skip).Select(d => d.Kind switch
            {
                "restored" => new Entry("ok", "check", "Backup zip", L["Restore.Result.restored"].Value, d.Ts, L["Restore.Done.restored", At(d.Ts), d.UserId ?? 0, d.Handle ?? ""].Value, L[d.HandleKept ? "Restore.Done.nameKept" : "Restore.Done.nameBackup"].Value, $"User id {d.UserId}, name {d.Handle}"), // TODO resx
                "refused" => new Entry("bad", "xmark", "Backup zip", "Could not be read", d.Ts, L["Restore.Done.refused", At(d.Ts)].Value, d.Reason), // TODO resx
                _ => new Entry("bad", "xmark", "Backup zip", "Rejected by the operator", d.Ts, L["Restore.Done.rejected", At(d.Ts)].Value, d.Reason), // TODO resx
            }));
            if (Archive?.Claimed is { } c)
                list.Add(new(c.State switch { "restored" => "ok", "rejected" => "bad", _ => "wait" }, c.State switch { "restored" => "check", "rejected" => "xmark", _ => "clock" },
                    "Old profile", c.State switch { "restored" => "Restored", "rejected" => "Rejected", _ => "Waiting for review" }, c.Ts, L["Claim.State." + c.State, c.Handle, At(c.Ts)].Value, "")); // TODO resx for What/Label
            return [.. list.OrderByDescending(e => e.Ts)];
        }
    }
}
