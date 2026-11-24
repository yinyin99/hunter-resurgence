using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>An arrive objective is met by a place entered (event 101) that the title names, or that lies within 200 m of the title's coordinates in the mission's reserve, on invented missions and places.</summary>
public class MissionArriveTests : MissionTestBase
{
    const string Chute = "Then arrive at the Chute (x -4633,y -11152)";

    /// <summary>The player enters a place at a position (metres, the map's x and z).</summary>
    static JsonNode EnterAt(string place, int x, int z) =>
        O($$"""{"event": 101, "location_name": "{{place}}", "direction": "enter", "player_x": {{x}}, "player_y": 2, "player_z": {{z}}}""");

    [Theory]
    [InlineData(Chute, "ALASKA_FROZEN_CHUTE")]
    [InlineData(Chute, "HUNTER_GAME_LOCATION_ALASKA_CHUTE")]
    [InlineData("Then arrive at Snaketongue Fall", "ALASKA_SNAKETONGUE_FALL")]
    [InlineData("Then arrive at Snaketongue Fall", "HUNTER_GAME_LOCATION_ALASKA_SNAKETONGUE_FALLS")]
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
    [InlineData("Arrive at the Old Tower", "ALASKA_NORTH_TOWER")]
    [InlineData("Arrive at the Grand Lodge", "ALASKA_FROZEN_LODGE")]
    [InlineData("Arrive at the Old Lodge", "ALASKA_LODGE")]
    [InlineData("Arrive at the Chute", "ALASKA_FROZEN_CHUTES_OF_DOOM")]
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
        state = New($"near-{x}-{z}-{reserve}.db", Catalogue([Chute], ", \"reserves\": [2]"));
        ActivateAll();
        Launch(reserve);
        Hb(EnterAt("ALASKA_UNRELATED_PLACE", x, z));
        Assert.Equal(met ? "1" : "", Completed());
    }

    [Fact]
    public void TestAnOrderedArriveWaitsForTheObjectiveBeforeItWhateverMeetsIt()
    {
        state = New("ordered.db", """
            {"missions": [{"id": 1, "title": "Two", "dependencies": [], "rewards": [{"text": "10 gm$"}], "reserves": [2], "objectives": [
              {"id": 0, "title": "Harvest a Test Deer"}, {"id": 1, "title": "Then arrive at the Chute (x -4633,y -11152)"}]}]}
            """);
        ActivateAll();
        Launch();
        Hb(Enter("ALASKA_FROZEN_CHUTE"), EnterAt("ALASKA_UNRELATED_PLACE", -4633, -11152));
        Assert.Equal("", Completed());
        Hb(Shot(1));
        Hb(Enter("ALASKA_FROZEN_CHUTE"));
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
        Hb(Enter("ALASKA_FROZEN_CHUTE"));  // not the place the titles name yet
        var wallet = Gm();
        Assert.Equal("", Completed());
        var later = New("stored.db", Missions("Chute"));
        Assert.Equal(1, later.ConfirmMissions());  // mission 2's arrive waits for its harvest
        Assert.Equal(("1", wallet + 10), (Completed(of: later), Gm(later)));
        Assert.Equal(0, later.ConfirmMissions());
    }

    [Fact]
    public void TestAnOverriddenArriveStaysExactAndTheCoverageListsTheArriveObjectivesNoLocationMeets()
    {
        state = New("coverage-arrive.db", Catalogue(["Arrive at the Test Lodge", "Arrive at the Mystery Spot", "Arrive at the Mystery Spot (x 5000, y 6000)", "Info message when you are south of the Test Pond"]),
            rules: new JsonObject { ["4/0"] = "arrive; place: TEST_RESERVE_SOUTH_OF_POND2" });
        var report = state.MissionCoverage().ToList();
        Assert.Contains("arrive objectives with no coordinates that no known location's name meets: 1", report);
        Assert.Equal(["arrive not matched: mission 2 objective 0: Arrive at the Mystery Spot"], report.Where(l => l.StartsWith("arrive not matched:", StringComparison.Ordinal)));
        ActivateAll();
        Hb(Enter("TEST_RESERVE_SOUTH_OF_POND2_EXTRA"), Enter("HUNTER_GAME_LOCATION_TEST_RESERVE_SOUTH_OF_POND2"));
        Assert.Equal("", Completed());  // an operator's place is met by that name only
    }
}
