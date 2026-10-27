using System.IO.Compression;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Backup;

namespace Resurgence.Tests.BackupTool;

/// <summary>The competition-history group: the list is paged by its total, one raw reply per edition, the player's row picked by user id. Hand-made fixtures, fake host.</summary>
public class HistoryTests
{
    static readonly string Tok = new('a', 40);
    const string Previous = "/v1/Page_content/list_previous_competitions", League = "/v1/Page_content/list_community_league_competitions", Edition = "/v1/Page_content/competitions_new";

    static string EditionRow(int id) => "{\"id\":" + id + ",\"start\":1000,\"end\":2000,\"finished\":1,\"entrants\":5,\"type\":{\"id\":77,\"name\":\"Test Type\"}}";

    static string PreviousPage(int total, IEnumerable<int> ids) => "{\"total\":" + total + ",\"competitions\":[" + string.Join(",", ids.Select(EditionRow)) + "]}";

    static string Entrant(int user, string handle, int position, string points) =>
        "{\"entrant_id\":" + user + "0,\"position\":" + position + ",\"points\":" + points + ",\"user\":{\"id\":" + user + ",\"handle\":\"" + handle + "\"}}";

    /// <summary>An edition reply: one info row, the entrants as given, <paramref name="older"/> as the list of earlier editions of the same type.</summary>
    static string EditionReply(int id, string[] entrants, int? total = null, int[]? older = null) =>
        "{\"info\":[" + EditionRow(id) + "],\"entrants_total\":" + (total ?? entrants.Length) + ",\"entrants\":[" + string.Join(",", entrants) + "],\"competitions\":[" + string.Join(",", (older ?? []).Select(EditionRow)) + "],\"competitions_total\":" + (older?.Length ?? 0) + "}";

    static HttpResponseMessage Reply(string body) => new() { Content = new StringContent(body) };

    static string Field(string form, string key) => Regex.Match(form, $"(?:^|&){key}=([^&]*)").Groups[1].Value;

    /// <summary>A fake whose lists and editions come from the given functions.</summary>
    static Fake World(Func<int, string> previous, Func<int, string> edition, string league = "[]", Func<int, HttpResponseMessage?>? broken = null) => new(Tok)
    {
        Custom = (path, form) => path switch
        {
            Previous => Reply(previous(int.Parse(Field(form, "offset")))),
            League => Reply(league),
            Edition => broken?.Invoke(int.Parse(Field(form, "id"))) ?? Reply(edition(int.Parse(Field(form, "id")))),
            _ => null,
        },
    };

    static async Task<Engine> Run(Fake f, TempDir t, bool older = false, bool resume = true, bool zip = false)
    {
        var e = Hosts.Make(f, Path.Combine(t.Path, "claim.key"));
        e.OlderEditions = older;
        await e.Run(t.Path, "", () => Tok, new HashSet<string> { "history" }, resume, zip, TestContext.Current.CancellationToken);
        return e;
    }

    static JsonNode History(TempDir t, string folder = "backup-7") => JsonNode.Parse(File.ReadAllText(Path.Combine(t.Path, folder, "competition-history.json")))!;

    static int Requests(Fake f, string path) => f.Seen.Count(p => p == path);

    static string[] Offsets(Fake f) => [.. f.Forms.Where(x => x.Contains("offset=")).Select(x => Field(x, "offset"))];

    static int[] EditionIds(Fake f) => [.. f.Forms.Where(x => x.Contains("entrants_limit=")).Select(x => int.Parse(Field(x, "id")))];

    /// <summary>Off by default, and public reads: it needs no game session.</summary>
    [Fact]
    public void GroupIsOptInAndNeedsNoSession()
    {
        var g = Engine.Groups.Single(x => x.Key == "history");
        Assert.True(!g.On && g.Needs == Engine.Session.None, "history: off by default, Session.None");
    }

    /// <summary>The list is paged with limit 20 and stops by its total: 45 gives the offsets 0, 20 and 40; the last page's editions are each asked for once.</summary>
    [Fact]
    public async Task ListIsPagedByTheTotal()
    {
        using var t = new TempDir("backup-test-history-");
        // like the server: an offset past the end gives the last page again
        var f = World(off => PreviousPage(45, Enumerable.Range(1, 45).Skip(Math.Min(off, 40)).Take(20)), id => EditionReply(id, []));
        await Run(f, t);
        Assert.True(Offsets(f).SequenceEqual(["0", "20", "40"]) && f.Forms.Where(x => x.Contains("offset=")).All(x => x.Contains("limit=20")), $"offsets {string.Join(",", Offsets(f))} with limit 20");
        Assert.True(EditionIds(f).Order().SequenceEqual(Enumerable.Range(1, 45)) && Requests(f, Edition) == 45, "45 editions, each once");
        Assert.True(History(t)["totals"]!["editions"]!.GetValue<int>() == 45, "all 45 are in the derived file");
    }

