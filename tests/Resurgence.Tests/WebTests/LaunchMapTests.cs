using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;
using Resurgence.Ui;
using Resurgence.Ui.Pages;

namespace Resurgence.Tests.WebTests;

public sealed class LaunchMapTests : UiContext
{
    [Fact]
    public void EveryReserveButtonSaysTrueOrFalseNeverAnEmptyAttribute()
    {
        var cut = Render<LaunchMap>(p => p.Add(m => m.Kind, 1));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[data-reserve]")));
        var states = cut.FindAll("button[data-reserve]").Select(b => b.GetAttribute("aria-pressed")).ToList();
        Assert.All(states, s => Assert.True(s is "true" or "false", s));
        Assert.Single(states, "true");
    }

    sealed class Recording : IServersService
    {
        public (int Reserve, int Marker)? Started;
        public Task<IReadOnlyList<Reserve>> ReservesAsync() => Task.FromResult<IReadOnlyList<Reserve>>([new(2, "Test Reserve")]);
        public Task<IReadOnlyList<MapMarker>> MarkersAsync(int reserve) =>
            Task.FromResult<IReadOnlyList<MapMarker>>([new(300, MarkerKind.Tower, "Tower 1", 50, 20), new(101, MarkerKind.Lodge, "Lodge 1", 10, 80), new(102, MarkerKind.Lodge, "Lodge 2", 90, 20)]);
        public Task<IReadOnlyList<GameServer>> ServersAsync() => Task.FromResult<IReadOnlyList<GameServer>>([]);
        public Task<IReadOnlyList<Tutorial>> TutorialsAsync() => Task.FromResult<IReadOnlyList<Tutorial>>([]);
        public Task<IReadOnlyList<Loadout>> LoadoutsAsync() => Task.FromResult<IReadOnlyList<Loadout>>([]);
        public Task<Result> StartSingleAsync(int reserve, int marker, int minutes, int loadout) { Started = (reserve, marker); return Task.FromResult(Result.Success); }
        public Task<Result> CreateAsync(NewGame game, int reserve, int marker, int minutes, int loadout) => Task.FromResult(Result.Fail("Launch.Failed"));
        public Task<Result> JoinAsync(int server, int marker, string? password, int loadout) => Task.FromResult(Result.Fail("Launch.Failed"));
        public Task<Result> StartTutorialAsync(int id) => Task.FromResult(Result.Success);
    }

    // the start ids are the server's: without a pick, the hunt starts at the first lodge of the reserve, not at a number of the mock
    [Fact]
    public void StartingWithoutAPickUsesTheFirstLodgeOfTheReserve()
    {
        var service = new Recording();
        Services.AddSingleton<IServersService>(service);
        var cut = Render<LaunchMap>(p => p.Add(m => m.Kind, 1));

        cut.WaitForAssertion(() => Assert.Null(cut.Find("button.go").GetAttribute("disabled")));
        cut.Find("button.go").Click();
        cut.WaitForAssertion(() => Assert.Equal((2, 101), service.Started));
    }

    sealed class Events(SeasonalEvents state) : ISeasonalEventsService
    {
        public Task<SeasonalEvents> GetAsync() => Task.FromResult(state);
        public Task<EventPanelData?> PanelAsync() => Task.FromResult<EventPanelData?>(null);
        public Task<Result> SetAsync(bool play) => Task.FromResult(Result.Success);
    }

    // the game's reserve 1 is the launch map's reserve 2
    [Theory]
    [InlineData(true, "halloween_2026", new[] { 0, 1, 2 }, true, true)]
    [InlineData(true, "halloween_2026", new int[0], true, true)]        // no reserves listed: every reserve
    [InlineData(false, "halloween_2026", new[] { 0, 1, 2 }, true, false)]  // the player opted out
    [InlineData(true, "halloween_2026", new[] { 8, 9 }, true, false)]    // not this reserve
    [InlineData(true, "halloween_2026", new[] { 0, 1, 2 }, false, false)] // the operator has no picture of the event
    [InlineData(true, null, new int[0], true, false)]                    // no event runs
    public void TheBadgeIsTheEventsPictureWhileItRunsForAPlayerWhoPlaysAndTheOperatorHasIt(bool play, string? name, int[] reserves, bool hasPicture, bool event_)
    {
        var pictures = new Dictionary<string, string> { ["reserve-02.png"] = "1" };
        if (hasPicture) pictures["reserve-02-halloween-2026.png"] = "1";
        Services.AddSingleton(new AssetImages(pictures));
        Services.AddSingleton<IServersService>(new Recording());
        Services.AddSingleton<ISeasonalEventsService>(new Events(new(play, name, reserves)));

        var cut = Render<LaunchMap>(p => p.Add(m => m.Kind, 1));

        cut.WaitForAssertion(() => Assert.Contains(event_ ? "/images/reserve-02-halloween-2026.png?" : "/images/reserve-02.png?", cut.Find("button[data-reserve='2'] img").GetAttribute("src")));
    }

    [Fact]
    public void WithoutAPictureTheBadgeSlotKeepsTheNormalName()
    {
        Services.AddSingleton<IServersService>(new Recording());
        Services.AddSingleton<ISeasonalEventsService>(new Events(new(true, "halloween_2026", [1])));  // no pictures at all

        var cut = Render<LaunchMap>(p => p.Add(m => m.Kind, 1));

        cut.WaitForAssertion(() => Assert.Equal("reserve-02", cut.Find("button[data-reserve='2'] [role=img]").GetAttribute("aria-label")));
    }
}
