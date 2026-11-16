using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;
using Resurgence.Ui.Pages;

namespace Resurgence.Tests.WebTests;

public sealed class RestoreBackupPageTests : UiContext
{
    sealed class Fixed : IRestoreService
    {
        public RestoreStatus Status = new(null, [], []);
        public ArchiveStatus? Archive;
        public ArchiveFind? Find;

        public Task<RestoreStatus?> StatusAsync() => Task.FromResult<RestoreStatus?>(Status);
        public Task<RestoreResult> UploadAsync(Stream zip, long length, IProgress<long>? progress = null) => Task.FromResult(new RestoreResult("review", ""));
        public Task<bool> DeleteAsync() => Task.FromResult(false);
        public Task<ArchiveStatus?> ArchiveStatusAsync() => Task.FromResult(Archive ?? new ArchiveStatus(false, null));
        public Task<ArchiveFind> FindArchivedAsync(string name) => Task.FromResult(Find ?? new ArchiveFind("notfound"));
        public Task<RestoreResult> ClaimArchivedAsync(string oldName, bool useOldName) => Task.FromResult(new RestoreResult("restored", ""));
    }

    const long At = 1_700_000_000;  // 2023-11-14 22:13 UTC

    readonly Fixed service = new();

    public RestoreBackupPageTests() => Services.AddSingleton<IRestoreService>(service);

    /// <summary>A new page over the given server answers (one test may open several).</summary>
    IRenderedComponent<RestoreBackup> Page(RestoreStatus status, ArchiveStatus? archive = null, ArchiveFind? find = null)
    {
        (service.Status, service.Archive, service.Find) = (status, archive, find);
        return Render<RestoreBackup>();
    }

    static RestoreStatus Empty => new(null, [], []);

    [Fact]
    public void ANewPlayerLandsOnStepOneAndAPlayerWithAResultOnStepThree()
    {
        var one = Page(Empty);
        Assert.Contains("What do you have?", one.Find(".stage h3").TextContent);
        Assert.Empty(one.FindAll(".now"));
        Assert.Equal(3, one.FindAll("ol.steps li").Count);
        Assert.Single(one.FindAll("button.choice"));  // the archive is off: no "no backup" choice
    }

    [Fact]
    public void AWaitingZipLandsOnStepThreeWithItsCardANextLineAndItsRow()
    {
        var cut = Page(new(null, [new KeptZip(At, "waiting for the operator")], []));
        var now = cut.Find(".now");
        Assert.Equal("wait", now.GetAttribute("data-tone"));
        Assert.Contains("Received: an operator will check it", now.TextContent);
        Assert.Contains("Next:", now.TextContent);
        var row = Assert.Single(cut.FindAll("tbody tr"));
        Assert.Contains("2023-11-14 22:13 UTC", row.TextContent);
        Assert.Contains("Waiting for review", row.TextContent);
        Assert.Contains("Made with our backup tool", row.TextContent);
    }

    [Fact]
    public void AZipFromAnotherToolIsToldApart()
    {
        var cut = Page(new(null, [new KeptZip(At, "waiting for the operator", "other")], []));
        Assert.Contains("a backup from another tool", cut.Find(".now").TextContent);
        Assert.Contains("Made with another tool", cut.Find("tbody tr").TextContent);
    }

    [Fact]
    public void SeveralWaitingZipsAreCounted()
    {
        var cut = Page(new(null, [new KeptZip(At, "waiting for the operator"), new KeptZip(At + 60, "The operator could not approve it: this backup has a user id this server does not accept.")], []));
        Assert.Contains("2", cut.Find(".now .t").TextContent);
        var rows = cut.FindAll("tbody tr");
        Assert.Equal(2, rows.Count);
        Assert.Contains("could not approve it", rows[0].TextContent);  // the newest first
    }

    [Fact]
    public void ARestoreShowsItsCardAndARejectIsToldApartFromAnUnreadableZip()
    {
        var ok = Page(new(1001, [], [new RestoreDecision(At, "restored", 1001, "sample", HandleKept: false, "")]));
        Assert.Equal("ok", ok.Find(".now").GetAttribute("data-tone"));
        Assert.Contains("1001", ok.Find(".now").TextContent);
        Assert.Contains("Next:", ok.Find(".now").TextContent);
        Assert.Contains("User id 1001, name sample", ok.Find("tbody tr").TextContent);

        var rejected = Page(new(null, [], [new RestoreDecision(At, "rejected", 7, null, HandleKept: false, "This is not your backup.")]));
        Assert.Equal("bad", rejected.Find(".now").GetAttribute("data-tone"));
        Assert.Contains("Rejected by the operator on 2023-11-14 22:13 UTC", rejected.Find(".now").TextContent);
        Assert.Contains("This is not your backup.", rejected.Find(".now").TextContent);
        Assert.Contains("Next:", rejected.Find(".now").TextContent);
    }

