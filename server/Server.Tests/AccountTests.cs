using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The schema, accounts, tokens, the account guard and the restore rows, on a Store alone.</summary>
public class StoreTests : IDisposable
{
    readonly string tmp = Directory.CreateTempSubdirectory("store").FullName;
    readonly string file;
    readonly Store store;

    public StoreTests()
    {
        file = Path.Combine(tmp, "state.db");
        store = new Store(file);
    }

    public void Dispose() => DeleteDir(tmp);

    static JsonObject Account(long id, string handle) => O($$"""{"user_id": {{id}}, "handle": "{{handle}}"}""");

    [Fact]
    public void TestSchemaIsMigratedOnceAndTheConnectionIsConfigured()
    {
        store.CreateAccount(1, "one", Account(1, "one"));
        var again = new Store(file);  // a second start runs no DDL: the account survives and nothing throws
        Assert.True(again.AccountExists(1));
        using var con = again.Open();
        Assert.Equal((long)Store.Version, (long)con.Scalar("PRAGMA user_version")!);
        Assert.Equal("wal", (string)con.Scalar("PRAGMA journal_mode")!);
        Assert.Equal(5000L, (long)con.Scalar("PRAGMA busy_timeout")!);
        Assert.Equal(1L, (long)con.Scalar("PRAGMA synchronous")!);  // NORMAL
        Assert.Equal(1L, (long)con.Scalar("PRAGMA foreign_keys")!);
    }

    [Fact]
    public void TestADatabaseFromBeforeAccountsIsRefused()
    {
        var old = Path.Combine(tmp, "old.db");
        using (var raw = new SqliteConnection($"Data Source={old};Pooling=False"))
        {
            raw.Open();
            using var c = raw.CreateCommand();
            c.CommandText = "CREATE TABLE items (guid INTEGER PRIMARY KEY)";
            c.ExecuteNonQuery();
        }
        Assert.Contains("predates accounts", Assert.Throws<InvalidOperationException>(() => new Store(old)).Message);
    }

    [Fact]
    public void TestIdHandleAndSteamIdAreUnique()
    {
        store.CreateAccount(1, "Hunter", Account(1, "Hunter"), "steam-1");
        Assert.Equal(1L, store.AccountBySteam("steam-1"));
        Assert.Equal(1L, store.AccountByHandle("hunter"));  // handles compare without case
        Assert.Null(store.AccountBySteam("steam-2"));
        Eq("""{"user_id": 1, "handle": "Hunter"}""", store.AccountJson(1));
        Assert.Throws<SqliteException>(() => store.CreateAccount(1, "other", Account(1, "other")));
        Assert.Throws<SqliteException>(() => store.CreateAccount(2, "HUNTER", Account(2, "HUNTER")));
        Assert.Throws<SqliteException>(() => store.CreateAccount(3, "third", Account(3, "third"), "steam-1"));
        Assert.False(store.AccountExists(2));  // a refused account leaves nothing behind
        Assert.False(store.AccountExists(3));
        store.CreateAccount(4, "fourth", Account(4, "fourth"));  // the Steam id is optional, and several accounts may have none
        store.CreateAccount(5, "fifth", Account(5, "fifth"));
    }

    [Fact]
    public void TestASeedFailureRollsTheAccountBack()
    {
        Assert.Throws<InvalidOperationException>(() => store.CreateAccount(1, "one", Account(1, "one"), seed: _ => throw new InvalidOperationException("seed")));
        Assert.False(store.AccountExists(1));
    }

    [Fact]
    public void TestTokensResolveToTheirAccountUntilTheyExpireOrTheAccountIsBanned()
    {
        store.CreateAccount(1, "one", Account(1, "one"));
        store.CreateAccount(2, "two", Account(2, "two"));
        var (t1, t2) = (store.CreateToken(1), store.CreateToken(2, TimeSpan.FromHours(1)));
        Assert.Equal(40, t1.Length);
        Assert.NotEqual(t1, store.CreateToken(1));
        Assert.Equal(1L, store.ResolveToken(t1));
        Assert.Equal(2L, store.ResolveToken(t2));
        Assert.Null(store.ResolveToken("0000"));
        Assert.Null(store.ResolveToken(store.CreateToken(1, TimeSpan.FromSeconds(-1))));  // already expired
        using (var con = store.Open())
        {
            Assert.Equal(0L, (long)con.Scalar("SELECT COUNT(*) FROM tokens WHERE hash = ?", t1)!);  // only the hash is kept
            con.Exec("UPDATE accounts SET banned = 1 WHERE id = ?", 1L);
        }
        Assert.Null(store.ResolveToken(t1));
        Assert.Equal(2L, store.ResolveToken(t2));
    }

