using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The running event's community missions: one sum over the players who play events, a target divided and rounded up, one payout per contributor, and the panel.</summary>
public class CommunityMissionTests : StateTestBase
{
    const long B = 8;

    // mission 1 is the event's personal one; community 1 needs 41 harvests (3 at the default divisor 20), community 2 needs 40 spots (2), both made-up numbers
    static readonly Facts EventFacts = Facts.Parse("""
        {"format": 1,
         "events": {"season": {"title": "Season", "missions": [1], "items": [10], "community": [
           {"id": 1, "title": "Harvest a Test Deer", "target": 41, "gm": 100, "em": 5},
           {"id": 2, "title": "Spot a Test Deer", "target": 40, "gm": 7, "em": 0}]}}}
        """);

    static readonly DateTimeOffset Start = new(2031, 3, 10, 0, 0, 0, TimeSpan.Zero);

    State New(int divisor = 20, string db = "state.db")
    {
        var app = APP;
        app["missions"] = JsonNode.Parse("""[{"id": 1, "title": "Personal", "dependencies": [], "rewards": [{"text": "50 gm$"}, {"text": "Test Scope"}], "objectives": [{"id": 0, "title": "Harvest a Test Deer"}, {"id": 1, "title": "Spot a Test Deer"}]}]""");
        var s = new State(Path.Combine(tmp, db), Py.DumpBytes(CONNECT), BASE, ICON, app, LOCALE, TEXTURES, offline: false, facts: EventFacts,
                          seasonal: new("season", "SEASON", [], Start, Start.AddDays(10))) { CommunityDivisor = divisor };
        s.Clock = () => Start.ToUnixTimeSeconds() + 3600;
        s.CreateAccount(B, "second");
        Scoring.StaticHost = "static.test";
        Scoring.OwnLogo = "testlogo";
        Scoring.Variants = new() { [1] = (30, 260, 1.1), [17] = (600, 1800, 0.9) };
        return s;
    }

    JsonArray Hb(long account, params JsonNode[] events) => Reply(state.HeartbeatReply(account, Events(events)))["updates"]!.AsArray();

    static JsonNode[] Kill(int individual) => AccountIsolationTests.Harvest(individual, 72270);

    string Counts(long account = ME) => string.Join(",", state.EventPanel(account)!["community"]!.AsArray().Select(c => $"{c!["progress"]}/{c["target"]}"));

    double Gm(long account) => state.Wallet(account)["gm"]!.GetValue<double>();

    [Fact]
    public void TestTheTargetIsDividedAndRoundedUpAndProgressSumsOverPlayers()
    {
        state = New();
        Hb(ME, Kill(1));
        Hb(B, Kill(2));
        Assert.Equal("2/3,0/2", Counts());
        Assert.Equal("2/3,0/2", Counts(B));
        var undivided = New(1, "one.db").EventPanel(ME)!["community"]!;  // divisor 1: the facts' numbers
        Eq("[41, 40]", new JsonArray([.. undivided.AsArray().Select(c => c!["target"]!.DeepClone())]));
    }

    [Fact]
    public void TestEveryContributorIsPaidOnceWhenTheTargetIsReached()
    {
        state = New();
        double ownBefore = Gm(ME), otherBefore = Gm(B);
        Hb(ME, Kill(1));
        Hb(B, Kill(2));
        var third = Hb(ME, Kill(3));  // the third harvest: 3/3
        Assert.Equal("3/3,0/2", Counts());
        Assert.Contains(third, u => (string)u!["type"]! == "gm");
        var own = Gm(ME);
        Assert.True(own >= ownBefore + 100, "the reward is paid to the player whose heartbeat completed it");
        Assert.True(Gm(B) < otherBefore + 100, "the other contributor is paid at their next heartbeat");
        Hb(B);
        Assert.True(Gm(B) >= otherBefore + 100);
        var paid = Gm(B);
        Hb(B);
        Hb(ME, Kill(4));  // past the target: counts nothing, pays nothing more
        Hb(B);
        Assert.Equal("3/3,0/2", Counts());
        Assert.Equal(paid, Gm(B));
        Assert.Equal(1, state.WalletPage(B, "gm", 0).Count(r => (string)r!["summary"]! == "gm$ community reward"));
        Assert.Equal(1, state.WalletPage(ME, "gm", 0).Count(r => (string)r!["summary"]! == "gm$ community reward"));
    }

    [Fact]
    public void TestAPlayerWhoDoesNotPlayEventsCountsNothingAndSeesNoPanel()
    {
        state = New();
        state.Store.SetPlaysEvents(B, false);
        Hb(B, Kill(2));
        Assert.Equal("0/3,0/2", Counts());
        Assert.Null(state.EventPanel(B));
        Hb(B);
        Assert.DoesNotContain(state.WalletPage(B, "gm", 0), r => (string)r!["summary"]! == "gm$ community reward");
    }

    [Fact]
    public void TestNothingCountsOutsideTheEventsWindow()
    {
        state = New();
        state.Clock = () => Start.AddDays(11).ToUnixTimeSeconds();
        Hb(ME, Kill(1));
        Assert.Null(state.EventPanel(ME));
        state.Clock = () => Start.ToUnixTimeSeconds() + 3600;
        Assert.Equal("0/3,0/2", Counts());
    }

    [Fact]
    public void TestThePanelListsThePersonalMissionTheTopContributorsAndTheStoreItems()
    {
        state = New();
        Hb(ME, Kill(1));
        Hb(ME, Kill(2));
        Hb(B, Kill(3));
        var panel = state.EventPanel(ME)!;
        Eq($$"""["season", "Season", {{Start.ToUnixTimeSeconds()}}]""", T(panel["name"], panel["title"], panel["start"]));
        Eq("""[["hunter", 2], ["second", 1]]""", JsonNode.Parse(Py.Dumps(new JsonArray([.. panel["contributors"]!.AsArray().Select(c => (JsonNode?)new JsonArray(c!["handle"]!.DeepClone(), c["count"]!.DeepClone()))]))));
        var mission = panel["missions"]![0]!;
        Eq("""[1, "Personal", "available", "50 gm$, Test Scope"]""", T(mission["id"], mission["title"], mission["state"], mission["reward"]));
        Eq("[false, false]", new JsonArray([.. mission["objectives"]!.AsArray().Select(o => o!["done"]!.DeepClone())]));
    }

    [Fact]
    public void TestWithNoEventTheDivisorOrCommunityMissionsThereIsNoPanel()
    {
        state = New();
        state.Clock = () => Start.ToUnixTimeSeconds() - 1;
        Assert.Null(state.EventPanel(ME));
        Assert.Equal(20, state.CommunityDivisor);
    }
}
