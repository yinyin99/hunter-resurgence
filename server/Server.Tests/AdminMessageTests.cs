using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

public class AdminMessageTests : IAsyncLifetime
{
    static readonly JsonObject Cfg = new(Replay.HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));

    readonly string tmp = Directory.CreateTempSubdirectory("adminmsg").FullName;
    State state = null!;
    Loopback server = null!;

    public async ValueTask InitializeAsync()
    {
        state = new State(Path.Combine(tmp, "state.db"), Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES);
        state.CreateAccount(8, "second");
        state.CreateAccount(9, "third");
        server = await Loopback.StartAsync(new Replay.Game(new Replay.HarTable(), Cfg, state, tmp), tmp);
    }

    public async ValueTask DisposeAsync()
    {
        await server.DisposeAsync();
        DeleteDir(tmp);
    }

    Task<HttpResponseMessage> Post(string path, object body) => server.Http.PostAsJsonAsync($"http://localhost{path}", body);

    /// <summary>The texts of the notice thread of an account, newest first (reading it marks it read).</summary>
    string[] Inbox(long account) =>
        [.. JsonNode.Parse(state.ConversationReply(account, Encoding.UTF8.GetBytes($"user_id={State.SYSTEM_SENDER}")))!["messages"]!.AsArray().Select(m => (string)m!["content"]!)];

    [Fact]
    public async Task OneAccountGetsOneUnreadNoticeAndNobodyElseDoes()
    {
        var sent = await Post("/admin/accounts/second/message", new { text = "Please send your problem report.\nThanks <b>a lot</b>" });
        Assert.Equal("""{"sent":1}""", await sent.Content.ReadAsStringAsync());
        Assert.Equal((1L, 0L, 0L), (state.Unread(8), state.Unread(9), state.Unread(ME)));
        Assert.Equal(["Please send your problem report.\nThanks <b>a lot</b>"], Inbox(8));  // as written
        var log = AdminMessages.SentLog(state.Store, null).Single();
        Assert.Equal((8L, "second", 1L, "Please send your problem report.\nThanks <b>a lot</b>"), (log.Account, log.Handle, log.Recipients, log.Text));
        Assert.Equal(HttpStatusCode.OK, (await Post("/admin/accounts/8/message", new { text = "by id" })).StatusCode);
        Assert.Equal(["by id", "Please send your problem report.\nThanks <b>a lot</b>"], Inbox(8));
        Assert.Empty(AdminMessages.SentLog(state.Store, 9));  // nothing went to the third account
    }

    [Fact]
    public async Task AMessageToEveryoneReachesEveryAccountAndIsLoggedOnce()
    {
        var sent = await Post("/admin/message", new { text = "Maintenance tonight." });
        Assert.Equal("""{"sent":3}""", await sent.Content.ReadAsStringAsync());
        foreach (var account in new[] { ME, 8L, 9L })
            Assert.Equal(["Maintenance tonight."], Inbox(account));
        var log = AdminMessages.SentLog(state.Store, null).Single();
        Assert.Equal((null, 3L, "Maintenance tonight."), (log.Account, log.Recipients, log.Text));
        Assert.Single(AdminMessages.SentLog(state.Store, 9));  // an account's own log lists the messages to everyone too
    }

    [Fact]
    public async Task AnUnknownAccountAndABadTextAreRefusedAndNothingIsSent()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Post("/admin/accounts/nobody/message", new { text = "hello" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post("/admin/accounts/999/message", new { text = "hello" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("/admin/accounts/second/message", new { text = "  " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("/admin/message", new { text = new string('x', AdminMessages.MaxLength + 1) })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post("/admin/accounts/second/message", new { text = new string('x', AdminMessages.MaxLength) })).StatusCode);
        Assert.Equal((0L, 1L, 0L), (state.Unread(ME), state.Unread(8), state.Unread(9)));
        Assert.Single(AdminMessages.SentLog(state.Store, null));
    }

    [Fact]
    public async Task AReplyToTheNoticeSenderIsListedNewestFirstAndIsNotTheNoticeThread()
    {
        await Post("/admin/accounts/second/message", new { text = "Which profile is yours?" });
        void Reply(long account, string text, long ts)
        {
            using var con = state.Store.Open(account);
            con.Exec("INSERT INTO messages (account_id, sender, content, ts, read) VALUES ($a, ?, ?, ?, 1)", account, text, ts);
        }
        Reply(8, "It is the one with the red hat.", 1_000);
        Reply(9, "Hello admin", 2_000);
        var all = AdminMessages.Replies(state.Store, null);
        Assert.Equal(new[] { (2_000L, 9L, "third", "Hello admin"), (1_000L, 8L, "second", "It is the one with the red hat.") }, all.Select(r => (r.Ts, r.Account, r.Handle, r.Text)).ToArray());
        Assert.Equal(new[] { "It is the one with the red hat." }, AdminMessages.Replies(state.Store, 8).Select(r => r.Text).ToArray());  // the server's own notice is no reply
        Assert.Equal(["Which profile is yours?"], Inbox(8));
        Assert.Equal("Which profile is yours?", JsonNode.Parse(state.ConversationsReply(8))![0]!["preview"]!.ToString());
    }

    [Fact]
    public void TheCommandLineTakesAnAccountOrAllAndTheTextAndTheListTakesAccountAndSent()
    {
        Assert.Equal(("alice", false, false, "hi there"), AdminMessages.ParseSend(["--account", "alice", "hi there"]));
        Assert.Equal((null, true, true, "hi"), AdminMessages.ParseSend(["--all", "hi", "--yes"]));
        foreach (var bad in new[] { new string[] { "hi" }, ["--all", "--account", "1", "hi"], ["--all"], ["--account", "1"], ["--account", "", "hi"], ["--all", "a", "b"], ["--all", "--all", "hi"], ["--all", "--unknown"] })
            Assert.Null(AdminMessages.ParseSend(bad));
        Assert.Equal((null, false), AdminMessages.ParseList([]));
        Assert.Equal(("alice", true), AdminMessages.ParseList(["--sent", "--account", "alice"]));
        foreach (var bad in new[] { new string[] { "--account" }, ["--sent", "--sent"], ["alice"] })
            Assert.Null(AdminMessages.ParseList(bad));
    }
}
