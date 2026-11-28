using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>An operator's `together` rule: one kill finishes every objective of the mission with that rule, and every path that judges a stored kill again (the start, the rollback, the audit, a multiplayer
/// harvest) finishes, keeps and reports each of them, not only the first.</summary>
public class MissionTogetherTests : MissionTestBase
{
    // mission 1: three objectives, one rule
    const string Three = "{\"missions\": [{\"id\": 1, \"title\": \"Three\", \"dependencies\": [], \"rewards\": [{\"text\": \"10 gm$\"}], \"species\": [1], \"reserves\": [2], \"objectives\": ["
        + "{\"id\": 0, \"title\": \"Harvest a Test Deer\"}, {\"id\": 1, \"title\": \"Harvest a Test Deer\"}, {\"id\": 2, \"title\": \"Harvest a Test Deer\"}]}]}";

    static JsonObject Together(params int[] objectives) => Rules([.. objectives.Select(o => ($"1/{o}", "harvest; species: Test Deer; together"))]);

    [Fact]
    public void TestAStartFinishesTheTogetherObjectivesAKillMetUnderTheNewRules()
    {
        state = New("tg-start.db", Three);  // no `together` yet: one deer, one objective
        ActivateAll();
        Hb(Shot(1));
        var later = New("tg-start.db", Three, rules: Together(0, 1));
        Assert.Equal(1, later.ConfirmMissions());  // the deer that finished objective 0 finished objective 1 too
        Assert.Equal("", Completed(of: later));
        Assert.Equal(0, later.ConfirmMissions());
        state = later;
        Hb(Shot(2));
        Assert.Equal("1", Completed());  // the third objective is the second deer's
    }

    [Fact]
    public void TestTheRollbackKeepsWhatOneKillFinishedTogether()
    {
        state = New("tg-rollback.db", Three, rules: Together(0, 1));
        ActivateAll();
        Hb(Shot(1));
        Hb(Shot(2));
        Assert.Equal("1", Completed());  // objectives 0 and 1 by the first deer, 2 by the second
        var strict = New("tg-rollback.db", Three, rules: Together(0, 1));
        Assert.StartsWith("would reopen 0 objectives in 0 missions", strict.RollbackMissions(apply: false).Last());
        Assert.StartsWith("reopened 0 objectives in 0 missions", strict.RollbackMissions(apply: true).Last());
        Assert.Equal("1", Completed(of: strict));
    }

    [Fact]
    public void TestTheRollbackOfAnActiveMissionKeepsTheObjectivesOfOneKill()
    {
        state = New("tg-active.db", Three, rules: Together(0, 1));
        ActivateAll();
        Hb(Shot(1));
        Assert.Equal("", Completed());
        var strict = New("tg-active.db", Three, rules: Together(0, 1));
        Assert.StartsWith("reopened 0 objectives", strict.RollbackMissions(apply: true).Last());
        using var con = strict.Store.Open(ME);
        Assert.Equal("0,1", string.Join(",", JsonNode.Parse((string)con.Scalar("SELECT objectives FROM missions WHERE account_id = $a AND mission_id = 1")!)!.AsArray()));
    }

    [Fact]
    public void TestTheAuditDoesNotFlagTheObjectivesOneKillFinishedTogether()
    {
        state = New("tg-audit.db", Three, rules: Together(0, 1));
        state.Clock = () => 1_800_000_000;
        ActivateAll();
        Hb(Shot(1));
        var report = state.AuditMissions(24).ToList();
        Assert.Equal("missions audit, the last 24 h: 2 objectives completed by 1 accounts", report[0]);
        Assert.Contains("unmet 0, rule changed 0", report[^1]);
    }

    [Fact]
    public void TestTheRecheckDryRunListsWhatTheStartWouldFinishAndWritesNothing()
    {
        state = New("tg-dry.db", Three);
        ActivateAll();
        Hb(Shot(1));
        Hb(Shot(2));
        var later = New("tg-dry.db", Three, rules: Together(0, 1));
        var dry = later.RecheckMissions().ToList();
        Assert.Equal($"account {ME}: mission 1 finishes objectives 2 and completes (pays 10 gm$, 0 em$)", dry[0]);  // objectives 0 and 1 are held; the first deer also meets 1, the second finishes 2
        Assert.Equal("would finish 1 objectives in 1 accounts, complete 1 missions and pay 10 gm$ and 0 em$; would reopen 0 (the start never reopens); dry run: nothing written", dry[^1]);
        Assert.Equal("", Completed(of: later));  // the dry run wrote nothing
        Assert.Equal(1, later.ConfirmMissions());  // the start does what the dry run printed
        Assert.Equal("1", Completed(of: later));
    }

