using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>What a mission objective's title asks of the kill (weapon, ammunition, shot, harvest value, distance, score, weight, state, stand, reserve, a named animal),
/// an objective the server does not understand, the coverage report, what a later version confirms, and the rollback of what the lax rules finished. Invented catalogue and events.</summary>
public class MissionConditionTests : MissionTestBase
{
    // ids 1 to n in this order; each is one objective that is its title
    static readonly string[] Titles =
    [
        "Harvest a Test Deer with the Test Carbine",                                                       // 1 weapon
        "Harvest a Test Deer using Test Round ammunition",                                                 // 2 ammunition
        "Harvest a Test Deer with one shot",                                                               // 3
        "Harvest a Test Deer with a double lung shot",                                                     // 4
        "Harvest a Test Deer at 100% Harvest Value",                                                       // 5
        "Harvest a Test Deer from over 100 meters (Approx. 328 feet)",                                     // 6
        "Harvest a Test Deer from under 40 meters",                                                        // 7
        "Harvest a Test Deer weighing at least 80 kg (approx. 176 lbs)",                                   // 8
        "Harvest an airborne Test Deer",                                                                   // 9
        "Harvest an unspooked Test Deer",                                                                  // 10
        "Harvest a Test Deer from a Tree Stand",                                                           // 11
        "Harvest a Test Deer in Test Reserve",                                                             // 12
        "Harvest \"Patient Zero\" by the large beach (X: 6.273, Y: 6.330) at over 100 meters with 100% Harvest Value.",  // 13 a named animal
        "Harvest a Test Deer without a scope",                                                             // 14
        "Harvest a Test Deer while whistling",                                                             // 15 not understood
        "Harvest a Test Deer using any shotgun",                                                           // 16 a weapon class
        "Harvest an airborne Test Deer using Test Round ammunition",                                       // 17 both
        "Harvest a Test Deer with a heart shot",                                                           // 18
    ];

    [Fact]
    public void TestAnObjectiveIsMetOnlyByAKillThatMeetsEveryConditionOfItsTitle()
    {
        state = New("conditions.db", Catalogue(Titles));
        ActivateAll();
        Hb(Shot(1));  // the Test Carbine and Test Round, one shot of both lungs at 150 m, calm, no scope, 100 % value, 90 kg
        Assert.Equal("1,2,3,4,5,6,8,10,14", Completed());
        Hb(Shot(2, weapon: 51, ammunition: 53, distance: 2000, organ: 1, animalState: 6, location: 2, scope: 7, value: 80000, grams: 50000, hits: 2));  // a shotgun, heart, in the air, from a tree stand
        Assert.Equal("1,2,3,4,5,6,7,8,9,10,11,14,16,18", Completed());
        Hb(Shot(3, animalState: 6));  // the Test Round, in the air: 17 needs both
        Assert.Equal("1,2,3,4,5,6,7,8,9,10,11,14,16,17,18", Completed());
        state.NoteLaunch(ME, "start=101&reserve=2"u8.ToArray());
        Hb(Shot(4));
        Assert.Equal("1,2,3,4,5,6,7,8,9,10,11,12,14,16,17,18", Completed());
        Hb(Shot(5, special: 0, x: 0, z: 0));  // an ordinary animal at 150 m and 100 %, but elsewhere, is not "Patient Zero"
        Hb(Shot(7, special: 0, distance: 8000, x: 6300, z: 6300));  // at the place, under 100 m
        Assert.DoesNotContain("13", Completed().Split(','));
        Hb(Shot(6, special: 0, x: 6300, z: 6300));  // an ordinary animal of the mission's kind at the place, over 100 m, with 100 % value
        Assert.Contains("13", Completed().Split(','));
        Assert.DoesNotContain("15", Completed().Split(','));  // "while whistling" is not understood: no kill meets it
    }

