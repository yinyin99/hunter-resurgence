// Missions on a community server: the catalogue's definitions (`missions[]`, `mission_groups[]` of the recorded application), the account's
// states (tables `missions` and `mission_prefs`, filled from a restored `Mission_missions.json` at the restore and on first use), the
// replies of Mission/*, and the progress the hunt's events make. The game keeps no mission state: an objective's only definition is its
// English title, which <see cref="RuleOf"/> turns into a rule over the events (docs/server.md, "Missions").
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Resurgence.Server;

public sealed partial class State
{
    enum Kind { Other, Harvest, Track, Spot, Arrive, Start }

    /// <summary>What an objective needs: its kind, the species (catalogue id, 0 to 99) and gender (0 male, 1 female) its title names (null: any; a title that names several species, a short
    /// name of some, or any animal but some, has a `Species` test instead), whether it counts only after the objectives before it, its title in
    /// <see cref="Normalise"/>d words (a place), and the conditions its title asks of the kill, the spot or the clue (<see cref="Reader"/>); `Unknown` when it
    /// has words the server does not understand: it is never met. `Zone` is an arrive objective's reserves and, when its title has coordinates, the point (the map's x and z).</summary>
    sealed record Rule(Kind Kind, long? Species, long? Gender, bool Ordered, string Text, Test[]? Tests = null, bool Unknown = false, string Left = "", Zone? Zone = null, long? After = null, bool Together = false);

    /// <summary>Where an arrive objective can be met: the point its title gives in metres (null: none), the reserves of its mission (none listed: any) and the defines of the catalogue's places
    /// the title names by their English name (<see cref="NamedPlaces"/>).</summary>
    sealed record Zone((double X, double Z)? Point, long[] Reserves, string[] Places);

    sealed record Objective(long Id, string Title, Rule Rule);

    /// <summary>The n-th `another <animal>` clue or spot objective of a mission needs the n-th different animal: n is 1 and the `earlier` objectives of the mission of its kind that ask for the same species, gender
    /// and kinds of clue, however they are worded (the reader leaves `another` as 1); the first one needs no other animal, so it has no test.</summary>
    static Rule Settle(Rule rule, IEnumerable<Rule> earlier)
    {
        if (rule.Tests?.FirstOrDefault(t => t.Check == Check.Different && t.Min < 2) is not { } another)
            return rule;
        static HashSet<long> Of(Rule r, Check c) => r.Tests?.FirstOrDefault(t => t.Check == c)?.Ids ?? [];
        var n = 1 + earlier.Count(e => e.Kind == rule.Kind && e.Species == rule.Species && e.Gender == rule.Gender && Of(e, Check.Species).SetEquals(Of(rule, Check.Species)) && Of(e, Check.Clue).SetEquals(Of(rule, Check.Clue)));
        return rule with { Tests = [.. rule.Tests.Where(t => t != another), .. n < 2 ? [] : new[] { another with { Min = n } }] };
    }

