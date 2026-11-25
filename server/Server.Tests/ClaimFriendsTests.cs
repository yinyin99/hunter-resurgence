using System.Text;
using Microsoft.Data.Sqlite;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The friends of archive claims: an archived friend list names old user ids, and a claimed account keeps its own, so an old id is resolved to the account that holds that profile
/// when a claim writes its edges and when the other player's claim arrives later. Three made-up players: Xena (friend Yara, request from Zed), Yara (friend Xena), Zed (asked Xena).</summary>
public class ClaimFriendsTests : StateTestBase
{
    const long Xena = 3001, Yara = 3002, Zed = 3003;
    int accounts;

    public ClaimFriendsTests()
    {
        state = new State(Db, Py.DumpBytes(Fixtures.CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false);
        MakeArchive();
    }

    string ArchivePath => Path.Combine(tmp, "archive.db");

    void MakeArchive()
    {
        using var con = new SqliteConnection($"Data Source={ArchivePath};Pooling=False");
        con.Open();
        void Run(string sql, params (string, object)[] ps)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v);
            cmd.ExecuteNonQuery();
        }
        Run("CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT); INSERT INTO meta VALUES ('format', '1');"
            + "CREATE TABLE player(uid INTEGER PRIMARY KEY, handle TEXT NOT NULL, hostname TEXT NOT NULL, hunterscore INTEGER NOT NULL);"
            + "CREATE UNIQUE INDEX player_hostname ON player(hostname);"
            + "CREATE TABLE reply(uid INTEGER NOT NULL, path TEXT NOT NULL, endpoint TEXT NOT NULL, params TEXT NOT NULL, status INTEGER NOT NULL, body BLOB NOT NULL, PRIMARY KEY(uid, path));");
        void Player(long uid, string handle, string friends)
        {
            Run("INSERT INTO player VALUES ($u, $h, $k, 100)", ("$u", uid), ("$h", handle), ("$k", handle.ToLowerInvariant()));
            Run("INSERT INTO reply VALUES ($u, $p, '/v1/Inventory/dict', $q, 200, $b)", ("$u", uid), ("$p", $"Inventory_dict__user_id={uid}.json"), ("$q", $$"""{"user_id":"{{uid}}"}"""), ("$b", "{}"u8.ToArray()));
            Run("INSERT INTO reply VALUES ($u, $p, '/v1/User/friendslist', $q, 200, $b)", ("$u", uid), ("$p", $"User_friendslist__user_id={uid}__offset=0__limit=0.json"),
                ("$q", $$"""{"user_id":"{{uid}}","offset":"0","limit":"0"}"""), ("$b", Encoding.UTF8.GetBytes(friends)));
        }
        Player(Xena, "Xena", $$"""{"total":1,"friends":[{"id":{{Yara}}}],"requests":[{"id":{{Zed}}}]}""");
        Player(Yara, "Yara", $$"""{"total":1,"friends":[{"id":{{Xena}}}]}""");
        Player(Zed, "Zed", $$"""{"total":0,"friends":[],"pending":[{"id":{{Xena}}}]}""");
    }

    /// <summary>An account the way the first sign-in makes it: it has its own user id.</summary>
    long SignedIn(string handle)
    {
        var id = state.Store.NextServerId();
        state.CreateAccount(id, handle, "7656119000000100" + accounts++);
        return id;
    }

    void Claim(long account, string name) =>
        Assert.Equal("restored", new Claims(state, new Archive(ArchivePath), tmp).Claim(account, name, useOldName: false, confirmOverwrite: false).Kind);

    (List<long> Friends, List<long> Requests, List<long> Pending) Friends(long account, Store? store = null) => (store ?? state.Store).FriendsOf(account, null);

    static void Is(List<long> actual, params long[] expected) => Assert.Equal(expected, actual.ToArray());

    long Edges(string where)
    {
        using var con = state.Store.Open();
        return (long)con.Scalar($"SELECT COUNT(*) FROM friends WHERE {where}")!;
    }

    [Theory]
    [InlineData("xyz")]
    [InlineData("zyx")]
    [InlineData("yxz")]
    [InlineData("xzy")]
    public void TestPlayersWhoClaimFriendsAreFriendsWhicheverClaimedFirst(string order)
    {
        var who = new Dictionary<char, long> { ['x'] = SignedIn("alice"), ['y'] = SignedIn("bob"), ['z'] = SignedIn("carol") };
        var names = new Dictionary<char, string> { ['x'] = "xena", ['y'] = "yara", ['z'] = "zed" };
        foreach (var c in order) Claim(who[c], names[c]);

        Is(Friends(who['x']).Friends, who['y']);  // the friends of the old profiles, as the accounts that claimed them
        Is(Friends(who['y']).Friends, who['x']);
        Is(Friends(who['x']).Requests, who['z']);  // Zed asked Xena
        Is(Friends(who['y']).Requests);
        Is(Friends(who['z']).Pending, who['x']);
        Is(Friends(who['z']).Friends);
        Assert.Equal(0L, Edges($"account_id < {Store.FirstServerId} OR other_id < {Store.FirstServerId}"));  // no edge names an old id any more
    }

    [Fact]
    public void TestAnEdgeToAnUnclaimedProfileWaitsForItsClaim()
    {
        var a = SignedIn("alice");
        Claim(a, "xena");
        Is(Friends(a).Friends);  // Yara's profile has no account here yet
        Assert.Equal(1L, Edges($"account_id = {a} AND other_id = {Yara} AND state = 'friend'"));
        var b = SignedIn("bob");
        Claim(b, "yara");
        Is(Friends(a).Friends, b);
    }

    [Fact]
    public void TestStartupConnectsTheEdgesOfClaimsMadeBefore()
    {
        var a = SignedIn("alice");
        var b = SignedIn("bob");
        Claim(a, "xena");  // the edge to Yara's old id is stored as it is
        using (var con = state.Store.Open())  // a claim of Yara's profile made by a server that did not move the edges
            con.Exec("INSERT INTO claims (account_id, archived_uid, handle, hunterscore, state, ts, decided_ts, note, keep_handle) VALUES (?, ?, 'Yara', 0, 'restored', 0, 0, '', 1)", b, Yara);
        Is(Friends(a).Friends);

        var restarted = new Store(Db);
        Is(Friends(a, restarted).Friends, b);
        Is(Friends(b, restarted).Friends, a);
        Assert.Equal(1L, Edges($"(account_id = {a} AND other_id = {b}) OR (account_id = {b} AND other_id = {a})"));
        Assert.Equal(0L, Edges($"account_id = {Yara} OR other_id = {Yara}"));

        _ = new Store(Db);  // and again: nothing changes
        Is(Friends(a, restarted).Friends, b);
        Assert.Equal(1L, Edges($"(account_id = {a} AND other_id = {b}) OR (account_id = {b} AND other_id = {a})"));
    }

    [Fact]
    public void TestAFriendshipRemovedHereIsNotMadeAgainByTheNextStart()
    {
        var a = SignedIn("alice");
        var b = SignedIn("bob");
        Claim(a, "xena");
        Claim(b, "yara");
        state.Store.RemoveFriend(a, b);
        var restarted = new Store(Db);
        Is(Friends(a, restarted).Friends);
        Is(Friends(b, restarted).Friends);
    }

    [Fact]
    public void TestAZipRestoredOntoAnAccountThatPlayedHoldsItsUserId()
    {
        var a = SignedIn("alice");
        var b = SignedIn("bob");
        using (var con = state.Store.Open())
            con.Exec("INSERT INTO restored_ids (user_id, account_id, ts) VALUES (?, ?, 0)", Xena, a);  // Alice's own zip of Xena restored onto her account, which kept its id
        Claim(b, "yara");
        Is(Friends(b).Friends, a);
        Is(Friends(a).Friends, b);
    }

    [Fact]
    public void TestTheHolderIsTheAccountWithTheIdThenTheZipThenTheClaim()
    {
        var a = SignedIn("alice");
        var b = SignedIn("bob");
        using var con = state.Store.Open();
        long? Holder() => Store.Holder(con, Xena);
        Assert.Null(Holder());
        con.Exec("INSERT INTO claims (account_id, archived_uid, handle, hunterscore, state, ts, note, keep_handle) VALUES (?, ?, 'Xena', 0, 'waiting', 0, '', 0)", a, Xena);
        Assert.Null(Holder());  // a claim that waits holds nothing
        con.Exec("UPDATE claims SET state = 'restored' WHERE account_id = ?", a);
        Assert.Equal((long?)a, Holder());
        con.Exec("INSERT INTO restored_ids (user_id, account_id, ts) VALUES (?, ?, 0)", Xena, b);
        Assert.Equal((long?)b, Holder());  // a zip proves more than the archive
        state.CreateAccount(Xena, "own", "76561190000009991");
        Assert.Equal((long?)Xena, Holder());  // the account that has the id is the profile's
    }
}
