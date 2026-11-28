using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>A catalogue and its case files, with one engine loaded once for all their cases (each case plays on an account of its own), as the server loads it: the catalogue's application JSON
/// and the operator's `mission_rules.json`. Every species' permitted ammunition is its catalogue's `ethicalAmmo`, every scale factor 1. `Engine` has `spawn_named_animals` off (a named animal is met
/// by its species near its place); `Placed`, loaded when a case asks for it, has it on (the server places each named animal and only that one meets its objective).</summary>
public sealed class CaseSource
{
    public readonly string CasesDir;
    public readonly State Engine;
    public readonly JsonObject App;
    /// <summary>Every objective of the catalogue: its title and the line `coverage --rules` prints for it (the rule the server judges it by).</summary>
    public readonly Dictionary<(long Mission, long Objective), (string Title, string Rule)> Objectives = [];
    readonly string dir;
    readonly Lazy<State> placed;

    public CaseSource(string dir, string casesDir, JsonObject app, string? rules)
    {
        (this.dir, CasesDir, App) = (dir, casesDir, app);
        var species = App["species"]!.AsArray().OfType<JsonObject>().ToDictionary(s => (long)s["id"]!);
        var connect = CONNECT;
        connect["ethical"] = new JsonArray([.. Enumerable.Range(0, 100).Select(i => (JsonNode?)(species.GetValueOrDefault(i)?["ethicalAmmo"]?.DeepClone() ?? new JsonArray()))]);
        connect["species"] = new JsonArray([.. Enumerable.Range(0, 100).Select(i => (JsonNode?)new JsonObject { ["scale_factor"] = 1.0, ["variant_id"] = i })]);
        var overrides = rules is null ? null : State.ReadMissionRules(rules);
        State Build(string at, bool place)
        {
            Directory.CreateDirectory(at);
            return new State(Path.Combine(at, "state.db"), Py.DumpBytes(connect), BASE, ICON, App, LOCALE, TEXTURES, offline: false, missionRules: overrides, spawnNamedAnimals: place);
        }
        Engine = Build(dir, false);
        rebuild = (at, rules) =>
        {
            Directory.CreateDirectory(at);
            return new State(Path.Combine(at, "state.db"), Py.DumpBytes(connect), BASE, ICON, App, LOCALE, TEXTURES, offline: false, missionRules: rules);
        };
        placed = new(() => Build(Path.Combine(dir, "placed"), true));
        var lines = Engine.RuleLines().Select(l => (Match: Regex.Match(l, @"\Amission (\d+) objective (\d+)"), Line: l)).ToDictionary(x => (long.Parse(x.Match.Groups[1].Value), long.Parse(x.Match.Groups[2].Value)), x => x.Line);
        foreach (var m in App["missions"]!.AsArray().OfType<JsonObject>())
            foreach (var o in m["objectives"]!.AsArray().OfType<JsonObject>())
                Objectives[((long)m["id"]!, (long)o["id"]!)] = ((string)o["title"]!, lines[((long)m["id"]!, (long)o["id"]!)]);
    }

    public State Placed => placed.Value;

    readonly Func<string, JsonObject?, State> rebuild;

    /// <summary>The catalogue's engine again under these operator's rules (none: the reader's alone), in a folder of its own beside the source's.</summary>
    public State Rebuild(string name, JsonObject? rules) => rebuild(Path.Combine(dir, name), rules);