    /// <summary>A server that repeats a page does not loop and does not duplicate an edition.</summary>
    [Fact]
    public async Task RepeatedPageCausesNoLoopAndNoDuplicate()
    {
        using var t = new TempDir("backup-test-history-");
        var f = World(_ => PreviousPage(45, Enumerable.Range(1, 20)), id => EditionReply(id, []));
        await Run(f, t);
        Assert.True(Offsets(f).Length == 3 && Requests(f, Edition) == 20 && History(t)["editions"]!.AsArray().Count == 20, "3 list requests, 20 editions, once each");
    }

    /// <summary>A total of 0: the first page is asked (it holds the total), no edition is.</summary>
    [Fact]
    public async Task TotalZeroAsksForNoEdition()
    {
        using var t = new TempDir("backup-test-history-");
        var f = World(_ => PreviousPage(0, []), id => EditionReply(id, []));
        await Run(f, t);
        Assert.True(Offsets(f).SequenceEqual(["0"]) && Requests(f, Edition) == 0 && History(t)["editions"]!.AsArray().Count == 0, "one list request, no edition request, an empty history");
    }

    /// <summary>The own row is found by user id (not handle, not index): positions 1 and 9, and not_listed. The raw replies stay as sent, the derived file is in the manifest (derived) and the zip.</summary>
    [Fact]
    public async Task OwnRowIsPickedByUserId()
    {
        using var t = new TempDir("backup-test-history-");
        var mine = new Dictionary<int, string[]>
        {
            [1] = [Entrant(7, "hh", 1, "30.25"), Entrant(8, "x", 2, "20")],
            [2] = [Entrant(55, "hh", 1, "99"), Entrant(8, "x", 2, "50"), Entrant(7, "other", 9, "12.50")], // the first row has our handle, the 9th our id
            [3] = [Entrant(55, "hh", 1, "99"), Entrant(8, "x", 2, "50")],
        };
        var f = World(_ => PreviousPage(3, [1, 2, 3]), id => EditionReply(id, mine[id]));
        var e = await Run(f, t, zip: true);
        var text = File.ReadAllText(Path.Combine(t.Path, "backup-7", "competition-history.json"));
        var h = JsonNode.Parse(text)!;
        var ed = h["editions"]!.AsArray();
        Assert.True(ed.Select(x => x!["id"]!.GetValue<int>()).SequenceEqual([3, 2, 1]), "newest id first");
        Assert.True(ed[0]!["not_listed"]!.GetValue<bool>() && ed[0]!["position"] == null, "edition 3: not listed, no rank guessed");
        Assert.True(!ed[1]!["not_listed"]!.GetValue<bool>() && ed[1]!["position"]!.GetValue<int>() == 9 && ed[1]!["type_id"]!.GetValue<int>() == 77 && ed[1]!["type_name"]!.ToString() == "Test Type" && ed[1]!["start"]!.GetValue<int>() == 1000 && ed[1]!["end"]!.GetValue<int>() == 2000, "edition 2: position 9 by id, type and dates");
        Assert.True(ed[2]!["position"]!.GetValue<int>() == 1 && text.Contains("\"points\": 30.25") && text.Contains("\"points\": 12.50"), "edition 1: position 1; points as sent (30.25, and 12.50 keeps its zero)");
        Assert.True(h["totals"]!["listed"]!.GetValue<int>() == 2 && h["totals"]!["not_listed"]!.GetValue<int>() == 1, "totals");
        var raw = File.ReadAllText(Directory.GetFiles(Path.Combine(t.Path, "backup-7"), "Page_content_competitions_new*id=2*").Single());
        Assert.True(raw == EditionReply(2, mine[2]), "the raw edition file is the reply, untouched");
        var m = JsonNode.Parse(File.ReadAllText(Path.Combine(t.Path, "backup-7", "manifest.json")))!["files"]!.AsArray().Single(x => x!["path"]!.ToString() == "competition-history.json")!;
        Assert.True(m["derived"]!.GetValue<bool>() && m["sha256"] != null, "listed in the manifest as derived");
        var zip = Directory.GetFiles(t.Path, "*.zip").Single();
        using var z = ZipFile.OpenRead(zip);
        Assert.True(z.Entries.Any(x => x.Name == "competition-history.json") && e.Failed == 0, "in the zip, nothing failed");
    }

    /// <summary>Only the best 100 are returned: an edition with 100 rows and 130 entrants where we are not among them is not_listed; every edition request carries entrants_limit=100.</summary>
    [Fact]
    public async Task CappedEntrantListGivesNotListed()
    {
        using var t = new TempDir("backup-test-history-");
        var hundred = Enumerable.Range(100, 100).Select(u => Entrant(u, "p" + u, u - 99, "1.5")).ToArray();
        var f = World(_ => PreviousPage(1, [1]), id => EditionReply(id, hundred, total: 130));
        var e = await Run(f, t);
        var ed = History(t)["editions"]!.AsArray();
        Assert.True(ed.Count == 1 && ed[0]!["not_listed"]!.GetValue<bool>() && e.Failed == 0, "not_listed, not an error");
        Assert.True(f.Forms.Where(x => x.Contains("entrants_limit=")).All(x => x.Contains("entrants_limit=100")) && Requests(f, Edition) == 1, "entrants_limit=100 sent");
    }

