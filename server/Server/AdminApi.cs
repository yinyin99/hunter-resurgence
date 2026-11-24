// Server admin API, on localhost only (Swagger UI: http://localhost/swagger): the player's wallet and inventory,
// grants of items and currency, item definitions and the latest misses. Errors are RFC 7807 problem details.
using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Resurgence.Server;

public static class AdminApi
{
    public static void MapAdmin(this WebApplication app, Replay.Game game, int harCount, string? workDir, string[] hosts)
    {
        var admin = app.MapGroup("/admin").RequireHost(hosts).WithGroupName("admin").WithTags("Admin");
        var state = game.State;
        var log = app.Services.GetRequiredService<RequestLog>();
        var reports = app.Services.GetService<ProblemReports>();

        admin.MapGet("/status", () => TypedResults.Ok(new Status(
                state is null || workDir is null ? null : Path.Combine(workDir, "state.db"), harCount,
                game.Table.Entries.Keys.Count(k => k.Params is null),
                game.Cfg["install_dir"] is JsonValue dir ? ArcList.ArchiveIndex((string)dir!).Count : 0,
                log.Misses().Length)))
             .WithName("GetStatus").WithSummary("What the server serves from");

        admin.MapGet("/wallet", Results<Ok<Dictionary<string, double>>, ProblemHttpResult> ([Description(AccountDoc)] string? account) =>
                state is null ? NoState() : ResolveAccount(state, account) is not { } id ? NoAccount(account!)
                : TypedResults.Ok(state.Wallet(id).ToDictionary(kv => kv.Key, kv => Py.D(kv.Value))))
             .WithName("GetWallet").WithSummary("Balance per currency (em, gm)").ProducesProblem(503);

        admin.MapGet("/inventory", Results<Ok<JsonArray>, ProblemHttpResult> ([Description(AccountDoc)] string? account) =>
                state is null ? NoState() : ResolveAccount(state, account) is not { } id ? NoAccount(account!)
                : TypedResults.Ok(state.Inventory(id)))
             .WithName("GetInventory").WithSummary("Owned items: item_id, slot (0 = stash), guid, metadata, units").ProducesProblem(503);

        admin.MapGet("/items/{itemId:long}", Results<Ok<JsonObject>, ProblemHttpResult> (long itemId) =>
                state is null ? NoState()
                : state.ItemInfo(itemId) is { } info ? TypedResults.Ok(info)
                : TypedResults.Problem($"item {itemId} has no definition", statusCode: 404, title: "Unknown item"))
             .WithName("GetItem").WithSummary("Item definition as heartbeat update 10 sends it").ProducesProblem(404).ProducesProblem(503);

        admin.MapPost("/grants/items", Results<Ok<ItemGrantResult>, ValidationProblem, ProblemHttpResult> (ItemGrant grant, [Description(AccountDoc)] string? account) =>
            {
                var errors = new Dictionary<string, string[]>();
                if (grant.ItemId is null) errors["itemId"] = ["required"];
                if (grant.Count is < 1 or > MaxStacks) errors["count"] = [$"1 to {MaxStacks}"];
                if (errors.Count > 0)
                    return TypedResults.ValidationProblem(errors);
                if (state is null)
                    return NoState();
                if (ResolveAccount(state, account) is not { } id)
                    return NoAccount(account!);
                var itemId = grant.ItemId!.Value;
                if (!state.GrantItems(id, itemId, grant.Count))
                    return TypedResults.Problem($"item {itemId} has no definition", statusCode: 404, title: "Unknown item");
                return TypedResults.Ok(new ItemGrantResult(itemId, grant.Count, Py.Str(state.ItemInfo(itemId)!["name"])));
            })
             .WithName("GrantItems").WithSummary("Give the player items")
             .WithDescription("Full stacks in slot 0. A running hunt gets them with its next heartbeat (update 10), else the "
                              + "next connect. Not idempotent: every call grants again.")
             .ProducesProblem(404).ProducesProblem(503);

        admin.MapPost("/grants/currency", Results<Ok<Balance>, ValidationProblem, ProblemHttpResult> (CurrencyGrant grant, [Description(AccountDoc)] string? account) =>
            {
                var errors = new Dictionary<string, string[]>();
                if (grant.Currency is not ("em" or "gm")) errors["currency"] = ["em or gm"];
                if (grant.Amount is null) errors["amount"] = ["required"];
                else if (!ValidAmount(grant.Amount.Value)) errors["amount"] = [$"a number from -{MaxAmount} to {MaxAmount}"];
                if (errors.Count > 0)
                    return TypedResults.ValidationProblem(errors);
                if (state is null)
                    return NoState();
                if (ResolveAccount(state, account) is not { } id)
                    return NoAccount(account!);
                return state.Give(id, grant.Currency!, grant.Amount!.Value) is { } balance
                    ? TypedResults.Ok(new Balance(grant.Currency!, Py.D(balance)))
                    : TypedResults.Problem($"{grant.Currency} unchanged: the balance would drop below 0", statusCode: 409, title: "Insufficient balance");
            })
             .WithName("GrantCurrency").WithSummary("Add to (or, negative, take from) a balance")
             .WithDescription("Sent as update 15 with the next heartbeat, else carried by the next connect. Not idempotent.")
             .ProducesProblem(409).ProducesProblem(503);

        admin.MapPost("/rescore", Results<Ok<RescoreResult[]>, ProblemHttpResult> ([Description("User id or handle; omitted: every account")] string? account,
                                                                               [Description("true (default): report only, write nothing")] bool? dry_run,
                                                                               [Description("Kill id of that account: that harvest alone")] long? kill) =>
            {
                if (state is null)
                    return NoState();
                if (kill is not null && string.IsNullOrWhiteSpace(account))
                    return TypedResults.Problem("kill ids are per account: give the account too", statusCode: 400, title: "Account missing");
                long? only = null;
                if (!string.IsNullOrWhiteSpace(account))
                {
                    if (ResolveAccount(state, account) is not { } id)
                        return NoAccount(account);
                    only = id;
                }
                return TypedResults.Ok(state.Rescore(only, dry_run ?? true, kill).Select(r => new RescoreResult(r.Account, r.Rescored, r.Paid, r.Gms, r.Harvests)).ToArray());
            })
             .WithName("Rescore").WithSummary("Score stored harvests again and pay the gm$ they missed")
             .WithDescription("Each stored harvest is scored with today's rules. A changed score updates the harvest page; round(css) gm$ is paid "
                              + "once per kill (never when a `CSS Reward (Animal Id: <kill>)` wallet row exists) and a reward already paid is not taken back. "
                              + "A harvest that now scores nothing gets 0. Disqualified harvests stay as they are. Repeating pays nothing more. "
                              + "`harvests` lists each changed harvest with its old and new score and the gm$ its kill was paid. dry_run (default true) reports without writing; a real run drops the kept boards. kill (with account) rescores that harvest alone.")
             .ProducesProblem(400).ProducesProblem(404).ProducesProblem(503);

        admin.MapPost("/season/start", Results<Ok<Store.Season>, ProblemHttpResult> () =>
                state is null ? NoState()
                : state.StartSeason((long)state.Clock()) is { } season ? TypedResults.Ok(season)
                : TypedResults.Problem("the current season started this second or later", statusCode: 409, title: "Season too young"))
             .WithName("StartSeason").WithSummary("Close the current leaderboard season and start the next")
             .WithDescription("The current season ends one second before now and the next number starts now (`{no, start, end}` of the new one, `end` null); harvests made from now on rank in it. Proposed rhythm: the first day of March, June, September and December. "
                              + "The Hall of Fame of the ended season is then available. 409 when the current season started this second or later.")
             .ProducesProblem(409).ProducesProblem(503);

        admin.MapGet("/taxidermies", Results<Ok<JsonArray>, ProblemHttpResult> ([Description(AccountDoc)] string? account) =>
                state is null ? NoState() : ResolveAccount(state, account) is not { } id ? NoAccount(account!) : TypedResults.Ok(state.Taxidermies(id)))
             .WithName("GetTaxidermies").WithSummary("The account's taxidermized harvests, newest first")
             .WithDescription("`[{kill, species, harvested, done: taxidermy|skull, currency: em|gm, paid, ts}]`; `currency`, `paid` and `ts` (Unix seconds) are null for a harvest the game marked taxidermized before the price existed.")
             .ProducesProblem(404).ProducesProblem(503);

        admin.MapPost("/taxidermies/{kill:long}/undo", Results<Ok<TaxidermyUndone>, ProblemHttpResult> (long kill, [Description(AccountDoc)] string? account, [Description("true (default): give the price back")] bool? refund) =>
            {
                if (state is null)
                    return NoState();
                if (ResolveAccount(state, account) is not { } id)
                    return NoAccount(account!);
                return state.UndoTaxidermy(id, kill, refund ?? true) is { } undone
                    ? TypedResults.Ok(new TaxidermyUndone(kill, undone.Currency, undone.Amount))
                    : TypedResults.Problem($"harvest {kill} is not taxidermized", statusCode: 404, title: "Not taxidermized");
            })
             .WithName("UndoTaxidermy").WithSummary("Take a taxidermy back")
             .WithDescription("The harvest is no longer taxidermized (nor a skull), so it leaves the lodge editor's trophy list and can be bought again within its window; the price is given back with a wallet row "
                              + "(`refund` false keeps it). Nothing happens to a lodge slot that held it.")
             .ProducesProblem(404).ProducesProblem(503);

        admin.MapPost("/spawn", Results<Ok<Spawner.Entry>, ValidationProblem, ProblemHttpResult> (Spawner.Request req) =>
            {
                var errors = Spawner.Validate(req);
                if (errors.Count > 0)
                    return TypedResults.ValidationProblem(errors);
                return state is null ? NoState() : TypedResults.Ok(state.Spawns.Add(req));
            })
             .WithName("AddSpawn").WithSummary("Add animals to the next hunt")
             .WithDescription("A group of one species appended to the `populations` row of that reserve (a new row when the reserve "
                              + "has none) in the next connect reply, so it needs a new hunt. x/z default to the expedition start. "
                              + "minWeight/maxWeight (integer kg, both > 0) fix weight = trophy value. dormant: the group exists "
                              + "but sleeps until /admin/spawn/wake. Stored in the spawn file.")
             .ProducesProblem(503);

        admin.MapGet("/spawn", Results<Ok<SpawnList>, ProblemHttpResult> () => state is null
                ? NoState()
                : TypedResults.Ok(new SpawnList(state.Spawns.List(), state.Spawns.LastApplied)))
             .WithName("GetSpawns").WithSummary("Spawn entries and where the last connect put them (population id, group index)")
             .ProducesProblem(503);

        admin.MapDelete("/spawn", Results<Ok<SpawnRemoved>, ProblemHttpResult> ([Description("Entry id; omitted: all")] int? id) =>
                state is null ? NoState() : TypedResults.Ok(new SpawnRemoved(state.Spawns.Remove(id))))
             .WithName("DeleteSpawns").WithSummary("Remove one spawn entry or all").ProducesProblem(503);

        admin.MapPost("/spawn/wake", Results<Ok, ValidationProblem, ProblemHttpResult> (SpawnWake wake, [Description(AccountDoc)] string? account) =>
            {
                var errors = new Dictionary<string, string[]>();
                if (wake.PopulationId is null or < 0) errors["populationId"] = ["required, from GET /admin/spawn"];
                if (wake.Group is null or < 1) errors["group"] = ["required, 1 or more (record 0 never sleeps)"];
                if (errors.Count > 0)
                    return TypedResults.ValidationProblem(errors);
                if (state is null)
                    return NoState();
                if (ResolveAccount(state, account) is not { } id)
                    return NoAccount(account!);
                state.WakeGroup(id, wake.PopulationId!.Value, wake.Group!.Value);
                return TypedResults.Ok();
            })
             .WithName("WakeSpawn").WithSummary("Wake a dormant group in the running hunt")
             .WithDescription("Heartbeat update 5 with the next heartbeat: the client activates the group once. It fixes when, "
                              + "not where; a group that is already active ignores it.")
             .ProducesProblem(503);

        admin.MapGet("/misses", Ok<JsonObject[]> ([Description("How many, newest first (1–1000, default 50)")] int? limit) =>
                TypedResults.Ok(log.Misses().Reverse().Take(Math.Clamp(limit ?? 50, 1, 1000))
                    .Select(l => JsonNode.Parse(l)!.AsObject()).ToArray()))
             .WithName("GetMisses").WithSummary("Requests nothing could answer (the miss log: keys only)");

        admin.MapGet("/reports", Results<Ok<List<ProblemReports.Item>>, ProblemHttpResult> () =>
                reports is null ? NoReports() : TypedResults.Ok(reports.List()))
             .WithName("GetReports").WithSummary("The problem reports the launcher sent (kept 30 days), newest first")
             .WithDescription("Each `{reference, account, handle, ts, trigger, size}`. Download one with `GET /admin/reports/<reference>`.").ProducesProblem(503);

        admin.MapGet("/reports/{reference}", Results<FileContentHttpResult, ProblemHttpResult> (string reference) =>
                reports is null ? NoReports()
                : reports.PathOf(reference) is not { } zip ? TypedResults.Problem($"no report {reference}", statusCode: 404, title: "Unknown report")
                : TypedResults.File(File.ReadAllBytes(zip), "application/zip", reference + ".zip"))
             .WithName("GetReport").WithSummary("Download a problem report's zip by its reference (p<account>-<8 hex>)").ProducesProblem(404).ProducesProblem(503);

        admin.MapGet("/diagnostics", Results<Ok<Diagnostics>, ProblemHttpResult> ([Description(AccountDoc)] string? account) =>
                state is null ? NoState() : ResolveAccount(state, account) is not { } id ? NoAccount(account!)
                : TypedResults.Ok(Diagnostics.Of(state.Store, id, Store.Now())))
             .WithName("GetDiagnostics").WithSummary("The account's last 30 days: crash reports, hunts without a disconnect, hunts under 60 s, event 30 count")
             .WithDescription("`crashes`: the kept crash and error reports (time, kind, module, frames with the crashing one first, message); `noDisconnect`: hunts that ended without the game's disconnect call; "
                              + "`short`: closed hunts under 60 s (both newest first, at most 100); `event30`: how many times the game said the connection to the host failed.").ProducesProblem(404).ProducesProblem(503);

        admin.MapGet("/hunting", Results<Ok<JsonObject>, ProblemHttpResult> () => state is null ? NoState() : TypedResults.Ok(state.HuntingAdmin()))
             .WithName("GetHuntingStatistics").WithSummary("Every player's server playtime and hunting figures, with names")
             .WithDescription("`{since, players: [{account, handle, huntSecondsHere, reserves: [{reserve, gm, harvests, hours, gmPerHour, gmPerHarvest, harvestsPerHour}], species: [{species, reserve, harvests, gm}], otherGm: {mission, community, daily, dog}}]}`, "
                              + "most playtime first: the hunts since `playtime_since`, the gm$ of the harvests (`CSS Reward` wallet rows) on the hunts' reserves and, apart, the mission, community, daily and dog rewards since then. No 5-player floor. "
                              + "Computed on demand and kept an hour, like `GET /stats/hunting`.").ProducesProblem(503);
    }

