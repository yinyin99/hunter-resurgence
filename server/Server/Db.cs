// One sqlite connection, scoped to one account. `$a` in SQL is the account id; SQL that touches a per-player table
// without it is refused, so a missed filter fails in the tests instead of leaking another player's rows.
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Resurgence.Server;

/// <summary>One sqlite connection (from the pool) plus the transaction of a Python `with con:` block (Begin … Commit;
/// Dispose rolls back). Plain `?` placeholders bind in order like sqlite3's; `?N` ones by number; `$a` is the account.
/// Begin is BEGIN IMMEDIATE: every transaction here may write, and a read that upgrades later can fail with SQLITE_BUSY.</summary>
public sealed class Db : IDisposable
{
    // each reference to a per-player table (FROM, JOIN, INTO, UPDATE, comma join) needs its own `$a` in the statement
    // ponytail: a text check, not a parser; an `$a` that is bound but not used as a filter is not caught
    static readonly Regex PlayerTable = new(
        @"(?:\b(?:FROM|JOIN|INTO|UPDATE)\s+|,\s*)(?:" + string.Join('|', Store.PlayerTables) + @")\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex AccountParam = new(@"\$a\b", RegexOptions.Compiled);

    readonly SqliteConnection con;
    readonly long? account;
    bool inTx;

    /// <summary>The account this connection serves, null for the shared tables only (accounts, tokens).</summary>
    public long? Account => account;

    internal Db(string connectionString, long? account)
    {
        this.account = account;
        con = new SqliteConnection(connectionString);
        con.Open();
        try
        {
            // per connection, unlike journal_mode=WAL, which is set once with the schema
            Script("PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON");
        }
        catch
        {
            con.Dispose();
            throw;
        }
    }

    /// <summary>Run statements as written, no account check: the schema and the pragmas.</summary>
    internal void Script(string sql)
    {
        using var c = con.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    public void Begin()
    {
        Script("BEGIN IMMEDIATE");
        inTx = true;
    }

    public void Commit()
    {
        Script("COMMIT");
        inTx = false;
    }

    SqliteCommand Cmd(string sql, object?[] args)
    {
        var tables = PlayerTable.Count(sql);
        var bound = AccountParam.Count(sql);
        if (tables > bound)
            throw new InvalidOperationException($"SQL touches a per-player table {tables}x but binds $a {bound}x: {sql}");
        if (bound > 0 && account is null)
            throw new InvalidOperationException($"SQL uses $a but this connection has no account: {sql}");
        var cmd = con.CreateCommand();
        var sb = new StringBuilder(sql.Length + 8);
        var n = 0;
        for (var i = 0; i < sql.Length; i++)
        {
            sb.Append(sql[i]);
            if (sql[i] == '?' && (i + 1 == sql.Length || !char.IsAsciiDigit(sql[i + 1]))) sb.Append(++n);
        }
        // `$a` becomes the parameter after the caller's: a named parameter would share the number of the first `?N`
        cmd.CommandText = AccountParam.Replace(sb.ToString(), $"?{args.Length + 1}");
        if (bound > 0)
            cmd.Parameters.AddWithValue($"?{args.Length + 1}", account);
        for (var i = 0; i < args.Length; i++)
            cmd.Parameters.AddWithValue($"?{i + 1}", args[i] switch
            {
                null => DBNull.Value,
                JsonNode j => Py.Sql(j),
                bool b => b ? 1L : 0L,
                var v => v,
            });
        return cmd;
    }

    public int Exec(string sql, params object?[] args)
    {
        using var c = Cmd(sql, args);
        return c.ExecuteNonQuery();
    }

    public List<object[]> Rows(string sql, params object?[] args)
    {
        using var c = Cmd(sql, args);
        using var r = c.ExecuteReader();
        var rows = new List<object[]>();
        while (r.Read())
        {
            var row = new object[r.FieldCount];
            r.GetValues(row);
            rows.Add(row);
        }
        return rows;
    }

    public object? Scalar(string sql, params object?[] args)
    {
        using var c = Cmd(sql, args);
        return c.ExecuteScalar();
    }

    public long Insert(string sql, params object?[] args)
    {
        Exec(sql, args);
        return (long)Scalar("SELECT last_insert_rowid()")!;
    }

    public void Dispose()
    {
        if (inTx)
            try { Script("ROLLBACK"); }
            catch (SqliteException) { }  // the connection is closed below either way
        con.Dispose();
    }
}
