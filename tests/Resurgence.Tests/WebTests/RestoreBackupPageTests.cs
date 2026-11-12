using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;
using Resurgence.Ui.Pages;

namespace Resurgence.Tests.WebTests;

public sealed class RestoreBackupPageTests : UiContext
{
    sealed class Fixed(RestoreStatus status, ArchiveStatus? archive = null, ArchiveFind? find = null) : IRestoreService
    {
        public Task<RestoreStatus?> StatusAsync() => Task.FromResult<RestoreStatus?>(status);
        public Task<RestoreResult> UploadAsync(Stream zip, long length) => Task.FromResult(new RestoreResult("review", ""));
        public Task<bool> DeleteAsync() => Task.FromResult(false);
        public Task<ArchiveStatus?> ArchiveStatusAsync() => Task.FromResult(archive ?? new ArchiveStatus(false, null));
        public Task<ArchiveFind> FindArchivedAsync(string name) => Task.FromResult(find ?? new ArchiveFind("notfound"));
        public Task<RestoreResult> ClaimArchivedAsync(string oldName, bool useOldName) => Task.FromResult(new RestoreResult("restored", ""));
    }

    const long At = 1_700_000_000;  // 2023-11-14 22:13 UTC

    [Fact]
    public void AKeptZipShowsItsTimeAndTheServersReason()
    {
        Services.AddSingleton<IRestoreService>(new Fixed(new(null, [new KeptZip(At, "This backup has a user id this server does not accept.")], [])));
        var cut = Render<RestoreBackup>();
        var kept = Assert.Single(cut.FindAll("ul.kept li"));
        Assert.Contains("2023-11-14 22:13 UTC", kept.TextContent);
        Assert.Contains("This backup has a user id this server does not accept.", kept.TextContent);
    }

    [Fact]
    public void ARestoreShowsTheUserIdAndTheFinalNameAndARejectShowsItsReason()
    {
        Services.AddSingleton<IRestoreService>(new Fixed(new(null, [],
        [
            new RestoreDecision(At, "restored", 1001, "sample", HandleKept: false, ""),
            new RestoreDecision(At + 60, "rejected", 7, null, HandleKept: false, "This is not your backup."),
        ])));
        var cut = Render<RestoreBackup>();
        var ok = Assert.Single(cut.FindAll("div.alert.ok"));
        Assert.Contains("1001", ok.TextContent);
        Assert.Contains("sample", ok.TextContent);
        Assert.Contains("2023-11-14 22:13 UTC", ok.TextContent);
        var bad = Assert.Single(cut.FindAll("div.alert.bad"));
        Assert.Contains("This is not your backup.", bad.TextContent);
        Assert.Contains("2023-11-14 22:14 UTC", bad.TextContent);
    }

    [Fact]
    public void AKeptNameAndAnAdoptedNameAreToldApart()
    {
        Services.AddSingleton<IRestoreService>(new Fixed(new(null, [],
        [
            new RestoreDecision(At, "restored", 1001, "sample", HandleKept: true, ""),
            new RestoreDecision(At, "restored", 1001, "sample", HandleKept: false, ""),
        ])));
        var alerts = Render<RestoreBackup>().FindAll("div.alert.ok");
        Assert.Equal(2, alerts.Count);
        Assert.NotEqual(alerts[0].TextContent, alerts[1].TextContent);
    }

    [Fact]
    public void AFoundOldNameShowsItsCard()
    {
        var found = new ArchiveFind("found", new("SampleHunter", 1234, 40, 12, 3, true, true, false, true));
        Services.AddSingleton<IRestoreService>(new Fixed(new(null, [], []), new ArchiveStatus(true, null), found));
        var cut = Render<RestoreBackup>();
        cut.Find("#oldname").Input("samplehunter");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Find").Click();
        Assert.Contains("SampleHunter", cut.Markup);
        Assert.Contains("1234", cut.Markup);
    }

    [Fact]
    public void ADisabledArchiveHidesTheClaimBox()
    {
        Services.AddSingleton<IRestoreService>(new Fixed(new(null, [], [])));
        Assert.Empty(Render<RestoreBackup>().FindAll("#oldname"));
    }
}
