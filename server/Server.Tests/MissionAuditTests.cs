using System.Text;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The operator's audit of the recent mission completions: each flag on invented missions and events, and the Discord messages.</summary>
public class MissionAuditTests : MissionTestBase
{
    double now = 1_800_000_000;

    State Audited(string file, string catalogue)
    {
        var s = New(file, catalogue);
        s.Clock = () => now;
        return s;
    }

    List<string> Audit(double hours = 24, State? of = null, bool discord = false) => (of ?? state).AuditMissions(hours, discord: discord).ToList();

    static List<string> Flagged(List<string> report, string flag) => report.Where(l => l.StartsWith(flag + " | ")).ToList();

    static byte[] Q(string form) => Encoding.ASCII.GetBytes(form);

    string Me => System.Text.RegularExpressions.Regex.Escape(state.Store.HandleOf(ME)!);

    [Fact]
    public void TestAKillWithin5PercentOfTheTitlesBoundIsBorderline()
    {
        state = Audited("borderline.db", Catalogue(["Harvest a Test Deer from over 100 meters", "Harvest a Test Deer from over 50 meters", "Harvest a Test Deer at 100% Harvest Value"]));
        ActivateAll();
        Hb(Enter("Test Lake"));
        now += 600;
        Hb(Shot(1, distance: 10300));  // 103 m, 100 % value
        var report = Audit();
        Assert.Equal("missions audit, the last 24 h: 3 objectives completed by 1 accounts", report[0]);
        var line = Assert.Single(Flagged(report, "borderline"));  // 103 m is far over 50 m; 100 % value has nothing above it
        Assert.Matches($@"^borderline \| mission 1 Harvest a Test Deer from over 100 meters \| Harvest a Test Deer from over 100 meters \| {Me} \| harvest event \d+: distance 103 vs min 100$", line);
        Assert.Equal("borderline 1, unmet 0, fast 0, shared 3, spike 0", report[^1]);
        now += 25 * 3600;
        Assert.Equal("borderline 0, unmet 0, fast 0, shared 0, spike 0", Audit()[^1]);  // out of the window
    }

    [Fact]
    public void TestAnObjectiveCompletedWithin2MinutesOfItsMissionsActivationIsFast()
    {
        state = Audited("fast.db", Catalogue(["Harvest a Test Deer", "Harvest a Test Deer weighing at least 80 kg"]));
        state.MissionCall(ME, "activate", Q("mission_id=1"));
        Hb(Enter("Test Lake"));  // the first event after the activation: when it happened
        now += 500;
        state.MissionCall(ME, "activate", Q("mission_id=2"));
        Hb(Enter("Test Lake"));
        now += 60;
        Hb(Shot(1));  // 560 s after the first, 60 s after the second
        var report = Audit();
        Assert.Matches($@"^fast \| mission 2 Harvest a Test Deer weighing at least 80 kg \| Harvest a Test Deer weighing at least 80 kg \| {Me} \| harvest event \d+, 60 s after the activation$",
                       Assert.Single(Flagged(report, "fast")));
        Assert.Equal("borderline 0, unmet 0, fast 1, shared 0, spike 0", report[^1]);  // one kill met two missions: not shared
    }

    [Fact]
    public void TestOneKillThatMeetsObjectivesOf3MissionsIsShared()
    {
        state = Audited("shared.db", Catalogue(["Harvest a Test Deer", "Harvest a Test Deer with one shot", "Harvest a Test Deer with the Test Carbine"]));
        ActivateAll();
        Hb(Enter("Test Lake"));
        now += 600;
        Hb(Shot(1));
        var shared = Flagged(Audit(), "shared");
        Assert.Equal(3, shared.Count);
        Assert.All(shared, l => Assert.Matches(@"\| harvest event \d+ met objectives of 3 missions$", l));
    }

    [Fact]
    public void TestAHeldObjectiveNoStoredEventMeetsIsUnmetWhileTheAccountHuntsInTheWindow()
    {
        state = Audited("unmet.db", Catalogue(["Harvest a Test Deer"]));
        ActivateAll();
        Hb(Shot(1, distance: 2000));  // 20 m: the earlier rules finish it
        Assert.Equal("1", Completed());
        var strict = Audited("unmet.db", Catalogue(["Harvest a Test Deer from over 100 meters"]));
        var report = Audit(of: strict);
        Assert.Equal($"unmet | mission 1 Harvest a Test Deer from over 100 meters | Harvest a Test Deer from over 100 meters | {state.Store.HandleOf(ME)} | no harvest or clue since its activation meets it",
                     Assert.Single(Flagged(report, "unmet")));
        Assert.Equal("missions audit, the last 24 h: 0 objectives completed by 0 accounts", report[0]);
        now += 25 * 3600;
        Assert.Empty(Flagged(Audit(of: strict), "unmet"));  // no harvest or clue of the account in the window: it was not completed then
    }

    [Fact]
    public void TestAMissionCompletedMoreThan3TimesItsDailyAverageAndAtLeast5TimesIsASpike()
    {
        state = Audited("spike.db", Catalogue(["Harvest a Test Deer"]));
        var next = 100L;
        void Complete()
        {
            var account = next++;
            state.CreateAccount(account, $"hunter{account}");
            state.MissionCall(account, "activate_all_available", []);
            state.HeartbeatReply(account, Events(Shot(1)));
        }
        now -= 3 * 86400;
        for (var i = 0; i < 24; i++)  // 24 in the 14 days before: 12 / 7 a day
            Complete();
        now += 3 * 86400;
        for (var i = 0; i < 5; i++)
            Complete();
        Assert.Empty(Flagged(Audit(), "spike"));  // 5 is not more than 3 × 12 / 7
        Complete();
        Assert.Equal("spike | mission 1 Harvest a Test Deer | every objective | 6 accounts | 6 completions in 24 h vs 1.71 a day over the 14 days before",
                     Assert.Single(Flagged(Audit(), "spike")));
    }

    [Fact]
    public void TestTheDiscordReportIsMarkdownInMessagesOf2000CharactersAtMost()
    {
        var missions = string.Join(",", Enumerable.Range(1, 40).Select(i =>
            $$"""{"id": {{i}}, "title": "Mission *{{i}}*", "dependencies": [], "rewards": [], "objectives": [{"id": 0, "title": "Harvest a Test Deer"}]}"""));
        state = Audited("discord.db", "{\"missions\": [" + missions + "]}");
        ActivateAll();
        Hb(Enter("Test Lake"));
        now += 600;
        Hb(Shot(1));
        var output = Audit(discord: true);
        var messages = string.Join("\n", output).Split("\n---8<---\n");
        Assert.True(messages.Length > 1);
        Assert.All(messages, m => Assert.InRange(m.Length, 1, 2000));
        Assert.StartsWith("**missions audit, the last 24 h: 40 objectives completed by 1 accounts**\n- shared | mission 1 Mission \\*1\\* | Harvest a Test Deer |", messages[0]);
        Assert.EndsWith("\n**borderline 0, unmet 0, fast 0, shared 40, spike 0**", messages[^1]);
    }
}