    [Fact]
    public void TestAWeaponNamedByATitleAlsoAcceptsTheLongerNamesThatHoldItsWordsAndTheWeaponsOfTheMissionsText()
    {
        // 1 to 3: a revolver, a bow ("any") and a rifle named by their shorter names; 4: the longer name, which stays exact
        state = New("longer.db", Catalogue(["Harvest a Test Deer with the Test Revolver", "Harvest a Test Deer with any Test Recurve Bow", "Harvest a Test Deer with the Test Bolt Rifle",
            "Harvest a Test Deer with the Test Heavy Recurve Bow"]));
        ActivateAll();
        Hb(Shot(1, weapon: 101));
        Hb(Shot(2, weapon: 103));
        Hb(Shot(3, weapon: 105));
        Assert.Equal("1,2,3", Completed());  // the short names are met by themselves
        Hb(Shot(4, weapon: 102));
        Hb(Shot(5, weapon: 104));
        Hb(Shot(6, weapon: 106));
        Assert.Equal("1,2,3,4", Completed());
        state = New("longer2.db", Catalogue(["Harvest a Test Deer with the Test Revolver", "Harvest a Test Deer with any Test Recurve Bow", "Harvest a Test Deer with the Test Bolt Rifle",
            "Harvest a Test Deer with the Test Heavy Recurve Bow"]));
        ActivateAll();
        Hb(Shot(1, weapon: 102));  // the longer names meet the shorter titles
        Hb(Shot(2, weapon: 104));
        Hb(Shot(3, weapon: 106));
        Assert.Equal("1,2,3,4", Completed());
        state = New("longer3.db", Catalogue(["Harvest a Test Deer with the Test Magnum Revolver"], ", \"desc\": \"Use a Test Revolver\""));
        ActivateAll();
        Hb(Shot(1, weapon: 101));  // a title naming the longer name is exact, whatever the text names
        Assert.Equal("", Completed());
        state = New("longer4.db", Catalogue(["Harvest a Test Deer with the Test Revolver"], ", \"weapons\": [60], \"desc\": \"Use a Test Carbine\""));
        ActivateAll();
        Hb(Shot(2, weapon: 60));  // the mission's weapons list is not read; a weapon of neither is refused
        Assert.Equal("", Completed());
        Hb(Shot(1, weapon: 50));  // the weapon the text names
        Assert.Equal("1", Completed());
        state = New("longer5.db", Catalogue(["Harvest a Test Deer with the Test 30 30 Lever Rifle"]));
        ActivateAll();
        Hb(Shot(1, weapon: 108));  // a repeated word (30 30) is not held by 30 06: each word is used once
        Assert.Equal("", Completed());
        state = New("longer6.db", Catalogue(["Harvest a female Test Deer with the Test Revolver"]));
        ActivateAll();
        Hb(Shot(1, weapon: 102, gender: 0));  // a male
        Assert.Equal("", Completed());
        Hb(Shot(2, weapon: 50, gender: 1));  // a female, another weapon
        Assert.Equal("", Completed());
        Hb(Shot(3, weapon: 102, gender: 1));
        Assert.Equal("1", Completed());
    }

    [Fact]
    public void TestAHarvestAtShortRangeOrOfAnOrdinaryAnimalDoesNotFinishAMissionWithoutItsConditions()
    {
        state = New("short.db", Catalogue([Titles[5], Titles[12], Titles[3]]));  // over 100 m; "Patient Zero" over 100 m at 100 %; a double lung shot
        ActivateAll();
        Hb(Shot(1, distance: 9000, organ: 4));  // 90 m, one lung
        Assert.Equal("", Completed());
        Hb(Shot(2, distance: 20000, organ: 6, special: 0));  // 200 m, but ordinary: the named animal's mission stays open
        Assert.Equal("1,3", Completed());
    }

    [Fact]
    public void TestTheScoreAndTheHarvestValueAreBothNeeded()
    {
        state = New("score.db", Catalogue(["Harvest a male Test Rabbit at 100% Harvest Value with a score of 800 or higher"]));
        ActivateAll();
        Hb(Shot(1, species: 17, value: 50000, grams: 1000));  // a rabbit scores its weight in grams: 1000
        Assert.Equal("", Completed());
        Hb(Shot(2, species: 17, value: 100000, grams: 700));
        Assert.Equal("", Completed());
        Hb(Shot(3, species: 17, value: 100000, grams: 1000));
        Assert.Equal("1", Completed());
    }

