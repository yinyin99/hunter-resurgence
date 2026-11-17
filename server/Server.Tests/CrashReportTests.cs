using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The game's crash and error reports (the crash host's envelopes): what is kept, what is dropped, the 30 days, and the telemetry file.</summary>
public class CrashReportTests : StateTestBase, IAsyncLifetime
{
    static readonly JsonObject Cfg = new(Replay.HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test")))) { ["upload_min_free_bytes"] = 0L };
    static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    Loopback server = null!;
    long player;

    public async ValueTask InitializeAsync()
    {
        state = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false);
        server = await Loopback.StartAsync(new Replay.Game(new Replay.HarTable(), Cfg, state, tmp), tmp);
        player = Account("player");
    }

    public async ValueTask DisposeAsync() => await server.DisposeAsync();

    long Account(string handle)
    {
        var id = state.Store.NextServerId();
        state.CreateAccount(id, handle, null);
        return id;
    }

    // image and instruction addresses as the report writes them: a prefix and hex digits
    static string Addr(ulong value) => string.Concat("0", "x", value.ToString("x"));

    const string Install = "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Game\\game.exe";

    /// <summary>An error report: two frames (the caller in other.dll, the crash in game.exe at offset 26) and everything a real one carries that must not be kept.</summary>
    static JsonObject Event(string id, string level, long? user, string value = "read at C:\\Users\\Bob\\Documents\\Game\\save.dat failed", long? ts = null)
    {
        var e = new JsonObject
        {
            ["event_id"] = id, ["level"] = level, ["timestamp"] = ts ?? Now, ["release"] = "build-7",
            ["server_name"] = "BOB-DESKTOP", ["contexts"] = new JsonObject { ["device"] = new JsonObject { ["id"] = "device-hash-123" } },
            ["exception"] = new JsonObject
            {
                ["values"] = new JsonArray(new JsonObject
                {
                    ["type"] = "ACCESS_VIOLATION", ["value"] = value,
                    ["stacktrace"] = new JsonObject
                    {
                        ["frames"] = new JsonArray(new JsonObject { ["instruction_addr"] = Addr(268435456 + 4096) }, new JsonObject { ["instruction_addr"] = Addr(4194304 + 26) }),
                    },
                }),
            },
            ["debug_meta"] = new JsonObject
            {
                ["images"] = new JsonArray(
                    new JsonObject { ["code_file"] = Install, ["image_addr"] = Addr(4194304), ["image_size"] = 8388608 },
                    new JsonObject { ["code_file"] = "C:\\Users\\Bob\\AppData\\Local\\Temp\\other.dll", ["image_addr"] = Addr(268435456), ["image_size"] = 65536 }),
            },
        };
        if (user is not null)
            e["user"] = new JsonObject { ["id"] = user.ToString(), ["username"] = "Bob", ["ip_address"] = "203.0.113.9" };
        return e;
    }

    static JsonObject Session(string sid, string status, long? did) =>
        new() { ["sid"] = sid, ["did"] = did?.ToString() ?? "device-hash-123", ["status"] = status, ["timestamp"] = Now, ["attrs"] = new JsonObject { ["release"] = "build-7" } };

    static byte[] Envelope(params (string Type, JsonObject Payload)[] items)
    {
        var bytes = new List<byte>();
        void Line(string s)
        {
            bytes.AddRange(Encoding.UTF8.GetBytes(s));
            bytes.Add((byte)'\n');
        }
        Line("""{"event_id":"aaaaaaaa-0000-0000-0000-000000000000","sent_at":"2026-11-17T10:00:00Z","dsn":"https://key@crash.test/34"}""");
        foreach (var (type, payload) in items)
        {
            var text = payload.ToJsonString();
            Line($$"""{"type":"{{type}}","length":{{Encoding.UTF8.GetByteCount(text)}}}""");
            Line(text);
        }
        return [.. bytes];
    }

    List<object[]> Rows(long account)
    {
        using var con = state.Store.Open();
        return con.Rows("SELECT event_id, kind, module, frames, message, release, ts FROM crash_reports WHERE account_id = ? ORDER BY n", account);
    }

