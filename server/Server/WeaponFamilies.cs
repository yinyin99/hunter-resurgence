// The weapon families: every weapon item of the catalogue is in exactly one, and a family is one weapon type (its finishes and engraved or event variants).
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Resurgence.Server;

/// <summary>One weapon type: its representative (the base weapon the others derive from) and every member item id, the representative first, then ascending.</summary>
public sealed record WeaponFamily(long Representative, IReadOnlyList<long> Members);

/// <summary>The weapon families (docs/server.md, "Weapon families"). Three relations put weapon items (catalogue category 1) in one family, merged transitively:
/// <list type="bullet">
/// <item><c>name</c>: the same name (<see cref="State.NameWords"/>), whatever the finish (`variationname`);</item>
/// <item><c>chain</c>: the item definitions' `parent` chain leads to the same root (the variants of one weapon, the chain that shares the weapon skill);</item>
/// <item><c>shares</c>: a description names the weapon whose skill it shares: "shares skill level and rank with the X", "shares the same skill of the X (Finish)", "All variations of the X share the same weapon skill".
/// X must be a whole name of a weapon item (compared as <see cref="State.NameWords"/>), and `(Finish)` narrows it to that finish; a name that matches none adds nothing.</item>
/// </list>
/// A weapon with none is its own family. The representative is a member whose definition has no parent (the weapon the others derive from; every member when no definitions were given), the lowest id among those.</summary>
public sealed class WeaponFamilies
{
    static readonly Regex Shares = new(
        @"(?:shares?\s+(?:the\s+same\s+)?(?:weapon\s+)?skill(?:\s+level\s+and\s+rank)?\s+(?:with|of)|all\s+variations\s+of)\s+(?:the\s+)?(?<name>.+?)(?:\s*\((?<finish>[^)]*)\))?(?:\s+share\b|\.(?:\s|$)|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    readonly Dictionary<long, JsonObject> weapons;
    readonly Dictionary<long, WeaponFamily> family = [];
    readonly Dictionary<long, string> why = [];
    public IReadOnlyList<WeaponFamily> All { get; }

    /// <summary>The family of a weapon item; null for an item that is no weapon (or unknown).</summary>
    public WeaponFamily? Of(long itemId) => family.GetValueOrDefault(itemId);

    /// <param name="items">The items of the catalogue and of the install's definitions the catalogue lacks; only category 1 counts.</param>
    /// <param name="itemDefs">The game's item definitions (`id`, `define`, `parent`); null or empty: the name and shares relations only.</param>
    public static WeaponFamilies Build(IEnumerable<JsonObject> items, JsonArray? itemDefs) => new(items, itemDefs);

    WeaponFamilies(IEnumerable<JsonObject> items, JsonArray? itemDefs)
    {
        weapons = [];
        foreach (var i in items.Where(i => Py.IsNum(i["id"]) && Py.IsNum(i["cat"]) && Py.L(i["cat"]) == State.WeaponCategory))
            weapons[Py.L(i["id"])] = i;
        var ids = weapons.Keys.Order().ToArray();
        var group = ids.ToDictionary(i => i, i => i);
        long Find(long i) => group[i] == i ? i : group[i] = Find(group[i]);
        var reasons = ids.ToDictionary(i => i, _ => new SortedSet<string>(StringComparer.Ordinal));
        void Link(long a, long b, string reason)
        {
            if (a == b) return;
            reasons[a].Add(reason);
            reasons[b].Add(reason);
            group[Find(a)] = Find(b);
        }

        // name
        foreach (var same in ids.Where(i => Py.IsStr(weapons[i]["name"])).GroupBy(i => State.NameWords((string)weapons[i]["name"]!)).Where(g => g.Key.Length > 0))
            foreach (var i in same)
                Link(same.First(), i, "name");

        // chain
        var byId = new Dictionary<long, JsonObject>();
        var byDefine = new Dictionary<string, JsonObject>();
        foreach (var d in (itemDefs ?? []).OfType<JsonObject>().Where(d => Py.IsNum(d["id"]) && Py.IsStr(d["define"])))
            (byId[Py.L(d["id"])], byDefine[(string)d["define"]!]) = (d, d);
        JsonObject? Parent(JsonObject d) => d["parent"] is { } p && Py.IsStr(p) && byDefine.TryGetValue((string)p!, out var up) ? up : null;
        long Root(JsonObject d)
        {
            for (var n = 0; Parent(d) is { } up && n < byDefine.Count; n++) d = up;
            return Py.L(d["id"]);
        }
        foreach (var i in ids.Where(byId.ContainsKey))
            if (Root(byId[i]) is var root && weapons.ContainsKey(root))
                Link(i, root, "chain");

        // shares
        var named = ids.Where(i => Py.IsStr(weapons[i]["name"])).ToLookup(i => State.NameWords((string)weapons[i]["name"]!));
        foreach (var i in ids.Where(i => Py.IsStr(weapons[i]["description"])))
            foreach (Match m in Shares.Matches(((string)weapons[i]["description"]!).ReplaceLineEndings(" ")))
            {
                var finish = m.Groups["finish"].Success ? State.NameWords(m.Groups["finish"].Value) : null;
                foreach (var other in named[State.NameWords(m.Groups["name"].Value)].Where(o => finish is null || weapons[o]["variationname"] is { } v && Py.IsStr(v) && State.NameWords((string)v!) == finish))
                    Link(i, other, "shares");
            }

        var derived = ids.Where(i => byId.TryGetValue(i, out var d) && Parent(d) is not null).ToHashSet();
        All = [.. ids.GroupBy(Find).Select(g =>
        {
            var rep = g.OrderBy(derived.Contains).ThenBy(i => i).First();
            return new WeaponFamily(rep, [rep, .. g.Where(i => i != rep).Order()]);
        }).OrderBy(f => f.Representative)];
        foreach (var f in All)
            foreach (var m in f.Members)
            {
                family[m] = f;
                why[m] = reasons[m].Count == 0 ? "alone" : string.Join('+', reasons[m]);
            }
    }

    /// <summary>The table of every weapon item (a Markdown table, ascending by id, then a count line): id, name, finish, the family (its representative's id) and why the item is in it
    /// (`name`, `chain`, `shares`, joined by `+` when several relate it to the family; `alone`). The same catalogue gives the same lines.</summary>
    public IEnumerable<string> Table()
    {
        yield return "| id | name | finish | family | why |";
        yield return "|---|---|---|---|---|";
        foreach (var id in weapons.Keys.Order())
            yield return $"| {id} | {Cell(weapons[id]["name"])} | {Cell(weapons[id]["variationname"])} | {family[id].Representative} | {why[id]} |";
        yield return "";
        yield return $"{weapons.Count} weapons, {All.Count} families, {All.Count(f => f.Members.Count > 1)} with several members.";
    }

    static string Cell(JsonNode? v) => Py.IsStr(v) ? ((string)v!).Replace("|", "\\|") : "";
}
