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
    [InlineData("api_host", null, "api_host: missing")]
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
}
