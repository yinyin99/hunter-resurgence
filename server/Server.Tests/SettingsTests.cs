using System.Net;
using System.Text.Json.Nodes;

namespace Resurgence.Server.Tests;

public class SettingsTests : IDisposable
{
    readonly string tmp = Directory.CreateTempSubdirectory("settings").FullName;

    public void Dispose() => Directory.Delete(tmp, true);

    JsonObject Minimal()
    {
        var o = new JsonObject();
        foreach (var k in Replay.HostKeys) o[k] = k[..^"_host".Length] + ".test";
        o["install_dir"] = "game-copy";
        o["listeners"] = new JsonArray(new JsonObject { ["address"] = "127.0.0.1", ["port"] = 80 });
        return o;
    }

    Settings Load(JsonObject o)
    {
        var file = Path.Combine(tmp, "settings.json");
        File.WriteAllText(file, o.ToJsonString());
        return Settings.Load(file);
    }

    [Fact]
    public void TheCommunityDivisorDefaultsToTwentyAndNeedsAWholeNumberOfAtLeastOne()
    {
        Assert.Equal(20, (int)Load(Minimal()).Cfg["event_community_divisor"]!);
        var five = Minimal();
        five["event_community_divisor"] = 5;
        Assert.Equal(5, (int)Load(five).Cfg["event_community_divisor"]!);
        foreach (var bad in new JsonNode[] { 0, -3, 2.5, "20" })
        {
            var o = Minimal();
            o["event_community_divisor"] = bad;
            Assert.Throws<InvalidDataException>(() => Load(o));
        }
    }

    [Fact]
    public void AllLicensesDefaultsToTrueAndNeedsTrueOrFalse()
    {
        Assert.True((bool)Load(Minimal()).Cfg["all_licenses"]!);
        var off = Minimal();
        off["all_licenses"] = false;
        Assert.False((bool)Load(off).Cfg["all_licenses"]!);
        foreach (var bad in new JsonNode[] { 0, "true" })
        {
            var o = Minimal();
            o["all_licenses"] = bad;
            Assert.Throws<InvalidDataException>(() => Load(o));
        }
    }

    [Fact]
    public void MinimalFileGetsDefaultsAndPathsRelativeToTheFile()
    {
        var s = Load(Minimal());
        Assert.Equal(Path.Combine(tmp, "data"), s.DataDir);
        Assert.Equal(Path.Combine(tmp, "recordings"), s.RecordingsDir);
        Assert.Equal(Path.Combine(tmp, "mirror"), s.MirrorDir);
        Assert.Equal(Path.Combine(tmp, "game-copy"), (string)s.Cfg["install_dir"]!);
        Assert.Equal("gameserver.test", (string)s.Cfg["gameserver_host"]!);
        Assert.Null(s.VariantsFile);
        Assert.True((bool)s.Cfg["offline_mode"]!);  // a local server unless the file says otherwise
        Assert.Equal((IPAddress.Loopback, 80, null), (s.Listeners[0].Address, s.Listeners[0].Port, s.Listeners[0].Cert));
        Assert.Empty(s.TrustedProxies);
        Assert.Equal(("X-Game-Host", Settings.DefaultMaxBodyBytes), (s.HostHeader, s.MaxBodyBytes));
    }

    [Fact]
    public void OnlyTheTwoWebHostsAreRequiredAndTheOthersAreNamesNoClientAsks()
    {
        var o = Minimal();
        foreach (var k in Replay.HostKeys.Where(k => k is not ("web_host" or "game_web_host"))) o.Remove(k);
        var s = Load(o);
        Assert.Equal(("web.test", "api.invalid"), ((string)s.Cfg["web_host"]!, (string)s.Cfg["api_host"]!));
        Assert.Equal(Replay.HostKeys.Length, Replay.HostKeys.Select(k => (string)s.Cfg[k]!).Distinct().Count());  // distinct: the server routes by them
    }

    [Fact]
    public void AllOptionsAreRead()
    {
        var o = Minimal();
        var abs = Path.Combine(tmp, "elsewhere");
        o["data_dir"] = abs;
        o["variants_file"] = "v.json";
        o["listeners"] = new JsonArray(new JsonObject { ["address"] = "::1", ["port"] = 443, ["cert"] = "c.pem", ["key"] = "c.key" });
        o["trusted_proxies"] = new JsonArray("10.0.0.0/8", "192.0.2.7", "fd00::/8");
        o["host_header"] = "X-Orig";
        o["max_body_bytes"] = 1000;
        o["offline_mode"] = false;
        var s = Load(o);
        Assert.Equal(abs, s.DataDir);
        Assert.Equal(Path.Combine(tmp, "v.json"), s.VariantsFile);
        Assert.Equal((IPAddress.IPv6Loopback, Path.Combine(tmp, "c.pem"), Path.Combine(tmp, "c.key")), (s.Listeners[0].Address, s.Listeners[0].Cert, s.Listeners[0].Key));
        Assert.Equal(3, s.TrustedProxies.Count);
        Assert.True(s.TrustedProxies[0].Contains(IPAddress.Parse("10.1.2.3")));
        Assert.True(s.TrustedProxies[1].Contains(IPAddress.Parse("192.0.2.7")));
        Assert.False(s.TrustedProxies[1].Contains(IPAddress.Parse("192.0.2.8")));
        Assert.Equal(("X-Orig", 1000L), (s.HostHeader, s.MaxBodyBytes));
        Assert.False((bool)s.Cfg["offline_mode"]!);
    }

