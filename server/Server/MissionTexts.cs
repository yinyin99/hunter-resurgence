// Mission texts in the player's language (docs/server.md, "Mission texts"): titles, descriptions and objectives are display only, the rules always read the English ones.
// A text is looked up by key in the original's own translation, then in ours (machine translated), else it stays English.
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed class MissionTexts
{
    /// <summary>The 18 languages with texts: the launcher's, English apart.</summary>
    public static readonly string[] Languages = [.. Games.Languages.Where(l => l != "en")];

    /// <summary>No texts at all: every text stays English.</summary>
    public static MissionTexts None => new([], []);

    readonly Dictionary<string, Dictionary<string, string>> original, ours;
    readonly ConditionalWeakTable<byte[], ConcurrentDictionary<string, byte[]>> localized = [];

    /// <summary>Texts by language code (`fr`, `pt-BR`) and key: <paramref name="original"/> is what the game's original served, <paramref name="ours"/> our translation of what it did not.</summary>
    public MissionTexts(Dictionary<string, Dictionary<string, string>> original, Dictionary<string, Dictionary<string, string>> ours) => (this.original, this.ours) = (original, ours);

    /// <summary>The texts of the files in <paramref name="dir"/> (setting `mission_texts_dir`, which is not in the repository: it is game text): per language `&lt;code&gt;.original.json` and `&lt;code&gt;.mt.json`.
    /// A missing folder, a missing file or a file that cannot be read as a flat JSON object of texts (broken JSON, a duplicate key, a text that is not a string) means no texts for it (English), never an error;
    /// <paramref name="log"/> (the server's start; none for the admin commands) gets one line for it and one for what was loaded.</summary>
    public static MissionTexts Load(string dir, TextWriter? log = null)
    {
        if (!Directory.Exists(dir))
        {
            log?.WriteLine($"mission texts: no folder {dir}: the missions stay English");
            return None;
        }
        Dictionary<string, Dictionary<string, string>> Read(string suffix)
        {
            Dictionary<string, Dictionary<string, string>> read = [];
            foreach (var lang in Languages)
            {
                var path = Path.Combine(dir, $"{lang}.{suffix}.json");
                if (!File.Exists(path))
                    continue;
                try { read[lang] = ReadFile(path); }
                catch (Exception e)  // whatever is wrong with a file of ours: the language stays English, the server starts
                {
                    log?.WriteLine($"mission texts: {path} is not read ({e.Message}): English for it");
                }
            }
            return read;
        }
        var original = Read("original");
        var ours = Read("mt");
        log?.WriteLine($"mission texts: {dir}: {string.Join(", ", original.Keys.Union(ours.Keys).Order())}");
        return new(original, ours);
    }

    /// <summary>A flat JSON object of texts by key; <see cref="InvalidDataException"/> (with the file's name) for anything else: not JSON, not an object, a duplicate key, a value that is not a string.</summary>
    public static Dictionary<string, string> ReadFile(string path)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject o && o.All(p => p.Value is JsonValue v && v.TryGetValue<string>(out _)))
                return o.ToDictionary(p => p.Key, p => (string)p.Value!);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException($"{path}: {e.Message}", e);
        }
        throw new InvalidDataException($"{path}: not a flat JSON object of texts");
    }

    /// <summary>The code of the language a request names, one of <see cref="Languages"/>: the launcher's code (`pt-BR`, `fr`) or the game's (`fr_FR`, `pt_br`), a regional tag as its language (`de-AT`), `no` and `nn` as
    /// Norwegian bokmål; null for English and for anything else (a bare `pt` too: it names no one of the two).</summary>
    public static string? Code(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;
        if (Array.IndexOf(Languages, tag) >= 0)  // already a code: a caller that read it once asks again for each text
            return tag;
        tag = tag.Trim().Replace('_', '-');
        var code = Games.LanguageOf(tag);
        if (code.Length == 0)
        {
            var primary = tag.Split('-')[0].ToLowerInvariant();
            code = primary is "no" or "nn" ? "nb" : primary == "pt" ? "" : Games.LanguageOf(primary);
        }
        return code is "" or "en" ? null : code;
    }

    /// <summary>The language the request asks for (its `lang` or `locale`, in the query or the form), as <see cref="Code"/> reads it.</summary>
    public static string? RequestLanguage(Req r) =>
        Replay.RequestParams(r.Query, r.Body, r.Header("Content-Type")).FirstOrDefault(p => p.Name is "lang" or "locale").Values?.FirstOrDefault();

    /// <summary>The text of <paramref name="key"/> in the language: the original's, else ours, else <paramref name="english"/>.</summary>
    public string Text(string? lang, string key, string english) =>
        Code(lang) is { } code && (original.GetValueOrDefault(code)?.GetValueOrDefault(key) ?? ours.GetValueOrDefault(code)?.GetValueOrDefault(key)) is { Length: > 0 } text ? text : english;

    bool Has(string code) => original.ContainsKey(code) || ours.ContainsKey(code);

    /// <summary>The catalogue's missions and mission groups (their titles, descriptions and objectives) in the language, in place. The ids and everything else stay as they are.</summary>
    public void Apply(JsonObject catalogue, string? lang)
    {
        if (Code(lang) is not { } code || !Has(code))
            return;
        void Set(JsonObject o, string field, string key)
        {
            if (o[field] is JsonValue v && v.TryGetValue<string>(out var english) && Text(code, key, english) is var text && text != english)
                o[field] = text;
        }
        foreach (var m in (catalogue["missions"] as JsonArray ?? []).OfType<JsonObject>().Where(m => Py.IsNum(m["id"])))
        {
            var id = Py.L(m["id"]);
            Set(m, "title", $"m{id}.title");
            Set(m, "desc", $"m{id}.desc");
            foreach (var o in (m["objectives"] as JsonArray ?? []).OfType<JsonObject>().Where(o => Py.IsNum(o["id"])))
                Set(o, "title", $"m{id}.o{Py.L(o["id"])}");
        }
        foreach (var g in (catalogue["mission_groups"] as JsonArray ?? []).OfType<JsonObject>().Where(g => Py.IsNum(g["id"])))
        {
            Set(g, "title", $"g{Py.L(g["id"])}.title");
            Set(g, "desc", $"g{Py.L(g["id"])}.desc");
        }
    }

    /// <summary>A recorded catalogue reply in the language (kept for the next request of that language); the reply itself for English or a language without texts.</summary>
    public byte[] Localize(byte[] reply, string? lang)
    {
        if (Code(lang) is not { } code || !Has(code))
            return reply;
        return localized.GetOrCreateValue(reply).GetOrAdd(code, _ =>
        {
            try
            {
                if (JsonNode.Parse(reply) is not JsonObject catalogue)
                    return reply;
                Apply(catalogue, code);
                return Py.DumpBytes(catalogue);
            }
            catch (System.Text.Json.JsonException)
            {
                return reply;  // not a plain JSON reply (a callback around it): left as it is
            }
        });
    }
}
