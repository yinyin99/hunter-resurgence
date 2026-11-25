using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The events log, a payment's base and event, and the backfill of the payments made before them (docs/server.md#reward-bases-and-events), on invented rows.</summary>
public class RewardEventsTests : StateTestBase
{
    const long First = 8;

    // the file the operator passes to `admin backfill-rewards --events`, as docs/server.md shows it
    const string EventsFile = """
        [
          {"name": "Harvest gm$ x2 weekend", "kind": "gm_bonus", "applies_to": "harvest_gm", "multiplier": 2, "start": "2026-11-21T21:48:00Z", "end": "2026-11-23T05:00:00Z"},
          {"name": "Harvest gm$ x1.25", "kind": "gm_bonus", "applies_to": "harvest_gm", "multiplier": 1.25, "start": "2026-11-24T13:19:51Z", "end": "2026-11-27T15:00:00Z"}
        ]
        """;

    static long T(string iso) => RewardEvents.ParseTime(iso)!.Value;

    State Start(double at)
    {
        var s = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES);
        s.Clock = () => at;
        return s;
    }

    void Pay(long account, string at, double amount, string summary, double? baseAmount = null, long? bonusEvent = null)
    {
        using var con = state.Store.Open(account);
        con.Exec("INSERT INTO wallet_log (account_id, ts, currency, amount, after, summary, item_ids, base, bonus_event) VALUES ($a, ?, 'gm', ?, 0, ?, '[]', ?, ?)",
                 T(at), amount, summary, baseAmount, bonusEvent);
    }

    List<(double Amount, double? Base, long? Event)> Rows(long account, string summaryLike)
    {
        using var con = state.Store.Open(account);
        return [.. con.Rows("SELECT amount, base, bonus_event FROM wallet_log WHERE account_id = $a AND summary LIKE ? ORDER BY n", summaryLike)
            .Select(r => (Convert.ToDouble(r[0]), r[1] is double b ? b : (double?)null, r[2] is long e ? e : (long?)null))];
    }

    /// <summary>The wallet_log rows of the two test accounts that meet the condition.</summary>
    long Wallet(string where)
    {
        long n = 0;
        foreach (var account in new[] { First, First + 1 })
        {
            using var con = state.Store.Open(account);
            n += (long)con.Scalar($"SELECT COUNT(*) FROM wallet_log WHERE account_id = $a AND {where}")!;
        }
        return n;
    }

    long Count(string sql)
    {
        using var con = state.Store.Open();
        return (long)con.Scalar(sql)!;
    }

    [Fact]
    public void ABonusInTheSettingsIsWrittenOnceWhateverTheNumberOfStartsAndAnEndedOneNotAtAll()
    {
        var at = (double)T("2026-11-24T20:00:00Z");
        state = Start(at);
        state.HarvestGmBonus = (1.25, T("2026-11-27T15:00:00Z"));
        state = Start(at + 3600);  // the next start reads the same setting
        state.HarvestGmBonus = (1.25, T("2026-11-27T15:00:00Z"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM events_log"));
        Assert.Equal(1.25, state.GmMultiplier(at + 3600));
        Assert.NotNull(state.GmEvent(at + 3600));
        state.HarvestGmBonus = (2, T("2026-11-27T15:00:00Z"));  // another multiplier is another event
        Assert.Equal(2, Count("SELECT COUNT(*) FROM events_log"));
        state.HarvestGmBonus = (3, T("2026-11-20T00:00:00Z"));  // over before the start: nothing to record, nothing applies
        Assert.Equal(2, Count("SELECT COUNT(*) FROM events_log"));
        Assert.Null(state.GmEvent(at));
    }

    [Fact]
    public void TwoOverlappingBonusesKeepTheirOwnBaseAndTheirOwnEvent()
    {
        var at = (double)T("2026-11-24T20:00:00Z");
        state = Start(at);
        state.HarvestGmBonus = (2, T("2026-11-27T15:00:00Z"));
        using var con = state.Store.Open(ME);
        var mission = RewardEvents.Record(con, "Mission rewards x2", RewardEvents.MissionBonus, T("2026-11-24T00:00:00Z"), T("2026-11-26T00:00:00Z"), 2, "mission_reward");
        var harvest = state.GmEvent(at)!.Value;
        Assert.NotEqual(mission, harvest);
        con.Begin();
        State.AddCurrency(con, "gm", 200, "gm$ reward", null, 100, mission);
        State.AddCurrency(con, "gm", 60, "CSS Reward (Animal Id: 1)", null, 30, harvest);
        State.AddCurrency(con, "gm", 5, "Dog level reward.");  // no bonus: base is the amount
        con.Commit();
        Assert.Equal([(200.0, (double?)100, (long?)mission)], Rows(ME, "gm$ reward"));
        Assert.Equal([(60.0, (double?)30, (long?)harvest)], Rows(ME, "CSS Reward%"));
        Assert.Equal([(5.0, (double?)5, (long?)null)], Rows(ME, "Dog level reward%"));
    }

    [Fact]
    public void BackfillGivesTheTwoEventsTheirBasesAndEveryOtherPaymentItsAmountAndASecondRunChangesNothing()
    {
        state = Start(T("2026-11-24T20:00:00Z"));
        state.HarvestGmBonus = (1.25, T("2026-11-27T15:00:00Z"));  // the deploy during the x1.25: its event exists, started later than the operator says
        state.CreateAccount(First, "hunter0");
        state.CreateAccount(First + 1, "hunter1");
        // the x2 weekend: a doubled harvest, an odd one (rounds as the payment did: to even), one just before it, one at its end (not inside), a mission reward inside it
        Pay(First, "2026-11-22T10:00:00Z", 100, "CSS Reward (Animal Id: 1)");
        Pay(First, "2026-11-22T11:00:00Z", 101, "CSS Reward (Animal Id: 2)");
        Pay(First, "2026-11-21T21:47:59Z", 40, "CSS Reward (Animal Id: 3)");
        Pay(First, "2026-11-23T05:00:00Z", 30, "CSS Reward (Animal Id: 4)");
        Pay(First, "2026-11-22T12:00:00Z", 200, "gm$ reward");
        // the x1.25: a payment before the deploy has no base; one since has it already and is never touched
        Pay(First + 1, "2026-11-25T08:00:00Z", 125, "CSS Reward (Animal Id: 1)");
        Pay(First + 1, "2026-11-25T09:00:00Z", 77, "CSS Reward (Animal Id: 2)", baseAmount: 77);
        var specs = RewardEvents.ParseSpecs(EventsFile);

        var dry = RewardEvents.Backfill(state.Store, specs, apply: false);
        Assert.Contains("Harvest gm$ x2 weekend: 2 harvest payments, paid 201, base 100", dry);
        Assert.Contains("Harvest gm$ x1.25: 1 harvest payments, paid 125, base 100", dry);
        Assert.Contains("every other payment: 3 get base = amount", dry);
        Assert.Equal(1, Count("SELECT COUNT(*) FROM events_log"));  // a dry run writes nothing
        Assert.Equal(6, Wallet("base IS NULL"));

        RewardEvents.Backfill(state.Store, specs, apply: true);
        using var all = state.Store.Open();
        var weekend = (long)all.Scalar("SELECT id FROM events_log WHERE multiplier = 2")!;
        var quarter = (long)all.Scalar("SELECT id FROM events_log WHERE multiplier = 1.25")!;
        Assert.Equal(2, Count("SELECT COUNT(*) FROM events_log"));  // the server's x1.25 row is the operator's: one row, with the operator's start
        Assert.Equal(T("2026-11-24T13:19:51Z"), (long)all.Scalar("SELECT start_ts FROM events_log WHERE id = ?", quarter)!);
        Assert.Equal(T("2026-11-21T21:48:00Z"), (long)all.Scalar("SELECT start_ts FROM events_log WHERE id = ?", weekend)!);
        Assert.Equal([(100.0, (double?)50, (long?)weekend), (101.0, (double?)50, (long?)weekend), (40.0, (double?)40, (long?)null), (30.0, (double?)30, (long?)null)],
                     Rows(First, "CSS Reward%"));
        Assert.Equal([(200.0, (double?)200, (long?)null)], Rows(First, "gm$ reward"));
        Assert.Equal([(125.0, (double?)100, (long?)quarter), (77.0, (double?)77, (long?)null)], Rows(First + 1, "CSS Reward%"));
        Assert.Equal(0, Wallet("base IS NULL"));

        var again = RewardEvents.Backfill(state.Store, specs, apply: true);
        Assert.Contains("Harvest gm$ x2 weekend: 0 harvest payments, paid 0, base 0", again);
        Assert.Equal(2, Count("SELECT COUNT(*) FROM events_log"));
        Assert.Equal(2, Wallet("bonus_event IS NOT NULL AND base < amount AND amount IN (100, 101)"));
    }

    [Fact]
    public void ASeasonalEventIsAddedByTheOperatorOnceAndListed()
    {
        state = Start(T("2026-11-24T20:00:00Z"));
        var (start, end) = (T("2026-10-24T00:00:00Z"), T("2026-11-02T00:00:00Z"));
        var id = RewardEvents.Add(state.Store, "Test season", RewardEvents.Seasonal, start, end);
        Assert.Equal(id, RewardEvents.Add(state.Store, "Test season", RewardEvents.Seasonal, start, end));
        Assert.Equal(["" + id + " | Test season | seasonal | 2026-10-24T00:00:00Z .. 2026-11-02T00:00:00Z | no multiplier"], RewardEvents.List(state.Store));
        Assert.Null(RewardEvents.ParseTime("not a time"));
    }

    [Theory]
    [InlineData("""[{"name": "a", "start": "2026-11-02T00:00:00Z", "end": "2026-11-01T00:00:00Z"}]""")]  // ends before it starts
    [InlineData("""[{"name": "a", "start": "2026-11-01T00:00:00Z", "end": "2026-11-02T00:00:00Z", "multiplier": 2}]""")]  // a bonus on nothing
    [InlineData("""[{"name": "a", "start": "2026-11-01T00:00:00Z", "end": "2026-11-02T00:00:00Z", "multiplier": 0, "applies_to": "harvest_gm"}]""")]
    [InlineData("""{"name": "a"}""")]
    public void ABadEventsFileIsRefused(string json) => Assert.Throws<InvalidDataException>(() => RewardEvents.ParseSpecs(json));

    [Theory]
    [InlineData(33), InlineData(34)]  // below and at this schema's number: a database already at the current version must get them too
    public void ADatabaseWithoutTheBaseColumnsAndTheEventsLogGetsThemWhateverItsVersion(int version)
    {
        var file = Path.Combine(tmp, $"v{version}.db");
        using (var con = new Store(file).Open())
        {
            con.Exec("DROP TABLE events_log");
            con.Exec("ALTER TABLE wallet_log DROP COLUMN base");
            con.Exec("ALTER TABLE wallet_log DROP COLUMN bonus_event");
            con.Exec($"PRAGMA user_version = {version}");
        }
        using var check = new Store(file).Open();
        Assert.Equal(2L, check.Scalar("SELECT COUNT(*) FROM pragma_table_info('wallet_log') WHERE name IN ('base', 'bonus_event')"));
        Assert.Equal(0L, check.Scalar("SELECT COUNT(*) FROM events_log"));
    }

    [Fact]
    public void ABackfillReportsAHarvestPaidLongAfterItsEventAndDoesNotDivideIt()
    {
        state = Start(T("2026-11-24T20:00:00Z"));
        state.CreateAccount(First, "hunter0");
        using (var con = state.Store.Open(First))
        {
            // kill 1 was paid with its heartbeat; kill 2 was paid a day later, by a rescore, without the bonus
            foreach (var (kill, at) in new[] { (1, "2026-11-22T10:00:00Z"), (2, "2026-11-22T11:00:00Z") })
            {
                con.Exec("INSERT INTO kills (account_id, kill_id, individual, species, harvest) VALUES ($a, ?, ?, 1, '{}')", kill, 500 + kill);
                con.Exec("INSERT INTO events (account_id, event, body, ts) VALUES ($a, 3, ?, ?)", $$"""{"event": 3, "individual": {{500 + kill}}}""", T(at));
            }
        }
        Pay(First, "2026-11-22T10:00:01Z", 100, "CSS Reward (Animal Id: 1)");
        Pay(First, "2026-11-23T04:00:00Z", 60, "CSS Reward (Animal Id: 2)");
        var lines = RewardEvents.Backfill(state.Store, RewardEvents.ParseSpecs(EventsFile), apply: true);
        Assert.Contains(lines, l => l.Contains("CSS Reward (Animal Id: 2)") && l.Contains("not divided"));
        Assert.Contains("Harvest gm$ x2 weekend: 1 harvest payments, paid 100, base 50", lines);
        long? weekend;
        using (var con = state.Store.Open())
            weekend = con.Scalar("SELECT id FROM events_log WHERE multiplier = 2") as long?;
        Assert.Equal([(100.0, (double?)50, weekend), (60.0, (double?)60, (long?)null)], Rows(First, "CSS Reward%"));
    }
}
