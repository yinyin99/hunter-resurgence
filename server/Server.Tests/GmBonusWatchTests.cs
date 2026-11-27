using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>Editing `harvest_gm_bonus` in settings.json takes effect without a restart, and a wrong edit changes nothing (docs/server.md#harvest-gm-bonus).</summary>
public class GmBonusWatchTests : StateTestBase
{
    const string Weekend = """{"multiplier": 2, "from": "2026-11-27T15:00:00Z", "until": "2026-11-30T07:00:00Z"}""";

    readonly List<string> log = [];
    int edits;

    string SettingsFile => Path.Combine(tmp, "settings.json");

    static long T(string iso) => RewardEvents.ParseTime(iso)!.Value;

    /// <summary>The settings file with this content; each write gets its own time, as the watch tells a change by it.</summary>
    void Write(string json)
    {
        File.WriteAllText(SettingsFile, json);
        File.SetLastWriteTimeUtc(SettingsFile, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(++edits));
    }

    GmBonusWatch Start(double at, string json = "{}")
    {
        state = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES);
        state.Clock = () => at;
        Write(json);
        return new GmBonusWatch(state, SettingsFile, null) { Log = log.Add };
    }

    long Events()
    {
        using var con = state.Store.Open();
        return (long)con.Scalar("SELECT COUNT(*) FROM events_log")!;
    }

    [Fact]
    public void AGoodEditIsAppliedWithoutARestartAndABrokenOneKeepsTheBonusesAndSaysWhy()
    {
        long from = T("2026-11-27T15:00:00Z");
        var watch = Start(from - 3600);
        Assert.Equal(1, state.GmMultiplier(from));

        Write($$"""{"harvest_gm_bonus": [{{Weekend}}]}""");
        watch.Check();
        Assert.Equal([1.0, 2], new[] { from - 1, from }.Select(t => state.GmMultiplier(t)));
        Assert.Equal(1, Events());  // written as soon as it is read, before it starts
        Assert.Single(log);

        Write($$"""{"harvest_gm_bonus": [{{Weekend}}, {"multiplier": 3, "from": "2026-11-29T00:00:00Z", "until": "2026-12-01T00:00:00Z"}]}""");
        watch.Check();  // overlaps the weekend
        Write("{ not json");
        watch.Check();
        Write("""{"harvest_gm_bonus": [{"multiplier": 0, "until": "2026-12-01T00:00:00Z"}]}""");
        watch.Check();
        Assert.Equal(2, state.GmMultiplier(from));
        Assert.Equal(1, Events());
        Assert.Equal(4, log.Count);
        Assert.Contains("overlap", log[1]);
        Assert.All(log.Skip(1), line => Assert.Contains("not applied, the bonuses in force stay", line));

        Write("""{"harvest_gm_bonus": []}""");  // removing every bonus is a good edit too
        watch.Check();
        Assert.Equal(1, state.GmMultiplier(from));
    }

    [Fact]
    public void AnUnchangedFileOrAnEditOfAnotherSettingIsNotReadAgain()
    {
        var watch = Start(T("2026-11-27T15:00:00Z"));
        Write($$"""{"harvest_gm_bonus": [{{Weekend}}]}""");
        watch.Check();
        Assert.Single(log);
        watch.Check();  // same file
        Write($$"""{"max_body_bytes": 5, "harvest_gm_bonus": [{{Weekend}}]}""");
        watch.Check();  // another setting changed
        Assert.Single(log);
    }

    [Fact]
    public void TodaysSingleObjectStartsWhenItIsReadAndEndsAtItsUntil()
    {
        long at = T("2026-11-27T15:00:00Z"), until = T("2026-11-30T07:00:00Z");
        var watch = Start(at);
        Write("""{"harvest_gm_bonus": {"multiplier": 2, "until": "2026-11-30T07:00:00Z"}}""");
        watch.Check();
        Assert.Equal([2.0, 2, 1], new[] { at, until - 1, until }.Select(t => state.GmMultiplier(t)));
    }
}
