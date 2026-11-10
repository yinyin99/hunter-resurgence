// The wallet history of an account: the pages a restored backup brought (`Wallet_transactions`, `Wallet_silver_transactions`, `Wallet_spent` in `extras`)
// and the balance changes made here (`wallet_log`, written by AddCurrency), in the rows `Wallet/transactions` and `Wallet/silver_transactions` have. Owner only.
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Resurgence.Server;

public sealed partial class State
{
    public const int WalletPageSize = 50;
    const string PAYMENT = "In-game payment.";

    /// <summary>One page (<see cref="WalletPageSize"/> rows from `offset`) of the history of a currency (`em`, `gm`): this server's changes and the restored rows, newest first.
    /// A row is `{id, created, type: {id: C|D, name}, amount (never negative), preBalance, postBalance, summary, mission, items: [definition], qty}`.
    /// ponytail: the restored pages are read whole for each page asked; keep them parsed if a wallet outgrows a few thousand rows.</summary>
    public JsonArray WalletPage(long account, string currency, long offset)
    {
        using var con = Con(account);
        var restored = con.Rows("SELECT path, body FROM extras WHERE account_id = $a AND path LIKE ? ESCAPE '!'",
                                currency == "gm" ? "Wallet!_silver!_transactions!_!_%" : "Wallet!_transactions!_!_%")
            .Select(r => (Offset: Regex.Match((string)r[0], @"__offset=(\d+)").Groups[1].Value, Body: (string)r[1]))
            .OrderBy(p => p.Offset.Length == 0 ? 0 : long.Parse(p.Offset))
            .SelectMany(p => JsonNode.Parse(p.Body) is JsonArray page ? page.OfType<JsonObject>() : []);
        var ours = con.Rows("SELECT n, ts, amount, after, summary, item_ids FROM wallet_log WHERE account_id = $a AND currency = ? ORDER BY n DESC", currency).Select(r =>
        {
            var (delta, after) = (Convert.ToDouble(r[2]), Convert.ToDouble(r[3]));
            var ids = JsonNode.Parse((string)r[5])!.AsArray().Select(Py.L).ToArray();
            return new JsonObject
            {
                ["id"] = (long)r[0], ["created"] = (long)r[1],
                ["type"] = delta < 0 ? new JsonObject { ["id"] = "D", ["name"] = "Debit" } : new JsonObject { ["id"] = "C", ["name"] = "Credit" },
                ["amount"] = Math.Abs(delta), ["preBalance"] = after - delta, ["postBalance"] = after, ["summary"] = (string)r[4], ["mission"] = "",
                ["items"] = new JsonArray([.. ids.Distinct().Select(i => items.TryGetValue(i, out var def) ? C(def) : new JsonObject { ["id"] = i })]),
                ["qty"] = ids.Length == 0 ? 1 : ids.Length / ids.Distinct().Count(),
            };
        });
        return new JsonArray([.. ours.Concat(restored).OrderByDescending(x => Py.IsNum(x["created"]) ? Py.D(x["created"]) : 0)  // stable: this server's first on a tie
            .Skip((int)Math.Clamp(offset, 0, int.MaxValue)).Take(WalletPageSize).Select(x => x.DeepClone())]);
    }

    /// <summary>`Wallet/spent` of the account: `{spent, silver_spent}`, the restored totals plus the payments made here.</summary>
    public JsonObject WalletSpent(long account)
    {
        using var con = Con(account);
        var saved = con.Scalar("SELECT body FROM extras WHERE account_id = $a AND path = 'Wallet_spent.json'") is string s ? JsonNode.Parse(s) : null;
        double Spent(string key, string currency) =>
            (Py.IsNum(saved?[key]) ? Py.D(saved![key]) : 0)
            + Convert.ToDouble(con.Scalar("SELECT COALESCE(-SUM(amount), 0) FROM wallet_log WHERE account_id = $a AND currency = ? AND amount < 0 AND summary = ?", currency, PAYMENT));
        return new JsonObject { ["spent"] = Spent("spent", "em"), ["silver_spent"] = Spent("silver_spent", "gm") };
    }

    /// <summary>`Wallet/transactions` (currency em) or `Wallet/silver_transactions` (gm) of the form's `offset`; null unless the request is the player's own.</summary>
    public byte[]? WalletListReply(long account, string currency, byte[] formBody)
    {
        if (!IsOwn(account, formBody))
            return null;
        long.TryParse(Form1(Py.ParseQs(Encoding.UTF8.GetString(formBody)), "offset"), out var offset);
        return Py.DumpBytes(WalletPage(account, currency, offset));
    }

    /// <summary>`Wallet/spent`; null unless the request is the player's own.</summary>
    public byte[]? WalletSpentReply(long account, byte[] formBody) => IsOwn(account, formBody) ? Py.DumpBytes(WalletSpent(account)) : null;
}