    /// <summary>The population the placing engine gave the named animal of `species` nearest `at` ([x, z]; none: (0, 0)), from the file it keeps them in (`named_animals.json`: keys
    /// `reserve|species|gender|coat|x|z`).</summary>
    public long Population(long species, JsonArray? at, long mission = 0, long objective = 0)
    {
        _ = Placed;  // the server places them when it starts
        var file = Path.Combine(dir, "placed", "named_animals.json");
        var (x, z) = at is null ? (0.0, 0.0) : (Py.D(at[0]), Py.D(at[1]));
        double Far(string[] key) => Math.Abs(double.Parse(key[4], CultureInfo.InvariantCulture) - x) + Math.Abs(double.Parse(key[5], CultureInfo.InvariantCulture) - z);
        var all = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file))!.AsObject().Select(p => (Key: p.Key.Split('|'), Id: Py.L(p.Value))).ToList() : new List<(string[] Key, long Id)>();
        // the objective's own animal: the one at the point its rule names (several animals of a species are placed in a catalogue); else the nearest to `at`
        if (Objectives.TryGetValue((mission, objective), out var written) && Regex.Match(written.Rule, @"named animal near \((-?\d+), (-?\d+)\) m") is { Success: true } near
            && all.Where(p => p.Key.Length >= 6 && p.Key[1] == species.ToString(CultureInfo.InvariantCulture) && p.Key[4] == near.Groups[1].Value && p.Key[5] == near.Groups[2].Value).Select(p => (long?)p.Id).FirstOrDefault() is { } own)
            return own;
        return all.Where(p => p.Key.Length >= 6 && p.Key[1] == species.ToString(CultureInfo.InvariantCulture)).OrderBy(p => Far(p.Key)).Select(p => (long?)p.Id).FirstOrDefault()
            ?? throw new InvalidDataException($"the server placed no named animal of species {species}");
    }

    public IEnumerable<string> Files => Directory.GetFiles(CasesDir, "*.json").Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal);

    public JsonObject Mission(long id) => App["missions"]!.AsArray().OfType<JsonObject>().First(m => (long)m["id"]! == id);

    /// <summary>The id of the one entry of `list` whose name (or define) is `name`, the case not compared; the error names some that share a word with it.</summary>
    static long Named(IEnumerable<(long Id, string Name)> list, string name, string what)
    {
        var all = list.ToList();
        if (all.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Name: not null } found)
            return found.Id;
        var words = name.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        throw new InvalidDataException($"no {what} is named \"{name}\"; names that share a word: "
            + string.Join(", ", all.Where(x => words.Any(w => x.Name.ToLowerInvariant().Split(' ').Contains(w))).Select(x => "\"" + x.Name + "\"").Distinct().Take(12)));
    }

    IEnumerable<JsonObject> Of(string key) => App[key]!.AsArray().OfType<JsonObject>();

    /// <summary>A species (0 to 99) by name; two species of one name (a catalogue may have them) are told apart by the mission's `species` list, else the name is refused.</summary>
    public long Species(string name, long mission)
    {
        var all = Of("species").Where(s => (long)s["id"]! < 100).Select(s => ((long)s["id"]!, ((string)s["name"]!).Split(" (")[0])).ToList();
        var id = Named(all, name, "species");
        var same = all.Where(s => s.Item2.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(s => s.Item1).ToList();
        if (same.Count == 1)
            return id;
        var own = same.Intersect(Mission(mission)["species"]!.AsArray().Select(x => (long)x! % 100)).ToList();
        return own.Count == 1 ? own[0] : throw new InvalidDataException($"several species are named \"{name}\" ({string.Join(", ", same)}) and the mission lists {(own.Count == 0 ? "none" : "several")} of them");
    }

    public long Reserve(string name) => Named(Of("reserves").Select(r => ((long)r["id"]!, (string)r["name"]!)), name, "reserve");

    /// <summary>A weapon (`cat` 1), sight (2) or ammunition (3) by its catalogue name; `Name (Variant)` picks one variant of items that share a name.</summary>
    public long Item(string name, long cat, string what) => Named(Of("items").Where(i => Py.IsNum(i["cat"]) && (long)i["cat"]! == cat)
        .SelectMany(i => Py.IsStr(i["variationname"]) ? new[] { ((long)i["id"]!, (string)i["name"]!), ((long)i["id"]!, $"{(string)i["name"]!} ({(string)i["variationname"]!})") } : [((long)i["id"]!, (string)i["name"]!)]), name, what);

    /// <summary>A location of the catalogue (a lodge, a tower, a place) by its name (in the hunt's reserve first) or its define; null for none.</summary>
    public JsonObject? Location(string name, long? reserve) =>
        Of("locations").Where(l => Py.IsStr(l["name"]) && Py.IsStr(l["define"])).OrderBy(l => Py.IsNum(l["reserve"]) && (long)l["reserve"]! == reserve ? 0 : 1)
            .FirstOrDefault(l => ((string)l["name"]!).Equals(name, StringComparison.OrdinalIgnoreCase) || (string)l["define"]! == name);

    /// <summary>The name the game sends for a place (event 101 `location_name`): a catalogue location's define, else the case's text as it is (a place no catalogue lists, by the name the game sends).</summary>
    public string Place(string name, long? reserve) => Location(name, reserve)?["define"] is { } define ? (string)define! : name;

    /// <summary>The permitted ammunition a kill gets when the case names none: the species' first; 0 for a species with no list.</summary>
    public long DefaultAmmo(long species) => Of("species").FirstOrDefault(s => (long)s["id"]! == species)?["ethicalAmmo"] is JsonArray { Count: > 0 } a ? (long)a[0]! : 0;
}

/// <summary>The two sources of cases, each loaded on first use. `sample`: the invented catalogue of server/Server.Tests/mission-cases (`catalogue.json`, an application fragment over the test
/// fixture's, its `mission_rules.json` and its `cases/`), committed so CI proves the runner. `real`: the operator's catalogue and the real cases, never committed here: MISSION_CASES is the folder of
/// case files, MISSION_CATALOGUE the catalogue's application JSON, MISSION_RULES the operator's mission_rules.json (optional: without it the reader's rules alone).</summary>
public sealed class MissionCaseSources : IDisposable
{
    public static readonly string SampleDir = Path.Combine(AppContext.BaseDirectory, "mission-cases");
    static readonly (string? Cases, string? Catalogue, string? Rules) Env =
        (Environment.GetEnvironmentVariable("MISSION_CASES"), Environment.GetEnvironmentVariable("MISSION_CATALOGUE"), Environment.GetEnvironmentVariable("MISSION_RULES"));

    /// <summary>Why the real cases cannot run (they are skipped), or null.</summary>
    public static readonly string? RealMissing =
        !Directory.Exists(Env.Cases) || !File.Exists(Env.Catalogue) || !string.IsNullOrEmpty(Env.Rules) && !File.Exists(Env.Rules)
            ? "the real mission cases need MISSION_CASES (their folder), MISSION_CATALOGUE (the catalogue's application JSON) and, for the operator's rules, MISSION_RULES (mission_rules.json): docs/mission-cases.md"
            : null;

    readonly string dir = Directory.CreateTempSubdirectory("mission-cases").FullName;
    readonly Lazy<CaseSource> sample, real;

