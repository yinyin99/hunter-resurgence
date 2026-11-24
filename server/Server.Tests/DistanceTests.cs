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
