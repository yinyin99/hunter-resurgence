using System.Text.Json.Nodes;
using Backup;

namespace Resurgence.Tests.BackupTool;

/// <summary>settings.json as a player gets it: a placeholder that was never filled in must be refused, an optional value that is no value must be absent.</summary>
public class SettingsTests
{
    const string Good = """
        {"apiv2_host":"api.example.test","web_host":"www.example.test","client_exe":"game.exe","oauth_consumer_key":"public-key",
         "photo_host":"photo.example.test","avatar_host":"avatar.example.test","server_url":"https://community.example.test"}
        """;

    /// <summary>Good with one key set to value, or removed when value is null.</summary>
    static string With(string key, string? value)
    {
        var o = JsonNode.Parse(Good)!.AsObject();
        if (value == null) o.Remove(key); else o[key] = value;
        return o.ToJsonString();
    }

    [Fact]
    public void FilledInSettingsAreRead()
    {
        var s = Settings.Parse(Good);
        Assert.Equal(("api.example.test", "www.example.test", "game.exe", "public-key", "https://community.example.test", "avatar.example.test"),
            (s.Apiv2Host, s.WebHost, s.ClientExe, s.ConsumerKey, s.ServerUrl, s.AvatarHost));
        Assert.True(s.Hosts.SetEquals(["api.example.test", "www.example.test", "photo.example.test", "avatar.example.test"]), "every *_host value, and nothing else, may serve pictures");
    }

    [Theory]
    [InlineData("apiv2_host", "<apiv2 host>")]
    [InlineData("web_host", "")]
    [InlineData("client_exe", "<game exe>")]
    [InlineData("client_exe", null)]
    public void PlaceholderOrMissingRequiredValueIsRefused(string key, string? value) =>
        Assert.Contains(key, Assert.Throws<InvalidOperationException>(() => Settings.Parse(With(key, value))).Message);

    /// <summary>The template a player gets holds every key Parse reads: untouched it is refused for its first placeholder, filled in it parses.</summary>
    [Fact]
    public void TheShippedExampleHasEveryKeyParseReads()
    {
        var text = File.ReadAllText(Repo.Path("tools", "backup", "settings.example.json"));
        Assert.Contains("apiv2_host", Assert.Throws<InvalidOperationException>(() => Settings.Parse(text)).Message);
        var o = JsonNode.Parse(text)!.AsObject();
        foreach (var (k, _) in o.ToList()) o[k] = k == "server_url" ? "https://c.test" : k == "client_exe" ? "game.exe" : k.Replace('_', '-') + ".test";
        var s = Settings.Parse(o.ToJsonString());
        Assert.True(s.AvatarHost == "avatar-host.test" && s.ConsumerKey == "oauth-consumer-key.test" && s.ServerUrl == "https://c.test" && s.Hosts.Count == 5, "every key of the template is read");
    }

    [Fact]
    public void OptionalValuesThatAreNoValueAreAbsent()
    {
        var s = Settings.Parse(With("server_url", "not a url"));
        Assert.True(s.ServerUrl == null && s.ConsumerKey == "public-key", "a text that is no URL is unset");
        Assert.Null(Settings.Parse(With("server_url", "ftp://community.example.test")).ServerUrl);
        s = Settings.Parse(With("photo_host", "<photo host>"));
        Assert.True(!s.Hosts.Any(h => h.Contains('<')) && s.Hosts.Count == 3, "a placeholder host is never a host the tool would ask");
        Assert.Null(Settings.Parse(With("oauth_consumer_key", "<key>")).ConsumerKey);
        Assert.Null(Settings.Parse(With("avatar_host", "<avatar host>")).AvatarHost);
    }
}
