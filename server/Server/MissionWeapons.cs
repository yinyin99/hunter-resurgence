// The weapons the mission objectives accept, resolved: the names a rule's `weapon:` words print, the list `coverage --rules` gives under the rules, and the two audit tables
// `admin mission-weapons` writes (docs/server.md, "Weapons of a mission").
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed partial class State
{
    /// <summary>A weapon item and the other members of its weapon family that the catalogue lists: a mission's weapons are the catalogue's (a weapon only the install defines is no part of one).</summary>
    long[] FamilyInCatalogue(long id) => Weapons.Of(id)?.Members.Where(items.ContainsKey).ToArray() ?? [id];

    /// <summary>The weapons by name, as a rule's words print them: a name is bare when the set holds the whole weapon family of every item of that name (the operator's file reads a bare name as the family),
    /// else each held item with a finish is `Name (Finish)` (read as that one item), so the printed words read back to the same set.</summary>
    string WeaponNames(IEnumerable<long> ids)
    {
        var held = ids.ToHashSet();
        string Label(long id)
        {
            var name = ItemName(id);
            var whole = items.Where(o => Py.IsNum(o.Value["cat"]) && Py.L(o.Value["cat"]) == WeaponCategory && ItemName(o.Key) == name)
                .All(o => FamilyInCatalogue(o.Key).All(held.Contains));
            return whole || ItemOf(id)?["variationname"] is not { } v || !Py.IsStr(v) ? name : $"{name} ({(string)v!})";
        }
        return Names(held.Select(Label));
    }

    /// <summary>What the missions page shows under an objective that names a weapon (`named weapon`, `named weapon + class`, `named finish`; none for any other): one line per weapon name, `Name`, or `Name (Finish, Finish)`
    /// when only some finishes of that name count; sorted.</summary>
    string[] WeaponsOnPage(Rule r)
    {
        if (WeaponsOf(r) is not { } w || w.Basis is not (WeaponBasis.Name or WeaponBasis.NameAndClass or WeaponBasis.Finish))
            return [];
        var held = w.Ids.ToHashSet();
        return [.. held.GroupBy(ItemName).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
            items.Where(o => Py.IsNum(o.Value["cat"]) && Py.L(o.Value["cat"]) == WeaponCategory && ItemName(o.Key) == g.Key).All(o => held.Contains(o.Key)) ? g.Key
                : $"{g.Key} ({string.Join(", ", g.Select(i => ItemOf(i)?["variationname"] is { } v && Py.IsStr(v) ? (string)v! : "?").Order(StringComparer.Ordinal))})")];
    }

    /// <summary>`<id> <name> (<finish>)`.</summary>
    string WeaponLabel(long id) => $"{id} {ItemName(id)}{(ItemOf(id)?["variationname"] is { } v && Py.IsStr(v) ? $" ({(string)v!})" : "")}";

    /// <summary>Every objective: the catalogue's missions by id, then the daily missions' templates and the running event's community missions (`daily <id>`, `community <id>`; a community mission has the one objective 0).</summary>
    IEnumerable<(string Mission, long Objective, string Title, Rule Rule)> AllObjectives() =>
        missions.Values.OrderBy(m => m.Id).SelectMany(m => m.Objectives.OrderBy(o => o.Id).Select(o => (m.Id.ToString(), o.Id, o.Title, o.Rule)))
            .Concat(dailies.OrderBy(d => d.Template.Id).SelectMany(d => d.Def.Objectives.OrderBy(o => o.Id).Select(o => ($"daily {d.Template.Id}", o.Id, o.Title, o.Rule))))
            .Concat(community.OrderBy(c => c.Id).Select(c => ($"community {c.Id}", 0L, c.Title, c.Rule)));

    /// <summary>The weapon condition of a rule: its basis (<see cref="WeaponBasis"/>) and the accepted item ids ascending; null when the rule has none.</summary>
    static (string Basis, long[] Ids)? WeaponsOf(Rule r) =>
        r.Tests?.FirstOrDefault(t => t.Check == Check.Weapon) is { } t ? (t.Basis ?? WeaponBasis.Name, [.. t.Ids!.Order()]) : null;

    /// <summary>The weapons of every objective that has a weapon condition, for `coverage --rules`: `mission <m> objective <o>: <basis>: <id> <name> (<finish>); ...`, by mission and objective, the weapons by id.</summary>
    public IEnumerable<string> WeaponLines() =>
        AllObjectives().Select(o => (o, w: WeaponsOf(o.Rule))).Where(x => x.w is not null)
            .Select(x => $"mission {x.o.Mission} objective {x.o.Objective}: {x.w!.Value.Basis}: {string.Join("; ", x.w.Value.Ids.Select(WeaponLabel))}");

    static string TableCell(string s) => s.Replace("|", "\\|").ReplaceLineEndings(" ");

    /// <summary>Table (a), Markdown: every objective of every mission with its weapon condition (`none`, `class`, `named weapon`, `named weapon + class`, `any weapon but`, `named finish`) and the accepted weapons
    /// (`<id> <name> (<finish>)`, ascending by id), then a count line. The same catalogue gives the same lines.</summary>
    public IEnumerable<string> ObjectiveWeaponTable()
    {
        yield return "| mission | objective | title | weapon condition | accepted weapons |";
        yield return "|---|---|---|---|---|";
        var rows = AllObjectives().ToList();
        foreach (var (mission, objective, title, rule) in rows)
            yield return WeaponsOf(rule) is { } w
                ? $"| {mission} | {objective} | {TableCell(title)} | {w.Basis} | {TableCell(string.Join("; ", w.Ids.Select(WeaponLabel)))} |"
                : $"| {mission} | {objective} | {TableCell(title)} | none | |";
        yield return "";
        var counted = rows.Select(r => WeaponsOf(r.Rule)?.Basis ?? "none").GroupBy(b => b).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}");
        yield return $"{rows.Count} objectives: {string.Join(", ", counted)}.";
    }

    /// <summary>Table (b), Markdown: every weapon item (ascending by id) with its family (the representative's id) and the objectives that accept it (`<mission>/<objective>`, in order), then a count line.</summary>
    public IEnumerable<string> WeaponObjectiveTable()
    {
        var accepted = new Dictionary<long, List<string>>();
        foreach (var (mission, objective, _, rule) in AllObjectives())
            if (WeaponsOf(rule) is { } w)
                foreach (var id in w.Ids)
                    (accepted.TryGetValue(id, out var l) ? l : accepted[id] = []).Add($"{mission}/{objective}");
        yield return "| id | name | finish | family | accepted by (mission/objective) |";
        yield return "|---|---|---|---|---|";
        var all = Weapons.All.SelectMany(f => f.Members.Where(items.ContainsKey).Select(m => (Id: m, Family: f.Representative))).OrderBy(w => w.Id).ToList();
        foreach (var (id, family) in all)
            yield return $"| {id} | {TableCell(ItemName(id))} | {(ItemOf(id)?["variationname"] is { } v && Py.IsStr(v) ? TableCell((string)v!) : "")} | {family} | {(accepted.TryGetValue(id, out var by) ? string.Join(", ", by) : "none")} |";
        yield return "";
        yield return $"{all.Count} weapons, {all.Count(w => accepted.ContainsKey(w.Id))} accepted by at least one objective, {all.Count(w => !accepted.ContainsKey(w.Id))} by none.";
    }

    public bool HasMissions => missions.Count > 0;

    /// <summary>The two tables as (file name, lines): `mission-objectives.md` (<see cref="ObjectiveWeaponTable"/>) and `weapon-missions.md` (<see cref="WeaponObjectiveTable"/>).</summary>
    public IEnumerable<(string File, IEnumerable<string> Lines)> MissionWeaponTables() =>
        [("mission-objectives.md", ObjectiveWeaponTable()), ("weapon-missions.md", WeaponObjectiveTable())];
}