    static readonly Regex FirstEnd = new(@"\bfirst\W*\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex AfterVisiting = new(@"\A\s*after\s+(?:visiting|arriving at|reaching)\s+(?<p>[^,:]+)[,:]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex SameHuntLead = new(@"\A\s*(?:(?:then|finally|first|lastly)\b[\s,:-]*)?at any (?:point|time)\s+during\s+(?:the\s+|a\s+)?(?:same|one|single)\s+hunt\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>`Harvest a Puma first.` ends the objective before: the next one is ordered after it. `After visiting X, harvest ...` waits for the arrive objective before it that names X only, not for every objective
    /// before it (<see cref="Rule.After"/>). `At any point during the same hunt, ...` in a travel mission waits for its `Start at ...` objective: the hunt that started at the lodge.</summary>
    static Rule Order(Rule rule, string title, Objective[] before)
    {
        if (before.Length > 0 && FirstEnd.IsMatch(before[^1].Title))
            rule = rule with { Ordered = true };
        if (AfterVisiting.Match(title) is { Success: true } m && Normalise(m.Groups["p"].Value) is { Length: > 0 } place
            && before.FirstOrDefault(p => p.Rule.Kind == Kind.Arrive && (" " + p.Rule.Text + " ").Contains(" " + place + " ", StringComparison.Ordinal)) is { } visit)
            return rule with { After = visit.Id };
        return SameHuntLead.IsMatch(title) && before.FirstOrDefault(p => p.Rule.Kind == Kind.Start) is { } start ? rule with { Ordered = true, After = start.Id } : rule;
    }

    /// <summary>The objectives an ordered objective waits for: every one before it, or the one its `After visiting ...` names.</summary>
    static IEnumerable<Objective> Before(MissionDef m, int i) => m.Objectives[i].Rule.After is { } after ? m.Objectives.Where(o => o.Id == after) : m.Objectives[..i];

    /// <summary>`Gm` and `Em` are the sums of the `N gm$` and `N em$` entries of `rewards`; the other entries (items, a trophy) are not paid.</summary>
    sealed record MissionDef(long Id, string Title, bool SingleExpedition, long[] Dependencies, long Gm, long Em, Objective[] Objectives);

    // `Then`, `Finally`, `First`, `Lastly`, `After that,` and `After spotting the Wolves,` (up to the comma) put the objective after the ones before it
    static readonly Regex OrderedWord = new(@"\A\s*(?:(?:then|finally|first|lastly)\b|after\b[^,:]*(?=[,:])|after\b)[\s,:-]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // `At any point during the same hunt,` and `At any time,` say nothing; `At any point while in <reserve>,` names the reserve
    static readonly Regex AtAny = new(@"\A\s*at any (?:point|time)\b(?:\s+(?:(?:during|in|within|throughout)\s+(?:the\s+|a\s+)?(?:same|one|single)\s+(?:hunt|expedition|session)|while\s+in\s+(?<r>[^,:]+)))?\s*[,:]?\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex GoTo = new(@"\A(?:(?:head|go|travel)(?: over| out)? to|make it to)\b", RegexOptions.CultureInvariant);
    static readonly Regex OneHunt = new(@"\b(?:same|one|single)\s+(?:hunt|expedition|session|outing)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // the plurals of species names no `s` makes (`two Grey Wolves`, `5 Snow Geese`)
    static readonly (Regex Plural, string Name)[] Plurals = [(new(@"\bwolves\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "wolf"), (new(@"\bgeese\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "goose")];
    static readonly Regex TrackWord = new(@"\b(tracks?|droppings?|calls?|audio|blood|bedding|beds?|feeding|footprints?|footsteps?|clues?|scat)\b", RegexOptions.CultureInvariant);
    static readonly Regex Reward = new(@"\A\s*(\d+)\s*(gm|em)\$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly string[] Switches = ["auto_mission_activate", "activate_only_favourite", "hide_completed_favourite"];
    const string MissionsPath = "Mission_missions.json";

    readonly Dictionary<long, MissionDef> missions = [];
    readonly Dictionary<long, long[]> missionGroups = [];  // group id → its mission ids
    /// <summary>A place of the catalogue (a lodge, a campsite, a tower, a point of interest): its reserve, its English name and the define the game sends when the player enters it (`Words`: that
    /// define <see cref="Normalise"/>d, as an arrival carries it).</summary>
    sealed record Location(long Reserve, string Name, string Define, string Words);

    readonly Dictionary<long, Location> catalogueLocations = [];  // location id → the place; the name is <see cref="Normalise"/>d

    // the base letter of each accented Latin letter: the server runs with invariant globalization, where string.Normalize leaves every letter as it is
    static readonly Dictionary<char, char> Unaccented = new[]
    {
        "aàáâãäåāăą", "cçćĉċč", "dďđ", "eèéêëēĕėęě", "gĝğġģ", "hĥħ", "iìíîïĩīĭįı", "jĵ", "kķ", "lĺļľŀł", "nñńņň", "oòóôõöøōŏő", "rŕŗř", "sśŝşš", "tţťŧ", "uùúûüũūŭůűų", "wŵ", "yýÿŷ", "zźżž",
    }.SelectMany(g => g[1..].Select(c => (Accented: c, Base: g[0]))).ToDictionary(x => x.Accented, x => x.Base);

    /// <summary>The text in lower case, without accents and without combining marks.</summary>
    static string Letters(string s) => new([.. s.Where(c => char.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).Select(char.ToLowerInvariant).Select(c => Unaccented.GetValueOrDefault(c, c))]);

    /// <summary>Lower case letters and digits in single-space words, accents removed: the form titles, names and defines are compared in.</summary>
    static string Normalise(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in Letters(s))
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    // words that name no place alone: the last word of a place is tried by itself only when it is none of these
    static readonly HashSet<string> CommonWords = ["lodge", "tower", "campsite", "camp", "site", "point", "bay", "lake", "pond", "creek", "river", "falls", "fall", "hill", "pass", "island", "bridge", "road", "area", "north", "south", "east", "west"];
    static readonly string LocationWords = Normalise(ProtocolLiterals.LocationPrefix) + " ";

    /// <summary>Does the title name the place (both <see cref="Normalise"/>d)? A define carries its reserve in front and a number behind (`REGION_CALM_POND`): the place's own words
    /// match, then without leading ones, down to two words (one for a one-word place).
    /// ponytail: a title that names only the place's tail matches any place with that tail (two hunting towers).</summary>
    static bool PlaceIn(string title, string place)
    {
        var words = place.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && words[^1].All(char.IsAsciiDigit))
            words.RemoveAt(words.Count - 1);
        for (var i = 0; words.Count > 0 && i < Math.Max(1, words.Count - 1); i++)
            if ((" " + title + " ").Contains(" " + string.Join(' ', words.Skip(i)) + " ", StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>Does the title name the place less strictly than <see cref="PlaceIn"/>: without the game's location prefix, with or without a final `s` of the last word, or by the last word
    /// alone unless that word (in either form) is a common one, shorter than 4 letters or a number? Only an arrival's match, and only in the objective's reserves (<see cref="ArrivesAt"/>).
    /// ponytail: a title that names only the last word matches any place of the mission's reserves that ends in it; the title's coordinates and a `place:` override are the exact ways.</summary>
    static bool PlaceLike(string title, string place)
    {
        if (place.StartsWith(LocationWords, StringComparison.Ordinal))
            place = place[LocationWords.Length..];
        if (PlaceIn(title, place))
            return true;
        var words = place.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && words[^1].All(char.IsAsciiDigit))
            words.RemoveAt(words.Count - 1);
        if (words.Count == 0)
            return false;
        var padded = " " + title + " ";
        bool Has(IEnumerable<string> w) => padded.Contains(" " + string.Join(' ', w) + " ", StringComparison.Ordinal);
        var last = words[^1];
        var other = last.EndsWith('s') ? last[..^1] : last + "s";  // a final `s` is optional
        for (var i = 0; i < Math.Max(1, words.Count - 1); i++)
            if (Has(words.Skip(i).SkipLast(1).Append(other)))
                return true;
        return last.Length >= 4 && !last.All(char.IsAsciiDigit) && !CommonWords.Contains(last) && !CommonWords.Contains(other) && (Has([last]) || Has([other]));
    }

    /// <summary>The player entering a place (event 101, `direction` enter): its normalised name, its position in metres (the map's x and z) and the reserve of the hunt it is in (read when asked).</summary>
    sealed record Arrival(string Place, double? X, double? Z, Lazy<long?> Reserve);

    /// <summary>The arrival an event 101 stored as `eventId` is; null for a place left or an event without a name.</summary>
    static Arrival? ArrivalOf(Db con, long eventId, JsonObject e) =>
        Py.IsStr(e["direction"]) && (string)e["direction"]! == "enter" && Py.IsStr(e["location_name"]) && Normalise((string)e["location_name"]!) is { Length: > 0 } place
            ? new(place, NumOf(e, "player_x"), NumOf(e, "player_z"), new(() => ReserveAt(con, eventId))) : null;

    /// <summary>The defines of the catalogue's places of `reserves` (none: any) whose English name the title (<see cref="Normalise"/>d) holds as whole words, those with the longest name only: the
    /// title of `the Inland Campsite` does not name `Campsite` too. The game sends a place's define when the player enters it (docs/server.md, "What the stored events show").</summary>
    static string[] NamedPlaces(IEnumerable<Location> places, string title, long[] reserves)
    {
        var padded = " " + title + " ";
        var named = places.Where(l => (reserves.Length == 0 || reserves.Contains(l.Reserve)) && padded.Contains(" " + l.Name + " ", StringComparison.Ordinal)).ToList();
        var longest = named.Select(l => l.Name.Length).DefaultIfEmpty().Max();
        return [.. named.Where(l => l.Name.Length == longest && l.Define.Length > 0).Select(l => l.Define).Distinct()];
    }

    /// <summary>Is the hunt of the arrival in one of the zone's reserves (none listed: any)?</summary>
    static bool InReserves(Zone z, Arrival a) => z.Reserves.Length == 0 || a.Reserve.Value is { } here && z.Reserves.Contains(here);

    /// <summary>Is the arrival within 200 m of the zone's point, in its reserves, whatever the place's name?</summary>
    static bool NearPoint(Zone z, Arrival a) =>
        z.Point is { } n && a.X is { } ax && a.Z is { } az && Math.Sqrt((ax - n.X) * (ax - n.X) + (az - n.Z) * (az - n.Z)) <= 200 && InReserves(z, a);

    /// <summary>Does the arrival meet an arrive rule: the exact place of an operator's `arrive; place: NAME` (or, when the title has coordinates, a point near them); else the place the title names
    /// (<see cref="PlaceIn"/>) or the catalogue place whose English name it holds (<see cref="NamedPlaces"/>); else, when the hunt is in one of the mission's reserves (none listed: any), a point within
    /// 200 m of the title's coordinates, whatever the place's name, or, for a title without coordinates, the place the title names less strictly (<see cref="PlaceLike"/>).</summary>
    static bool ArrivesAt(Rule r, Arrival a) =>
        r.Tests?.FirstOrDefault(t => t.Check == Check.Place) is { } at ? a.Place == Normalise(at.Place!) || r.Zone is { } w && NearPoint(w, a)
        : PlaceIn(r.Text, a.Place) || r.Zone is { } z && (z.Places.Any(p => Normalise(p) == a.Place) || (z.Point is null ? InReserves(z, a) && PlaceLike(r.Text, a.Place) : NearPoint(z, a)));

    /// <summary>Does an objective with this rule begin at the start place `lodge` (a catalogue location id)? Its name or its define is in the title.</summary>
    bool StartsAt(Rule r, long? lodge) => lodge is { } l && catalogueLocations.TryGetValue(l, out var p) && (PlaceIn(r.Text, p.Name) || PlaceIn(r.Text, p.Words));

    /// <summary>An arrive objective no place the server knows can meet: no operator's place, no coordinates, and no catalogue place whose English name or define (<see cref="PlaceLike"/>) the title
    /// holds. The game names more places than the catalogue (its areas): an arrival at one of those is met by the title's words and is reported here all the same.</summary>
    bool NoPlaceMeets(Rule r) => r is { Kind: Kind.Arrive, Zone: { Point: null, Places.Length: 0 } } && !(r.Tests ?? []).Any(t => t.Check == Check.Place) && !catalogueLocations.Values.Any(l => PlaceLike(r.Text, l.Words));

    /// <summary>Read the catalogue's missions and groups (a community server; none without a recorded catalogue).</summary>
    void LoadMissions()
    {
        foreach (var loc in Arr(G(application, "locations", null)).OfType<JsonObject>().Where(l => TryKey(l["id"], out _)))
            if (Py.Str(G(loc, "name", "")) is var placeName && Py.Str(G(loc, "define", "")) is var define && Normalise(placeName + define).Length > 0)
                catalogueLocations[Py.L(loc["id"])] = new(TryKey(loc["reserve"], out _) ? Py.L(loc["reserve"]) : -1, Normalise(placeName), define, Normalise(define));
        var names = species.Where(s => s.Key < 100 && Py.IsStr(s.Value["name"]))  // a variant (100 and up) is its species'
            .Select(s => (Id: s.Key, Name: Normalise(((string)s.Value["name"]!).Split(" (")[0]))).Where(s => s.Name.Length > 0)
            .OrderByDescending(s => s.Name.Length).ToList();  // the longest name wins: "mule deer" before "deer"

        var towers = Arr(G(application, "locations", null)).OfType<JsonObject>()
            .Where(l => Py.IsStr(l["type"]) && (string)l["type"]! == "HUNTING_TOWER" && Py.IsStr(l["name"]) && Py.IsStr(l["define"]) && TryKey(l["reserve"], out _)
                        && l["pos"] is JsonObject pos && Py.IsNum(pos["x"]) && Py.IsNum(pos["z"]))
            .Select(l => (Py.L(l["reserve"]), Reader.Words((string)l["name"]!), (string)l["define"]!, Py.D(l["pos"]!["x"]), Py.D(l["pos"]!["z"])));
        var reader = new Reader(names, items.Values, reserves, towers, Weapons);
        // a plural of a species name (`two Snowshoe Hares`, `two Red Foxes`) is read as the name
        var plural = names.Count == 0 ? null : new Regex(@"\b(" + string.Join('|', names.Select(n => Regex.Escape(n.Name))) + @")e?s\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // the permitted ammunition of each species by its typical id: the catalogue's lists (the connect lists the game checks a kill with are read later)
        var permits = Arr(G(application, "species", null)).OfType<JsonObject>().Where(s => Py.L(s["id"]) is >= 0 and < 100)
            .ToDictionary(s => Py.L(s["id"]), s => Arr(G(s, "ethicalAmmo", null)).Select(Py.L).ToHashSet());
        Rule RuleOf(string title, long[]? own = null, long[]? named = null, bool singleHunt = false, long[]? places = null)
        {
            var ordered = OrderedWord.Match(title);
            var rest = title[ordered.Length..];
            var lead = AtAny.Match(rest);
            rest = rest[lead.Length..];
            var body = lead.Groups["r"].Success ? rest.TrimEnd().TrimEnd('.') + " in " + lead.Groups["r"].Value : rest;  // the reserve is read like one at the end of the title
            var text = Normalise(body);
            var animals = body;  // the title as its animals are read, plurals as names (a place keeps its words)
            foreach (var (word, name) in Plurals)
                animals = word.Replace(animals, name);
            animals = plural?.Replace(animals, "$1") ?? animals;
            var first = text.Split(' ')[0];
            var kind = first switch
            {
                "harvest" or "take" or "kill" => Kind.Harvest,
                "spot" => Kind.Spot,
                "arrive" or "visit" or "walk" => Kind.Arrive,
                "head" or "go" or "travel" or "make" when GoTo.IsMatch(text) => Kind.Arrive,
                "start" => Kind.Start,
                "begin" when text.StartsWith("begin at ", StringComparison.Ordinal) => Kind.Start,
                "locate" or "id" or "identify" or "find" or "register" when TrackWord.IsMatch(text) => Kind.Track,
                _ => Kind.Other,
            };
            var words = " " + Normalise(animals);
            long? species = names.Where(x => words.Contains(" " + x.Name, StringComparison.Ordinal)).Select(x => (long?)x.Id).FirstOrDefault();
            long? gender = Reader.FemaleWord.IsMatch(text) ? (long?)1 : Reader.MaleWord.IsMatch(text) ? (long?)0 : null;
            Test[] tests = [];
            var left = "";
            if (kind is Kind.Harvest or Kind.Spot)
                (tests, left) = reader.Read(animals, own, named, kind == Kind.Harvest && (singleHunt || OneHunt.IsMatch(title)), places, kind == Kind.Spot);  // a spot's ordinal says nothing, as before
            else if (kind == Kind.Track)
                (tests, left) = reader.ReadClue(animals, own, singleHunt || OneHunt.IsMatch(title));
            if (tests.Any(t => t.Check == Check.Species))
                species = null;  // several species, a short name or "any animal except ...": the test decides, not the first name found
            else if (species is null && gender is not null && own is [var only] && kind is Kind.Harvest or Kind.Spot or Kind.Track)
                species = only;  // `an airborne rooster` in the mission of one species: that species
            // a spot has no weapon, hit or harvest: only its stand, its reserve, its place, its species, its coat and its name can be checked
            if (kind == Kind.Spot)
                left = string.Join(' ', tests.Where(t => t.Check is not (Check.Stand or Check.Reserve or Check.Place or Check.Named or Check.Species or Check.Coat or Check.NotCoat or Check.Different)).Select(t => t.Check.ToString().ToLowerInvariant()).Prepend(left)).Trim();
            // a title that names ammunition accepts the animal's permitted rounds only, unless it names none the animal permits (the objective would be impossible: it stays as the title says)
            if (kind == Kind.Harvest && tests.FirstOrDefault(t => t.Check == Check.Ammo) is { Ids: { } rounds } && !tests.Any(t => t.Check is Check.Permitted or Check.NotAmmo))
            {
                HashSet<long> animal = species is { } one ? [one] : tests.FirstOrDefault(t => t.Check == Check.Species)?.Ids ?? [.. own ?? []];
                if ((animal.Count > 0 ? animal.Select(s => ((s % 100) + 100) % 100) : permits.Keys).Any(s => permits.TryGetValue(s, out var list) && list.Overlaps(rounds)))
                    tests = [.. tests, new Test(Check.Permitted)];
            }
            var point = Reader.PointOf(body);  // a title with coordinates is met near them, not by the name of a place beside the one it means (`Fort Stillwater Watchtower`)
            return new Rule(kind, species, gender, ordered.Success, text, tests, left.Length > 0, left, kind == Kind.Arrive ? new Zone(point, places ?? [], point is null ? NamedPlaces(catalogueLocations.Values, text, places ?? []) : []) : null);
        }

        foreach (var d in Arr(G(application, "missions", null)).OfType<JsonObject>())
        {
            if (!TryKey(d["id"], out var id))
                continue;
            long gm = 0, em = 0;
            foreach (var text in Arr(d["rewards"]).OfType<JsonObject>().Select(r => Py.IsStr(r["text"]) ? (string)r["text"]! : ""))
                if (Reward.Match(text) is { Success: true } paid && long.TryParse(paid.Groups[1].Value, out var amount))
                    if (paid.Groups[2].Value.Equals("gm", StringComparison.OrdinalIgnoreCase)) gm += amount;
                    else em += amount;
            var listed = Ids(d["species"]).Select(x => x % 100).Distinct().ToArray();
            var mentioned = reader.WeaponsIn(Py.Str(G(d, "desc", "")));  // the weapons the mission's text names
            Rule Build(string title)
            {
                var rule = RuleOf(title, listed, mentioned, Py.Truthy(d["singleExpedition"]), Ids(d["reserves"]));  // a named animal's title names no species: the mission's own one does
                return rule.Species is null && listed.Length == 1 && (rule.Tests ?? []).Any(t => t.Check == Check.Named) ? rule with { Species = listed[0] } : rule;
            }
            var objectives = Arr(d["objectives"]).OfType<JsonObject>().Where(o => TryKey(o["id"], out _))
                .Select(o => new Objective(Py.L(o["id"]), Py.Str(G(o, "title", "")), Build(Py.Str(G(o, "title", ""))))).ToArray();
            missions[id] = new MissionDef(id, Py.Str(G(d, "title", "")), Py.Truthy(d["singleExpedition"]), [.. Ids(d["dependencies"]).Except(Beginner)], gm, em,
                [.. objectives.Select((o, i) => o with { Rule = Settle(Order(o.Rule, o.Title, objectives[..i]), objectives[..i].Select(p => p.Rule)) })]);
        }
        foreach (var g in Arr(G(application, "mission_groups", null)).OfType<JsonObject>().Where(g => TryKey(g["id"], out _)))
            missionGroups[Py.L(g["id"])] = Ids(g["missions"]);
        LoadDailyMissions(title => Settle(RuleOf(title), []));
        LoadCommunity(title => Settle(RuleOf(title), []));
    }

    static long[] Ids(JsonNode? n) => [.. Arr(n).Where(x => TryKey(x, out _)).Select(Py.L).Distinct()];

    /// <summary>The Whitetail Deer missions 0, 1 and 2 (track, spot, harvest) that open most packs in the original: no mission needs them here, the community's players are not beginners (#1012).</summary>
    static readonly long[] Beginner = [0, 1, 2];

    static JsonArray List(IEnumerable<long> ids) => new([.. ids.Select(i => (JsonNode?)i)]);

    // ---- the account's rows

    /// <summary>The account's mission rows and switches exist. The first time (no `mission_prefs` row) they come from the restored mission page
    /// (`Mission_missions.json` of `extras`): `completed` and `active` with the finished objectives of `states`, the three switches and the favourite
    /// mission packs; ids without a definition are kept as they are. `available` is never stored. A restored completed mission is marked as restored (`kept` is `*`), a restored
    /// active one keeps its finished objectives (`kept`), and the events up to `before` (default: the last one) are not the missions': they are older than the rows.
    /// Call inside a transaction.</summary>
    public static void EnsureMissions(Db con, long? before = null)
    {
        if (con.Scalar("SELECT 1 FROM mission_prefs WHERE account_id = $a") is not null)
            return;
        var prefs = new JsonObject { ["favourites"] = new JsonArray() };
        foreach (var key in Switches)
            prefs[key] = 0;
        if (con.Scalar("SELECT body FROM extras WHERE account_id = $a AND path = ?", MissionsPath) is string text && JsonNode.Parse(text) is JsonObject saved)
        {
            foreach (var key in Switches)
                prefs[key] = Py.Truthy(saved[key]) ? 1 : 0;
            prefs["favourites"] = List(Ids(saved["favourites"]));
            var (completed, seen) = (Ids(saved["completed"]), before ?? LastEvent(con));
            foreach (var id in completed)
                con.Exec("INSERT INTO missions (account_id, mission_id, state, objectives, since, seen, kept) VALUES ($a, ?, 3, '[]', NULL, ?, ?)", id, seen, RestoredMark);
            foreach (var id in Ids(saved["active"]).Except(completed))
            {
                var done = Py.Dumps(List(Ids((saved["states"] as JsonObject)?[id.ToString()] is JsonObject st ? st["objectives"] : null)));
                con.Exec("INSERT INTO missions (account_id, mission_id, state, objectives, since, seen, kept, earlier) VALUES ($a, ?, 2, ?, ?, ?, ?, ?)", id, done, seen, seen, done, done);
            }
        }
        con.Exec("INSERT INTO mission_prefs (account_id, body) VALUES ($a, ?)", Py.Dumps(prefs));
    }

    static JsonObject PrefsOf(Db con) => Parse(con.Scalar("SELECT body FROM mission_prefs WHERE account_id = $a")!);

    static void SetPref(Db con, string key, JsonNode value)
    {
        var prefs = PrefsOf(con);
        prefs[key] = value;
        con.Exec("UPDATE mission_prefs SET body = ? WHERE account_id = $a", Py.Dumps(prefs));
    }

    /// <summary>The rows by mission id: state (2 active, 3 completed) and the finished objective ids in the order they were finished.</summary>
    static Dictionary<long, (long State, List<long> Done)> MissionRows(Db con) =>
        con.Rows("SELECT mission_id, state, objectives FROM missions WHERE account_id = $a ORDER BY mission_id")
            .ToDictionary(r => (long)r[0], r => ((long)r[1], Ids(JsonNode.Parse((string)r[2])).ToList()));

    /// <summary>The missions with a definition that are not started and whose dependencies are all completed.</summary>
    IEnumerable<MissionDef> Available(Dictionary<long, (long State, List<long> Done)> rows) =>
        missions.Values.Where(m => !rows.ContainsKey(m.Id) && m.Dependencies.All(d => rows.TryGetValue(d, out var r) && r.State == 3)).OrderBy(m => m.Id);

    /// <summary>What a row remembers besides its objectives: `Since`, the last event before the mission was activated here (null: not known, the row is older than schema 30);
    /// `Seen`, the last event the mission's progress is not owed from (the harvests after it are the ones to check); `Kept`, the finished objectives a restore brought, or a harvest of an animal
    /// another member shot (<see cref="HarvesterEvent"/>) finished (a JSON list), or `*` for a mission completed before the restore: those are never checked or reopened, no event of the account's
    /// stands behind them; `Earlier`, the objectives finished before `Seen` (a JSON list): no harvest after it finished them.</summary>
    sealed record Mark(long? Since, long Seen, string Kept, string Earlier)
    {
        public bool Restored => Kept == RestoredMark;
        public long[] KeptIds => Restored ? [] : Ids(JsonNode.Parse(Kept));
        public long[] EarlierIds => Ids(JsonNode.Parse(Earlier));
    }

    const string RestoredMark = "*";

    static Dictionary<long, Mark> Marks(Db con) =>
        con.Rows("SELECT mission_id, since, seen, kept, earlier FROM missions WHERE account_id = $a")
            .ToDictionary(r => (long)r[0], r => new Mark(r[1] as long?, (long)r[2], (string)r[3], (string)r[4]));

    /// <summary>The mission becomes active; its progress counts the events after `at` (default: the last one stored).</summary>
    static void Activate(Db con, long id, long? at = null)
    {
        var seen = at ?? LastEvent(con);
        con.Exec("INSERT INTO missions (account_id, mission_id, state, objectives, since, seen) VALUES ($a, ?, 2, '[]', ?, ?)", id, seen, seen);
    }

    // ---- the replies

    /// <summary>Mission/missions of the account; null when the catalogue has no missions (the empty reply of ReplayScope stands).</summary>
    public byte[]? MissionsReply(long account)
    {
        if (missions.Count == 0)
            return null;
        using var con = Con(account);
        con.Begin();
        EnsureMissions(con);
        var rows = MissionRows(con);
        var prefs = PrefsOf(con);
        con.Commit();
        var available = Available(rows).Select(m => m.Id).ToList();
        var states = new JsonObject();
        foreach (var id in available)
            states[id.ToString()] = new JsonObject { ["state"] = 1 };
        foreach (var (id, (state, done)) in rows)
            states[id.ToString()] = state == 3 ? new JsonObject { ["state"] = 3 } : new JsonObject { ["state"] = 2, ["objectives"] = List(done) };
        var reply = new JsonObject
        {
            ["available"] = List(available), ["active"] = List(rows.Where(r => r.Value.State == 2).Select(r => r.Key)),
            ["completed"] = List(rows.Where(r => r.Value.State == 3).Select(r => r.Key)), ["states"] = states,
        };
        foreach (var key in Switches)
            reply[key] = prefs[key]!.DeepClone();
        reply["favourites"] = prefs["favourites"]!.DeepClone();
        return Py.DumpBytes(reply);
    }

    /// <summary>One of the page's writes (`activate`, `activate_all_available`, `update_automatic_activation`, `update_activate_only_favourite`,
    /// `update_hide_completed_favourite`, `update_favourite`): true when it was done, false for a mission that is not available or a group id that is
    /// no number; null when the catalogue has no missions.</summary>
    public bool? MissionCall(long account, string call, byte[] formBody)
    {
        if (missions.Count == 0)
            return null;
        var form = Py.ParseQs(Encoding.UTF8.GetString(formBody));
        static bool Yes(string v) => v is "true" or "1";
        using var con = Con(account);
        con.Begin();
        EnsureMissions(con);
        var rows = MissionRows(con);
        var ok = true;
        switch (call)
        {
            case "activate":
                ok = long.TryParse(Form1(form, "mission_id"), out var id) && Available(rows).Any(m => m.Id == id);
                if (ok)
                    Activate(con, id);
                break;
            case "activate_all_available":
                foreach (var m in Available(rows))
                    Activate(con, m.Id);
                break;
            case "update_favourite":
                ok = long.TryParse(Form1(form, "group_id"), out var group);
                if (ok)
                {
                    var favourites = Ids(PrefsOf(con)["favourites"]).Where(g => g != group);
                    SetPref(con, "favourites", List(Yes(Form1(form, "is_favourite")) ? favourites.Append(group) : favourites));
                }
                break;
            default:
                var key = call["update_".Length..] switch { "automatic_activation" => Switches[0], var other => other };
                SetPref(con, key, Yes(Form1(form, key)) ? 1 : 0);
                break;
        }
        con.Commit();
        return ok;
    }

    static readonly Regex Tag = new("<[^>]*>", RegexOptions.CultureInvariant), Spaces = new(@"\s+", RegexOptions.CultureInvariant);

    static string Unmarked(string html) => WebUtility.HtmlDecode(Spaces.Replace(Tag.Replace(html, " "), " ").Trim());

    /// <summary>The website's missions page (GET /account/missions): `missions` (every definition: id, title, desc without its tags, state `active`, `available`, `completed` or `locked`,
    /// objective titles, `objectives_done` (one flag per objective: the account holds it, all for a completed one) and `objective_weapons` (one list per objective of the weapons it accepts, one name per weapon, empty for an objective that names none: <see cref="WeaponsOnPage"/>), `needs` (the titles of the missions it depends on that are not completed yet, `#<id>` for one with no definition: why a `locked` one is locked; a restored active or completed mission may carry some too), reward text, and the first reserve, species and weapon name its filter lists name, "" for none), `groups` (id, title, desc, favourite, mission ids in order),
    /// `completed`, and the three switches. Null when the catalogue has no missions.</summary>
    public JsonObject? WebMissions(long account)
    {
        if (missions.Count == 0)
            return null;
        using var con = Con(account);
        con.Begin();
        EnsureMissions(con);
        var rows = MissionRows(con);
        var prefs = PrefsOf(con);
        con.Commit();
        var available = Available(rows).Select(m => m.Id).ToHashSet();
        string First(JsonNode? ids, Func<long, string?> name) => Ids(ids).Select(name).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? "";
        string Named(Dictionary<long, JsonObject> from, long id) => from.GetValueOrDefault(id) is { } o ? Py.Str(G(o, "name", "")).Split(" (")[0] : "";
        var favourites = Ids(prefs["favourites"]);
        var list = new JsonArray([.. Arr(G(application, "missions", null)).OfType<JsonObject>().Where(d => TryKey(d["id"], out _) && missions.ContainsKey(Py.L(d["id"]))).Select(d =>
        {
            var id = Py.L(d["id"]);
            return (JsonNode?)new JsonObject
            {
                ["id"] = id, ["title"] = Py.Str(G(d, "title", "")), ["desc"] = Unmarked(Py.Str(G(d, "desc", ""))),
                ["state"] = rows.TryGetValue(id, out var r) ? r.State == 3 ? "completed" : "active" : available.Contains(id) ? "available" : "locked",
                ["objectives"] = new JsonArray([.. missions[id].Objectives.Select(o => (JsonNode?)o.Title)]),
                // beside `objectives` (titles), not inside it: a launcher of an earlier release reads `objectives` as strings
                ["objectives_done"] = new JsonArray([.. missions[id].Objectives.Select(o => (JsonNode?)(rows.TryGetValue(id, out var row) && (row.State == 3 || row.Done.Contains(o.Id))))]),
                // the weapons each objective that names one accepts, one list per objective (empty for the others): beside `objectives`, as `objectives_done` is
                ["objective_weapons"] = new JsonArray([.. missions[id].Objectives.Select(o => (JsonNode?)new JsonArray([.. WeaponsOnPage(o.Rule).Select(n => (JsonNode?)n)]))]),
                ["needs"] = new JsonArray([.. missions[id].Dependencies.Where(n => !(rows.TryGetValue(n, out var done) && done.State == 3)).Select(n => (JsonNode?)(missions.TryGetValue(n, out var dep) ? dep.Title : $"#{n}"))]),
                ["reward"] = string.Join(", ", Arr(d["rewards"]).OfType<JsonObject>().Select(x => Py.Str(G(x, "text", "")))),
                ["reserve"] = First(d["reserves"], i => reserves.GetValueOrDefault(i)), ["species"] = First(d["species"], i => Named(species, i)),
                ["weapon"] = First(d["weapons"], i => Named(items, i)),
            };
        })]);
        var groups = new JsonArray([.. Arr(G(application, "mission_groups", null)).OfType<JsonObject>().Where(g => TryKey(g["id"], out _)).Select(g => (JsonNode?)new JsonObject
        {
            ["id"] = Py.L(g["id"]), ["title"] = Py.Str(G(g, "title", "")), ["desc"] = Unmarked(Py.Str(G(g, "desc", ""))),
            ["favourite"] = favourites.Contains(Py.L(g["id"])), ["missions"] = List(Ids(g["missions"])),
        })]);
        var reply = new JsonObject { ["missions"] = list, ["groups"] = groups, ["completed"] = rows.Count(r => r.Value.State == 3 && missions.ContainsKey(r.Key)) };
        foreach (var key in Switches)
            reply[key] = Py.Truthy(prefs[key]);
        return reply;
    }

    // ---- progress

    /// <summary>The hunt's event as the missions see it (called by Heartbeat after each stored event): a harvest (3), a spotted animal (11), a clue (12) or a
    /// place entered (101) finishes the first objective of each active mission that it meets. update 15 goes into `updates` (this heartbeat's reply),
    /// the toasts into `pending` (the next one's: after the trophy score update 6 of the harvest).</summary>
    void MissionEvent(Db con, JsonObject e, JsonArray updates)
    {
        var kind = Is(e["event"], 3) ? Kind.Harvest : Is(e["event"], 11) ? Kind.Spot : Is(e["event"], 12) ? Kind.Track : Is(e["event"], 101) ? Kind.Arrive : Kind.Other;
        if (kind == Kind.Other || (missions.Count == 0 && dailies.Count == 0 && community.Count == 0))
            return;
        var eventId = LastEvent(con);  // the event itself: the missions it meets are those activated before it
        EnsureMissions(con, eventId - 1);
        long? animalSpecies = null, gender = null;
        Kill? kill = null;
        if (kind != Kind.Arrive)
        {
            var animal = SpawnBefore(con, e["individual"], eventId);
            (animalSpecies, gender) = AnimalOf(animal, kind == Kind.Harvest ? e["extended_species"] : animal["species"]);
            kill = kind == Kind.Harvest ? KillOf(con, e, eventId, animal) : kind == Kind.Spot ? SpotOf(con, e, eventId, animal) : ClueOf(con, eventId, e, animal);
        }
        var arrival = ArrivalOf(con, eventId, e);
        Advance(con, kind, (rule, after) =>
            kind == Kind.Arrive ? arrival is not null && ArrivesAt(rule, arrival)
            : (rule.Species is null || rule.Species == animalSpecies) && (rule.Gender is null || rule.Gender == gender) && (kill is null || Meets(rule, kill, after)),
            Into(updates), eventId);
    }

    /// <summary>One update 15 per currency in a reply, as the game's server sends: a kill's css and a mission's reward add into the last balance.</summary>
    static Action<JsonObject> Into(JsonArray updates) => u =>
    {
        if (updates.OfType<JsonObject>().LastOrDefault(x => Is(x["update"], 15) && Py.Str(x["type"]) == Py.Str(u["type"])) is { } same)
            same["amount"] = u["amount"]!.DeepClone();
        else
            updates.Add(u);
    };

    /// <summary>Does the objective's title ask for a harvest in words: is its first word, after `Then`, `At any point ...` and the like, `harvest`? A title that asks to kill or to take an
    /// animal is a harvest objective too (<see cref="Kind.Harvest"/>), and so is any title an operator's rule makes one; only a title that says harvest counts for a member who harvests another's kill (#1041).</summary>
    static bool AsksHarvest(string title)
    {
        var rest = title[OrderedWord.Match(title).Length..];
        return Normalise(rest[AtAny.Match(rest).Length..]).Split(' ')[0] == "harvest";
    }

    /// <summary>A harvest the heartbeat credited to the member who shot the animal last (#1041; <see cref="TryCredit"/>): `theirs` is the shooter's account, where it is event `shot`,
    /// `con` the harvester's, where it is still event `eventId`. The harvester's active missions (not the daily mission, the community missions or the challenges) get progress on the objectives
    /// whose title asks for a harvest (<see cref="AsksHarvest"/>), judged on the shooter's hits and the animal's score as the shooter's are. The animal is the shooter's spawn of it, else, when the
    /// shooter's events have none, the harvester's `e7` (the shooter's own missions then know no gender, coat or weight; the harvester's do). Not on an objective with a sequence (within a time, one
    /// after another, the same shot, different animals): it counts the harvester's own harvests, which this is not. The objectives it finishes are `kept` (<see cref="Mark"/>): the harvester's stored
    /// events have no harvest that meets them. update 15 goes into `updates`, the toasts into `pending`.</summary>
    void HarvesterEvent(Db con, Db theirs, JsonObject e3, JsonObject e7, long shot, long eventId, JsonArray updates)
    {
        if (missions.Count == 0)
            return;
        EnsureMissions(con, eventId - 1);
        var animal = SpawnBefore(theirs, e3["individual"], shot) is { Count: > 0 } seen ? seen : e7;
        var (animalSpecies, gender) = AnimalOf(animal, e3["extended_species"]);
        var kill = KillOf(theirs, e3, shot, animal);
        Advance(con, Kind.Harvest, (rule, after) => (rule.Species is null || rule.Species == animalSpecies) && (rule.Gender is null || rule.Gender == gender)
            && !(rule.Tests ?? []).Any(t => IsSequence(t.Check)) && Meets(rule, kill, after), Into(updates), eventId, credited: true);
    }

    /// <summary>A hunt starts at location `lodge` (Launch/start, inside its transaction): what a single-hunt mission had done is forgotten, then the
    /// `Start at ...` objectives of the lodge are met. update 15 goes into `pending`.</summary>
    void MissionLaunch(Db con, long? lodge)
    {
        if (missions.Count == 0 && dailies.Count == 0 && community.Count == 0)
            return;
        EnsureMissions(con);
        foreach (var (id, (state, done)) in MissionRows(con))
            if (state == 2 && done.Count > 0 && missions.TryGetValue(id, out var m) && m.SingleExpedition)
                con.Exec("UPDATE missions SET objectives = '[]', kept = '[]', earlier = '[]' WHERE account_id = $a AND mission_id = ?", id);
        Advance(con, Kind.Start, (rule, _) => StartsAt(rule, lodge), u => con.Exec("INSERT INTO pending (account_id, body) VALUES ($a, ?)", Py.Dumps(u)), long.MaxValue);
    }

    /// <summary>Finish the first unfinished objective of this kind that `meets` accepts (`credited`, a harvest of an animal another member shot: only one whose title asks for a harvest,
    /// <see cref="AsksHarvest"/>); an ordered objective only once every one before it is finished; `after` is the last event the progress is not owed from: its id is added to `done`, and it is returned; null when none is met.</summary>
    static Objective? Meet(MissionDef m, List<long> done, Kind kind, Func<Rule, long, bool> meets, long after = 0, bool credited = false)
    {
        for (var i = 0; i < m.Objectives.Length; i++)
        {
            var o = m.Objectives[i];
            if (o.Rule.Kind != kind || done.Contains(o.Id) || (o.Rule.Ordered && Before(m, i).Any(p => !done.Contains(p.Id))) || credited && !AsksHarvest(o.Title) || !meets(o.Rule, after))
                continue;
            done.Add(o.Id);
            if (o.Rule.Together)  // one animal finishes every objective that asks the same (the operator's `together`)
                done.AddRange(m.Objectives.Where(p => p.Id != o.Id && !done.Contains(p.Id) && p.Rule.Together && p.Rule.Text == o.Rule.Text).Select(p => p.Id));
            return o;
        }
        return null;
    }

    /// <summary>For each active mission that was activated before the event `eventId`, finish its first unfinished objective of this kind that `meets` accepts; then the same
    /// for today's daily mission, and the community missions count it. `credited`: a harvest of an animal another member shot (<see cref="HarvesterEvent"/>): only the missions, only objectives whose title
    /// asks for a harvest, each kept (<see cref="Mark"/>).</summary>
    void Advance(Db con, Kind kind, Func<Rule, long, bool> meets, Action<JsonObject> now, long eventId, bool credited = false)
    {
        var marks = Marks(con);
        foreach (var (id, (state, done)) in MissionRows(con))
        {
            if (state != 2 || !missions.TryGetValue(id, out var m) || marks[id].Seen >= eventId)
                continue;
            var had = done.ToArray();
            if (Meet(m, done, kind, meets, marks[id].Seen, credited) is not { } o)
                continue;
            Stamp(con, m, m.Objectives.Where(x => done.Contains(x.Id) && !had.Contains(x.Id)));
            if (credited)
                con.Exec("UPDATE missions SET kept = json_insert(kept, '$[#]', ?) WHERE account_id = $a AND mission_id = ?", o.Id, m.Id);
            if (m.Objectives.Any(x => !done.Contains(x.Id)))
            {
                con.Exec("UPDATE missions SET objectives = ? WHERE account_id = $a AND mission_id = ?", Py.Dumps(List(done)), m.Id);
                Toast(con, $"Mission \"{m.Title}\", objective \"{o.Title}\" completed.");
            }
            else
                Complete(con, m, now);
        }
        if (credited)
            return;
        DailyAdvance(con, kind, meets, now);
        CommunityCount(con, kind, rule => meets(rule, 0));
    }

    /// <summary>The rule the objectives are finished under is kept with the row (`missions.rules`: objective id to <see cref="Fingerprint"/>), for the audit.</summary>
    void Stamp(Db con, MissionDef m, IEnumerable<Objective> finished)
    {
        var rules = Parse(con.Scalar("SELECT rules FROM missions WHERE account_id = $a AND mission_id = ?", m.Id)!);
        foreach (var o in finished)
            rules[o.Id.ToString()] = Fingerprint(m, o);
        con.Exec("UPDATE missions SET rules = ? WHERE account_id = $a AND mission_id = ?", Py.Dumps(rules), m.Id);
    }

    /// <summary>The last objective is finished: the mission is completed, its gm$ and em$ are paid (update 15 with the new balance, a wallet row each),
    /// the toast follows, and the missions it unlocks are activated when the account's switches say so (counting the events after `at`, default the last one).</summary>
    void Complete(Db con, MissionDef m, Action<JsonObject> now, long? at = null)
    {
        con.Exec("UPDATE missions SET state = 3, objectives = '[]' WHERE account_id = $a AND mission_id = ?", m.Id);
        foreach (var (currency, amount) in new[] { ("gm", m.Gm), ("em", m.Em) })
            if (amount > 0 && AddCurrency(con, currency, amount, $"{currency}$ reward") is { } balance)
                now(new JsonObject { ["amount"] = balance, ["type"] = currency, ["update"] = 15 });
        Toast(con, $"Mission {m.Title} completed.");
        var prefs = PrefsOf(con);
        var (auto, onlyFavourite) = (Py.Truthy(prefs["auto_mission_activate"]), Py.Truthy(prefs["activate_only_favourite"]));
        if (!auto && !onlyFavourite)
            return;
        var favourites = Ids(prefs["favourites"]);
        foreach (var next in Available(MissionRows(con)).Where(n => n.Dependencies.Contains(m.Id)).ToList())
            if (!onlyFavourite || missionGroups.Any(g => favourites.Contains(g.Key) && g.Value.Contains(next.Id)))
                Activate(con, next.Id, at);
    }

    void Toast(Db con, string text) => con.Exec("INSERT INTO pending (account_id, body) VALUES ($a, ?)", Py.Dumps(new JsonObject
    {
        ["text"] = text, ["colour"] = COLOUR, ["destination"] = MediaUrl("doc_64.jpg"), ["update"] = 3, ["timer"] = -1, ["type"] = 100,
    }));
}