    [Fact]
    public void TestOnlyFatalErrorAndAbnormalSessionsAreKeptOnePerEventIdForTheAccountInTheEvent()
    {
        var body = Envelope(("event", Event("e0000001", "fatal", player)), ("event", Event("e0000002", "error", player)), ("event", Event("e0000003", "warning", player)),
                            ("event", Event("e0000004", "info", player)), ("event", Event("e0000005", "error", null)), ("event", Event("e0000006", "error", player + 1000)),
                            ("session", Session("s0000001", "abnormal", player)), ("session", Session("s0000002", "exited", player)), ("session", Session("s0000003", "crashed", player)),
                            ("session", Session("s0000004", "abnormal", null)), ("attachment", new JsonObject { ["x"] = 1 }), ("transaction", Event("e0000007", "error", player)));
        Assert.Equal(3, CrashReports.Keep(state.Store, body, Now));
        Assert.Equal(["e0000001", "e0000002", "s0000001"], Rows(player).Select(r => (string)r[0]));
        Assert.Equal(["fatal", "error", "abnormal"], Rows(player).Select(r => (string)r[1]));
        Assert.Equal(0, CrashReports.Keep(state.Store, body, Now));  // the same event ids again: nothing new
        Assert.Equal(3, Rows(player).Count);
        using var con = state.Store.Open();
        Assert.Equal(3L, (long)con.Scalar("SELECT COUNT(*) FROM crash_reports")!);  // the report of an account that does not exist is dropped
    }

    [Fact]
    public void TestInstallPathsAndTheDeviceAreNotKept()
    {
        CrashReports.Keep(state.Store, Envelope(("event", Event("e0000001", "fatal", player))), Now);
        var row = Assert.Single(Rows(player));
        Assert.Equal("game.exe", row[2]);
        Eq("""["game.exe+1a", "other.dll+1000"]""", JsonNode.Parse((string)row[3]));  // the crashing frame first, module and offset in hex
        Assert.Equal("ACCESS_VIOLATION: read at save.dat failed", row[4]);
        Assert.Equal("build-7", row[5]);
        var everything = string.Join(" ", row.Select(c => c.ToString()));
        foreach (var leak in new[] { "Users", "Bob", "Steam", "Program Files", "device-hash", "BOB-DESKTOP", "203.0.113.9", "\\" })
            Assert.DoesNotContain(leak, everything);
    }

    [Theory]
    [InlineData("opened C:\\Users\\Bob\\x.dll: access denied", "opened x.dll: access denied")]
    [InlineData("C:/Games/My Game/data/core.pak is bad", "core.pak is bad")]
    [InlineData("share \\\\host\\share\\dir\\f.log and D:\\a\\b.txt", "share f.log and b.txt")]
    [InlineData("no path here: 12 of 30", "no path here: 12 of 30")]
    [InlineData("profile C:\\Users\\Bob Smith", "profile <path>")]
    [InlineData("in C:\\Games\\data, then x", "in <path>")]
    [InlineData("see C:\\Users\\john.doe", "see <path>")]
    [InlineData("see C:\\Users\\jdoe. Then it failed", "see <path>")]
    [InlineData("C:\\Users\\john.doe\\Saved Games\\a.sav", "a.sav")]
    [InlineData("admin share \\\\pc\\c$\\Users\\john.doe\\AppData\\x.dll failed", "admin share x.dll failed")]
    [InlineData("admin share \\\\pc\\c$\\Users\\john.doe", "admin share <path>")]
    [InlineData("\\\\pc\\c$\\Users\\Jane Doe.PC", "<path>")]
    [InlineData("home Z:\\home\\john.doe", "home <path>")]
    [InlineData("home Z:\\home\\john.doe\\Game\\save.dat ok", "home save.dat ok")]
    [InlineData("opened Z:/john.doe/v.2/Config.INI", "opened Config.INI")]
    public void TestAPathInATextIsReducedToItsFileName(string text, string expected) => Assert.Equal(expected, CrashReports.Clean(text));

