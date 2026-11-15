using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;
using Resurgence.Ui.Modals;
using Resurgence.Ui.Pages;

namespace Resurgence.Tests.WebTests;

/// <summary>The multiplayer screens against a service that answers like the server's: the games list, the host form of the launch map and a refused join. Invented players only.</summary>
public sealed class MultiplayerPagesTests : UiContext
{
    sealed class Fake : IServersService
    {
        public IReadOnlyList<GameServer> Games = [];
        public Func<string?, Result> Join = _ => Result.Success;
        public (NewGame Game, int Reserve, int Marker, int Minutes)? Hosted;
        public (long Server, int Marker, string? Password)? Joined;
        public Task<IReadOnlyList<Reserve>> ReservesAsync() => Task.FromResult<IReadOnlyList<Reserve>>([new(2, "Test Reserve"), new(4, "Other Reserve")]);
        public Task<IReadOnlyList<MapMarker>> MarkersAsync(int reserve) => Task.FromResult<IReadOnlyList<MapMarker>>([new(101, MarkerKind.Lodge, "Lodge 1", 10, 80), new(102, MarkerKind.Lodge, "Lodge 2", 90, 20)]);
        public Task<IReadOnlyList<GameServer>> ServersAsync() => Task.FromResult(Games);
        public Task<IReadOnlyList<Tutorial>> TutorialsAsync() => Task.FromResult<IReadOnlyList<Tutorial>>([]);
        public Task<IReadOnlyList<Loadout>> LoadoutsAsync() => Task.FromResult<IReadOnlyList<Loadout>>([]);
        public Task<Result> StartSingleAsync(int reserve, int marker, int minutes, int loadout) => Task.FromResult(Result.Success);
        public Task<Result> CreateAsync(NewGame game, int reserve, int marker, int minutes, int loadout) { Hosted = (game, reserve, marker, minutes); return Task.FromResult(Result.Success); }
        public Task<Result> JoinAsync(long server, int marker, string? password, int loadout) { Joined = (server, marker, password); return Task.FromResult(Join(password)); }
        public Task<Result> StartTutorialAsync(int id) => Task.FromResult(Result.Success);
    }

    // 10-digit ids: a network id does not fit an int
    static readonly GameServer Open = new(4000000001, "Test game", "Come and hunt.", 2, 2, 4, "HostOne", false, "", Distance.Short, 600, 100, 2000,
        [new("HostOne", 1500, null, null), new("Guest", 300, null, null)]);
    static readonly GameServer Locked = new(4000000002, "Locked game", "", 4, 1, 8, "HostTwo", true, "", Distance.Short, 60, 0, 99999, [new("HostTwo", 900, null, null)]);

