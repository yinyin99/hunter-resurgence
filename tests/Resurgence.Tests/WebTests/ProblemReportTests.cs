using Bunit;
using Microsoft.JSInterop;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;
using Resurgence.Ui;
using Resurgence.Ui.Layouts;
using Resurgence.Ui.Modals;
using Resurgence.Ui.Shell;

namespace Resurgence.Tests.WebTests;

/// <summary>The problem report's first-launch consent, the Settings entry, and the "Done hunting for now?" window after a failed start (docs/launcher.md#report-a-problem).</summary>
public sealed class ProblemReportTests : UiContext
{
    sealed class Bridge(string consent = "unset", GameRun? run = null, ReportSent? answer = null, bool saveFails = false, bool showFails = false) : ILauncherBridge
    {
        public bool IsSteam => false;
        public bool WaitingForSteam => false;
        public void StartWithoutSteam() { }
        public event Action? StartStateChanged { add { } remove { } }
        public string? LastStartMode => null;
        public Result OpenFolder(string kind) => Result.Success;
        public List<string> Shown { get; } = [];
        public Result ShowFile(string path) { Shown.Add(path); return showFails ? Result.Fail("Report.ErrShow") : Result.Success; }
        public GameInstallInfo GameInstall => new(null, null, []);
        public Result ChooseGameFolder() => Result.Success;
        public void UseAutomaticGameFolder() { }
        public Task<IReadOnlyList<InstallCheck>> CheckInstallAsync() => Task.FromResult<IReadOnlyList<InstallCheck>>([]);
        public Result ClearCache(string kind) => Result.Success;
        public void OpenExternal(string url) { }
        public Result StartGame(string url) => Result.Success;
        public Task<GameExit?> WhenGameExitsAsync() => Task.FromResult<GameExit?>(new(0, 1800, "steam", false, null));
        public Result RunGameRuntimes() => Result.Success;
        public void ToggleDevTools() { }
        public void RestartLauncher() { }

        public GameRun? LastRun => run;
        public string ReportConsent { get; set; } = consent;
        public List<string> Built { get; } = [];
        public int Sent { get; private set; }
        public int Saved { get; private set; }

        public Task<ProblemReport> BuildReportAsync(string trigger)
        {
            Built.Add(trigger);
            return Task.FromResult(new ProblemReport("report.zip", ["debug.log", "system.txt"], ["cef.log"], 1234, trigger));
        }

        public Task<ReportSent> SendReportAsync(ProblemReport report)
        {
            Sent++;
            return Task.FromResult(answer ?? new ReportSent("p7-0a1b2c3d", null));
        }

        public string SaveReport(ProblemReport report) { Saved++; return saveFails ? throw new UnauthorizedAccessException() : @"C:\Downloads\report.zip"; }
    }

    sealed class NoRestoreOffer : IRestoreOfferService
    {
        public Task<bool> FirstAsync() => Task.FromResult(false);
    }

    static readonly GameRun Failed = new(DateTimeOffset.Now, TimeSpan.FromSeconds(20), 0, true);
    static readonly GameRun Fine = new(DateTimeOffset.Now, TimeSpan.FromMinutes(30), 0, true);

    Bridge Use(Bridge bridge)
    {
        Services.AddSingleton<ILauncherBridge>(bridge);
        return bridge;
    }

    [Fact]
    public async Task TheConsentDialogOpensOnlyWhileUnsetAndWaitsForAnotherDialog()
    {
        var modal = new ModalService();
        var bridge = new Bridge();
        modal.Alert("something else");
        var offer = modal.OfferReportConsentAsync(bridge);
        Assert.False(offer.IsCompleted);          // never over another dialog
        modal.Close();
        await offer;
        Assert.Equal(typeof(ReportConsentModal), modal.Type);
        modal.Close();

        bridge.ReportConsent = "ask";
        await modal.OfferReportConsentAsync(bridge);  // the player chose already: once
        Assert.Null(modal.Type);
    }

