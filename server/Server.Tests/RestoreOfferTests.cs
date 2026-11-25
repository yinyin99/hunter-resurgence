using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The one-time restore offer: `POST /account/restore-offer` is true the first time for an account without a zip restore or an upload (schema 23).</summary>
public class RestoreOfferTests : StateTestBase
{
    static readonly JsonObject WebCfg = new(Replay.HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));

    [Fact]
    public async Task TestTheFirstAskIsTheFirstTimeAndNoOtherIs()
    {
        var s = new State(Path.Combine(tmp, "offer.db"), Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false);
        await using var server = await Loopback.StartAsync(new Replay.Game(new Replay.HarTable(), WebCfg, s, tmp), tmp);
        var cookie = ("Cookie", $"{SignIn.SessionCookie}={s.Store.CreateWebSession(ME, TimeSpan.FromHours(1))}");
        var own = ("X-Requested-With", "XMLHttpRequest");
        Task<Reply> Post(params (string, string)[] headers) => server.SendAsync("POST", "web.test", "/account/restore-offer", headers, Encoding.ASCII.GetBytes(""));

        Assert.Equal(401, (await Post(own)).Status);
        Assert.Equal(400, (await Post(cookie)).Status);  // not from our page
        Eq("""{"first": true}""", JsonNode.Parse((await Post(cookie, own)).Body));
        Eq("""{"first": false}""", JsonNode.Parse((await Post(cookie, own)).Body));
    }

    [Theory]
    [InlineData("INSERT INTO restore_audit (account_id, ts, zip_sha256, mode, report) VALUES ($a, 1, 'x', 'm', 'r')")]
    [InlineData("INSERT INTO uploads (account_id, ts, zip_sha256, user_id, note) VALUES ($a, 1, 'x', 1, 'n')")]
    public void TestAnAccountWithAZipRestoreOrAnUploadIsNeverOffered(string insert)
    {
        var store = new Store(Path.Combine(tmp, "has.db"));
        store.CreateAccount(5, "old", new JsonObject());
        store.CreateAccount(6, "new", new JsonObject());
        using (var con = store.Open(5)) con.Exec(insert);
        Assert.False(store.NoteRestoreOffer(5));
        Assert.True(store.NoteRestoreOffer(6));
        Assert.False(store.NoteRestoreOffer(6));
    }

    [Theory]
    [InlineData("INSERT INTO claims (account_id, archived_uid, handle, hunterscore, state, ts, note, keep_handle) VALUES (5, 1, 'h', 1, '{0}', 1, 'n', 0)", "waiting")]
    [InlineData("INSERT INTO claims (account_id, archived_uid, handle, hunterscore, state, ts, note, keep_handle) VALUES (5, 1, 'h', 1, '{0}', 1, 'n', 0)", "rejected")]
    [InlineData("INSERT INTO restore_audit (account_id, ts, zip_sha256, mode, report) VALUES ($a, 1, 'archive:7', 'm', 'r')", "")]
    [InlineData("INSERT INTO restore_audit (account_id, ts, zip_sha256, mode, report) VALUES ($a, 1, 'archive:7', 'm', 'r'), ($a, 2, 'abc', 'merge', 'r')", "")]
    public void TestAnArchiveClaimNeverBlocksTheOffer(string insert, string state)
    {
        var store = new Store(Path.Combine(tmp, "claim.db"));
        store.CreateAccount(5, "old", new JsonObject());
        using (var con = store.Open(5)) con.Exec(insert.Replace("{0}", state));
        Assert.True(store.NoteRestoreOffer(5));
    }

    [Fact]
    public void TestTheSchemaOfVersion22OffersEveryAccountOnce()
    {
        var file = Path.Combine(tmp, "v22.db");
        var store = new Store(file);
        store.CreateAccount(5, "old", new JsonObject());
        using (var con = store.Open())
        {
            con.Exec("ALTER TABLE accounts DROP COLUMN restore_offered");
            con.Exec("PRAGMA user_version = 22");
        }
        var migrated = new Store(file);
        Assert.True(migrated.NoteRestoreOffer(5));
        Assert.False(migrated.NoteRestoreOffer(5));
        using var check = migrated.Open();
        Assert.Equal((long)Store.Version, check.Scalar("PRAGMA user_version"));
    }
}
