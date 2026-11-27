// The store's sales (settings.json `store_sales`, docs/server.md#store-sales): a fixed list of items, or a random pick of items, at a percentage off, for a period, tied or not to an event.
// The price rule is State.Price; the recorded Application/discounts of the original is not read.
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Resurgence.Server;

/// <summary>`random` of a sale: <paramref name="Count"/> items of the buyable ones that match the filters, each at a percentage from <paramref name="Min"/> to <paramref name="Max"/> in steps of 5;
/// a new pick every <paramref name="Every"/> seconds (null: one for the whole run). Prices are whole em$ / gm$ of the catalogue; an empty <paramref name="Categories"/> matches every category.</summary>
public sealed record RandomSale(int Count, double Min, double Max, long? Every, string[] Categories, long? MinEm, long? MaxEm, long? MinGm, long? MaxGm, long[] Exclude);

/// <summary>One `store_sales` entry. Times are Unix seconds. A fixed sale has <paramref name="Items"/> and <paramref name="Bundles"/> (ids; either may be empty, not both) at <paramref name="Percent"/>, a random one <paramref name="Random"/> (items only).
/// <paramref name="Event"/>: the name of the seasonal event or harvest bonus it runs with (only while that runs, inside its own From/Until when given). <paramref name="Em"/> and
/// <paramref name="Gm"/>: which prices drop (a bundle has an em$ price only).</summary>
public sealed record StoreSale(string Name, long? From, long? Until, string? Event, bool Em, bool Gm, double Percent, long[]? Items, long[] Bundles, RandomSale? Random)
{
    /// <summary>A stretch of time with one pick: [Start, End).</summary>
    public readonly record struct Period(long Start, long End);

    static readonly Regex EveryRule = new(@"^(\d{1,6})([mhd])$", RegexOptions.CultureInvariant);

    /// <summary>The runs of the sale: its own period cut by each window of its event (`windows` gives them by name; none: the sale never runs).</summary>
    public IEnumerable<Period> Runs(Func<string, IEnumerable<(long Start, long End)>> windows)
    {
        foreach (var (start, end) in Event is null ? new[] { (long.MinValue, long.MaxValue) } : windows(Event))
        {
            Period run = new(Math.Max(From ?? long.MinValue, start), Math.Min(Until ?? long.MaxValue, end));
            if (run.Start < run.End) yield return run;
        }
    }

    /// <summary>The period with one pick that holds `t`: the run, or with `every` the slice of it that starts at a multiple of `every` after the run's start. Null: the sale does not run at `t`.</summary>
    public Period? At(long t, Func<string, IEnumerable<(long Start, long End)>> windows)
    {
        foreach (var run in Runs(windows))
            if (run.Start <= t && t < run.End)
            {
                if (Random?.Every is not { } every) return run;
                var start = run.Start + (t - run.Start) / every * every;
                return new(start, Math.Min(start + every, run.End));
            }
        return null;
    }

    /// <summary>The first period that starts after `t`: null for none.</summary>
    public Period? Next(long t, Func<string, IEnumerable<(long Start, long End)>> windows) =>
        (At(t, windows) is { } now ? new[] { now.End } : Array.Empty<long>()).Concat(Runs(windows).Select(r => r.Start))
            .Where(c => c > t && At(c, windows) is not null).Order().Select(c => At(c, windows)).FirstOrDefault();

