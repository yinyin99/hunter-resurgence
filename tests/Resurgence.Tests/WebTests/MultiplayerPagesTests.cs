using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Resurgence.Ui;
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
        /// <summary>The server's join map: the starts and the host's items, or none with the message key of why (<c>Join.WrongPassword</c> for a password not given or wrong); <see cref="Asked"/> are the passwords it was asked with.</summary>
        public Func<string?, JoinMap> Answer = _ => new(Map);
        public static readonly IReadOnlyList<MapMarker> Map = [new(101, MarkerKind.Lodge, "Lodge 1", 10, 80), new(102, MarkerKind.Lodge, "Lodge 2", 90, 20), new(-501, MarkerKind.Tent, "Host tent", 40, 40), new(-502, MarkerKind.TreeStand, "Host stand", 30, 30, true)];
        public List<string?> Asked = [];
        public Task<JoinMap> JoinMarkersAsync(long server, int reserve, string? password) { Asked.Add(password); return Task.FromResult(Answer(password)); }
        public Task<IReadOnlyList<Reserve>> ReservesAsync() => Task.FromResult<IReadOnlyList<Reserve>>([new(2, "Test Reserve"), new(4, "Other Reserve")]);
        public Task<IReadOnlyList<MapMarker>> MarkersAsync(int reserve) => Task.FromResult<IReadOnlyList<MapMarker>>([new(101, MarkerKind.Lodge, "Lodge 1", 10, 80), new(102, MarkerKind.Lodge, "Lodge 2", 90, 20)]);
        public Task<IReadOnlyList<GameServer>> ServersAsync() => Task.FromResult(Games);
        public Task<IReadOnlyList<Tutorial>> TutorialsAsync() => Task.FromResult<IReadOnlyList<Tutorial>>([]);
        public Task<IReadOnlyList<Loadout>> LoadoutsAsync() => Task.FromResult<IReadOnlyList<Loadout>>([]);
        public Task<Result> StartSingleAsync(int reserve, int marker, int minutes, int loadout) => Task.FromResult(Result.Success);
        /// <summary>When set, a create or join waits for it: the launch is "in flight".</summary>
        public TaskCompletionSource<Result>? Pending;
        public int Launches;
        public Task<Result> CreateAsync(NewGame game, int reserve, int marker, int minutes, int loadout) { Hosted = (game, reserve, marker, minutes); Launches++; return Pending?.Task ?? Task.FromResult(Result.Success); }
        public Task<Result> JoinAsync(long server, int marker, string? password, int loadout) { Joined = (server, marker, password); Launches++; return Pending?.Task ?? Task.FromResult(Join(password)); }
        public Task<Result> StartTutorialAsync(int id) => Task.FromResult(Result.Success);
    }

    // 10-digit ids: a network id does not fit an int
    static readonly GameServer Open = new(4000000001, "Test game", "Come and hunt.", 2, 2, 4, "HostOne", false, null, 600, 100, 2000,
        [new("HostOne", 1500, null, null), new("Guest", 300, null, null)]);
    static readonly GameServer Locked = new(4000000002, "Locked game", "", 4, 1, 8, "HostTwo", true, null, 60, 0, 99999, [new("HostTwo", 900, null, null)]);

    static GameServer At(GameServer g, string name, Distance? distance) => g with { Name = name, Distance = distance };

    [Fact]
    public void TheDotsTheLegendAndTheCreditShowWhenAGameHasADistanceAndUnknownOnesComeLast()
    {
        // the server's order is oldest first; the default order is the nearest first, the games it cannot place after the rest
        Services.AddSingleton<IServersService>(new Fake { Games = [At(Open, "Unknown", null), At(Open, "Far", Distance.Long), At(Open, "Near", Distance.Short), At(Open, "Middle", Distance.Medium), At(Open, "Unknown too", null)] });
        var cut = Render<Servers>();

        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll("table.tbl tbody tr").Count));
        var rows = cut.FindAll("table.tbl tbody tr");
        Assert.Equal(["Near", "Middle", "Far", "Unknown", "Unknown too"], rows.Select(r => r.QuerySelector("td.name")!.TextContent.Trim()));
        Assert.Equal(["short", "medium", "long", "", ""], rows.Select(r => string.Join(" ", r.QuerySelectorAll("i.dot").Select(d => d.ClassName!.Replace("dot", "").Trim()))));
        Assert.Single(cut.FindAll("p.legend"));
        Assert.Equal(3, cut.Find("p.legend").QuerySelectorAll("i.dot").Length);
        var credit = cut.Find("p.attribution a");
        Assert.Equal(("IP Geolocation by DB-IP", "https://db-ip.com"), (credit.TextContent, credit.GetAttribute("href")));
        Assert.Contains("legend", cut.Markup.Substring(0, cut.Markup.IndexOf("attribution", StringComparison.Ordinal)));  // the credit is under the legend
    }

    [Fact]
    public void QuickJoinShowsNoDotBecauseItHasNeitherTheLegendNorTheCredit()
    {
        Services.AddSingleton<IServersService>(new Fake { Games = [At(Open, "Near", Distance.Short), At(Open, "Far", Distance.Long)] });
        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".servers li").Count));
        Assert.Empty(cut.FindAll("i.dot"));
        Assert.Empty(cut.FindAll("p.attribution"));
    }

    [Fact]
    public void AnEmptyListHasNoLegendAndNoCredit()
    {
        Services.AddSingleton<IServersService>(new Fake { Games = [] });
        var cut = Render<Servers>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".empty")));
        Assert.Empty(cut.FindAll("p.legend"));
        Assert.Empty(cut.FindAll("p.attribution"));
    }

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
        // the games have no distance: no dots, no text about them (and no country column: the language column is there)
        Assert.DoesNotContain("Country", cut.Find("table.tbl thead").TextContent);
        Assert.Empty(cut.FindAll("i.dot"));
        Assert.Empty(cut.FindAll("p.legend"));
        Assert.Empty(cut.FindAll("p.attribution"));  // and no credit for a database that was not used
        // the first game's details: its HunterScore range and its players
        var preview = cut.Find("aside.preview").TextContent;
        Assert.Contains("Min HunterScore: 100", preview);
        Assert.Contains("Max HunterScore: 2000", preview);
        Assert.Contains("Guest", preview);
        // a game without a range shows none, a locked one says so
        cut.FindAll("table.tbl tbody tr")[1].Click();  // found again: the page redraws with the launcher state
        preview = cut.Find("aside.preview").TextContent;
        Assert.DoesNotContain("Min HunterScore", preview);
        Assert.Contains("Password required", preview);
    }

    [Fact]
    public void TheFilterListsOnlyGamesThePlayerMayJoinAndTheOthersShowTheirReasonAndNoJoin()
    {
        Services.AddSingleton<IPatchService>(new Resurgence.Services.Mocks.MockPatchService("patch:done"));  // the Join button is off while patching
        GameServer[] games = [Open, Locked, .. new[] { "friends", "hunterscore", "full", "own", "starting", "banned" }.Select((why, i) => Open with { Id = i + 1, Name = $"Game {why}", Why = why })];
        Services.AddSingleton<IServersService>(new Fake { Games = games });
        var cut = Render<Servers>();

        cut.WaitForAssertion(() => Assert.Equal(8, cut.FindAll("table.tbl tbody tr").Count));
        string[] reasons = [.. cut.FindAll("table.tbl tbody tr").Select(r => string.Join("", r.QuerySelectorAll(".why").Select(w => w.TextContent)))];
        Assert.Equal(["", "", "Friends only", "HunterScore 100–2000", "Full", "Your game", "Starting…", "Banned by the host"], reasons);  // a game with a password has its lock, not a reason

        string Join(int row)
        {
            cut.FindAll("table.tbl tbody tr")[row].Click();
            return cut.Find("aside.preview .btn-block").GetAttribute("disabled") is null ? "on" : "off";
        }
        Assert.Equal(["on", "on", "off", "off", "off", "off", "off", "off"], Enumerable.Range(0, 8).Select(Join));  // a password game asks for its password: its Join stays on
        cut.Find("aside.preview .refusal");  // the details say it too

        cut.Find(".tools input.tick").Change(true);
        cut.WaitForAssertion(() => Assert.Equal(["Test game"], cut.FindAll("table.tbl tbody tr td.name").Select(td => td.TextContent.Trim())));
    }

    [Fact]
    public void ThePreviewDrawsTheGamesPlayersOnAMapAboveTheirListAndAGameWithoutPositionsHasNoMap()
    {
        var placed = Open with { Roster = [new("HostOne", 1500, 25.5, 75), new("Guest", 300, null, null)] };
        Services.AddSingleton<IServersService>(new Fake { Games = [placed, Locked with { Roster = [new("HostTwo", 900, null, null)] }] });
        var cut = Render<Servers>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("table.tbl tbody tr").Count));
        Assert.Equal(["left:25.5%;top:75%"], cut.FindAll("aside.preview .preview-map span.hunter").Select(h => h.GetAttribute("style")));  // the player without a position is listed, not drawn
        Assert.Equal(2, cut.FindAll("aside.preview .player").Count);
        Assert.Empty(cut.FindAll("aside.preview .preview-map span.hunter-label"));
        cut.FindAll("aside.preview .player")[0].MouseEnter();  // hovering a player labels its marker
        Assert.Contains("HostOne", cut.Find("aside.preview .preview-map span.hunter-label").TextContent);

        cut.FindAll("table.tbl tbody tr")[1].Click();
        Assert.Empty(cut.FindAll("aside.preview .preview-map"));
    }

    [Fact]
    public void ThePlayersWithAPositionAreDrawnOnTheJoinMapAndTheOthersAreOnlyListed()
    {
        Services.AddSingleton<IServersService>(new Fake());
        var game = Open with { Roster = [new("HostOne", 1500, 25.5, 75), new("Guest", 300, null, null)] };
        var cut = Render<JoinMapModal>(p => p.Add(m => m.Server, game));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button.marker")));
        Assert.Equal(["left:25.5%;top:75%"], cut.FindAll("span.hunter").Select(h => h.GetAttribute("style")));
        Assert.Equal(2, cut.FindAll("aside .player").Count);
        cut.FindAll("aside .player")[0].MouseEnter();
        Assert.Contains("HostOne", cut.Find("span.hunter-label").TextContent);
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
        using var _ = new Culture("fr");
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
        Assert.Equal(new NewGame("Test hunt", "Quiet morning", true, "letmein", 8, 100, 2000, "fr"), game);  // the launcher's language, set for this test, is the game's
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

    string? AlertText
    {
        get
        {
            var modal = Services.GetRequiredService<ModalService>();
            if (modal.Type == typeof(StartFailedDialog)) return Services.GetRequiredService<IStringLocalizer<Strings>>()[(string)modal.Parameters!["Error"]!].Value;
            return modal.Type == typeof(AlertDialog) ? (string?)modal.Parameters!["Text"] : null;
        }
    }

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
    public void AGameWithAPasswordShowsNoMapUntilItsPasswordIsEnteredAndThenTheStartJoinsWithIt()
    {
        var fake = new Fake { Answer = password => password == "letmein" ? new(Fake.Map) : new(null, "Join.WrongPassword") };
        Services.AddSingleton<IServersService>(fake);
        var cut = Render<JoinMapModal>(p => p.Add(m => m.Server, Locked with { Roster = [new("HostTwo", 900, 20, 30)] }));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("form#mp-password-dialog")));
        Assert.Empty(fake.Asked);  // the server is not asked for the map before the player has a password to give
        Assert.Empty(cut.FindAll("div.map"));
        Assert.Empty(cut.FindAll("button.marker"));
        Assert.Empty(cut.FindAll("span.hunter"));
        cut.Find("#join-password").Change("guess");
        cut.Find("form#mp-password-dialog").Submit();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("p.wrong")));
        Assert.Contains("Wrong password", cut.Find("p.wrong").TextContent);
        Assert.Empty(cut.FindAll("button.marker"));
        cut.Find("#join-password").Change("letmein");
        cut.Find("form#mp-password-dialog").Submit();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("form#mp-password-dialog")));
        Assert.Equal(["guess", "letmein"], fake.Asked);
        Assert.Equal(4, cut.FindAll("button.marker").Count);
        Assert.Single(cut.FindAll("span.hunter"));
        cut.Find("button.marker").Click();
        cut.WaitForAssertion(() => Assert.Equal((4000000002L, 101, "letmein"), fake.Joined));
    }

    [Fact]
    public void AMapRefusedForAnotherReasonThanThePasswordSaysThatReasonAndNoAnswerIsTheGenericMessage()
    {
        var fake = new Fake { Answer = password => password == "full" ? new(null, "Join.Full") : password == "net" ? new(null, "Join.HostNetwork") : new(null, "Launch.Failed") };
        Services.AddSingleton<IServersService>(fake);
        var cut = Render<JoinMapModal>(p => p.Add(m => m.Server, Locked with { Roster = [new("HostTwo", 900, 20, 30)] }));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("form#mp-password-dialog")));
        Assert.Empty(cut.FindAll("p.wrong"));  // nothing was asked: no message yet
        string Try(string password)
        {
            cut.Find("#join-password").Change(password);
            cut.Find("form#mp-password-dialog").Submit();
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("p.wrong")));
            return cut.Find("p.wrong").TextContent;
        }
        Assert.Contains("full", Try("full"));
        Assert.DoesNotContain("Wrong password", cut.Find("p.wrong").TextContent);
        Assert.Contains("IPv6", Try("net"));
        Assert.Contains("Something went wrong", Try("any"));
        Assert.Empty(cut.FindAll("button.marker"));
    }

    [Fact]
    public void AGameThePlayerMayNotJoinShowsNoMapNoSetupAndNoPlayersOnIt()
    {
        var fake = new Fake();
        Services.AddSingleton<IServersService>(fake);
        var cut = Render<JoinMapModal>(p => p.Add(m => m.Server, Open with { Why = "friends", Roster = [new("HostOne", 1500, 25.5, 75)] }));

        cut.WaitForAssertion(() => Assert.Contains("only shown to players who may join", cut.Find("div.nomap").TextContent));
        Assert.Empty(fake.Asked);
        Assert.Empty(cut.FindAll("button.marker"));
        Assert.Empty(cut.FindAll("span.hunter"));
        Assert.Empty(cut.FindAll("form#mp-password-dialog"));
    }

    [Fact]
    public void TheHostsTentIsAStartOnTheJoinMapAndItsStandIsOnlyShown()
    {
        var fake = new Fake();
        Services.AddSingleton<IServersService>(fake);
        var cut = Render<JoinMapModal>(p => p.Add(m => m.Server, Open));

        cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll("button.marker").Count));
        Assert.Equal([null], fake.Asked);  // a game without a password: asked once, with none
        Assert.Equal(["Lodge 1:on", "Lodge 2:on", "Host tent:on", "Host stand:off"], cut.FindAll("button.marker").Select(m => $"{m.GetAttribute("aria-label")}:{(m.GetAttribute("disabled") is null ? "on" : "off")}"));
    }

    [Fact]
    public void OnlyAGameThatRunsTheSeasonalEventShowsItsBadgeInTheListAndItsDetails()
    {
        // the first game's server sent no event fields (an older server): it renders as before
        Services.AddSingleton<IServersService>(new Fake { Games = [Open, Locked with { Event = "halloween_2026", EventDiffers = true }] });
        var cut = Render<Servers>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("table.tbl tbody tr").Count));
        var rows = cut.FindAll("table.tbl tbody tr");
        Assert.Empty(rows[0].QuerySelectorAll("small.event"));
        Assert.Equal("Test game", rows[0].QuerySelector("td.name")!.TextContent.Trim());
        Assert.Contains("Seasonal event", rows[1].QuerySelector("td.name small.event")!.TextContent);
        Assert.DoesNotContain("Seasonal event", cut.Find("aside.preview").TextContent);
        cut.FindAll("table.tbl tbody tr")[1].Click();
        Assert.Contains("Seasonal event", cut.Find("aside.preview").TextContent);
    }

    [Theory]
    [InlineData("halloween_2026", true, "This game is played with the seasonal event.", true)]
    [InlineData("halloween_2026", false, "This game is played with the seasonal event.", false)]
    [InlineData(null, true, "This game is played without the seasonal event.", true)]
    [InlineData(null, false, null, false)]
    public void TheJoinDialogSaysWhetherTheGameRunsTheSeasonalEventAndThatTheHostsSettingAppliesOnlyWhenItDiffers(string? gameEvent, bool differs, string? sentence, bool hostNote)
    {
        var fake = new Fake();
        Services.AddSingleton<IServersService>(fake);
        var cut = Render<JoinMapModal>(p => p.Add(m => m.Server, Open with { Event = gameEvent, EventDiffers = differs }));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button.marker")));
        if (sentence is null) Assert.Empty(cut.FindAll("p.event"));  // no event and no difference (an older server too): nothing is said
        else Assert.StartsWith(sentence, cut.Find("p.event").TextContent);
        string[] note = hostNote ? ["In multiplayer, the host's setting applies."] : [];
        Assert.Equal(note, cut.FindAll("p.event small").Select(s => s.TextContent));
        // no refusal and no extra click: a start of the map still joins
        cut.Find("button.marker").Click();
        cut.WaitForAssertion(() => Assert.NotNull(fake.Joined));
    }

    [Fact]
    public void ThePreviewOfAGameThePlayerMayNotJoinDrawsNoPlayersOnAMap()
    {
        GamePlayer[] placed = [new("HostOne", 1500, 25.5, 75)];
        Services.AddSingleton<IServersService>(new Fake { Games = [Open with { Why = "hunterscore", Roster = placed }] });
        var cut = Render<Servers>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("table.tbl tbody tr")));
        Assert.Empty(cut.FindAll("aside.preview .preview-map"));
        Assert.Single(cut.FindAll("aside.preview .player"));  // listed, not placed
    }

    [Fact]
    public void TwoQuickClicksOnStartSendOneLaunchAndAFailedStartEnablesTheButtonAgain()
    {
        var fake = new Fake { Pending = new() };
        Services.AddSingleton<IServersService>(fake);
        var cut = Render<LaunchMap>(p => p.Add(m => m.Kind, 2));

        cut.WaitForAssertion(() => Assert.Null(cut.Find(".start button.btn-primary").GetAttribute("disabled")));
        cut.Find(".start button.btn-primary").Click();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".start button.btn-primary").GetAttribute("disabled")));
        cut.Find(".start button.btn-primary").Click();
        Assert.Equal(1, fake.Launches);

        fake.Pending.SetResult(Result.Fail("Launch.Failed"));
        cut.WaitForAssertion(() => Assert.Null(cut.Find(".start button.btn-primary").GetAttribute("disabled")));
        fake.Pending = null;
        cut.Find(".start button.btn-primary").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, fake.Launches));
    }

    [Fact]
    public void TwoQuickClicksOnAMapMarkerSendOneJoin()
    {
        var fake = new Fake { Pending = new() };
        Services.AddSingleton<IServersService>(fake);
        var cut = Render<JoinMapModal>(p => p.Add(m => m.Server, Open));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button.marker")));
        cut.Find("button.marker").Click();
        cut.Find("button.marker").Click();
        Assert.Equal(1, fake.Launches);

        fake.Pending.SetResult(Result.Fail("Join.Full"));
        cut.WaitForAssertion(() => Assert.Equal("This game is full. Please try another game.", AlertText));
        fake.Pending = null;
        cut.Find("button.marker").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, fake.Launches));
    }

    /// <summary>The launcher's current language for the length of a test: the pages read the thread's UI culture, so a test never depends on the PC's.</summary>
    sealed class Culture : IDisposable
    {
        readonly System.Globalization.CultureInfo old = System.Globalization.CultureInfo.CurrentUICulture;
        public Culture(string tag) => System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(tag);
        public void Dispose() => System.Globalization.CultureInfo.CurrentUICulture = old;
    }

    /// <summary>Opens the host form and hosts at once: the language the list shows, and the code the start sent.</summary>
    (string Shown, string Sent) HostWith(Fake fake)
    {
        fake.Hosted = null;
        var cut = Render<LaunchMap>(p => p.Add(m => m.Kind, 2));
        cut.WaitForAssertion(() => Assert.Null(cut.Find(".start button.btn-primary").GetAttribute("disabled")));
        var shown = cut.Find("#game-language").TextContent.Trim();
        cut.Find(".start button.btn-primary").Click();
        cut.WaitForAssertion(() => Assert.NotNull(fake.Hosted));
        return (shown, fake.Hosted!.Value.Game.Language);
    }

    // #930: the first time the host form opens with the launcher's language selected (a region or a language the launcher lacks falls to its nearest or English)
    [Theory]
    [InlineData("fr", "Français", "fr")]
    [InlineData("pt-BR", "Português (Brasil)", "pt-BR")]
    [InlineData("de-AT", "Deutsch", "de")]
    [InlineData("ja", "English", "en")]
    public void TheHostFormOpensWithTheLaunchersLanguage(string culture, string shown, string code)
    {
        using var _ = new Culture(culture);
        var fake = new Fake();
        Services.AddSingleton<IServersService>(fake);
        Assert.Equal((shown, code), HostWith(fake));
    }

    [Fact]
    public async Task TheHostFormTheNextTimeOpensWithTheLanguageChosenLastAndTheListOffersTheNineteen()
    {
        using var _ = new Culture("fr");
        var fake = new Fake();
        Services.AddSingleton<IServersService>(fake);
        var cut = Render<LaunchMap>(p => p.Add(m => m.Kind, 2));
        cut.WaitForAssertion(() => Assert.Null(cut.Find(".start button.btn-primary").GetAttribute("disabled")));
        Assert.Equal(Languages.All.Select(l => l.Name), cut.FindAll("#game-language + ul li button").Select(b => b.TextContent.Trim()));
        Assert.Equal(19, cut.FindAll("#game-language + ul li button img.flag").Count);
        cut.FindAll("#game-language + ul li button").Single(b => b.TextContent.Trim() == "Deutsch").Click();  // not the launcher's language
        Assert.Equal("Deutsch", cut.Find("#game-language").TextContent.Trim());
        cut.Find(".start button.btn-primary").Click();
        cut.WaitForAssertion(() => Assert.NotNull(fake.Hosted));
        Assert.Equal("de", fake.Hosted!.Value.Game.Language);
        Assert.Equal("de", await Services.GetRequiredService<IGameSettingsService>().HostLanguageAsync());  // saved with the start

        // the next form: the saved choice, whatever the launcher's language is
        Assert.Equal(("Deutsch", "de"), HostWith(fake));
    }

    [Fact]
    public async Task AHostFormOpensWithTheSavedChoiceOverTheLaunchersLanguageAndAnUnknownCodeIsNoChoice()
    {
        using var _ = new Culture("fr");
        var fake = new Fake();
        Services.AddSingleton<IServersService>(fake);
        var settings = Services.GetRequiredService<IGameSettingsService>();
        await settings.SetHostLanguageAsync("sv");
        Assert.Equal(("Svenska", "sv"), HostWith(fake));
        await settings.SetHostLanguageAsync("xx");  // not one of the 19: the launcher's language again
        Assert.Equal(("Français", "fr"), HostWith(fake));
    }

    static readonly GameServer[] InLanguages =
    [
        Open with { Id = 11, Name = "French game", Language = "fr" }, Open with { Id = 12, Name = "German game", Language = "de" }, Open with { Id = 13, Name = "Unknown game" },
        Open with { Id = 14, Name = "Brazil game", Language = "pt-BR" }, Open with { Id = 15, Name = "English game", Language = "en" },
    ];

    IRenderedComponent<Servers> ListInLanguages()
    {
        Services.AddSingleton<IServersService>(new Fake { Games = InLanguages });
        var cut = Render<Servers>();
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll("table.tbl tbody tr").Count));
        return cut;
    }

    static string[] Names(IRenderedComponent<Servers> cut) => [.. cut.FindAll("table.tbl tbody tr td.name").Select(td => td.TextContent.Trim())];

    // #930: a column with the host's flag and its name as the tip (none for a game without a language), and the details say "Language: <name>"
    [Fact]
    public void TheListShowsEachGamesLanguageAsAFlagWithItsNameAndTheDetailsNameIt()
    {
        using var _ = new Culture("en");
        var cut = ListInLanguages();

        Assert.Equal(["Language"], cut.FindAll("th.lang").Select(th => th.TextContent.Trim()));
        var cells = cut.FindAll("table.tbl tbody tr td.lang");
        Assert.Equal(["fr", "de", null, "br", "en"], cells.Select(td => td.QuerySelector("img.flag")?.GetAttribute("src")?.Split('/').Last().Replace(".svg", "")));
        Assert.Equal(["Français", "Deutsch", null, "Português (Brasil)", "English"], cells.Select(td => td.QuerySelector("img.flag")?.GetAttribute("data-tip")));
        Assert.Contains("Language: Français", cut.Find("aside.preview").TextContent);  // the first game is selected
        cut.FindAll("table.tbl tbody tr")[2].Click();  // a game without one says nothing of it
        Assert.DoesNotContain("Language:", cut.Find("aside.preview").TextContent);
    }

    [Fact]
    public void TheListSortsByLanguageName()
    {
        using var _ = new Culture("en");
        var cut = ListInLanguages();

        cut.Find("th.lang a").Click();
        Assert.Equal(["German game", "English game", "French game", "Brazil game", "Unknown game"], Names(cut));  // Deutsch, English, Français, Português, none last
        Assert.Equal("ascending", cut.Find("th.lang").GetAttribute("aria-sort"));
        cut.Find("th.lang a").Click();
        Assert.Equal(["Brazil game", "French game", "English game", "German game", "Unknown game"], Names(cut));  // none last again
    }

    [Fact]
    public void TheSearchMatchesTheLanguageNameAndCode()
    {
        using var _ = new Culture("en");
        var cut = ListInLanguages();

        cut.Find("input[type=search]").Input("deutsch");
        Assert.Equal(["German game"], Names(cut));
        cut.Find("input[type=search]").Input("pt-br");  // the code, though the name is Português (Brasil)
        Assert.Equal(["Brazil game"], Names(cut));
        cut.Find("input[type=search]").Input("french fr");  // every word: the name of the game and the code
        Assert.Equal(["French game"], Names(cut));
    }

    // #930: "only games in my language" keeps the games of the launcher's current language (a game without one is not known to be)
    [Theory]
    [InlineData("fr", "French game")]
    [InlineData("pt-BR", "Brazil game")]
    [InlineData("de-AT", "German game")]
    [InlineData("ja", "English game")]  // a language the launcher lacks reads as English
    public void TheFilterKeepsOnlyTheGamesInTheLaunchersLanguage(string culture, string expected)
    {
        using var _ = new Culture(culture);
        var cut = ListInLanguages();

        cut.Find("#onlyMyLanguage").Change(true);
        Assert.Equal([expected], Names(cut));
        cut.Find("#onlyMyLanguage").Change(false);
        Assert.Equal(5, Names(cut).Length);
    }
}