    [Fact]
    public void TestTheJwtKeyIsKeptInAFileAndSurvivesARestart()
    {
        Assert.Equal(32, store.JwtKey.Length);
        Assert.Equal(store.JwtKey, new Store(file).JwtKey);
        Assert.True(File.Exists(file + ".jwtkey"));
    }

    [Fact]
    public void TestAWebSessionStopsWorkingWhenBannedOrDeleted()
    {
        store.CreateAccount(1, "one", Account(1, "one"));
        var cookie = store.CreateWebSession(1, TimeSpan.FromHours(1));
        Assert.Equal(1L, store.ResolveWebSession(cookie));
        using (var con = store.Open()) con.Exec("UPDATE accounts SET banned = 1 WHERE id = ?", 1L);
        Assert.Null(store.ResolveWebSession(cookie));
        using (var con = store.Open()) con.Exec("UPDATE accounts SET banned = 0 WHERE id = ?", 1L);
        Assert.Equal(1L, store.ResolveWebSession(cookie));
        store.DeleteWebSession(cookie);
        Assert.Null(store.ResolveWebSession(cookie));
    }

    [Fact]
    public void TestBackupCopiesTheDatabaseEvenInAFolderWithAQuote()
    {
        var dir = Path.Combine(tmp, "O'Brien");
        Directory.CreateDirectory(dir);
        var s = new Store(Path.Combine(dir, "state.db"));
        s.CreateAccount(1, "one", Account(1, "one"));
        var (a, b) = (s.Backup(), s.Backup());
        Assert.NotEqual(a, b);
        Assert.True(File.Exists(a) && File.Exists(b));
        Assert.True(new Store(a).AccountExists(1));
    }