    /// <summary>`store_sales` of settings.json: none for null; InvalidDataException says what is wrong.</summary>
    public static StoreSale[] Parse(JsonNode? node)
    {
        if (node is null) return [];
        var rows = node as JsonArray ?? throw new InvalidDataException("store_sales: a list of sales");
        var sales = new List<StoreSale>();
        for (var n = 0; n < rows.Count; n++)
        {
            var where = $"store_sales[{n}]";
            var o = rows[n] as JsonObject ?? throw new InvalidDataException($"{where}: an object");
            var name = Text(o, "name", where) ?? throw new InvalidDataException($"{where}.name: missing");
            if (sales.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException($"{where}.name: used by another sale");
            long? from = Time(o, "from", where), until = Time(o, "until", where);
            var @event = Text(o, "event", where);
            if (@event is null && (from is null || until is null)) throw new InvalidDataException($"{where}: needs from and until, or an event to take them from");
            if (from >= until) throw new InvalidDataException($"{where}.until: must be after from");
            bool em = true, gm = false;
            if (o["currencies"] is { } currencies)
            {
                var list = (currencies as JsonArray)?.Select(x => (x as JsonValue)?.TryGetValue<string>(out var text) == true ? text : null).ToArray();
                if (list is not { Length: > 0 } || list.Any(x => x is not ("em" or "gm"))) throw new InvalidDataException($"{where}.currencies: [\"em\"], [\"gm\"] or [\"em\", \"gm\"]");
                (em, gm) = (list.Contains("em"), list.Contains("gm"));
            }
            var fixedSale = o["items"] is not null || o["bundles"] is not null;
            if (fixedSale == (o["random"] is not null)) throw new InvalidDataException($"{where}: give items and/or bundles (a fixed sale) or random, not both or neither");
            if (fixedSale)
            {
                var percent = Pct(o["percent"], $"{where}.percent");
                long[] ids = o["items"] is null ? [] : Ids(o["items"], $"{where}.items"), bundles = o["bundles"] is null ? [] : Ids(o["bundles"], $"{where}.bundles");
                if (ids.Length + bundles.Length == 0) throw new InvalidDataException($"{where}: at least one item or bundle id");
                sales.Add(new(name, from, until, @event, em, gm, percent, ids, bundles, null));
                continue;
            }
            if (o["percent"] is not null) throw new InvalidDataException($"{where}.percent: a random sale has it inside random");
            var r = o["random"] as JsonObject ?? throw new InvalidDataException($"{where}.random: an object");
            var rw = $"{where}.random";
            double min, max;
            if (r["percent"] is JsonArray { Count: 2 } range) (min, max) = (Pct(range[0], $"{rw}.percent"), Pct(range[1], $"{rw}.percent"));
            else min = max = Pct(r["percent"], $"{rw}.percent");
            if (min > max || Math.Ceiling(min / 5) * 5 > max) throw new InvalidDataException($"{rw}.percent: a number, or [lowest, highest] with a multiple of 5 between them (percentages are given in steps of 5)");
            if (r["count"] is not JsonValue cv || !cv.TryGetValue<int>(out var count) || count is < 1 or > 1000) throw new InvalidDataException($"{rw}.count: a whole number from 1 to 1000");
            long? every = null;
            if (Text(r, "every", rw) is { } e)
                every = EveryRule.Match(e) is { Success: true } m && long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) is > 0 and var amount
                    ? amount * (m.Groups[2].Value switch { "m" => 60L, "h" => 3600L, _ => 86400L })
                    : throw new InvalidDataException($"{rw}.every: a whole number and m, h or d, such as \"24h\"");
            string[] categories = r["categories"] is null ? [] : r["categories"] is JsonArray cats && cats.All(x => x is JsonValue v && v.TryGetValue<string>(out _))
                ? [.. cats.Select(x => (string)x!)] : throw new InvalidDataException($"{rw}.categories: a list of category names or keys");
            sales.Add(new(name, from, until, @event, em, gm, 0, null, [], new(count, min, max, every, categories, Price(r, "min_price_em", rw), Price(r, "max_price_em", rw), Price(r, "min_price_gm", rw), Price(r, "max_price_gm", rw),
                r["exclude"] is null ? [] : Ids(r["exclude"], $"{rw}.exclude"))));
        }
        return [.. sales];
    }

    static string? Text(JsonObject o, string key, string where) => o[key] switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) && s.Trim() is { Length: > 0 } t => t,
        _ => throw new InvalidDataException($"{where}.{key}: text"),
    };

    static long? Time(JsonObject o, string key, string where) =>
        Text(o, key, where) is { } t ? RewardEvents.ParseTime(t) ?? throw new InvalidDataException($"{where}.{key}: an ISO time such as \"2026-11-27T15:00:00Z\"") : null;

    static double Pct(JsonNode? n, string where) =>
        n is JsonValue v && v.TryGetValue<double>(out var p) && p > 0 && p < 100 && p == Math.Floor(p) ? p : throw new InvalidDataException($"{where}: a whole number above 0 and below 100");

    static long[] Ids(JsonNode? n, string where) =>
        n is JsonArray a && a.All(x => x is JsonValue v && v.TryGetValue<long>(out _)) ? [.. a.Select(x => x!.GetValue<long>())] : throw new InvalidDataException($"{where}: a list of item ids");

    static long? Price(JsonObject o, string key, string where) =>
        o[key] is null ? null : o[key] is JsonValue v && v.TryGetValue<long>(out var p) && p >= 0 ? p : throw new InvalidDataException($"{where}.{key}: a whole number, 0 or more");
}

