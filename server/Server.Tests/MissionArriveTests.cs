using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>An arrive objective is met by a place entered (event 101) that the title names, or that lies within 200 m of the title's coordinates in the mission's reserve, on invented missions and places.</summary>
public class MissionArriveTests : MissionTestBase
{
    const string Zorbel = "Then arrive at the Zorbel (x -4633,y -11152)";

    /// <summary>The player enters a place at a position (metres, the map's x and z).</summary>
    static JsonNode EnterAt(string place, int x, int z) =>
        O($$"""{"event": 101, "location_name": "{{place}}", "direction": "enter", "player_x": {{x}}, "player_y": 2, "player_z": {{z}}}""");

    [Theory]
    [InlineData("Then arrive at the Zorbel", "TESTLAND_FROZEN_ZORBEL")]
    [InlineData("Then arrive at the Zorbel", ProtocolLiterals.LocationPrefix + "TESTLAND_ZORBEL")]
    [InlineData("Then arrive at Quillon Fall", "TESTLAND_QUILLON_FALL")]
    [InlineData("Then arrive at Quillon Fall", ProtocolLiterals.LocationPrefix + "TESTLAND_QUILLON_FALLS")]
    [InlineData("Arrive at the Test Pond", "TEST_RESERVE_TEST_POND")]
    public void TestAPlaceTheGameNamesWithOtherWordsMeetsTheObjective(string title, string place)
    {
        state = New("names.db", Catalogue([title]));
        ActivateAll();
        Hb(Enter(place, "leave"));
        Assert.Equal("", Completed());
        Hb(Enter(place));  // the fixture's position is far from the title's point: the name decides
        Assert.Equal("1", Completed());
    }

    [Theory]
    [InlineData("Arrive at the Old Tower", "TESTLAND_NORTH_TOWER")]
    [InlineData("Arrive at the Grand Lodge", "TESTLAND_FROZEN_LODGE")]
    [InlineData("Arrive at the Old Lodge", "TESTLAND_LODGE")]
    [InlineData("Arrive at the Zorbel", "TESTLAND_FROZEN_ZORBELS_OF_DOOM")]
    [InlineData("Arrive at Zorbel Hills", "TESTLAND_WINDY_HILLS")]
    public void TestACommonWordOrAnotherPlaceAloneDoesNotMeetTheObjective(string title, string place)
    {
        state = New("common.db", Catalogue([title]));
        ActivateAll();
        Hb(Enter(place));
        Assert.Equal("", Completed());
    }

    [Theory]
    [InlineData(-4483, -11152, 2, true)]  // 150 m away
    [InlineData(-4633, -10952, 2, true)]  // 200 m away
    [InlineData(-4233, -11152, 2, false)]  // 400 m away
    [InlineData(-4483, -11152, 3, false)]  // the same numbers in another reserve
    public void TestAPlaceNearTheTitlesCoordinatesInTheMissionsReserveMeetsTheObjectiveWhateverItsName(int x, int z, int reserve, bool met)
    {
        state = New($"near-{x}-{z}-{reserve}.db", Catalogue([Zorbel], ", \"reserves\": [2]"));
        ActivateAll();
        Launch(reserve);
        Hb(EnterAt("TESTLAND_UNRELATED_PLACE", x, z));
        Assert.Equal(met ? "1" : "", Completed());
    }

    [Fact]
    public void TestATitleWithCoordinatesIsMetNearThemOnlyNotByALooselyNamedPlaceFarAway()
    {
        state = New("far.db", Catalogue([Zorbel]));
        ActivateAll();
        Hb(EnterAt("TESTLAND_FROZEN_ZORBEL", -1000, -2000));
        Assert.Equal("", Completed());
        Hb(EnterAt("TESTLAND_FROZEN_ZORBEL", -4600, -11100));
        Assert.Equal("1", Completed());
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]  // the same name in a reserve the mission does not list
    public void TestAPlaceNamedLooselyMeetsTheObjectiveOnlyInTheMissionsReserve(int reserve, bool met)
    {
        state = New($"reserve-{reserve}.db", Catalogue(["Then arrive at the Zorbel"], ", \"reserves\": [2]"));
        ActivateAll();
        Launch(reserve);
        Hb(Enter("TESTLAND_FROZEN_ZORBEL"));
        Assert.Equal(met ? "1" : "", Completed());
    }