    [Fact]
    public void AnUnreadableZipSaysCouldNotBeReadWithTheServersReasonNotRejectedByTheOperator()
    {
        const string why = "This zip holds a program, not a backup: upload the file the program made (it ends in _PRIVATE.zip)";
        var cut = Page(new(null, [], [new RestoreDecision(At, "refused", null, null, HandleKept: false, why)]));
        Assert.Contains("could not be read", cut.Find(".now").TextContent);
        Assert.Contains(why, cut.Find(".now").TextContent);
        Assert.Contains("Next:", cut.Find(".now").TextContent);
        var row = cut.Find("tbody tr");
        Assert.Contains("Could not be read", row.TextContent);
        Assert.Contains(why, row.TextContent);
        Assert.DoesNotContain("Rejected by the operator", cut.Markup);
    }

    [Fact]
    public void AClaimIsACardAndARow()
    {
        var found = Page(Empty, new ArchiveStatus(true, new("waiting", "SampleHunter", At)));
        Assert.Equal("wait", found.Find(".now").GetAttribute("data-tone"));
        Assert.Contains("SampleHunter", found.Find(".now").TextContent);
        Assert.Contains("Old profile", found.Find("tbody tr").TextContent);
        Assert.Equal("ok", Page(Empty, new ArchiveStatus(true, new("restored", "SampleHunter", At))).Find(".now").GetAttribute("data-tone"));
        Assert.Equal("bad", Page(Empty, new ArchiveStatus(true, new("rejected", "SampleHunter", At))).Find(".now").GetAttribute("data-tone"));
    }

    [Fact]
    public void TheTableListsEveryDecisionAndTheCurrentOneToo()
    {
        var cut = Page(new(null, [new KeptZip(At + 300, "waiting for the operator")],
        [
            new RestoreDecision(At, "rejected", 7, null, false, "No."),
            new RestoreDecision(At + 60, "refused", null, null, false, "Not a backup."),
            new RestoreDecision(At + 120, "restored", 1001, "sample", false, ""),
        ]), new ArchiveStatus(true, new("rejected", "SampleHunter", At + 200)));
        Assert.Equal(5, cut.FindAll("tbody tr").Count);
    }

    [Fact]
    public void WithoutRoomThePageSaysWhyInsteadOfOfferingAnotherUpload()
    {
        Assert.Contains("Upload another backup", Page(new(null, [new KeptZip(At, "waiting for the operator")], [])).Markup);
        var full = Page(new(null, [new KeptZip(At, "waiting for the operator")], [], Room: false));
        Assert.DoesNotContain("Upload another backup", full.Markup);
        Assert.Contains("cannot upload another backup now", full.Markup);
    }

    [Fact]
    public void UploadAnotherGoesBackToStepOneAndTheUploadStepHasItsChecks()
    {
        var cut = Page(new(null, [new KeptZip(At, "waiting for the operator")], []));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Upload another backup").Click();
        Assert.Contains("What do you have?", cut.Find(".stage h3").TextContent);
        cut.Find("button.choice").Click();
        Assert.Equal(3, cut.FindAll(".rules li").Count);
        Assert.Contains("All the rules", cut.Find("details summary").TextContent);
        Assert.NotNull(cut.Find("#zip"));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Back").Click();
        Assert.Contains("What do you have?", cut.Find(".stage h3").TextContent);
    }

    [Fact]
    public void AFoundOldNameShowsItsCard()
    {
        var found = new ArchiveFind("found", new("SampleHunter", 1234, 40, 12, 3, true, true, false, true));
        var cut = Page(Empty, new ArchiveStatus(true, null), found);
        Assert.Equal(2, cut.FindAll("button.choice").Count);
        cut.FindAll("button.choice")[1].Click();
        cut.Find("#oldname").Input("samplehunter");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Find").Click();
        Assert.Contains("SampleHunter", cut.Markup);
        Assert.Contains("1234", cut.Markup);
    }

    [Fact]
    public void AClaimMovesOnToWhatHappensNext()
    {
        var found = new ArchiveFind("found", new("SampleHunter", 1234, 40, 12, 3, true, true, false, true));
        var cut = Page(Empty, new ArchiveStatus(true, null), found);
        cut.FindAll("button.choice")[1].Click();
        cut.Find("#oldname").Input("samplehunter");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Find").Click();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Claim this profile").Click();
        Assert.Contains("What happens next", cut.Find(".stage h3").TextContent);
    }

    [Fact]
    public void ADisabledArchiveOrAClaimedOneHidesTheClaimChoice()
    {
        Assert.Single(Page(Empty).FindAll("button.choice"));
        var claimed = Page(Empty, new ArchiveStatus(true, new("waiting", "SampleHunter", At)));
        Assert.Empty(claimed.FindAll("button.choice"));  // lands on step 3, where the claim is shown
        claimed.FindAll("button").First(b => b.TextContent.Trim() == "Upload another backup").Click();
        Assert.Single(claimed.FindAll("button.choice"));
    }
}
