// `mission-texts <folder> build|todo|check`: builds and checks the mission text files of the setting `mission_texts_dir` (docs/server.md, "Mission texts"). It needs no settings file; the folder is not in the repository (game text).
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Resurgence.Server;

public static class MissionTextsTool
{
    public const string Usage = """
        Usage (repo root): dotnet run --project server/Server -- mission-texts <folder> build <english catalogue.json> <folder of app-<locale>.json>
                           dotnet run --project server/Server -- mission-texts <folder> todo
                           dotnet run --project server/Server -- mission-texts <folder> check
        <folder> is the setting `mission_texts_dir` (default <data_dir>/mission-texts); build creates it. build reads the English Application/application reply and, in the second folder, the same reply
        for each language (app-de_DE.json, app-pt_BR.json, ...), and writes en.json (every English text by key), <lang>.original.json (the original's texts that differ from the English one),
        names/<lang>.json (the names that stay as the game shows them) and, where missing, an empty <lang>.mt.json; then it does todo. todo writes todo/<lang>.json: the English texts that neither
        the original nor <lang>.mt.json has. check verifies every <lang>.mt.json (keys that exist, texts that are not empty, protected names kept); it prints one line per problem and exits 1 when there is one.
        """;

    public const string English = "en.json";

    static readonly JsonSerializerOptions Plain = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Runs the command (the words after `mission-texts`: the folder, the command and its arguments); 0 when it did what was asked.</summary>
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            switch (args)
            {
                case [var dir, "build", var english, var folder]:
                    Build(dir, english, folder, output);
                    return 0;
                case [var dir, "todo"]:
                    Todo(dir, output);
                    return 0;
                case [var dir, "check"]:
                    var problems = Check(dir);
                    foreach (var p in problems) output.WriteLine(p);
                    output.WriteLine(problems.Count == 0 ? "mission texts: ok" : $"mission texts: {problems.Count} problem(s)");
                    return problems.Count == 0 ? 0 : 1;
                default:
                    error.WriteLine(Usage);
                    return 1;
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            error.WriteLine(e.Message);
            return 1;
        }
    }

    // ---- the files

    static string FileOf(string dir, string lang, string kind) => Path.Combine(dir, $"{lang}.{kind}.json");

    static Dictionary<string, string> ReadOrEmpty(string path) => File.Exists(path) ? MissionTexts.ReadFile(path) : [];

    /// <summary>One entry a line: a text file stays readable and a change of one text is one changed line. LF endings, no byte order mark.</summary>
    static void WriteFlat(string path, IEnumerable<KeyValuePair<string, string>> entries)
    {
        var sb = new StringBuilder();
        foreach (var (key, text) in entries)
            sb.Append(sb.Length == 0 ? "{\n  " : ",\n  ").Append(JsonSerializer.Serialize(key, Plain)).Append(": ").Append(JsonSerializer.Serialize(text, Plain));
        sb.Append(sb.Length == 0 ? "{}\n" : "\n}\n");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    static JsonObject ReadCatalogue(string path) => JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException($"{path}: not a catalogue reply");

    // ---- the texts

    static bool Blank(string? text) => string.IsNullOrWhiteSpace(text);

    /// <summary>The catalogue's mission and group texts by key, in the order of the catalogue: `m&lt;id&gt;.title`, `m&lt;id&gt;.desc`, `m&lt;id&gt;.o&lt;objective id&gt;`, `g&lt;id&gt;.title`, `g&lt;id&gt;.desc`.
    /// A text that is empty is left out.</summary>
    public static List<KeyValuePair<string, string>> CatalogueTexts(JsonObject catalogue)
    {
        List<KeyValuePair<string, string>> texts = [];
        void Add(string key, JsonNode? node)
        {
            if (node is JsonValue v && v.TryGetValue<string>(out var text) && !Blank(text))
                texts.Add(new(key, text));
        }
        foreach (var m in (catalogue["missions"] as JsonArray ?? []).OfType<JsonObject>().Where(m => Py.IsNum(m["id"])))
        {
            var id = Py.L(m["id"]);
            Add($"m{id}.title", m["title"]);
            Add($"m{id}.desc", m["desc"]);
            foreach (var o in (m["objectives"] as JsonArray ?? []).OfType<JsonObject>().Where(o => Py.IsNum(o["id"])))
                Add($"m{id}.o{Py.L(o["id"])}", o["title"]);
        }
        foreach (var g in (catalogue["mission_groups"] as JsonArray ?? []).OfType<JsonObject>().Where(g => Py.IsNum(g["id"])))
        {
            Add($"g{Py.L(g["id"])}.title", g["title"]);
            Add($"g{Py.L(g["id"])}.desc", g["desc"]);
        }
        return texts;
    }

    /// <summary>The English texts the catalogue and the facts file hold: the catalogue's (<see cref="CatalogueTexts"/>), then the daily missions' (`d&lt;template&gt;.title`, `d&lt;template&gt;.o&lt;n&gt;`, n from 1) and the
    /// community missions' (`c&lt;event&gt;.&lt;id&gt;.title`).</summary>
    public static List<KeyValuePair<string, string>> EnglishTexts(JsonObject catalogue, Facts facts)
    {
        var texts = CatalogueTexts(catalogue);
        foreach (var t in facts.DailyTemplates)
        {
            texts.Add(new($"d{t.Id}.title", t.Title));
            texts.AddRange(t.Objectives.Select((o, i) => new KeyValuePair<string, string>($"d{t.Id}.o{i + 1}", o)));
        }
        foreach (var name in facts.EventNames)
            foreach (var c in (facts.EventOf(name)?["community"] as JsonArray ?? []).OfType<JsonObject>())
                if (c["title"] is JsonValue title && title.TryGetValue<string>(out var text) && !Blank(text))
                    texts.Add(new($"c{name}.{Py.L(c["id"])}.title", text));
        return texts;
    }

    // ---- build

    static void Build(string dir, string englishFile, string folder, TextWriter output)
    {
        var catalogue = ReadCatalogue(englishFile);
        var english = EnglishTexts(catalogue, Facts.Default);
        var englishByKey = english.ToDictionary(e => e.Key, e => e.Value);
        WriteFlat(Path.Combine(dir, English), english);
        output.WriteLine($"{English}: {english.Count} texts");
        foreach (var file in Directory.GetFiles(folder, "app-*.json").Order())
        {
            var locale = Path.GetFileNameWithoutExtension(file)["app-".Length..];
            if (MissionTexts.Code(locale) is not { } lang)
                continue;  // English, or a file that is no language of the launcher
            var theirs = ReadCatalogue(file);
            var translated = CatalogueTexts(theirs).ToDictionary(e => e.Key, e => e.Value);
            var original = english.Where(e => translated.TryGetValue(e.Key, out var t) && t != e.Value).Select(e => new KeyValuePair<string, string>(e.Key, translated[e.Key])).ToList();
            WriteFlat(FileOf(dir, lang, "original"), original);
            if (!File.Exists(FileOf(dir, lang, "mt")))
                WriteFlat(FileOf(dir, lang, "mt"), []);
            var names = NamesOf(catalogue, theirs, english.Select(e => e.Value));
            WriteFlat(Path.Combine(dir, "names", $"{lang}.json"), names.OrderBy(n => n.Key, StringComparer.Ordinal));
            output.WriteLine($"{lang}: {original.Count} original texts, {names.Count} names");
        }
        Todo(dir, output);
    }

    /// <summary>The names of the catalogue's species, items, reserves and places (<c>name</c>) in English and in the language's reply, by id; only those that some English text names.</summary>
    static Dictionary<string, string> NamesOf(JsonObject english, JsonObject theirs, IEnumerable<string> texts)
    {
        Dictionary<string, string> names = new(StringComparer.Ordinal);
        foreach (var section in new[] { "species", "items", "reserves", "locations" })
        {
            var local = (theirs[section] as JsonArray ?? []).OfType<JsonObject>().Where(o => Py.IsNum(o["id"])).GroupBy(o => Py.L(o["id"])).ToDictionary(g => g.Key, g => g.First());
            foreach (var o in (english[section] as JsonArray ?? []).OfType<JsonObject>().Where(o => Py.IsNum(o["id"])).OrderBy(o => Py.L(o["id"])))
                if (local.TryGetValue(Py.L(o["id"]), out var l) && o["name"] is JsonValue a && a.TryGetValue<string>(out var en) && l["name"] is JsonValue b && b.TryGetValue<string>(out var game))
                {
                    Add(names, en, game);
                    if (section is "species" or "items")  // a text names a model without its variant: `Whitetail Deer`, not `Whitetail Deer (Albino)`
                        Add(names, en.Split(" (")[0], game.Split(" (")[0]);
                }
        }
        var matcher = new NameMatcher(names);
        var used = texts.SelectMany(matcher.Found).ToHashSet(StringComparer.Ordinal);
        return names.Where(n => used.Contains(n.Key)).ToDictionary(n => n.Key, n => n.Value);
    }

    // a name worth protecting has a capital or a digit (`Tripod Stand`, `.243 Winchester`, `Tower 7`): a plain word such as `scope` is not a name
    static void Add(Dictionary<string, string> names, string english, string game)
    {
        english = english.Trim();
        game = game.Split('\n')[0].Trim();  // an item's name may carry a second line (what it fits)
        if (english.Length >= 3 && game.Length > 0 && english.Any(c => char.IsUpper(c) || char.IsDigit(c)))
            names.TryAdd(english, game);
    }

    // ---- todo

    static void Todo(string dir, TextWriter output)
    {
        var english = MissionTexts.ReadFile(Path.Combine(dir, English));
        foreach (var lang in MissionTexts.Languages)
        {
            var original = ReadOrEmpty(FileOf(dir, lang, "original"));
            var ours = ReadOrEmpty(FileOf(dir, lang, "mt"));
            var missing = english.Where(e => !original.ContainsKey(e.Key) && !ours.ContainsKey(e.Key)).ToList();
            WriteFlat(Path.Combine(dir, "todo", $"{lang}.json"), missing);
            output.WriteLine($"todo/{lang}.json: {missing.Count} texts");
        }
    }

    // ---- the names that stay

    /// <summary>The protected names of one language, found in a text and looked for in its translation. A name is found as a whole word (a plural `s` or `es` may follow it); the longest name
    /// wins where names overlap (`Mule Deer` is not also `Deer`).</summary>
    public sealed class NameMatcher
    {
        readonly Dictionary<string, string> names;
        readonly Regex? any;

        /// <summary>English name to the word the game shows in the language.</summary>
        public NameMatcher(IReadOnlyDictionary<string, string> names)
        {
            this.names = new(names, StringComparer.Ordinal);
            any = names.Count == 0 ? null : new Regex(@"(?<![\p{L}\p{N}])(" + string.Join('|', names.Keys.OrderByDescending(n => n.Length).ThenBy(n => n, StringComparer.Ordinal).Select(Regex.Escape)) + @")(?:e?s)?(?![\p{L}\p{N}])",
                RegexOptions.CultureInvariant);
        }

        /// <summary>The protected names the English text contains.</summary>
        public IEnumerable<string> Found(string english) => any is null ? [] : any.Matches(english).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal);

        /// <summary>The protected names of the English text that the translation lacks: each must be there unchanged, or as the game's word. Case, spaces and punctuation do not count (`Sambar-Hirsch`
        /// is `Sambarhirsch`), and an inflected word does (`Weißwedelhirsche`, `волка` for `волк`): every word of the game's name is looked for without its last one to three letters.</summary>
        public IReadOnlyList<string> Missing(string english, string translation)
        {
            var text = Squash(translation);
            return [.. Found(english).Where(n => !text.Contains(Squash(n), StringComparison.Ordinal) && !Stems(names[n]).All(s => text.Contains(s, StringComparison.Ordinal)))];
        }

        static string Squash(string s) => string.Concat(s.Where(char.IsLetterOrDigit)).ToLowerInvariant();

        static IEnumerable<string> Stems(string name) =>
            name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(w => Squash(w.Length >= 7 ? w[..^3] : w.Length >= 5 ? w[..^2] : w.Length >= 4 ? w[..^1] : w)).Where(s => s.Length > 0);
    }

    // ---- check

    /// <summary>Every problem of the <c>&lt;lang&gt;.mt.json</c> files: a key the English texts lack, an empty text, a protected name that is not kept.</summary>
    public static List<string> Check(string dir)
    {
        var english = MissionTexts.ReadFile(Path.Combine(dir, English));
        List<string> problems = [];
        foreach (var lang in MissionTexts.Languages)
        {
            Dictionary<string, string> ours;
            NameMatcher matcher;
            try
            {
                ours = ReadOrEmpty(FileOf(dir, lang, "mt"));
                if (ours.Count == 0)
                    continue;
                var namesFile = Path.Combine(dir, "names", $"{lang}.json");
                matcher = new NameMatcher(File.Exists(namesFile) ? MissionTexts.ReadFile(namesFile) : throw new InvalidDataException($"{namesFile}: missing, but {lang}.mt.json has texts"));
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                problems.Add($"{lang}: {e.Message}");  // a file that cannot be read (broken JSON, a duplicate key): the server would show English for the language
                continue;
            }
            foreach (var (key, text) in ours)
                if (!english.TryGetValue(key, out var source)) problems.Add($"{lang} {key}: no such text");
                else if (Blank(text)) problems.Add($"{lang} {key}: empty");
                else if (matcher.Missing(source, text) is { Count: > 0 } lost) problems.Add($"{lang} {key}: protected name lost: {string.Join(", ", lost)}");
        }
        return problems;
    }
}
