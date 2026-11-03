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
}
