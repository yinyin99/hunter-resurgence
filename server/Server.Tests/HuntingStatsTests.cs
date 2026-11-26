using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>Server playtime (`huntSecondsHere`) and the hunting statistics, on invented hunts written straight into the tables.</summary>
public class HuntingStatsTests : StateTestBase
{
    const long First = 8;  // accounts First .. First + 5
    const int Hour = 3600;
    double now = State.DefaultPlaytimeSince + 100 * Hour;

    State Online()
    {
        var s = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false);
        s.Clock = () => now;
        return s;
    }

    void Players(int count)
    {
        for (var i = 0; i < count; i++)
            state.CreateAccount(First + i, "hunter" + i);
    }

    /// <summary>A hunt of `account` on `reserve` that started `ago` seconds before now and lasted `seconds` (null: still open); `heartbeat` false: it had none after its start. Returns its id.</summary>
    long Hunt(long account, long reserve, double ago, double? seconds, bool heartbeat = true)
    {
        var start = now - ago;
        using var con = state.Store.Open(account);
        return con.Insert("INSERT INTO expeditions (account_id, reserve, start_ts, end_ts, last_seen) VALUES ($a, ?, ?, ?, ?)", reserve, start,
                          seconds is { } s ? start + s : (object?)null, heartbeat ? start + (seconds ?? ago) : (object?)null);
    }

    long kills;

    /// <summary>A harvest of `species` in `hunt`, paid `gm` (null: no reward row), whose base was `baseGm` (null: the row has none, as one from before the bases did).</summary>
    void Harvest(long account, long hunt, long species, double? gm, double? baseGm = null)
    {
        var kill = ++kills;
        using var con = state.Store.Open(account);
        con.Exec("INSERT INTO kills (account_id, kill_id, individual, species, harvest) VALUES ($a, ?, ?, ?, '{}')", kill, kill, species);
        con.Exec("INSERT INTO harvests (account_id, kill_id, body) VALUES ($a, ?, ?)", kill, $$"""{"expedition_id": {{hunt}}}""");
        if (gm is { } paid)
            Wallet(account, paid, $"CSS Reward (Animal Id: {kill})", baseAmount: baseGm);
    }

    void Wallet(long account, double amount, string summary, double? ts = null, double? baseAmount = null)
    {
        using var con = state.Store.Open(account);
        con.Exec("INSERT INTO wallet_log (account_id, ts, currency, amount, after, summary, item_ids, base) VALUES ($a, ?, 'gm', ?, 0, ?, '[]', ?)", (long)(ts ?? now), amount, summary, baseAmount);
    }

    static string Names(JsonNode rows, string key) => string.Join(",", rows.AsArray().Select(r => (string)r![key]!));

    [Fact]
    public void ThreeHoursHereAndNineHundredRestoredAreNineHundredThreeInTotalAndThreeHere()
    {
        state = Online();
        Players(1);
        using (var con = state.Store.Open(First))
        {
            PlayerRows.SetStatistics(con, O("{}"), 0, 900 * Hour, null, null);
            var id = Hunt(First, 2, 5 * Hour, 3 * Hour);
            con.Exec("UPDATE expeditions SET stats = ? WHERE account_id = $a AND id = ?", """{"duration": 10800, "distance": 0, "data": {"achievements": []}}""", id);
        }
        var page = state.StatisticsPage(First);
        Assert.Equal((903 * Hour, 3 * Hour), ((int)page["huntSeconds"]!, (long)page["huntSecondsHere"]!));
    }

    [Fact]
    public void PlaytimeSinceLeavesOutEarlierHuntsAndAHuntBeforeTheOpeningDayIsNoServerPlaytime()
    {
        state = Online();
        Players(1);
        Hunt(First, 2, 100 * Hour + 50, 4 * Hour);  // started 50 s before the opening day
        Hunt(First, 2, 100 * Hour, 2 * Hour);  // started at the opening day
        Hunt(First, 2, 10 * Hour, 1 * Hour);
        using (var con = state.Store.Open(First))
            Assert.Equal(3 * Hour, state.HuntSecondsHere(con));
        state.PlaytimeSince = now - 20 * Hour;  // a later opening day: only the last one
        using (var con = state.Store.Open(First))
            Assert.Equal(1 * Hour, state.HuntSecondsHere(con));
    }

    [Fact]
    public void AnOpenHuntCountsSoFarAndAClosedOneWithoutAHeartbeatCountsZero()
    {
        state = Online();
        Players(1);
        Hunt(First, 2, 3 * Hour, 1 * Hour);
        Hunt(First, 2, 1800, null);  // open: so far
        Hunt(First, 2, 900, 600, heartbeat: false);  // closed with no heartbeat after its start: 0
        using var con = state.Store.Open(First);
        Assert.Equal(1 * Hour + 1800, state.HuntSecondsHere(con));
    }

    [Fact]
    public void AnOpenHuntCountsUpToItsLastHeartbeatAndOneWithoutAHeartbeatCountsZeroHoweverOldItIs()
    {
        state = Online();
        Players(1);
        Hunt(First, 2, 20 * Hour, null, heartbeat: false);  // a launch that crashed: open for ever, never a heartbeat
        var beaten = Hunt(First, 2, 10 * Hour, null);  // open, its last heartbeat now
        using var con = state.Store.Open(First);
        con.Exec("UPDATE expeditions SET last_seen = start_ts + 3600 WHERE account_id = $a AND id = ?", beaten);  // it stopped beating after 1 h
        Assert.Equal(1 * Hour, state.HuntSecondsHere(con));
        now += 24 * Hour;
        Assert.Equal(1 * Hour, state.HuntSecondsHere(con));  // a day later: nothing more
    }

    /// <summary>Five players on reserve 2: each a closed hunt of 2 h with one harvest paid 100; the first also an open hunt of 30 min (a harvest paid 50) and a no-heartbeat hunt (a harvest paid 25).</summary>
    void FivePlayersOnReserve2()
    {
        Players(5);
        for (var i = 0; i < 5; i++)
            Harvest(First + i, Hunt(First + i, 2, 3 * Hour, 2 * Hour), 1, 100);
        Harvest(First, Hunt(First, 2, 1800, null), 1, 50);
        Harvest(First, Hunt(First, 2, 900, 600, heartbeat: false), 1, 25);
    }

    [Fact]
    public void AReservesFiguresAreTheSumsOfItsHuntsAndTheirHarvestRewards()
    {
        state = Online();
        FivePlayersOnReserve2();
        var stats = state.HuntingStatistics();
        var reserve = Assert.Single(stats["reserves"]!.AsArray())!;
        // 5 x 2 h + 30 min (the open hunt so far) + 0 (no heartbeat) = 10.5 h; 500 + 50 + 25 gm$ in 7 harvests
        Assert.Equal(("Test Reserve", 5, 10.5, 54.76, 82.14, 0.67), ((string)reserve["reserve"]!, (int)reserve["players"]!, (double)reserve["hours"]!, (double)reserve["gmPerHour"]!,
                                                                    (double)reserve["gmPerHarvest"]!, (double)reserve["harvestsPerHour"]!));
        var deer = Assert.Single(stats["species"]!.AsArray())!;
        Assert.Equal(("Test Deer (Typical)", 5, 10.5, 54.76, 82.14, "Test Reserve"), ((string)deer["species"]!, (int)deer["players"]!, (double)deer["hours"]!, (double)deer["gmPerHour"]!,
                                                                           (double)deer["gmPerHarvest"]!, (string)deer["bestReserve"]!));
        Assert.Equal("2026-11-12T00:00:00Z", (string)stats["since"]!);
    }

    [Fact]
    public void ComparableFiguresUseTheBaseAndAPeriodThatOverlapsAnEventSaysSo()
    {
        state = Online();
        Players(5);
        for (var i = 0; i < 5; i++)
            Harvest(First + i, Hunt(First + i, 2, 3 * Hour, 2 * Hour), 1, 200, baseGm: 100);  // paid double a base of 100
        Assert.Empty(state.HuntingStatistics()["events"]!.AsArray());
        using (var con = state.Store.Open())
            RewardEvents.Record(con, "Test weekend", RewardEvents.GmBonus, (long)now - 10 * Hour, (long)now - 5 * Hour, 2, RewardEvents.HarvestGm);
        var stats = state.HuntingStatistics();
        var reserve = Assert.Single(stats["reserves"]!.AsArray())!;
        // 5 x 2 h = 10 h; 1000 gm$ paid, 500 at the base, in 5 harvests
        Assert.Equal((100.0, 50.0, 200.0, 100.0), ((double)reserve["gmPerHour"]!, (double)reserve["comparableGmPerHour"]!, (double)reserve["gmPerHarvest"]!, (double)reserve["comparableGmPerHarvest"]!));
        var ev = Assert.Single(stats["events"]!.AsArray())!;
        Assert.Equal(("Test weekend", "gm_bonus", 2.0), ((string)ev["name"]!, (string)ev["kind"]!, (double)ev["multiplier"]!));
        Assert.Equal(100.0, (double)state.HuntingAdmin()["players"]![0]!["reserves"]![0]!["baseGm"]!);  // one player's own
    }

    [Fact]
    public void TheBestReserveIsChosenAtTheBaseSoABonusNeverPicksIt()
    {
        state = Online();
        Players(5);
        for (var i = 0; i < 5; i++)
        {
            Harvest(First + i, Hunt(First + i, 2, 3 * Hour, 1 * Hour), 1, 150, baseGm: 150);  // reserve 2: 150 an hour, no bonus
            Harvest(First + i, Hunt(First + i, 3, 6 * Hour, 1 * Hour), 1, 400, baseGm: 100);  // reserve 3 paid 400 an hour only because of a bonus (base 100)
        }
        Assert.Equal("Test Reserve", (string)state.HuntingStatistics()["species"]![0]!["bestReserve"]!);
    }

    [Fact]
    public void ARowOfFewerThanFivePlayersIsHiddenAndNoNameIsEverShown()
    {
        state = Online();
        FivePlayersOnReserve2();
        for (var i = 0; i < 4; i++)  // four of them also hunt reserve 3 and harvest a rabbit and, better paid, a deer there
        {
            var hunt = Hunt(First + i, 3, 2 * Hour, 1 * Hour);
            Harvest(First + i, hunt, 17, 10);
            Harvest(First + i, hunt, 1, 1000);
        }
        var stats = state.HuntingStatistics();
        Assert.Equal("Test Reserve", Names(stats["reserves"]!, "reserve"));  // reserve 3 has 4 players
        Assert.Equal("Test Deer (Typical)", Names(stats["species"]!, "species"));  // the rabbit has 4
        Assert.Equal("Test Reserve", (string)stats["species"]![0]!["bestReserve"]!);  // reserve 3 paid more, but from 4 players
        Assert.DoesNotContain("hunter", stats.ToJsonString());
    }

    [Fact]
    public void TheOperatorSeesEveryPlayerWithNamesAndTheOtherRewardsApartAndTheFiguresAreKeptAnHour()
    {
        state = Online();
        Players(2);
        Harvest(First, Hunt(First, 2, 3 * Hour, 2 * Hour), 1, 100);
        Wallet(First, 300, "gm$ reward");  // a mission
        Wallet(First, 20, "Reward for Daily Mission");
        Wallet(First, 5, "Dog level reward.");
        Wallet(First, 9, "gm$ reward", ts: State.DefaultPlaytimeSince - 1);  // before the opening day
        Hunt(First + 1, 2, 2 * Hour, 1 * Hour);
        var admin = state.HuntingAdmin();
        var players = admin["players"]!.AsArray();
        Assert.Equal("hunter0,hunter1", Names(players, "handle"));  // most playtime first
        var first = players[0]!;
        Assert.Equal((2 * Hour, 100.0), ((long)first["huntSecondsHere"]!, (double)first["reserves"]![0]!["gm"]!));
        Assert.Equal("""{"daily":20,"dog":5,"mission":300}""", first["otherGm"]!.ToJsonString());
        Assert.Equal("Test Deer (Typical)", (string)first["species"]![0]!["species"]!);
        Assert.Empty(state.HuntingStatistics()["reserves"]!.AsArray());  // two players: under the floor

        Hunt(First + 1, 2, 1 * Hour, 1 * Hour);
        Assert.Equal(1L * Hour, (long)state.HuntingAdmin()["players"]![1]!["huntSecondsHere"]!);  // kept: the new hunt is not in yet
        now += State.HuntingTtl.TotalSeconds + 1;
        Assert.Equal(2L * Hour, (long)state.HuntingAdmin()["players"]![1]!["huntSecondsHere"]!);
    }
}
