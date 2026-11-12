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

    /// <summary>The gm$ an account has at least after a restore: a new account starts with the same (`start.wallet` of the facts file).</summary>
    public const double GmFloor = 15000;

    /// <summary>Keys dropped from every JSON reply that is stored: the accounts of this server keep no e-mail, real name, address or phone.</summary>
    static readonly string[] Personal = ["email", "first_name", "last_name", "real_name", "address", "phone"];

    /// <summary>Restore the zip into an account: any zip is accepted and replaces the account's profile rows (no merge;
    /// hunt history stays). target null: the account of the zip's user id. A missing target account is created only from
    /// that user id and the zip's handle. An existing account is copied aside first (<see cref="Store.Backup"/>) and keeps
    /// its handle when the zip's is another account's. The zip is kept as `restores/&lt;account&gt;/&lt;sha256&gt;.zip` in dataDir.</summary>
    /// <exception cref="RestoreException">The zip is refused, the target does not exist, or the new account's handle is taken.</exception>
    public static Result Import(State state, string zipPath, long? target, string dataDir)
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
        return Apply(state, zipPath, sha, read, id, id, handle, exists, backupDatabase: true, dataDir, null);
    }

    public static string Sha256(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(f));
    }

    /// <summary>Write the model into the account `current` (created when it does not exist) and keep the zip. finalId: the id
    /// the account has afterwards (a restore that adopts the zip's user id; every row, token and session moves with it).
    /// within runs inside the transaction after the rows are written. backupDatabase: copy the whole database first
    /// (the local rule; a community server keeps the zip as the way back instead).</summary>
    internal static Result Apply(State state, string zipPath, string sha, Restored read, long current, long finalId, string handle, bool exists,
                                 bool backupDatabase, string dataDir, Action<Db>? within)
    {
        var model = read.Model;
        var store = state.Store;
        var account = AccountObject(exists ? store.AccountJson(current) : state.NewAccount(current, handle), model, finalId, handle);
        var kept = Path.Combine(dataDir, "restores", finalId.ToString(), sha + ".zip");
        var copied = !File.Exists(kept);
        var backup = exists && backupDatabase ? store.Backup() : null;
        if (copied)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
            File.Copy(zipPath, kept);
        }
        var report = "";
        State.Unpacking? pictures = null;
        void Write(Db con)
        {
            pictures = state.PreparePictures(con, read);
            report = Rows(con, model, read.Report, finalId, handle, exists, sha, backup, pictures.Report);
            within?.Invoke(con);
            if (finalId != current) Adopt(con, current, finalId);
            Friends(con, finalId, model.Extras);
        }
        try
        {
            if (!exists)
                store.CreateAccount(current, handle, account, null, Write);
            else
            {
                using var con = store.Open(current);
                con.Begin();
                con.Exec("UPDATE accounts SET handle = ?, account = ? WHERE id = ?", handle, Py.Dumps(account), current);
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
        var before = Path.Combine(dataDir, "restores", current.ToString());
        if (finalId != current && Directory.Exists(before))  // zips kept for review under the old id follow the account
        {
            foreach (var file in Directory.GetFiles(before))
                File.Move(file, Path.Combine(Path.GetDirectoryName(kept)!, Path.GetFileName(file)), overwrite: true);
            Directory.Delete(before);
        }
        if (finalId != current)  // the photos taken here follow the account; a name clash keeps the account's own photo
            state.MovePhotos(current, finalId);
        state.Unpack(finalId, kept, pictures!);  // last: the files follow the committed rows, and a failure here (the disk) leaves the account restored, its hunterscore updated and its zips under its id; a restore again replaces them
        return new(finalId, !exists, sha, report);
    }

    /// <summary>Give the account (and its rows, tokens, sessions and claimed user ids) another user id, inside the caller's
    /// transaction: the foreign keys are checked at its end, when every row has moved.</summary>
    static void Adopt(Db con, long from, long to)
    {
        con.Script("PRAGMA defer_foreign_keys = ON");
        foreach (var table in Store.PlayerTables)
            con.Exec($"UPDATE {table} SET account_id = ? WHERE account_id = $a", to);
        foreach (var table in new[] { "tokens", "web_sessions", "restored_ids" })
            con.Exec($"UPDATE {table} SET account_id = ? WHERE account_id = ?", to, from);
        con.Exec("UPDATE accounts SET id = ? WHERE id = ?", to, from);
        foreach (var column in new[] { "account_id", "other_id" })  // the friend edges (no foreign keys); one the new id has already stays
            con.Exec($"UPDATE OR IGNORE friends SET {column} = ? WHERE {column} = ?", to, from);
        con.Exec("DELETE FROM friends WHERE account_id = ?1 OR other_id = ?1", from);
    }

    /// <summary>The friend edges of the restored `User_friendslist` and `Me_friends` replies: a friend, an incoming request (`requests`),
    /// an outgoing one (`pending`). Each counts once the other player has an account here (<see cref="Store.FriendsOf"/>).</summary>
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
    /// profile's fields and the importer's defaults. No e-mail, Facebook id or Steam id of the zip.</summary>
    static JsonObject AccountObject(JsonObject baseAccount, PlayerModel m, long id, string handle)
    {
        var a = (JsonObject)baseAccount.DeepClone();
        var p = m.Profile;
        a["handle"] = handle;
        a["user_id"] = id;
        a["account_id"] = id;
        a["membership"] = p.MembershipInt;
        a["gender"] = p.Gender;
        a["hunterscore"] = p.Hunterscore;
        a["face"] = m.Account.Face;
        foreach (var key in new[] { "avatar", "avatar_id" })  // the account's own copies of the chosen avatar
            if (a.ContainsKey(key)) a[key] = m.Account.Face;
        a["facebook_enabled"] = m.Account.FacebookEnabled;
        a["share_on_facebook"] = m.Account.ShareOnFacebook != 0;
        Put(a, "hostname", p.Hostname);
        Put(a, "avatar_url", p.AvatarUrl);
        return a;
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

    /// <summary>Replace the account's profile rows with the model's (inside the caller's transaction) and add the audit row;
    /// returns the report.</summary>
    static string Rows(Db con, PlayerModel m, Report layerA, long id, string handle, bool replaced, string sha, string? backup, string pictures)
    {
        PlayerRows.ClearModel(con);
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
        Put(me, "avatar", p.AvatarUrl);
        Put(me, "membershipType", p.MembershipType);
        Put(me, "uuid", p.Uuid);
        Put(me, "locale", p.Locale);
        Put(me, "timezone", p.Timezone);
        Put(me, "country", p.Country);
        Put(me, "memberSince", p.MemberSince);
        Put(me, "rank", p.Rank);
        me["user_group"] ??= p.UserGroup;
        personal += Strip(me);
        PlayerRows.SetProfile(con, me);

        var wallet = m.Wallet ?? new Wallet(0, 0);
        var gm = Math.Max(wallet.Gm, GmFloor);  // every restore leaves the player enough gm$ to start again
        PlayerRows.SetWallet(con, "em", wallet.Em);
        PlayerRows.SetWallet(con, "gm", gm);
        if (m.Wallet is null) dropped.Add($"not in the zip: wallet ({wallet.Em} em, {gm} gm stored)");
        else if (gm > wallet.Gm) lines.Add($"wallet: gm raised from {wallet.Gm} to {gm}");

        foreach (var i in m.Items)
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

        var skills = 0;
        void Skill(string kind, long key, double number)
        {
            PlayerRows.SetSkill(con, kind, key, Skills.Xp(number));
            skills++;
        }
        if (m.Skills is { } s)
        {
            foreach (var (item, number) in s.Weapon) Skill("weapon", item, number);
            for (var k = 0; k < s.Tracking.Length; k++) Skill("tracking", k, s.Tracking[k]);
            for (var k = 0; k < s.Spotting.Length; k++) Skill("spotting", k, s.Spotting[k]);
        }
        else
        {
            for (var k = 0; k < 59; k++)
            {
                Skill("tracking", k, 1);
                Skill("spotting", k, 1);
            }
            dropped.Add("not in the zip: skills (a new player's levels stored)");
        }

        if (m.Stats is { } st)
            PlayerRows.SetStatistics(con, st.UserStats, st.Distance, st.Duration, st.BustThroughScore, st.LaneBanditScore);
        foreach (var a in m.Achievements)
            PlayerRows.AddAchievement(con, a.Id, a.CategoryId, a.GroupId, a.Value, a.Triggers);
        foreach (var t in m.Trophies)
        {
            personal += Strip(t.Node);
            PlayerRows.AddTrophy(con, t.Source, t.Id, t.Node);
        }
        foreach (var l in m.Lodges)
        {
            personal += Strip(l.Node);
            PlayerRows.AddLodge(con, l.Source, l.Id, l.Node);
        }
        foreach (var (path, body) in m.Extras)
        {
            personal += Strip(body);
            PlayerRows.SetExtra(con, path, body);
        }
        State.EnsureMissions(con);  // the saved mission states become the account's rows
        con.Exec(Store.RestoredChallenges);  // the saved completed challenges too
        var loadouts = con.Exec(Store.RestoredLoadouts);  // the saved loadouts, now under this server's ids

        lines.Add($"stored: items {m.Items.Count}, wallet 2, skills {skills}, profile 1, statistics {(m.Stats is null ? 0 : 1)}, achievements {m.Achievements.Count}, "
                  + $"trophies {m.Trophies.Count}, lodges {m.Lodges.Count}, extras {m.Extras.Count}, loadouts {loadouts}");
        lines.Add(pictures);
        if (personal > 0) dropped.Add($"{personal} personal field(s) (e-mail, real name, address, phone) in the saved replies");
        dropped.Add("e-mail, Facebook id and the other login-only fields of the account: this server's accounts keep none");
        if (m.Identity.SteamId64 is not null) dropped.Add("the zip's Steam id: a claim, not proof; the account's Steam id is only the one the server verified");
        dropped.Add("the zip's claim hash: a per-PC key, not proof of the account");

        var text = new StringBuilder(layerA.ToText());
        text.AppendLine($"account: {(replaced ? "profile replaced" : "created")}");
        if (backup is not null) text.AppendLine($"old database copied to {Path.GetFileName(backup)}");
        foreach (var line in lines) text.AppendLine(line);
        text.AppendLine($"dropped: {dropped.Count}");
        foreach (var d in dropped) text.AppendLine("  " + d);
        var report = text.ToString();
        PlayerRows.AddAudit(con, sha, layerA.Mode, report);
        return report;
    }
}
