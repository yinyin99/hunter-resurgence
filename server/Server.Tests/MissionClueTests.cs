using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>What a clue objective asks: the kind of clue its title names (footprints, droppings, a call, a blood trail, bedding, any; `tracks` footprints, but any clue of one animal's trail),
/// the animal (a short species name too, a gender) and how many clues of one animal after the mission was activated; a clue word the server does not read leaves the objective open; the
/// rollback and the start check clues too. Invented catalogue and events (`track_type` 2 footprints, 1 droppings, 9 a call, 0 a blood trail, 12 bedding, 20 rooting, 31 water bubbles).</summary>
public class MissionClueTests : MissionTestBase
{
    [Fact]
    public void TestACallDroppingsAndFootprintsAreToldApartAndAFemaleIsNotAMale()
    {
        state = New("kinds.db", Catalogue(["ID a Call from a Male Test Deer", "ID droppings from a Test Deer", "ID tracks from a Test Deer", "ID a blood trail of a Test Deer", "ID a bed from a Test Deer"]));
        ActivateAll();
        Hb(Clue(1, 2));  // footprints
        Assert.Equal("3", Completed());
        Hb(Clue(2, 1));  // droppings
        Assert.Equal("2,3", Completed());
        Hb(Clue(4, 9, gender: 1));  // the call of a female
        Assert.Equal("2,3", Completed());
        Hb(Clue(3, 9));  // the call of a male
        Assert.Equal("1,2,3", Completed());
        Hb(Clue(5, 0));  // a blood trail
        Assert.Equal("1,2,3,4", Completed());
        Hb(Clue(6, 12));  // bedding
        Assert.Equal("1,2,3,4,5", Completed());
    }

    [Fact]
    public void TestAShortSpeciesNameMeansTheSpeciesOfItAndThreeCluesMeanThreeOfAnyAnimalOfIt()
    {
        state = New("elk.db", Catalogue(["Locate 3 Elk tracks"]));
        ActivateAll();
        for (var i = 0; i < 3; i++)
            Hb(Clue(1, 2));  // three footprints of a deer
        Hb(Clue(2, 8, species: 3));
        Hb(Clue(3, 8, species: 3));  // two clues of two elk
        Assert.Equal("", Completed());
        Hb(Clue(4, 9, species: 3));  // a call of a third: the third clue of an elk in the hunt
        Assert.Equal("1", Completed());
    }

    [Fact]
    public void TestTracksAreFootprintsButTheCluesOfOneAnimalsTrailAreAnyClues()
    {
        state = New("trail.db", Catalogue(["Locate 3 Test Deer tracks from the same animal", "ID tracks or droppings from a Test Fox", "ID 2 tracks from a Test Fox"]));
        ActivateAll();
        Hb(Clue(1, 1));
        Hb(Clue(2, 9));
        Hb(Clue(3, 2));  // three clues of three deer
        Hb(Clue(4, 1));
        Hb(Clue(4, 9));
        Assert.Equal("", Completed());
        Hb(Clue(4, 2));  // droppings, a call and footprints of one deer
        Assert.Equal("1", Completed());
        Hb(Clue(5, 9, species: 5));
        Hb(Clue(5, 9, species: 5));  // two calls of a fox are neither its tracks nor its droppings
        Assert.Equal("1", Completed());
        Hb(Clue(5, 2, species: 5));
        Assert.Equal("1,2", Completed());
        Hb(Clue(5, 2, species: 5));  // its second footprints
        Assert.Equal("1,2,3", Completed());
    }

    [Fact]
    public void TestATrailNamedByTheSameAnimalTakesEveryClueOfItRootingAndWaterBubblesToo()
    {
        state = New("same.db", Catalogue(["Identify 3 Test Deer tracks from the same animal", "ID 3 tracks from one and the same Test Fox", "ID 2 tracks from a Plains Elk"]));
        ActivateAll();
        Hb(Clue(1, 2));
        Hb(Clue(1, 31));
        Assert.Equal("", Completed());
        Hb(Clue(1, 20));  // footprints, water bubbles and rooting of one deer
        Assert.Equal("1", Completed());
        Hb(Clue(2, 1, species: 5));
        Hb(Clue(2, 9, species: 5));
        Hb(Clue(2, 0, species: 5));  // droppings, a call and a blood trail of one fox
        Assert.Equal("1,2", Completed());
        Hb(Clue(3, 8, species: 3));
        Hb(Clue(3, 31, species: 3));  // footprints and water bubbles of an elk: tracks that are no trail are footprints
        Assert.Equal("1,2", Completed());
    }

    [Fact]
    public void TestTracksBesideAnotherKindAreFootprintsEvenOnATrail()
    {
        state = New("listed.db", Catalogue(["Locate 2 Test Fox tracks or droppings from the same animal", "ID tracks, droppings or calls of a Test Deer"]));
        ActivateAll();
        Hb(Clue(1, 9, species: 5));
        Hb(Clue(1, 0, species: 5));  // a call and a blood trail of one fox
        Hb(Clue(2, 0));  // a blood trail of a deer
        Hb(Clue(1, 1, species: 5));  // the fox's droppings: one clue of the kinds named
        Assert.Equal("", Completed());
        Hb(Clue(1, 2, species: 5));  // and its footprints
        Assert.Equal("1", Completed());
        Hb(Clue(2, 9));
        Assert.Equal("1,2", Completed());
    }

    [Fact]
    public void TestOnlyTheElkTheMissionListsCountAndOnlyTheCluesAfterTheActivation()
    {
        state = New("elks.db", Catalogue(["Locate 2 Elk tracks"], ", \"species\": [3]"));  // the mission lists one of the two elk
        ActivateAll();
        Hb(Clue(1, 8, species: 4));
        Hb(Clue(1, 8, species: 4));
        Assert.Equal("", Completed());
        Hb(Clue(2, 8, species: 3));
        Hb(Clue(2, 8, species: 3));
        Assert.Equal("1", Completed());
        state = New("cutoff.db", Catalogue(["Locate 3 Test Deer tracks"]));
        Hb(Clue(1, 2));
        Hb(Clue(1, 2));  // two before the activation
        ActivateAll();
        Hb(Clue(1, 2));
        Hb(Clue(1, 2));
        Assert.Equal("", Completed());
        Hb(Clue(1, 2));
        Assert.Equal("1", Completed());
    }

    [Fact]
    public void TestAnyClueIsMetByAKnownKindRootingIncludedAndNeverByAClueNoKindIsKnownFor()
    {
        state = New("any.db", Catalogue(["ID any clue from a Test Fox", "ID tracks or droppings from a Test Fox", "ID the call of a Test Fox"]));
        ActivateAll();
        Hb([Clue(3, 0, species: 5)[0], O("""{"event": 12, "individual": 3, "track_type": 9.5}""")]);  // a type that is no whole number
        Hb([O("""{"event": 12, "individual": 3}""")]);  // none
        Assert.Equal("", Completed());
        Hb(Clue(2, 20, species: 5));  // rooting: a bare `any clue` takes it (the maintainer's reading)
        Assert.Equal("1", Completed());
        Hb(Clue(5, 1, species: 5));  // droppings
        Assert.Equal("1,2", Completed());
    }

    [Fact]
    public void TestTheTypesTwoKindsShareNeedTheAnimalToHaveAKind()
    {
        state = New("ambiguous.db", Catalogue(["ID any clue"]));  // no species: the kind of 25 and 32 cannot be told from water bubbles without the animal
        ActivateAll();
        Hb(O("""{"event": 12, "individual": 1, "track_type": 25}"""));
        Hb(O("""{"event": 12, "individual": 2, "track_type": 32}"""));
        Assert.Equal("", Completed());
        Hb(Clue(3, 25, species: 5));  // a known animal: droppings
        Assert.Equal("1", Completed());
    }

    [Fact]
    public void TestAClueObjectiveWithWordsTheServerDoesNotReadIsNeverMetAndTheCoverageCountsIt()
    {
        state = New("clue-unread.db", Catalogue(["Find a Test Fox track north of the Middle Tower", "ID tracks from different Test Fox", "ID a Test Fox track",
            "ID tracks from a Sitka", "ID tracks from a Test Deer at (X: 10, Y: 20)"]));
        ActivateAll();
        Hb(Clue(1, 2, species: 5));
        Hb(Clue(2, 2));  // a Test Deer anywhere
        Assert.Equal("3", Completed());  // the short name no species has and the place are not read
        Assert.Equal("understood: 1, not understood: 4 (never met until a version reads them)", state.MissionCoverage().ElementAt(1));
    }

    [Fact]
    public void TestAClueOfAnIndividualWhoseSpawnIsFromAnEarlierHuntHasNoAnimal()
    {
        state = New("spawn.db", Catalogue(["ID tracks from a Test Deer"]));
        Launch();
        ActivateAll();
        Hb(O("""{"event": 7, "individual": 1, "species": 1, "gender": 0, "weight_grams": 90000, "texture": 0, "special": 0}"""));  // seen, no clue
        Launch();
        Hb(O("""{"event": 12, "individual": 1, "track_type": 2}"""));  // the next hunt's animal of that number: no spawn of its own
        Assert.Equal("", Completed());
        Hb(Clue(1, 2));
        Assert.Equal("1", Completed());
    }

    [Fact]
    public void TestTheRollbackReopensAFootprintsCompletionOfACallAndPaysAgainAtTheCall()
    {
        state = New("clue-rollback.db", Catalogue(["ID a clue from a Test Deer"]));  // the earlier rules took any clue
        ActivateAll();
        Hb(Clue(1, 2));
        Assert.Equal("1", Completed());
        var wallet = Gm();
        var strict = New("clue-rollback.db", Catalogue(["ID a call from a Test Deer"]));
        var dry = strict.RollbackMissions(apply: false).ToList();
        Assert.Contains("mission 1 (completed, active again) objectives 0", dry[0]);
        Assert.Equal("1", Completed(of: strict));
        Assert.Equal("reopened 1 objectives in 1 missions; kept: not understood 0 finished objectives", strict.RollbackMissions(apply: true).Last());
        Assert.Equal(("", wallet), (Completed(of: strict), Gm(strict)));  // nothing is taken back
        state = strict;
        Hb(Clue(2, 9));
        Assert.Equal(("1", wallet + 10), (Completed(of: strict), Gm(strict)));  // the call pays the mission again
        Assert.Equal("reopened 0 objectives in 0 missions; kept: not understood 0 finished objectives", strict.RollbackMissions(apply: true).Last());
    }

    [Fact]
    public void TestTheRollbackOfAnActiveSingleHuntMissionKeepsWhatItsLatestHuntFinished()
    {
        state = New("one-hunt.db", """{"missions": [{"id": 1, "title": "One Hunt", "singleExpedition": true, "dependencies": [], "rewards": [{"text": "10 gm$"}], "objectives": [""" +
            """{"id": 0, "title": "ID footprints from a Test Deer"}, {"id": 1, "title": "ID footprints from a Test Fox"}, {"id": 2, "title": "ID footprints from a Plains Elk"}]}]}""");
        ActivateAll();
        Launch();
        Hb(Clue(1, 2));
        Hb(Clue(2, 8, species: 3));  // the first hunt finishes the deer and the elk
        Launch();
        Hb(Clue(1, 2, species: 5));
        Hb(Clue(2, 8, species: 3));  // the latest one the fox and the elk
        Eq("""{"state": 2, "objectives": [1, 2]}""", Reply(state.MissionsReply(ME)!)["states"]!["1"]);
        Assert.Equal("would reopen 0 objectives in 0 missions; kept: not understood 0 finished objectives (dry run: nothing written, --apply writes)", state.RollbackMissions(apply: false).Last());
    }

    [Fact]
    public void TestAClueTheVersionThatReadsItsTitleFindsAmongTheStoredOnesFinishesItAtTheStart()
    {
        state = New("clue-later.db", Catalogue(["ID a call from a Test Deer north of the Middle Tower"]));
        ActivateAll();
        Hb(Clue(1, 9));
        Assert.Equal("", Completed());
        var later = New("clue-later.db", Catalogue(["ID a call from a Test Deer"]));
        Assert.Equal(1, later.ConfirmMissions());
        Assert.Equal("1", Completed(of: later));
    }

    // one mission whose objectives are these titles and a last one (a harvest of a fox) that no test finishes, so the mission stays active
    static string Mission(string[] titles, bool single = false) =>
        """{"missions": [{"id": 1, "title": "Test", "singleExpedition": """ + (single ? "true" : "false") + """, "dependencies": [], "rewards": [{"text": "10 gm$"}], "objectives": [""" +
        string.Join(",", titles.Append("Harvest a Test Fox").Select((t, i) => "{\"id\": " + i + ", \"title\": \"" + t + "\"}")) + "]}]}";

    string Done() => string.Join(",", Reply(state.MissionsReply(ME)!)["states"]!["1"]!["objectives"]!.AsArray().Select(i => (long)i!));

    [Fact]
    public void TestAnotherAnimalNeedsTheNextDifferentAnimalAndAnotherTrackAnyAnimal()
    {
        state = New("another.db", Mission(["ID the tracks of a Test Deer.", "Then ID the tracks of another Test Deer.", "Then ID the tracks of another Test Deer."]));
        ActivateAll();
        for (var i = 0; i < 3; i++)
            Hb(Clue(1, 2));  // three footprints of one deer
        Assert.Equal("0", Done());
        Hb(Clue(2, 2));
        Assert.Equal("0,1", Done());
        Hb(Clue(1, 2));  // the first deer again: two animals so far
        Assert.Equal("0,1", Done());
        Hb(Clue(3, 2));
        Assert.Equal("0,1,2", Done());

        state = New("another-track.db", Mission(["ID a track from a Test Deer", "ID another track from a Test Deer", "ID another set of tracks"]));
        ActivateAll();
        Hb(Clue(1, 2));
        Hb(Clue(1, 2));
        Hb(Clue(1, 2));  // another track says nothing more: each track finishes one objective
        Assert.Equal("0,1,2", Done());
    }

    [Fact]
    public void TestAnotherMaleNeedsAMaleAndASecondOrThirdAnimalIsTheSecondOrThird()
    {
        state = New("gender.db", Mission(["ID tracks of a Test Deer", "ID Tracks of another male Test Deer", "ID tracks of a third Test Deer"]));
        ActivateAll();
        Hb(Clue(1, 2));
        Hb(Clue(2, 2, gender: 1));  // a female: no other male yet
        Assert.Equal("0", Done());
        Hb(Clue(3, 2));
        Assert.Equal("0,1", Done());
        Hb(Clue(1, 2));  // a third animal of the hunt is needed, whichever clue
        Assert.Equal("0,1,2", Done());
    }

    [Fact]
    public void TestAnotherAnimalOfALaterHuntCountsInAMissionOfSeveralHuntsAndNotInASingleHuntOne()
    {
        state = New("hunts.db", Mission(["ID the tracks of a Test Deer", "ID the tracks of another Test Deer"]));
        ActivateAll();
        Launch();
        Hb(Clue(1, 2));
        Hb(Clue(1, 2));
        Assert.Equal("0", Done());
        Launch();
        Hb(Clue(1, 2));  // the next hunt's animal of that number is another one
        Assert.Equal("0,1", Done());

        state = New("single.db", Mission(["ID the tracks of a Test Deer", "ID the tracks of another Test Deer"], single: true));
        ActivateAll();
        Launch();
        Hb(Clue(1, 2));
        Launch();  // the hunt forgets the progress of a single-hunt mission
        Hb(Clue(1, 2));
        Hb(Clue(1, 2));
        Assert.Equal("0", Done());
        Hb(Clue(2, 2));
        Assert.Equal("0,1", Done());
    }

    [Fact]
    public void TestTheStartFinishesAnotherAnimalObjectivesTheirEarlierReadingNeverMet()
    {
        state = New("another-start.db", Mission(["ID the tracks of a Test Deer", "ID the tracks of another Test Deer while whistling"]));  // not read: never met
        ActivateAll();
        Hb(Clue(1, 2));
        Hb(Clue(1, 2));
        var later = New("another-start.db", Mission(["ID the tracks of a Test Deer", "ID the tracks of another Test Deer"]));
        Assert.Equal(0, later.ConfirmMissions());  // one animal
        Hb(Clue(2, 2));
        Assert.Equal(1, New("another-start.db", Mission(["ID the tracks of a Test Deer", "ID the tracks of another Test Deer"])).ConfirmMissions());
    }

    [Fact]
    public void TestTheCoveragePrintsTheDifferentAnimalsAnotherAsks()
    {
        state = New("another-rules.db", Mission(["ID tracks of a Test Deer", "ID tracks of another Test Deer", "ID the call of another Test Deer", "ID tracks of a male Test Deer", "ID tracks of another male Test Deer in the same hunt", "ID another track"]));
        var lines = state.MissionCoverage(rules: true).Where(l => l.StartsWith("mission 1 objective", StringComparison.Ordinal)).ToList();
        Assert.Contains("mission 1 objective 1: ID tracks of another Test Deer => track; species: Test Deer; clues: footprints x1; in the mission: 2 different animals", lines);
        Assert.Contains("mission 1 objective 2: ID the call of another Test Deer => track; species: Test Deer; clues: call x1", lines);  // another animal's call, the first one: no other animal
        Assert.Contains("mission 1 objective 4: ID tracks of another male Test Deer in the same hunt => track; species: Test Deer; male; clues: footprints x1; in the hunt: 2 different animals", lines);
        Assert.Contains("mission 1 objective 5: ID another track => track; clues: footprints x1", lines);
    }

    [Fact]
    public void TestFootprintsOfEachSpeciesNearThePointFinishTheObjectiveAndOneSpeciesOrOnesFarAwayDoNot()
    {
        static JsonNode[] Print(int individual, int species, int x, int z) =>
        [
            O($$"""{"event": 7, "individual": {{individual}}, "species": {{species}}, "gender": 0, "weight_grams": 90000, "texture": 0, "special": 0}"""),
            O($$"""{"event": 12, "individual": {{individual}}, "track_type": 2, "x": {{x}}, "z": {{z}}}"""),
        ];
        state = New("each.db", Catalogue(["Look for the tracks near the pond"]), rules: Rules(("1/0", "track; clues: footprints x1; footprints of each of Test Deer, Test Fox within 300 m of (1000, 2000)")));
        ActivateAll();
        Hb(Print(1, 1, 1100, 2000));  // a deer
        Hb(Print(2, 1, 1000, 2100));  // another deer
        Assert.Equal("", Completed());  // one species alone is not enough
        Hb(Print(3, 5, 1000, 2400));  // a fox 400 m away
        Assert.Equal("", Completed());
        Hb(Print(4, 5, 900, 1900));  // a fox 141 m away: both species near
        Assert.Equal("1", Completed());
        Assert.Contains("mission 1 objective 0 (override): Look for the tracks near the pond => track; clues: footprints x1; footprints of each of Test Deer, Test Fox within 300 m of (1000, 2000)", state.MissionCoverage(rules: true));
    }
}
