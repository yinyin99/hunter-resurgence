using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The wordings of an objective's title the server reads, by group: the words in front (`At any point ...,`, `Take`, `Register`, `Head to`, `Lastly,`), the species written another way
/// or several, the weapon, ammunition and sight words, the stand, the organs, the animal's state, the player's stance and the numbers. Invented catalogue and events; each objective is one
/// mission whose id is its place in the list.</summary>
public class MissionWordingTests : MissionTestBase
{
    // organ bits: the heart is 1, the left lung 4, a spine bone 1 << 21, the skull 1 << 28
    const long Heart = 1, LeftLung = 4, Spine = 1L << 21, Skull = 1L << 28;

    string Done(int mission, State? of = null) =>
        string.Join(",", Reply((of ?? state).MissionsReply(ME)!)["states"]![mission.ToString()]!["objectives"]!.AsArray().Select(i => (long)i!));

    static string Mission(int id, params string[] objectives) =>
        $$"""{"id": {{id}}, "title": "Mission {{id}}", "dependencies": [], "rewards": [{"text": "10 gm$"}], "objectives": [{{string.Join(",", objectives.Select((o, i) => $$"""{"id": {{i}}, "title": "{{o}}"}"""))}}]}""";

    [Fact]
    public void TestAPrefixOrAVerbInFrontOfTheTitleIsTakenOffBeforeTheKindIsChosenAndItsReserveIsKept()
    {
        state = New("prefix.db", Catalogue(
        [
            "At any point during the same hunt, harvest a Test Deer",                                                // 1
            "At any point during the same hunt, spot a Test Deer",                                                   // 2
            "At any point during the same hunt, ID any clue (footprints, droppings, or audio) from a Test Deer",     // 3 any clue
            "At any time, harvest a Test Deer with the Test Carbine",                                                // 4
            "At any point while in Test Reserve, harvest a Test Deer",                                               // 5
            "At any point while in Test Reserve, ID a Test Deer track.",                                             // 6
            "Take a Test Deer from under 40 meters",                                                                 // 7
            "Kill a Test Deer",                                                                                      // 8
            "Register an audio clue of a Test Deer",                                                                 // 9 a heard call
        ]));
        ActivateAll();
        Hb(Shot(1));  // 150 m with the carbine, before any hunt: no reserve yet
        Assert.Equal("1,4,8", Completed());
        Hb(Spot(2));
        Hb(Clue(3, trackType: 2));  // footprints: any clue, not the heard call, and not in the reserve
        Assert.Equal("1,2,3,4,8", Completed());
        Hb(Clue(4, trackType: 9));
        Assert.Equal("1,2,3,4,8,9", Completed());
        Launch();
        Hb(Shot(5, distance: 2000));
        Assert.Equal("1,2,3,4,5,7,8,9", Completed());
        Hb(Clue(6, trackType: 2));
        Assert.Equal("1,2,3,4,5,6,7,8,9", Completed());
    }

    [Fact]
    public void TestHeadingToAPlaceArrivesThereAndBeginningAtALodgeIsTheStartOfTheHunt()
    {
        state = New("go.db", Catalogue(["Head over to Test Pond (x 5, y 6)", "Travel to Test Hill", "Go to Test Field", "Make it to Test Barn", "Begin at Test Lodge in Test Reserve.", "Head to Test Cabin"]));
        ActivateAll();
        Hb(Enter("TEST_POND"));
        Assert.Equal("1", Completed());
        Hb(Enter("TEST_HILL"), Enter("TEST_FIELD"), Enter("TEST_BARN"));
        Assert.Equal("1,2,3,4", Completed());
        Hb(Enter("TEST_LODGE"));  // walking into the lodge is not the start of a hunt
        Assert.Equal("1,2,3,4", Completed());
        Launch();
        Assert.Equal("1,2,3,4,5", Completed());
        Hb(Enter("TEST_CABIN"));
        Assert.Equal("1,2,3,4,5,6", Completed());
    }

    [Fact]
    public void TestAnObjectiveAfterOthersCountsOnlyOnceTheEarlierOnesAreDoneWhateverWordPutsItAfter()
    {
        state = New("after.db", "{\"missions\": ["
            + Mission(1, "Spot a Test Deer", "After spotting the Test Deer, harvest a Test Deer", "Lastly, locate a Test Deer track") + ","
            + Mission(2, "Harvest a Test Deer", "After that, spot a Test Deer") + "]}");
        ActivateAll();
        Hb(Shot(1));  // 1 has no spot yet; 2 has its first objective
        Assert.Equal(("", "0"), (Done(1), Done(2)));
        Hb(Spot(2));
        Assert.Equal(("0", "2"), (Done(1), Completed()));  // the spot finished mission 2: its ordered objective had its harvest
        Hb(Clue(3, trackType: 2));  // the last objective waits for the harvest
        Assert.Equal("0", Done(1));
        Hb(Shot(4));
        Assert.Equal("0,1", Done(1));
        Hb(Clue(5, trackType: 2));
        Assert.Equal("1,2", Completed());
    }

