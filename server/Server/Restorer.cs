// Restore layer B: the player model of a backup zip (src/Restore, layer A) becomes the rows of one account of the
// database, in one transaction, and the zip is kept. The same zip always gives the same rows.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Restore;

namespace Resurgence.Server;

public static class Restorer
{
    /// <summary>Report: what layer A read, what was stored per table, what was dropped and why (also kept in the audit row).</summary>
    public sealed record Result(long Account, bool Created, string Sha256, string Report);

    /// <summary>The account's own-backup restore changed between the caller's check and the write transaction (<see cref="Apply"/>, `checkFill`): nothing was written.</summary>
    internal sealed class FillChangedException : Exception;

    /// <summary>The gm$ an account has at least after a restore: a new account starts with the same (`start.wallet` of the facts file).</summary>
    public const double GmFloor = 15000;

    /// <summary>The em$ a new account starts with (`start.wallet`): a restore never takes the part of it the account has not spent.</summary>
    public const double StartEm = 1000;

    /// <summary>Keys dropped from every JSON reply that is stored: the accounts of this server keep no e-mail, real name, address or phone.</summary>
    static readonly string[] Personal = ["email", "first_name", "last_name", "real_name", "address", "phone"];

    /// <summary>Restore the zip into an account: any zip is accepted and replaces the account's profile rows (no merge;
    /// hunt history stays). target null: the account of the zip's user id. A missing target account is created only from
    /// that user id and the zip's handle. An existing account is copied aside first (<see cref="Store.Backup"/>) and keeps
    /// its handle when the zip's is another account's. The zip is kept as `restores/&lt;account&gt;/&lt;sha256&gt;.zip` in dataDir.</summary>
    /// <exception cref="RestoreException">The zip is refused, the target does not exist, or the new account's handle is taken.</exception>
    public static Result Import(State state, string zipPath, long? target, string dataDir, PictureLinks? links = null)
    {
        var sha = Sha256(zipPath);
        var read = Importer.Read(zipPath, state.Catalogue);
        var model = read.Model;
        var uid = long.Parse(model.Identity.Uid);  // layer A: digits only and a 64-bit id
        var id = target ?? uid;
        var store = state.Store;
        var exists = store.AccountExists(id);
        if (!exists && id != uid)
            throw new RestoreException($"account {id} does not exist (a new account takes the zip's user id)");
        var zipHandle = model.Profile.Handle ?? model.Identity.Handle;
        string handle;
        if (!exists)
        {
            handle = zipHandle ?? $"user-{uid}";
            if (store.AccountByHandle(handle) is not null)
                throw new RestoreException("the zip's handle belongs to another account: restore it into that account, or settle the handle first");
        }
        else
        {
            var owner = zipHandle is null ? null : store.AccountByHandle(zipHandle);
            handle = zipHandle is not null && (owner is null || owner == id) ? zipHandle : (string)store.AccountJson(id)["handle"]!;
        }
        return Apply(state, zipPath, sha, read, id, id, handle, exists, backupDatabase: true, dataDir, null, pictureLinks: links);
    }

