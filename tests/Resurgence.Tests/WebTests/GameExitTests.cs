using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Resurgence.Services;
using Resurgence.Services.Mocks;
using Resurgence.Ui.Modals;

namespace Resurgence.Tests.WebTests;

/// <summary>The window after the started game closed: "Done hunting for now?" only for a real hunt, "The game closed right away" with one action per cause for an early exit (docs/website.md, "The game closed right away").</summary>
public sealed class GameExitTests : UiContext
{
    sealed class Lines : ILogger<MockLauncherBridge>
    {
        public List<string> Seen { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Seen.Add(formatter(state, exception));
    }

    readonly Lines log = new();

    MockLauncherBridge Use(string? mock)
    {
        var bridge = new MockLauncherBridge(log, mock);
        Services.AddSingleton<ILauncherBridge>(bridge);
        return bridge;
    }

    static GameExit Early(string? cause) => new(1, 4, "steam", true, cause);

    [Fact]
    public void ARealHuntOpensTheDoneWindowAndAGameThatNeverAppearedNone()
    {
        var modal = Services.GetRequiredService<ModalService>();
        modal.GameEnded(null);
        Assert.Null(modal.Type);
        modal.GameEnded(new GameExit(0, 1800, "steam", false, null));
        Assert.Equal(typeof(GameStartedModal), modal.Type);
    }

    [Theory]
    [InlineData("steam", "#exit-report")]
    [InlineData("crash", "#exit-report")]
    [InlineData(null, "#exit-report")]  // a cause this launcher does not know reads as a crash
    [InlineData("blocked", "#exit-security")]
    [InlineData("runtime", "#exit-runtimes")]
    [InlineData("dll", "#exit-verify")]
    [InlineData("closed", "#exit-report")]  // the clock in step: the neutral text with the report button
    public void AnEarlyExitOpensItsOwnDialogWithOneAction(string? cause, string? action)
    {
        Use(null);
        var modal = Services.GetRequiredService<ModalService>();
        modal.GameEnded(Early(cause));
        Assert.Equal(typeof(GameExitDialog), modal.Type);
        Assert.Equal("alert", modal.Backdrop);

        var cut = Render<GameExitDialog>(p => p.Add(x => x.Exit, Early(cause)));
        Assert.Equal("The game closed right away", cut.Find("h2").TextContent);
        Assert.Equal(action is null ? ["exit-ok"] : [action.TrimStart('#'), "exit-ok"], cut.FindAll(".actions .btn").Select(b => b.Id));
        Assert.Equal("exit-ok", cut.Find("[autofocus]").Id);
        Assert.Single(cut.FindAll(".btn-primary"));  // one primary action: the cause's, or OK when there is none
    }

    [Fact]
    public void BlockedPointsToWindowsSecurity()
    {
        Use(null);
        var blocked = Render<GameExitDialog>(p => p.Add(x => x.Exit, Early("blocked")));
        Assert.Contains("Smart App Control", blocked.Markup);
        blocked.Find("#exit-security").Click();
        Assert.Contains("open windowsdefender://appbrowser", log.Seen);

    }

    [Fact]
    public void ADllCauseSaysAGameFileWasReplacedAndItsButtonOpensSteamsVerifyPage()
    {
        Use(null);
        var cut = Render<GameExitDialog>(p => p.Add(x => x.Exit, Early("dll")));
        Assert.Contains("A game file was replaced", cut.Find(".message").TextContent);
        Assert.Contains("steam_api.dll", cut.Find(".message").TextContent);
        cut.Find("#exit-verify").Click();
        Assert.Contains("open steam://validate/{steam_app_id}", log.Seen);
    }

    [Fact]
    public void ClosedBlamesTheClockOnlyWhenItIsOffAndSaysByHowMuch()
    {
        Use(null);
        var neutral = Render<GameExitDialog>(p => p.Add(x => x.Exit, Early("closed")));
        Assert.Equal("The game closed by itself before the hunt started.", neutral.Find(".message").TextContent);
        Assert.Equal("Send a problem report so we can look.", neutral.Find(".how").TextContent);
        Assert.DoesNotContain("clock", neutral.Markup);

        string How(TimeSpan off) => Render<GameExitDialog>(p => p.Add(x => x.Exit, Early("closed") with { ClockAhead = off })).Find(".how").TextContent;
        Assert.Equal("Your PC's clock is 3h ahead: set it to update automatically.", How(TimeSpan.FromHours(3)));
        Assert.Equal("Your PC's clock is 12m behind: set it to update automatically.", How(TimeSpan.FromMinutes(-12)));
        var clock = Render<GameExitDialog>(p => p.Add(x => x.Exit, Early("closed") with { ClockAhead = TimeSpan.FromHours(3) }));
        Assert.Equal(["exit-ok"], clock.FindAll(".actions .btn").Select(b => b.Id));  // the clock is the cause: nothing to send
    }

    [Theory]
    [InlineData("exit:noruntime", "The game folder has no components to install: verify the game in Steam.")]
    [InlineData("exit:runtime", null)]
    public void TheRuntimesButtonShowsTheErrorWhenTheGameFolderHasNoRuntimes(string mock, string? error)
    {
        Use(mock);
        var cut = Render<GameExitDialog>(p => p.Add(x => x.Exit, Early("runtime")));
        cut.Find("#exit-runtimes").Click();
        Assert.Equal(error is null ? [] : [error], cut.FindAll("#exit-error").Select(e => e.TextContent));
    }

    [Theory]
    [InlineData("exit:crash")]
    [InlineData("exit:steam")]
    public void TheMockedEarlyExitReachesTheDialogAndItsButtonOpensTheReportWithTheFailedStartsTrigger(string mock)
    {
        var bridge = Use(mock);
        var modal = Services.GetRequiredService<ModalService>();
        modal.GameStarted(bridge);
        SpinWait.SpinUntil(() => modal.Type is not null, TimeSpan.FromSeconds(10));
        Assert.Equal(typeof(GameExitDialog), modal.Type);

        var cut = Render<GameExitDialog>(p => p.Add(x => x.Exit, (GameExit)modal.Parameters![nameof(GameExitDialog.Exit)]!));
        cut.Find("#exit-report").Click();
        Assert.Equal(typeof(ReportModal), modal.Type);
        Assert.Equal(bridge.LastRun!.Trigger, modal.Parameters![nameof(ReportModal.Trigger)]);
        Assert.Equal(mock == "exit:crash" ? "exitcode" : "nowindow", bridge.LastRun.Trigger);
    }
}