    [Fact]
    public void TestAShortNameOfASpeciesIsTheMissionsOwnSpeciesAndSeveralSpeciesAreAnyOfThem()
    {
        state = New("elk-own.db", Catalogue(["Harvest an Elk", "Spot an Elk"], ", \"species\": [3]"));  // the mission lists the plains elk
        ActivateAll();
        Hb(Shot(1, species: 4));  // a hill elk
        Hb(Spot(2, species: 4));
        Assert.Equal("", Completed());
        Hb(Shot(3, species: 3));
        Hb(Spot(4, species: 3));
        Assert.Equal("1,2", Completed());

        state = New("elk-any.db", Catalogue(["Harvest an Elk"]));  // no species listed: any elk
        ActivateAll();
        Hb(Shot(1, species: 4));
        Assert.Equal("1", Completed());

        state = New("species.db", Catalogue(["Harvest a Test Fox or a Test Rabbit", "Harvest any animal except Test Fox", "Harvest one of any species of Ptarmigan", "Harvest a Test Mallard duck", "Harvest a Pintail duck"]));
        ActivateAll();
        Hb(Shot(1, species: 5));
        Assert.Equal("1", Completed());  // a fox: the first, and the one animal the second leaves out
        Hb(Shot(2, species: 1));
        Assert.Equal("1,2", Completed());
        Hb(Shot(3, species: 10));
        Hb(Shot(4, species: 7));
        Hb(Shot(5, species: 6));
        Assert.Equal("1,2,3,4,5", Completed());

        state = New("except.db", Catalogue(["Harvest any animal except an Elk", "Harvest a Test Fox and a Test Deer", "Harvest any animal except with a rifle"]));  // the short name is left out whatever the mission lists; "and" is two animals; nothing is left out by the third
        ActivateAll();
        Hb(Shot(1, species: 3));
        Hb(Shot(2, species: 4));
        Hb(Shot(3, species: 5));
        Hb(Shot(4, species: 1));
        Assert.Equal("1", Completed());
    }

    [Fact]
    public void TestAWeaponAmmunitionOrSightIsMetByItsClassItsBracketedNoteOrItsNameAndWhatIsLeftOutIsNot()
    {
        state = New("weapons.db", Catalogue(
        [
            "Harvest a Test Deer with a scoped weapon",                                       // 1 any scope
            "Harvest a Test Deer using any traditional bow",                                  // 2 not a compound bow, not a crossbow
            "Harvest a Test Deer with a tracer arrow",                                        // 3
            "Harvest a Test Deer with any .243 or .223 rifle",                                // 4 either calibre
            "Harvest a Test Deer using a rifle mounted with a Test Dot sight",                // 5 that sight
            "Harvest a Test Deer using 7mm Test Ammunition (HV)",                             // 6
            "Harvest a Test Deer using 7mm Test Ammunition (HP)",                             // 7
            "Harvest a Test Deer with any gun except a rifle",                                // 8
            "Harvest a Test Deer without using any type of buckshot",                         // 9
        ]));
        ActivateAll();
        Hb(Shot(1, weapon: 50, ammunition: 74));  // a rifle with buckshot, no scope
        Assert.Equal("", Completed());
        Hb(Shot(2, weapon: 51, ammunition: 74));  // a shotgun
        Assert.Equal("8", Completed());
        Hb(Shot(3, weapon: 63, ammunition: 71));  // a compound bow
        Assert.Equal("8,9", Completed());
        Hb(Shot(4, weapon: 62, ammunition: 71));  // a longbow
        Assert.Equal("2,8,9", Completed());
        Hb(Shot(5, weapon: 50, ammunition: 52, scope: 81));  // another scope than the dot sight
        Assert.Equal("1,2,8,9", Completed());
        Hb(Shot(6, weapon: 61, ammunition: 70, scope: 80));  // the .223 rifle, tracer arrows (a made-up pair), the dot sight
        Assert.Equal("1,2,3,4,5,8,9", Completed());
        Hb(Shot(7, weapon: 50, ammunition: 72));
        Assert.Equal("1,2,3,4,5,6,8,9", Completed());  // the HV, not the HP
        Hb(Shot(8, ammunition: 73));
        Assert.Equal("1,2,3,4,5,6,7,8,9", Completed());

        state = New("classes.db", Catalogue(["Harvest a Test Deer with any weapon except a longbow", "Harvest a Test Deer with any gun", "Harvest a Test Deer (no crossbows)", "Harvest a Test Deer with a sight"]));
        ActivateAll();
        Hb(Shot(1, weapon: 62));  // a longbow is left out of the first, is no gun, and is no crossbow
        Assert.Equal("3", Completed());
        Hb(Shot(2, weapon: 64));  // a crossbow: the second is no gun, the third refuses it
        Assert.Equal("1,3", Completed());
        Hb(Shot(3, weapon: 50, scope: 80));  // "a sight" is no sight of the catalogue: never met
        Assert.Equal("1,2,3", Completed());

        state = New("lists.db", Catalogue(["Harvest a Test Deer with any weapon except a rifle and a shotgun", "Harvest a Test Deer (no crossbows or shotguns)", "Harvest a Test Deer with any gun except a muzzleloader and a Test Shotgun"]));
        ActivateAll();
        Hb(Shot(1, weapon: 51));  // a shotgun is refused by the first and the third; the second is not understood
        Assert.Equal("", Completed());
        Hb(Shot(2, weapon: 50));  // a rifle: refused by the first, fine for the third
        Assert.Equal("3", Completed());
        Hb(Shot(3, weapon: 62));  // a longbow: no rifle and no shotgun
        Assert.Equal("1,3", Completed());
    }