    [Theory]
    [InlineData("#consent-auto", "auto")]
    [InlineData("#consent-ask", "ask")]
    public void AButtonStoresTheChoiceAndClosesTheDialog(string button, string consent)
    {
        var bridge = Use(new Bridge());
        var modal = Services.GetRequiredService<ModalService>();
        modal.Show<ReportConsentModal>();
        var cut = Render<ReportConsentModal>();
        Assert.Empty(cut.FindAll("a"));  // no link that would leave the dialog
        Assert.DoesNotContain("password", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("consent-auto", cut.Find(".btn-primary").Id);  // "Send automatically" is the primary choice
        Assert.Equal("consent-auto", Assert.Single(cut.FindAll("[autofocus]")).Id);  // so Enter accepts it

        cut.Find(button).Click();
        Assert.Equal(consent, bridge.ReportConsent);
        Assert.Null(modal.Type);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]  // the website never asks
    public async Task TheShellAsksTheLauncherOnceAfterSignIn(bool launcher, bool asked)
    {
        Services.AddSingleton(new HostMode { Launcher = launcher });
        Services.AddSingleton<IRestoreOfferService>(new NoRestoreOffer());
        Use(new Bridge());
        var modal = Services.GetRequiredService<ModalService>();
        var cut = Render<ShellLayout>();
        if (asked) cut.WaitForAssertion(() => Assert.Equal(typeof(ReportConsentModal), modal.Type));
        else { await Task.Delay(300); Assert.NotEqual(typeof(ReportConsentModal), modal.Type); }  // the restore offer is none: the consent would be next
    }

    [Fact]
    public void AfterAFailedStartTheAgreedReportGoesOutOnceAndTheWindowShowsTheReference()
    {
        var bridge = Use(new Bridge("auto", Failed));
        var cut = Render<GameStartedModal>();
        cut.WaitForAssertion(() => Assert.Contains("p7-0a1b2c3d", cut.Find("#done-report").TextContent));
        cut.Render();
        Assert.Equal(["short"], bridge.Built);
        Assert.Equal(1, bridge.Sent);
        Assert.Empty(cut.FindAll("#done-report-link"));
    }

    [Fact]
    public void AFailedSendShowsItsError()
    {
        Use(new Bridge("auto", Failed, new ReportSent(null, "Report.ErrLimit")));
        var cut = Render<GameStartedModal>();
        cut.WaitForAssertion(() => Assert.Contains("24", cut.Find("#done-report").TextContent));
    }

    [Theory]
    [InlineData("ask")]
    [InlineData("unset")]
    public void WithoutTheAgreementTheWindowOffersTheReportAndSendsNothing(string consent)
    {
        var bridge = Use(new Bridge(consent, Failed));
        var modal = Services.GetRequiredService<ModalService>();
        var cut = Render<GameStartedModal>();
        cut.Find("#done-report-link").Click();
        Assert.Equal(typeof(ReportModal), modal.Type);
        Assert.Equal("short", modal.Parameters![nameof(ReportModal.Trigger)]);
        Assert.Equal(0, bridge.Sent);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("ask")]
    public void AGoodRunHasNoReportRow(string consent)
    {
        var bridge = Use(new Bridge(consent, Fine));
        var cut = Render<GameStartedModal>();
        Assert.Empty(cut.FindAll("#done-report"));
        Assert.Equal(0, bridge.Sent);
        Assert.Empty(bridge.Built);
    }

    [Fact]
    public void TheSettingsMenuReportsAProblemFromTheLauncherOnly()
    {
        Services.GetRequiredService<HostMode>().Launcher = true;
        var cut = Render<SettingsMenu>();
        cut.Find("#report-problem").Click();
        Assert.Equal(typeof(ReportModal), Services.GetRequiredService<ModalService>().Type);

        Services.GetRequiredService<HostMode>().Launcher = false;
        Assert.Empty(Render<SettingsMenu>().FindAll("#report-problem"));
    }

    [Fact]
    public void TheButtonBuildsAManualReportThatIsSavedThenSentWithItsReference()
    {
        var bridge = Use(new Bridge());
        var cut = Render<ReportModal>();
        cut.WaitForAssertion(() => Assert.Equal(["debug.log", "system.txt"], cut.FindAll("#report-files li").Select(li => li.TextContent)));
        Assert.Equal(["manual"], bridge.Built);
        Assert.Equal(["cef.log"], cut.FindAll("#report-missing li").Select(li => li.TextContent));
        Assert.Equal(2, cut.Find("details.files").QuerySelectorAll("ul").Length);  // both lists sit behind "Show files"
        Assert.Equal("report-send", cut.Find(".btn-primary").Id);

        cut.Find("#report-save").Click();
        Assert.Contains(@"C:\Downloads\report.zip", cut.Find("#report-saved").TextContent);

        cut.Find("#report-send").Click();
        cut.WaitForAssertion(() => Assert.Equal("p7-0a1b2c3d", cut.Find("#report-reference").GetAttribute("value")));
        Assert.Equal("Give this reference on Discord", cut.Find("#report-give").TextContent);
        Assert.DoesNotContain("p7-0a1b2c3d", cut.Find("#report-sent").TextContent);  // the reference stands alone in its box
        Assert.Equal(1, bridge.Sent);
        Assert.True(cut.Find("#report-send").HasAttribute("disabled"));
    }

    [Fact]
    public void CopyPutsExactlyTheReferenceOnTheClipboardAndSaysCopiedFor2Seconds()
    {
        var shell = JSInterop.SetupModule("./_content/Resurgence.Ui/shell.js");
        shell.SetupVoid("copy", _ => true).SetVoidResult();
        Use(new Bridge());
        var cut = Render<ReportModal>();
        cut.WaitForElement("#report-send").Click();
        cut.WaitForElement("#report-copy").Click();
        Assert.Equal("p7-0a1b2c3d", Assert.Single(shell.Invocations["copy"]).Arguments.Single());
        cut.WaitForAssertion(() => Assert.Equal("Copied", cut.Find("#report-copy").TextContent));
        cut.WaitForAssertion(() => Assert.Equal("Copy", cut.Find("#report-copy").TextContent), TimeSpan.FromSeconds(4));
    }

    [Fact]
    public void ARefusedClipboardLeavesTheReferenceSelectableAndSaysNothingCopied()
    {
        var shell = JSInterop.SetupModule("./_content/Resurgence.Ui/shell.js");
        shell.SetupVoid("copy", _ => true).SetException(new JSException("refused"));
        Use(new Bridge());
        var cut = Render<ReportModal>();
        cut.WaitForElement("#report-send").Click();
        cut.WaitForElement("#report-copy").Click();
        shell.VerifyInvoke("copy");
        Assert.Equal("Copy", cut.Find("#report-copy").TextContent);
        Assert.True(cut.Find("#report-reference").HasAttribute("readonly"));
    }

    [Fact]
    public void OpenFolderShowsTheSavedFile()
    {
        var bridge = Use(new Bridge());
        var cut = Render<ReportModal>();
        cut.WaitForElement("#report-save");
        Assert.Empty(cut.FindAll("#report-show"));  // nothing to show before Save
        cut.Find("#report-save").Click();
        cut.Find("#report-show").Click();
        Assert.Equal([@"C:\Downloads\report.zip"], bridge.Shown);
        Assert.Empty(cut.FindAll("[role=alert]"));
    }

    [Fact]
    public void AnExplorerFailureIsSaidAndSaveClearsIt()
    {
        Use(new Bridge(showFails: true));
        var cut = Render<ReportModal>();
        cut.WaitForElement("#report-save").Click();
        cut.Find("#report-show").Click();
        Assert.Contains("could not be shown", cut.Find("[role=alert]").TextContent);
        cut.Find("#report-save").Click();
        Assert.Empty(cut.FindAll("[role=alert]"));
    }

    [Fact]
    public void TheToggleStoresAutomaticOrAsk()
    {
        var bridge = Use(new Bridge("ask"));
        var cut = Render<ReportModal>();
        Assert.False(cut.Find("#report-auto").HasAttribute("checked"));
        cut.Find("#report-auto").Change(true);
        Assert.Equal("auto", bridge.ReportConsent);
        cut.Find("#report-auto").Change(false);
        Assert.Equal("ask", bridge.ReportConsent);
    }

    [Fact]
    public void AnUnknownSendErrorIsTheGenericOne()
    {
        Use(new Bridge(answer: new ReportSent(null, "whatever")));
        var cut = Render<ReportModal>();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#report-send")));
        cut.Find("#report-send").Click();
        cut.WaitForAssertion(() => Assert.Contains("could not be reached", cut.Find("[role=alert]").TextContent));
    }

    [Fact]
    public void ASaveThatCannotWriteSaysSoAndSendStillWorks()
    {
        var bridge = Use(new Bridge(saveFails: true));
        var cut = Render<ReportModal>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#report-files li")));
        cut.Find("#report-save").Click();
        Assert.Contains("could not be saved", cut.Find("[role=alert]").TextContent);
        Assert.Empty(cut.FindAll("#report-saved"));
        cut.Find("#report-send").Click();
        cut.WaitForAssertion(() => Assert.Equal(1, bridge.Sent));
    }

    [Fact]
    public void TheReportFromTheDoneWindowReplacesIt()
    {
        Use(new Bridge("ask", Failed));
        var modal = Services.GetRequiredService<ModalService>();
        var host = Render<ModalHost>();
        modal.Show<GameStartedModal>();
        host.WaitForElement("#done-report-link").Click();
        host.WaitForElement("#report-files li");
        Assert.Empty(host.FindAll(".done"));          // the Done window is gone, not stacked under the report
        Assert.Single(host.FindAll(".dialog-card"));
    }
}