    /// <summary>The earlier editions in each reply are not followed by default; with the switch each is asked once, however many replies name it.</summary>
    [Fact]
    public async Task OlderEditionsAreFollowedOnlyWithTheSwitchAndOnce()
    {
        var older = new Dictionary<int, int[]> { [1] = [901, 902], [901] = [902, 903], [902] = [], [903] = [] };
        Fake Make() => World(_ => PreviousPage(1, [1]), id => EditionReply(id, [Entrant(7, "hh", 3, "1")], older: older[id]));

        using var t = new TempDir("backup-test-history-");
        var off = Make();
        await Run(off, t);
        Assert.True(EditionIds(off).SequenceEqual([1]) && History(t)["totals"]!["editions"]!.GetValue<int>() == 1, "switch off: only the listed edition");

        using var t2 = new TempDir("backup-test-history-");
        var on = Make();
        await Run(on, t2, older: true);
        Assert.True(EditionIds(on).Order().SequenceEqual([1, 901, 902, 903]), $"switch on: each edition once ({string.Join(",", EditionIds(on))})");
        var h = History(t2);
        Assert.True(h["editions"]!.AsArray().Select(x => x!["id"]!.GetValue<int>()).SequenceEqual([903, 902, 901, 1]) && h["older_editions"]!.GetValue<bool>(), "all four in the file, no duplicate");
    }

    /// <summary>The community league list is a plain array; its editions are fetched and written like the others.</summary>
    [Fact]
    public async Task CommunityLeagueEditionsAreHandledTheSame()
    {
        using var t = new TempDir("backup-test-history-");
        var f = World(_ => PreviousPage(0, []), id => EditionReply(id, [Entrant(7, "hh", 4, "8")]), league: "[" + EditionRow(500) + "]");
        await Run(f, t);
        var ed = History(t)["editions"]!.AsArray();
        Assert.True(EditionIds(f).SequenceEqual([500]) && ed.Count == 1 && ed[0]!["id"]!.GetValue<int>() == 500 && ed[0]!["position"]!.GetValue<int>() == 4, "edition 500 asked for and in the file");
        Assert.True(Directory.GetFiles(Path.Combine(t.Path, "backup-7"), "Page_content_list_community_league_competitions*").Length == 1, "the list is saved");
    }

    /// <summary>A failed edition does not stop the others; the next run asks for that one only.</summary>
    [Fact]
    public async Task ResumeAsksOnlyForTheFailedEdition()
    {
        using var t = new TempDir("backup-test-history-");
        var f = World(_ => PreviousPage(3, [1, 2, 3]), id => EditionReply(id, [Entrant(7, "hh", id, "1")]), broken: id => id == 2 ? new HttpResponseMessage(HttpStatusCode.BadGateway) : null);
        var e = await Run(f, t);
        Assert.True(e.Failed == 1 && History(t)["editions"]!.AsArray().Select(x => x!["id"]!.GetValue<int>()).SequenceEqual([3, 1]), "2 failed, 1 and 3 are saved and in the file");

        var f2 = World(_ => PreviousPage(3, [1, 2, 3]), id => EditionReply(id, [Entrant(7, "hh", id, "1")]));
        var e2 = await Run(f2, t);
        Assert.True(Directory.GetDirectories(t.Path).Length == 1 && e2.Failed == 0, "the unfinished run is continued in its folder");
        Assert.True(EditionIds(f2).SequenceEqual([2]) && Offsets(f2).Length == 0, $"only edition 2 (and no list page) asked again ({string.Join(",", EditionIds(f2))})");
        Assert.True(History(t)["editions"]!.AsArray().Select(x => x!["id"]!.GetValue<int>()).SequenceEqual([3, 2, 1]), "the file has all three now");
    }

    /// <summary>Public reads: a public profile run has the history too.</summary>
    [Fact]
    public async Task PublicProfileRunHasTheHistory()
    {
        using var t = new TempDir("backup-test-history-");
        var f = World(_ => PreviousPage(1, [1]), id => EditionReply(id, [Entrant(7, "hh", 2, "5")]));
        await Hosts.Make(f, Path.Combine(t.Path, "claim.key")).Run(t.Path, "hh", null, new HashSet<string> { "history" }, true, false, TestContext.Current.CancellationToken);
        Assert.True(History(t)["editions"]![0]!["position"]!.GetValue<int>() == 2, "found by the id the handle resolves to");
    }
}