    [Fact]
    public void TestAnObjectiveWithWordsTheServerDoesNotReadIsNeverMetAndTheCoverageCountsIt()
    {
        state = New("coverage.db", Catalogue(["Harvest a Test Deer", "Harvest a Test Deer while whistling", "Take a Test Deer to a friend"]));
        ActivateAll();
        Hb(Shot(1));
        Assert.Equal("1", Completed());
        var report = state.MissionCoverage().ToList();
        Assert.Equal("missions: 3, objectives: 3", report[0]);
        Assert.Equal("understood: 1, not understood: 2 (never met until a version reads them)", report[1]);
        Assert.Equal("missions fully readable: 1 of 3", report[2]);
        Assert.Equal(["objectives not understood, by mission:", "mission 2 objective 0: Harvest a Test Deer while whistling", "mission 3 objective 0: Take a Test Deer to a friend"], report[^3..]);
    }

    [Fact]
    public void TestWhatAVersionUnderstandsLaterIsFinishedAtItsStartWithItsRewardsAndOnlyFromTheHarvestsAfterTheActivation()
    {
        state = New("later.db", Catalogue(["Harvest a Test Deer using a gizmo"]));
        Hb(Shot(1));  // before the activation
        ActivateAll();
        Hb(Shot(2, weapon: 51));
        Hb(Shot(3));  // meets the carbine once it is understood
        var wallet = Gm();
        Assert.Equal("", Completed());  // the server does not understand "a gizmo": the objective is open, its harvest kept
        var later = New("later.db", Catalogue(["Harvest a Test Deer with the Test Carbine"]));
        Assert.Equal(1, later.ConfirmMissions());
        Assert.Equal(("1", wallet + 10), (Completed(of: later), Gm(later)));  // paid as at that harvest
        Assert.Equal(0, later.ConfirmMissions());  // once
        Assert.Equal(wallet + 10, Gm(later));
    }

    [Fact]
    public void TestAMissionThatWaitsIsCompletedOnlyByTheHarvestsOfItsOrderAndOfOneHunt()
    {
        // two objectives, none understood yet; the single-hunt mission counts the latest hunt only
        const string Two = """, "singleExpedition": true""";
        string Missions(string a, string b) => "{\"missions\": [{\"id\": 1, \"title\": \"Two\", \"dependencies\": [], \"rewards\": [{\"text\": \"10 gm$\"}]" + Two
            + $", \"objectives\": [{{\"id\": 0, \"title\": \"{a}\"}}, {{\"id\": 1, \"title\": \"{b}\"}}]}}]}}";
        state = New("hunts.db", Missions("Harvest a Test Deer using a gizmo", "Harvest a Test Deer using a gadget"));
        ActivateAll();
        state.NoteLaunch(ME, "start=101&reserve=2"u8.ToArray());
        Hb(Shot(1));
        state.NoteLaunch(ME, "start=101&reserve=2"u8.ToArray());
        Hb(Shot(2));
        var later = New("hunts.db", Missions("Harvest a Test Deer with the Test Carbine", "Harvest a Test Deer using Test Round ammunition"));
        Assert.Equal(1, later.ConfirmMissions());  // the second hunt's kill finishes the first objective; the first hunt's is forgotten, as its progress would have been
        Eq("""{"state": 2, "objectives": [0]}""", Reply(later.MissionsReply(ME)!)["states"]!["1"]);
    }

