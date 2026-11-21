using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>What a clue objective asks: the kind of clue its title names (footprints, droppings, a call, a blood trail, any), the animal (a short species name too, a gender) and how many
/// clues of one animal; a clue word the server does not read leaves the objective open; the rollback and the start check clues too. Invented catalogue and events
/// (`track_type` 2 footprints, 1 droppings, 9 a call, 0 a blood trail, 31 water bubbles).</summary>
public class MissionClueTests : MissionTestBase
{
    [Fact]
    public void TestACallDroppingsAndFootprintsAreToldApartAndAFemaleIsNotAMale()
    {
        state = New("kinds.db", Catalogue(["ID a Call from a Male Test Deer", "ID droppings from a Test Deer", "ID tracks from a Test Deer", "ID a blood trail of a Test Deer"]));
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
    }

    [Fact]
    public void TestAShortSpeciesNameMeansTheSpeciesOfItAndThreeCluesMeanThreeOfOneAnimal()
    {
        state = New("elk.db", Catalogue(["Locate 3 Elk tracks"]));
        ActivateAll();
        for (var i = 0; i < 3; i++)
            Hb(Clue(1, 2));  // three footprints of a deer
        Hb(Clue(2, 8, species: 3));
        Hb(Clue(2, 8, species: 3));
        Hb(Clue(3, 8, species: 3));  // two of one elk, one of another
        Hb(Clue(2, 9, species: 3));  // a call of the first one is no footprints
        Assert.Equal("", Completed());
        Hb(Clue(2, 8, species: 3));
        Assert.Equal("1", Completed());
    }

    [Fact]
    public void TestAnyClueIsMetByAnyKindAndAClueOfAKindTheServerDoesNotKnowIsNoFootprints()
    {
        state = New("any.db", Catalogue(["ID any clue from a Test Fox", "ID tracks or droppings from a Test Fox", "ID the call of a Test Fox"]));
        ActivateAll();
        Hb(Clue(1, 31, species: 5));  // water bubbles
        Assert.Equal("1", Completed());
        Hb(Clue(2, 0, species: 5));  // a blood trail
        Assert.Equal("1", Completed());
        Hb(Clue(3, 1, species: 5));
        Assert.Equal("1,2", Completed());
    }

    [Fact]
    public void TestAClueObjectiveWithWordsTheServerDoesNotReadIsNeverMetAndTheCoverageCountsIt()
    {
        state = New("clue-unread.db", Catalogue(["Find a Test Fox track north of the Middle Tower", "ID tracks from three different Test Fox within a span of 5 min", "ID a Test Fox track"]));
        ActivateAll();
        Hb(Clue(1, 2, species: 5));
        Assert.Equal("3", Completed());
        Assert.Equal("understood: 1, not understood: 2 (never met until a version reads them)", state.MissionCoverage().ElementAt(1));
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
}