    [Fact]
    public void TestRankIsThePlaceOnTheBoardAmongAccountsNotBanned()
    {
        foreach (var (id, score) in new (long, long?)[] { (1, 900), (2, 500), (3, 500), (4, 100), (5, null), (6, 1000), (7, 0) })
        {
            var account = Account(id, $"h{id}");
            if (score is { } s)
                account["hunterscore"] = s;
            store.CreateAccount(id, $"h{id}", account);
        }
        using (var con = store.Open())
            con.Exec("UPDATE accounts SET banned = 1 WHERE id = ?", 6L);
        // equal scores are numbered by account id, no hunterscore is 0 (after the 0 of the lower id), and the banned 1000 is not counted
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6 }, new long[] { 1, 2, 3, 4, 5, 7 }.Select(id => store.Rank(id)));
    }

    [Fact]
    public void TestSqlThatMissesTheAccountFilterThrows()
    {
        store.CreateAccount(1, "one", Account(1, "one"));
        using var con = store.Open(1);
        Assert.Empty(con.Rows("SELECT * FROM items WHERE account_id = $a"));
        foreach (var sql in new[]
                 {
                     "SELECT * FROM items",
                     "SELECT * FROM items WHERE account_id = 1",  // the id must be the bound one
                     "UPDATE wallet SET amount = 0 WHERE currency = 'em'",
                     "DELETE FROM pending",
                     "INSERT INTO messages (account_id, sender, content, ts) VALUES (1, 2, 'x', 3)",
                     // each table of a join or a subquery needs its filter: here one is missing
                     "SELECT k.kill_id FROM kills k JOIN harvests h ON h.kill_id = k.kill_id WHERE k.account_id = $a",
                     "SELECT 1 FROM events, items WHERE events.account_id = $a",
                     "WITH k AS (SELECT MAX(id) AS id FROM events WHERE account_id = $a) SELECT body FROM events, k",
                 })
            Assert.Contains("per-player table", Assert.Throws<InvalidOperationException>(() => con.Rows(sql)).Message);
        using var shared = store.Open();  // no account: no per-player table at all, and no $a
        Assert.Throws<InvalidOperationException>(() => shared.Rows("SELECT * FROM items WHERE account_id = $a"));
        Assert.Throws<InvalidOperationException>(() => shared.Rows("SELECT * FROM wallet"));
        Assert.Empty(shared.Rows("SELECT * FROM accounts WHERE id = 99"));
    }

    [Fact]
    public void TestRestoreRowsAreWrittenPerAccountAndCleared()
    {
        store.CreateAccount(1, "one", Account(1, "one"));
        store.CreateAccount(2, "two", Account(2, "two"));
        void Write(long account, long guid)
        {
            using var con = store.Open(account);
            con.Begin();
            PlayerRows.ClearModel(con);
            PlayerRows.AddItem(con, guid, 512, 3, null, new JsonObject { ["dog"] = "Rex" });
            PlayerRows.SetWallet(con, "em", 12.5);
            PlayerRows.SetSkill(con, "weapon", 5, 3.25);
            PlayerRows.SetProfile(con, O("""{"Id": 1, "Handle": "one"}"""));
            PlayerRows.SetStatistics(con, O("""{"animal": {}}"""), 10.5, 20, 1, null);
            PlayerRows.AddAchievement(con, 4, 1, 2, JsonValue.Create(7), O("""{"3": 1700000000}"""));
            PlayerRows.AddTrophy(con, "harvest", "9", O("""{"id": 9}"""));
            PlayerRows.AddLodge(con, "lodge", "100", O("""{"id": 100}"""));
            PlayerRows.SetExtra(con, "Me/me.json", O("""{"x": 1}"""));
            PlayerRows.AddAudit(con, new string('a', 64), "my-account", "restored: 1");
            con.Commit();
        }
        long Count(long account, string table)
        {
            using var con = store.Open(account);
            return (long)con.Scalar($"SELECT COUNT(*) FROM {table} WHERE account_id = $a")!;
        }
        Write(1, 5);
        Write(2, 5);  // the zip's guid is only unique within its account
        Write(2, 5);  // the same zip twice: the model is replaced, the audit grows
        foreach (var table in new[] { "items", "wallet", "skills", "profile", "statistics", "achievements", "trophies", "lodges", "extras" })
            Assert.Equal((1L, 1L), (Count(1, table), Count(2, table)));
        Assert.Equal((1L, 2L), (Count(1, "restore_audit"), Count(2, "restore_audit")));
        using var con1 = store.Open(1);
        Assert.Equal(512L, (long)con1.Scalar("SELECT item_id FROM items WHERE account_id = $a AND guid = 5")!);
        con1.Begin();
        PlayerRows.ClearModel(con1);
        con1.Commit();
        Assert.Equal(0L, Count(1, "items"));
        Assert.Equal(1L, Count(1, "restore_audit"));  // the audit stays
        Assert.Equal(1L, Count(2, "items"));  // another account is not touched
        PlayerRows.AddItem(con1, 5, 1, 1, 1, new JsonObject());
        Assert.Throws<SqliteException>(() => PlayerRows.AddItem(con1, 5, 1, 1, 1, new JsonObject()));  // (account, guid) is the key
    }

    [Fact]
    public void TestDeletingAnAccountDeletesItsRows()
    {
        store.CreateAccount(1, "one", Account(1, "one"));
        var token = store.CreateToken(1);
        using (var con = store.Open(1))
            PlayerRows.SetWallet(con, "em", 1);
        using (var con = store.Open())
            con.Exec("DELETE FROM accounts WHERE id = ?", 1L);
        Assert.Null(store.ResolveToken(token));
        using var shared = store.Open();
        Assert.Equal(0L, (long)shared.Scalar("SELECT COUNT(*) FROM tokens")!);
    }
}

/// <summary>Two accounts on one database: what one does is never read or written through the other.</summary>
public class AccountIsolationTests : StateTestBase
{
    const long A = ME, B = 8;

    public AccountIsolationTests()
    {
        var connect = CONNECT;
        connect["skills"] = LEVELS;
        state = new State(Db, Py.DumpBytes(connect), BASE, ICON, APP, LOCALE, TEXTURES, discounts: DISCOUNTS, skillList: NUMBERS, itemDefs: ITEM_DEFS);
        state.CreateAccount(B, "second");
        Scoring.StaticHost = "static.test";
        Scoring.OwnLogo = "testlogo";
    }

    JsonArray Hb(long account, params JsonNode[] events) => Reply(state.HeartbeatReply(account, Events(events)))["updates"]!.AsArray();

    JsonObject Inventory(long account)
    {
        var items = new JsonObject();
        foreach (var i in state.Inventory(account))
            items[(string)i!["guid"]!] = i.DeepClone();
        return items;
    }

    JsonNode Numbers(long account) => Reply(state.SkillListReply(account, Encoding.ASCII.GetBytes($"user_id={account}"))!);

    JsonNode ExpeditionOf(long account) => Reply(state.ExpeditionReply(account, "{}"u8.ToArray()));

    string Clone(long account, int slot) => Py.Str(Reply(state.MoveMultiple2Reply(account, Form(
        JsonNode.Parse($$"""[{"item_guid": "new_item_1", "slot_id": {{slot}}}]""")!,
        JsonNode.Parse("""[{"item_guid": "new_item_1", "item_id": 10}]"""))))["new_item_1"]);