    [Fact]
    public void TestACutOrGarbledEnvelopeKeepsWhatIsWholeAndNeverThrows()
    {
        var whole = Envelope(("event", Event("e0000001", "error", player)), ("event", Event("e0000002", "error", player)));
        Assert.Equal(1, CrashReports.Keep(state.Store, whole[..(whole.Length - 20)], Now));  // the second item is cut: only the first is kept
        Assert.Equal(0, CrashReports.Keep(state.Store, "not an envelope"u8.ToArray(), Now));
        Assert.Equal(0, CrashReports.Keep(state.Store, [], Now));
        Assert.Equal(0, CrashReports.Keep(state.Store, "{}\n{\"type\":\"event\",\"length\":999999}\n{}"u8.ToArray(), Now));
        Assert.Equal(0, CrashReports.Keep(state.Store, "{}\n{\"type\":\"event\"}\n{\"event_id\":\"e0000009\",\"level\":\"error\",\"level\":\"fatal\"}"u8.ToArray(), Now));  // a duplicate key
        Assert.Single(Rows(player));
    }

    [Fact]
    public void TestAReportOlderThan30DaysIsDroppedAndThePurgeDeletesWhatGrewOld()
    {
        var now = Now;
        Assert.Equal(0, CrashReports.Keep(state.Store, Envelope(("event", Event("e0000001", "error", player, ts: now - 31 * 86400L))), now));
        var then = now - 40 * 86400L;  // received 40 days ago, its own clock 1 day before that: in time then
        Assert.Equal(1, CrashReports.Keep(state.Store, Envelope(("event", Event("e0000002", "error", player, ts: then - 86400L))), then));
        Assert.Equal(1, CrashReports.Keep(state.Store, Envelope(("event", Event("e0000003", "error", player))), now));
        CrashReports.Purge(state.Store, now);
        Assert.Equal(["e0000003"], Rows(player).Select(r => (string)r[0]));
    }

    [Fact]
    public void TestAnAccountKeepsItsNewest200()
    {
        CrashReports.Keep(state.Store, Envelope(("event", Event("f0000001", "fatal", player))), Now);
        for (var n = 0; n < 205; n++)
            CrashReports.Keep(state.Store, Envelope(("event", Event($"e{n:D7}", "error", player))), Now);
        var rows = Rows(player);
        Assert.Equal(201, rows.Count);  // the cap is per kind: the flood of errors never pushes the fatal report out
        Assert.Equal("f0000001", (string)rows[0][0]);
        Assert.Equal(("e0000005", "e0000204"), ((string)rows[1][0], (string)rows[^1][0]));
    }

    [Fact]
    public async Task TestTheCrashHostKeepsReportsPerAccountAndNothingInTelemetryWhileTheTelemetryHostStaysThere()
    {
        var reply = await server.SendAsync("POST", "crash.test", "/api/34/envelope/", [], Envelope(("event", Event("e0000001", "fatal", player))));
        Assert.Equal((204, "sink"), (reply.Status, reply.Source));
        Assert.Single(Rows(player));
        await server.SendAsync("POST", "telemetry.test", "/api/x/v1/trackPlayerDeath", [], "death_type=25"u8.ToArray());
        var line = JsonNode.Parse(Assert.Single(File.ReadAllLines(Path.Combine(tmp, "telemetry.jsonl"))))!;
        Assert.Equal("telemetry.test", (string)line["host"]!);
        Assert.Equal(204, (await server.SendAsync("POST", "crash.test", "/api/34/envelope/", [], "garbage"u8.ToArray())).Status);  // the game gets its 204 whatever it sent
        Assert.Equal(204, (await server.SendAsync("POST", "crash.test", "/api/34/envelope/", [], Encoding.UTF8.GetBytes("{}\n{\"type\":\"event\"}\n" + new string('[', 5000)))).Status);
    }

