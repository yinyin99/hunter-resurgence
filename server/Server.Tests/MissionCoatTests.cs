using Resurgence.Server;

namespace Resurgence.Server.Tests;

/// <summary>The coat a title names (`a Chocolate colored ...`, `with fur variation Piebald Black`, `(Blue color variation)`, `a dark ...`, `with a color other than Common`, `Halloween`) is
/// the animal's spawn `texture`: the ids of that name, the Grey Wolf's (36) common coats by their colour; a word that names no coat of the species leaves the objective open; a spot is
/// read for its coat too; the rollback and the start judge coats. Invented events.</summary>
public class MissionCoatTests : MissionTestBase
{
    [Fact]
    public void TestACoatIsTheIdsOfItsNameWithOrWithoutItsFrame()
    {
        state = New("names.db", Catalogue(["Harvest a Chocolate colored Test Deer", "Harvest a Test Deer with fur variation Piebald Black", "Harvest a Test Deer (Blue color variation)",
            "Harvest a Dark Red Test Deer", "Harvest any Piebald Test Deer", "Harvest a Grey coloured Test Deer"]));
        ActivateAll();
        Hb(Shot(1, texture: 0));  // common
        Assert.Equal("", Completed());
        Hb(Shot(2, texture: 30));  // a second id of the name `Chocolate`
        Assert.Equal("1", Completed());
        Hb(Shot(3, texture: 25));  // `Piebald Black` is no `Piebald`
        Assert.Equal("1,2", Completed());
        Hb(Shot(4, texture: 23));  // `Common Blue`
        Assert.Equal("1,2,3", Completed());
        Hb(Shot(5, texture: 26));  // `Dark` is no `Dark Red`
        Assert.Equal("1,2,3", Completed());
        Hb(Shot(6, texture: 27));
        Assert.Equal("1,2,3,4", Completed());
        Hb(Shot(7, texture: 38));  // a third id of `Piebald`
        Assert.Equal("1,2,3,4,5", Completed());
        Hb(Shot(8, texture: 12));
        Assert.Equal("1,2,3,4,5,6", Completed());
        Assert.DoesNotContain(state.MissionCoverage(), l => l.StartsWith("mission ", StringComparison.Ordinal));  // every title is understood
    }

    [Fact]
    public void TestTheGreyWolfsCommonCoatsAreToldApartByColourAndOtherSpeciesKeepTheGameNames()
    {
        state = New("wolf.db", Catalogue(["Harvest an unspooked common Forest Wolf", "Harvest a dark Forest Wolf", "Harvest a brown Forest Wolf", "Harvest a dark Test Deer",
            "Harvest a brown Test Deer"]));
        ActivateAll();
        Hb(Shot(1, species: 36, texture: 1));
        Assert.Equal("2", Completed());
        Hb(Shot(2, texture: 1));  // a common deer: `dark` is the deer's own coat 26, 28 or 32
        Assert.Equal("2", Completed());
        Hb([.. Shot(3, species: 36, texture: 3), .. Shot(4, species: 36, texture: 0)]);
        Assert.Equal("1,2,3", Completed());
        Hb(Shot(5, texture: 32));
        Assert.Equal("1,2,3,4", Completed());
        Hb(Shot(6, texture: 3));  // `brown` names a coat of the Grey Wolf only: never met for a deer
        Assert.Equal("1,2,3,4", Completed());
        Assert.Contains("mission 5 objective 0: Harvest a brown Test Deer", state.MissionCoverage());
    }

    [Fact]
    public void TestOtherThanAGenderWordBetweenAndTheHalloweenCoats()
    {
        state = New("other.db", Catalogue(["Harvest a male Test Deer with a color other than Common", "Harvest an Orange female Test Deer", "Harvest an airborne Halloween male Test Deer"]));
        ActivateAll();
        Hb(Shot(1, texture: 3));  // a common coat
        Assert.Equal("", Completed());
        Hb(Shot(2, texture: 29));  // `Orange` of a male
        Assert.Equal("1", Completed());
        Hb(Shot(3, texture: 57));  // the Halloween coat of a standing deer
        Assert.Equal("1", Completed());
        Hb(Shot(4, texture: 29, gender: 1));
        Assert.Equal("1,2", Completed());
        Hb(Shot(5, texture: 57, animalState: 6));
        Assert.Equal("1,2,3", Completed());
    }

    [Fact]
    public void TestASpotIsReadForItsCoat()
    {
        state = New("spot.db", Catalogue(["Spot a Common colored Test Deer", "Spot a Test Deer with fur variation Grey"]));
        ActivateAll();
        Hb(Spot(1, texture: 7));
        Assert.Equal("", Completed());
        Hb(Spot(2, texture: 12));
        Assert.Equal("2", Completed());
        Hb(Spot(3, texture: 1));
        Assert.Equal("1,2", Completed());
    }

    [Fact]
    public void TestTheRollbackReopensACoatTheEarlierRulesDidNotReadAndTheStartFinishesOneAHarvestMet()
    {
        state = New("coat-rollback.db", Catalogue(["Harvest a Test Deer", "Harvest a Test Deer the unread way"]));  // the earlier rules read no coat: the second title is not understood
        ActivateAll();
        Hb(Shot(1, texture: 0));
        Hb(Shot(2, texture: 32));
        Assert.Equal("1", Completed());
        var strict = New("coat-rollback.db", Catalogue(["Harvest a Chocolate colored Test Deer", "Harvest a dark Test Deer"]));
        Assert.Equal(1, strict.ConfirmMissions());  // the dark deer meets the open one at the start
        Assert.Equal("1,2", Completed(of: strict));
        // the first was finished by a common deer, and no chocolate one was harvested: reopened; the second stands
        Assert.Equal("reopened 1 objectives in 1 missions; kept: not understood 0 finished objectives", strict.RollbackMissions(apply: true).Last());
        Assert.Equal("2", Completed(of: strict));
    }
}