    [Fact]
    public void TestAStandThatIsNotAllowedAndTheOrgansThatMayOrMayNotBeHit()
    {
        state = New("organs.db", Catalogue(
        [
            "Harvest a Test Deer without using a stand",                                                                                 // 1
            "Harvest a Test Deer without using a platform or blind",                                                                     // 2
            "Harvest a Test Deer. Stands, blinds, towers and shooting tripod rests are not allowed.",                                    // 3
            "Harvest a Test Deer with lung and/or heartshots only",                                                                      // 4 every shot has an organ
            "Harvest a Test Deer without hitting the skull",                                                                             // 5
            "Harvest a Test Deer without penetrating any bones.",                                                                        // 6
            "Harvest a Test Deer with a heart shot. Other organs may be hit, but the heart must be hit with the killing shot.",          // 7 the killing hit
            "Harvest a Test Deer. Only organs may be hit.",                                                                              // 8 no bone, a shot at no organ is none
            "Harvest a Test Deer with a shot through the heart and/or lungs. No other vital organs or bones should be hit.",             // 9 the named organs only
        ]));
        ActivateAll();
        Hb(Shot(1, location: 3, organs: [Heart | Skull]));
        Assert.Equal("7", Completed());
        Hb(Shot(2, location: 2, organs: [Spine, LeftLung]));  // a bone first, then a lung
        Assert.Equal("5,7", Completed());
        Hb(Shot(3, location: 5, organs: [0, LeftLung]));  // a shot at no organ first, then a lung
        Assert.Equal("5,6,7", Completed());
        Hb(Shot(4, location: 0, organs: [LeftLung]));  // from no stand at all
        Assert.Equal("1,2,3,4,5,6,7,8,9", Completed());
    }

    [Fact]
    public void TestTheAnimalsStateAndThePlayersStanceAreThoseOfTheKillingShot()
    {
        state = New("state.db", Catalogue(
        [
            "Harvest a Test Deer while it is charging you",                                                  // 1
            "Harvest a Test Deer when the animal is idle",                                                   // 2
            "Harvest a Test Deer while it is standing still",                                                // 3
            "Harvest a Test Deer that was shot while swimming",                                              // 4
            "Harvest a Test Deer killed with any shotgun while it is emerging from or diving into a burrow exit",  // 5
            "Harvest a Test Deer. The Test Deer should not be running away or charging you.",                // 6 neither
            "Harvest a Test Deer while prone",                                                               // 7
            "Harvest a Test Deer while crouching",                                                           // 8
            "Harvest a Test Deer while standing upright",                                                    // 9
        ]));
        ActivateAll();
        Hb(Shot(1, animalState: 5, pose: 2050));  // running, prone
        Assert.Equal("7", Completed());
        Hb(Shot(2, animalState: 9, pose: 2048));  // charging, upright
        Assert.Equal("1,7,9", Completed());
        Hb(Shot(3, animalState: 3, pose: 2049));  // standing, crouching
        Assert.Equal("1,2,3,6,7,8,9", Completed());
        Hb(Shot(4, animalState: 7));
        Assert.Equal("1,2,3,4,6,7,8,9", Completed());
        Hb(Shot(5, animalState: 14, weapon: 50));  // leaving a burrow, with the rifle
        Assert.Equal("1,2,3,4,6,7,8,9", Completed());
        Hb(Shot(6, animalState: 14, weapon: 51));
        Assert.Equal("1,2,3,4,5,6,7,8,9", Completed());
    }