    internal static JsonNode[] Harvest(int individual, int grams, bool hit = true)
    {
        var events = new List<JsonNode>
        {
            O($$"""{"event": 7, "individual": {{individual}}, "species": 1, "gender": 0, "weight_grams": {{grams}}, "texture": 0}"""),
        };
        if (hit)
            events.Add(O($$"""
                {"event": 1, "individual": {{individual}}, "distance_cms": 9614, "organ1": 17, "organ2": 8388610, "weapon_id": 5,
                 "ammunition_id": 10, "scope_id": 2, "pose": 2, "animal_state": 3, "player_x": -9593, "player_z": 8198}
                """));
        events.Add(O($$"""
            {"event": 3, "individual": {{individual}}, "extended_species": 1, "antlers_seed": 46658, "base_texture": "b.dds", "fur_texture": "",
             "scale": 0.9, "antlers_score_percentage": 0.25, "trophy_integrity": 96731, "harvest_value": 89795, "wound_time": 6123}
            """));
        return [.. events];
    }

    [Fact]
    public void TestEachAccountIsSeededAndHasItsOwnAccountObject()
    {
        Assert.Equal(["100", "200"], Inventory(A).Select(kv => kv.Key));
        Assert.Equal(["100", "200"], Inventory(B).Select(kv => kv.Key));  // the same guids, another account
        var connectA = Reply(state.ConnectReply(A));
        Eq("""[7, "hunter"]""", T(connectA["account"]!["user_id"], connectA["account"]!["handle"]));
        var connectB = Reply(state.ConnectReply(B));
        Eq("""[8, "second"]""", T(connectB["account"]!["user_id"], connectB["account"]!["handle"]));
        Eq("""{"em": 182.0, "gm": 33.0}""", connectB["wallet"]);
        Assert.Equal(B, state.Store.AccountByHandle("second"));
        Assert.Throws<SqliteException>(() => state.CreateAccount(B, "again"));
    }

    [Fact]
    public void TestInventoryMovesClonesAndGuidsStayInTheirAccount()
    {
        state.MoveMultiple2Reply(B, Form(JsonNode.Parse("""[{"item_guid": 100, "slot_id": 12}]""")!));
        Eq("0", Inventory(A)["100"]!["slot"]);
        Eq("12", Inventory(B)["100"]!["slot"]);
        Assert.Equal("201", Clone(B, 13));
        // a guid that only the other account has is unknown here
        Assert.Equal("null"u8.ToArray(), state.MoveMultiple2Reply(A, Form(JsonNode.Parse("""[{"item_guid": 201, "slot_id": 5}]""")!)));
        Assert.DoesNotContain("201", Inventory(A).Select(kv => kv.Key));
        Assert.Equal("201", Clone(A, 14));  // each account allocates its own guids
        Eq("[13, 14]", T(Inventory(B)["201"]!["slot"], Inventory(A)["201"]!["slot"]));
        // a consumed item and an item metadata event change one account's row only
        Hb(B, O("""{"event": 6, "item": "200", "parent_item": "300", "quantity": 4}"""),
           O("""{"event": 23, "item": "100", "metadata": {"dog_name": "Rex"}}"""));
        Eq("[6, {}]", T(Inventory(B)["200"]!["units"], Inventory(A)["100"]!["metadata"]));
        Eq("""[10, {"dog_name": "Rex"}]""", T(Inventory(A)["200"]!["units"], Inventory(B)["100"]!["metadata"]));
    }

    [Fact]
    public void TestWalletBuyGrantAndPendingUpdatesStayInTheirAccount()
    {
        Eq("""{"price": 9.0, "success": true}""", Reply(state.BuyReply(B, "item_id=19&amount=1&wallet=GOLD"u8.ToArray())));
        Eq("[182.0, 173.0]", T(state.Wallet(A)["em"], state.Wallet(B)["em"]));
        Assert.Equal((2, 3), (state.Inventory(A).Count, state.Inventory(B).Count));
        Assert.Empty(Hb(A));  // the item and the balance update are B's
        Assert.Equal(2, Hb(B).Count);
        Assert.True(state.GrantItems(B, 19));
        Assert.Equal(2, state.Inventory(A).Count);
        Eq("5.0", state.Give(A, "gm", -28));
        Eq("[5.0, 33.0]", T(state.Wallet(A)["gm"], state.Wallet(B)["gm"]));
        Assert.Null(state.Give(B, "gm", -34));  // too little for B, though A has the same balance now
        state.WakeGroup(B, 100, 1);
        Eq("""{"amount": 5.0, "type": "gm", "update": 15}""", Assert.Single(Hb(A))!);
        Assert.Equal([10, 5], Hb(B).Select(u => (int)u!["update"]!));  // the granted item and the woken group
    }

