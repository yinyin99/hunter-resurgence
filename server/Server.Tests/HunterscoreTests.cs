using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

public class HunterscoreTests : StateTestBase
{
    const long B = 8;

    // animal species 1, animal species 17, weapon item 5
    static readonly Facts Ranks = Facts.Parse("""
        {"format": 1, "ranks": {"cap": 1000, "rows": [
          {"id": 1, "title": "One", "category": "animal", "species": 1},
          {"id": 2, "title": "Rabbit", "category": "animal", "species": 17},
          {"id": 3, "title": "Five", "category": "weapon", "items": [5]}]}}
        """);

    public HunterscoreTests()
    {
        Scoring.StaticHost = "static.test";
        Scoring.OwnLogo = "testlogo";
        Scoring.Variants = new() { [1] = (30, 260, 1.1), [17] = (600, 1800, 0.9) };
    }

    State Online() => new(Db, Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false, facts: Ranks);

    long Stored(long account) => state.Store.AccountJson(account)["hunterscore"] is { } h ? Py.L(h) : 0;

    void Kill(long account, long individual, long species, long ammo) => state.HeartbeatReply(account, Events(
        O($$"""{"event": 7, "individual": {{individual}}, "species": {{species}}, "gender": 0, "weight_grams": 1000, "texture": 0}"""),
        O($$"""{"event": 1, "individual": {{individual}}, "weapon_id": 5, "ammunition_id": {{ammo}}}"""),
        O($$"""{"event": 3, "individual": {{individual}}, "extended_species": {{species}}}""")));

    [Fact]
    public void TestScoreSumsTheCappedRows()
    {
        var facts = Facts.Parse("""
            {"format": 1, "ranks": {"cap": 3, "rows": [
              {"id": 1, "title": "One", "category": "animal", "species": 1},
              {"id": 2, "title": "Rabbit", "category": "animal", "species": 17},
              {"id": 3, "title": "Guns", "category": "weapon", "items": [5, 706]},
              {"id": 4, "title": "Finds", "category": "collectable", "types": [0, 1]}]}}
            """);
        var stats = O("""
            {"animal": {"101": {"ethical_kills": 2}, "17": {"kills": 5}, "x": {"ethical_kills": 9}},
             "weapon": {"5": {"10": {"ethical_kills": 1}, "11": {"ethical_kills": 1}}, "999": {"10": {"ethical_kills": 4}}},
             "collectables": {"0": {"collected": 1}, "1": {"collected": 1}}}
            """);
        // species 1: 2 (its variant 101) + 2 kills, capped to 3; species 17: no ethical_kills, 0; the weapon row 1 + 1 + the kill
        // with 706 (999 is in no row); collectables 1 + 1
        Assert.Equal(3 + 0 + 3 + 2, State.Score(facts, stats, [(1, 706), (1, null)]));
    }

    [Fact]
    public void TestAKillIsCreditedWhenItsHuntCloses()
    {
        state = Online();
        var local = Stored(ME);
        state.CreateAccount(B, "second");
        state.ConnectReply(B);
        Kill(B, 81, 1, 10);
        Assert.Equal(0, Stored(B));  // mid-hunt: nothing yet
        state.Disconnect(B, "{}"u8.ToArray());
        Assert.Equal(2, Stored(B));  // the species row and the weapon row
        // a second hunt: ammunition 10 is not permitted on species 17, so the kill is not ethical
        state.ConnectReply(B);
        Kill(B, 82, 17, 10);
        state.Disconnect(B, "{}"u8.ToArray());
        Assert.Equal(2, Stored(B));
        Assert.Equal(local, Stored(ME));  // the recorded account keeps the recorded player's value
    }

    [Fact]
    public void TestEveryAccountIsRecomputedAtStart()
    {
        state = Online();
        state.CreateAccount(B, "second");
        using (var con = state.Store.Open())
        {
            con.Exec("UPDATE accounts SET account = json_set(account, '$.hunterscore', 5000) WHERE id = ?", B);
            con.Exec("UPDATE accounts SET account = json_set(account, '$.hunterscore', 777) WHERE id = ?", ME);
        }
        state = Online();
        Assert.Equal((0L, 777L), (Stored(B), Stored(ME)));
    }

    [Fact]
    public void TestValuesAPlayerSentNeverStopTheScore()
    {
        // a restored counter too large for a number of 64 bits counts the cap
        var stats = O("""{"animal": {"1": {"ethical_kills": 99999999999999999999}}, "weapon": {"5": {"10": {"ethical_kills": 9223372036854775807}, "11": {"ethical_kills": 9223372036854775807}}}}""");
        Assert.Equal(2000, State.Score(Ranks, stats, []));

        state = Online();
        state.CreateAccount(B, "second");
        state.ConnectReply(B);
        state.HeartbeatReply(B, Events(
            O("""{"event": 7, "individual": 81, "species": 1, "gender": 0, "weight_grams": 1000, "texture": 0}"""),
            O("""{"event": 1, "individual": 81, "weapon_id": "x", "ammunition_id": 10}"""),
            O("""{"event": 3, "individual": 81, "extended_species": 1}"""),
            O("""{"event": 3, "individual": 82}"""),  // no species: skipped
            O("""{"event": 7, "individual": 83, "species": 1, "gender": 0, "weight_grams": 1000, "texture": 0}"""),
            O("""{"event": 1, "individual": 83, "weapon_id": 5, "ammunition_id": 99999999999999999999}"""),
            O("""{"event": 3, "individual": 83, "extended_species": 17}""")));  // an ammo id beyond 64 bits is not permitted on 17
        state.Disconnect(B, "{}"u8.ToArray());
        Assert.Equal(1, Stored(B));  // the species row only: "x" is no weapon
        // a kill the database cannot read back (a number that is not finite) is skipped, the others still count, and the
        // close, the next launch, a restore and the start still work
        state.ConnectReply(B);
        state.HeartbeatReply(B, """{"events": [{"event": 3, "individual": 84, "extended_species": 1, "trophy_integrity": 1e999}]}"""u8.ToArray());
        Kill(B, 85, 1, 10);
        state.Disconnect(B, "{}"u8.ToArray());
        Assert.Equal(3, Stored(B));  // 81: the species row; 85: the species and the weapon row
        state.NoteLaunch(B, "start=101&reserve=2"u8.ToArray());
        state.NoteLaunch(B, "start=101&reserve=2"u8.ToArray());
        // the zip's own hunterscore (1234) never stays: the value is always the computed one
        Restorer.Import(state, TestZip.Own().Write(tmp), B, tmp);
        Assert.Equal(3, Stored(B));
        state = Online();
        Assert.Equal(3, Stored(B));
    }
}
