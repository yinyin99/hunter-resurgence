using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>A hunt's distance: the game's own at its disconnect, else the walk measured from the heartbeats' positions (invented positions).</summary>
public class DistanceTests : StateTestBase
{
    double now = Py.Now();

    public DistanceTests()
    {
        state = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES) { Clock = () => now };
        state.NoteLaunch(ME, "start=100&reserve=2"u8.ToArray());
    }

    /// <summary>A heartbeat `seconds` after the last one, from (x, z) at a height y.</summary>
    void At(double seconds, double x, double z, double y = 50)
    {
        now += seconds;
        state.HeartbeatReply(ME, Encoding.ASCII.GetBytes(FormattableString.Invariant($$"""{"events": [], "xpos": {{x}}, "ypos": {{y}}, "zpos": {{z}}}""")));
    }

    /// <summary>The player quits through the game's menu: no disconnect, the heartbeats stop and the timer closes the hunt.</summary>
    void Quit()
    {
        now += 200;
        state.CloseStaleHunts();
    }

    JsonNode? Distance() => Reply(state.ExpeditionReply(ME, "{}"u8.ToArray()))["stats"]!["distance"];

    [Fact]
    public void TestTheWalkIsTheSumOfTheStepsBetweenHeartbeatsAndIgnoresTheHeight()
    {
        At(30, 100, 100);
        At(30, 130, 140, y: 90);  // 30 east, 40 north (a 3-4-5 step) and 40 up: 50 m
        At(30, 130, 240, y: 10);  // 100 m
        Quit();
        Eq("150", Distance());
    }

    [Fact]
    public void TestAFastTravelAddsNothingForTheJump()
    {
        At(30, 100, 100);
        At(30, 130, 140);         // 50 m
        At(30, 130, 2540);        // 2.4 km in one heartbeat: a lodge or a tent, not a walk
        At(30, 130, 2590);        // 50 m from where the player stands now
        Quit();
        Eq("100", Distance());
    }

    [Fact]
    public void TestTheDisconnectsOwnDistanceIsPreferred()
    {
        At(30, 100, 100);
        At(30, 100, 400);         // 300 m measured
        now += 5;                 // the disconnect comes after the last heartbeat
        state.Disconnect(ME, """{"distance_travelled": 466}"""u8.ToArray());
        Eq("466", Distance());
    }

    [Fact]
    public void TestADisconnectWithoutADistanceKeepsTheMeasuredOne()
    {
        At(30, 100, 100);
        At(30, 100, 400);
        now += 5;                 // the disconnect comes after the last heartbeat
        state.Disconnect(ME, "{}"u8.ToArray());
        Eq("300", Distance());
    }

    [Fact]
    public void TestAHuntWithNoPositionStaysAtZero()
    {
        Hb();  // a heartbeat with events only
        now += 200;
        Hb(O("""{"event": 21, "weapon_id": 5, "ammunition_id": 10}"""));  // a position of zeros is none either
        state.HeartbeatReply(ME, """{"events": [], "xpos": 0, "ypos": 0, "zpos": 0}"""u8.ToArray());
        Quit();
        Eq("0", Distance());
    }

    [Fact]
    public void TestTheDistanceHereIsThisServersHuntsAndTheTotalAddsTheRestoredOne()
    {
        using (var con = state.Store.Open(ME))
            PlayerRows.SetStatistics(con, O("""{"animal": {}}"""), 2_000_000, 0, null, null);  // 2,000 km restored
        At(30, 100, 100);
        At(30, 100, 400);
        At(30, 100, 4_900);       // a fast travel of 4.5 km, left out
        At(30, 100, 4_950);       // 50 m
        Quit();
        var page = state.StatisticsPage(ME);
        Assert.Equal(2000.35, (double)page["distanceKm"]!, 6);
        Assert.Equal(0.35, (double)page["distanceKmHere"]!, 6);
    }
}

public class DistanceHereTests : StateTestBase
{
    double now = Py.Now();

    public DistanceHereTests()
    {
        state = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES) { Clock = () => now };
        state.NoteLaunch(ME, "start=100&reserve=2"u8.ToArray());
    }

    void At(double seconds, double x, double z)
    {
        now += seconds;
        state.HeartbeatReply(ME, Encoding.ASCII.GetBytes(FormattableString.Invariant($$"""{"events": [], "xpos": {{x}}, "ypos": 50, "zpos": {{z}}}""")));
    }

    double Here() => (double)state.StatisticsPage(ME)["distanceKmHere"]! * 1000;

    [Fact]
    public void TestAStaleHuntReopenedByALateHeartbeatMeasuresOnAndTakesItsDistanceAgainAtTheNextClose()
    {
        At(30, 0, 0);
        At(30, 0, 100);
        now += 200;
        state.CloseStaleHunts();  // the timer closes it with 100 m
        Assert.Equal(100, Here(), 3);
        At(300, 0, 5_000);        // late: the hunt reopens; the step over the gap is no walk
        Assert.Equal(100, Here(), 3);  // the open hunt's walk so far
        At(30, 0, 5_050);         // 50 m more
        Assert.Equal(150, Here(), 3);
        now += 200;
        state.CloseStaleHunts();
        Assert.Equal(150, Here(), 3);
        Eq("150", Reply(state.ExpeditionReply(ME, "{}"u8.ToArray()))["stats"]!["distance"]);
    }

    [Fact]
    public void TestTheOpenHuntsWalkSoFarIsInTheDistanceHere()
    {
        At(30, 0, 0);
        At(30, 60, 80);
        Assert.Equal(100, Here(), 3);
    }

    [Fact]
    public void TestANewLaunchClosesTheHuntWithItsMeasuredDistance()
    {
        At(30, 0, 0);
        At(30, 0, 100);
        state.NoteLaunch(ME, "start=100&reserve=2"u8.ToArray());  // the player started another hunt from the launcher: no disconnect came
        Assert.Equal(100, Here(), 3);
        Assert.Equal(0.1, (double)state.StatisticsPage(ME)["distanceKm"]!, 6);  // the closed hunt's statistics hold it
    }

    [Fact]
    public void TestAHuntBeforePlaytimeSinceIsNoDistanceHereButStaysInTheTotal()
    {
        At(30, 0, 0);
        At(30, 0, 300);
        state.Disconnect(ME, "{}"u8.ToArray());
        using (var con = state.Store.Open(ME))
            con.Exec("UPDATE expeditions SET start_ts = ? WHERE account_id = $a", State.DefaultPlaytimeSince - 1);  // a hunt of before the opening day
        state.NoteLaunch(ME, "start=100&reserve=2"u8.ToArray());
        At(30, 0, 0);
        At(30, 0, 100);
        now += 200;
        state.CloseStaleHunts();
        var page = state.StatisticsPage(ME);
        Assert.Equal(0.1, (double)page["distanceKmHere"]!, 6);
        Assert.Equal(0.4, (double)page["distanceKm"]!, 6);
    }
}
