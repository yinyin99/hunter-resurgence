using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;
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
}