    [Theory]
    [InlineData("Arrive at the Zorbel (-4633, y -11152)")]
    [InlineData("Arrive at the Zorbel, at Tower 96, x -4633, y -11152")]
    [InlineData("Arrive at the Zorbel (x: -4.633, y: -11.152)")]  // a fraction below 100 is in thousands of metres
    public void TestTheCoordinatesOfATitleMayBeWrittenInOtherWays(string title)
    {
        state = New("forms.db", Catalogue([title]));
        ActivateAll();
        Hb(EnterAt("TESTLAND_UNRELATED_PLACE", -4500, -11100));
        Assert.Equal("1", Completed());
    }

    [Fact]
    public void TestAnOrderedArriveWaitsForTheObjectiveBeforeItWhateverMeetsIt()
    {
        state = New("ordered.db", """
            {"missions": [{"id": 1, "title": "Two", "dependencies": [], "rewards": [{"text": "10 gm$"}], "reserves": [2], "objectives": [
              {"id": 0, "title": "Harvest a Test Deer"}, {"id": 1, "title": "Then arrive at the Zorbel (x -4633,y -11152)"}]}]}
            """);
        ActivateAll();
        Launch();
        Hb(EnterAt("TESTLAND_UNRELATED_PLACE", -4633, -11152));
        Assert.Equal("", Completed());
        Hb(Shot(1));
        Hb(EnterAt("TESTLAND_UNRELATED_PLACE", -4633, -11152));
        Assert.Equal("1", Completed());
    }

    static string Missions(string place) => $$"""
        {"missions": [
          {"id": 1, "title": "Two", "dependencies": [], "rewards": [{"text": "10 gm$"}], "objectives": [
            {"id": 0, "title": "Harvest a Test Deer"}, {"id": 1, "title": "Then arrive at the {{place}}"}]},
          {"id": 2, "title": "Waits", "dependencies": [], "rewards": [{"text": "10 gm$"}], "objectives": [
            {"id": 0, "title": "Harvest a Test Deer with the Test Shotgun"}, {"id": 1, "title": "Then arrive at the {{place}}"}]}]}
        """;

    [Fact]
    public void TestAnEnterStoredBeforeTheFixFinishesTheObjectiveAtTheNextStartOnlyWhenTheOneBeforeItIsFinished()
    {
        state = New("stored.db", Missions("Gully"));
        ActivateAll();
        Hb(Shot(1));  // mission 1's harvest is done; mission 2's shotgun harvest is not
        Hb(Enter("TESTLAND_FROZEN_ZORBEL"));  // not the place the titles name yet
        var wallet = Gm();
        Assert.Equal("", Completed());
        var later = New("stored.db", Missions("Zorbel"));
        Assert.Equal(1, later.ConfirmMissions());  // mission 2's arrive waits for its harvest
        Assert.Equal(("1", wallet + 10), (Completed(of: later), Gm(later)));
        Assert.Equal(0, later.ConfirmMissions());
    }

    [Fact]
    public void TestAnOverriddenArriveStaysExactAndTheCoverageReportsTheArriveObjectivesNoCataloguePlaceMeetsAsNotUnderstood()
    {
        state = New("coverage-arrive.db", Catalogue(["Arrive at the Test Lodge", "Arrive at the Mystery Spot", "Arrive at the Mystery Spot (x 5000, y 6000)", "Info message when you are south of the Test Pond"]),
            rules: new JsonObject { ["4/0"] = "arrive; place: TEST_RESERVE_SOUTH_OF_POND2" });
        var report = state.MissionCoverage(rules: true).ToList();
        Assert.Contains("understood: 3, not understood: 1 (never met until a version reads them)", report);
        Assert.Contains("mission 2 objective 0: Arrive at the Mystery Spot (no place of the catalogue matches it)", report);
        Assert.Contains("mission 2 objective 0: Arrive at the Mystery Spot => arrive; not understood, left over: arrive at the mystery spot", report);
        Assert.Contains("mission 1 objective 0: Arrive at the Test Lodge => arrive; places: TEST_RESERVE_LODGE", report);
        ActivateAll();
        Hb(Enter("TEST_RESERVE_SOUTH_OF_POND2_EXTRA"), Enter(ProtocolLiterals.LocationPrefix + "TEST_RESERVE_SOUTH_OF_POND2"));
        Assert.Equal("", Completed());  // an operator's place is met by that name only
    }