public sealed partial class State
{
    /// <summary>The sales in force, with what was worked out for them: the pick of each period (a function of the sale's name, the period's start and the catalogue, so it is the same after a reload or a restart)
    /// and the periods already written to `store_sale_picks`. Swapped whole when the settings are read again.</summary>
    sealed class SaleSet(StoreSale[] sales)
    {
        public readonly StoreSale[] Sales = sales;
        public readonly ConcurrentDictionary<(int Sale, long Start), Dictionary<long, double>> Picks = new();
        public readonly ConcurrentDictionary<(int Sale, long Start), bool> Recorded = new();
    }

    /// <summary>The best sale on one item for one currency: its percentage and the period it holds in.</summary>
    sealed record SaleHit(StoreSale Sale, double Percent, long Start, long Until);

    volatile SaleSet sales = new([]);

    /// <summary>Sets the sales from the settings' `store_sales` (Settings.cs, checked there); none: no sale. The list is swapped whole. Returns one line for each sale whose `event` is neither the settings' seasonal event
    /// nor a harvest bonus now in force (it never runs until one is): the caller logs them, once per (re)load.</summary>
    public string[] LoadStoreSales(JsonNode? cfg)
    {
        var set = new SaleSet(StoreSale.Parse(cfg));
        sales = set;
        return [.. set.Sales.Where(s => s.Event is { } e && !EventWindows(e).Any()).Select(s => $"store_sales: {s.Name}: event {s.Event} unknown, the sale never runs")];
    }