    [Fact]
    public void TestHeartbeatsKillsAndHarvestsStayInTheirAccount()
    {
        state.NoteLaunch(A, "start=100&reserve=2"u8.ToArray());
        state.NoteLaunch(B, "start=100&reserve=2"u8.ToArray());
        // the same individual id and kill id in both accounts, with different animals
        Hb(A, Harvest(71, 50000));
        Hb(B, Harvest(71, 72270));
        Hb(B, Harvest(72, 10000, hit: false));
        Eq("[50000, 72270, 10000]", T(Reply(state.HarvestReply(A, 1)!)["weight"], Reply(state.HarvestReply(B, 1)!)["weight"],
                                      Reply(state.HarvestReply(B, 2)!)["weight"]));
        Assert.Null(state.HarvestReply(A, 2));
        Assert.False(state.Taxidermize(A, 2));
        Assert.True(state.Taxidermize(B, 2));
        Assert.False((bool)Reply(state.HarvestReply(B, 1)!)["taxidermized"]!);
        var (killsA, killsB) = (ExpeditionOf(A)["kills"]!.AsArray(), ExpeditionOf(B)["kills"]!.AsArray());
        Assert.Equal([1L], killsA.Select(k => (long)k!["id"]!));
        Assert.Equal([2L, 1L], killsB.Select(k => (long)k!["id"]!));
        Assert.Single(killsA[0]!["hits"]!.AsArray());  // A's own hit, not the one B made on the same individual
        Assert.Empty(killsB[0]!["hits"]!.AsArray());
        Assert.Single(killsB[1]!["hits"]!.AsArray());
        Eq("[1, 2]", T(Assert.Single(Hb(A), u => Py.Eq(u!["update"], 6))!["kill_id"], Assert.Single(Hb(B), u => Py.Eq(u!["update"], 6))!["kill_id"]));
        // the kill's page: kill ids repeat across accounts, and each page shows its own animal
        Assert.Contains("50.000 kg", Encoding.UTF8.GetString(state.ScorePageReply(A, 1)!));
        Assert.Contains("72.270 kg", Encoding.UTF8.GetString(state.ScorePageReply(B, 1)!));
        Assert.DoesNotContain("72.270 kg", Encoding.UTF8.GetString(state.ScorePageReply(A, 1)!));
        Assert.Null(state.ScorePageReply(A, 2));
    }

    [Fact]
    public void TestExpeditionsStayInTheirAccount()
    {
        state.NoteLaunch(A, "start=100&reserve=2"u8.ToArray());
        state.NoteLaunch(B, "start=300&reserve=3"u8.ToArray());
        var (a, b) = (ExpeditionOf(A)["expedition"]!, ExpeditionOf(B)["expedition"]!);
        Eq("[2, 3, null, null]", T(a["reserve"], b["reserve"], a["end_ts"], b["end_ts"]));
        Assert.NotEqual(Py.Dumps(a["id"]), Py.Dumps(b["id"]));
        state.Disconnect(A, """{"distance_travelled": 5}"""u8.ToArray());  // closes A's hunt only
        Assert.NotNull(ExpeditionOf(A)["expedition"]!["end_ts"]);
        Assert.Null(ExpeditionOf(B)["expedition"]!["end_ts"]);
        state.NoteLaunch(B, "start=400&reserve=5"u8.ToArray());
        var recorded = """{"total": 0, "expeditions": []}"""u8.ToArray();
        List<string> Listed(long account) => Reply(state.ExpeditionListReply(account, recorded, "limit=10&offset=0"u8.ToArray()))["expeditions"]!
            .AsArray().Select(e => Py.Dumps(e!["reserve"])).ToList();
        Assert.Equal(["2"], Listed(A));
        Assert.Equal(["5", "3"], Listed(B));
        // a connect opens or continues the account's own expedition
        Assert.NotEqual(Py.Dumps(Reply(state.ConnectReply(A))["expedition"]!["expedition_id"]), Py.Dumps(Reply(state.ConnectReply(B))["expedition"]!["expedition_id"]));
        Assert.Equal(2, Listed(A).Count);  // A's closed hunt and the one its connect opened
        Assert.Equal(2, Listed(B).Count);  // B's connect continued its open hunt
    }

