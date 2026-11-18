using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Resurgence.Services;
using Resurgence.Services.Mocks;
using Resurgence.Ui;
using Resurgence.Ui.Components;
using Resurgence.Ui.Modals;
using Resurgence.Ui.Pages;

namespace Resurgence.Tests.WebTests;

/// <summary>The dashboard's game check line, its "See all" window and the start errors that carry a check's fix (docs/website.md#the-game-check).</summary>
public sealed class GameCheckTests : UiContext
{
    sealed class Opened : ILogger<MockLauncherBridge>
    {
        public List<string> Urls { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception) is var line && line.StartsWith("open steam://")) Urls.Add(line[5..]);
        }
    }

    Opened Use(string? state)
    {
        var log = new Opened();
        Services.AddSingleton<ILauncherBridge>(new MockLauncherBridge(log, state));
        return log;
    }

    IRenderedComponent<GameCheck> Line(string? state, out Opened log)
    {
        log = Use(state);
        Services.GetRequiredService<HostMode>().Launcher = true;
        var cut = Render<GameCheck>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#game-check")));
        return cut;
    }

    [Fact]
    public void WhenAllIsWellTheLineIsGreenAndHasNoFix()
    {
        var cut = Line(null, out _);
        Assert.Equal("Game check: OK", cut.Find("#game-check .text").TextContent);
        Assert.Contains("ok", cut.Find("#game-check").ClassList);
        Assert.Empty(cut.FindAll("#game-check-fix"));
        Assert.Equal("See all", cut.Find("#game-check-all").TextContent);
    }

    [Theory]
    [InlineData("check:old", "block", "The game is not up to date: update it in Steam.", "Open Steam")]
    [InlineData("check:updating", "block", "Steam is still updating the game.", "Open Steam")]
    [InlineData("check:files", "block", "Some game files are missing: in Steam, Properties, Installed files, Verify integrity.", "Open Steam")]
    [InlineData("check:steam", "warn", "Steam is not signed in: the game will start without Steam.", "Open Steam")]
    [InlineData("steam:none", "warn", "Steam is not signed in: the game will start without Steam.", "Open Steam")]
    [InlineData("check:running", "block", "The game is already running: close it, then start it from here.", null)]
    [InlineData("check:path", "warn", @"The game folder's path has special characters (C:\Users\Zoë\Games\Hunter); a plain folder like C:\Games works best.", "Choose folder…")]
    [InlineData("check:disk", "warn", "Low disk space on C: (600 MB left).", null)]
    [InlineData("folder:none", "block", "The game was not found.", "Choose folder…")]
    public void AProblemShowsItsTextItsLevelAndItsFix(string state, string level, string text, string? button)
    {
        var cut = Line(state, out _);
        Assert.Equal(text, cut.Find("#game-check .text").TextContent);
        Assert.Contains(level, cut.Find("#game-check").ClassList);
        Assert.Equal(button, cut.FindAll("#game-check-fix").SingleOrDefault()?.TextContent);
    }

    [Fact]
    public void ABlockingProblemOutranksAnEarlierWarning()
    {
        var cut = Line("check:many", out _);
        Assert.Equal("The game is not up to date: update it in Steam.", cut.Find("#game-check .text").TextContent);
    }

    [Fact]
    public void TheFixOpensSteamAndTheChecksRunAgain()
    {
        var cut = Line("check:old", out var log);

        cut.Find("#game-check-fix").Click();

        Assert.Equal(["steam://open/main"], log.Urls);
        cut.WaitForAssertion(() => Assert.Equal("Game check: OK", cut.Find("#game-check .text").TextContent));
    }

    [Fact]
    public void MissingFilesSendThePlayerToSteamsVerifyPage()
    {
        var cut = Line("check:files", out var log);
        cut.Find("#game-check-fix").Click();
        Assert.Equal(["steam://nav/games/details/{steam_app_id}"], log.Urls); // the launcher puts its own id in
    }

    [Fact]
    public void ARefusedStartWhoseProblemIsGoneDoesNotShowEmptyValues()
    {
        Use(null); // no check holds the port any more
        var cut = Render<StartFailedDialog>(p => p.Add(a => a.Error, "Check.Setup.Port"));
        cut.WaitForAssertion(() => Assert.Equal("The game could not be started.", cut.Find("p").TextContent));
    }

    [Fact]
    public void ChoosingAFolderOpensTheGameFolderWindowAndTheChecksRunAgainWhenItCloses()
    {
        var cut = Line("folder:none", out _);
        var modal = Services.GetRequiredService<ModalService>();

        cut.Find("#game-check-fix").Click();
        Assert.Equal(typeof(GameFolderModal), modal.Type);

        var window = Render<GameFolderModal>();
        window.Find("#folder-choose").Click(); // the mock's picker chooses a folder with the game
        modal.Close();
        cut.WaitForAssertion(() => Assert.Equal("Game check: OK", cut.Find("#game-check .text").TextContent));
    }

    [Fact]
    public void SeeAllListsEveryCheckWithItsLevel()
    {
        var cut = Line("check:many", out _);
        var modal = Services.GetRequiredService<ModalService>();

        cut.Find("#game-check-all").Click();
        Assert.Equal(typeof(GameCheckModal), modal.Type);

        var all = Render<GameCheckModal>(p => p.Add(a => a.Results, (IReadOnlyList<InstallCheck>)modal.Parameters![nameof(GameCheckModal.Results)]!));
        Assert.Equal(["game", "version", "files", "path", "steam", "setup", "windows", "disk", "running"], all.FindAll("li").Select(li => li.GetAttribute("data-check")));
        Assert.Equal(["OK", "Blocks the start", "OK", "Warning", "OK", "OK", "OK", "Warning", "OK"], all.FindAll("li .level").Select(l => l.TextContent));
        Assert.Contains("Low disk space on C:", all.Find("li[data-check=disk] .text").TextContent);
        Assert.Empty(all.FindAll("li.ok .text"));
        Assert.Equal(["Open Steam", "Choose folder…"], all.FindAll("li .fix").Select(b => b.TextContent)); // the old game and the odd path
    }

    [Theory]
    [InlineData("dashboard")]
    [InlineData("single")]
    [InlineData("multi")]
    [InlineData("find")]
    [InlineData("tutorials")]
    public void TheDashboardAndEveryHuntNowPageShowTheLineInTheLauncherOnly(string page)
    {
        Use("check:old");
        IRenderedComponent<Microsoft.AspNetCore.Components.IComponent> Open() => page switch
        {
            "dashboard" => Render<Dashboard>(),
            "single" => Render<LaunchMap>(p => p.Add(m => m.Kind, 1)),
            "multi" => Render<LaunchMap>(p => p.Add(m => m.Kind, 0)),
            "find" => Render<Servers>(),
            _ => Render<Tutorials>(),
        };

        Services.GetRequiredService<HostMode>().Launcher = true;
        var launcher = Open();
        launcher.WaitForAssertion(() => Assert.Equal("The game is not up to date: update it in Steam.", launcher.Find("#game-check .text").TextContent));

        Services.GetRequiredService<HostMode>().Launcher = false;
        var web = Open();
        web.WaitForAssertion(() => Assert.NotEmpty(web.FindAll(".page-title, .tutorials")));
        Assert.Empty(web.FindAll("#game-check"));
    }

    [Theory]
    [InlineData("check:old", "Check.Version.Old", "The game is not up to date: update it in Steam.", "Open Steam")]
    [InlineData("check:files", "Check.Files.Missing", "Some game files are missing: in Steam, Properties, Installed files, Verify integrity.", "Open Steam")]
    [InlineData("check:setup", "Kit.NoHosts", "The hosts file has no block from setup (not run, or removed), so the game still talks to its own servers.", "Set up")]
    [InlineData("check:steam", "Launch.NoSteam", "Start Steam and sign in, then try again.", "Open Steam")]
    [InlineData("check:running", "Launch.AlreadyRunning", "The game is already running.", null)]
    [InlineData("folder:none", "Launch.NoGame", "The game was not found. Check its folder in the settings.", "Choose folder…")]
    [InlineData(null, "Launch.StartFailed", "The game could not be started.", null)]
    public void ARefusedStartSaysWhyAndOffersTheFixOfItsCheck(string? state, string error, string text, string? button)
    {
        Use(state);
        var cut = Render<StartFailedDialog>(p => p.Add(a => a.Error, error));
        cut.WaitForAssertion(() => Assert.Equal(text, cut.Find("p").TextContent));
        Assert.Equal(button, cut.FindAll("#alert-fix").SingleOrDefault()?.TextContent);
    }

    [Fact]
    public void ARefusedStartForAMissingGameOffersTheGameFolderWindow()
    {
        Use(null);
        var modal = Services.GetRequiredService<ModalService>();
        modal.StartFailed(Result.Fail("Launch.NoGame"));
        Assert.Equal(typeof(StartFailedDialog), modal.Type);
        Assert.Equal("Launch.NoGame", modal.Parameters![nameof(StartFailedDialog.Error)]);

        var cut = Render<StartFailedDialog>(p => p.Add(a => a.Error, "Launch.NoGame"));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#alert-fix")));
        cut.Find("#alert-fix").Click();
        Assert.Equal(typeof(GameFolderModal), modal.Type);
    }

    [Fact]
    public void TheFixOfARefusedStartRunsAsFromTheLine()
    {
        var log = Use("check:old");
        var cut = Render<StartFailedDialog>(p => p.Add(a => a.Error, "Check.Version.Old"));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#alert-fix")));
        cut.Find("#alert-fix").Click();
        Assert.Equal(["steam://open/main"], log.Urls);
    }

    [Fact]
    public void AMockStartIsRefusedWithTheTextOfTheFirstBlock()
    {
        Assert.Equal("Check.Version.Old", new MockLauncherBridge(NullLogger(), "check:old").StartGame("x").Error);
        Assert.Equal("Launch.NoGame", new MockLauncherBridge(NullLogger(), "folder:none").StartGame("x").Error);
        Assert.True(new MockLauncherBridge(NullLogger(), "check:path").StartGame("x").Ok); // a warning does not stop it
        Assert.True(new MockLauncherBridge(NullLogger(), "check:steam").StartGame("x").Ok); // nor does a Steam that is not signed in: the game starts without it
    }

    static ILogger<MockLauncherBridge> NullLogger() => Microsoft.Extensions.Logging.Abstractions.NullLogger<MockLauncherBridge>.Instance;
}