    /// <summary>The windows of the event `name`: the seasonal event of the settings and the harvest gm$ bonuses of that name.</summary>
    IEnumerable<(long Start, long End)> EventWindows(string name)
    {
        if (Seasonal is { } e && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
            yield return (e.Start.ToUnixTimeSeconds(), e.End.ToUnixTimeSeconds());
        foreach (var s in scheduled)
            if (string.Equals(BonusName(s.Bonus), name, StringComparison.OrdinalIgnoreCase))
                yield return ((long)s.Bonus.From, (long)s.Bonus.Until);
    }

    /// <summary>What is paid for `full` (a whole price) under a sale of `percent`: rounded half up like the store script's, never below 1 (a sale makes nothing free); 0 stays 0.</summary>
    static long Discounted(long full, double percent) => full > 0 ? Math.Max(1, (long)Math.Floor(full - full * (percent / 100) + 0.5)) : 0;

    /// <summary>The sale that applies to `item` at `now` for the price `key` (`price` em$, `silver_price` gm$; null: either): the larger discount when two do.</summary>
    SaleHit? SaleOn(JsonObject item, string? key, double now) => SaleFor(Py.L(item["id"]), false, key, now);

    /// <summary>The sale that applies to the bundle `id` at `now` (em$ only): the larger discount when two do.</summary>
    SaleHit? BundleSaleOn(JsonNode bundle, double now) => SaleFor(Py.L(bundle["id"]), true, "price", now);

    /// <summary>What the store asks for the bundle before the share of what the player owns is taken off (<see cref="Offer"/>): its list price at the sale's percentage while a sale lists it.</summary>
    long BundlePrice(JsonNode bundle) => BundleSaleOn(bundle, Clock()) is { } sale ? Discounted(Whole(bundle["price"]), sale.Percent) : Whole(bundle["price"]);

    SaleHit? SaleFor(long id, bool bundle, string? key, double now)
    {
        var set = sales;
        var t = (long)Math.Floor(now);
        SaleHit? best = null;
        for (var i = 0; i < set.Sales.Length; i++)
        {
            var sale = set.Sales[i];
            if (key == "price" && !sale.Em || key == "silver_price" && !sale.Gm || sale.At(t, EventWindows) is not { } period) continue;
            double percent = 0;
            var on = bundle ? sale.Bundles.Contains(id) && (percent = sale.Percent) > 0 : PicksOf(set, i, period).TryGetValue(id, out percent);
            if (on && (best is null || percent > best.Percent))
                best = new(sale, percent, period.Start, period.End);
        }
        return best;
    }

    Dictionary<long, double> PicksOf(SaleSet set, int sale, StoreSale.Period period)
    {
        if (!set.Picks.TryGetValue((sale, period.Start), out var picks))
            set.Picks[(sale, period.Start)] = picks = Pick(set.Sales[sale], period.Start);
        return picks;
    }

    /// <summary>The items of a sale in the period that starts at `start` and the percentage of each. A random sale orders the matching items by a hash of its name, the start and the item and keeps the first `count`,
    /// the percentage of each from another hash: no random number generator, so the pick is the same on every machine, after a restart and a reload.</summary>
    Dictionary<long, double> Pick(StoreSale sale, long start)
    {
        if (sale.Items is { } listed) return listed.Where(items.ContainsKey).Distinct().ToDictionary(id => id, _ => sale.Percent);
        var r = sale.Random!;
        ulong Hash(long id, int salt) => BitConverter.ToUInt64(SHA256.HashData(Encoding.UTF8.GetBytes($"{sale.Name}|{start.ToString(CultureInfo.InvariantCulture)}|{id.ToString(CultureInfo.InvariantCulture)}|{salt}")));
        double lowest = Math.Ceiling(r.Min / 5) * 5, steps = Math.Floor(r.Max / 5) - Math.Ceiling(r.Min / 5) + 1;
        return items.Values.Where(i => Matches(sale, r, i)).Select(i => Py.L(i["id"])).OrderBy(id => Hash(id, 0)).ThenBy(id => id).Take(r.Count)
            .ToDictionary(id => id, id => lowest + 5 * (Hash(id, 1) % (ulong)steps));
    }

    /// <summary>Whether the filters of a random sale let `item` in: shown, sold (not an unlimited ammunition or fuel), for sale in a currency of the sale within its price range, not excluded, in one of its categories.</summary>
    bool Matches(StoreSale sale, RandomSale r, JsonObject item)
    {
        long id = Py.L(item["id"]);
        bool Fits(string key, long? min, long? max) => StoreSells(item, key) && Whole(item[key]) >= (min ?? 0) && Whole(item[key]) <= (max ?? long.MaxValue);
        return !Py.Truthy(G(item, "hidden", null)) && !IsUnlimitedAmmo(item) && !IsUnlimitedFuel(id) && !r.Exclude.Contains(id)
            && (sale.Em && Fits("price", r.MinEm, r.MaxEm) || sale.Gm && Fits("silver_price", r.MinGm, r.MaxGm))
            && (r.Categories.Length == 0 || CategoryNames(item).Any(c => r.Categories.Contains(c, StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>What a `categories` entry of a random sale may call the item's category: its store key (the sub-category's id), the sub-category's title or its category's title.</summary>
    string[] CategoryNames(JsonObject item)
    {
        if (item["subcat"] is not JsonObject { } sub || sub["id"] is not { } id) return [];
        foreach (var c in Arr(G(application, "categories", null)))
            foreach (var s in Arr(c!["subcategories"]))
                if (Py.Eq(s!["id"], id)) return [Py.Str(id), Py.Str(s["title"]), Py.Str(c["title"])];
        return [Py.Str(id)];
    }

    /// <summary>Writes the picks of the current and the next period of every random sale to `store_sale_picks` (once each; one already there is left as it was), so the history of what was on sale stays readable.
    /// The running server does it at every look of <see cref="GmBonusWatch"/>, so a period is on record within 5 seconds of its start (prices are worked out from the same pick at once, whether or not it is recorded yet).</summary>
    public void RecordSalePicks()
    {
        var set = sales;
        var now = (long)Math.Floor(Clock());
        Db? con = null;
        try
        {
            for (var i = 0; i < set.Sales.Length; i++)
                if (set.Sales[i].Random is not null)
                    foreach (var period in new[] { set.Sales[i].At(now, EventWindows), set.Sales[i].Next(now, EventWindows) }.OfType<StoreSale.Period>())
                    {
                        if (set.Recorded.ContainsKey((i, period.Start))) continue;
                        con ??= Store.Open();
                        if ((long)con.Scalar("SELECT COUNT(*) FROM store_sale_picks WHERE sale = ? AND period_start = ?", set.Sales[i].Name, period.Start)! == 0)
                        {
                            con.Begin();
                            foreach (var (id, percent) in PicksOf(set, i, period))
                                con.Exec("INSERT INTO store_sale_picks (sale, period_start, period_end, item_id, percent) VALUES (?, ?, ?, ?, ?)", set.Sales[i].Name, period.Start, period.End, id, percent);
                            con.Commit();
                        }
                        set.Recorded[(i, period.Start)] = true;
                    }
        }
        finally { con?.Dispose(); }
    }

    /// <summary>`admin sales`: every sale, then its current and its next period with the items on sale and what each costs then.</summary>
    public List<string> SalesReport()
    {
        var set = sales;
        var now = (long)Math.Floor(Clock());
        var lines = new List<string>();
        for (var i = 0; i < set.Sales.Length; i++)
        {
            var s = set.Sales[i];
            string P(double d) => d.ToString(CultureInfo.InvariantCulture);
            var what = (s.Random is { } r ? $"random {r.Count}{(r.Every is { } e ? $" every {P(e / 3600.0)} h" : "")}, {P(r.Min)}-{P(r.Max)} %" : $"{P(s.Percent)} %")
                + $", {string.Join("+", new[] { s.Em ? "em$" : null, s.Gm ? "gm$" : null }.OfType<string>())}{(s.Event is { } ev ? $", event {ev}" : "")}";
            lines.Add($"{s.Name}: {what}");
            foreach (var (label, period) in new[] { ("now", s.At(now, EventWindows)), ("next", s.Next(now, EventWindows)) })
            {
                if (period is not { } p) { lines.Add($"  {label}: none"); continue; }
                lines.Add($"  {label}: {RewardEvents.Iso(p.Start)} .. {RewardEvents.Iso(p.End)}");
                foreach (var (id, percent) in PicksOf(set, i, p).OrderBy(x => x.Key))
                {
                    var item = items[id];
                    var prices = new[] { s.Em ? ("em$", Whole(item["price"])) : default, s.Gm ? ("gm$", Whole(item["silver_price"])) : default }
                        .Where(x => x.Item2 > 0).Select(x => $"{x.Item1} {x.Item2} -> {Discounted(x.Item2, percent)}");
                    lines.Add($"    {id} {Py.Str(item["name"])}: {P(percent)} %, {string.Join(", ", prices)}");
                }
                if (s.Em)
                    foreach (var bundle in Arr(G(application, "bundles", null)).Where(b => s.Bundles.Contains(Py.L(b!["id"]))))
                        lines.Add($"    bundle {Py.L(bundle!["id"])} {Py.Str(bundle["name"])}: {P(s.Percent)} %, em$ {Whole(bundle["price"])} -> {Discounted(Whole(bundle["price"]), s.Percent)}");
            }
        }
        return lines.Count > 0 ? lines : ["no store_sales"];
    }

    /// <summary>The reply of `Application/discounts`, which the game's store takes today's em$ prices from: a row `{item_id, percentage, start_ts, end_ts}` for each item on sale in em$ now. (A gm$ sale is not in it: the original's discounts are em$ only.)</summary>
    public byte[] DiscountsReply()
    {
        var now = Clock();
        var rows = new JsonArray();
        foreach (var item in items.Values)
            if (SaleOn(item, "price", now) is { } hit && Discounted(Whole(item["price"]), hit.Percent) < Whole(item["price"]))
                rows.Add(new JsonObject { ["item_id"] = C(item["id"]), ["percentage"] = hit.Percent % 1 == 0 ? JsonValue.Create((long)hit.Percent) : JsonValue.Create(hit.Percent), ["start_ts"] = hit.Start, ["end_ts"] = hit.Until });
        return Py.DumpBytes(rows);
    }
}