    [Fact]
    public void TestPhotosStayInTheirAccountFolder()
    {
        var update = Assert.Single(Hb(B, O("""{"event": 4, "hash": 77, "photo_type": 2, "individual": 97}""")))!;
        var query = new Uri((string)update["url"]!).Query.TrimStart('?');
        Assert.Equal(404, state.UploadReply(A, query, "other"u8.ToArray()).Status);  // A cannot fill B's upload
        Assert.Empty(Directory.GetFiles(tmp, "*.jpg", SearchOption.AllDirectories));
        var (status, body) = state.UploadReply(B, query, [0xff, 0xd8]);
        Assert.Equal(200, status);
        var media = new Uri((string)Reply(body)["mediaLink"]!).AbsolutePath;
        Assert.Equal("/photos/8/1-2.jpg", media);
        Assert.Equal(Path.Combine(tmp, "photos", "8", "1-2.jpg"), state.PhotoFile(media));
        Assert.Null(state.PhotoFile("/photos/7/1-2.jpg"));
        Assert.Null(state.PhotoFile("/photos/8/../7/1-2.jpg"));
        Assert.Null(state.PhotoFile("/photos/1-2.jpg"));
        // A's own photo gets its own folder
        var own = Assert.Single(Hb(A, O("""{"event": 4, "hash": 5, "photo_type": 2}""")))!;
        Assert.Equal(200, state.UploadReply(A, new Uri((string)own["url"]!).Query.TrimStart('?'), [1]).Status);
        Assert.Equal(["7", "8"], Directory.GetDirectories(Path.Combine(tmp, "photos")).Select(d => Path.GetFileName(d)).Order());
    }

    [Fact]
    public void TestMessagesStayInTheirAccount()
    {
        Hb(B, O("""{"event": 7, "individual": 253, "species": 17, "gender": 0, "weight_grams": 1241}"""),
           O("""{"event": 1, "individual": 253, "ammunition_id": 10, "weapon_id": 5}"""));  // .243 on a rabbit: not permitted
        Assert.Equal((0L, 1L), (state.Unread(A), state.Unread(B)));
        Assert.Equal("[]", Encoding.ASCII.GetString(state.ConversationsReply(A)));
        Assert.Single(Reply(state.ConversationsReply(B)).AsArray());
        Eq("0", Reply(state.ConversationReply(A, "user_id=1024"u8.ToArray()))["total"]);
        Assert.Equal(1, state.Unread(B));  // reading A's (empty) thread marked nothing of B's
        Eq("1", Reply(state.ConversationReply(B, "user_id=1024"u8.ToArray()))["total"]);
        Assert.Equal(0, state.Unread(B));
        Assert.Empty(Hb(A));
        Assert.Single(Hb(B));  // the toast
    }

    [Fact]
    public void TestSkillsStayInTheirAccount()
    {
        var before = Py.Dumps(Numbers(A));
        Hb(B, O("""{"event": 7, "individual": 9, "species": 1}"""), O("""{"event": 11, "individual": 9}"""),
           O("""{"event": 1, "individual": 9, "weapon_id": 5, "ammunition_id": 10, "animal_state": 3}"""));
        Assert.Equal(before, Py.Dumps(Numbers(A)));
        Eq("[1.14, 1.273]", T(Numbers(B)["spotting"]![1], Numbers(B)["weapon"]!["5"]));
        Assert.Null(state.SkillListReply(B, "user_id=7"u8.ToArray()));  // another player: the recording is replayed
        // one clue each on the same individual id: the second account's count starts at its own hunt, so both pay the same
        state.NoteLaunch(A, "start=100&reserve=2"u8.ToArray());
        state.NoteLaunch(B, "start=100&reserve=2"u8.ToArray());
        var clue = O("""{"event": 12, "individual": 67, "track_type": 4}""");
        var start = Py.Dumps(Numbers(A)["tracking"]![1]);
        Hb(A, O("""{"event": 7, "individual": 67, "species": 1}"""), clue.DeepClone());
        Hb(B, O("""{"event": 7, "individual": 67, "species": 1}"""), clue.DeepClone());
        Assert.NotEqual(start, Py.Dumps(Numbers(A)["tracking"]![1]));
        Assert.Equal(Py.Dumps(Numbers(A)["tracking"]![1]), Py.Dumps(Numbers(B)["tracking"]![1]));
    }

    [Fact]
    public void TestAnAccountCanBeCreatedAndFoundBySteamId()
    {
        state.CreateAccount(9, "third", "steam-3");
        Assert.Equal(9L, state.Store.AccountBySteam("steam-3"));
        Eq("[182.0, 33.0]", T(state.Wallet(9)["em"], state.Wallet(9)["gm"]));
        Assert.Equal(A, state.PublicAccount("third"));  // offline: the page of any handle is the local account's
    }
}

