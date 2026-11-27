using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>#974: the owner of a lodge with 75 mounted trophies or more gets one inbox message, once, naming the largest.</summary>
public class BigLodgeNoticeTests : StateTestBase
{
    const long OWNER = 8, OTHER = 9;

    public BigLodgeNoticeTests()
    {
        state = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false);
        state.CreateAccount(OWNER, "owner");
        state.CreateAccount(OTHER, "other");
    }

    /// <summary>`mounted` slots with a trophy, then `empty` slots without one.</summary>
    static string Slots(int mounted, int empty = 0) =>
        "[" + string.Join(",", Enumerable.Range(0, mounted).Select(i => $$"""{"slot_id": {{i}}, "trophy_id": {{1000 + i}}}""")
            .Concat(Enumerable.Range(mounted, empty).Select(i => $$"""{"slot_id": {{i}}, "trophy_id": null}"""))) + "]";

    void AddLodge(long account, int id, string name, int mounted, int empty = 0)
    {
        using var con = state.Store.Open(account);
        con.Begin();
        var lodge = JsonNode.Parse($$"""{"id": {{id}}, "name": "{{name}}", "animal_slots": {{Slots(mounted, empty)}}, "weapon_slots": []}""")!;
        PlayerRows.AddLodge(con, "/game/user/lodges", id.ToString(), new JsonObject { ["id"] = id, ["name"] = name });
        PlayerRows.AddLodge(con, "/game/user/lodge", id.ToString(), lodge);
        con.Commit();
    }

    List<string> Notices(long account)
    {
        using var con = state.Store.Open(account);
        return [.. con.Rows("SELECT content FROM messages WHERE account_id = $a AND sender = ? AND content LIKE 'Your trophy lodge %' ORDER BY id", State.SYSTEM_SENDER).Select(r => (string)r[0])];
    }

    const string Expected = " trophies. The game keeps a lodge's trophies in memory after you leave it, and with about 100 or more it can close during the next hunt. "
        + "To avoid it: keep a lodge under about 60 trophies (move some to another lodge), or restart the game after visiting it before you hunt.";

    [Fact]
    public void AnOwnerOfA75TrophyLodgeGetsTheMessageAtStartAndOneOf74DoesNot()
    {
        AddLodge(OWNER, 3, "Big Cabin", 75);
        AddLodge(OTHER, 4, "Almost", 74);
        state.BackfillLodges();
        Assert.Equal(["Your trophy lodge Big Cabin holds 75" + Expected], Notices(OWNER));
        Assert.Empty(Notices(OTHER));
    }

    [Fact]
    public void OnlyMountedTrophiesCountNotEmptySlots()
    {
        AddLodge(OWNER, 3, "Roomy", 74, empty: 60);
        state.BackfillLodges();
        Assert.Empty(Notices(OWNER));
    }

    [Fact]
    public void AStartARestoreAndASaveSendItOnlyOnceAndAnotherLodgeLaterDoesNotSendAgain()
    {
        AddLodge(OWNER, 3, "Big Cabin", 80);
        state.BackfillLodges();
        state.BackfillLodges();  // a restart
        state.BackfillLodges(OWNER);  // a restore
        Assert.Single(Notices(OWNER));
        AddLodge(OWNER, 4, "Bigger", 120);
        Assert.Equal(200, state.SaveLodge(OWNER, 4, Encoding.UTF8.GetBytes($$"""{"animal_slots": {{Slots(130)}}}""")));
        state.BackfillLodges();
        Assert.Single(Notices(OWNER));
    }

    [Fact]
    public void ALodgeThatReaches75ThroughASaveSendsItThenAndTheMessageNamesTheLargestLodge()
    {
        AddLodge(OWNER, 3, "Small", 20);
        AddLodge(OWNER, 4, "Medium", 60);
        state.BackfillLodges();
        Assert.Empty(Notices(OWNER));
        Assert.Equal(200, state.SaveLodge(OWNER, 4, Encoding.UTF8.GetBytes($$"""{"animal_slots": {{Slots(74)}}}""")));
        Assert.Empty(Notices(OWNER));
        AddLodge(OWNER, 5, "Second", 90);
        Assert.Equal(200, state.SaveLodge(OWNER, 4, Encoding.UTF8.GetBytes($$"""{"animal_slots": {{Slots(100)}}}""")));
        Assert.Equal(["Your trophy lodge Medium holds 100" + Expected], Notices(OWNER));  // the largest of the two over 75, not the one saved last
    }

    [Fact]
    public void TheMessageNamesTheLargestWhenSeveralLodgesQualifyAtStart()
    {
        AddLodge(OWNER, 3, "First", 76);
        AddLodge(OWNER, 4, "Largest", 97);
        AddLodge(OWNER, 5, "Third", 80);
        state.BackfillLodges();
        Assert.Equal(["Your trophy lodge Largest holds 97" + Expected], Notices(OWNER));
    }

    [Fact]
    public void TheLodgeIsStillSentWhole()
    {
        AddLodge(OWNER, 3, "Big Cabin", 120);
        state.BackfillLodges();
        Assert.Equal(120, Reply(state.LodgeReply(OWNER, 3)!)["animal_slots"]!.AsArray().Count);
    }

    /// <summary>A restored lodge whose slots hold an object and an array as `trophy_id`, next to `mounted` plain ones.</summary>
    void AddOddLodge(long account, int id, string name, int mounted)
    {
        using var con = state.Store.Open(account);
        con.Begin();
        const string odd = """{"slot_id": 500, "trophy_id": {"x": 1}}, {"slot_id": 501, "trophy_id": [1, 2]}, {"slot_id": 502, "trophy_id": true}""";
        var plain = string.Join(",", Enumerable.Range(0, mounted).Select(i => $$"""{"slot_id": {{i}}, "trophy_id": {{2000 + i}}}"""));
        var lodge = JsonNode.Parse($$"""{"id": {{id}}, "name": "{{name}}", "animal_slots": [{{odd}}{{(mounted > 0 ? "," + plain : "")}}], "weapon_slots": []}""")!;
        PlayerRows.AddLodge(con, "/game/user/lodges", id.ToString(), new JsonObject { ["id"] = id, ["name"] = name });
        PlayerRows.AddLodge(con, "/game/user/lodge", id.ToString(), lodge);
        con.Commit();
    }

    [Fact]
    public void AnOddTrophyIdIsNoTrophyAndTheCountIgnoresIt()
    {
        AddOddLodge(OWNER, 3, "Odd", 74);
        state.BackfillLodges();
        Assert.Empty(Notices(OWNER));  // 74 plain ones; the object, the array and the boolean are not counted
        AddOddLodge(OTHER, 3, "Odd", 75);
        state.BackfillLodges();
        Assert.Equal(["Your trophy lodge Odd holds 75" + Expected], Notices(OTHER));
    }

    [Fact]
    public void AnOddSlotInOneLodgeDoesNotFailTheSaveOfAnotherAndTheNoticeStillGoes()
    {
        AddOddLodge(OWNER, 3, "Odd", 0);
        AddLodge(OWNER, 4, "Normal", 10);
        Assert.Equal(200, state.SaveLodge(OWNER, 4, Encoding.UTF8.GetBytes($$"""{"name": "renamed", "animal_slots": {{Slots(80)}}}""")));
        using (var con = state.Store.Open(OWNER))  // read from the rows: the page reads of an odd slot are not this change's business
        {
            var stored = JsonNode.Parse((string)con.Scalar("SELECT node FROM lodges WHERE account_id = $a AND source = '/game/user/lodge' AND id = '4'")!)!;
            Assert.Equal(("renamed", 80), (stored["name"]!.GetValue<string>(), stored["animal_slots"]!.AsArray().Count));
        }
        Assert.Equal(["Your trophy lodge renamed holds 80" + Expected], Notices(OWNER));
    }

    [Fact]
    public void AnOddSlotInOneLodgeDoesNotStopTheStartsFixesOfAnotherLodge()
    {
        AddOddLodge(OWNER, 3, "Odd", 0);
        using (var con = state.Store.Open(OWNER))
        {
            con.Begin();
            var lodge = JsonNode.Parse("""{"id": 5, "name": "five", "lodge_engine_id": 3, "animal_slots": [{"slot_id": 2, "trophy_id": 21}], "weapon_slots": []}""")!;
            PlayerRows.AddLodge(con, "/game/user/lodges", "5", lodge.DeepClone());
            PlayerRows.AddLodge(con, "/game/user/lodge", "5", lodge);
            con.Commit();
        }
        state.BackfillLodges();
        using var read = state.Store.Open(OWNER);
        var slot = JsonNode.Parse((string)read.Scalar("SELECT node FROM lodges WHERE account_id = $a AND source = '/game/user/lodge' AND id = '5'")!)!["animal_slots"]![0]!;
        Assert.NotNull(slot["is_full_body"]);  // the fit of lodge 5 was kept, not rolled back
    }

    [Fact]
    public void AVeryLongNameIsCutWithAnEllipsis()
    {
        var name = new string('x', 90);
        AddLodge(OWNER, 3, name, 75);
        state.BackfillLodges();
        Assert.Equal(["Your trophy lodge " + new string('x', 60) + "… holds 75" + Expected], Notices(OWNER));
        AddLodge(OTHER, 3, new string('y', 60), 75);
        state.BackfillLodges();
        Assert.StartsWith("Your trophy lodge " + new string('y', 60) + " holds 75", Notices(OTHER).Single());  // a name of 60 is kept whole
    }
}
