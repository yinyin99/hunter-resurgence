// The seasonal event the operator turns on (settings.json `event`, docs/server.md#seasonal-events): the connect reply's `ingame_event`
// while its window runs.
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Resurgence.Server;

/// <summary>An event the game build knows by <paramref name="Name"/> and <paramref name="Define"/>; <paramref name="Reserves"/> are the game's own reserve numbers
/// (the lodge id / 100), empty for every reserve. It runs from <paramref name="Start"/> to <paramref name="End"/> (exclusive).</summary>
public sealed partial record SeasonalEvent(string Name, string Define, int[] Reserves, DateTimeOffset Start, DateTimeOffset End)
{
    public bool Running(double now) => now >= Start.ToUnixTimeSeconds() && now < End.ToUnixTimeSeconds();

    public bool Covers(int reserve) => Reserves.Length == 0 || Reserves.Contains(reserve);

    /// <summary>The connect reply's `ingame_event`.</summary>
    public JsonObject Json() => new() { ["name"] = Name, ["define"] = Define, ["reserves"] = new JsonArray([.. Reserves.Select(r => (JsonNode?)r)]) };

    [GeneratedRegex("^[a-z0-9_]{1,64}$")] private static partial Regex NameRule();
    [GeneratedRegex("^[A-Z0-9_]{1,64}$")] private static partial Regex DefineRule();

    /// <summary>The `event` of settings.json: null for none; InvalidDataException says what is wrong.</summary>
    public static SeasonalEvent? Parse(JsonNode? node)
    {
        if (node is null) return null;
        var o = node as JsonObject ?? throw new InvalidDataException("event: an object {name, define, reserves, start, end}, or null");
        string Text(string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new InvalidDataException($"event.{key}: missing or not text");
        var name = Text("name");
        var define = Text("define");
        if (!NameRule().IsMatch(name)) throw new InvalidDataException("event.name: lower case letters, digits and _, at most 64");
        if (!DefineRule().IsMatch(define)) throw new InvalidDataException("event.define: upper case letters, digits and _, at most 64");
        var reserves = new List<int>();
        foreach (var r in o["reserves"] as JsonArray ?? throw new InvalidDataException("event.reserves: a list of reserve numbers (empty: every reserve)"))
            reserves.Add(r is JsonValue v && v.TryGetValue<int>(out var n) && n is >= 0 and < 100 ? n : throw new InvalidDataException("event.reserves: whole numbers from 0 to 99"));
        DateTimeOffset At(string key) => DateTimeOffset.TryParse(Text(key), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t.ToUniversalTime()
            : throw new InvalidDataException($"event.{key}: a date and time, e.g. 2026-10-22T00:00:00Z");
        var (start, end) = (At("start"), At("end"));
        return end > start ? new(name, define, [.. reserves], start, end) : throw new InvalidDataException("event: end is not after start");
    }
}
