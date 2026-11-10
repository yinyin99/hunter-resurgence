// Trophy score: server `features` -> score per family, as the public score pages print it. Replies must stay byte for byte identical to the format the client expects:
// Python json.dumps default separators and key order, round half to even, repr(float) shortest round-trip text, and
// the evaluation order and r4 placement of every operation kept as is.
using System.Text;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public static class Scoring
{
    public static string StaticHost = "", OwnLogo = ProtocolLiterals.SheetLogo;  // OwnLogo: header image of the non-B&C sheets
    public static Dictionary<int, (double Lo, double Hi, double Mult)> Variants = new();  // per-variant min/max score and css multiplier
    const double LB = 1 / 0.45359237;

    public static readonly Dictionary<int, string> FAMILY = Facts.Default.Families;  // species id → score family
    public static readonly HashSet<int> NON_TYPICAL = [100, 101, 106];  // pages carry these as killData.variant (species = the typical id)
    public static readonly HashSet<int> BIRDS_AG = [7, 15, 16, 27, 28, 29, 33, 34, 35, 47, 51];
    public static readonly HashSet<int> FLYING = [6, 11];  // animal_state flying, flying (not spooked)

    /// <summary>Config-dependent globals: game_web_host (the score sheets' images) and the variant table (min/max score and css multiplier per variant; a missing file = empty table).</summary>
    public static void Load(JsonObject cfg, string? variantsFile)
    {
        StaticHost = (string)cfg["game_web_host"]!;
        Variants = new();
        var path = variantsFile;
        if (path is null || !File.Exists(path)) return;
        foreach (var v in JsonNode.Parse(File.ReadAllText(path))!.AsArray())
            Variants[(int)Py.L(v!["id"])] = (Py.D(v["min_score"]), Py.D(v["max_score"]), Py.D(v["css_multiplier"]));
    }

    // ---- numeric helpers -------------------------------------------------------------------------------------

    static double R4(double x) => Py.Round(x, 4);
    static double Inch(double v) => R4(v / 25.4);
    static double Lb(double w) => R4(w / 1000 * LB);
    static double Lb(JsonNode? w) => Lb(Py.D(w));
    static JsonNode Num(double x) => JsonValue.Create(x);
    static double N(JsonNode? o, string k) => Py.D(o![k]);
    static double Get(JsonNode? o, string k) => o is JsonObject j && j[k] is { } v ? Py.D(v) : 0;  // dict.get(k, 0)
    static double PyMin(double a, double b) => b < a ? b : a;
    static double PyMax(double a, double b) => b > a ? b : a;

    /// <summary>Python sum() of floats (3.12+): Neumaier compensated summation.</summary>
    public static double Sum(IEnumerable<double> xs)
    {
        double f = 0, c = 0;
        foreach (var x in xs)
        {
            var t = f + x;
            c += Math.Abs(f) >= Math.Abs(x) ? f - t + x : x - t + f;
            f = t;
        }
        return c != 0 && double.IsFinite(c) ? f + c : f;
    }

    /// <summary>Python sum() over a mix of ints and floats: ints before the first float add exactly, the first float
    /// adds plainly, later floats are compensated, later ints add plainly.</summary>
    static double SumMixed(IEnumerable<(double V, bool IsInt)> xs)
    {
        double f = 0, c = 0;
        var inFloat = false;
        foreach (var (x, isInt) in xs)
        {
            if (!inFloat || isInt)
            {
                f += x;
                inFloat |= !isInt;
                continue;
            }
            var t = f + x;
            c += Math.Abs(f) >= Math.Abs(x) ? f - t + x : x - t + f;
            f = t;
        }
        return c != 0 && double.IsFinite(c) ? f + c : f;
    }

    // ponytail: .NET G ties away from zero and uses E+XX; exact ties and exponents don't occur for these magnitudes
    /// <summary>A float as the site's PHP echo prints it (precision 14): 72.126, 0.</summary>
    static string Php(double x) => x.ToString("G14");

    // ---- score -----------------------------------------------------------------------------------------------

    static JsonObject Side(JsonNode? f, string k) => f is JsonObject o && o[k] is JsonObject s ? s : new JsonObject();  // f.get(k, {})
    static JsonArray Arr(JsonObject o, string k) => o[k] as JsonArray ?? [];  // o.get(k, [])
    static double? Code(JsonNode? n) => Py.IsNum(n) ? Py.D(n) : null;

    /// <summary>Python list[a:b].</summary>
    static List<double> Slice(List<double> l, long a, long b)
    {
        long Norm(long i) => Math.Clamp(i < 0 ? i + l.Count : i, 0, l.Count);
        a = Norm(a);
        b = Norm(b);
        return a >= b ? [] : l.GetRange((int)a, (int)(b - a));
    }

    /// <summary>B&amp;C columns: (spread credit, right values, left values, difference, E) in inches, as the sheet prints them.
    /// gBase: type number of G-1 in `g` (typical pages 1, non-typical pages 0).</summary>
    static (double Spread, List<double> Right, List<double> Left, double Diff, double E) BcSides(JsonNode? f, int gBase = 1)
    {
        (List<double>, List<double>) Vals(JsonObject side)
        {
            var g = new Dictionary<long, double>();
            foreach (var pair in Arr(side, "g"))
                g[Py.L(pair![1]) - gBase + 1] = Inch(Py.D(pair[0]));
            var first = new List<double> { Inch(Get(side, "f")) };
            var max = g.Count == 0 ? 0 : g.Keys.Max();
            for (long k = 1; k <= max; k++) first.Add(g.GetValueOrDefault(k, 0));
            return (first, Arr(side, "h").Select(x => Inch(Py.D(x))).ToList());
        }
        var (rf, rh) = Vals(Side(f, "right"));
        var (lf, lh) = Vals(Side(f, "left"));
        var n = Math.Max(rf.Count, lf.Count);
        rf.AddRange(new double[n - rf.Count]);
        lf.AddRange(new double[n - lf.Count]);
        var m = Math.Max(rh.Count, lh.Count);
        rh.AddRange(new double[m - rh.Count]);
        lh.AddRange(new double[m - lh.Count]);
        List<double> right = [.. rf, .. rh], left = [.. lf, .. lh];
        var spread = PyMin(Inch(Get(f, "d")), PyMax(right[0], left[0]));
        var diff = Sum(right.Zip(left, (a, b) => R4(Math.Abs(a - b))));
        var e = Sum(new[] { "right", "left" }.SelectMany(side => Arr(Side(f, side), "e")).Select(x => Inch(Py.D(x))));
        return (spread, right, left, diff, e);
    }

    /// <summary>(score, scoreType) of a kill; (null, null) for an unknown species or an unsolved family.</summary>
    public static (JsonNode? Score, string? Type) Score(int species, JsonNode? features, JsonNode? weightGrams, JsonNode? lastHitState)
    {
        FAMILY.TryGetValue(species, out var fam);
        // a kill that scores nothing (a female) carries `features: []`, not an object
        if (features is JsonArray { Count: 0 } && fam is not (null or "weight_lb" or "weight_kg" or "weight_g" or "tusks")) return (Num(0), null);
        var f = features;
        JsonNode? s = null;
        string? ty = null;
        switch (fam)
        {
            case "weight_lb":
                s = Num(Lb(weightGrams));
                break;
            case "weight_mg":
                s = Num(R4(N(f, "weight_mg") / 1000));
                break;
            case "weight_kg":
                s = Num(R4(Py.D(weightGrams) / 1000));
                break;
            case "weight_g":
                s = weightGrams?.DeepClone();
                break;
            case "skull":
                s = Num(R4(Inch(N(f, "length")) + Inch(N(f, "width"))));
                break;
            case "turkey":
            {
                var sp = f!["spurs"];
                s = Num(R4(Lb(weightGrams) + 10 * R4(Inch(N(sp, "left")) + Inch(N(sp, "right"))) + 2 * Inch(N(f, "beard"))));
                break;
            }
            case "pheasant":
            {
                var sp = f!["spurs"];
                s = Num(R4(Inch(N(f, "tail")) + R4(Inch(N(sp, "left")) + Inch(N(sp, "right")))));
                break;
            }
            case "tusks":  // sows have no tusks (features [])
            {
                var t = Py.Truthy(f) ? f!["tusks"]! : JsonNode.Parse("""{"length": {"left": 0, "right": 0}, "circ": {"left": 0, "right": 0}}""")!;
                var t1 = R4(Inch(N(t["length"], "left")) + Inch(N(t["length"], "right")));
                var t2 = R4(Inch(N(t["circ"], "left")) + Inch(N(t["circ"], "right")));
                s = Num(R4(Lb(weightGrams) + t2 + t1 * 50));
                ty = t1 * 50 > Lb(weightGrams) ? "T" : "W";
                break;
            }
            case "bc_deer":
            {
                var nt = NON_TYPICAL.Contains(species);
                var (spread, right, left, diff, e) = BcSides(f, nt ? 0 : 1);
                s = Num(R4(spread + Sum(right) + Sum(left) - diff + (nt ? e : -e)));
                break;
            }
            case "elk":  // B&C typical plus crown points (Total I)
            {
                var (spread, right, left, diff, e) = BcSides(f);
                var crown = Sum(new[] { "right", "left" }.SelectMany(side => Arr(Side(f, side), "i")).Select(x => Inch(Py.D(x))));
                s = Num(R4(spread + Sum(right) + Sum(left) - diff + crown - e));
                break;
            }
            case "moose":  // sheet prints rounded values, the total uses the unrounded ones
            {
                var a = f!["right"]!;
                var b = f["left"]!;
                (double, bool)[] ra = [(N(a, "p"), Py.IsInt(a["p"])), (N(a, "w") / 25.4, false), (N(a, "l") / 25.4, false), (N(a, "c") / 25.4, false)];
                (double, bool)[] rb = [(N(b, "p"), Py.IsInt(b["p"])), (N(b, "w") / 25.4, false), (N(b, "l") / 25.4, false), (N(b, "c") / 25.4, false)];
                var diffs = ra.Zip(rb, (x, y) => (Math.Abs(x.Item1 - y.Item1), x.Item2 && y.Item2));
                s = Num(R4(N(f, "span") / 25.4 + SumMixed(ra) + SumMixed(rb) - SumMixed(diffs) - N(a, "a") - N(b, "a")));
                break;
            }
            case "caribou":  // point counts add to the columns but never to the difference; differences from mm
            {
                string[] keys = ["mb", "bp", "bez", "rp", "stp", "tp", "bpw", "tpw", "c1", "c2", "c3", "c4"];
                var a = f!["right"]!;
                var b = f["left"]!;
                s = Num(R4(Inch(N(f, "sc")) + N(a, "pts") + N(a, "bpts") + N(b, "pts") + N(b, "bpts")
                    + Sum(keys.Select(k => Inch(N(a, k)) + Inch(N(b, k))))
                    - Sum(keys.Select(k => Inch(Math.Abs(N(a, k) - N(b, k)))))));
                break;
            }
            case "bovid":  // sheet prints rounded values, the total uses the unrounded ones
            {
                List<double> Col(JsonNode? x) => [N(x, "l") / 2540, .. new[] { "c1", "c2", "c3", "c4" }.Select(c => N(x, c) / 25.4)];
                var ra = Col(f!["right"]);
                var rb = Col(f["left"]);
                s = Num(R4(Sum(ra) + Sum(rb) - Sum(ra.Zip(rb, (x, y) => Math.Abs(x - y)))));
                break;
            }
            case "sheep":
            {
                var rc = ((JsonArray)f!["rc"]!).Select(x => Inch(Py.D(x))).ToList();
                var lc = ((JsonArray)f["lc"]!).Select(x => Inch(Py.D(x))).ToList();
                s = Num(R4(Inch(N(f, "rl")) + Inch(N(f, "ll")) + Sum(rc) + Sum(lc) - Sum(rc.Zip(lc, (x, y) => R4(Math.Abs(x - y))))));
                break;
            }
            case "tines":
            {
                var sum = Inch(N(f, "inside_span_main_beam"));
                foreach (var x in new[] { f!["left"], f["right"] })
                    sum += Inch(N(x, "length_main_beam")) + Sum(((JsonArray)x!["length_tines"]!).Select(t => Inch(Py.D(t))))
                        + Inch(N(x, "circumference_burr")) + Inch(N(x, "circumference_main_beam"));
                s = Num(R4(sum));
                break;
            }
            case "cic_horn":  // cm; lengths in 1/1000 cm, the rest in mm; colour, age rings, curve 0-3 points
                s = Num(R4((N(f, "ll") + N(f, "rl")) / 2000 + (N(f, "lc") + N(f, "rc") + N(f, "g")) / 10 + N(f, "col") + N(f, "ar") + N(f, "hc")));
                break;
            case "cic_fallow":
            {
                var a = f!["left"];
                var b = f["right"];
                double Avg(string k) => (N(a, k) + N(b, k)) / 20;
                s = Num(R4(Avg("mb") * 0.5 + Avg("bp") * 0.25 + Avg("tp") + Avg("tpw") * 1.5 + Avg("c1")
                    + (N(a, "c2") + N(b, "c2") + N(a, "c3") + N(b, "c3")) / 10 + (N(a, "pts") + N(b, "pts")) / 2));
                break;
            }
            case "cic_red":
            {
                var a = f!["left"];
                var b = f["right"];
                double Avg(string k) => (N(a, k) + N(b, k)) / 20;
                s = Num(R4(Avg("mb") * 0.5 + Avg("bt") * 0.25 + Avg("tt") * 0.25 + Avg("cc") + (N(a, "cl") + N(b, "cl")) / 10
                    + (N(a, "cu") + N(b, "cu")) / 10 + N(f, "w") / 1e6 * 2 + SpanPoints(N(f, "s") / 10, Avg("mb"), RED_SPAN)
                    + N(a, "p") + N(b, "p")));
                break;
            }
            case "cic_roe":
            {
                var beam = (N(f, "lb") + N(f, "rb")) / 20;
                s = Num(R4(beam * 0.5 + N(f, "w") / 1000 * 0.1 + N(f, "v") / 1000 * 0.3 + SpanPoints(N(f, "s") / 10, beam, ROE_SPAN, ROE_SPAN_MAX)
                    + N(f, "col") + N(f, "p") + N(f, "cor")));
                break;
            }
        }
        if (BIRDS_AG.Contains(species) && s is not null) ty = InFlying(lastHitState) ? "A" : "G";
        return (s, ty);
    }

    /// <summary>`state in FLYING` for a JSON animal_state (None and non-numbers are never flying).</summary>
    public static bool InFlying(JsonNode? state) => Py.IsNum(state) && FLYING.Any(x => x == Py.D(state));

    static readonly int[] RED_SPAN = [60, 70, 80];  // inside span, % of the average main beam: one CIC point per threshold reached
    static readonly int[] ROE_SPAN = [30, 35, 40, 45];
    // ponytail: a roe span above this % scores 0; the exact limit is unknown: 63.64 scores 4 points, 85.71 scores 0
    const int ROE_SPAN_MAX = 75;

    static double SpanPoints(double span, double beam, int[] thresholds, int? limit = null)
    {
        var pct = beam != 0 ? Py.Round(100 * span / beam, 2) : 0;  // 8.2 / 20.5 is 40 %, not 39.999…
        return limit is not null && pct > limit ? 0 : thresholds.Count(t => pct >= t);
    }

    /// <summary>Trophy rating as the server sends it in update 6 (gold = round(css)); 0.0 without variant bounds.</summary>
    public static double Css(int species, JsonNode? score)
    {
        var (lo, hi, mult) = Variants.GetValueOrDefault(species);
        if (!Py.Truthy(score) || hi <= lo) return 0.0;
        return Py.Round(mult * 100 * (Py.D(score) - lo) / (hi - lo), 1);
    }

    // ---- features --------------------------------------------------------------------------------------------

    // ponytail: turkey, pheasant and elk crown points `i` are not mapped yet
    /// <summary>Server `features` from the kill's event 7 (spawn) and event 22 (trophy); null where the mapping is unknown.</summary>
    public static JsonNode? Features(int species, JsonObject e7, JsonObject e22, JsonNode? scale)
    {
        FAMILY.TryGetValue(species, out var fam);
        if (fam is "weight_lb" or "weight_kg" or "weight_g") return new JsonArray();
        if (fam == "weight_mg")
        {
            if (e7.ContainsKey("feature")) return new JsonObject { ["weight_mg"] = e7["feature"]?.DeepClone() };
            var w = e7["weight_grams"];
            return new JsonObject
            {
                ["weight_mg"] = w is null ? JsonValue.Create(0L) : Py.IsInt(w) ? JsonValue.Create(Py.L(w) * 1000) : JsonValue.Create(Py.D(w) * 1000),
            };
        }
        // skull: event 7 `feature` = width << 16 | length, both in 1/100 mm, times scale_factor, not floored
        // (e.g. 100 << 16 | 200 → 1.00 / 2.00 at scale 1)
        if (fam == "skull")
        {
            if (!e7.ContainsKey("feature")) return null;
            var packed = Py.L(e7["feature"]);
            double Scaled(long v) => R4(v / 100.0 * Py.D(scale));
            return new JsonObject { ["width"] = Scaled(packed >> 16), ["length"] = Scaled(packed & 0xFFFF) };
        }
        // elk = bc_deer keys; crown points `i` stay unmapped
        if (fam is null or "turkey" or "pheasant" || !Py.Truthy(e22)) return null;
        JsonObject Antler(string k) => e22[k] is { } v && Py.Truthy(v) ? (JsonObject)v : new JsonObject();  // e22.get(k) or {}
        var sides = new List<(string K, JsonObject A)> { ("right", Antler("right_antler")), ("left", Antler("left_antler")) };
        if (!sides.Any(s => Arr(s.A, "points").Any(p => Code(p!["type"]) == 0))) return null;

        long Mm(double v) => (long)Math.Floor(v * Py.D(scale));
        long MmGet(JsonObject o, string k) => Mm(Get(o, k));
        long Rd(JsonObject o, string k) => (long)Math.Round(Get(o, k) * Py.D(scale));  // round half even, like Python round()
        long Point(JsonObject a, int t) => Arr(a, "points").FirstOrDefault(p => Py.D(p!["type"]) == t) is { } pt ? Mm(Py.D(pt["length"])) : 0;  // 0 when the side has none
        JsonNode L(long v) => JsonValue.Create(v);
        JsonNode? Of(JsonObject o, string k) => o.ContainsKey(k) ? o[k]?.DeepClone() : JsonValue.Create(0L);  // o.get(k, 0)
        JsonNode? Pass(string k) => Of(e22, k);
        JsonNode Count(JsonObject a, string k) => L(Arr(a, k).Count);
        JsonObject Circ(JsonObject o, JsonObject a, int n)  // c1..cn = h1..hn
        {
            for (var i = 1; i <= n; i++) o[$"c{i}"] = L(MmGet(a, $"circumference_h{i}"));
            return o;
        }
        JsonObject Both(JsonObject top, Func<JsonObject, JsonObject> side)
        {
            foreach (var (k, a) in sides) top[k] = side(a);
            return top;
        }
        // one case per family
        switch (fam)
        {
            case "tusks":
                return new JsonObject
                {
                    ["tusks"] = new JsonObject
                    {
                        ["length"] = new JsonObject { ["left"] = L(Point(sides[1].A, 0)), ["right"] = L(Point(sides[0].A, 0)) },
                        ["circ"] = new JsonObject { ["left"] = L(MmGet(sides[1].A, "circumference_h1")), ["right"] = L(MmGet(sides[0].A, "circumference_h1")) },
                    },
                };
            case "moose":  // `c` is h1 unscaled; palm length / width round (100 x 0.6 = 60.0 → 60; 101 x 0.6 = 60.6 → 61)
                return Both(new JsonObject { ["span"] = L(MmGet(e22, "greatest_spread")) }, a => new JsonObject
                {
                    ["p"] = Count(a, "points"), ["a"] = Count(a, "abnormals"), ["c"] = Of(a, "circumference_h1"),
                    ["l"] = L(Rd(a, "palm_length")), ["w"] = L(Rd(a, "palm_width")),
                });
            case "caribou":
                return Both(new JsonObject
                {
                    ["g"] = L(MmGet(e22, "greatest_spread")), ["s"] = L(MmGet(e22, "inside_spread_of_main_beams")),
                    ["t"] = L(MmGet(e22, "tip_to_tip_spread")), ["w"] = L(MmGet(e22, "weight")), ["sc"] = L(MmGet(e22, "inside_spread_of_main_beams")),
                }, a => Circ(new JsonObject
                {
                    ["mb"] = L(Point(a, 0)), ["bp"] = L(Point(a, 1)), ["bez"] = L(Point(a, 2)), ["rp"] = L(Point(a, 3)),
                    ["stp"] = L(Point(a, 4)), ["tp"] = L(Point(a, 5)), ["bpw"] = L(MmGet(a, "brow_palm_width")),
                    ["tpw"] = L(MmGet(a, "top_palm_width")), ["pts"] = Of(a, "nof_points_excluding_brows"), ["bpts"] = Of(a, "nof_brow_points"),
                }, a, 4));
            case "bovid":
                return Both(new JsonObject { ["g"] = L(MmGet(e22, "greatest_spread")), ["t"] = L(MmGet(e22, "tip_to_tip_spread")) },
                    a => Circ(new JsonObject { ["l"] = L(Point(a, 0)) }, a, 4));
            case "sheep":
            {
                JsonArray Hs(JsonObject a) => new(Enumerable.Range(1, 4).Select(i => L(MmGet(a, $"circumference_h{i}"))).ToArray());
                return new JsonObject
                {
                    ["rl"] = L(Point(sides[0].A, 0)), ["ll"] = L(Point(sides[1].A, 0)), ["rc"] = Hs(sides[0].A), ["lc"] = Hs(sides[1].A),
                    ["g"] = L(MmGet(e22, "greatest_spread")), ["ts"] = L(MmGet(e22, "tip_to_tip_spread")),
                };
            }
            case "cic_red":
                return Both(new JsonObject { ["s"] = L(MmGet(e22, "inside_spread_of_main_beams")), ["w"] = L(MmGet(e22, "weight")) }, a => new JsonObject
                {
                    ["mb"] = L(Point(a, 0)), ["bt"] = L(Point(a, 1)), ["tt"] = L(Point(a, 3)), ["cc"] = L(MmGet(a, "circumference_h1")),
                    ["cl"] = L(MmGet(a, "circumference_h2")), ["cu"] = L(MmGet(a, "circumference_h3")), ["p"] = Count(a, "points"),
                });
            case "cic_fallow":
                return Both(new JsonObject
                {
                    ["g"] = L(MmGet(e22, "greatest_spread")), ["s"] = L(MmGet(e22, "inside_spread_of_main_beams")),
                    ["t"] = L(MmGet(e22, "tip_to_tip_spread")), ["w"] = L(MmGet(e22, "weight")), ["sc"] = L(MmGet(e22, "inside_spread_of_main_beams")),
                }, a => Circ(new JsonObject
                {
                    ["mb"] = L(Point(a, 0)), ["bp"] = L(Point(a, 1)), ["tp"] = L(MmGet(a, "top_palm_length")),
                    ["tpw"] = L(MmGet(a, "top_palm_width")), ["pts"] = Count(a, "points"),
                }, a, 3));
            case "cic_roe":  // beams floor, span / volume / weight round (85.6 → 86, 172849.6 → 172850, 345699.2 → 345699)
                return new JsonObject
                {
                    ["lb"] = L(Point(sides[1].A, 0)), ["rb"] = L(Point(sides[0].A, 0)), ["s"] = L(Rd(e22, "inside_spread_of_main_beams")),
                    ["v"] = L(Rd(e22, "volume")), ["w"] = L(Rd(e22, "weight")), ["col"] = Pass("color"), ["p"] = Pass("pearling"),
                    ["cor"] = Pass("coronets"),
                };
        }

        if (fam == "tines")  // T-1..T-3 = point types 1..3; burr / main beam = h1 / h2
        {
            var r = new JsonObject { ["inside_span_main_beam"] = L(MmGet(e22, "inside_spread_of_main_beams")) };
            foreach (var (k, a) in sides)
                r[k] = new JsonObject
                {
                    ["length_main_beam"] = L(Point(a, 0)),
                    ["length_tines"] = new JsonArray(L(Point(a, 1)), L(Point(a, 2)), L(Point(a, 3))),
                    ["circumference_burr"] = L(MmGet(a, "circumference_h1")),
                    ["circumference_main_beam"] = L(MmGet(a, "circumference_h2")),
                };
            return r;
        }
        if (fam == "cic_horn")  // horn length = main beam point (1/1000 cm), circumference = h1
        {
            var r = sides[0].A;
            var left = sides[1].A;
            return new JsonObject
            {
                ["ll"] = L(Point(left, 0)), ["rl"] = L(Point(r, 0)), ["lc"] = L(MmGet(left, "circumference_h1")),
                ["rc"] = L(MmGet(r, "circumference_h1")), ["g"] = L(MmGet(e22, "greatest_spread")),
                ["col"] = Pass("color"), ["ar"] = Pass("age_rings"), ["hc"] = Pass("horn_curve"),
            };
        }
        JsonObject SideOut(JsonObject a)
        {
            // f = the first type-0 point, g = the others sorted by type; a non-typical side sends a second type-0 point
            // (G-1), which goes into g as [len, 0]
            var pts = Arr(a, "points").ToList();
            var first = pts.FindIndex(p => Py.D(p!["type"]) == 0);
            var beam = first >= 0 ? Mm(Py.D(pts[first]!["length"])) : 0;
            var g = new JsonArray();
            foreach (var q in pts.Where((_, i) => i != first).OrderBy(p => Py.D(p!["type"])))  // stable, like sorted(key=)
                g.Add(new JsonArray(L(Mm(Py.D(q!["length"]))), q["type"]!.DeepClone()));
            var side = new JsonObject
            {
                ["h"] = new JsonArray(Enumerable.Range(1, 4).Select(i => L(MmGet(a, $"circumference_h{i}"))).ToArray()),
                ["g"] = g,
                ["f"] = L(beam),
            };
            // abnormal points: `e`, only on a side that has any
            var abnormal = Arr(a, "abnormals").Select(p => L(Mm(Py.D(p!["length"])))).ToArray();
            if (abnormal.Length > 0) side["e"] = new JsonArray(abnormal);
            return side;
        }
        return new JsonObject
        {
            ["c"] = L(MmGet(e22, "greatest_spread")), ["b"] = L(MmGet(e22, "tip_to_tip_spread")),
            ["d"] = L(MmGet(e22, "inside_spread_of_main_beams")), ["right"] = SideOut(sides[0].A), ["left"] = SideOut(sides[1].A),
        };
    }

    // ---- score sheets ----------------------------------------------------------------------------------------

    // B&C typical sheets: score-info image and row labels per species
    static readonly Dictionary<int, string> BC_IMAGE = new() { [0] = "antlers", [1] = "antlers_mule", [6] = "antlers_blacktail", [26] = "antlers_sitka_deer" };
    static readonly string[] MULE_G = ["G-1. First point length (if present)", "G-2. Second point length",
        "G-3. Third point length (if present)", "G-4. Fourth point length (if present)"];
    static readonly string[] MULE_H = ["H-1. Narrowest circumference, burr to first point",
        "H-2. Narrowest circumference, first to second point",
        "H-3. Narrowest circumference, main beam to third point",
        "H-4. Narrowest circumference, second to fourth point"];
    static readonly string[] WHITETAIL_G = ["G-1. First point length", "G-2. Second point length", "G-3. Third point length",
        "G-4. Fourth point length (if present)", "G-5. Fifth point length (if present)",
        "G-6. Sixth point length (if present)", "G-7. Seventh point length (if present)"];
    static readonly string[] WHITETAIL_H = ["H-1. Narrowest circumference, burr to first point",
        "H-2. Narrowest circumference, first to second point",
        "H-3. Narrowest circumference, second to third point",
        "H-4. Narrowest circumference, third to fourth point"];
    static readonly Dictionary<int, (string[] G, string[] H)> BC_LABELS = new()
    {
        [0] = (WHITETAIL_G, WHITETAIL_H), [1] = (MULE_G, MULE_H), [6] = (MULE_G, MULE_H), [26] = (MULE_G, MULE_H),
    };
    static readonly Dictionary<int, string> SHEET_IMAGE = new() { [27] = "northern_pintail", [35] = "white_tailed_ptarmigan" };  // else define.lower()
    static readonly HashSet<int> GENDERED_IMAGE = [58];  // <define>_male / _female
    const int ABNORMAL_ROWS = 6;  // least abnormal-points rows on typical sheets (padded with 0.0000)

    static string SheetHead(string cssClass, string logo, string name, string image) =>
        $"<div class=\"{cssClass}\">\n" +
            $"\t<img class=\"score-header-image\" src=\"//{StaticHost}/static/img/scoring/{logo}.png\" alt=\"\">\n" +
            $"\t\t<div class=\"species-title\">{ScorePage.Escape(name, false)}</div>\n" +
            $"\t\t<div class=\"infoimage\">\n" +
            $"\t\t\t<img class=\"score-info-image\" src=\"//{StaticHost}/static/img/scoring/{image}.png\" alt=\"\">\n";

    /// <summary>What a B&amp;C typical sheet is worked from: the row labels, the right and left values of each row (inches), the abnormal points of each side, the spread credit, the sum
    /// of the differences, E, and the number of points of each side.</summary>
    sealed record Bc(List<string> Labels, List<double> Right, List<double> Left, List<double> Er, List<double> El, double Spread, double Diff, double E, int[] Points);

    static Bc BcCalc(int species, JsonNode? f)
    {
        var (gLabels, hLabels) = BC_LABELS[species];
        var (spread, right, left, diff, e) = BcSides(f);
        var nG = right.Count - 1 - hLabels.Length;
        var pad = Math.Max(gLabels.Length - nG, 0);
        right = [.. Slice(right, 0, 1 + nG), .. new double[pad], .. Slice(right, 1 + nG, int.MaxValue)];
        left = [.. Slice(left, 0, 1 + nG), .. new double[pad], .. Slice(left, 1 + nG, int.MaxValue)];
        List<string> labels = ["F. Main beam length", .. gLabels];
        for (var i = gLabels.Length + 1; i <= nG; i++) labels.Add($"G-{i}");
        labels.AddRange(hLabels);
        var er = Arr(Side(f, "right"), "e").Select(x => Inch(Py.D(x))).ToList();
        var el = Arr(Side(f, "left"), "e").Select(x => Inch(Py.D(x))).ToList();
        var points = new[] { "right", "left" }.Select(side => 1 + Arr(Side(f, side), "g").Count + Arr(Side(f, side), "e").Count).ToArray();
        return new(labels, right, left, er, el, spread, diff, e, points);
    }

    /// <summary>The worked sheet as data, for the website: <c>kind</c> "score" (a B&amp;C typical sheet: <c>facts</c>, <c>rows</c> as right, left and difference, <c>abnormal</c> pairs, <c>sums</c>),
    /// "antler" (a tines sheet: <c>rows</c> as left, right and points) or "antler" without rows (a species whose sheet is no table here). Numbers are inches, as the HTML sheet prints them.</summary>
    public static JsonObject SheetData(int species, JsonNode? features)
    {
        static JsonObject Row(string label, double? a, double? b, double? c) => new() { ["label"] = label, ["a"] = a, ["b"] = b, ["c"] = c };
        static JsonObject Fact(string label, double value) => new() { ["label"] = label, ["value"] = value };
        FAMILY.TryGetValue(species, out var fam);
        var data = new JsonObject { ["kind"] = "antler", ["facts"] = new JsonArray(), ["rows"] = new JsonArray(), ["abnormal"] = new JsonArray(), ["sums"] = new JsonArray() };
        if (fam == "bc_deer" && BC_LABELS.ContainsKey(species))
        {
            var bc = BcCalc(species, features);
            double c2 = Sum(bc.Right), c3 = Sum(bc.Left), subtract = bc.Diff + bc.E;
            data["kind"] = "score";
            data["facts"] = new JsonArray(Fact("A. Points, right antler", bc.Points[0]), Fact("Points, left antler", bc.Points[1]), Fact("B. Tip-to-tip spread", Inch(Get(features, "b"))),
                Fact("C. Greatest spread", Inch(Get(features, "c"))), Fact("D. Inside spread of main beams", Inch(Get(features, "d"))));
            var rows = new JsonArray([.. Enumerable.Range(0, Math.Min(bc.Labels.Count, Math.Min(bc.Right.Count, bc.Left.Count)))
                .Select(i => (JsonNode?)Row(bc.Labels[i], bc.Right[i], bc.Left[i], R4(Math.Abs(bc.Right[i] - bc.Left[i]))))]);
            rows.Add(Row("Total", c2, c3, subtract));
            data["rows"] = rows;
            data["abnormal"] = new JsonArray([.. Enumerable.Range(0, Math.Max(ABNORMAL_ROWS, Math.Max(bc.Er.Count, bc.El.Count)))
                .Select(i => (JsonNode?)new JsonObject { ["right"] = i < bc.Er.Count ? bc.Er[i] : 0, ["left"] = i < bc.El.Count ? bc.El[i] : 0 })]);
            data["sums"] = new JsonArray(Fact("Column 1", bc.Spread), Fact("Column 2", c2), Fact("Column 3", c3), Fact("Subtotal", R4(bc.Spread + c2 + c3)),
                Fact("Subtract Column 4", subtract), Fact("Total", R4(bc.Spread + c2 + c3 - bc.Diff - bc.E)));
        }
        else if (fam == "tines" && features is JsonObject tf && TINES_SHEET.ContainsKey(species))
        {
            var (labels, mm, span) = TinesCalc(species, tf);
            var rows = new JsonArray([.. mm.Select((m, i) => (JsonNode?)Row(labels[i], Inch(m.L), Inch(m.R), Inch(m.L + m.R)))]);
            rows.Add(Row(labels[^1], null, null, Inch(span)));
            data["rows"] = rows;
        }
        return data;
    }

    static string BcSheet(int species, JsonNode? f, string cssClass, string name)
    {
        var bc = BcCalc(species, f);
        var (spread, right, left, diff, e, labels, er, el) = (bc.Spread, bc.Right, bc.Left, bc.Diff, bc.E, bc.Labels, bc.Er, bc.El);
        var nE = Math.Max(ABNORMAL_ROWS, Math.Max(er.Count, el.Count));
        var abnormal = new StringBuilder();
        for (var i = 0; i < nE; i++)
        {
            double r = i < er.Count ? er[i] : 0, lv = i < el.Count ? el[i] : 0;
            abnormal.Append($"\n" +
            $"\t\t<tr class=\"scoresheet-row\">\n" +
            $"\t\t\t<td class=\"short\"></td>\n" +
            $"\t\t\t<td class=\"abnormal value\">{Py.Fixed(r, 4)}</td>\n" +
            $"\t\t\t<td class=\"abnormal value\">{Py.Fixed(lv, 4)}</td>\n" +
            $"\t\t</tr>");
        }
        var rows = new StringBuilder();
        for (var i = 0; i < Math.Min(labels.Count, Math.Min(right.Count, left.Count)); i++)
        {
            string label = labels[i];
            double r = right[i], lv = left[i];
            rows.Append($"\n" +
            $"<tr class=\"scoresheet-row\">\n" +
            $"\t<td class=\"scoresheet-label\" colspan=\"4\">{label}</td>\n" +
            $"\t<td class=\"empty\"></td>\n" +
            $"\t<td class=\"value\">{Py.Fixed(r, 4)}</td>\n" +
            $"\t<td class=\"value\">{Py.Fixed(lv, 4)}</td>\n" +
            $"\t<td class=\"value\">{Py.Fixed(R4(Math.Abs(r - lv)), 4)}</td>\n" +
            $"</tr>");
        }
        double c2 = Sum(right), c3 = Sum(left);
        var sub = R4(spread + c2 + c3);
        var points = bc.Points;
        return SheetHead(cssClass, "boonelogo", name, BC_IMAGE[species]) + $"\t\t\t<div>\n" +
            $"\t<table>\n" +
            $"\t\t<tr class=\"scoresheet-row\">\n" +
            $"\t\t\t<td class=\"short\"></td>\n" +
            $"\t\t\t<td class=\"head\" colspan=\"2\">Abnormal points</td>\n" +
            $"\t\t</tr>\n" +
            $"\t\t<tr class=\"scoresheet-row\">\n" +
            $"\t\t\t<td class=\"short\"></td>\n" +
            $"\t\t\t<td class=\"baseab\">Right Antler</td>\n" +
            $"\t\t\t<td class=\"baseab\">Left Antler</td>\n" +
            $"\t\t</tr>{abnormal}\n" +
            $"\t\t<tr class=\"scoresheet-row\">\n" +
            $"\t\t\t<td class=\"abnormal value\">Subtotals</td>\n" +
            $"\t\t\t<td class=\"abnormal value\">{Php(Sum(er))}</td>\n" +
            $"\t\t\t<td class=\"abnormal value\">{Php(Sum(el))}</td>\n" +
            $"\t\t</tr>\n" +
            $"\t\t<tr class=\"scoresheet-row\">\n" +
            $"\t\t\t<td class=\"abnormal\">Total E</td>\n" +
            $"\t\t\t<td class=\"sum value\" colspan=\"2\">{Py.Fixed(e, 4)}</td>\n" +
            $"\t\t</tr>\n" +
            $"\t</table>\n" +
            $"\t\t\t<div class=\"clear\"></div>\n" +
            $"\t\t</div>\n" +
            $"</div>\n" +
            $"<div>\n" +
            $"\t<table class=\"score\">\n" +
            $"\t\t<tr class=\"scoresheet-row\">\n" +
            $"\t\t\t<td class=\"head\" colspan=\"4\"></td>\n" +
            $"\t\t\t<td class=\"head\">Column 1</td>\n" +
            $"\t\t\t<td class=\"head\">Column 2</td>\n" +
            $"\t\t\t<td class=\"head\">Column 3</td>\n" +
            $"\t\t\t<td class=\"head\">Column 4</td>\n" +
            $"\t\t</tr>\n" +
            $"\t\t<tr class=\"scoresheet-row\">\n" +
            $"\t\t\t<td class=\"baselabel\">A. Points, right antler</td>\n" +
            $"\t\t\t<td class=\"basevalue\">{points[0]}</td>\n" +
            $"\t\t\t<td class=\"baselabel\">Points, left antler</td>\n" +
            $"\t\t\t<td class=\"basevalue\">{points[1]}</td>\n" +
            $"\t\t\t<td class=\"baselabel\">Spread credit</td>\n" +
            $"\t\t\t<td class=\"baselabel\">Right Antler</td>\n" +
            $"\t\t\t<td class=\"baselabel\">Left Antler</td>\n" +
            $"\t\t\t<td class=\"baselabel\">Difference</td>\n" +
            $"\t\t</tr>\n" +
            $"\t\t<tr class=\"scoresheet-row\">\n" +
            $"\t\t\t<td class=\"baselabel\">B. Tip-to-tip spread</td>\n" +
            $"\t\t\t<td class=\"basevalue\">{Py.Fixed(Inch(Get(f, "b")), 4)}</td>\n" +
            $"\t\t\t<td class=\"baselabel\">C. Greatest spread</td>\n" +
            $"\t\t\t<td class=\"basevalue\">{Py.Fixed(Inch(Get(f, "c")), 4)}</td>\n" +
            $"\t\t</tr>\n" +
            $"\t\t<tr class=\"scoresheet-row\">\n" +
            $"\t\t\t<td class=\"baselabel\">D. Inside spread of main beams</td>\n" +
            $"\t\t\t<td class=\"basevalue\">{Py.Fixed(Inch(Get(f, "d")), 4)}</td>\n" +
            $"\t\t\t<td class=\"scoresheet-label\" colspan=\"2\">Spread credit is capped at the longer main beam</td>\n" +
            $"\t\t\t<td class=\"value\">{Py.Fixed(spread, 4)}</td>\n" +
            $"\t\t</tr>\n" +
            $"\t\t<tr class=\"scoresheet-row\">\n" +
            $"\t\t\t<td class=\"scoresheet-label\" colspan=\"4\">E. Total abnormal point length</td>\n" +
            $"\t\t\t<td class=\"empty\"></td>\n" +
            $"\t\t\t<td class=\"empty\"></td>\n" +
            $"\t\t\t<td class=\"empty\"></td>\n" +
            $"\t\t\t<td class=\"value\">{Py.Fixed(e, 4)}</td>\n" +
            $"\t\t</tr>\n" +
            $"{rows}\n" +
            $"\t\t<tr class=\"scoresheet-row\">\n" +
            $"\t\t\t<td class=\"empty\" colspan=\"3\"></td>\n" +
            $"\t\t\t<td class=\"scoresheet-label\">Total</td>\n" +
            $"\t\t\t<td class=\"value total\">{Py.Fixed(spread, 4)}</td>\n" +
            $"\t\t\t<td class=\"value total\">{Php(c2)}</td>\n" +
            $"\t\t\t<td class=\"value total\">{Php(c3)}</td>\n" +
            $"\t\t\t<td class=\"value total\">{Py.Fixed(diff + e, 4)}</td>\n" +
            $"\t\t</tr>\n" +
            $"\t</table>\n" +
            $"\t<table class=\"summary\">\n" +
            $"\t\t<tr class=\"scoresheet-row\">\n" +
            $"\t\t\t<td class=\"basesummarywide\"></td>\n" +
            $"\t\t\t<td class=\"basesummarywide\"></td>\n" +
            $"\t\t\t<td class=\"add\" rowspan=\"3\">ADD</td>\n" +
            $"\t\t\t<td class=\"summaryhead\">Column 1</td>\n" +
            $"\t\t\t<td class=\"value\">{Py.Fixed(spread, 4)}</td>\n" +
            $"\t\t</tr>\n" +
            $"\t\t<tr class=\"scoresheet-row\">\n" +
            $"\t\t\t<td class=\"empty\"></td>\n" +
            $"\t\t\t<td class=\"empty\"></td>\n" +
            $"\t\t\t<td class=\"scoresheet-label\">Column 2</td>\n" +
            $"\t\t\t<td class=\"value\">{Py.Fixed(c2, 4)}</td>\n" +
            $"\t\t</tr>\n" +
            $"\t\t<tr class=\"scoresheet-row\">\n" +
            $"\t\t\t<td class=\"empty\"></td>\n" +
            $"\t\t\t<td class=\"empty\"></td>\n" +
            $"\t\t\t<td class=\"scoresheet-label\">Column 3</td>\n" +
            $"\t\t\t<td class=\"value\">{Py.Fixed(c3, 4)}</td>\n" +
            $"\t\t</tr>\n" +
            $"\t\t<tr class=\"scoresheet-row\">\n" +
            $"\t\t\t<td class=\"empty\" colspan=\"2\"></td>\n" +
            $"\t\t\t<td class=\"scoresheet-label\" colspan=\"2\">Subtotal</td>\n" +
            $"\t\t\t<td class=\"value\">{Py.Fixed(sub, 4)}</td>\n" +
            $"\t\t</tr>\n" +
            $"\t\t<tr class=\"scoresheet-row\">\n" +
            $"\t\t\t<td class=\"empty\" colspan=\"2\"></td>\n" +
            $"\t\t\t<td class=\"scoresheet-label\" colspan=\"2\">Subtract Column 4</td>\n" +
            $"\t\t\t<td colspan=\"subvalue value\">{Py.Fixed(diff + e, 4)}</td>\n" +
            $"\t\t</tr>\n" +
            $"\t\t<tr class=\"scoresheet-row\">\n" +
            $"\t\t\t<td class=\"empty\" colspan=\"2\"></td>\n" +
            $"\t\t\t<td class=\"scoresheet-label\" colspan=\"2\" rowspan=\"2\">Total</td>\n" +
            $"\t\t\t<td class=\"value\" colspan=\"2\">{Py.Fixed(R4(spread + c2 + c3 - diff - e), 4)}</td>\n" +
            $"\t\t</tr>\n" +
            $"\t</table>\n" +
            $"</div>\n";
    }

    // tines sheets (T-1..T-n of the species: two for rusa / sambar, three for axis)
    static readonly Dictionary<int, (string Image, string[] Nums)> TINES_SHEET = new()
    {
        [44] = ("antlers_sambar_and_rusa_deer", ["1.1", "1.2", "1.2", "1.3", "1.4", "1.5"]),
        [45] = ("antlers_sambar_and_rusa_deer", ["1.1", "1.2", "1.2", "1.3", "1.4", "1.5"]),
        [54] = ("axis_deer_v2", ["1.", "2.1", "2.2", "2.3", "3.", "4.", "5."]),
    };
    static readonly Dictionary<int, string> HORN_IMAGE = new() { [19] = "horns_alpine_ibex", [40] = "feral_goat" };

    static string TinesRow(string label, string l, string r, string points) =>
        $"            <tr class=\"scoresheet-row\">\n" +
        $"                <td class=\"scoresheet-label\">{label}</td>\n" +
        $"                <td class=\"value\">{l}</td>\n" +
        $"                <td class=\"value\">{r}</td>\n" +
        $"                <td class=\"value\">{points}</td>\n" +
        $"            </tr>\n";

    /// <summary>The rows of a tines sheet: labels ("1.1 Length of Main Beam"…, the last one the inside span), the (left, right) millimetres of every row but the last, and the inside span in millimetres.</summary>
    static (List<string> Labels, List<(double L, double R)> Mm, double Span) TinesCalc(int species, JsonObject f)
    {
        var nums = TINES_SHEET[species].Nums;
        var (a, b) = (f["left"]!, f["right"]!);
        var n = nums.Length - 4;  // tine rows
        var rows = new List<string> { "Length of Main Beam" };
        for (var i = 1; i <= n; i++) rows.Add($"Length of Typical Tines T-{i}");
        rows.AddRange(["Circumference of Burr", "Circumference of Main Beam", "Inside Span of Main Beams"]);
        double Tine(JsonNode? s, int i) => ((JsonArray)s!["length_tines"]!) is var t && i < t.Count ? Py.D(t[i]) : 0;
        List<(double L, double R)> mm = [(N(a, "length_main_beam"), N(b, "length_main_beam"))];
        for (var i = 0; i < n; i++) mm.Add((Tine(a, i), Tine(b, i)));
        mm.Add((N(a, "circumference_burr"), N(b, "circumference_burr")));
        mm.Add((N(a, "circumference_main_beam"), N(b, "circumference_main_beam")));
        return ([.. rows.Select((r, i) => $"{nums[i]} {r}")], mm, N(f, "inside_span_main_beam"));
    }

    /// <summary>Left / Right / Points table (no total). Points are the two sides' millimetres added, then converted.</summary>
    static string TinesSheet(int species, JsonObject f, string cssClass, string name)
    {
        var image = TINES_SHEET[species].Image;
        var (labels, mm, span) = TinesCalc(species, f);
        var sb = new StringBuilder();
        for (var i = 0; i < mm.Count; i++)
            sb.Append(TinesRow(labels[i], Py.Fixed(Inch(mm[i].L), 4), Py.Fixed(Inch(mm[i].R), 4), Py.Fixed(Inch(mm[i].L + mm[i].R), 4)));
        sb.Append(TinesRow(labels[^1], "", "", Py.Fixed(Inch(span), 4)));
        return SheetHead(cssClass, OwnLogo, name, image) + $"\t\t\t\n" +
            $"\t\t\t<div class=\"clear\"></div>\n" +
            $"\t\t</div>\n" +
            $"\t        <table class=\"score\">\n" +
            $"            <tr class=\"scoresheet-row\">\n" +
            $"                <td class=\"head\"></td>\n" +
            $"                <td class=\"head\">\n                    Left\n                </td>\n" +
            $"                <td class=\"head\">\n                    Right\n                </td>\n" +
            $"                <td class=\"head\">\n                    Points\n                </td>\n" +
            $"            </tr>\n" +
            $"{sb}\n" +
            $"        </table>\n\t";
    }

    static string HornRow(string no, string label, string value, string rowspan, string total, string avg, string points) =>
        $"        <tr class=\"scoresheet-row\">\n" +
        $"            <td class=\"scoresheet-label\">{no}</td>\n" +
        $"            <td class=\"scoresheet-label\">{label}</td>\n" +
        $"            <td class=\"value\">{value}</td>\n" +
        (total == "" ? $"            <td class=\"scoresheet-label\"></td>\n            <td class=\"scoresheet-label\"></td>\n            <td class=\"value\"> x 1</td>\n            <td class=\"value\">{points}</td>\n"
            : $"            <td{rowspan} class=\"value\">{total}</td>\n            <td{rowspan} class=\"value\">{avg}</td>\n            <td{rowspan} class=\"value\"> x 1</td>\n            <td{rowspan} class=\"value\">{points}</td>\n") +
        $"        </tr>\n";

    /// <summary>CIC horn worksheet: lengths in 1/1000 cm, circumferences and spread in mm, beauty points 0-3.</summary>
    static string HornSheet(int species, JsonObject f, string cssClass, string name)
    {
        double ll = N(f, "ll"), rl = N(f, "rl"), lc = N(f, "lc"), rc = N(f, "rc"), g = N(f, "g");
        // the site rounds the average and the total half up at 3 decimals (PHP round pre-rounds, so 146.0215 gives 146.022)
        double Round3(double x) => (double)Math.Round((decimal)x, 3, MidpointRounding.AwayFromZero);
        var avg = Round3((ll + rl) / 2000);
        string Cm(double v) => $"{Php(v)} cm";
        string Beauty(string no, string label, string k) =>
            $"        <tr class=\"scoresheet-row\">\n" +
            $"            <td class=\"scoresheet-label\">{no}</td>\n" +
            $"            <td class=\"scoresheet-label\">{label}</td>\n" +
            $"            <td class=\"text-center\" colspan=\"4\">0-3 Points</td>\n" +
            $"            <td class=\"value\">{Php(Get(f, k))}</td>\n" +
            $"        </tr>\n";
        var (td, th) = ("            <td class=\"head nowrap\">\n                    ", "\n            </td>\n");
        return SheetHead(cssClass, OwnLogo, name, HORN_IMAGE[species]) + $"\t\t\t\n" +
            $"\t\t\t<div class=\"clear\"></div>\n" +
            $"\t\t</div>\n" +
            $"\t<div>\n" +
            $"    <table class=\"score\">\n" +
            $"        <tr class=\"scoresheet-row\">\n" +
            $"            <td colspan=\"2\" class=\"scoresheet-label\">\n            </td>\n" +
            $"{td}Measurement{th}{td}Total{th}{td}Average{th}{td}Factor{th}{td}Points{th}" +
            $"        </tr>\n" +
            $"        <tr class=\"scoresheet-row\">\n" +
            $"            <td class=\"scoresheet-label\">1.</td>\n" +
            $"            <td class=\"scoresheet-label\" colspan=\"6\"> Measurements</td>\n" +
            $"        </tr>\n" +
            HornRow("1.1", "Length of horn, left", Cm(ll / 1000), " rowspan=\"2\"", Cm((ll + rl) / 1000), Cm(avg), Php(avg)) +
            $"        <tr class=\"scoresheet-row\">\n" +
            $"            <td class=\"scoresheet-label\"></td>\n" +
            $"            <td class=\"scoresheet-label\">Length of horn, right</td>\n" +
            $"            <td class=\"value\">{Cm(rl / 1000)}</td>\n" +
            $"        </tr>\n" +
            HornRow("1.2", "Circumference of horn, left", Cm(lc / 10), "", "", "", Php(lc / 10)) +
            HornRow("", "Circumference of horn, right", Cm(rc / 10), "", "", "", Php(rc / 10)) +
            HornRow("1.3", "Greatest spread", Cm(g / 10), "", "", "", Php(g / 10)) +
            $"        <tr class=\"scoresheet-row\">\n" +
            $"            <td class=\"scoresheet-label\">2.</td>\n" +
            $"            <td class=\"scoresheet-label\" colspan=\"6\"> Beauty Points</td>\n" +
            $"        </tr>\n" +
            Beauty("2.1", "Colour", "col") + Beauty("2.2", "Age rings and ridges", "ar") + Beauty("2.3", "Horn curve", "hc") +
            $"        <tr class=\"scoresheet-row\">\n" +
            $"            <td class=\"text-right\" colspan=\"6\">Total</td>\n" +
            $"            <td class=\"value\">{Php(Round3((ll + rl) / 2000 + (lc + rc + g) / 10 + Get(f, "col") + Get(f, "ar") + Get(f, "hc")))}</td>\n" +
            $"        </tr>\n" +
            $"    </table>\n" +
            $"</div>\n\t";
    }

    /// <summary>The worked score sheet the score page shows between `widgetRoot scoresheet` and the shots table; null where unmapped.
    /// gender: 0 male, 1 female (a few species have one image per gender).</summary>
    public static string? SheetHtml(int species, string define, string name, JsonNode? features, JsonNode? weightGrams, JsonNode? gender)
    {
        FAMILY.TryGetValue(species, out var fam);
        if (fam == "bc_deer" && BC_LABELS.ContainsKey(species)) return BcSheet(species, features, define.ToLowerInvariant(), name);
        if (fam == "tines" && features is JsonObject tf && TINES_SHEET.ContainsKey(species)) return TinesSheet(species, tf, define.ToLowerInvariant(), name);
        if (fam == "cic_horn" && features is JsonObject hf && HORN_IMAGE.ContainsKey(species)) return HornSheet(species, hf, define.ToLowerInvariant(), name);
        if (fam is not ("weight_lb" or "weight_mg" or "weight_kg" or "weight_g")) return null;
        var weight = fam == "weight_lb" ? $"<div>\n" +
            $"\t<table class=\"score\">\n" +
            $"\t\t<tr class=\"scoresheet-row\">\n" +
            $"\t\t\t<td class=\"scoresheet-label\" rowspan=\"2\">Weight</td>\n" +
            $"\t\t\t<td class=\"basevalue\">{Py.Fixed(Lb(weightGrams), 4)}</td>\n" +
            $"\t\t</tr>\n" +
            $"\t</table>\n" +
            $"</div>\n" : "";
        var image = GENDERED_IMAGE.Contains(species) ? $"{define.ToLowerInvariant()}_{(Py.Truthy(gender) ? "female" : "male")}"
            : SHEET_IMAGE.GetValueOrDefault(species, define.ToLowerInvariant());
        return SheetHead(define.ToLowerInvariant(), OwnLogo, name, image) + $"\t\t\t<div class=\"clear\"></div>\n" +
            $"\t\t</div>\n" +
            $"{weight}";
    }
}
