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
        // ids from 4 on: the server drops 0, 1 and 2 from every dependency list (#1012)
        state = New("needs.db", $$"""
            {"missions": [
              {"id": 4, "title": "First", "dependencies": [], "rewards": [], "objectives": [{"id": 0, "title": "{{Deer}}"}]},
              {"id": 5, "title": "Second", "dependencies": [4], "rewards": [], "objectives": [{"id": 0, "title": "{{Deer}}"}]},
              {"id": 6, "title": "Third", "dependencies": [4, 99], "rewards": [], "objectives": [{"id": 0, "title": "{{Deer}}"}]}]}
            """);
        Assert.Equal("""["First"]""", Needs(5));
        Assert.Equal("""["First", "#99"]""", Needs(6));
        state.MissionCall(ME, "activate", System.Text.Encoding.ASCII.GetBytes("mission_id=4"));
        state.HeartbeatReply(ME, Events(Shot(1)));
        Assert.Equal("4", Completed());
        Assert.Equal("[]", Needs(5));
        Assert.Equal("""["#99"]""", Needs(6));
    }
}