    [Theory]
    [InlineData("Then arrive at the Upper Test Camp", "TEST_RESERVE_CAMPSITE_0", true)]
    [InlineData("Then arrive at the Upper Test Camp", "TEST_RESERVE_CAMPSITE_1", false)]  // the shorter name is not named by the longer one
    [InlineData("Visit the Test Camp.", "TEST_RESERVE_CAMPSITE_1", true)]
    [InlineData("Visit the Test Camp.", "TEST_RESERVE_CAMPSITE_0", false)]
    [InlineData("Visit the Test Camp.", "TEST_SECOND_CAMPSITE_0", false)]  // the second reserve's: the mission lists the first
    [InlineData("Arrive at Zelan's Cairn", "TEST_RESERVE_CAIRN_2", true)]  // the catalogue's name has an accent
    [InlineData("Arrive at the Zurnhutte", "TEST_RESERVE_ZUERNHUETTE", true)]
    [InlineData("Arrive at the Zürnhütte Lodge", "TEST_RESERVE_ZUERNHUETTE", true)]
    [InlineData("Arrive at the Cairn", "TEST_RESERVE_CAIRN_2", false)]  // only part of the name
    [InlineData("Visit the Test Camp Watchtower.", "TEST_RESERVE_CAMPSITE_1", false)]  // a place the catalogue lacks, whose name holds a catalogued one: not the neighbouring place
    [InlineData("Arrive at the Upper Test Camp (x 5000, y 6000)", "TEST_RESERVE_CAMPSITE_0", false)]  // a title with coordinates is met near them, not by a name
    public void TestAPlaceTheTitleNamesByItsEnglishNameIsMetByTheDefineTheGameSends(string title, string define, bool met)
    {
        state = New($"named-{Math.Abs((title + define).GetHashCode())}.db", Catalogue([title], ", \"reserves\": [2]"));
        ActivateAll();
        Hb(Enter(define));  // the fixture's position is far from any point: the name decides
        Assert.Equal(met ? "1" : "", Completed());
    }

    [Fact]
    public void TestACampsiteOfAnotherReserveIsMetWhenTheMissionListsThatReserveOrNone()
    {
        state = New("campsites.db", Catalogue(["Visit the Test Camp."], ", \"reserves\": [3]"));
        ActivateAll();
        Hb(Enter("TEST_RESERVE_CAMPSITE_1"));
        Assert.Equal("", Completed());
        Hb(Enter("TEST_SECOND_CAMPSITE_0"));
        Assert.Equal("1", Completed());
        state = New("campsites-any.db", Catalogue(["Visit the Test Camp."]));  // no reserve listed: the camp of any reserve
        ActivateAll();
        Hb(Enter("TEST_SECOND_CAMPSITE_0"));
        Assert.Equal("1", Completed());
    }

    [Fact]
    public void TestAnOverriddenArriveWithCoordinatesIsMetAtItsPlaceOrNearThem()
    {
        var rules = new JsonObject { ["1/0"] = "arrive; place: TESTLAND_FROZEN_ZORBEL" };
        state = New("override-point.db", Catalogue([Zorbel]), rules: rules);
        ActivateAll();
        Hb(EnterAt("TESTLAND_FROZEN_ZORBEL_EDGE", -1000, -2000));
        Assert.Equal("", Completed());  // another place, far from the point
        Hb(EnterAt("TESTLAND_UNRELATED_PLACE", -4600, -11100));
        Assert.Equal("1", Completed());  // near the point, whatever the name
        state = New("override-place.db", Catalogue([Zorbel]), rules: rules);
        ActivateAll();
        Hb(EnterAt("TESTLAND_FROZEN_ZORBEL", -4000, -11500));  // the game's area, entered 700 m from the point
        Assert.Equal("1", Completed());
    }

    [Theory]
    [InlineData("Start at Zurnhutte in Test Reserve", 103, true)]  // the title drops the umlaut the catalogue's name has
    [InlineData("Start at Zürnhütte in Test Reserve", 103, true)]
    [InlineData("Start at Zurnhutte in Test Reserve", 101, false)]
    public void TestAStartIsMetByALodgeNameWithOrWithoutItsAccents(string title, int lodge, bool met)
    {
        state = New($"start-{Math.Abs(title.GetHashCode())}-{lodge}.db", Catalogue([title]));
        ActivateAll();
        state.NoteLaunch(ME, System.Text.Encoding.ASCII.GetBytes($"start={lodge}&reserve=2"));
        Assert.Equal(met ? "1" : "", Completed());
    }
}
