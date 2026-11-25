using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Ui.Modals;
using Resurgence.Ui;

namespace Resurgence.Tests.WebTests;

public sealed class GameSettingsModalTests : UiContext
{
    [Theory]
    [InlineData("0", false)]
    [InlineData("1", false)]
    [InlineData("2", true)]
    public void TheMemoryWarningShowsOnlyOnHighTextures(string quality, bool warning)
    {
        var cut = Render<GameSettingsModal>();
        cut.Find("#texture_quality").Change(quality);
        Assert.Equal(warning ? 1 : 0, cut.FindAll("#highQualityTextureWarning").Count);
    }

    [Fact]
    public void TheFolderButtonOpensTheGameFolderWindowAndTheSteamBoxIsChecked()
    {
        var cut = Render<GameSettingsModal>();
        Assert.True(cut.Find("#start_with_steam").HasAttribute("checked"));
        cut.Find("#game-folder").Click();
        Assert.Equal(typeof(GameFolderModal), Services.GetRequiredService<ModalService>().Type);
    }

    // #810: Borderless (the default) runs at the screen's size, so the resolution list shows only in the other modes
    [Fact]
    public void TheResolutionListShowsOnlyOutsideBorderless()
    {
        var cut = Render<GameSettingsModal>();
        Assert.Equal("Borderless", cut.Find("#window_mode").GetAttribute("value"));
        Assert.Empty(cut.FindAll("#display_mode"));
        cut.Find("#window_mode").Change("Windowed");
        Assert.Single(cut.FindAll("#display_mode"));
    }

    // #825: the game's language by its own name, English by default, saved with the other settings; the units are the signed-in player's
    [Fact]
    public async Task TheGameLanguageListsTheGamesLanguagesByTheirOwnNameAndIsSaved()
    {
        var cut = Render<GameSettingsModal>();
        var options = cut.FindAll("#game_language option");
        Assert.Equal(22, options.Count);
        Assert.Contains(options, o => o.GetAttribute("value") == "fr_FR" && o.TextContent == "Français");
        Assert.Equal("en_US", cut.Find("#game_language").GetAttribute("value"));
        cut.Find("#game_language").Change("fr_FR");
        cut.Find(".btn-primary").Click();
        Assert.Equal("fr_FR", await Services.GetRequiredService<IGameSettingsService>().LanguageAsync());
    }

    [Fact]
    public void TheUnitsAreOfferedToASignedInPlayer() =>
        Assert.Equal(["False", "True"], Render<GameSettingsModal>().FindAll("#units option").Select(o => o.GetAttribute("value")));  // the mock session is signed in

    [Fact]
    public async Task ASignedOutPlayerHasNoUnits()
    {
        var session = new Resurgence.Services.Mocks.MockSessionService();
        await session.SignOutAsync();
        Services.AddSingleton<ISessionService>(session);
        Assert.Empty(Render<GameSettingsModal>().FindAll("#units"));
    }
}