    /// <summary>Restore only the pictures of the zip onto an existing account (<see cref="State.PreparePictures"/>) and its avatar; nothing else of the account changes, and no picture of it is removed:
    /// only what the account lacks is added. The pictures of the gallery links the zip lacks come from the zip of fetched pictures kept with it (<see cref="PicturesZip"/>); when none was kept and `links` is
    /// given, they are fetched now. Returns the pictures report and the avatar link.</summary>
    /// <exception cref="RestoreException">The zip is refused or the account does not exist.</exception>
    public static (string Report, string Avatar) ImportPictures(State state, string zipPath, long id, string dataDir, PictureLinks? links = null)
    {
        var read = Importer.Read(zipPath, state.Catalogue);
        var store = state.Store;
        if (!store.AccountExists(id)) throw new RestoreException($"account {id} does not exist");
        var kept = Path.Combine(dataDir, "restores", id.ToString(), Sha256(zipPath) + ".zip");
        var copied = !File.Exists(kept);
        if (copied)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
            File.Copy(zipPath, kept);
        }
        string avatar;
        State.Unpacking pictures;
        try
        {
            var linked = PicturesZip(kept);
            FetchMissing(state, read, links, linked);  // before the transaction opens
            var hashes = state.HashPictures(read, id, kept, File.Exists(linked) ? linked : null, replace: false);  // before the transaction: it holds the database for writing
            avatar = state.RestoredAvatarUrl(read, id, state.AvatarFetched(read, hashes));
            using var con = store.Open(id);
            con.Begin();
            pictures = state.PreparePictures(con, read, hashes, replace: false);
            var account = store.AccountJson(id);
            var (face, picture, gender) = state.AvatarFace(state.ListedAvatarOf(read), account["gender"] is JsonValue g0 && g0.TryGetValue<long>(out var g1) ? g1 : 0);
            SetAvatar(account, face, picture, avatar);
            account["gender"] = gender;
            con.Exec("UPDATE accounts SET account = ? WHERE id = ?", Py.Dumps(account), id);
            if (con.Scalar("SELECT body FROM profile WHERE account_id = $a") is string body && JsonNode.Parse(body) is JsonObject me)
            {
                me["avatar"] = avatar;
                me["gender"] = gender;
                PlayerRows.SetProfile(con, me);
            }
            con.Commit();
        }
        catch
        {
            if (copied)
            {
                File.Delete(kept);
                File.Delete(PicturesZip(kept));
            }
            throw;
        }
        state.Unpack(id, kept, PicturesZip(kept), pictures);
        return (pictures.Report, avatar);
    }

    /// <summary>The zip of the pictures fetched from the links of a kept backup zip (<see cref="PictureLinks"/>), beside it: `restores/&lt;account&gt;/&lt;sha256&gt;.pictures.zip`.</summary>
    public static string PicturesZip(string keptZip) => keptZip[..^".zip".Length] + ".pictures.zip";

    public static string Sha256(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(f));
    }

    /// <summary>Every link the zip lacks (<see cref="State.MissingPictures"/>: the player's own avatar first, then the gallery) that the pictures zip `linked` (created when absent) lacks too, fetched into it; no `links`: nothing.
    /// The fetch at upload time gives up at its time budget (it is part of the player's request), so every restore path (approve, `import backup`, `import pictures`) completes it, with no budget but the per-link one.
    /// ponytail: on the caller's thread, which is the operator's command; a link that fails is tried again at each restore.</summary>
    static void FetchMissing(State state, Restored read, PictureLinks? links, string linked)
    {
        if (links is not null && state.MissingPictures(read) is { Count: > 0 } missing)
            links.Fetch(missing, linked, Timeout.InfiniteTimeSpan).GetAwaiter().GetResult();
    }

    /// <summary>Write the model into the account `current` (created when it does not exist) and keep the zip. finalId: the id
    /// the account has afterwards (a restore that adopts the zip's user id; every row, token and session moves with it).
    /// within runs inside the transaction after the rows are written. backupDatabase: copy the whole database first
    /// (the local rule; a community server keeps the zip as the way back instead). zipPath null: the model came from the operator's archive and no zip is kept.
    /// fill: not null = replace nothing: no row, picture file or name of the account is replaced, only the tables and keys the account has no row for are filled (an archive claim over a restore of the account's own backup);
    /// it receives the paths of the saved replies (extras) that were added. checkFill: inside the transaction, throw <see cref="FillChangedException"/> when the account's own backup is restored differently from what `fill` assumed.
    /// merge: the account already has this profile from an archive claim (`exists`, finalId == current): only what it lacks is added (<see cref="Merge"/>), nothing it has is replaced; separate from fill (an own-backup restore).</summary>
    internal static Result Apply(State state, string? zipPath, string sha, Restored read, long current, long finalId, string handle, bool exists,
                                 bool backupDatabase, string dataDir, Action<Db>? within, string? linksZip = null, PictureLinks? pictureLinks = null, HashSet<string>? fill = null, bool checkFill = false, bool merge = false)
    {
        var model = read.Model;
        var store = state.Store;
        var kept = Path.Combine(dataDir, "restores", finalId.ToString(), sha + ".zip");
        var copied = zipPath is not null && !File.Exists(kept);
        var backup = exists && backupDatabase ? store.Backup() : null;
        if (copied)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
            File.Copy(zipPath!, kept);
        }
        // the pictures fetched at upload time sit beside the kept zip: under the account's id here, or under the one it had before it adopted the zip's; an archive claim has no zip: the caller's own (`linksZip`)
        var linked = zipPath is null ? linksZip : new[] { kept, Path.Combine(dataDir, "restores", current.ToString(), sha + ".zip") }.Select(PicturesZip).FirstOrDefault(File.Exists);
        if (zipPath is not null)  // a zip kept with some of its links not fetched (the upload's time budget, or a zip kept before): the rest are fetched now, into the pictures zip beside the kept zip
        {
            var target = linked ?? PicturesZip(kept);
            FetchMissing(state, read, pictureLinks, target);
            if (File.Exists(target)) linked = target;
        }
        var hashes = state.HashPictures(read, current, zipPath, linked, replace: fill is null && !merge);  // before the transaction: it holds the database for writing
        var avatarUrl = state.RestoredAvatarUrl(read, finalId, state.AvatarFetched(read, hashes));
        var (face, picture, gender) = state.AvatarFace(state.ListedAvatarOf(read), model.Profile.Gender);  // a 3D face of the body's gender, whatever the archive's face and gender were
        model = model with { Profile = model.Profile with { Gender = (int)gender }, Account = model.Account with { Face = (int)face } };
        var account = AccountObject(exists ? store.AccountJson(current) : state.NewAccount(current, handle), model, finalId, handle, avatarUrl, picture);
        var report = "";
        State.Unpacking? pictures = null;
        void Write(Db con)
        {
            pictures = state.PreparePictures(con, read, hashes, replace: fill is null && !merge, keepFiles: fill is not null || merge);
            report = merge ? Merge(con, model, read.Report, sha, pictures.Report) : Rows(con, model, avatarUrl.EndsWith("/" + State.ImportedAvatar, StringComparison.Ordinal) ? read.Report with { Warnings = [.. read.Report.Warnings.Where(w => !w.StartsWith("the avatar picture address matches no avatar", StringComparison.Ordinal))] } : read.Report,
                          finalId, handle, exists, sha, backup, pictures.Report, avatarUrl, state.StartItems(), fill, state);  // the importer warns of an address that is no stock avatar; a stored picture is no default
            within?.Invoke(con);
            if (merge) return;
            if (finalId != current) Adopt(con, current, finalId);
            Friends(con, finalId, fill is null ? model.Extras : model.Extras.Where(e => fill.Contains(e.Key)).ToDictionary());
            if (long.TryParse(model.Identity.Uid, out var oldId)) Store.ReconnectFriends(con, oldId);  // the edges other restores saved to this profile's old user id now name the account that holds it
        }
        try
        {
            if (!exists)
                store.CreateAccount(current, handle, account, null, Write);
            else
            {
                using var con = store.Open(current);
                con.Begin();
                if (checkFill && (con.Scalar("SELECT 1 FROM restore_audit WHERE account_id = $a AND mode = 'my-account'") is not null) != (fill is not null))
                    throw new FillChangedException();  // the account's own backup was restored since the caller looked: nothing is written, the caller decides again
                if (fill is null && !merge) con.Exec("UPDATE accounts SET handle = ?, account = ? WHERE id = ?", handle, Py.Dumps(account), current);
                Write(con);
                con.Commit();
            }
        }
        catch
        {
            if (copied) File.Delete(kept);
            throw;
        }
        using (var con = store.Open(finalId))  // a community server's value is computed from the restored statistics
            state.UpdateHunterscore(con, finalId);
        if (state.Online) state.BackfillLodges(finalId);  // a community server: the restore brings lodge items, not the lodges
        var before = Path.Combine(dataDir, "restores", current.ToString());
        if (finalId != current && Directory.Exists(before))  // zips kept for review under the old id follow the account
        {
            foreach (var file in Directory.GetFiles(before))
                File.Move(file, Path.Combine(Path.GetDirectoryName(kept)!, Path.GetFileName(file)), overwrite: true);
            Directory.Delete(before);
        }
        if (finalId != current)  // the photos taken here follow the account; a name clash keeps the account's own photo
            state.MovePhotos(current, finalId);
        var reports = Path.Combine(dataDir, "reports", current.ToString());
        if (finalId != current && Directory.Exists(reports))  // so do its problem reports, whose references name the account
        {
            var target = Path.Combine(dataDir, "reports", finalId.ToString());
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(reports))
                File.Move(file, Path.Combine(target, Path.GetFileName(file).Replace($"p{current}-", $"p{finalId}-")), overwrite: true);
            Directory.Delete(reports);
        }
        state.Unpack(finalId, zipPath is null ? null : kept, zipPath is null ? linksZip : PicturesZip(kept), pictures!);  // last: the files follow the committed rows, and a failure here (the disk) leaves the account restored, its hunterscore updated and its zips under its id; a restore again replaces them
        return new(finalId, !exists, sha, report);
    }

    /// <summary>The saved replies a merge adds: the hunt list and details, the gallery lists and the competition replies (by their names in `extras`). Every other reply (the profiles, friends, missions, ...) is the claim's or the account's own.</summary>
    static readonly string[] Merged = ["Expedition_list", "Public_user_expedition", "Gallery_list", "Page_content_"];

    /// <summary>What a save adds to an account that has its profile from an archive claim: the saved replies <see cref="Merged"/> (hunts and their details, gallery lists, competitions) and the lodge rows it lacks; one it has stays, and so do its
    /// items, wallet, skills, statistics, achievements, trophies, profile and the play since. The gallery pictures come from <see cref="State.PreparePictures"/> (no replace). Adds the audit row (mode `merge`, which the start em$ rule
    /// does not count as a restored wallet); returns the report.</summary>
    static string Merge(Db con, PlayerModel m, Report layerA, string sha, string pictures)
    {
        int extras = 0, lodges = 0;
        var wanted = m.Extras.Where(e => Merged.Any(p => e.Key.StartsWith(p, StringComparison.Ordinal))).ToList();
        foreach (var (path, body) in wanted)
        {
            Strip(body);
            if (PlayerRows.AddExtra(con, path, body)) extras++;
        }
        // a saved lodge is added (both of its rows) when the account has no lodge of its id and none of its type. When the account holds a lodge of that type, the saved one replaces it
        // (rows updated in place: the account's lodge id stays) only if that lodge has nothing placed (the empty lodge the server gave for a lodge item, which a claim without lodges leaves); else the account's stays
        static long? TypeOf(JsonNode? node) =>
            node?["lodge_type_id"] is { } t ? Py.IsNum(t) ? Py.L(t) : Py.IsStr(t) && long.TryParse((string)t!, out var n) ? n : null : null;
        static bool Placed(JsonNode? slots, string key) => slots is JsonArray a && a.OfType<JsonObject>().Any(s => s[key] is { } v && !(Py.IsNum(v) && Py.L(v) == 0));
        static bool Filled(JsonNode? node) => Placed(node?["animal_slots"], "trophy_id") || Placed(node?["weapon_slots"], "weapon_id");
        var held = con.Rows("SELECT id, node FROM lodges WHERE account_id = $a AND source IN (?, ?)", State.LodgeList, State.LodgeOne)
            .Select(r => (Id: (string)r[0], Node: JsonNode.Parse((string)r[1]))).GroupBy(r => r.Id)
            .Select(g => (Id: g.Key, Type: g.Select(r => TypeOf(r.Node)).FirstOrDefault(t => t is not null), Filled: g.Any(r => Filled(r.Node)), Taken: false)).ToList();
        foreach (var lodge in m.Lodges.GroupBy(l => l.Id).OrderBy(g => g.Key.Length).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            var type = lodge.Select(l => TypeOf(l.Node)).FirstOrDefault(t => t is not null);
            if (held.Any(h => h.Id == lodge.Key)) continue;
            var same = type is null ? -1 : held.FindIndex(h => h.Type == type && !h.Taken && !h.Filled);
            if (type is not null && same < 0 && held.Any(h => h.Type == type)) continue;  // the account's lodge of that type has something placed, or was just replaced
            foreach (var l in lodge)
            {
                Strip(l.Node);
                if (same < 0) PlayerRows.AddLodge(con, l.Source, l.Id, l.Node);
                else
                {
                    var id = held[same].Id;
                    if (l.Node is JsonObject o) o["id"] = long.TryParse(id, out var number) ? number : id;
                    if (con.Exec("UPDATE lodges SET node = ? WHERE account_id = $a AND source = ? AND id = ?", Py.Dumps(l.Node), l.Source, id) == 0) PlayerRows.AddLodge(con, l.Source, id, l.Node);
                }
                lodges++;
            }
            if (same >= 0) held[same] = held[same] with { Taken = true };
        }
        var text = new StringBuilder(layerA.ToText());
        text.AppendLine("account: archive claim kept, missing parts added");
        text.AppendLine($"added: saved replies {extras} of {wanted.Count}, lodge rows {lodges} of {m.Lodges.Count}");
        text.AppendLine(pictures);
        var report = text.ToString();
        PlayerRows.AddAudit(con, sha, "merge", report);
        return report;
    }

    /// <summary>Give the account (and its rows, tokens, sessions and claimed user ids) another user id, inside the caller's
    /// transaction: the foreign keys are checked at its end, when every row has moved.</summary>
    public static void Adopt(Db con, long from, long to)
    {
        con.Script("PRAGMA defer_foreign_keys = ON");
        foreach (var table in Store.PlayerTables)
            con.Exec($"UPDATE {table} SET account_id = ? WHERE account_id = $a", to);
        foreach (var table in new[] { "tokens", "web_sessions", "restored_ids", "claims", "claim_lookups", "community_progress", "crash_reports", "admin_messages", "discord_links", "discord_rewards" })
            con.Exec($"UPDATE {table} SET account_id = ? WHERE account_id = ?", to, from);
        con.Exec("UPDATE accounts SET id = ? WHERE id = ?", to, from);
        foreach (var column in new[] { "account_id", "other_id" })  // the friend edges (no foreign keys); one the new id has already stays
            con.Exec($"UPDATE OR IGNORE friends SET {column} = ? WHERE {column} = ?", to, from);
        con.Exec("DELETE FROM friends WHERE account_id = ?1 OR other_id = ?1", from);
        foreach (var column in new[] { "host_id", "banned_id" })  // the host bans (no foreign keys either)
            con.Exec($"UPDATE OR IGNORE host_bans SET {column} = ? WHERE {column} = ?", to, from);
        con.Exec("DELETE FROM host_bans WHERE host_id = ?1 OR banned_id = ?1", from);
    }

    /// <summary>The friend edges of the restored `User_friendslist` and `Me_friends` replies: a friend, an incoming request (`requests`),
    /// an outgoing one (`pending`). A player's old user id is the account that holds that profile here (<see cref="Store.Holder"/>); each edge counts once the other player has an account here (<see cref="Store.FriendsOf"/>).</summary>
    static void Friends(Db con, long id, IReadOnlyDictionary<string, JsonNode?> extras)
    {
        static IEnumerable<JsonNode?> List(JsonNode? body, string key) => (body as JsonObject)?[key] as JsonArray ?? [];
        static long Id(JsonNode? n) => n is JsonObject o ? Id(o["id"]) : n is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0;
        foreach (var (path, body) in extras.Where(e => e.Key.StartsWith("User_friendslist", StringComparison.Ordinal) || e.Key == "Me_friends.json"))
        {
            foreach (var f in List(body, "friends")) Store.RestoreFriendEdge(con, id, Id(f), "friend");
            foreach (var f in List(body, "requests")) Store.RestoreFriendEdge(con, Id(f), id, "request");
            foreach (var f in List(body, "pending")) Store.RestoreFriendEdge(con, id, Id(f), "request");
        }
    }

    /// <summary>The connect reply's `account` object: base (the account's own, or the recorded one for a new account) with the
    /// profile's fields and the importer's defaults, and `avatarUrl` (<see cref="State.RestoredAvatarUrl"/>). No e-mail, Facebook id or Steam id of the zip.</summary>
    static JsonObject AccountObject(JsonObject baseAccount, PlayerModel m, long id, string handle, string avatarUrl, long picture)
    {
        var a = (JsonObject)baseAccount.DeepClone();
        var p = m.Profile;
        a["handle"] = handle;
        a["user_id"] = id;
        a["account_id"] = id;
        a["membership"] = p.MembershipInt;
        a["gender"] = p.Gender;
        a["hunterscore"] = p.Hunterscore;
        SetAvatar(a, m.Account.Face, picture, avatarUrl);
        a["facebook_enabled"] = m.Account.FacebookEnabled;
        a["share_on_facebook"] = m.Account.ShareOnFacebook != 0;
        Put(a, "hostname", p.Hostname);
        return a;
    }

    /// <summary>The account's own copies of the chosen avatar: `face` (a 3D face), `avatar`, `avatar_id` (the picture, where it has them) and `avatar_url`.</summary>
    static void SetAvatar(JsonObject a, long face, long picture, string avatarUrl)
    {
        a["face"] = face;
        foreach (var key in new[] { "avatar", "avatar_id" })
            if (a.ContainsKey(key)) a[key] = picture;
        a["avatar_url"] = avatarUrl;
    }

    static void Put(JsonObject o, string key, JsonNode? value)
    {
        if (value is not null) o[key] = value;
    }

    /// <summary>Drop the personal keys from every object below n; returns how many.</summary>
    static int Strip(JsonNode? n)
    {
        switch (n)
        {
            case JsonObject o:
                var count = 0;
                foreach (var key in o.Select(p => p.Key).ToList())
                    if (Personal.Contains(key, StringComparer.OrdinalIgnoreCase))
                    {
                        o.Remove(key);
                        count++;
                    }
                    else
                        count += Strip(o[key]);
                return count;
            case JsonArray a:
                return a.Sum(Strip);
            default:
                return 0;
        }
    }

    static JsonObject Vec(Vec3I v) => new() { ["x"] = v.X, ["y"] = v.Y, ["z"] = v.Z };

    /// <summary>Replace the account's profile rows with the model's (inside the caller's transaction) and add the audit row; `filled` not null: replace nothing, fill what the account lacks (see <see cref="Apply"/>).
    /// returns the report.</summary>
    static string Rows(Db con, PlayerModel m, Report layerA, long id, string handle, bool replaced, string sha, string? backup, string pictures, string avatarUrl, JsonArray kit, HashSet<string>? filled, State state)
    {
        // the start em$ the account has not spent yet, until a zip's wallet replaced them once (a claim keeps the account's wallet)
        var unspentStartEm = con.Scalar("SELECT 1 FROM restore_audit WHERE account_id = $a AND zip_sha256 NOT LIKE 'archive:%' AND mode <> 'merge'") is null
            ? Math.Min(Convert.ToDouble(con.Scalar("SELECT amount FROM wallet WHERE account_id = $a AND currency = 'em'") ?? 0.0, System.Globalization.CultureInfo.InvariantCulture), StartEm)
            : 0;
        // a fill (an archive claim over a restore of the account's own backup) replaces nothing: only a table with no row of the account is written, and an extra only under a path it lacks
        var empty = new[] { "profile", "wallet", "items", "skills", "statistics", "achievements", "trophies", "lodges", "loadouts" }
            .Where(t => filled is null || con.Scalar($"SELECT 1 FROM {t} WHERE account_id = $a") is null).ToHashSet();
        if (filled is null) PlayerRows.ClearModel(con);
        var lines = new List<string>();
        var dropped = new List<string>();
        var personal = 0;

        // profile: the zip's own Me/me reply (the wallet is its own table) under the typed fields
        var me = m.Extras.TryGetValue("Me_me.json", out var raw) && raw is JsonObject saved ? (JsonObject)saved.DeepClone() : [];
        foreach (var key in new[] { "ems", "gms", "unread_messages" })
            me.Remove(key);
        var p = m.Profile;
        me["id"] = id;
        me["handle"] = handle;
        Put(me, "hostname", p.Hostname);
        me["gender"] = p.Gender;
        me["hunterscore"] = p.Hunterscore;
        me["avatar"] = avatarUrl;
        Put(me, "membershipType", p.MembershipType);
        Put(me, "uuid", p.Uuid);
        Put(me, "locale", p.Locale);
        Put(me, "timezone", p.Timezone);
        Put(me, "country", p.Country);
        Put(me, "memberSince", p.MemberSince);
        Put(me, "rank", p.Rank);
        me["user_group"] ??= p.UserGroup;
        personal += Strip(me);
        if (empty.Contains("profile")) PlayerRows.SetProfile(con, me);

        if (empty.Contains("wallet"))
        {
            var wallet = m.Wallet ?? new Wallet(0, 0);
            var (em, gm) = (Math.Max(wallet.Em, unspentStartEm), Math.Max(wallet.Gm, GmFloor));  // every restore leaves the player enough gm$ to start again, and the start em$ not spent yet
            PlayerRows.SetWallet(con, "em", em);
            PlayerRows.SetWallet(con, "gm", gm);
            if (m.Wallet is null) dropped.Add($"not in the zip: wallet ({em} em, {gm} gm stored)");
            else
            {
                if (em > wallet.Em) lines.Add($"wallet: em raised from {wallet.Em} to {em}");
                if (gm > wallet.Gm) lines.Add($"wallet: gm raised from {wallet.Gm} to {gm}");
            }
        }

        if (empty.Contains("items"))
        {
            // a player owns at most as many of an item as its limit: the copies in use stay (equipped, placed), then the oldest (State.Excess); the rest are not restored
            var over = state.Excess(m.Items, i => i.ItemId, i => i.Slot > 0 || i.Position is not null, i => i.Guid).Select(i => i.Guid).ToHashSet();
            if (over.Count > 0) lines.Add($"items over their own limit, not restored: {over.Count}");
            foreach (var i in m.Items.Where(x => !over.Contains(x.Guid)))
            {
                var metadata = (JsonObject)i.Metadata.DeepClone();
                metadata.Remove(State.POSITION);  // the zip's own metadata cannot claim a placement
                metadata.Remove(State.DIRECTION);
                if (i.Position is { } pos) metadata[State.POSITION] = Vec(pos);
                if (i.Direction is { } dir) metadata[State.DIRECTION] = Vec(dir);
                metadata.Remove(State.RESERVE);
                if (i.WebReserve is { } reserve) metadata[State.RESERVE] = reserve;
                PlayerRows.AddItem(con, i.Guid, i.ItemId, i.Slot, i.Units, metadata);
            }

            // every restore leaves at least the starting kit: each start item id the model lacks is added as a new account gets it (its slot unless taken, else the backpack)
            var added = PlayerRows.AddMissingStart(con, kit, m.Items.Select(i => ((long)i.ItemId, (long)i.Slot)), m.Items.Select(i => i.Guid).DefaultIfEmpty(0).Max());
            if (added > 0) lines.Add($"start items added: {added}");

            // one weapon to a weapon slot and one fitting ammunition to its ammunition slot (the zip's two inventory replies can each hold one): the others go to the lodge (#1009)
            var lodged = state.SettleEquipped(con);
            if (lodged > 0) lines.Add($"weapons and ammunition not fitting the equipped slots, put in the lodge: {lodged}");
        }

        var skills = 0;
        void Skill(string kind, long key, double number)
        {
            if (!empty.Contains("skills")) return;
            PlayerRows.SetSkill(con, kind, key, Skills.Xp(number));
            skills++;
        }
        if (m.Skills is { } s)
        {
            foreach (var (item, number) in s.Weapon) Skill("weapon", item, number);
            for (var k = 0; k < s.Tracking.Length; k++) Skill("tracking", k, s.Tracking[k]);
            for (var k = 0; k < s.Spotting.Length; k++) Skill("spotting", k, s.Spotting[k]);
        }
        else if (empty.Contains("skills"))
        {
            for (var k = 0; k < 59; k++)
            {
                Skill("tracking", k, 1);
                Skill("spotting", k, 1);
            }
            dropped.Add("not in the zip: skills (a new player's levels stored)");
        }

        if (m.Stats is { } st && empty.Contains("statistics"))
            PlayerRows.SetStatistics(con, st.UserStats, st.Distance, st.Duration, st.BustThroughScore, st.LaneBanditScore);
        if (empty.Contains("achievements"))
            foreach (var a in m.Achievements)
                PlayerRows.AddAchievement(con, a.Id, a.CategoryId, a.GroupId, a.Value, a.Triggers);
        foreach (var t in m.Trophies.Where(_ => empty.Contains("trophies")))
        {
            personal += Strip(t.Node);
            PlayerRows.AddTrophy(con, t.Source, t.Id, t.Node);
        }
        foreach (var l in m.Lodges.Where(_ => empty.Contains("lodges")))
        {
            personal += Strip(l.Node);
            PlayerRows.AddLodge(con, l.Source, l.Id, l.Node);
        }
        foreach (var (path, body) in m.Extras)
        {
            personal += Strip(body);
            if (filled is null) PlayerRows.SetExtra(con, path, body);
            else if (PlayerRows.AddExtra(con, path, body)) filled.Add(path);
        }
        State.EnsureMissions(con);  // the saved mission states become the account's rows (the account's own, when it has them)
        con.Exec(Store.RestoredChallenges);  // the saved completed challenges too (a challenge the account has stays)
        var loadouts = filled is null || (filled.Contains("game_user_loadout.json") && empty.Contains("loadouts")) ? con.Exec(Store.RestoredLoadouts) : 0;  // the saved loadouts, now under this server's ids

        lines.Add($"{(filled is null ? "stored" : "offered, stored only where the account had no row")}: items {m.Items.Count}, wallet 2, skills {skills}, profile 1, statistics {(m.Stats is null ? 0 : 1)}, achievements {m.Achievements.Count}, "
                  + $"trophies {m.Trophies.Count}, lodges {m.Lodges.Count}, extras {m.Extras.Count}, loadouts {loadouts}");
        lines.Add(pictures);
        if (personal > 0) dropped.Add($"{personal} personal field(s) (e-mail, real name, address, phone) in the saved replies");
        dropped.Add("e-mail, Facebook id and the other login-only fields of the account: this server's accounts keep none");
        if (m.Identity.SteamId64 is not null) dropped.Add("the zip's Steam id: a claim, not proof; the account's Steam id is only the one the server verified");
        dropped.Add("the zip's claim hash: a per-PC key, not proof of the account");

        var text = new StringBuilder(layerA.ToText());
        text.AppendLine($"account: {(filled is not null ? "profile kept, only what it had no row for was added" : replaced ? "profile replaced" : "created")}");
        if (backup is not null) text.AppendLine($"old database copied to {Path.GetFileName(backup)}");
        foreach (var line in lines) text.AppendLine(line);
        text.AppendLine($"dropped: {dropped.Count}");
        foreach (var d in dropped) text.AppendLine("  " + d);
        var report = text.ToString();
        PlayerRows.AddAudit(con, sha, layerA.Mode, report);
        return report;
    }
}