    [Fact]
    public void TheListShowsEachGamesNameHostReservePlayersLockAndScoreRange()
    {
        Services.AddSingleton<IServersService>(new Fake { Games = [Open, Locked] });
        var cut = Render<Servers>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("table.tbl tbody tr").Count));
        var rows = cut.FindAll("table.tbl tbody tr");
        Assert.Equal(["Test game", "Test Reserve", "2/4", "HostOne"], rows[0].QuerySelectorAll("td").Take(4).Select(td => td.TextContent.Trim()));
        Assert.Empty(rows[0].QuerySelectorAll(".lock"));
        Assert.NotEmpty(rows[1].QuerySelectorAll(".lock"));
        // the games have no country: no location column, no dots, no text about them
        Assert.DoesNotContain("Country", cut.Find("table.tbl thead").TextContent);
        Assert.Empty(cut.FindAll("i.dot"));
        Assert.Empty(cut.FindAll("p.legend"));
        // the first game's details: its Hunter Score range and its players
        var preview = cut.Find("aside.preview").TextContent;
        Assert.Contains("Min Hunter Score: 100", preview);
        Assert.Contains("Max Hunter Score: 2000", preview);
        Assert.Contains("Guest", preview);
        // a game without a range shows none, a locked one says so
        cut.FindAll("table.tbl tbody tr")[1].Click();  // found again: the page redraws with the launcher state
        preview = cut.Find("aside.preview").TextContent;
        Assert.DoesNotContain("Min Hunter Score", preview);
        Assert.Contains("Password required", preview);
    }

    [Fact]
    public void WithoutGamesTheListOffersToHostOne()
    {
        Services.AddSingleton<IServersService>(new Fake());
        var cut = Render<Servers>();

        cut.WaitForAssertion(() => Assert.Equal("/launch-map/2", cut.Find(".empty a.btn").GetAttribute("href")));
    }

    [Fact]
    public void TheHostFormSendsWhatWasFilledInWithTheStartOfTheMap()
    {
        var fake = new Fake();
        Services.AddSingleton<IServersService>(fake);
        var cut = Render<LaunchMap>(p => p.Add(m => m.Kind, 2));

        cut.WaitForAssertion(() => Assert.Null(cut.Find(".start button.btn-primary").GetAttribute("disabled")));
        cut.Find("#game-name").Change("Test hunt");
        cut.Find("textarea[name=description]").Change("Quiet morning");
        cut.Find("input[name=publicGame]").Change(true);  // the first radio: friends only
        cut.Find("#game-password").Change("letmein");
        cut.Find("#limitHunterscore").Change(true);
        cut.Find("#hsMin").Change("100");
        cut.Find("#hsMax").Change("2000");
        cut.Find(".start button.btn-primary").Click();

        cut.WaitForAssertion(() => Assert.NotNull(fake.Hosted));
        var (game, reserve, marker, minutes) = fake.Hosted!.Value;
        Assert.Equal(new NewGame("Test hunt", "Quiet morning", true, "letmein", 8, 100, 2000), game);
        Assert.Equal((2, 101, 540), (reserve, marker, minutes));
    }

    [Fact]
    public void TheHostFormWithoutLimitOrPasswordSendsNone()
    {
        var fake = new Fake();
        Services.AddSingleton<IServersService>(fake);
        var cut = Render<LaunchMap>(p => p.Add(m => m.Kind, 2));

        cut.WaitForAssertion(() => Assert.Null(cut.Find(".start button.btn-primary").GetAttribute("disabled")));
        cut.Find(".start button.btn-primary").Click();

        cut.WaitForAssertion(() => Assert.NotNull(fake.Hosted));
        var game = fake.Hosted!.Value.Game;
        Assert.Equal((false, null, null, null), (game.FriendsOnly, game.Password, game.MinScore, game.MaxScore));
    }

    string? AlertText => Services.GetRequiredService<ModalService>() is { Type: var type, Parameters: var parameters } && type == typeof(AlertDialog) ? (string?)parameters!["Text"] : null;

    [Fact]
    public void AFullGamesRefusalIsShownAsAMessage()
    {
        var fake = new Fake { Join = _ => Result.Fail("Join.Full") };
        Services.AddSingleton<IServersService>(fake);
        var cut = Render<JoinMapModal>(p => p.Add(m => m.Server, Open));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button.marker")));
        cut.Find("button.marker").Click();

        cut.WaitForAssertion(() => Assert.Equal("This game is full. Please try another game.", AlertText));
        Assert.Equal((4000000001L, 101, (string?)null), fake.Joined);
    }

    [Fact]
    public void AWrongPasswordIsAskedAgainAndTheRightOneStartsTheGame()
    {
        var fake = new Fake { Join = password => password == "letmein" ? Result.Success : Result.Fail("Join.WrongPassword") };
        Services.AddSingleton<IServersService>(fake);
        var cut = Render<JoinMapModal>(p => p.Add(m => m.Server, Locked));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button.marker")));
        cut.Find("button.marker").Click();
        cut.Find("#join-password").Change("guess");
        cut.Find("form#mp-password-dialog").Submit();

        cut.WaitForAssertion(() => Assert.Equal("Wrong password. Please try again.", AlertText));
        Assert.Equal("guess", fake.Joined!.Value.Password);
        cut.Find("#join-password").Change("letmein");
        cut.Find("form#mp-password-dialog").Submit();

        cut.WaitForAssertion(() => Assert.Equal("letmein", fake.Joined!.Value.Password));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("form#mp-password-dialog")));
    }
}