    [Fact]
    public void TestTheRollbackReopensWhatNoHarvestMeetsKeepsTheWalletAndWhatTheyUnlockedAndNeverTouchesARestoredMission()
    {
        // the catalogue of the earlier version asks nothing of the kill; the stricter one asks a distance of 5 and 4, and 6 needs 5 (not 1 or 2: no mission needs those)
        string Rollback(bool strict) => "{\"missions\": [" + string.Join(",", new[] { (5, "Harvest a Test Deer from over 100 meters", "[]"), (6, "Harvest a Test Deer", "[5]"), (3, "Harvest a Test Deer", "[]"), (4, "Harvest a Test Deer from under 40 meters", "[]") }
            .Select(m => $"{{\"id\": {m.Item1}, \"title\": \"x\", \"dependencies\": {(strict ? m.Item3 : "[]")}, \"rewards\": [{{\"text\": \"10 gm$\"}}], \"objectives\": [{{\"id\": 0, \"title\": \"{(strict ? m.Item2 : "Harvest a Test Deer")}\"}}]}}")) + "]}";
        state = New("rollback.db", Rollback(strict: false));
        state.MissionCall(ME, "activate", Q("mission_id=5"));
        state.MissionCall(ME, "activate", Q("mission_id=4"));
        Hb(Shot(1, distance: 2000));  // 20 m: the lax rules finish 5 and 4
        Assert.Equal("4,5", Completed());
        var wallet = Gm();
        // a restored account: mission 5 was completed before the restore
        state.CreateAccount(8, "second");
        using (var con = state.Store.Open(8))
        {
            con.Begin();
            PlayerRows.SetExtra(con, "Mission_missions.json", O("""{"active": [], "completed": [5], "states": {"5": {"state": 3}}}"""));
            con.Commit();
        }
        Assert.Equal("5", Completed(8));

        var strict = New("rollback.db", Rollback(strict: true));
        var dry = strict.RollbackMissions(apply: false).ToList();
        Assert.StartsWith($"account {ME}: mission 5 (completed, active again) objectives 0", dry[0]);
        Assert.Equal("would reopen 1 objectives in 1 missions; kept: not understood 0 finished objectives (dry run: nothing written, --apply writes)", dry[^1]);
        Assert.Equal("4,5", Completed(of: strict));  // nothing was written
        var done = strict.RollbackMissions(apply: true).ToList();
        Assert.Equal("reopened 1 objectives in 1 missions; kept: not understood 0 finished objectives", done[^1]);
        Assert.Equal("4", Completed(of: strict));
        Assert.Equal("5,6", string.Join(",", Reply(strict.MissionsReply(ME)!)["active"]!.AsArray().Select(i => (long)i!)));  // 6 had become available with 5: it stays open to the player
        Eq("""{"state": 2, "objectives": []}""", Reply(strict.MissionsReply(ME)!)["states"]!["5"]);
        Assert.Equal(wallet, Gm(strict));  // nothing is taken back
        Assert.Equal("5", Completed(8, strict));  // a restored completion has no events to check
        using (var con = strict.Store.Open(ME))
            Eq("""{"reopened": [{"mission": 5, "was": "completed", "objectives": [0]}], "activated": [6]}""", JsonNode.Parse((string)con.Scalar("SELECT record FROM mission_rollbacks WHERE account_id = $a")!));
        Assert.Equal("reopened 0 objectives in 0 missions; kept: not understood 0 finished objectives", strict.RollbackMissions(apply: true).Last());  // once
    }

