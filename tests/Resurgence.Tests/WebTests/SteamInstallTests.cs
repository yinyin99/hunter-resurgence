using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Resurgence.Services;
using Resurgence.Services.Mocks;
using Resurgence.Ui.Components;
using Resurgence.Ui.Modals;

namespace Resurgence.Tests.WebTests;

/// <summary>"Install with Steam" where the game is not found, and the server's licence result next to it (docs/launcher.md#install-with-steam).</summary>
public sealed class SteamInstallTests : UiContext
{
    sealed class Opened : ILogger<MockLauncherBridge>
    {
        public List<string> Urls { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception) is { } line && line.StartsWith("open ")) Urls.Add(line[5..]);
        }
    }

    Opened Use(string state)
    {
        var log = new Opened();
        Services.AddSingleton<ILauncherBridge>(new MockLauncherBridge(log, state));
        Services.AddSingleton<ISessionService>(new MockSessionService(() => state));
        return log;
    }

    [Fact]
    public void TheButtonOpensSteamsInstallDialogForTheGamesAppId()
    {
        var log = Use("licence:unknown");
        var cut = Render<GameFolderModal>();

        Assert.Equal("Install with Steam", cut.Find("#steam-install").TextContent);
        cut.Find("#steam-install").Click();

        Assert.Equal(["steam://install/{steam_app_id}"], log.Urls);  // the launcher puts its steam_app_id in place of the placeholder
    }

    [Fact]
    public void WithoutSteamTheButtonSaysSoAndOpensSteamsDownloadPage()
    {
        var log = Use("licence:nosteam");
        var cut = Render<GameFolderModal>();

        Assert.Equal("Steam is not installed: get Steam", cut.Find("#steam-install").TextContent);
        cut.Find("#steam-install").Click();

        Assert.Equal(["https://store.steampowered.com/about/"], log.Urls);
    }

    [Theory]
    [InlineData("licence:owned", "Your Steam account has the game: install it with Steam.", false)]
    [InlineData("licence:not_owned", "Your Steam account doesn't have the game's licence. What can I do?", true)]
    public void TheLicenceTheServerReportsIsSaidNextToTheButton(string state, string text, bool faq)
    {
        Use(state);
        var cut = Render<SteamInstall>();

        cut.WaitForAssertion(() => Assert.Equal(text, cut.Find("#steam-licence").TextContent));
        Assert.Equal(faq ? "/help#steam-licence" : null, cut.FindAll("#steam-faq").SingleOrDefault()?.GetAttribute("href"));
        Assert.NotEmpty(cut.FindAll("#steam-install"));
    }

    [Fact]
    public void AnUnknownLicenceSaysNothingExtra()
    {
        Use("licence:unknown");
        var cut = Render<SteamInstall>();

        Assert.Empty(cut.FindAll("#steam-licence"));
        Assert.NotEmpty(cut.FindAll("#steam-install"));
    }

    [Fact]
    public void CheckAgainFindsTheGameInstalledMeanwhile()
    {
        Use("licence:owned");
        var cut = Render<GameFolderModal>();
        Assert.Equal("The game was not found", cut.Find("#folder-none").TextContent);

        cut.Find("#steam-install").Click();
        cut.Find("#steam-again").Click();

        Assert.Contains("steamapps", cut.Find("#folder-path").GetAttribute("value"));
        Assert.Empty(cut.FindAll("#steam-install"));
    }

    [Fact]
    public void ARefusedStartForAMissingGameOffersTheButtonToo()
    {
        Use("licence:owned");
        var cut = Render<StartFailedDialog>(p => p.Add(a => a.Error, "Launch.NoGame"));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#steam-install")));
        Assert.Empty(cut.FindAll("#steam-again"));  // the player starts again from the page once the game is there
    }
}
