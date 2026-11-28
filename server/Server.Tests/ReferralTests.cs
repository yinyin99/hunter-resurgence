using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>Referral codes (docs/server.md#referrals): who may enter a code and until when, the recruit's reward, the qualification (hours and hunterscore since the entry), the tiers, the weekly cap, the Discord milestone,
/// bans, the settings' reload and validation, the operator's commands and the two endpoints. The recruits' hunterscore is set through restored statistics (species 1 is the one rank row: n kills are a hunterscore of n)
/// and their hours through closed hunts; the clock is `at`. The catalogue has items 0, 2 and 3, rewards give those.</summary>
public class ReferralTests : StateTestBase
{
    static readonly JsonObject Cfg = new(Replay.HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));
    static readonly Facts Ranks = Facts.Parse("""{"format": 1, "ranks": {"cap": 1000, "rows": [{"id": 1, "title": "One", "category": "animal", "species": 1}]}}""");

    const string Catalogue = """
        {"species": [], "reserves": [], "locations": [],
         "items": [
           {"id": 0, "name": "Test Rifle", "variationname": "Blue", "subcat": {"id": 4}, "price": 100, "inventory": {}, "fit": {}},
           {"id": 2, "name": "Test Refill", "subcat": {"id": 5}, "price": 5, "units": {"count": 3, "name": "uses"}, "inventory": {}, "fit": {}},
           {"id": 3, "name": "Test Loose", "price": 5, "inventory": {}, "fit": {}}],
         "categories": [{"id": 1, "title": "Weapons", "subcategories": [{"id": 4, "title": "Rifles"}], "default": 4},
                        {"id": 2, "title": "Supplies", "subcategories": [{"id": 5, "title": "Refills"}], "default": 5}],
         "bundles": []}
        """;

    const long Hour = 3600, Day = 86400;
    const string Site = "https://web.test";

    double at = Math.Floor(Py.Now());  // whole seconds: the playtime counts whole seconds
    const string HuntStats = """{"data": {"animal": {}, "weapon": {}, "collectables": {}}, "duration": 0, "distance": 0}""";
    int edits;
    readonly List<string> log = [];

    public ReferralTests()
    {
        state = Open();
        Sql("UPDATE referral_release SET ts = ?", (long)at - 10 * Day);  // the feature is ten days old: an account made now is a new one
    }

    State Open() => new(Db, Py.DumpBytes(CONNECT), BASE, ICON, (JsonObject)JsonNode.Parse(Catalogue)!, LOCALE, TEXTURES, offline: false, facts: Ranks) { Clock = () => at, PlaytimeSince = 0 };

    string SettingsFile => Path.Combine(tmp, "settings.json");

    void Sql(string sql, params object?[] args)
    {
        using var con = state.Store.Open();
        con.Exec(sql, args);
    }

    /// <summary>A new account (its Steam id `steam-handle`) made at `created` (default now).</summary>
    long Account(string handle, double? created = null)
    {
        var id = state.Store.NextServerId();
        state.CreateAccount(id, handle, $"steam-{handle}");
        Sql("UPDATE accounts SET created_ts = ? WHERE id = ?", (long)(created ?? at), id);
        return id;
    }

    void Ban(string handle) => state.Store.Ban($"steam-{handle}", "test", (long)at);

    JsonObject Status(long account) => state.ReferralStatus(account, Site);

    string Code(long account) => (string)Status(account)["code"]!;

    JsonArray Recruits(long account) => Status(account)["recruits"]!.AsArray();

    void Referral(string json) => state.LoadReferral(JsonNode.Parse(json));

    /// <summary>`recruit` enters the code of `recruiter`; the status is asserted `ok`.</summary>
    void Recruit(long recruit, long recruiter) => Assert.Equal("ok", state.EnterReferralCode(recruit, Code(recruiter)).Status);

    /// <summary>The account played `hours` more on this server (a closed hunt).</summary>
    void Play(long account, double hours)
    {
        using var con = state.Store.Open(account);
        con.Exec("INSERT INTO expeditions (account_id, start_ts, end_ts, last_seen, stats) VALUES ($a, ?, ?, ?, ?)", at, at + hours * Hour, at + hours * Hour, HuntStats);
    }

    /// <summary>The account's hunterscore becomes `score` (a restored count of ethical kills of species 1) and is updated, as when a hunt closes.</summary>
    void Score(long account, int score)
    {
        using var con = state.Store.Open(account);
        PlayerRows.SetStatistics(con, O("{\"animal\": {\"1\": {\"ethical_kills\": " + score + "}}}"), 0, 0, null, null);
        state.UpdateHunterscore(con, account);
    }

    /// <summary>A recruit who has just entered gets 2 hours and 10 hunterscore (his score at the entry was 0).</summary>
    void Qualify(long recruit)
    {
        Play(recruit, 2);
        Score(recruit, 10);
    }

    double Gm(long account) => state.Wallet(account)["gm"]!.GetValue<double>();

    double Em(long account) => state.Wallet(account)["em"]!.GetValue<double>();

    int Owned(long account, long item) => state.Inventory(account).Count(i => (long)i!["item_id"]! == item);

    long Paid(long account)
    {
        using var con = state.Store.Open(account);
        return (long)con.Scalar("SELECT COUNT(*) FROM wallet_log WHERE account_id = $a AND summary = ?", State.ReferralSummary)!;
    }

    [Fact]
    public void AnAccountsCodeIsEightCharactersStableAndItsOwn()
    {
        var (a, b) = (Account("alice"), Account("bob"));
        var code = Code(a);
        Assert.Equal(8, code.Length);
        Assert.All(code, c => Assert.True("ABCDEFGHJKLMNPQRSTUVWXYZ23456789".Contains(c)));
        Assert.Equal(code, Code(a));
        Assert.NotEqual(code, Code(b));
        Assert.Equal($"{Site}/join/{code}", (string)Status(a)["link"]!);
        Assert.Equal(State.NormalizeCode(" ab-cd 23 "), "ABCD23");
    }

    [Fact]
    public void ACodeIsEnteredOnceAtSignUpOrWithinSeventyTwoHoursAndLaterIsExpired()
    {
        var (a, b, c) = (Account("alice"), Account("bob"), Account("carol"));
        var code = Code(a);
        var open = Status(b);
        Assert.True((bool)open["canEnter"]!);
        Assert.Equal((long)at + 72 * Hour, (long)open["enterUntil"]!);
        Assert.Null(open["enteredCode"]);

        at += 71 * Hour;
        var typed = code.ToLowerInvariant().Insert(4, "-") + " ";  // case, dashes and spaces do not count
        var (status, recruiter, reward) = state.EnterReferralCode(b, typed);
        Assert.Equal(("ok", "alice"), (status, recruiter));
        Assert.Null(reward);  // no reward is set
        Assert.Equal("already", state.EnterReferralCode(b, code).Status);
        var done = Status(b);
        Assert.False((bool)done["canEnter"]!);
        Assert.Null(done["enterUntil"]);
        Assert.Equal((code, "alice"), ((string)done["enteredCode"]!, (string)done["recruiter"]!));

        at += 2 * Hour;  // 73 hours after c was made
        Assert.Equal("expired", state.EnterReferralCode(c, code).Status);
        Assert.False((bool)Status(c)["canEnter"]!);
    }

    [Fact]
    public void AnAccountMadeBeforeTheFeatureHasThirtyOneDaysFromItsRelease()
    {
        var (a, early, late) = (Account("alice"), Account("early", at - 100 * Day), Account("late", at - 100 * Day));
        Sql("UPDATE referral_release SET ts = ?", (long)at - 10 * Day);
        var code = Code(a);
        var open = Status(early);
        Assert.Equal((long)at + 21 * Day, (long)open["enterUntil"]!);  // 31 days from the release, 10 days ago
        at += 20 * Day;
        Assert.Equal("ok", state.EnterReferralCode(early, code).Status);
        at += 2 * Day;
        Assert.Equal("expired", state.EnterReferralCode(late, code).Status);
    }

    [Fact]
    public void ACodeThatIsUnknownOwnLoopingOrOfABannedPlayerIsRefused()
    {
        var (a, b, c, d) = (Account("alice"), Account("bob"), Account("carol"), Account("dave"));
        Assert.Equal("own", state.EnterReferralCode(a, Code(a)).Status);
        Assert.Equal("unknown", state.EnterReferralCode(a, "ZZZZ2222").Status);
        Assert.Equal("unknown", state.EnterReferralCode(a, "").Status);
        Assert.Equal("unknown", state.EnterReferralCode(a, null).Status);

        Recruit(b, a);
        Assert.Equal("loop", state.EnterReferralCode(a, Code(b)).Status);  // a recruited b: b's code is not a's to enter
        Assert.Equal("ok", state.EnterReferralCode(a, Code(d)).Status);  // another one is

        var codeOfC = Code(c);
        Ban("carol");
        var e = Account("erin");
        Assert.Equal("banned", state.EnterReferralCode(e, codeOfC).Status);
        Assert.True((bool)Status(e)["canEnter"]!);  // nothing was recorded
    }

    [Fact]
    public void TheRecruitRewardIsPaidAtOnceWhenSetAndNothingWhenEmpty()
    {
        var (a, b, c) = (Account("alice"), Account("bob"), Account("carol"));
        Referral("""{"recruit_reward": {"gm": 500, "em": 20, "items": [{"id": 3, "count": 1}]}}""");
        var (gm, em) = (Gm(b), Em(b));
        var (status, _, reward) = state.EnterReferralCode(b, Code(a));
        Assert.Equal("ok", status);
        Assert.Equal((500L, 20L), (reward!.Gm, reward.Em));
        Assert.Equal((gm + 500, em + 20, 1), (Gm(b), Em(b), Owned(b, 3)));
        Assert.Equal(2L, Paid(b));  // a wallet row "Referral reward" per currency
        var shown = Status(b)["recruitReward"]!;
        Assert.Equal(("Test Loose", 1L), ((string)shown["items"]![0]!["name"]!, (long)shown["items"]![0]!["count"]!));

        Referral("{}");  // no reward
        (gm, em) = (Gm(c), Em(c));
        var empty = state.EnterReferralCode(c, Code(a));
        Assert.Equal("ok", empty.Status);
        Assert.Null(empty.Reward);
        Assert.Equal((gm, em, 0L, 0), (Gm(c), Em(c), Paid(c), Owned(c, 3)));
        Assert.Null(Status(c)["recruitReward"]);
    }

    [Fact]
    public void ARecruitQualifiesWithTheHoursAndTheScoreGainedAfterTheEntryAndNotBefore()
    {
        var (a, b, c) = (Account("alice"), Account("bob"), Account("carol"));
        Play(b, 5);  // a restored profile's old hours and score
        Score(b, 50);
        Recruit(b, a);
        Recruit(c, a);

        Play(b, 3);
        Score(b, 55);  // 3 hours but only 5 gained
        Assert.False((bool)Recruits(a).Single(r => (string)r!["handle"]! == "bob")!["qualified"]!);
        Score(b, 60);  // 10 gained
        var bob = Recruits(a).Single(r => (string)r!["handle"]! == "bob")!;
        Assert.True((bool)bob["qualified"]!);
        Assert.True((bool)bob["paid"]!);
        Assert.Equal(1, (int)Status(a)["qualifiedCount"]!);

        Score(c, 40);  // plenty of score and no hours
        Play(c, 1.9);
        Score(c, 41);
        Assert.False((bool)Recruits(a).Single(r => (string)r!["handle"]! == "carol")!["qualified"]!);
        Play(c, 0.2);
        Score(c, 42);
        Assert.True((bool)Recruits(a).Single(r => (string)r!["handle"]! == "carol")!["qualified"]!);
        Assert.Equal(2, (int)Status(a)["qualifiedCount"]!);
    }

    [Fact]
    public void WhatARestoreBringsAfterTheEntryIsNotGained()
    {
        var (a, b) = (Account("alice"), Account("bob"));
        Recruit(b, a);
        Play(b, 3);
        using (var con = state.Store.Open(b))
        {
            PlayerRows.SetStatistics(con, O("""{"animal": {"1": {"ethical_kills": 500}}}"""), 0, 0, null, null);
            state.UpdateHunterscore(con, b, scoreBefore: 0);  // as the restore calls it: the 500 are the backup's
        }
        Assert.False((bool)Recruits(a)[0]!["qualified"]!);
        Assert.Equal(0.0, (double)Recruits(a)[0]!["score"]!);
        Score(b, 510);
        Assert.True((bool)Recruits(a)[0]!["qualified"]!);
    }

    [Fact]
    public void ATierPaysOnceWhenItsRecruitsQualifyAndAnEmptyRewardPaysNothing()
    {
        Referral("""{"tiers": [{"recruits": 1, "reward": {"gm": 100}}, {"recruits": 2, "reward": null}, {"recruits": 3, "reward": {"items": [{"id": 2, "count": 1}]}}]}""");
        var (a, b, c, d) = (Account("alice"), Account("bob"), Account("carol"), Account("dave"));
        foreach (var r in new[] { b, c, d }) Recruit(r, a);
        var gm = Gm(a);

        Qualify(b);
        Assert.Equal(gm + 100, Gm(a));
        Qualify(c);  // the second tier has no reward
        Assert.Equal((gm + 100, 0), (Gm(a), Owned(a, 2)));
        Qualify(d);
        Assert.Equal((gm + 100, 1), (Gm(a), Owned(a, 2)));
        Score(b, 11);  // the check again: nothing is paid twice
        Assert.Equal(2L, Paid(a));
        Assert.Equal((gm + 100, 1), (Gm(a), Owned(a, 2)));

        var tiers = Status(a)["tiers"]!.AsArray();
        Assert.Equal([true, true, true], tiers.Select(t => (bool)t!["reached"]!));
        Assert.Null(tiers[1]!["reward"]);
        Assert.Equal(100L, (long)tiers[0]!["reward"]!["gm"]!);
    }

    [Fact]
    public void ARewardSetLaterPaysOnlyTheNextQualificationAndPayQualifiedPaysTheReachedOnesOnce()
    {
        Referral("""{"tiers": [{"recruits": 1, "reward": null}, {"recruits": 2, "reward": null}, {"recruits": 3, "reward": null}]}""");
        var (a, b, c, d) = (Account("alice"), Account("bob"), Account("carol"), Account("dave"));
        foreach (var r in new[] { b, c, d }) Recruit(r, a);
        var gm = Gm(a);
        Qualify(b);
        Qualify(c);
        Assert.Equal(gm, Gm(a));

        Referral("""{"tiers": [{"recruits": 1, "reward": {"gm": 100}}, {"recruits": 2, "reward": {"gm": 200}}, {"recruits": 3, "reward": {"gm": 300}}]}""");
        Assert.Equal(gm, Gm(a));  // no back-payment
        Qualify(d);
        Assert.Equal(gm + 300, Gm(a));  // the next qualification pays the tier it reaches

        var dry = state.PayReferralMilestones(apply: false);
        Assert.Equal(3, dry.Count);
        Assert.Contains("tier 1: would pay 100 gm$", dry[0]);
        Assert.Contains("tier 2: would pay 200 gm$", dry[1]);
        Assert.Equal(gm + 300, Gm(a));
        var applied = state.PayReferralMilestones(apply: true);
        Assert.Equal("paid 2", applied[^1]);
        Assert.Equal(gm + 600, Gm(a));
        Assert.Equal(["paid 0"], state.PayReferralMilestones(apply: true));  // once
        Assert.Equal(gm + 600, Gm(a));
    }

    [Fact]
    public void TheWeeklyCapHoldsAndTheRestAreCountedWhenThereIsRoom()
    {
        Referral("""{"weekly_cap": 2, "tiers": [{"recruits": 1, "reward": null}, {"recruits": 2, "reward": null}, {"recruits": 3, "reward": null}]}""");
        var (a, b, c, d) = (Account("alice"), Account("bob"), Account("carol"), Account("dave"));
        foreach (var r in new[] { b, c, d }) Recruit(r, a);
        Qualify(b);
        Qualify(c);
        at += Hour;
        Qualify(d);
        var week = Status(a);
        var dave = week["recruits"]!.AsArray().Single(r => (string)r!["handle"]! == "dave")!;
        Assert.Equal((true, false), ((bool)dave["qualified"]!, (bool)dave["paid"]!));  // qualified, over the cap
        Assert.Equal(3, (int)week["qualifiedCount"]!);
        Assert.Equal([true, true, false], week["tiers"]!.AsArray().Select(t => (bool)t!["reached"]!));

        at += 8 * Day;
        var later = Status(a);
        Assert.True((bool)later["recruits"]!.AsArray().Single(r => (string)r!["handle"]! == "dave")!["paid"]!);
        Assert.Equal([true, true, true], later["tiers"]!.AsArray().Select(t => (bool)t!["reached"]!));
    }

    [Fact]
    public void ABannedRecruitDoesNotCountHisUnpaidEntryIsDroppedAndAPaidOneStays()
    {
        var (a, b, c) = (Account("alice"), Account("bob"), Account("carol"));
        Recruit(b, a);
        Recruit(c, a);
        Qualify(c);
        Ban("carol");  // counted already: it stays
        Ban("bob");  // not yet: dropped
        var status = Status(a);
        var recruits = status["recruits"]!.AsArray();
        Assert.Equal(["carol"], recruits.Select(r => (string)r!["handle"]!));
        Assert.Equal(1, (int)status["qualifiedCount"]!);
        Assert.True((bool)status["tiers"]![0]!["reached"]!);

        Qualify(b);  // a banned recruit never qualifies
        Assert.Single(Recruits(a));
        Assert.Equal("already", state.EnterReferralCode(b, Code(a)).Status);  // and cannot enter another code
    }

    [Fact]
    public void TheDiscordMilestoneCountsTheRecruitsWhoLinkedDiscord()
    {
        Referral("""{"discord_milestones": [{"recruits": 1, "reward": {"gm": 50}}, {"recruits": 2, "reward": null}]}""");
        var (a, b, c) = (Account("alice"), Account("bob"), Account("carol"));
        Recruit(b, a);
        Recruit(c, a);
        var gm = Gm(a);
        Assert.Equal(0L, (long)Status(a)["discord"]!["linkedCount"]!);

        Sql("INSERT INTO discord_links (account_id, discord_id, linked_ts) VALUES (?, 'd-bob', 1)", b);
        var one = Status(a);
        Assert.Equal(1L, (long)one["discord"]!["linkedCount"]!);
        Assert.Equal([true, false], one["discord"]!["milestones"]!.AsArray().Select(m => (bool)m!["reached"]!));
        Assert.True((bool)one["recruits"]!.AsArray().Single(r => (string)r!["handle"]! == "bob")!["discordLinked"]!);
        Assert.Equal(gm + 50, Gm(a));

        Sql("INSERT INTO discord_links (account_id, discord_id, linked_ts) VALUES (?, 'd-carol', 1)", c);
        Assert.Equal([true, true], Status(a)["discord"]!["milestones"]!.AsArray().Select(m => (bool)m!["reached"]!));
        Assert.Equal(gm + 50, Gm(a));  // once, and the empty second one pays nothing
        Assert.Equal(1L, Paid(a));
    }

    [Fact]
    public void TheRecruitsAreSortedWithTheClosestToQualifyingFirstThenTheQualifiedLatestFirst()
    {
        var (a, bob, cat, dan, eve, fay) = (Account("alice"), Account("bob"), Account("cat"), Account("dan"), Account("eve"), Account("fay"));
        foreach (var r in new[] { bob, cat, dan, eve, fay }) Recruit(r, a);
        Score(bob, 5);  // 0.5
        Play(cat, 1);
        Score(cat, 8);  // 0.5 + 0.8
        Qualify(eve);
        at += Hour;
        Qualify(fay);
        var list = Recruits(a);
        Assert.Equal(["cat", "bob", "dan", "fay", "eve"], list.Select(r => (string)r!["handle"]!));
        Assert.Equal([1.0, 0.0, 0.0, 2.0, 2.0], list.Select(r => (double)r!["hours"]!));
        Assert.Equal([8.0, 5.0, 0.0, 10.0, 10.0], list.Select(r => (double)r!["score"]!));
        Assert.Equal(Hour, (long)list[3]!["qualifiedAt"]! - (long)list[4]!["qualifiedAt"]!);
    }

    [Fact]
    public void AnOperatorListsAndRevokesAnUnpaidEntryOnly()
    {
        var (a, b, c) = (Account("alice"), Account("bob"), Account("carol"));
        Recruit(b, a);
        Recruit(c, a);
        Qualify(c);

        var (done, message) = state.RevokeReferral(b);
        Assert.True(done);
        Assert.Contains("dropped", message);
        Assert.Empty(Recruits(a).Where(r => (string)r!["handle"]! == "bob"));
        Assert.Equal("already", state.EnterReferralCode(b, Code(a)).Status);
        Assert.False(state.RevokeReferral(b).Done);  // already dropped
        Assert.Contains("already paid", state.RevokeReferral(c).Message);  // no clawback
        Assert.False(state.RevokeReferral(a).Done);  // alice entered no code

        var report = state.ReferralReport(null);
        Assert.Contains(report, l => l.Contains("alice") && l.Contains("1 counted"));
        Assert.Contains(report, l => l.Contains("bob") && l.EndsWith("dropped"));
        Assert.Contains(report, l => l.Contains("carol") && l.EndsWith("paid"));
        Assert.Contains(state.ReferralReport(b), l => l.Contains("entered the code of"));
        Assert.Equal(["no referral"], state.ReferralReport(Account("dave")));
    }

    [Fact]
    public void TheSettingsAreReadAgainWithoutARestartAndAWrongOneChangesNothing()
    {
        File.WriteAllText(SettingsFile, "{}");
        var watch = new GmBonusWatch(state, SettingsFile, null) { Log = log.Add };
        var (a, b, c, d) = (Account("alice"), Account("bob"), Account("carol"), Account("dave"));
        void Write(string json)
        {
            File.WriteAllText(SettingsFile, json);
            File.SetLastWriteTimeUtc(SettingsFile, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(++edits));
        }

        Write("""{"referral": {"recruit_reward": {"gm": 10}}}""");
        watch.Check();
        Assert.Contains(log, l => l.StartsWith("referral: 1 read"));
        var gm = Gm(b);
        Recruit(b, a);
        Assert.Equal(gm + 10, Gm(b));

        log.Clear();
        Write("""{"referral": {"recruit_reward": {"gm": 10}, "weekly_cap": 0}}""");
        watch.Check();
        Assert.Single(log);
        Assert.Contains("referral: not applied, the referral settings in force stay: referral.weekly_cap", log[0]);
        gm = Gm(c);
        Recruit(c, a);
        Assert.Equal(gm + 10, Gm(c));  // the reward in force stays

        log.Clear();
        Write("""{"referral": {"recruit_reward": {"items": [{"id": 99999, "count": 1}]}}}""");
        watch.Check();
        Assert.Contains("item 99999 is not in the catalogue", Assert.Single(log));

        log.Clear();
        Write("""{"max_body_bytes": 5}""");  // the block removed: the defaults, no reward
        watch.Check();
        gm = Gm(d);
        Recruit(d, a);
        Assert.Equal(gm, Gm(d));
    }

    [Theory]
    [InlineData("""{"weekly_cap": 0}""", "referral.weekly_cap")]
    [InlineData("""{"qualify_score": -1}""", "referral.qualify_score")]
    [InlineData("""{"qualify_hours": "2"}""", "referral.qualify_hours")]
    [InlineData("""{"enter_hours": -5}""", "referral.enter_hours")]
    [InlineData("""{"existing_days": 1.5}""", "referral.existing_days")]
    [InlineData("""{"recruit_reward_gm": 5}""", "referral.recruit_reward_gm: unknown")]
    [InlineData("""{"tiers": [{"recruits": 1}, {"recruits": 1}]}""", "listed twice")]
    [InlineData("""{"tiers": [{"recruits": 0}]}""", "referral.tiers")]
    [InlineData("""{"tiers": {"recruits": 1}}""", "referral.tiers")]
    [InlineData("""{"discord_milestones": [{"recruits": 1, "rewards": null}]}""", "referral.discord_milestones")]
    [InlineData("""{"recruit_reward": {"gm": -1}}""", "referral.recruit_reward.gm")]
    [InlineData("""{"recruit_reward": {"coins": 1}}""", "referral.recruit_reward")]
    [InlineData("""{"recruit_reward": {"items": [{"id": 1}]}}""", "referral.recruit_reward.items")]
    [InlineData("""{"tiers": [{"recruits": 2, "reward": {"items": [{"id": 1, "count": 0}]}}]}""", "referral.tiers[2].reward.items")]
    [InlineData("""[]""", "referral: an object")]
    public void AWrongReferralBlockIsRefusedWithWhatIsWrong(string block, string why)
    {
        var e = Assert.Throws<InvalidDataException>(() => Settings.ParseReferral(JsonNode.Parse(block)));
        Assert.Contains(why, e.Message);
    }

    [Fact]
    public void TheDefaultsAreTheDocumentedOnesWithNoReward()
    {
        Settings.ParseReferral(null);
        Settings.ParseReferral(JsonNode.Parse("{}"));
        var a = Account("alice");
        var status = Status(a);
        Assert.Equal((2.0, 10L), ((double)status["qualification"]!["hours"]!, (long)status["qualification"]!["score"]!));
        Assert.Equal([1L, 3L, 5L, 10L, 20L], status["tiers"]!.AsArray().Select(t => (long)t!["recruits"]!));
        Assert.All(status["tiers"]!.AsArray(), t => Assert.Null(t!["reward"]));
        Assert.Equal([1L], status["discord"]!["milestones"]!.AsArray().Select(t => (long)t!["recruits"]!));
        Assert.Null(status["recruitReward"]);
    }

    [Fact]
    public void TheTablesAreMadeAtEveryStartAndTheReleaseTimeIsWrittenOnce()
    {
        var file = Path.Combine(tmp, "schema.db");
        var first = new Store(file);
        using (var con = first.Open())
        {
            con.Exec("UPDATE referral_release SET ts = 5");
            con.Exec("DROP TABLE referral_payouts");
        }
        var second = new Store(file);
        using var check = second.Open();
        Assert.Equal(5L, check.Scalar("SELECT ts FROM referral_release"));
        Assert.Equal(1L, check.Scalar("SELECT COUNT(*) FROM referral_release"));
        Assert.Equal(0L, check.Scalar("SELECT COUNT(*) FROM referral_payouts"));
        Assert.Equal((long)Store.Version, check.Scalar("PRAGMA user_version"));
    }

    [Fact]
    public async Task TheEndpointsAnswerTheSignedInPlayerAndRefuseWithTheReason()
    {
        var table = new Replay.HarTable();
        table.Entries[new("POST", "gameserver.test", Replay.CONNECT, null)] = new Replay.Hit(2, "", 200, [], "{}"u8.ToArray());
        await using var server = await Loopback.StartAsync(new Replay.Game(table, Cfg, state, tmp), tmp);
        var (a, b) = (Account("alice"), Account("bob"));
        string Cookie(long id) => $"{SignIn.SessionCookie}={state.Store.CreateWebSession(id, TimeSpan.FromHours(1))}";
        Task<Reply> Post(long? who, string body, bool ownPage = true)
        {
            List<(string, string)> headers = [("Content-Type", "application/json")];
            if (who is { } id) headers.Add(("Cookie", Cookie(id)));
            if (ownPage) headers.Add(("X-Requested-With", "XMLHttpRequest"));
            return server.SendAsync("POST", "web.test", "/account/referral/code", headers, Encoding.UTF8.GetBytes(body));
        }

        Assert.Equal(401, (await server.SendAsync("GET", "web.test", "/account/referral")).Status);
        var mine = await server.SendAsync("GET", "web.test", "/account/referral", [("Cookie", Cookie(a))]);
        Assert.Equal(200, mine.Status);
        var json = JsonNode.Parse(mine.Body)!;
        var code = (string)json["code"]!;
        Assert.Equal($"https://web.test/join/{code}", (string)json["link"]!);
        Assert.True((bool)json["canEnter"]!);
        Assert.Empty(json["recruits"]!.AsArray());
        Assert.Equal(5, json["tiers"]!.AsArray().Count);

        Assert.Equal(401, (await Post(null, $$"""{"code": "{{code}}"}""")).Status);
        Assert.Equal(400, (await Post(b, $$"""{"code": "{{code}}"}""", ownPage: false)).Status);
        Assert.Equal(400, (await Post(b, "not json")).Status);
        Assert.Equal(400, (await Post(b, """{"code": 5}""")).Status);
        var unknown = await Post(b, """{"code": "ZZZZ2222"}""");
        Assert.Equal((409, "unknown"), (unknown.Status, (string)JsonNode.Parse(unknown.Body)!["status"]!));
        var own = await Post(a, $$"""{"code": "{{code}}"}""");
        Assert.Equal((409, "own"), (own.Status, (string)JsonNode.Parse(own.Body)!["status"]!));

        var ok = await Post(b, $$"""{"code": "{{code.ToLowerInvariant()}}"}""");
        Assert.Equal(200, ok.Status);
        var accepted = JsonNode.Parse(ok.Body)!;
        Assert.Equal(("ok", "alice"), ((string)accepted["status"]!, (string)accepted["recruiter"]!));
        Assert.Null(accepted["reward"]);
        var again = await Post(b, $$"""{"code": "{{code}}"}""");
        Assert.Equal((409, "already"), (again.Status, (string)JsonNode.Parse(again.Body)!["status"]!));

        var theirs = JsonNode.Parse((await server.SendAsync("GET", "web.test", "/account/referral", [("Cookie", Cookie(b))])).Body)!;
        Assert.Equal(("alice", code, false), ((string)theirs["recruiter"]!, (string)theirs["enteredCode"]!, (bool)theirs["canEnter"]!));
        var seen = JsonNode.Parse((await server.SendAsync("GET", "web.test", "/account/referral", [("Cookie", Cookie(a))])).Body)!["recruits"]!.AsArray();
        Assert.Equal(["bob"], seen.Select(r => (string)r!["handle"]!));
    }
}
