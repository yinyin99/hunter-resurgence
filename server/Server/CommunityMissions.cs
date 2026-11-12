// The running seasonal event's community missions (facts `events.<name>.community`): objectives that every player who plays events counts
// toward together. A target is the facts' number divided by the `event_community_divisor` setting, rounded up; each contributor is paid once.
// Also the launcher's and the website's event panel (GET /account/events/panel). docs/server.md, "Seasonal events".
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed partial class State
{
    /// <summary>A community mission: its title is read like a mission objective's; <paramref name="Target"/> is the facts' number, before the divisor.</summary>
    sealed record CommunityDef(long Id, string Title, Rule Rule, long Target, long Gm, long Em);

    readonly List<CommunityDef> community = [];

    /// <summary>settings.json `event_community_divisor` (at least 1): a community mission needs ceil(target / divisor).</summary>
    public int CommunityDivisor { get; init; } = 20;

    long Needed(CommunityDef c) => (c.Target + CommunityDivisor - 1) / CommunityDivisor;

    /// <summary>The community missions of the configured event (none without an event or facts for it).</summary>
    void LoadCommunity(Func<string, Rule> ruleOf)
    {
        if (Seasonal is null || facts.EventOf(Seasonal.Name)?["community"] is not JsonArray list)
            return;
        foreach (var c in list.OfType<JsonObject>())
        {
            var title = Py.Str(G(c, "title", ""));
            community.Add(new(Py.L(c["id"]), title, ruleOf(title), Math.Max(1, (long)Num(c["target"])), (long)Num(c["gm"]), (long)Num(c["em"])));
        }
    }

    /// <summary>How many the players have counted together for this community mission of the event.</summary>
    static long Progress(Db con, string eventName, CommunityDef c) =>
        (long)con.Scalar("SELECT COALESCE(SUM(count), 0) FROM community_progress WHERE event = ? AND mission_id = ?", eventName, c.Id)!;

    /// <summary>A hunt's event (from <see cref="MissionEvent"/>, inside the heartbeat's transaction) counts 1 for each community mission of the running event it meets
    /// that is not done yet, for a player who plays events only.</summary>
    void CommunityCount(Db con, Kind kind, Func<Rule, bool> meets)
    {
        if (community.Count == 0 || Seasonal is not { } e || !e.Running(Clock())
            || con.Scalar("SELECT seasonal_events FROM accounts WHERE id = ?", con.Account) is 0L)
            return;
        foreach (var c in community.Where(c => c.Rule.Kind == kind && meets(c.Rule)).ToList())
            if (Progress(con, e.Name, c) < Needed(c))
                con.Exec("INSERT INTO community_progress (event, mission_id, account_id, count) VALUES (?, ?, ?, 1)"
                         + " ON CONFLICT (event, mission_id, account_id) DO UPDATE SET count = count + 1", e.Name, c.Id, con.Account);
    }

    /// <summary>The player's reward of each done community mission they counted for and were not paid yet (a heartbeat, after its events): gm$ and em$ through
    /// <see cref="AddCurrency"/>, update 15 into `updates`, a toast. A hunt changes only its own player's wallet, so every contributor is paid at their own next heartbeat.</summary>
    void CommunityPay(Db con, JsonArray updates)
    {
        if (community.Count == 0 || Seasonal is not { } e)
            return;
        con.Begin();
        foreach (var c in community.Where(c => Progress(con, e.Name, c) >= Needed(c)).ToList())
        {
            if (con.Exec("UPDATE community_progress SET paid = 1 WHERE event = ? AND mission_id = ? AND account_id = ? AND paid = 0 AND count > 0", e.Name, c.Id, con.Account) == 0)
                continue;
            foreach (var (currency, amount) in new[] { ("gm", c.Gm), ("em", c.Em) })
                if (amount > 0 && AddCurrency(con, currency, amount, $"{currency}$ community reward") is { } balance)
                    if (updates.OfType<JsonObject>().LastOrDefault(x => Is(x["update"], 15) && Py.Str(x["type"]) == currency) is { } same)
                        same["amount"] = balance.DeepClone();
                    else
                        updates.Add(new JsonObject { ["amount"] = balance, ["type"] = currency, ["update"] = 15 });
            Toast(con, $"Community mission \"{c.Title}\" completed.");
        }
        con.Commit();
    }

    /// <summary>GET /account/events/panel: the running event for a player who plays events (null otherwise): `name`, `title`, `start` and `end` (unix seconds),
    /// `community` (id, title, progress, the divided target, gm, em), the top 10 `contributors` (handle, count), the player's personal event `missions`
    /// (id, title, state `active`, `available`, `completed` or `locked`, objectives with `done`, reward text) and the event's store `items` (id, name).</summary>
    public JsonObject? EventPanel(long account)
    {
        if (Seasonal is not { } e || !e.Running(Clock()) || !Store.PlaysEvents(account))
            return null;
        var content = facts.EventOf(e.Name);
        using var con = Con(account);
        con.Begin();
        EnsureMissions(con);
        var rows = MissionRows(con);
        var shared = community.Select(c => new JsonObject
        {
            ["id"] = c.Id, ["title"] = c.Title, ["progress"] = Math.Min(Progress(con, e.Name, c), Needed(c)), ["target"] = Needed(c), ["gm"] = c.Gm, ["em"] = c.Em,
        }).ToList();
        var contributors = con.Rows("SELECT a.handle, SUM(p.count) AS n FROM community_progress p JOIN accounts a ON a.id = p.account_id"
                                    + " WHERE p.event = ? GROUP BY p.account_id ORDER BY n DESC, a.handle LIMIT 10", e.Name)
            .Select(r => new JsonObject { ["handle"] = (string)r[0], ["count"] = (long)r[1] }).ToList();
        con.Commit();
        var available = Available(rows).Select(m => m.Id).ToHashSet();
        var defs = Arr(G(application, "missions", null)).OfType<JsonObject>().Where(d => TryKey(d["id"], out _)).ToDictionary(d => Py.L(d["id"]));
        var personal = Ids(content?["missions"]).Where(missions.ContainsKey).Select(id =>
        {
            var done = rows.TryGetValue(id, out var r) ? r : (State: 0L, Done: []);
            return new JsonObject
            {
                ["id"] = id, ["title"] = missions[id].Title,
                ["state"] = done.State == 3 ? "completed" : done.State == 2 ? "active" : available.Contains(id) ? "available" : "locked",
                ["objectives"] = new JsonArray([.. missions[id].Objectives.Select(o => (JsonNode?)new JsonObject { ["title"] = o.Title, ["done"] = done.State == 3 || done.Done.Contains(o.Id) })]),
                ["reward"] = string.Join(", ", Arr(defs.GetValueOrDefault(id)?["rewards"]).OfType<JsonObject>().Select(x => Py.Str(G(x, "text", "")))),
            };
        }).ToList();
        var storeItems = Ids(content?["items"]).Where(items.ContainsKey).Select(id => new JsonObject { ["id"] = id, ["name"] = Py.Str(G(items[id], "name", "")).Split(" (")[0] }).ToList();
        return new JsonObject
        {
            ["name"] = e.Name, ["title"] = content?["title"] is JsonValue t && t.TryGetValue<string>(out var title) ? title : e.Name,
            ["start"] = e.Start.ToUnixTimeSeconds(), ["end"] = e.End.ToUnixTimeSeconds(),
            ["community"] = new JsonArray([.. shared]), ["contributors"] = new JsonArray([.. contributors]),
            ["missions"] = new JsonArray([.. personal]), ["items"] = new JsonArray([.. storeItems]),
        };
    }
}
