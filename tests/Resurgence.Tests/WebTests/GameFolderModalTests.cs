using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Services;
using Resurgence.Services.Mocks;
using Resurgence.Ui.Modals;

namespace Resurgence.Tests.WebTests;

/// <summary>The "Game folder" window: the install in use and how it was found, the other installs seen, and the buttons that apply (docs/launcher.md#settings-folders-and-caches).</summary>
public sealed class GameFolderModalTests : UiContext
{
    IRenderedComponent<GameFolderModal> Open(string? state)
    {
        Services.AddSingleton<ILauncherBridge>(new MockLauncherBridge(NullLogger<MockLauncherBridge>.Instance, state));
        return Render<GameFolderModal>();
    }

    static string[] Buttons(IRenderedComponent<GameFolderModal> cut) => cut.FindAll(".buttons button").Select(b => b.TextContent.Trim()).ToArray();

    [Fact]
    public void AnAutomaticInstallShowsItsFolderAndHowItWasFound()
    {
        var cut = Open(null);
        Assert.Contains("steamapps", cut.Find("#folder-path").GetAttribute("value"));
        Assert.Equal("Found in your Steam library", cut.Find("#folder-source").TextContent);
        Assert.Empty(cut.FindAll(".other"));
        Assert.Equal(["Choose folder…", "Open folder", "Close"], Buttons(cut));
    }

    [Fact]
    public void AnotherInstallIsNamedInAWarning()
    {
        var cut = Open("folder:two");
        Assert.Equal(@"Another install was found: E:\SteamLibrary\steamapps\common\Hunter", cut.Find(".other").TextContent);
    }

    [Fact]
    public void AChosenFolderCanGoBackToAutomatic()
    {
        var cut = Open("folder:manual");
        Assert.Equal("Chosen by you", cut.Find("#folder-source").TextContent);
        Assert.Equal(["Choose folder…", "Use automatic", "Open folder", "Close"], Buttons(cut));

        cut.Find("#folder-auto").Click();

        Assert.Equal("Found in your Steam library", cut.Find("#folder-source").TextContent);
        Assert.Empty(cut.FindAll("#folder-auto"));
    }

    [Fact]
    public void AChosenFolderThatLostTheGameIsNamedAndCanBeForgotten()
    {
        var cut = Open("folder:gone");
        Assert.Equal(@"The folder you chose no longer has the game: D:\Games\Old", cut.Find("#folder-gone").TextContent);
        Assert.Equal("Found in your Steam library", cut.Find("#folder-source").TextContent);
        Assert.Equal(["Choose folder…", "Use automatic", "Open folder", "Close"], Buttons(cut));

        cut.Find("#folder-auto").Click();

        Assert.Empty(cut.FindAll("#folder-gone"));
    }

    [Fact]
    public void ChoosingAFolderShowsItAsChosenByYou()
    {
        var cut = Open(null);
        cut.Find("#folder-choose").Click();
        Assert.Equal("Chosen by you", cut.Find("#folder-source").TextContent);
    }

    [Fact]
    public void WithoutAGameOnlyChooseAndCloseAreOffered()
    {
        var cut = Open("folder:none");
        Assert.Equal("The game was not found", cut.Find("#folder-none").TextContent);
        Assert.Empty(cut.FindAll("#folder-path"));
        Assert.Equal(["Choose folder…", "Close"], Buttons(cut));
    }

    [Fact]
    public void ARefusedFolderSaysWhyAndKeepsTheInstall()
    {
        var cut = Open("folder:refuse");
        cut.Find("#folder-choose").Click();
        Assert.StartsWith("The game was not found in that folder", cut.Find(".error").TextContent);
        Assert.Equal("Found in your Steam library", cut.Find("#folder-source").TextContent);
    }
}
