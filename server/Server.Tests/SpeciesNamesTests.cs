using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>A species is named in full (the catalogue's name, "(Typical)" or "(Non-Typical)") in lists and items, and plain in sentences.</summary>
public class SpeciesNamesTests : StateTestBase
{
    const long Hunter = 8;

    public SpeciesNamesTests()
    {
        var app = APP;
        app["species"]!.AsArray().Add(O("""{"id": 101, "name": "Test Deer (Non-Typical)", "define": "MULE_DEER"}"""));
        state = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, app, LOCALE, TEXTURES);
    }

    [Fact]
    public void ATypicalAndANonTypicalSpeciesHaveTheirOwnNameAndAnUnknownOneItsId()
    {
        Assert.Equal(["Test Deer (Typical)", "Test Deer (Non-Typical)", "#999"], new[] { state.BoardName(1), state.BoardName(101), state.BoardName(999) });
    }

    [Fact]
    public void TheStatisticsListTheTypicalRowBeforeTheNonTypicalOneNextToIt()
    {
        state.CreateAccount(Hunter, "nontypical");
        using (var con = state.Store.Open(Hunter))
            PlayerRows.SetStatistics(con, O("""{"collectables": {}, "animal": {"56": {"kills": 1}, "101": {"kills": 2}, "0": {"kills": 1}, "1": {"kills": 3}}, "weapon": {}}"""), 0, 0, null, null);
        var names = state.StatisticsPage(Hunter)["animals"]!.AsArray().Select(a => (string)a!["name"]!);
        Assert.Equal(["Test Deer (Typical)", "Test Deer (Non-Typical)", "Test Dog", "Test Wolf"], names);
    }

    [Fact]
    public void TheHuntTheGalleryAndTheHarvestPageOfANonTypicalKillSayNonTypical()
    {
        state.CreateAccount(Hunter, "nontypical");
        state.NoteLaunch(Hunter, "start=100&reserve=2"u8.ToArray());
        var events = AccountIsolationTests.Harvest(71, 50000);
        events[^1]["extended_species"] = 101;
        state.HeartbeatReply(Hunter, Events(events));
        var update = Reply(state.HeartbeatReply(Hunter, Events(O("""{"event": 4, "hash": 1, "photo_type": 2, "individual": 71}"""))))["updates"]!.AsArray().Single(u => (int)u!["update"]! == 11)!;
        Assert.Equal(200, state.UploadReply(Hunter, new Uri((string)update["url"]!).Query.TrimStart('?'), [1, 2]).Status);
        Assert.Equal("Test Deer (Non-Typical)", (string)Reply(state.ExpeditionReply(Hunter, "{}"u8.ToArray()))["kills"]![0]!["speciesName"]!);  // the harvest page
        Assert.Equal("Test Deer (Non-Typical)", (string)state.PublicHunt(Hunter, null)!["kills"]![0]!["species"]!);
        Assert.Equal("Test Deer (Non-Typical)", (string)Reply(state.GalleryListReply(Hunter, [])!)["photos"]![0]!["animal"]!);
    }

    [Fact]
    public void AnAmmoOrSkillToastNamesTheSpeciesWithoutItsType()
    {
        Assert.Equal("Mule Deer Tracking: level 2 reached", Skills.LevelUpText("tracking", "Mule Deer", 2));
    }
}