    [Fact]
    public void TestTheSchemaOfVersion29GetsTheMissionRowsTheirMarks()
    {
        state = New("v29.db", Catalogue(["Harvest a Test Deer"]));
        Hb(Shot(1));
        state.MissionCall(ME, "activate", Q("mission_id=1"));
        state.CreateAccount(8, "second");
        using (var con = state.Store.Open(8))
        {
            con.Begin();
            PlayerRows.SetExtra(con, "Mission_missions.json", O("""{"active": [2], "completed": [1], "states": {"1": {"state": 3}, "2": {"state": 2, "objectives": [0, 3]}}}"""));
            con.Commit();
        }
        state.MissionsReply(8);
        using (var con = state.Store.Open(8))
            con.Exec("UPDATE missions SET state = 3, objectives = '[]' WHERE account_id = $a AND mission_id = 2");  // the restored active mission was completed here
        using (var con = state.Store.Open())
        {
            foreach (var column in new[] { "since", "seen", "kept", "earlier" })
                con.Exec($"ALTER TABLE missions DROP COLUMN {column}");
            con.Exec("DROP TABLE mission_rollbacks");
            con.Exec("PRAGMA user_version = 29");
        }
        var store = new Store(Path.Combine(tmp, "v29.db"));
        var rows = new List<string>();
        foreach (var account in new long[] { ME, 8 })
            using (var con = store.Open(account))
                rows.AddRange(con.Rows("SELECT account_id, mission_id, since, seen, kept, earlier FROM missions WHERE account_id = $a ORDER BY mission_id")
                    .Select(r => string.Join("/", r.Select(v => v is DBNull ? "-" : v.ToString()))));
        Assert.Equal([$"{ME}/1/-/3/[]/[]", "8/1/-/0/*/[]", "8/2/-/0/[0,3]/[]"],  // the objectives a restore brought are kept even in a mission completed since
             rows.Select(r => r.Replace(" ", "")));  // 3: the last event of the account
        using (var con = store.Open())
            Assert.Equal((long)Store.Version, con.Scalar("PRAGMA user_version"));
    }

    [Fact]
    public void TestPermittedAmmunitionScopeWeightInPoundsARangeOfMetresAndASpookedAnimal()
    {
        state = New("more.db", Catalogue(["Harvest a Test Deer using permitted ammunition", "Harvest a scoped Test Deer", "Harvest a Test Deer weighing at least 176 lbs",
                                          "Harvest a Test Deer from between 30 and 50 meters", "Harvest a spooked Test Deer"]), permitted: [52]);
        ActivateAll();
        Hb(Shot(1, ammunition: 53, scope: -1, distance: 6000, grams: 70000));  // Test Slug Shells are not permitted on a deer; no scope; 60 m; 70 kg; calm
        Assert.Equal("", Completed());
        Hb(Shot(2, ammunition: 52, scope: 7, distance: 4000, grams: 80000, animalState: 5));  // permitted, scoped, 40 m, 80 kg (176 lbs is 79.8 kg), running
        Assert.Equal("1,2,3,4,5", Completed());
    }

    [Fact]
    public void TestABirdInTheAirIsSpookedWithState6AndCalmWithState11AtItsFirstHit()
    {
        state = New("birds.db", Catalogue(["Harvest a spooked airborne Test Mallard", "Harvest an unspooked airborne Test Mallard", "Harvest a fleeing Test Mallard",  // 1, 2, 3
                                           "Harvest a spooked airborne Spring Pintail", "Harvest an unspooked airborne Spring Pintail",                              // 4, 5
                                           "Harvest a Test Mallard. The Test Mallard should not be running away or charging you."]));                              // 6
        ActivateAll();
        Hb(Shot(1, species: 7, animalState: 6));  // flying, as the birds are after a shot at them: spooked and fleeing, so it is running away
        Assert.Equal("1,3", Completed());
        Hb(Shot(2, species: 7, animalState: 11));  // flying and not spooked: the first shot at birds nobody shot at
        Assert.Equal("1,2,3,6", Completed());

        static JsonNode Hit(int animalState) => O($$"""
            {"event": 1, "individual": 3, "weapon_id": 51, "ammunition_id": 53, "distance_cms": 3000, "organ1": 33, "organ2": 1, "animal_state": {{animalState}}, "location": 0, "scope_id": -1,
             "pose": 0, "species": 7}
            """);
        Hb(O("""{"event": 7, "individual": 3, "species": 6, "gender": 0, "weight_grams": 900, "texture": 0, "special": 0}"""), Hit(11), Hit(6),
           O("""{"event": 3, "individual": 3, "extended_species": 6, "harvest_value": 100000, "trophy_integrity": 100000}"""));
        Assert.Equal("1,2,3,5,6", Completed());  // calm at its first hit, which is the one that tells: the second finds it flushed by the first
    }

