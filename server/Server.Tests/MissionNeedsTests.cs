using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>`needs` of the website's missions page: the open dependencies of a mission, by title.</summary>
public class MissionNeedsTests : MissionTestBase
{
    const string Deer = "Harvest a Test Deer";

    string Needs(long mission) => Py.Dumps(state.WebMissions(ME)!["missions"]!.AsArray().Single(m => (long)m!["id"]! == mission)!["needs"]);

    [Fact]
    public void TestACompletedDependencyDropsOutOfNeedsAndADependencyWithoutADefinitionIsListedById()
    {
        state = New("needs.db", $$"""
            {"missions": [
              {"id": 1, "title": "First", "dependencies": [], "rewards": [], "objectives": [{"id": 0, "title": "{{Deer}}"}]},
              {"id": 2, "title": "Second", "dependencies": [1], "rewards": [], "objectives": [{"id": 0, "title": "{{Deer}}"}]},
              {"id": 3, "title": "Third", "dependencies": [1, 99], "rewards": [], "objectives": [{"id": 0, "title": "{{Deer}}"}]}]}
            """);
        Assert.Equal("""["First"]""", Needs(2));
        Assert.Equal("""["First", "#99"]""", Needs(3));
        state.MissionCall(ME, "activate", System.Text.Encoding.ASCII.GetBytes("mission_id=1"));
        state.HeartbeatReply(ME, Events(Shot(1)));
        Assert.Equal("1", Completed());
        Assert.Equal("[]", Needs(2));
        Assert.Equal("""["#99"]""", Needs(3));
    }
}