/// <summary>Which account a request is for, as the game hosts see it (a community server: nothing is the local account's).</summary>
public class AccountRequestTests : StateTestBase, IAsyncLifetime
{
    static readonly JsonObject Cfg = new(Replay.HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));
    static readonly (string, string)[] FormHeader = [("Content-Type", "application/x-www-form-urlencoded")];
    string tokenA = "", tokenB = "";
    Loopback server = null!;

    public async ValueTask InitializeAsync()
    {
        state = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, discounts: DISCOUNTS, offline: false);
        state.CreateAccount(8, "second");
        (tokenA, tokenB) = (state.Store.CreateToken(ME), state.Store.CreateToken(8));
        Scoring.StaticHost = "static.test";
        Scoring.OwnLogo = "testlogo";
        var table = new Replay.HarTable();
        table.Entries[new("POST", "gameserver.test", Replay.CONNECT, null)] = new Replay.Hit(2, "", 200, [], "{}"u8.ToArray());
        server = await Loopback.StartAsync(new Replay.Game(table, Cfg, state, tmp), tmp);
    }

    public async ValueTask DisposeAsync() => await server.DisposeAsync();

    static (string, string)[] Bearer(string? token) => token is null ? [] : [("Authorization", "Bearer " + token)];

    /// <summary>A request with this game-server session header (none: no header) and body.</summary>
    Task<Reply> Send(string method, string host, string path, string? session, string? body = null, params (string, string)[] headers)
    {
        var all = headers.ToList();
        if (session is not null)
            all.Add(("X-Hun-Session", session));
        return server.SendAsync(method, host, path, all, body is null ? null : Encoding.ASCII.GetBytes(body));
    }

    JsonArray Updates(Reply r) => JsonNode.Parse(r.Body)!["updates"]!.AsArray();

    void HarvestOf8() => state.HeartbeatReply(8, Events(AccountIsolationTests.Harvest(71, 72270)));

    [Fact]
    public async Task TestTheGameServerReadsTheSessionHeader()
    {
        Assert.Single(Updates(await Send("POST", "gameserver.test", Replay.HEARTBEAT, tokenB, """{"events": [{"event": 4, "hash": 77, "photo_type": 2}]}""")));
        Assert.Empty(Updates(await Send("POST", "gameserver.test", Replay.HEARTBEAT, tokenA)));
        // B's upload id with A's session is refused; with B's it is stored under B, and the stored photo is public
        var query = new Uri((string)Assert.Single(Updates(await Send("POST", "gameserver.test", Replay.HEARTBEAT, tokenB,
            """{"events": [{"event": 4, "hash": 5, "photo_type": 2}]}""")))!["url"]!).Query;
        Assert.Equal(404, (await Send("POST", "gameserver.test", State.UPLOAD_PATH + query, tokenA, "x")).Status);
        Assert.Equal(200, (await Send("POST", "gameserver.test", State.UPLOAD_PATH + query, tokenB, "x")).Status);
        var photo = await Send("GET", "gameserver.test", "/photos/8/2-2.jpg", null);
        Assert.Equal((200, "state"), (photo.Status, photo.Source));
    }

    [Fact]
    public async Task TestNoTokenOrAnUnknownOneGetsTheExpiredTokenReply()
    {
        foreach (var token in new string?[] { null, "0000", state.Store.CreateToken(ME, TimeSpan.FromSeconds(-1)) })
        {
            var game = await Send("POST", "gameserver.test", Replay.HEARTBEAT, token, "{}");
            Assert.Equal((401, "denied", "{\"err\": 1}"), (game.Status, game.Source, Encoding.ASCII.GetString(game.Body)));
            Assert.Equal("denied", (await Send("POST", "gameserver.test", Replay.CONNECT, token, "{}")).Source);
            var api = await Send("POST", "api.test", Replay.BUY, null,
                "item_id=19&amount=1&wallet=GOLD" + (token is null ? "" : "&oauth_access_token=" + token), FormHeader);
            Assert.Equal((200, "denied", "{\"errorCode\": 5, \"message\": \"token expired\"}"), (api.Status, api.Source, Encoding.ASCII.GetString(api.Body)));
            var v2 = await Send("GET", "apiv2.test", "/game/user/harvest/1", null, null, Bearer(token));
            Assert.Equal((401, "denied"), (v2.Status, v2.Source));
        }
        Eq("182.0", state.Wallet(ME)["em"]);
        // the CORS preflight of a browser carries no token: it is the replay's, never a state reply
        Assert.Equal("miss", (await Send("OPTIONS", "apiv2.test", "/game/user/harvest/1", null, null, ("Origin", "https://web.test"))).Source);
    }

    [Fact]
    public async Task TestTheWebApiReadsTheTokenFromTheFormAndTheQuery()
    {
        var reply = await Send("POST", "api.test", Replay.BUY, null, "item_id=19&amount=1&wallet=GOLD&oauth_access_token=" + tokenB, FormHeader);
        Assert.Equal((200, "state"), (reply.Status, reply.Source));
        Eq("[182.0, 173.0]", T(state.Wallet(ME)["em"], state.Wallet(8)["em"]));
        // a token in the query and an unknown one in the form: the one that resolves wins
        reply = await Send("POST", "api.test", Replay.BUY + "?oauth_access_token=" + tokenA, null, "item_id=19&amount=1&wallet=GOLD&oauth_access_token=0000", FormHeader);
        Assert.Equal("state", reply.Source);
        Eq("[173.0, 173.0]", T(state.Wallet(ME)["em"], state.Wallet(8)["em"]));
    }

    [Fact]
    public async Task TestTheGameApiV2ReadsTheBearerToken()
    {
        state.NoteLaunch(8, "start=100&reserve=2"u8.ToArray());
        HarvestOf8();
        var mine = await Send("GET", "apiv2.test", "/game/user/harvest/1", null, null, [.. Bearer(tokenB), ("Origin", "https://web.test")]);
        Assert.Equal((200, "state"), (mine.Status, mine.Source));
        Assert.Contains(("Access-Control-Allow-Origin", "https://web.test"), mine.Headers);
        Eq("72270", JsonNode.Parse(mine.Body)!["weight"]);
        // A has no kill 1: not B's page, and never a recorded one: the endpoint's empty reply
        var other = await Send("GET", "apiv2.test", "/game/user/harvest/1", null, null, Bearer(tokenA));
        Assert.Equal((404, "empty"), (other.Status, other.Source));
        Assert.Equal("empty", (await Send("POST", "apiv2.test", "/game/user/harvest/1/taxidermize", null, null, Bearer(tokenA))).Source);
        Assert.Equal("state", (await Send("POST", "apiv2.test", "/game/user/harvest/1/taxidermize", null, null, Bearer(tokenB))).Source);
        Assert.Equal("state", (await Send("OPTIONS", "apiv2.test", "/game/user/harvest/1", null, null, [.. Bearer(tokenB), ("Origin", "https://web.test")])).Source);
    }

    [Fact]
    public async Task TestAnOversizedHarvestIdIsNotAServerError()
    {
        const string big = "99999999999999999999";
        var statuses = new List<int>();
        foreach (var (method, path, token) in new[] { ("GET", "/game/user/harvest/" + big, null), ("GET", "/game/user/harvest/" + big, tokenA), ("POST", "/game/user/harvest/" + big + "/taxidermize", tokenA) })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), new Uri("http://apiv2.test" + path, new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true }));
            if (token is not null) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            using var response = await server.Http.SendAsync(request, TestContext.Current.CancellationToken);
            statuses.Add((int)response.StatusCode);
        }
        Assert.Equal("404,404,404", string.Join(",", statuses));
    }

    [Fact]
    public async Task TestAKillPageIsFoundByTheHandleInItsUrl()
    {
        HarvestOf8();
        var page = await Send("GET", "web.test", "/user/second/score/?id=1", null);
        Assert.Equal("state", page.Source);
        Assert.Contains("72.270 kg", Encoding.UTF8.GetString(page.Body));
        Assert.Equal("miss", (await Send("GET", "web.test", "/user/hunter/score/?id=1", null)).Source);  // the local account has no kill 1
        Assert.Equal("miss", (await Send("GET", "web.test", "/user/nobody/score/?id=1", null)).Source);
    }

    [Fact]
    public void TestOfflineAnswersAnUnknownTokenAsTheLocalAccount()
    {
        Assert.Null(state.Authenticate([]));
        Assert.Null(state.Authenticate(["0000"]));
        Assert.Equal(8L, state.Authenticate(["0000", tokenB, tokenA]));  // the first that resolves
        var local = new State(Path.Combine(tmp, "local.db"), Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES);
        Assert.Equal(ME, local.LocalAccount);
        Assert.Equal(ME, local.Authenticate([]));
        Assert.Equal(ME, local.Authenticate(["0000"]));
        local.CreateAccount(8, "second");
        Assert.Equal(8L, local.Authenticate([local.Store.CreateToken(8)]));
    }
}