    [Fact]
    public void TestANamedAnimalIsTheMissionsSpeciesNearItsCoordinatesAndASpotReadsItsStand()
    {
        state = New("named.db", Catalogue(["Harvest \"Patient Zero\" by the large beach (X: 6.273, Y: 6.330)", "Spot a Test Deer from a Tree Stand", "Spot a Test Deer without spooking it"], ", \"species\": [1]"));
        ActivateAll();
        Hb(Shot(1, special: 9, x: 0, z: 0));  // named, but 8 km away
        Hb(Shot(2, special: 9, x: 6800, z: 6000, species: 17));  // named, near, not the mission's species
        Assert.Equal("", Completed());
        Hb(Shot(3, special: 9, x: 6800, z: 6000));
        Assert.Equal("1", Completed());
        Hb(O("""{"event": 7, "individual": 40, "species": 1, "gender": 0, "weight_grams": 50000, "texture": 0}"""),
           O("""{"event": 11, "individual": 40, "milliseconds_viewed": 3000, "percentage": 80, "location": 0}"""));  // from the ground
        Assert.Equal("1", Completed());
        Hb(O("""{"event": 11, "individual": 40, "milliseconds_viewed": 3000, "percentage": 80, "location": 2}"""));  // from a tree stand; "without spooking it" is not read
        Assert.Equal("1,2", Completed());
    }

    [Fact]
    public void TestAMissionOfAnEarlierSchemaDoesNotGiveALaterHarvestToAnObjectiveItHadFinished()
    {
        string Two(string second) => "{\"missions\": [{\"id\": 1, \"title\": \"Two\", \"dependencies\": [], \"rewards\": [{\"text\": \"10 gm$\"}], \"objectives\": ["
            + $"{{\"id\": 0, \"title\": \"Harvest a Test Deer\"}}, {{\"id\": 1, \"title\": \"{second}\"}}]}}]}}";
        state = New("old.db", Two("Harvest a Test Deer using a gizmo"));
        ActivateAll();
        Hb(Shot(1));  // finishes the first objective
        using (var con = state.Store.Open())
        {
            foreach (var column in new[] { "since", "seen", "kept", "earlier" })
                con.Exec($"ALTER TABLE missions DROP COLUMN {column}");
            con.Exec("DROP TABLE mission_rollbacks");
            con.Exec("PRAGMA user_version = 29");
        }
        state = New("old.db", Two("Harvest a Test Deer using a gizmo"));  // schema 30: the finished objective is earlier
        Hb(Shot(2));  // meets the carbine, but the server does not read the second objective yet
        Assert.Equal("", Completed());
        var later = New("old.db", Two("Harvest a Test Deer with the Test Carbine"));
        Assert.Equal(1, later.ConfirmMissions());  // this harvest is the second objective's, not the first's again
        Assert.Equal("1", Completed(of: later));
    }

