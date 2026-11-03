using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;

namespace Resurgence.Server.Tests;

public class RequestLogTests : IDisposable
{
    static readonly JsonObject Cfg = new(Replay.HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));
    readonly string tmp = Directory.CreateTempSubdirectory("reqlog").FullName;
    public void Dispose() => Fixtures.DeleteDir(tmp);

    static readonly (string, string) Form = ("Content-Type", "application/x-www-form-urlencoded");

    [Fact]
    public async Task TestAMissIsLoggedByKeysOnlyAndSwaggerOriginsAreNot()
    {
        await using var server = await Loopback.StartAsync(new Replay.Game(new Replay.HarTable(), Cfg, null, Path.Combine(tmp, "mirror")), tmp);
        await server.SendAsync("POST", "api.test", "/v1/Nope/nope?b=2&a=1", [Form], "z=secret&y=1"u8.ToArray());
        await server.SendAsync("POST", "api.test", "/v1/Nope/nope", [Form, ("Origin", "http://localhost")], "z=secret&y=1"u8.ToArray());
        var text = File.ReadAllText(Path.Combine(tmp, "misses.jsonl"));
        var line = JsonNode.Parse(Assert.Single(text.Split('\n', StringSplitOptions.RemoveEmptyEntries)))!;
        Fixtures.Eq("""["a", "b"]""", line["query_keys"]);
        Fixtures.Eq("""["y", "z"]""", line["body_keys"]);
        Fixtures.Eq("12", line["bytes"]);
        Assert.DoesNotContain("secret", text);
    }

    [Fact]
    public async Task TestTelemetryBodiesAreCutAt64Kilobytes()
    {
        await using var server = await Loopback.StartAsync(new Replay.Game(new Replay.HarTable(), Cfg, null, Path.Combine(tmp, "mirror")), tmp);
        await server.SendAsync("POST", "telemetry.test", "/collect", [], new byte[100 * 1024].Select(_ => (byte)'a').ToArray());
        var line = JsonNode.Parse(File.ReadAllLines(Path.Combine(tmp, "telemetry.jsonl")).Single())!;
        Assert.Equal(65536, ((string)line["body"]!).Length);
    }

    static string ConsoleOf(Reply reply, string path)
    {
        var (old, output) = (Console.Out, new StringWriter());
        Console.SetOut(output);
        try
        {
            new RequestLog(console: true, null).Write(new Req("GET", "signup.test", path, "", _ => null, [], false), reply);
            return output.ToString();
        }
        finally
        {
            Console.SetOut(old);
        }
    }

    static Reply R(int status, string body) => new(status, [], Encoding.ASCII.GetBytes(body), "state");

    [Fact]
    public void TestThePendingSignInPollIsNotPrintedButTheOthersAre()
    {
        Assert.Equal("", ConsoleOf(R(200, """{"status":"pending"}"""), "/auth/link/abc"));
        Assert.Single(ConsoleOf(R(200, """{"status":"ok"}"""), "/auth/link/abc").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Single(ConsoleOf(R(404, """{"status":"expired"}"""), "/auth/link/abc").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }
}
