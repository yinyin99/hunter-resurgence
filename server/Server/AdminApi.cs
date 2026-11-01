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

        admin.MapGet("/status", () => TypedResults.Ok(new Status(
                state is null || workDir is null ? null : Path.Combine(workDir, "state.db"), harCount,
                game.Table.Entries.Keys.Count(k => k.Params is null),
                game.Cfg["install_dir"] is JsonValue dir ? ArcList.ArchiveIndex((string)dir!).Count : 0,
                log.Misses().Length)))
             .WithName("GetStatus").WithSummary("What the server serves from");

        admin.MapGet("/wallet", Results<Ok<Dictionary<string, double>>, ProblemHttpResult> () => state is null
                ? NoState()
                : TypedResults.Ok(state.Wallet(state.LocalAccount).ToDictionary(kv => kv.Key, kv => Py.D(kv.Value))))
             .WithName("GetWallet").WithSummary("Balance per currency (em, gm)").ProducesProblem(503);

        admin.MapGet("/inventory", Results<Ok<JsonArray>, ProblemHttpResult> () => state is null
                ? NoState()
                : TypedResults.Ok(state.Inventory(state.LocalAccount)))
             .WithName("GetInventory").WithSummary("Owned items: item_id, slot (0 = stash), guid, metadata, units").ProducesProblem(503);

        admin.MapGet("/items/{itemId:long}", Results<Ok<JsonObject>, ProblemHttpResult> (long itemId) =>
                state is null ? NoState()
                : state.ItemInfo(itemId) is { } info ? TypedResults.Ok(info)
                : TypedResults.Problem($"item {itemId} has no definition", statusCode: 404, title: "Unknown item"))
             .WithName("GetItem").WithSummary("Item definition as heartbeat update 10 sends it").ProducesProblem(404).ProducesProblem(503);

        admin.MapPost("/grants/items", Results<Ok<ItemGrantResult>, ValidationProblem, ProblemHttpResult> (ItemGrant grant) =>
            {
                var errors = new Dictionary<string, string[]>();
                if (grant.ItemId is null) errors["itemId"] = ["required"];
                if (grant.Count is < 1 or > MaxStacks) errors["count"] = [$"1 to {MaxStacks}"];
                if (errors.Count > 0)
                    return TypedResults.ValidationProblem(errors);
                if (state is null)
                    return NoState();
                var itemId = grant.ItemId!.Value;
                if (!state.GrantItems(state.LocalAccount, itemId, grant.Count))
                    return TypedResults.Problem($"item {itemId} has no definition", statusCode: 404, title: "Unknown item");
                return TypedResults.Ok(new ItemGrantResult(itemId, grant.Count, Py.Str(state.ItemInfo(itemId)!["name"])));
            })
             .WithName("GrantItems").WithSummary("Give the player items")
             .WithDescription("Full stacks in slot 0. A running hunt gets them with its next heartbeat (update 10), else the "
                              + "next connect. Not idempotent: every call grants again.")
             .ProducesProblem(404).ProducesProblem(503);

        admin.MapPost("/grants/currency", Results<Ok<Balance>, ValidationProblem, ProblemHttpResult> (CurrencyGrant grant) =>
            {
                var errors = new Dictionary<string, string[]>();
                if (grant.Currency is not ("em" or "gm")) errors["currency"] = ["em or gm"];
                if (grant.Amount is null) errors["amount"] = ["required"];
                else if (!ValidAmount(grant.Amount.Value)) errors["amount"] = [$"a number from -{MaxAmount} to {MaxAmount}"];
                if (errors.Count > 0)
                    return TypedResults.ValidationProblem(errors);
                if (state is null)
                    return NoState();
                return state.Give(state.LocalAccount, grant.Currency!, grant.Amount!.Value) is { } balance
                    ? TypedResults.Ok(new Balance(grant.Currency!, Py.D(balance)))
                    : TypedResults.Problem($"{grant.Currency} unchanged: the balance would drop below 0", statusCode: 409, title: "Insufficient balance");
            })
             .WithName("GrantCurrency").WithSummary("Add to (or, negative, take from) a balance")
             .WithDescription("Sent as update 15 with the next heartbeat, else carried by the next connect. Not idempotent.")
             .ProducesProblem(409).ProducesProblem(503);

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

        admin.MapPost("/spawn/wake", Results<Ok, ValidationProblem, ProblemHttpResult> (SpawnWake wake) =>
            {
                var errors = new Dictionary<string, string[]>();
                if (wake.PopulationId is null or < 0) errors["populationId"] = ["required, from GET /admin/spawn"];
                if (wake.Group is null or < 1) errors["group"] = ["required, 1 or more (record 0 never sleeps)"];
                if (errors.Count > 0)
                    return TypedResults.ValidationProblem(errors);
                if (state is null)
                    return NoState();
                state.WakeGroup(state.LocalAccount, wake.PopulationId!.Value, wake.Group!.Value);
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
    }

    public const int MaxStacks = 1000;  // one transaction per grant
    const long MaxAmount = 1L << 53;  // larger doubles skip whole numbers; NaN or Infinity would reach the client's JSON

    public static bool ValidAmount(double amount) => Math.Abs(amount) <= MaxAmount;

    static ProblemHttpResult NoState() =>
        TypedResults.Problem("no recorded connect to seed the player state from", statusCode: 503, title: "Player state off");

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
    public sealed record SpawnWake(long? PopulationId, int? Group);
}
