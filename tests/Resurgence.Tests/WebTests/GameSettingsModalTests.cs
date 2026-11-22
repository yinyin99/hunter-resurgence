using Bunit;
using Resurgence.Ui.Modals;

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
}