    [Fact]
    public void TestTheRollbackOfARowOfAnEarlierSchemaKeepsWhatItFinishedEarlierAndNotWhatItReopened()
    {
        // mission 1 has three objectives (the first over 100 m once the rules read it), mission 2 two; every harvest is at 20 m
        string Catalogue(bool strict) => "{\"missions\": ["
            + $"{{\"id\": 1, \"title\": \"Three\", \"dependencies\": [], \"rewards\": [{{\"text\": \"10 gm$\"}}], \"objectives\": [{{\"id\": 0, \"title\": \"Harvest a Test Deer{(strict ? " from over 100 meters" : "")}\"}}, "
            + "{\"id\": 1, \"title\": \"Harvest a Test Deer\"}, {\"id\": 2, \"title\": \"Harvest a Test Deer\"}]},"
            + $"{{\"id\": 2, \"title\": \"Two\", \"dependencies\": [], \"rewards\": [{{\"text\": \"10 gm$\"}}], \"objectives\": [{{\"id\": 0, \"title\": \"Harvest a Test Deer{(strict ? " from over 100 meters" : "")}\"}}, "
            + "{\"id\": 1, \"title\": \"Harvest a Test Deer\"}]}]}";
        state = New("earlier.db", Catalogue(strict: false));
        ActivateAll();
        Hb(Shot(1, distance: 2000));
        Hb(Shot(2, distance: 2000));  // mission 2 is completed, mission 1 has two objectives finished
        Assert.Equal("2", Completed());
        using (var con = state.Store.Open())
        {
            foreach (var column in new[] { "since", "seen", "kept", "earlier" })
                con.Exec($"ALTER TABLE missions DROP COLUMN {column}");
            con.Exec("DROP TABLE mission_rollbacks");
            con.Exec("PRAGMA user_version = 29");
        }
        var strict = New("earlier.db", Catalogue(strict: true));
        Assert.Equal("reopened 2 objectives in 2 missions; kept: not understood 0 finished objectives", strict.RollbackMissions(apply: true).Last());
        using var check = strict.Store.Open(ME);
        Assert.Equal(["1/2/[1]/[1]", "2/2/[1]/[1]"], check.Rows("SELECT mission_id, state, objectives, earlier FROM missions WHERE account_id = $a ORDER BY mission_id")
            .Select(r => string.Join("/", r.Select(v => v.ToString())).Replace(" ", "")));  // the first objective is open and not earlier: a harvest after the upgrade can finish it
    }

    [Fact]
    public void TestAHarvestAfterTheUpgradeIsNotPaidTwiceByTheRollbackAndTheStartThatFollows()
    {
        string Two(bool strict) => "{\"missions\": [{\"id\": 1, \"title\": \"Two\", \"dependencies\": [], \"rewards\": [{\"text\": \"10 gm$\"}], \"objectives\": ["
            + $"{{\"id\": 0, \"title\": \"Harvest a Test Deer{(strict ? " from over 50 meters" : "")}\"}}, {{\"id\": 1, \"title\": \"Harvest a Test Deer{(strict ? " from over 100 meters" : "")}\"}}]}}]}}";
        state = New("twice.db", Two(strict: false));
        ActivateAll();
        Hb(Shot(1, distance: 2000));  // 20 m: the earlier rules finish the first objective
        using (var con = state.Store.Open())
        {
            foreach (var column in new[] { "since", "seen", "kept", "earlier" })
                con.Exec($"ALTER TABLE missions DROP COLUMN {column}");
            con.Exec("DROP TABLE mission_rollbacks");
            con.Exec("PRAGMA user_version = 29");
        }
        var strict = New("twice.db", Two(strict: true));
        state = strict;
        Hb(Shot(2, distance: 15000));  // 150 m, after the upgrade: finishes the second objective and the mission
        Assert.Equal("1", Completed(of: strict));
        var wallet = Gm(strict);
        Assert.Equal("reopened 1 objectives in 1 missions; kept: not understood 0 finished objectives", strict.RollbackMissions(apply: true).Last());  // the 150 m harvest belongs to the first objective: the second is reopened
        Assert.Equal(0, strict.ConfirmMissions());  // and that harvest is not given to the second again
        Eq("""{"state": 2, "objectives": [0]}""", Reply(strict.MissionsReply(ME)!)["states"]!["1"]);
        Assert.Equal(wallet, Gm(strict));
    }

