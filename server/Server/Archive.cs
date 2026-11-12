// The operator's archive of public profiles (archive.db, made elsewhere and copied into the data folder; docs/server.md#archive-claims).
// Read only: the server never writes it and no route serves it. A missing file means the feature is off.
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Resurgence.Restore;

namespace Resurgence.Server;

/// <summary>An archived player and what a claim could restore: the counts are what <see cref="Importer.FromReplies"/> reads from the saved replies
/// (all zero and false when they cannot be read).</summary>
public sealed record ArchivedPlayer(long Uid, string Handle, long Hunterscore, int Items, int Trophies, int Lodges, bool Skills, bool Statistics, bool Achievements);

public sealed class Archive(string path)
{
    /// <summary>The archive file exists. Checked at every call, so a file copied in while the server runs is used at once.</summary>
    public bool Enabled => File.Exists(path);

    SqliteConnection Open()
    {
        var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        con.Open();
        return con;
    }

    /// <summary>The player whose handle is `name` in any case (exact, no part of a name); null when none, or when there is no archive.</summary>
    public ArchivedPlayer? Find(string name) => Enabled ? One("hostname = $k", name.ToLowerInvariant()) : null;

    /// <summary>The player with this user id (the operator's way in); null when none.</summary>
    public ArchivedPlayer? Find(long uid) => Enabled ? One("uid = $k", uid) : null;

    ArchivedPlayer? One(string where, object key)
    {
        long uid, score;
        string handle;
        using (var con = Open())
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = $"SELECT uid, handle, hunterscore FROM player WHERE {where}";
            cmd.Parameters.AddWithValue("$k", key);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            (uid, handle, score) = (r.GetInt64(0), r.GetString(1), r.GetInt64(2));
        }
        try
        {
            var m = Importer.FromReplies(uid.ToString(System.Globalization.CultureInfo.InvariantCulture), handle, Replies(uid)).Model;
            return new(uid, handle, score, m.Items.Count, m.Trophies.Count, m.Lodges.Count, m.Skills is not null, m.Stats is not null, m.Achievements.Count > 0);
        }
        catch (RestoreException)  // replies over a zip's caps: the player is listed, nothing of it can be restored
        {
            return new(uid, handle, score, 0, 0, 0, false, false, false);
        }
    }

    /// <summary>Every saved reply of the player, as a public-mode backup zip would hold them. A row whose params are not a JSON object of strings is skipped.</summary>
    public IReadOnlyList<ArchivedReply> Replies(long uid)
    {
        var rows = new List<ArchivedReply>();
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT path, endpoint, params, status, body FROM reply WHERE uid = $u";
        cmd.Parameters.AddWithValue("$u", uid);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            Dictionary<string, string>? ps;
            try { ps = JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(2)); }
            catch (JsonException) { continue; }
            if (ps is not null) rows.Add(new(r.GetString(0), r.GetString(1), ps, r.GetInt32(3), r.GetFieldValue<byte[]>(4)));
        }
        return rows;
    }
}