    public MissionCaseSources()
    {
        sample = new(() =>
        {
            var app = APP;
            foreach (var (key, value) in JsonNode.Parse(File.ReadAllText(Path.Combine(SampleDir, "catalogue.json")))!.AsObject())
                if (app[key] is JsonArray list)  // an entry replaces the fixture's of its id
                    foreach (var entry in value!.AsArray().OfType<JsonObject>())
                    {
                        list.Where(x => Py.Eq(x!["id"], entry["id"])).ToList().ForEach(x => list.Remove(x));
                        list.Add(entry.DeepClone());
                    }
                else app[key] = value!.DeepClone();
            return new CaseSource(Path.Combine(dir, "sample"), Path.Combine(SampleDir, "cases"), app, Path.Combine(SampleDir, State.MissionRulesFile));
        });
        real = new(() => new CaseSource(Path.Combine(dir, "real"), Env.Cases!, JsonNode.Parse(File.ReadAllText(Env.Catalogue!))!.AsObject(), string.IsNullOrEmpty(Env.Rules) ? null : Env.Rules));
    }

    public CaseSource this[string source] => source == "sample" ? sample.Value : real.Value;

    public static IEnumerable<string> RealFiles => RealMissing is null ? Directory.GetFiles(Env.Cases!, "*.json").Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal) : [];

    public void Dispose() => DeleteDir(dir);
}

/// <summary>Every mission objective's cases (docs/mission-cases.md), authored from its wording: a scenario that must finish it, others that must too and near misses that must not, each played on a
/// fresh account through the real engine against the catalogue's rules. A known disagreement is reported, not failed; one that agrees again fails until its mark is removed.</summary>
public class MissionCaseTests(MissionCaseSources sources, ITestOutputHelper output) : MissionTestBase, IClassFixture<MissionCaseSources>
{
    static long accounts = 1_000_000;
    double now = 1_800_000_000;
    CaseSource c = null!;

    /// <summary>A row per case file: the sample's, then the real ones (one row that is skipped when they are not set).</summary>
    public static TheoryData<string, string> Files =>
    [
        .. Directory.GetFiles(Path.Combine(MissionCaseSources.SampleDir, "cases"), "*.json").Select(f => ("sample", Path.GetFileName(f))).Order(),
        .. MissionCaseSources.RealMissing is null ? MissionCaseSources.RealFiles.Select(f => ("real", f)) : [("real", "")],
    ];

