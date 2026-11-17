using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;
using Resurgence.Ui;
using Resurgence.Ui.Modals;
using Resurgence.Ui.Shell;

namespace Resurgence.Tests.WebTests;

/// <summary>The Settings menu's folder and cache entries: a refusal opens a dialog with its translated text; the entries that cannot be used yet say so to a screen reader too.</summary>
public sealed class SettingsMenuTests : UiContext
{
    sealed class Refusing(string error) : ILauncherBridge
    {
        public List<string> Asked { get; } = [];
        public bool IsSteam => false;
        public Result OpenFolder(string kind) { Asked.Add("open " + kind); return Result.Fail(error); }
        public GameInstallInfo GameInstall => new(null, null, []);
        public Result ChooseGameFolder() => Result.Success;
        public void UseAutomaticGameFolder() { }
        public Result ClearCache(string kind) { Asked.Add("clear " + kind); return Result.Fail(error); }
        public void OpenExternal(string url) { }
        public Result StartGame(string url) => Result.Success;
        public Task<bool> WhenGameExitsAsync() => Task.FromResult(true);
        public void ToggleDevTools() { }
        public void RestartLauncher() { }
        public GameRun? LastRun => null;
        public Task<ProblemReport> BuildReportAsync(string trigger) => throw new NotSupportedException();
        public Task<ReportSent> SendReportAsync(ProblemReport report) => throw new NotSupportedException();
        public string SaveReport(ProblemReport report) => throw new NotSupportedException();
        public string ReportConsent { get; set; } = "auto";
    }

    [Theory]
    [InlineData("Screenshots", "open screenshots", "Settings.NoFolder", "The folder was not found. The game may not be installed yet.")]
    [InlineData("Clear game cache", "clear game", "Settings.CacheGameRunning", "Close the game first, then clear its cache.")]
    [InlineData("Clear launcher cache", "clear launcher", "Settings.CacheFailed", "Some files could not be deleted. Try again later.")]
    public void ARefusedFolderOrCacheActionOpensADialogWithItsReason(string entry, string asked, string key, string text)
    {
        var bridge = new Refusing(key);
        Services.AddSingleton<ILauncherBridge>(bridge);
        Services.GetRequiredService<HostMode>().Launcher = true;
        var cut = Render<SettingsMenu>();

        cut.FindAll("li a").Single(a => a.TextContent.Trim() == entry).Click();

        Assert.Equal([asked], bridge.Asked);
        var modal = Services.GetRequiredService<ModalService>();
        Assert.Equal(typeof(AlertDialog), modal.Type);
        Assert.Equal(text, modal.Parameters!["Text"]);
    }

    [Fact]
    public void TheGameFolderEntryOpensItsWindow()
    {
        Services.GetRequiredService<HostMode>().Launcher = true;
        Render<SettingsMenu>().Find("#game-folder").Click();
        Assert.Equal(typeof(GameFolderModal), Services.GetRequiredService<ModalService>().Type);
    }

    [Fact]
    public void TheDisabledEntryTellsAScreenReaderItIsComingLater()
    {
        var item = Render<SettingsMenu>().Find("li[aria-disabled=true]");
        Assert.Contains("Coming later", item.QuerySelector(".sr")!.TextContent);
        Assert.Null(item.QuerySelector("a"));
    }
}