    public const int MaxStacks = 1000;  // one transaction per grant
    const long MaxAmount = 1L << 53;  // larger doubles skip whole numbers; NaN or Infinity would reach the client's JSON

    public static bool ValidAmount(double amount) => Math.Abs(amount) <= MaxAmount;

    const string AccountDoc = "User id or handle of the account; omitted: the local account";

    /// <summary>The account an operator names (user id or handle); no name: the local account; null: no such account.</summary>
    public static long? ResolveAccount(State state, string? who) =>
        string.IsNullOrWhiteSpace(who) ? state.LocalAccount
        : long.TryParse(who, out var id) && state.Store.AccountExists(id) ? id
        : state.Store.AccountByHandle(who);

    /// <summary>The arguments after `admin rescore`: `[--account <id or handle>] [--kill <id>] [--apply]` in any order; null when they are not that
    /// (an unknown or repeated option, a missing or non-numeric value, `--kill` without `--account`: kill ids are per account).</summary>
    public static (string? Account, long? Kill, bool Apply)? ParseRescoreArgs(string[] a)
    {
        string? account = null;
        long? kill = null;
        var apply = false;
        for (var i = 0; i < a.Length; i++)
            if (a[i] == "--apply" && !apply)
                apply = true;
            else if (a[i] == "--account" && account is null && i + 1 < a.Length && !string.IsNullOrWhiteSpace(a[i + 1]))
                account = a[++i];
            else if (a[i] == "--kill" && kill is null && i + 1 < a.Length && long.TryParse(a[i + 1], out var k) && k > 0)
                (kill, i) = (k, i + 1);
            else
                return null;
        return kill is not null && account is null ? null : (account, kill, apply);
    }

