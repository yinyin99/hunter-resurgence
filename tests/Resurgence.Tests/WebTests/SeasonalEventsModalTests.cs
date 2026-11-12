using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;
using Resurgence.Ui.Modals;
using Resurgence.Ui.Shell;

namespace Resurgence.Tests.WebTests;

public sealed class SeasonalEventsModalTests : UiContext
{
    sealed class Fake(bool play, bool saves = true) : ISeasonalEventsService
    {
        public List<bool> Set { get; } = [];
        public Task<SeasonalEvents> GetAsync() => Task.FromResult(new SeasonalEvents(play, null, []));

        public Task<Result> SetAsync(bool value)
        {
            Set.Add(value);
            return Task.FromResult(saves ? Result.Success : Result.Fail("Events.Failed"));
        }
    }

    [Fact]
    public void TheSettingsMenuOpensTheDialog()
    {
        var menu = Render<SettingsMenu>();
        menu.Find("#seasonal-events").Click();
        Assert.Equal(typeof(SeasonalEventsModal), Services.GetRequiredService<ModalService>().Type);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheBoxShowsTheStoredSetting(bool play)
    {
        Services.AddSingleton<ISeasonalEventsService>(new Fake(play));
        var cut = Render<SeasonalEventsModal>();
        cut.WaitForAssertion(() => Assert.Null(cut.Find("#play-events").GetAttribute("disabled")));
        Assert.Equal(play, cut.Find("#play-events").HasAttribute("checked"));
    }

    [Fact]
    public void ChangingTheBoxStoresTheSettingAndAFailureTurnsItBack()
    {
        var service = new Fake(play: true);
        Services.AddSingleton<ISeasonalEventsService>(service);
        var cut = Render<SeasonalEventsModal>();
        cut.WaitForAssertion(() => Assert.Null(cut.Find("#play-events").GetAttribute("disabled")));
        cut.Find("#play-events").Change(false);
        cut.WaitForAssertion(() => Assert.Equal([false], service.Set));
        Assert.False(cut.Find("#play-events").HasAttribute("checked"));
        Assert.Empty(cut.FindAll("[role=alert]"));
    }

    [Fact]
    public void ARefusedChangeSaysSoAndKeepsTheOldSetting()
    {
        var service = new Fake(play: true, saves: false);
        Services.AddSingleton<ISeasonalEventsService>(service);
        var cut = Render<SeasonalEventsModal>();
        cut.WaitForAssertion(() => Assert.Null(cut.Find("#play-events").GetAttribute("disabled")));
        cut.Find("#play-events").Change(false);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[role=alert]")));
        Assert.True(cut.Find("#play-events").HasAttribute("checked"));
    }
}