    [Fact]
    public void TestTheRollbackKeepsAFinishedObjectiveItCannotJudge()
    {
        // the earlier rules finished both; the stricter reader understands the first (over 100 m) and not the second ("while whistling")
        string Catalogue(bool strict) => "{\"missions\": [{\"id\": 1, \"title\": \"Two\", \"dependencies\": [], \"rewards\": [{\"text\": \"10 gm$\"}], \"objectives\": ["
            + $"{{\"id\": 0, \"title\": \"Harvest a Test Deer{(strict ? " from over 100 meters" : "")}\"}}, {{\"id\": 1, \"title\": \"Harvest a Test Deer{(strict ? " while whistling" : "")}\"}}]}}]}}";
        state = New("judge.db", Catalogue(strict: false));
        ActivateAll();
        Hb(Shot(1, distance: 2000));
        Hb(Shot(2, distance: 2000));
        Assert.Equal("1", Completed());
        var strict = New("judge.db", Catalogue(strict: true));
        var dry = strict.RollbackMissions(apply: false).ToList();
        Assert.Equal("would reopen 1 objectives in 1 missions; kept: not understood 1 finished objectives (dry run: nothing written, --apply writes)", dry[^1]);
        Assert.Contains("mission 1 (completed, active again) objectives 0", dry[0]);
        Assert.DoesNotContain("objectives 0, 1", dry[0]);
        strict.RollbackMissions(apply: true).ToList();
        Eq("""{"state": 2, "objectives": [1]}""", Reply(strict.MissionsReply(ME)!)["states"]!["1"]);  // the one it cannot judge stays finished
    }

    [Fact]
    public void TestAnObjectiveTheRollbackCannotJudgeDoesNotBlockTheOrderedOneBehindIt()
    {
        // the unread objective is first; the ordered one behind it is met by the stored harvest, so it must not be reopened
        string Catalogue(bool strict) => "{\"missions\": [{\"id\": 1, \"title\": \"Two\", \"dependencies\": [], \"rewards\": [{\"text\": \"10 gm$\"}], \"objectives\": ["
            + $"{{\"id\": 0, \"title\": \"Harvest a Test Deer{(strict ? " while whistling" : "")}\"}}, {{\"id\": 1, \"title\": \"Then, harvest a Test Deer{(strict ? " from under 40 meters" : "")}\"}}]}}]}}";
        state = New("ordered.db", Catalogue(strict: false));
        ActivateAll();
        Hb(Shot(1, distance: 2000));
        Hb(Shot(2, distance: 2000));
        Assert.Equal("1", Completed());
        var strict = New("ordered.db", Catalogue(strict: true));
        Assert.Equal("reopened 0 objectives in 0 missions; kept: not understood 1 finished objectives", strict.RollbackMissions(apply: true).Last());
        Assert.Equal("1", Completed(of: strict));
    }

    [Fact]
    public void TestAHarvestOnlyConditionInATrackEntryOfTheOperatorsFileIsRefusedAtTheStart()
    {
        var catalogue = Catalogue(["Find a Test Deer track"]);
        foreach (var rule in new[] { "track; species: Test Deer; clues: footprints x1; weapon: Test Carbine", "track; species: Test Deer; clues: footprints x1; one shot" })
            Assert.StartsWith("mission_rules.json \"1/0\": a track rule cannot check \"" + rule[(rule.LastIndexOf("; ", StringComparison.Ordinal) + 2)..] + "\"",
                Assert.Throws<InvalidDataException>(() => New("track.db", catalogue, rules: new JsonObject { ["1/0"] = rule })).Message);
    }

    [Fact]
    public void TestAClueTitleOfOneDifferentAnimalGivesTheRuleTheOperatorsFileAcceptsToo()
    {
        var catalogue = Catalogue(["ID 1 different Test Deer tracks"]);
        var line = New("one.db", catalogue).MissionCoverage(rules: true).Single(l => l.StartsWith("mission 1 objective 0:", StringComparison.Ordinal) && l.Contains(" => ", StringComparison.Ordinal));
        Assert.Equal("mission 1 objective 0: ID 1 different Test Deer tracks => track; species: Test Deer; clues: footprints x1", line);
        var written = New("one2.db", catalogue, rules: new JsonObject { ["1/0"] = line[(line.IndexOf("=> ", StringComparison.Ordinal) + 3)..] });  // the file takes the printed rule
        Assert.Contains("mission 1 objective 0 (override): ID 1 different Test Deer tracks => track; species: Test Deer; clues: footprints x1", written.MissionCoverage(rules: true));
    }

    static byte[] Q(string form) => Encoding.ASCII.GetBytes(form);
}
