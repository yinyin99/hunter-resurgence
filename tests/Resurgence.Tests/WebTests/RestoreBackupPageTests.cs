using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;
using Resurgence.Ui.Pages;

namespace Resurgence.Tests.WebTests;

public sealed class RestoreBackupPageTests : UiContext
{
    sealed class Fixed(RestoreStatus status) : IRestoreService
    {
        public Task<RestoreStatus?> StatusAsync() => Task.FromResult<RestoreStatus?>(status);
        public Task<RestoreResult> UploadAsync(Stream zip, long length) => Task.FromResult(new RestoreResult("review", ""));
        public Task<bool> DeleteAsync() => Task.FromResult(false);
    }

    const long At = 1_700_000_000;  // 2023-11-14 22:13 UTC

    [Fact]
    public void AKeptZipShowsItsTimeAndTheServersReason()
    {
        Services.AddSingleton<IRestoreService>(new Fixed(new(null, [new KeptZip(At, "The account has already played, so its user id cannot change.")], [])));
        var cut = Render<RestoreBackup>();
        var kept = Assert.Single(cut.FindAll("ul.kept li"));
        Assert.Contains("2023-11-14 22:13 UTC", kept.TextContent);
        Assert.Contains("The account has already played, so its user id cannot change.", kept.TextContent);
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
}