    [Fact]
    public void TestAnObjectiveTheFileMarksTogetherNeedsATwin()
    {
        var e = Assert.Throws<InvalidDataException>(() => New("tg-alone.db", Three, rules: Together(0)));
        Assert.Contains("\"1/0\"", e.Message);
        Assert.Contains("together", e.Message);
    }

    [Fact]
    public void TestTogetherGivenTwiceStopsTheStart()
    {
        var e = Assert.Throws<InvalidDataException>(() => New("tg-twice.db", Three, rules: Rules(("1/0", "harvest; species: Test Deer; together; together"), ("1/1", "harvest; species: Test Deer; together"))));
        Assert.Contains("given twice", e.Message);
    }

    [Fact]
    public void TestAnOrderedObjectiveOfTheGroupWaitsForItsOwnOrder()
    {
        // objectives 0 and 2 are one group, ordered; objective 1 (a fox) is between them: a deer finishes 0, and 2 waits for its own order
        state = New("tg-ordered.db", Three, rules: Rules(("1/0", "harvest; species: Test Deer; ordered; together"), ("1/1", "harvest; species: Test Fox"), ("1/2", "harvest; species: Test Deer; ordered; together")));
        ActivateAll();
        Hb(Shot(1));
        using var con = state.Store.Open(ME);
        Assert.Equal("0", string.Join(",", JsonNode.Parse((string)con.Scalar("SELECT objectives FROM missions WHERE account_id = $a AND mission_id = 1")!)!.AsArray()));
        Hb(Shot(2, species: 5));
        Hb(Shot(3));
        Assert.Equal("1", Completed());
    }
}

/// <summary>A harvest of an animal another member shot finishes every objective the harvester's `together` rule groups, keeps each, and tells of each.</summary>
public class MissionTogetherMpTests : MissionTestBase
{
    const long HOST = 11, GUEST = 12;
    const string Start = "start=101&loctype=LODGE&reserve=2&time=540&loadout_id=0";

    const string Three = "{\"missions\": [{\"id\": 1, \"title\": \"Three\", \"dependencies\": [], \"rewards\": [{\"text\": \"10 gm$\"}], \"species\": [1], \"reserves\": [2], \"objectives\": ["
        + "{\"id\": 0, \"title\": \"Harvest a Test Deer\"}, {\"id\": 1, \"title\": \"Harvest a Test Deer\"}, {\"id\": 2, \"title\": \"Harvest a Test Deer\"}]}]}";

    JsonArray Beat(long who, params JsonNode[] events) => Reply(state.HeartbeatReply(who, Events(events)))["updates"]!.AsArray();

    [Fact]
    public void TestTheHarvesterKeepsAndIsToldOfEveryObjectiveTheCreditedHarvestFinishesTogether()
    {
        state = New("tg-mp.db", Three, rules: Rules(("1/0", "harvest; species: Test Deer; together"), ("1/1", "harvest; species: Test Deer; together")));
        foreach (var (id, handle) in new[] { (HOST, "host"), (GUEST, "guest") })
            state.CreateAccount(id, handle);
        state.HostGame(HOST, "host-session", Encoding.UTF8.GetBytes($"{Start}&name=Camp&max_players=2"));
        Beat(HOST, O("""{"event": 29, "port": 32303, "guid": "9007199254740993", "color": "c73378"}"""));
        var game = (long)Reply(state.NetworkListReply())[0]!["id"]!;
        Assert.Null(state.JoinGame(GUEST, Encoding.UTF8.GetBytes($"start=101&loctype=LODGE&network_id={game}&loadout_id=0")).Error);
        foreach (var id in new[] { HOST, GUEST })
            state.MissionCall(id, "activate_all_available", []);
        Beat(GUEST, Shot(1)[..2]);  // the guest shoots the deer
        Beat(HOST, Shot(1, hits: 0));  // the host skins it
        var updates = Beat(HOST);  // the toasts queued for him
        using var con = state.Store.Open(HOST);
        Assert.Equal("0,1", string.Join(",", JsonNode.Parse((string)con.Scalar("SELECT kept FROM missions WHERE account_id = $a AND mission_id = 1")!)!.AsArray()));
        Assert.Equal(2, updates.Count(u => Py.Str(u!["text"]).Contains("objective")));  // one toast for each
        Assert.StartsWith("would reopen 0 objectives", state.RollbackMissions(apply: false, only: HOST).Last());
    }
}
