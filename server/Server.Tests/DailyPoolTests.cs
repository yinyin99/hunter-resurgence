using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The shipped facts file's daily missions (not made-up ones): the missions added from the original's pool are readable, the calendar can pick each, and the MEDIUM row is the original's.</summary>
public class DailyPoolTests : StateTestBase
{
    // species id, name and define of the added missions' species (the catalogue's ids)
    static readonly (long Id, string Name, string Define)[] Species =
    [
        (37, "Grizzly Bear", "GRIZZLY_BEAR"), (38, "Rocky Mountain Elk", "ROCKY_MOUNTAIN_ELK"), (3, "Roosevelt Elk", "ROOSEVELT_ELK"), (26, "Sitka Deer", "SITKA_DEER"),
        (21, "Red Kangaroo", "RED_KANGAROO"), (34, "Rock Ptarmigan", "PTARMIGAN_ROCK"), (14, "Red Fox", "RED_FOX"), (44, "Rusa Deer", "RUSA_DEER"), (9, "Black Bear", "BLACK_BEAR"),
    ];

    // mission id, difficulty, species, gender, reserves, giver alias, giver name, picture
    static readonly (long Id, long Difficulty, string Species, long Gender, string Reserves, string Giver, string Name, string Image)[] Added =
    [
        (49, 1, "GRIZZLY_BEAR", 0, "Timbergold Trails", "BUCK_WILLIAMS", "Buck Williams", "sheriff_buck_williams.jpg"),
        (121, 1, "ROCKY_MOUNTAIN_ELK", 1, "Timbergold Trails", "BUCK_WILLIAMS", "Buck Williams", "sheriff_buck_williams.jpg"),
        (196, 1, "ROOSEVELT_ELK", 1, "Whitehart Island, Settler Creeks, Redfeather Falls", "CLINTON_DOUGLAS", "Clinton Douglas aka Bull", "clinton_douglas.jpg"),
        (273, 1, "SITKA_DEER", 0, "Whiterime Ridge", "IGSIVALITAQ", "Igsivalitaq", "igsivalitaq.jpg"),
        (291, 1, "RED_KANGAROO", 0, "Bushrangers Run", "LAURA_FRANCESE", "Laura Francese", "laura_francese.jpg"),
        (324, 1, "PTARMIGAN_ROCK", 0, "Val-des-Bois", "TAXIDERMIST_MAX", "Taxidermist Max", "taxidermist_max.jpg"),
        (335, 1, "RED_FOX", 1, "Hirschfelden, Hemmeldal, Val-des-Bois, Bushrangers Run", "TAXIDERMIST_MAX", "Taxidermist Max", "taxidermist_max.jpg"),
        (381, 2, "RUSA_DEER", 0, "Piccabeen Bay", "VERA_HERZSCHLAG", "Vera Herzschlag", "vera_herzschlag.jpg"),
        (510, 2, "BLACK_BEAR", 1, "Settler Creeks, Redfeather Falls, Rougarou Bayou", "ANGELICA_RODRIGUEZ", "Angelica Rodriguez", "angelica_rodriguez.jpg"),
    ];

    static readonly DateOnly D0 = new(2031, 3, 10);

    static JsonNode FactsJson() => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "facts", "facts.json")))!;

    State New(Facts facts)
    {
        var app = APP;
        foreach (var (id, name, define) in Species)
            app["species"]!.AsArray().Add(O($$"""{"id": {{id}}, "name": "{{name}}", "define": "{{define}}"}"""));
        var s = new State(Path.Combine(tmp, Guid.NewGuid().ToString("N") + ".db"), Py.DumpBytes(CONNECT), BASE, ICON, app, LOCALE, TEXTURES, offline: false, facts: facts);
        s.Clock = () => 0;
        return s;
    }

    /// <summary>Every mission the calendar shows for new accounts over enough dates, when every day of the cycle has this difficulty.</summary>
    Dictionary<long, JsonNode> Picked(long difficulty)
    {
        var json = FactsJson();
        json["daily_missions"]!["cycle"] = new JsonArray(difficulty);
        state = New(Facts.Parse(json.ToJsonString()));
        var picked = new Dictionary<long, JsonNode>();
        for (var i = 0; i < 40; i++)
        {
            state.Clock = () => (D0.AddDays(i).DayNumber - DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber) * 86400.0 + 43200;
            state.CreateAccount(100 + i, "player" + i);
            var today = Reply(state.DailyCalendarReply(100 + i)!)["calendar"]!.AsArray().Single(e => (bool)e!["today"]!)!;
            picked[(long)today["mission"]!["id"]!] = today;
        }
        return picked;
    }

    [Fact]
    public void TestTheCalendarCanPickEveryAddedMissionAsTheOriginalWritesIt()
    {
        var easy = Picked(1);
        var medium = Picked(2);
        foreach (var (id, difficulty, species, gender, reserves, giver, name, image) in Added)
        {
            var day = (difficulty == 1 ? easy : medium)[id];  // an unreadable template is left out, so it would never be here
            var (mission, character) = (day["mission"]!, day["character"]!);
            Eq($"""[{id}, "{species}", {gender}, "{reserves}", "{species}"]""", T(mission["id"], mission["species"], mission["gender"], mission["reserves"], day["species"]));
            Eq($$"""{"id": {{character["id"]}}, "name": "{{name}}", "alias": "{{giver}}", "description": "{{name}}", "image": "{{image}}"}""", character);
        }
    }

    [Fact]
    public void TestADroppingsClueAndAHarvestAreWrittenAsTheOriginalWritesThem()
    {
        Eq("""["CLUE", "a.species() == c.species.PTARMIGAN_ROCK and e.trackType() == c.clue.DROPPINGS"]""", Objective(Picked(1)[324], 0));
        Eq("""["CONFIRMKILL", "a.ethical() and a.killedBy(e.getUserId()) and a.species() == c.species.BLACK_BEAR and a.gender()== c.gender.FEMALE"]""", Objective(Picked(2)[510], 0));
        // two clues of one species in the same hunt: a male's, then a female's
        Eq("""["CLUE", "a.species() == c.species.RUSA_DEER and e.trackType() == c.clue.FOOTSTEPS and a.gender() == c.gender.MALE"]""", Objective(Picked(2)[381], 0));
        Eq("""["CLUE", "a.species() == c.species.RUSA_DEER and e.trackType() == c.clue.FOOTSTEPS and a.gender() == c.gender.FEMALE"]""", Objective(Picked(2)[381], 1));
    }

    static JsonNode Objective(JsonNode day, int index) => T(JsonNode.Parse((string)day["objectives"]!)![index]!["event_type"], JsonNode.Parse((string)day["objectives"]!)![index]!["objective_data"]!["conditions"]);

    [Fact]
    public void TestTheMediumRowIsTheOriginalsAndTheDefaultCycleShowsIt()
    {
        state = New(Facts.Parse(FactsJson().ToJsonString()));
        var day = Reply(state.DailyCalendarReply(ME)!)["calendar"]![3]!;  // the 4th day of the cycle
        Eq("""{"id": 2, "title": "Medium", "alias": "MEDIUM", "mission_cooldown": 20, "color": "blue"}""", day["difficulty"]);
        Eq("""[131, "difficulty", 2, "SILVER", 500]""", T(day["rewards"]![0]!["id"], day["rewards"]![0]!["root_type"], day["rewards"]![0]!["root_id"], day["rewards"]![0]!["reward_type"], day["rewards"]![0]!["amount"]));
    }
}