    [Fact]
    public void TestAnOversizedTelemetryFileIsTrimmedOnceItsCrashLinesBecomeRowsAndTheNewestOtherLinesStay()
    {
        var file = Path.Combine(tmp, "old-telemetry.jsonl");
        string Line(string host, string body, string ts) => new JsonObject { ["ts"] = ts, ["host"] = host, ["method"] = "POST", ["path"] = "/x", ["body"] = body }.ToJsonString();
        var recent = DateTimeOffset.UtcNow.AddDays(-2).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'");
        var old = DateTimeOffset.UtcNow.AddDays(-40).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'");
        File.WriteAllLines(file,
        [
            Line("telemetry.test", "first", old),
            Line("crash.test", Encoding.UTF8.GetString(Envelope(("event", Event("e0000001", "fatal", player, ts: Now - 2 * 86400L)))), recent),
            Line("crash.test", Encoding.UTF8.GetString(Envelope(("event", Event("e0000002", "fatal", player, ts: Now - 40 * 86400L)))), old),
            "cut line {\"ts\"",
            Line("telemetry.test", "second", recent),
            Line("telemetry.test", "third", recent),
        ]);
        var size = new FileInfo(file).Length;
        string Bodies() => string.Join(",", File.ReadAllLines(file).Select(l => (string)JsonNode.Parse(l)!["body"]!));
        CrashReports.TrimTelemetry(file, "crash.test", state.Store, Now, cap: size * 10);  // under the cap, the crash lines and the cut line still go
        Assert.Equal("first,second,third", Bodies());
        Assert.Equal(["e0000001"], Rows(player).Select(r => (string)r[0]));  // the older crash line is past its 30 days
        var bytes = File.ReadAllBytes(file);
        CrashReports.TrimTelemetry(file, "crash.test", state.Store, Now, cap: size * 10);  // nothing left to drop: untouched
        Assert.Equal(bytes, File.ReadAllBytes(file));

        var cap = Line("telemetry.test", "second", recent).Length + Line("telemetry.test", "third", recent).Length + 2 * Environment.NewLine.Length;
        CrashReports.TrimTelemetry(file, "crash.test", state.Store, Now, cap);
        Assert.Equal("second,third", Bodies());
    }

    [Fact]
    public void TestDeletingTheAccountDeletesItsReports()
    {
        var other = Account("other");
        CrashReports.Keep(state.Store, Envelope(("event", Event("e0000001", "fatal", player)), ("event", Event("e0000002", "fatal", other))), Now);
        Assert.Single(Rows(player));
        new Uploads(state, tmp).Delete(player);
        Assert.Empty(Rows(player));
        Assert.Single(Rows(other));
    }

    [Fact]
    public void TestDiagnosticsListTheLast30DaysOfOneAccount()
    {
        var now = Now;
        long Start(int minutesAgo) => now - minutesAgo * 60L;
        using (var con = state.Store.Open(player))
        {
            void Hunt(long start, long? end, long? seen) => con.Exec("INSERT INTO expeditions (account_id, start_ts, end_ts, last_seen) VALUES ($a, ?, ?, ?)", start, end, seen);
            Hunt(Start(600), Start(600) + 300, Start(600) + 250);  // a disconnect: end after the last heartbeat
            Hunt(Start(500), Start(500) + 400, Start(500) + 400);  // silence: ended at its last heartbeat
            Hunt(Start(400), Start(400) + 30, null);  // 30 s, a disconnect
            Hunt(Start(300), Start(300) + 20, Start(300) + 20);  // both
            Hunt(now - 40 * 86400L, now - 40 * 86400L + 20, now - 40 * 86400L + 20);  // too old
            Hunt(Start(10), null, Start(10) + 5);  // still open
            foreach (var ts in new[] { now - 5, now - 60, now - 40 * 86400L })
                con.Exec("INSERT INTO events (account_id, event, body, ts) VALUES ($a, 30, '{}', ?)", ts);
            con.Exec("INSERT INTO events (account_id, event, body, ts) VALUES ($a, 29, '{}', ?)", now - 5);
        }
        CrashReports.Keep(state.Store, Envelope(("event", Event("e0000001", "fatal", player))), now);
        var d = Diagnostics.Of(state.Store, player, now);
        Assert.Equal([Start(300), Start(500)], d.NoDisconnect.Select(h => h.Start));  // newest first
        Assert.Equal([Start(300), Start(400)], d.Short.Select(h => h.Start));
        Assert.Equal(2L, d.Event30);
        var crash = Assert.Single(d.Crashes);
        Assert.Equal(("fatal", "game.exe"), (crash.Kind, crash.Module));
        Assert.Contains("hunts under 60 s: 2", d.Lines());
        Assert.Empty(Diagnostics.Of(state.Store, Account("quiet"), now).Short);
    }
}