    [Fact]
    public void TestWhileStandingIsThePlayersStanceAndAStandingAnimalIsTheAnimalsState()
    {
        state = New("standing.db", Catalogue(
        [
            "Harvest a charging Test Deer while standing.",         // 1
            "Harvest a Test Deer with one shot while standing up",  // 2
            "Harvest a standing Test Deer",                         // 3
        ]));
        ActivateAll();
        Hb(Shot(1, animalState: 9, pose: 2049));  // charging, crouching
        Assert.Equal("", Completed());
        Hb(Shot(2, animalState: 9, pose: 2048));  // charging, upright
        Assert.Equal("1,2", Completed());
        Hb(Shot(3, animalState: 3, pose: 2049));  // standing animal, crouching player
        Assert.Equal("1,2,3", Completed());
    }

    [Fact]
    public void TestWeightRangeAndScoreWordsAndTheWordsThatOnlyNameTheGender()
    {
        state = New("numbers.db", Catalogue(
        [
            "Harvest a Test Deer with a weight below 100",                                                            // 1 kilograms
            "Harvest a Test Deer at a range of 59.43m - 62.49m (approx. 195 - 205 ft.)",                              // 2
            "Harvest a Test Rabbit bringing in a total score of at least 800",                                        // 3
            "Spot a fine female Plains Elk",                                                                          // 4
            "Spot a dandy male Plains Elk",                                                                           // 5
            "Harvest a Test Deer weighing at least 200 Test libras (or 7,2 kg, approx. 15,4 lbs.)",                   // 6 the kilograms it gives
        ]));
        ActivateAll();
        Hb(Shot(1, grams: 150000, distance: 15000));
        Assert.Equal("6", Completed());
        Hb(Shot(2, grams: 90000, distance: 6000));
        Assert.Equal("1,2,6", Completed());
        Hb(Shot(3, species: 17, grams: 700));
        Assert.Equal("1,2,6", Completed());
        Hb(Shot(4, species: 17, grams: 1000));
        Hb(Spot(5, species: 3, gender: 0));
        Assert.Equal("1,2,3,5,6", Completed());
        Hb(Spot(6, species: 3, gender: 1));
        Assert.Equal("1,2,3,4,5,6", Completed());
    }

    [Fact]
    public void TestAWordThatIsStillNotReadKeepsTheObjectiveOpenWhateverIsInFrontOfIt()
    {
        state = New("unread.db", Catalogue(["At any point during the same hunt, harvest a Test Deer while whistling", "Lastly, take a Test Deer using a gizmo", "Harvest an Elk while whistling"]));
        ActivateAll();
        Hb(Shot(1, species: 3));
        Hb(Shot(2));
        Assert.Equal("", Completed());
        Assert.Equal("missions fully readable: 0 of 3", state.MissionCoverage().ElementAt(2));
    }

    [Fact]
    public void TestWhatThisVersionReadsIsFinishedAtTheStartFromTheHarvestsAlreadyMade()
    {
        state = New("late.db", Catalogue(["Kill a Test Deer while whistling"]));  // an earlier version could not read it
        ActivateAll();
        Hb(Shot(1));
        Assert.Equal("", Completed());
        var wallet = Gm();
        var later = New("late.db", Catalogue(["At any point during the same hunt, kill a Test Deer"]));
        Assert.Equal(1, later.ConfirmMissions());
        Assert.Equal(("1", wallet + 10), (Completed(of: later), Gm(later)));
    }

    [Fact]
    public void TestFromTowerNIsATowerStandAtThePlaceOfThatTowerAndOnAnyMapSaysNothing()
    {
        state = New("tower.db", Catalogue(
        [
            "Harvest a Test Deer from Tower 2 in Test Reserve",      // 1 the tower of the title's reserve
            "Harvest a Test Deer from Tower 2 (x 100, y 200).",      // 2 of the mission's reserve; its place in brackets
            "Harvest a Test Deer from Tower 2 (the central tower)",  // 3 a note in brackets
            "Harvest a Test Deer from Tower 2 (x 900, y 200)",       // 4 another place: not understood
            "Harvest a Test Deer from Tower 7 in Test Reserve",      // 5 no such tower: not understood
            "Harvest any Test Deer on any map.",                     // 6
        ], extra: ", \"reserves\": [2]"));
        Launch();
        ActivateAll();
        Hb(Shot(1, location: 1));  // from another tower
        Assert.Equal("6", Completed());
        Hb(Enter("TEST_HUNTING_TOWER_1"));
        Hb(Shot(2));  // at the tower, on the ground
        Assert.Equal("6", Completed());
        Hb(Shot(3, location: 1));
        Assert.Equal("1,2,3,6", Completed());
        Assert.Equal(["mission 4 objective 0: Harvest a Test Deer from Tower 2 (x 900, y 200)", "mission 5 objective 0: Harvest a Test Deer from Tower 7 in Test Reserve"],
            state.MissionCoverage().SkipWhile(l => l != "objectives not understood, by mission:").Skip(1).TakeWhile(l => l != "warnings:"));
    }
}