    [Theory]
    [InlineData("web_host", null, "web_host: missing")]
    [InlineData("game_web_host", null, "game_web_host: missing")]
    [InlineData("install_dir", null, "install_dir: missing")]
    [InlineData("listeners", null, "listeners: needs at least one")]
    [InlineData("trusted_proxies", "not an address", "trusted_proxies")]
    [InlineData("max_body_bytes", "-1", "max_body_bytes")]
    [InlineData("offline_mode", "\"maybe\"", "offline_mode")]
    public void BadFilesSayWhatIsWrong(string key, string? value, string message)
    {
        var o = Minimal();
        if (value is null) o.Remove(key);
        else o[key] = key == "trusted_proxies" ? new JsonArray(value) : JsonNode.Parse(value);
        Assert.Contains(message, Assert.Throws<InvalidDataException>(() => Load(o)).Message);
    }

    [Fact]
    public void ListenersAreAdminOnlyWhenMarkedAndLoopback()
    {
        var o = Minimal();
        Assert.False(Load(o).Listeners[0].Admin);
        o["listeners"] = new JsonArray(new JsonObject { ["address"] = "127.0.0.1", ["port"] = 80, ["admin"] = true });
        Assert.True(Load(o).Listeners[0].Admin);
        o["listeners"] = new JsonArray(new JsonObject { ["address"] = "0.0.0.0", ["port"] = 80, ["admin"] = true });
        Assert.Contains("loopback", Assert.Throws<InvalidDataException>(() => Load(o)).Message);
        o["listeners"] = new JsonArray(new JsonObject { ["address"] = "127.0.0.1", ["port"] = 80, ["admin"] = "yes" });
        Assert.Contains("admin is true or false", Assert.Throws<InvalidDataException>(() => Load(o)).Message);
    }

    [Fact]
    public void ListenerCertNeedsItsKey()
    {
        var o = Minimal();
        o["listeners"] = new JsonArray(new JsonObject { ["address"] = "127.0.0.1", ["port"] = 443, ["cert"] = "c.pem" });
        Assert.Contains("cert and key", Assert.Throws<InvalidDataException>(() => Load(o)).Message);
    }

    [Fact]
    public void MissingOrBrokenFileIsReported()
    {
        Assert.Throws<InvalidDataException>(() => Settings.Load(Path.Combine(tmp, "none.json")));
        File.WriteAllText(Path.Combine(tmp, "bad.json"), "{");
        Assert.Throws<InvalidDataException>(() => Settings.Load(Path.Combine(tmp, "bad.json")));
    }

    [Fact]
    public void TheShippedExampleLoads()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir.FullName, "hunter-resurgence.slnx"))) dir = dir.Parent!;
        var file = Path.Combine(dir.FullName, "server", "Server", "settings.example.json");
        var s = Settings.Load(file);
        Assert.Equal(2, s.Listeners.Count);
        Assert.All(s.Listeners, l => Assert.True(l.Admin));
        Assert.Equal((Settings.DefaultMaxBodyBytes, Settings.DefaultHostHeader), (s.MaxBodyBytes, s.HostHeader));
        Assert.True((bool)s.Cfg["offline_mode"]!);
        Assert.NotNull(s.Cfg["install_dir"]);
        Assert.NotNull(s.VariantsFile);
        Assert.NotNull(s.DataDir);
        var keys = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        foreach (var key in new[] { "install_dir", "variants_file", "data_dir", "listeners" })
            Assert.True(keys.ContainsKey(key), key);
    }

    [Fact]
    public void AdminListenersAreTheMarkedOnes()
    {
        var o = Minimal();
        o["listeners"] = new JsonArray(
            new JsonObject { ["address"] = "127.0.0.1", ["port"] = 80, ["admin"] = true },
            new JsonObject { ["address"] = "127.0.0.1", ["port"] = 8080 });
        Assert.Equal([(IPAddress.Loopback, 80)], Load(o).AdminListeners);
        Assert.Empty(Load(Minimal()).AdminListeners);
    }

    [Fact]
    public void AnEventIsCheckedAndKeptForTheState()
    {
        Assert.Null(SeasonalEvent.Parse(Load(Minimal()).Cfg["event"]));
        var o = Minimal();
        o["event"] = new JsonObject
        {
            ["name"] = "season", ["define"] = "SEASON", ["reserves"] = new JsonArray(1, 2), ["start"] = "2031-03-10T00:00:00Z", ["end"] = "2031-03-20T00:00:00Z",
        };
        var e = SeasonalEvent.Parse(Load(o).Cfg["event"])!;
        Assert.Equal(("season", "SEASON"), (e.Name, e.Define));
        Assert.Equal([1, 2], e.Reserves);
        o["event"]!["end"] = "2031-03-01T00:00:00Z";
        Assert.Throws<InvalidDataException>(() => Load(o));
    }
}