    static ProblemHttpResult NoAccount(string who) =>
        TypedResults.Problem($"no account {who}", statusCode: 404, title: "Unknown account");

    static ProblemHttpResult NoState() =>
        TypedResults.Problem("no recorded connect to seed the player state from", statusCode: 503, title: "Player state off");

    static ProblemHttpResult NoReports() =>
        TypedResults.Problem("problem reports are kept by a community server (offline_mode false)", statusCode: 503, title: "Reports off");

    public sealed record Status(
        [property: Description("Player state database, null when off")] string? StateDb,
        [property: Description("Recordings loaded")] int Recordings,
        [property: Description("Distinct method/host/path replies")] int RecordedKeys,
        [property: Description("Install archive entries served")] int ArchiveEntries,
        [property: Description("Lines in the miss log")] int Misses);

    public sealed record ItemGrant(
        [property: Description("Catalogue item id")] long? ItemId,
        [property: Description("Number of full stacks, 1 to 1000")] int Count = 1);

    public sealed record ItemGrantResult(long ItemId, int Count, string Name);

    public sealed record CurrencyGrant(
        [property: Description("`em` or `gm`")] string? Currency,
        [property: Description("Amount to add; negative takes (at most 2^53 either way)")] double? Amount);

    public sealed record Balance(string Currency, double Amount);

    public sealed record SpawnList(List<Spawner.Entry> Entries, List<Spawner.Applied> Applied);
    public sealed record SpawnRemoved(int Removed);
    public sealed record RescoreResult(long Account, int Rescored, int Paid, long Gms, List<State.RescoredHarvest> Harvests);
    public sealed record TaxidermyUndone(long Kill, [property: Description("em or gm, null for a harvest marked before the price existed")] string? Currency, [property: Description("What was given back, 0 for none")] long Refunded);
    public sealed record SpawnWake(long? PopulationId, int? Group);
}
