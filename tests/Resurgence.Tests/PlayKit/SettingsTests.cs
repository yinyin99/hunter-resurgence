using Play;

namespace Resurgence.Tests.PlayKit;

public class SettingsTests
{
    const string Good = """{"server_url":"https://server.example.test/","api_host":"API.example.test","web_host":"www.example.test","photo_host":"api.example.test","note":"x"}""";

    [Fact]
    public void HostsAreTheHostKeysLowercasedDedupedWithTheBareNameOfWww()
    {
        var s = Settings.Parse(Good);
        Assert.Equal("https://server.example.test/", s.ServerUrl.AbsoluteUri);
        Assert.Equivalent(new[] { "api.example.test", "www.example.test", "example.test" }, s.Hosts);
    }

    [Theory]
    [InlineData("""{"server_url":"https://<community-server>","api_host":"a.example.test"}""")]   // placeholder url
    [InlineData("""{"server_url":"https://s.example.test","api_host":"<api-host>"}""")]           // placeholder host
    [InlineData("""{"server_url":"https://s.example.test"}""")]                                    // no hosts
    [InlineData("""{"api_host":"a.example.test"}""")]                                              // no url
    [InlineData("""{"server_url":"http://s.example.test","api_host":"a.example.test"}""")]         // not https
    [InlineData("""{"server_url":"https://s.example.test/?x=1","api_host":"a.example.test"}""")]   // query
    [InlineData("""{"server_url":"https://s.example.test","api_host":"a.example.test\n127.0.0.1 evil.test"}""")] // would inject a hosts line
    [InlineData("""{"server_url":"https://s.example.test","api_host":"10.0.0.1"}""")]              // an address, not a name
    [InlineData("not json")]
    public void BadSettingsAreRefused(string json) => Assert.Throws<InvalidOperationException>(() => Settings.Parse(json));

    [Fact]
    public void ExampleFileIsOnlyPlaceholders()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir.FullName, "tools", "play", "settings.example.json"))) dir = dir.Parent!;
        var e = Assert.Throws<InvalidOperationException>(() => Settings.Parse(File.ReadAllText(Path.Combine(dir.FullName, "tools", "play", "settings.example.json"))));
        Assert.Contains("server_url", e.Message);
    }
}