    JsonArray Load(string file) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(c.CasesDir, file)), documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, AllowDuplicateProperties = false })
            as JsonArray ?? throw new InvalidDataException($"{file}: a list of objective entries");

    /// <summary>The source's engine, or the test is skipped when it is the real one and it is not set.</summary>
    void Use(string source)
    {
        if (source == "real" && MissionCaseSources.RealMissing is { } why)
            Assert.Skip(why);
        c = sources[source];
    }

    public static string Hash(string title) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(title)))[..8];

    static (long Mission, long Objective) Key(JsonObject e) => ((long)e["mission"]!, (long)e["objective"]!);

    /// <summary>The reason of a `known` or `passKnown` mark: a text. Anything else is a problem line of the file (the mark is then taken as none).</summary>
    static string? Reason(JsonNode? mark, string where, List<string> problems)
    {
        if (mark is null)
            return null;
        if (mark is JsonValue v && v.TryGetValue<string>(out var reason))
            return reason;
        problems.Add($"{where}: a known mark is the reason as a text, not {mark.ToJsonString()}");
        return null;
    }

    [Theory, MemberData(nameof(Files))]
    public void TestEveryCaseOfTheFileGetsTheVerdictItsWordingAsks(string source, string file)
    {
        Use(source);
        Check(file);
    }

    void Check(string file)
    {
        var entries = Load(file).OfType<JsonObject>().ToList();
        var passes = entries.Where(e => e["pass"] is JsonArray).DistinctBy(Key).ToDictionary(Key, e => e["pass"]!.AsArray());
        var (problems, known, doubts) = (new List<string>(), new List<string>(), new List<string>());
        var (cases, started, seen) = (0, DateTime.UtcNow, new HashSet<(long, long)>());
        foreach (var e in entries)
        {
            var key = Key(e);
            if (!seen.Add(key))
            {
                problems.Add($"mission {key.Mission} objective {key.Objective}: two entries in {file}, only the first is read");
                continue;
            }
            if (!c.Objectives.TryGetValue(key, out var objective))
            {
                problems.Add($"mission {key.Item1} objective {key.Item2}: the catalogue has no such objective");
                continue;
            }
            var about = $"mission {key.Item1} objective {key.Item2} \"{objective.Title}\"";
            if (Py.Str(e["hash"]) != Hash(objective.Title))
            {
                problems.Add($"{about}: the hash is \"{Py.Str(e["hash"])}\", the title's is \"{Hash(objective.Title)}\": the title changed (or the hash is wrong), read the cases again");
                continue;
            }
            var variants = (e["also"] as JsonArray ?? []).Concat(e["fail"] as JsonArray ?? []).OfType<JsonObject>();
            if (e.Select(p => p.Key).Where(k => !EntryKeys.Contains(k)).Concat(variants.SelectMany(v => v.Select(p => p.Key)).Where(k => !VariantKeys.Contains(k))).FirstOrDefault() is { } bad)
            {
                problems.Add($"{about}: no key `{bad}`: an objective's are {string.Join(", ", EntryKeys)}, a variant's (also, fail) {string.Join(", ", VariantKeys)}");
                continue;
            }
            if (e["doubt"] is { } doubt)
                doubts.Add($"{about}: {doubt}");
            var all = new List<(string Name, bool Done, JsonObject? Variant, string? Known)>();
            if (e["pass"] is JsonArray)
            {
                all.Add(("pass", true, null, Reason(e["passKnown"], $"{about}, pass", problems)));
                foreach (var (list, done) in new[] { ("also", true), ("fail", false) })
                    foreach (var (variant, i) in (e[list] as JsonArray ?? []).OfType<JsonObject>().Select((f, i) => (f, i)))
                        all.Add(($"{list} {i + 1} ({Py.Str(variant["why"])})", done, variant, Reason(variant["known"], $"{about}, {list} {i + 1}", problems)));
            }
            else if (e["doubt"] is null)
                problems.Add($"{about}: no pass case and no doubt");
            foreach (var (name, expected, variant, mark) in all)
            {
                cases++;
                bool done;
                var placed = (variant?["placed"] ?? e["placed"]) is { } placedMark && (bool)placedMark;  // the server places the named animals (spawn_named_animals on)
                try
                {
                    done = Play(key.Item1, key.Item2, variant is null ? e["pass"]!.AsArray() : Variant(e["pass"]!.AsArray(), variant), passes, placed);
                }
                catch (Exception x) when (x is InvalidDataException or InvalidOperationException or InvalidCastException or FormatException or KeyNotFoundException)
                {
                    problems.Add($"{about}, {name}: the case cannot be played: {x.Message}");
                    continue;
                }
                var verdict = $"{about}, {name}: the wording says {(expected ? "done" : "not done")}, the server says {(done ? "done" : "not done")}\n    rule: {objective.Rule}";
                if (done != expected && mark is null) problems.Add(verdict);
                else if (done != expected) known.Add($"{verdict}\n    known: {mark}");
                else if (mark is not null) problems.Add($"{about}, {name}: marked known (\"{mark}\") but the server agrees now: remove the mark");
            }
        }
        output.WriteLine($"{file}: {cases} cases of {entries.Count} objectives in {(DateTime.UtcNow - started).TotalSeconds:0.0} s; {known.Count} known disagreements, {doubts.Count} doubts");
        foreach (var k in known) output.WriteLine("KNOWN " + k);
        foreach (var d in doubts) output.WriteLine("DOUBT " + d);
        Assert.True(problems.Count == 0, string.Join("\n\n", problems));
    }

    [Theory, InlineData("sample"), InlineData("real")]
    public void TestEveryObjectiveOfTheCatalogueHasItsCasesInOneFileOnly(string source)
    {
        Use(source);
        var seen = new Dictionary<(long, long), string>();
        var twice = new List<string>();
        foreach (var file in c.Files)
            foreach (var e in Load(file).OfType<JsonObject>())
                if (!seen.TryAdd(((long)e["mission"]!, (long)e["objective"]!), file))
                    twice.Add($"mission {e["mission"]} objective {e["objective"]}: in {seen[((long)e["mission"]!, (long)e["objective"]!)]} and {file}");
        var missing = c.Objectives.Keys.Where(k => !seen.ContainsKey(k)).OrderBy(k => k).ToList();
        output.WriteLine($"{c.Objectives.Count - missing.Count} of {c.Objectives.Count} objectives have cases; {missing.Select(k => k.Mission).Distinct().Count()} missions have an objective without");
        Assert.True(twice.Count == 0, string.Join("\n", twice));
        Assert.True(missing.Count == 0, "objectives without cases:\n" + string.Join("\n", missing.Select(k => $"mission {k.Mission} objective {k.Objective} \"{c.Objectives[k].Title}\" hash {Hash(c.Objectives[k].Title)}")));
    }

    [Fact]
    public void TestAnEntryTwiceInOneFileAndAKnownMarkThatIsNoTextAreProblemLinesOfTheFile()
    {
        var cases = Directory.CreateDirectory(Path.Combine(tmp, "bad-cases")).FullName;
        var title = sources["sample"].Objectives[(2, 0)].Title;  // a spot of a deer
        string Entry(string more = "") => $$"""{"mission": 2, "objective": 0, "hash": "{{Hash(title)}}", "pass": [{"spot": "Test Deer"}]{{more}}}""";
        File.WriteAllText(Path.Combine(cases, "twice.json"), $"[{Entry()}, {Entry()}]");
        File.WriteAllText(Path.Combine(cases, "known.json"), "[" + Entry(""", "passKnown": true, "also": [{"why": "the same", "set": {"spot": "Test Deer"}, "known": 5}]""") + "]");
        c = new CaseSource(Path.Combine(tmp, "bad-source"), cases, sources["sample"].App, null);
        Assert.Contains("mission 2 objective 0: two entries in twice.json, only the first is read", Assert.ThrowsAny<Exception>(() => Check("twice.json")).Message);
        var known = Assert.ThrowsAny<Exception>(() => Check("known.json")).Message;
        Assert.Contains("pass: a known mark is the reason as a text, not true", known);
        Assert.Contains("also 1: a known mark is the reason as a text, not 5", known);
    }

    /// <summary>A variant's steps (`also`, `fail`): its own `steps`, or the pass's with the keys of `set` replaced (null removes one) in the step `step` (default: the last one).</summary>
    static JsonArray Variant(JsonArray pass, JsonObject variant)
    {
        if (variant["steps"] is JsonArray own)
            return variant["set"] is null ? own : throw new InvalidDataException("a variant has `set` or `steps`, not both");
        if (variant["set"] is not JsonObject set)
            throw new InvalidDataException("a variant has `set` (the one thing changed) or `steps`");
        var steps = (JsonArray)pass.DeepClone();
        var at = variant["step"] is { } n ? (int)n : steps.Count - 1;
        var step = steps[at] as JsonObject ?? throw new InvalidDataException($"the pass has no step {at}");
        foreach (var (k, v) in set)
            if (v is null) step.Remove(k);
            else step[k] = v.DeepClone();
        return steps;
    }

    static readonly string[] EntryKeys = ["mission", "objective", "hash", "doubt", "placed", "pass", "passKnown", "also", "fail"], VariantKeys = ["why", "placed", "set", "step", "steps", "known"];

    static readonly Dictionary<string, string[]> Keys = new()
    {
        ["hunt"] = ["hunt", "lodge"], ["wait"] = ["wait"], ["enter"] = ["enter", "at"], ["leave"] = ["leave", "at"], ["done"] = ["done"],
        ["harvest"] = ["harvest", "gender", "weapon", "ammo", "sight", "distance", "hits", "state", "pose", "stand", "value", "weight", "score", "points", "abnormal", "nonTypical", "beard", "spurs",
                       "tusks", "coat", "place", "at", "named", "population", "animal", "shot"],
        ["spot"] = ["spot", "gender", "coat", "stand", "place", "animal"],
        ["clue"] = ["clue", "kind", "gender", "coat", "place", "at", "animal"],
    };

    // the `animal_state` of a hit by name (docs/server.md, "Conditions of a harvest"); a number is the state itself
    static readonly Dictionary<string, long> States = new()
    {
        ["standing"] = 3, ["walking"] = 4, ["running"] = 5, ["flying"] = 6, ["swimming"] = 7, ["charging"] = 9, ["trotting"] = 10, ["flying unspooked"] = 11, ["burrow"] = 13,
    };

    static readonly Dictionary<string, long> ClueTypes = new() { ["blood"] = 0, ["droppings"] = 1, ["footprints"] = 2, ["call"] = 9, ["bedding"] = 12, ["other"] = 20 };

    static string Kind(JsonObject step) => Keys.Keys.FirstOrDefault(step.ContainsKey) ?? throw new InvalidDataException($"a step is one of {string.Join(", ", Keys.Keys)}: {step.ToJsonString()}");

    /// <summary>The steps with each `done` replaced by the pass steps of that objective of the mission; the objectives so done are added to `required`.</summary>
    static IEnumerable<JsonObject> Expand(long mission, JsonArray steps, Dictionary<(long, long), JsonArray> passes, HashSet<long> required, int depth = 0)
    {
        if (depth > 20)
            throw new InvalidDataException("`done` steps go round in a circle");
        foreach (var s in steps)
        {
            var step = s as JsonObject ?? throw new InvalidDataException("a step is an object");
            var kind = Kind(step);
            if (step.Select(p => p.Key).FirstOrDefault(k => !Keys[kind].Contains(k)) is { } bad)
                throw new InvalidDataException($"a {kind} step has no `{bad}`: it takes {string.Join(", ", Keys[kind])}");
            if (kind != "done")
                yield return step;
            else if (passes.TryGetValue((mission, (long)step["done"]!), out var earlier))
            {
                required.Add((long)step["done"]!);
                foreach (var e in Expand(mission, earlier, passes, required, depth + 1))
                    yield return e;
            }
            else
                throw new InvalidDataException($"`done: {step["done"]}`: that objective has no pass case in this file");
        }
    }

    long objectiveNow;  // the objective the case being played is about
    // ponytail: one engine, one case at a time (about 5 ms each, the heartbeat most of it); the cases of a file in parallel need a clock per case (State.Clock is one)
    /// <summary>Plays the steps on a fresh account whose only active mission is `mission` (a hunt in the mission's first reserve first, unless the steps start with one); is the objective done, and
    /// still every objective a `done` step did (a single-hunt mission forgets them at the next hunt)?</summary>
    bool Play(long mission, long objective, JsonArray written, Dictionary<(long, long), JsonArray> passes, bool placed)
    {
        objectiveNow = objective;
        var s = placed ? c.Placed : c.Engine;
        s.Clock = () => now;
        var account = Interlocked.Increment(ref accounts);
        s.CreateAccount(account, "case" + account);
        using (var con = s.Store.Open(account))
        {
            con.Begin();
            PlayerRows.SetExtra(con, "Mission_missions.json", O($$"""{"active": [{{mission}}]}"""));
            con.Commit();
        }
        var required = new HashSet<long> { objective };
        var steps = Expand(mission, written, passes, required).ToList();
        long? reserve = null;
        if (steps.FirstOrDefault()?.ContainsKey("hunt") != true && c.Mission(mission)["reserves"] is JsonArray { Count: > 0 } reserves)
            Hunt((long)reserves[0]!, null);
        var (animals, individual, shot) = (new Dictionary<string, int>(), 0, 0);
        JsonArray? standingAt = null;
        int Animal(JsonObject step) => step["animal"] is { } label ? animals.TryGetValue(label.ToString(), out var a) ? a : animals[label.ToString()] = ++individual : ++individual;
        void Hunt(long r, string? lodge)
        {
            reserve = r;
            var start = lodge is null ? "" : (c.Location(lodge, r) ?? throw new InvalidDataException($"no location is named \"{lodge}\""))["id"]!.ToString();
            s.NoteLaunch(account, Encoding.ASCII.GetBytes($"start={start}&reserve={r}"));
        }
        JsonNode Arrival(JsonObject step, string place, string direction)
        {
            var e = Enter(c.Place(place, reserve), direction);
            if (step["at"] is JsonArray at)
                (e["player_x"], e["player_z"]) = (at[0]!.DeepClone(), at[1]!.DeepClone());
            return e;
        }
        foreach (var step in steps)
        {
            now += 1;
            List<JsonNode> events;
            switch (Kind(step))
            {
                case "hunt":
                    Hunt(c.Reserve((string)step["hunt"]!), (string?)step["lodge"]);
                    continue;
                case "wait":
                    now += Py.D(step["wait"]) * 60;
                    continue;
                case "enter" or "leave":
                    events = [Arrival(step, (string)(step["enter"] ?? step["leave"])!, step.ContainsKey("enter") ? "enter" : "leave")];
                    standingAt = step["at"] as JsonArray ?? standingAt;
                    break;
                case "spot":
                    events = [.. Spot(Animal(step), (int)c.Species((string)step["spot"]!, mission), Gender(step), (int)Pick(step["stand"], State.StandNames, "stand"), Coat(step))];
                    break;
                case "clue":
                    var kind = step["kind"]?.ToString() ?? "footprints";
                    var type = ClueTypes.TryGetValue(kind, out var t) ? t : throw new InvalidDataException($"a clue's kind is one of {string.Join(", ", ClueTypes.Keys)}, not \"{kind}\"");
                    events = [.. Clue(Animal(step), (int)type, (int)c.Species((string)step["clue"]!, mission), Gender(step))];
                    events[0]["texture"] = Coat(step);
                    if ((step["at"] as JsonArray ?? standingAt) is { } where)  // where the clue is found (x, z in metres): `at`, else where the player stood at the last place he entered with an `at`
                        (events[1]["x"], events[1]["z"]) = ((long)where[0]!, (long)where[1]!);
                    break;
                default:
                    events = Harvest(step, mission, Animal(step), () => ++shot, placed);
                    break;
            }
            if (step["place"] is { } place)
                events.Insert(1, Arrival(new JsonObject(), place.ToString(), "enter"));  // after the spawn: the player is there at the spot, the clue or the killing hit
            s.HeartbeatReply(account, Events([.. events]));
        }
        var state = Reply(s.MissionsReply(account)!)["states"]![mission.ToString()];
        return state is not null && ((long)state["state"]! == 3 || required.All(r => state["objectives"]!.AsArray().Any(o => (long)o! == r)));
    }

    static int Gender(JsonObject step) => step["gender"]?.ToString() switch
    {
        null or "male" => 0,
        "female" => 1,
        var g => throw new InvalidDataException($"a gender is male or female, not \"{g}\""),
    };

    static long Pick(JsonNode? name, string[] names, string what) =>
        name is null ? 0 : Array.IndexOf(names, name.ToString()) is >= 0 and var i ? i : throw new InvalidDataException($"a {what} is one of {string.Join(", ", names)}, not \"{name}\"");

    /// <summary>A coat by the game's name of it (`common`, `albino`, `blonde` ...; the first id of that name) or `coat <id>`.</summary>
    static int Coat(JsonObject step)
    {
        if (step["coat"]?.ToString() is not { } name)
            return 0;
        if (name.StartsWith("coat ", StringComparison.Ordinal) && int.TryParse(name[5..], out var id))
            return id;
        return Enumerable.Range(0, 200).FirstOrDefault(i => State.CoatName(i) == name, -1) is >= 0 and var found ? found : throw new InvalidDataException($"no coat is named \"{name}\" (or give `coat <id>`)");
    }

    /// <summary>An organ mask: organ names joined by `+` (`heart`, `left lung+right lung`, `skull`; State.OrganBits).</summary>
    static long Organs(string words) => words.Split('+').Select(w => w.Trim()).Aggregate(0L, (mask, w) =>
        mask | (State.OrganBits.FirstOrDefault(o => o.Name == w) is { Name: not null } o ? 1L << o.Bit : throw new InvalidDataException($"no organ is named \"{w}\"")));

    static readonly string[] HitKeys = ["organs", "weapon", "ammo", "sight", "distance", "state", "stand", "pose", "shot"];

    static long StateOf(JsonNode? state) => state switch
    {
        null => 3,
        JsonValue v when v.TryGetValue<long>(out var n) => n,
        var name => States.TryGetValue(name.ToString(), out var n) ? n : throw new InvalidDataException($"a state is one of {string.Join(", ", States.Keys)} or a number, not \"{name}\""),
    };

    /// <summary>A pair of lengths in millimetres: one number for both sides, or `[left, right]`.</summary>
    static (double Left, double Right)? Pair(JsonNode? n) => n switch
    {
        null => null,
        JsonArray { Count: 2 } a => (Py.D(a[0]), Py.D(a[1])),
        JsonArray => throw new InvalidDataException("a pair of lengths is one number or [left, right]"),
        var one => (Py.D(one), Py.D(one)),
    };

    /// <summary>A kill: the spawn, its trophy event when the step has one, the hits and the harvest. Every hit has the step's weapon, ammunition, sight, distance (to the millimetre), state, stand
    /// and pose, unless an entry of `hits` that is an object changes them for that hit; the last hit kills. `placed`: the server places the named animals, so `named` is the placed animal.</summary>
    List<JsonNode> Harvest(JsonObject step, long mission, int individual, Func<int> nextShot, bool placed)
    {
        var species = c.Species((string)step["harvest"]!, mission);
        var scored = step["nonTypical"] is { } nt && (bool)nt ? species + 100 : species;  // a non-typical rack is the species' variant 100 up, which never has typical points
        static JsonObject Hit(JsonNode? h) => h is JsonObject o
            ? o.Select(p => p.Key).FirstOrDefault(k => !HitKeys.Contains(k)) is { } bad ? throw new InvalidDataException($"a hit has no `{bad}`: it takes {string.Join(", ", HitKeys)}") : o
            : new JsonObject { ["organs"] = h!.ToString() };
        List<JsonObject> hits = step["hits"] switch
        {
            null => [new JsonObject()],
            JsonArray each => [.. each.Select(Hit)],
            var one => [Hit(one)],
        };
        JsonNode? Of(JsonObject hit, string key) => hit.ContainsKey(key) ? hit[key] : step[key];
        var at = step["at"] as JsonArray;
        var events = Shot(individual, value: (int)Math.Round(Py.D(step["value"] ?? 100) * 1000), special: !placed && step["named"] is { } named && (bool)named ? 1 : 0, species: (int)species,
            x: at is null ? 0 : (int)Math.Round(Py.D(at[0])), z: at is null ? 0 : (int)Math.Round(Py.D(at[1])), gender: Gender(step),
            organs: [.. hits.Select(h => Organs(h["organs"]?.ToString() ?? "left lung+right lung"))], texture: Coat(step)).ToList();
        for (var i = 0; i < hits.Count; i++)
        {
            var (hit, e) = (hits[i], events[1 + i]);
            var cms = Math.Round(Py.D(Of(hit, "distance") ?? 50) * 1000) / 10;  // metres to the millimetre, as centimetres
            e["distance_cms"] = cms == Math.Floor(cms) ? JsonValue.Create((long)cms) : JsonValue.Create(cms);
            e["weapon_id"] = Of(hit, "weapon") is { } w ? c.Item(w.ToString(), 1, "weapon") : 0;
            e["ammunition_id"] = Of(hit, "ammo") is { } a ? c.Item(a.ToString(), 3, "ammunition") : c.DefaultAmmo(species);
            e["scope_id"] = Of(hit, "sight") is { } sight ? c.Item(sight.ToString(), 2, "sight") : -1;
            e["animal_state"] = StateOf(Of(hit, "state"));
            e["location"] = Pick(Of(hit, "stand"), State.StandNames, "stand");
            e["pose"] = 2048 + Pick(Of(hit, "pose"), State.PoseNames, "pose");
            e["shot_id"] = Of(hit, "shot") is { } id ? (int)id : nextShot();
        }
        var (e7, e3) = (events[0].AsObject(), events[^1].AsObject());
        e3["extended_species"] = scored;
        if (step["weight"] is { } kg)
            e7["weight_grams"] = Py.D(kg) * 1000;
        if (step["population"] is { } population)
            e7["population"] = population.DeepClone();
        else if (placed && step["named"] is { } placedNamed && (bool)placedNamed)
            e7["population"] = c.Population(species, at, mission, objectiveNow);
        var family = Scoring.FAMILY.GetValueOrDefault((int)scored);
        var weighed = family is "weight_lb" or "weight_kg" or "weight_g" or "weight_mg" or "turkey" or "tusks";
        var measured = step["score"] is not null && family is not null && !weighed && family != "skull";  // antlers, horns, a tail: a trophy measured to the score
        if (measured && (step["beard"] ?? step["spurs"] ?? step["tusks"]) is not null)
            throw new InvalidDataException("a measured trophy's `score` with a beard, spurs or tusks is not expressible");
        var rack = measured || step["points"] is not null || step["abnormal"] is not null;
        var (typical, abnormal) = (step["points"] is { } p ? (int)p : 8, step["abnormal"] is { } ab ? (int)ab : 0);
        JsonObject Trophy(double k)
        {
            var t = !rack ? new JsonObject { ["event"] = 22, ["individual"] = individual } : measured ? Rack(individual, typical, abnormal, k) : Antlers(individual, typical, abnormal);
            if (Pair(step["tusks"]) is { } tusks)  // a tusk is the first point of its side
                (t["left_antler"], t["right_antler"]) = (new JsonObject { ["points"] = new JsonArray(new JsonObject { ["length"] = tusks.Left, ["type"] = 0 }) },
                                                          new JsonObject { ["points"] = new JsonArray(new JsonObject { ["length"] = tusks.Right, ["type"] = 0 }) });
            if (step["beard"] is { } beard)
                t["beard"] = Py.D(beard) * 1000;
            if (Pair(step["spurs"]) is { } spurs)
                (t["left_spur"], t["right_spur"]) = (spurs.Left * 1000, spurs.Right * 1000);
            return t;
        }
        var trophy = Trophy(1);
        if (step["score"] is { } score)
        {
            if (weighed && step["weight"] is not null)
                throw new InvalidDataException("a harvest of a species scored by its weight has a `weight` or a `score`, not both: it gets the weight of its score");
            var spawn = (JsonObject)e7.DeepClone();
            JsonObject With(string key, JsonNode value)
            {
                var o = (JsonObject)spawn.DeepClone();
                o[key] = value;
                return o;
            }
            var weighedTrophy = trophy;
            Func<double, (JsonObject E7, JsonObject E22)> build = family switch
            {
                null => throw new InvalidDataException("this species has no score"),
                "skull" => k => (With("feature", ((long)k << 16) | (long)k), weighedTrophy),
                _ when weighed => k => (With("weight_grams", k), weighedTrophy),
                _ => k => (spawn, Trophy(k)),
            };
            var (scoredSpawn, scoredTrophy) = build(Scored((int)scored, score, build, family == "skull" ? 65535 : weighed ? 10_000_000 : 100, family == "skull", StateOf(Of(hits[^1], "state"))));
            foreach (var key in new[] { "weight_grams", "feature" })
                if (scoredSpawn[key] is { } v)
                    e7[key] = v.DeepClone();
            trophy = scoredTrophy;
        }
        if (trophy.Count > 2)
            events.Insert(1, trophy);
        return events;
    }

    /// <summary>A rack with no score asked: every point 500 mm and every abnormal point 100 mm long, nothing else measured. Cases written before `score` measured a rack rely on this shape.</summary>
    static JsonObject Antlers(int individual, int typical, int abnormal)
    {
        static JsonObject Side(int points, int abnormals) => new()
        {
            ["points"] = new JsonArray([.. Enumerable.Range(0, points).Select(i => (JsonNode?)new JsonObject { ["length"] = 500, ["type"] = i })]),
            ["abnormals"] = new JsonArray([.. Enumerable.Range(0, abnormals).Select(_ => (JsonNode?)new JsonObject { ["length"] = 100 })]),
        };
        return new JsonObject { ["event"] = 22, ["individual"] = individual, ["left_antler"] = Side((typical + 1) / 2, (abnormal + 1) / 2), ["right_antler"] = Side(typical / 2, abnormal / 2) };
    }

    /// <summary>The trophy event of a measured rack, pair of horns or tail: `typical` points (the main beams among them; the left side has the odd one), `abnormal` non-typical points, and every
    /// length, circumference, spread and weight of a middling trophy times `k`. Each family's score reads its own part of it (Scoring.Features); every one grows with `k`. The measures are of
    /// different sizes so that, as `k` grows, they reach their next whole millimetre one after another, not all at once: the score climbs in small steps.</summary>
    static JsonObject Rack(int individual, int typical, int abnormal, double k)
    {
        JsonObject Side(int points, int abnormals)
        {
            var side = new JsonObject
            {
                ["points"] = new JsonArray([.. Enumerable.Range(0, points).Select(i => (JsonNode?)new JsonObject { ["length"] = (i == 0 ? 607 : 193 + 13 * i) * k, ["type"] = i })]),
                ["abnormals"] = new JsonArray([.. Enumerable.Range(0, abnormals).Select(i => (JsonNode?)new JsonObject { ["length"] = (97 + 7 * i) * k })]),
            };
            foreach (var (key, mm) in new[] { ("circumference_h1", 101), ("circumference_h2", 103), ("circumference_h3", 107), ("circumference_h4", 109), ("palm_length", 307), ("palm_width", 211),
                                               ("brow_palm_width", 113), ("top_palm_width", 127), ("top_palm_length", 131) })
                side[key] = mm * k;
            return side;
        }
        var rack = new JsonObject { ["event"] = 22, ["individual"] = individual, ["left_antler"] = Side((typical + 1) / 2, (abnormal + 1) / 2), ["right_antler"] = Side(typical / 2, abnormal / 2) };
        foreach (var (key, mm) in new[] { ("greatest_spread", 503), ("tip_to_tip_spread", 401), ("inside_spread_of_main_beams", 307), ("weight", 1009), ("volume", 311), ("tail", 409),
                                           ("left_spur", 20011), ("right_spur", 20011) })
            rack[key] = mm * k;
        return rack;
    }

    /// <summary>How far a score the harness sets may be from the one the case asks: a trophy's measures are whole millimetres, so a measured trophy's score climbs in steps of up to a few tenths.</summary>
    const double ScoreTolerance = 0.5;

    /// <summary>The knob (a weight in grams, a skull's size, a rack's size) whose kill the server's own scoring scores as `spec` asks: a number, the score nearest it; `>=N`, `>N`, `<=N` or `<N`,
    /// the score nearest N on that side, N itself when a knob reaches it. Halving works because every score grows with its knob; a score further than ScoreTolerance from N refuses the case.</summary>
    static double Scored(int species, JsonNode spec, Func<double, (JsonObject E7, JsonObject E22)> build, double top, bool whole, long animalState)
    {
        var text = spec.ToString().Trim();
        var op = text.StartsWith(">=", StringComparison.Ordinal) || text.StartsWith("<=", StringComparison.Ordinal) ? text[..2] : text.Length > 0 && text[0] is '>' or '<' ? text[..1] : "";
        if (!double.TryParse(text[op.Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out var target))
            throw new InvalidDataException($"a score is a number, or >=, >, <= or < and a number, not \"{text}\"");
        double S(double k)
        {
            var (e7, e22) = build(k);
            return Scoring.Features(species, e7, e22, JsonValue.Create(1.0)) is { } f && Scoring.Score(species, f, e7["weight_grams"], animalState).Score is { } s && Py.IsNum(s)
                ? Py.D(s) : double.NegativeInfinity;
        }
        // the least knob that is ok, when ok holds from some knob up (`up`); else the greatest, when it holds up to some knob
        double Edge(Func<double, bool> ok, bool up)
        {
            var (lo, hi) = (0.0, top);
            for (var i = 0; i < 200 && hi - lo > (whole ? 1 : 1e-9); i++)
            {
                var mid = whole ? Math.Floor((lo + hi) / 2) : (lo + hi) / 2;
                if (ok(mid) == up) hi = mid;
                else lo = mid;
            }
            return up ? hi : lo;
        }
        bool Keeps(double k) => op switch { ">=" => S(k) >= target, ">" => S(k) > target, "<=" => S(k) <= target, "<" => S(k) < target, _ => true };
        var knob = op switch
        {
            ">=" or ">" => Edge(Keeps, true),
            "<=" or "<" => Edge(Keeps, false),
            _ => new[] { Edge(k => S(k) >= target, true), Edge(k => S(k) <= target, false) }.MinBy(k => Math.Abs(S(k) - target)),
        };
        // a score that is the weight itself has no rounding to land in: the round weights near the knob are tried too
        knob = new[] { knob, Math.Round(knob, 3), Math.Round(knob) }.Where(Keeps).DefaultIfEmpty(knob).MinBy(k => Math.Abs(S(k) - target));
        if (!(Math.Abs(S(knob) - target) <= ScoreTolerance))
            throw new InvalidDataException($"no {(whole ? "skull" : "weight or trophy")} of this species scores {text} within {ScoreTolerance} (the nearest scores {S(knob)})");
        return knob;
    }
}
