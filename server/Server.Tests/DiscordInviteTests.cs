using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The one-time Discord invitation: `POST /account/discord` is true the first time for an account (schema 20).</summary>
public class DiscordInviteTests : StateTestBase
{
    static readonly JsonObject WebCfg = new(Replay.HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));

    [Fact]
    public async Task TestTheFirstAskIsTheFirstTimeAndNoOtherIs()
    {
        var s = new State(Path.Combine(tmp, "discord.db"), Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false);
        await using var server = await Loopback.StartAsync(new Replay.Game(new Replay.HarTable(), WebCfg, s, tmp), tmp);
        var cookie = ("Cookie", $"{SignIn.SessionCookie}={s.Store.CreateWebSession(ME, TimeSpan.FromHours(1))}");
        var own = ("X-Requested-With", "XMLHttpRequest");
        Task<Reply> Post(params (string, string)[] headers) => server.SendAsync("POST", "web.test", "/account/discord", headers, Encoding.ASCII.GetBytes(""));

        Assert.Equal(401, (await Post(own)).Status);
        Assert.Equal(400, (await Post(cookie)).Status);  // not from our page
        Eq("""{"first": true}""", JsonNode.Parse((await Post(cookie, own)).Body));
        Eq("""{"first": false}""", JsonNode.Parse((await Post(cookie, own)).Body));
    }

    [Fact]
    public void TestTheSchemaOfVersion19InvitesEveryAccountOnce()
    {
        var file = Path.Combine(tmp, "v19.db");
        var store = new Store(file);
        store.CreateAccount(5, "old", new JsonObject());
        using (var con = store.Open())
        {
            con.Exec("ALTER TABLE accounts DROP COLUMN discord_invited");
            con.Exec("PRAGMA user_version = 19");
        }
        var migrated = new Store(file);
        Assert.True(migrated.NoteDiscordInvite(5));
        Assert.False(migrated.NoteDiscordInvite(5));
        using var check = migrated.Open();
        Assert.Equal((long)Store.Version, check.Scalar("PRAGMA user_version"));
    }
}
