using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>Mission texts in the player's language (docs/server.md, "Mission texts"): the original's, then ours, then English, per text; the pages, the game's catalogue, the daily and the event missions
/// show them; the rules never read them. Made-up missions and texts, and invented files: the real texts are game text and are not in the repository.</summary>
public class MissionTextTests : StateTestBase, IAsyncLifetime
{
    static readonly JsonObject Cfg = new(Replay.HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));
    static readonly DateTimeOffset Start = new(2031, 3, 10, 0, 0, 0, TimeSpan.Zero);

    // 11 harvests a deer, 12 tracks and harvests (needs 11); pack 10 holds 12; the facts add the event's mission 11, a community mission and the daily templates of the daily tests
    const string Definitions = """
        {"missions": [
          {"id": 11, "title": "Harvest a Test Deer", "desc": "<b>Easy</b> money.", "dependencies": [], "rewards": [{"text": "100 gm$"}], "objectives": [{"id": 0, "title": "Harvest a Test Deer"}]},
          {"id": 12, "title": "Track and Harvest", "desc": "Two steps.", "dependencies": [11], "rewards": [{"text": "250 gm$"}],
           "objectives": [{"id": 0, "title": "Locate Test Deer tracks."}, {"id": 1, "title": "Harvest a Test Deer."}]}],
         "mission_groups": [{"id": 10, "title": "Deer Pack", "desc": "All the deer.", "missions": [12]}]}
        """;

    static readonly Facts EventFacts = Facts.Parse("""
        {"format": 1,
         "events": {"season": {"title": "Season", "missions": [11], "items": [], "community": [{"id": 1, "title": "Spot a Test Deer", "target": 40, "gm": 7, "em": 0}]}}}
        """);

    // German, from the original: the title of 11 and the objectives of 11 and 12 (a rule that read them would find no animal in them); ours: the description of 11, the title of 12 and the pack's
    static readonly MissionTexts German = new(
        new() { ["de"] = new() { ["m11.title"] = "Erlege einen Testhirsch", ["m11.o0"] = "Erlege einen Testhirsch", ["m12.o0"] = "Finde Testhirsch-Spuren.", ["m12.o1"] = "Erlege einen Testhirsch." } },
        new()
        {
            ["de"] = new()
            {
                ["m11.title"] = "Unser Titel", ["m11.desc"] = "<b>Leicht</b> verdient.", ["m12.title"] = "Spuren und Ernte", ["g10.title"] = "Hirsch-Paket",
                ["cseason.1.title"] = "Entdecke einen Testhirsch"
            }
        });

    Loopback server = null!;

    public async ValueTask InitializeAsync()
    {
        state = New("state.db", German);
        var table = new Replay.HarTable();
        table.Entries[new("POST", "gameserver.test", Replay.CONNECT, null)] = new Replay.Hit(2, "", 200, [], "{}"u8.ToArray());
        var catalogue = """{"season": 1, "items": [], "missions": [{"id": 11, "title": "Harvest a Test Deer", "desc": "Easy.", "objectives": [{"id": 0, "title": "Harvest a Test Deer"}]}], "mission_groups": [{"id": 10, "title": "Deer Pack", "desc": "All.", "missions": [11]}]}"""u8.ToArray();
        foreach (var method in new[] { "GET", "POST" })
            foreach (var path in new[] { Replay.APPLICATION, Replay.CLIENT_APPLICATION })
                table.Entries[new(method, "api.test", path, null)] = new Replay.Hit(2, "", 200, [("Content-Type", "application/json")], catalogue);
        server = await Loopback.StartAsync(new Replay.Game(table, Cfg, state, tmp), tmp);
    }

    protected override ValueTask StopServersAsync() => server.DisposeAsync();

    State New(string db, MissionTexts texts, Facts? facts = null, bool season = false)
    {
        var app = APP;
        foreach (var (key, value) in O(Definitions))
            app[key] = value!.DeepClone();
        app["locations"]![0]!["name"] = "Test Lodge";
        var s = new State(Path.Combine(tmp, db), Py.DumpBytes(CONNECT), BASE, ICON, app, LOCALE, TEXTURES, offline: false, facts: facts ?? EventFacts,
                          seasonal: season ? new("season", "SEASON", [], Start, Start.AddDays(10)) : null)
        { Texts = texts };
        s.Clock = () => Start.ToUnixTimeSeconds() + 3600;
        return s;
    }

    static JsonNode[] Kill(int individual)
    {
        var events = AccountIsolationTests.Harvest(individual, 72270);
        events[0]["species"] = 1;
        events[0]["gender"] = 0;
        events[^1]["extended_species"] = 1;
        return events;
    }

    static JsonNode Mission(JsonNode page, int id) => page["missions"]!.AsArray().Single(m => (long)m!["id"]! == id)!;

    [Fact]
    public void TestATextIsTheOriginalsThenOursThenEnglish()
    {
        // a title, a description and an objective each take the first text they have: the original's (even when ours exists), ours, English
        Assert.Equal("Erlege einen Testhirsch", German.Text("de", "m11.title", "Harvest a Test Deer"));
        Assert.Equal("<b>Leicht</b> verdient.", German.Text("de", "m11.desc", "<b>Easy</b> money."));
        Assert.Equal("Finde Testhirsch-Spuren.", German.Text("de", "m12.o0", "Locate Test Deer tracks."));
        Assert.Equal("Spuren und Ernte", German.Text("de", "m12.title", "Track and Harvest"));
        Assert.Equal("Two steps.", German.Text("de", "m12.desc", "Two steps."));  // neither has it
        Assert.Equal("Harvest a Test Deer", German.Text("fr", "m11.title", "Harvest a Test Deer"));  // a language without texts
        Assert.Equal("Harvest a Test Deer", German.Text(null, "m11.title", "Harvest a Test Deer"));
        Assert.Equal("Harvest a Test Deer", German.Text("en_US", "m11.title", "Harvest a Test Deer"));
        var both = new MissionTexts(new() { ["de"] = new() { ["a"] = "original" } }, new() { ["de"] = new() { ["a"] = "ours", ["b"] = "" } });
        Assert.Equal("original", both.Text("de", "a", "english"));
        Assert.Equal("english", both.Text("de", "b", "english"));  // an empty text is no text
    }

    [Theory]
    [InlineData("de", "de")]
    [InlineData("de_DE", "de")]
    [InlineData("DE-at", "de")]
    [InlineData("pt_BR", "pt-BR")]
    [InlineData("pt-pt", "pt-PT")]
    [InlineData("nb_NO", "nb")]
    [InlineData("no", "nb")]
    [InlineData("pt", null)]
    [InlineData("en_US", null)]
    [InlineData("en", null)]
    [InlineData("klingon", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TestTheLanguageIsTheLaunchersCodeOrTheGamesLocale(string? asked, string? code) => Assert.Equal(code, MissionTexts.Code(asked));

    [Fact]
    public void TestTheMissionsPageShowsTheTextsOfTheRequestsLanguageAndEnglishWhereItHasNone()
    {
        var english = state.WebMissions(ME)!;
        var german = state.WebMissions(ME, "de")!;
        var french = state.WebMissions(ME, "fr")!;
        Eq(english, french);  // no French texts: English whole
        Assert.Equal(("Erlege einen Testhirsch", "Leicht verdient.", "Erlege einen Testhirsch"), (Py.Str(Mission(german, 11)["title"]), Py.Str(Mission(german, 11)["desc"]), Py.Str(Mission(german, 11)["objectives"]![0])));
        Assert.Equal(("Spuren und Ernte", "Two steps.", "Finde Testhirsch-Spuren."), (Py.Str(Mission(german, 12)["title"]), Py.Str(Mission(german, 12)["desc"]), Py.Str(Mission(german, 12)["objectives"]![0])));
        Assert.Equal(("Harvest a Test Deer", "Easy money."), (Py.Str(Mission(english, 11)["title"]), Py.Str(Mission(english, 11)["desc"])));
        Assert.Equal("Erlege einen Testhirsch", Py.Str(Mission(german, 12)["needs"]![0]));  // the title of the mission it waits for
        var pack = german["groups"]![0]!;
        Assert.Equal(("Hirsch-Paket", "All the deer."), (Py.Str(pack["title"]), Py.Str(pack["desc"])));
        // only the words differ: ids, states, progress and the rest are the English page's
        foreach (var page in new[] { english, german })
            Assert.Equal("11,12", string.Join(",", page["missions"]!.AsArray().Select(m => (long)m!["id"]!)));
        Eq(Mission(english, 12)["objectives_done"], Mission(german, 12)["objectives_done"]);
        Eq(Mission(english, 12)["state"], Mission(german, 12)["state"]);
    }

    [Fact]
    public void TestTheEventPanelShowsTheEventMissionsAndTheCommunityMissionsInTheLanguage()
    {
        state = New("event.db", German, season: true);
        var german = state.EventPanel(ME, "de_DE")!;
        var english = state.EventPanel(ME)!;
        Assert.Equal(("Erlege einen Testhirsch", "Erlege einen Testhirsch", "Entdecke einen Testhirsch"),
            (Py.Str(german["missions"]![0]!["title"]), Py.Str(german["missions"]![0]!["objectives"]![0]!["title"]), Py.Str(german["community"]![0]!["title"])));
        Assert.Equal(("Harvest a Test Deer", "Harvest a Test Deer", "Spot a Test Deer"),
            (Py.Str(english["missions"]![0]!["title"]), Py.Str(english["missions"]![0]!["objectives"]![0]!["title"]), Py.Str(english["community"]![0]!["title"])));
    }

    [Fact]
    public void TestARuleReadsTheEnglishObjectiveWhateverTheLanguage()
    {
        // two states of one catalogue, one that has German texts and one that has none, see the same hunt: the German objective "Erlege einen Testhirsch" is no harvest rule, the English one is
        var plain = New("plain.db", new MissionTexts([], []));
        foreach (var s in new[] { state, plain })
        {
            Assert.True(s.MissionCall(ME, "activate", Encoding.ASCII.GetBytes("mission_id=11")));
            s.HeartbeatReply(ME, Events(Kill(71)));
            s.WebMissions(ME, "de");  // reading the German page changes nothing
        }
        Eq(Reply(plain.MissionsReply(ME)!), Reply(state.MissionsReply(ME)!));
        Assert.Equal("11", string.Join(",", Reply(state.MissionsReply(ME)!)["completed"]!.AsArray().Select(m => (long)m!)));
        Assert.Equal(plain.Wallet(ME)["gm"]!.GetValue<double>(), state.Wallet(ME)["gm"]!.GetValue<double>());
        Assert.Equal("completed", Py.Str(Mission(state.WebMissions(ME, "de")!, 11)["state"]));
        Eq(Mission(plain.WebMissions(ME)!, 12)["state"], Mission(state.WebMissions(ME, "de")!, 12)["state"]);
    }

    [Fact]
    public void TestTheDailyMissionFollowsTheLanguageAndItsConditionsStayThoseOfTheEnglishTitle()
    {
        var templates = DailyMissionTests.DailyFacts;
        var texts = new Dictionary<string, string>();
        foreach (var t in templates.DailyTemplates)
        {
            texts[$"d{t.Id}.title"] = "DE " + t.Title;
            foreach (var (o, i) in t.Objectives.Select((o, i) => (o, i + 1)))
                texts[$"d{t.Id}.o{i}"] = "DE " + o;
        }
        var daily = New("daily.db", new MissionTexts(new() { ["de"] = texts }, []), templates);
        daily.Clock = () => (new DateOnly(2031, 3, 10).DayNumber - DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber) * 86400.0 + 43200;
        JsonNode Today(string? lang) => Reply(daily.DailyCalendarReply(ME, lang)!)["calendar"]!.AsArray().Single(e => (bool)e!["today"]!)!;
        var english = Today(null);
        var german = Today("de_DE");
        var title = Py.Str(english["mission"]!["title"]);
        Assert.Equal(("DE " + title, "DE " + title + "."), (Py.Str(german["mission"]!["title"]), Py.Str(german["mission"]!["description"])));
        var englishObjectives = JsonNode.Parse((string)english["objectives"]!)!.AsArray();
        var germanObjectives = JsonNode.Parse((string)german["objectives"]!)!.AsArray();
        Assert.Equal(englishObjectives.Count, germanObjectives.Count);
        for (var i = 0; i < englishObjectives.Count; i++)
        {
            Assert.Equal("DE " + Py.Str(englishObjectives[i]!["objective_data"]!["title"]), Py.Str(germanObjectives[i]!["objective_data"]!["title"]));
            Assert.Equal(Py.Str(englishObjectives[i]!["objective_data"]!["conditions"]), Py.Str(germanObjectives[i]!["objective_data"]!["conditions"]));  // the rule is the English title's
        }
        Assert.Equal("DE " + title, Py.Str(daily.WebDaily(ME, "de")!["missions"]!.AsArray().Single(m => Py.Str(m!["title"]).Length > 0)!["title"]));
    }

    [Fact]
    public async Task TestTheGamesCatalogueIsInTheLanguageItAsksForOnEveryRoute()
    {
        async Task<JsonNode> Ask(string method, string path, string lang)
        {
            var reply = method == "GET"
                ? await server.SendAsync("GET", "api.test", path + "?lang=" + lang)
                : await server.SendAsync("POST", "api.test", path, [("Content-Type", "application/x-www-form-urlencoded")], Encoding.ASCII.GetBytes("oauth_consumer_key=k&lang=" + lang));
            Assert.Equal(200, reply.Status);
            return JsonNode.Parse(reply.Body)!;
        }
        foreach (var method in new[] { "POST", "GET" })
            foreach (var path in new[] { Replay.APPLICATION, Replay.CLIENT_APPLICATION })
            {
                var german = await Ask(method, path, "de_DE");
                var english = await Ask(method, path, "en_US");
                Assert.True(("Erlege einen Testhirsch", "<b>Leicht</b> verdient.", "Erlege einen Testhirsch", "Hirsch-Paket") ==
                            (Py.Str(german["missions"]![0]!["title"]), Py.Str(german["missions"]![0]!["desc"]), Py.Str(german["missions"]![0]!["objectives"]![0]!["title"]), Py.Str(german["mission_groups"]![0]!["title"])),
                            $"{method} {path}: {german}");
                Assert.Equal(("Harvest a Test Deer", "Easy.", "All."), (Py.Str(english["missions"]![0]!["title"]), Py.Str(english["missions"]![0]!["desc"]), Py.Str(english["mission_groups"]![0]!["desc"])));
                Assert.Equal(11, (long)german["missions"]![0]!["id"]!);  // the ids stay
            }
        var recorded = "{}"u8.ToArray();
        Assert.Same(recorded, state.Texts.Localize(recorded, "fr"));  // a language without texts: the reply itself
        var once = state.Texts.Localize(Encoding.ASCII.GetBytes("""{"missions": []}"""), "de");
        Assert.Equal("""{"missions": []}""", Encoding.ASCII.GetString(once));
    }

    // ---- the files (game text: never in the repository; the tests make invented ones)

    const string FixtureEnglish = """
        {"missions": [
          {"id": 1, "title": "Harvest a Test Deer at Nowhere Pond", "desc": "Be quick.", "objectives": [{"id": 0, "title": "Harvest a Test Deer at Nowhere Pond."}]},
          {"id": 2, "title": "Second", "desc": "<b>Again</b>.", "objectives": [{"id": 0, "title": "Spot a Test Deer."}]}],
         "mission_groups": [], "species": [{"id": 1, "name": "Test Deer"}], "items": [], "reserves": [],
         "locations": [{"id": 1, "name": "Nowhere Pond", "type": "LODGE"}]}
        """;

    const string FixtureGerman = """
        {"missions": [
          {"id": 1, "title": "Erlege einen Testhirsch bei Nirgendwo-Teich", "desc": "Sei schnell.", "objectives": [{"id": 0, "title": "Erlege einen Testhirsch bei Nirgendwo-Teich."}]},
          {"id": 2, "title": "Second", "desc": "<b>Nochmal</b>.", "objectives": [{"id": 0, "title": "Entdecke einen Testhirsch."}]}],
         "mission_groups": [], "species": [{"id": 1, "name": "Testhirsch"}], "items": [], "reserves": [],
         "locations": [{"id": 1, "name": "Nirgendwo-Teich", "type": "LODGE"}]}
        """;

    /// <summary>The folder the tool builds from invented replies: English and German.</summary>
    string Built(out string apps)
    {
        var dir = Path.Combine(tmp, "texts");
        apps = Path.Combine(tmp, "apps");
        Directory.CreateDirectory(apps);
        File.WriteAllText(Path.Combine(tmp, "en.json"), FixtureEnglish);
        File.WriteAllText(Path.Combine(apps, "app-de_DE.json"), FixtureGerman);
        File.WriteAllText(Path.Combine(apps, "app-en_US.json"), FixtureEnglish);  // English is skipped
        var output = new StringWriter();
        Assert.Equal(0, MissionTextsTool.Run([dir, "build", Path.Combine(tmp, "en.json"), apps], output, output));
        return dir;
    }

    [Fact]
    public void TestTheToolBuildsTheFilesInTheFolderItIsGivenFromTheRepliesAndTheNameCheckPassesOnThem()
    {
        var dir = Built(out _);
        var german = MissionTexts.ReadFile(Path.Combine(dir, "de.original.json"));
        Assert.Equal(["m1.title", "m1.desc", "m1.o0", "m2.desc", "m2.o0"], german.Keys);  // only what differs from English: not the title "Second"
        Assert.Equal("Sei schnell.", german["m1.desc"]);
        Assert.Empty(MissionTexts.ReadFile(Path.Combine(dir, "de.mt.json")));  // ours starts empty
        var todo = MissionTexts.ReadFile(Path.Combine(dir, "todo", "de.json"));
        Assert.Equal("Second", todo["m2.title"]);  // what neither file has, with its English text
        Assert.DoesNotContain("m1.title", todo.Keys);
        Assert.Contains("m1.title", MissionTexts.ReadFile(Path.Combine(dir, "todo", "fr.json")).Keys);  // a language with no texts at all: everything
        Assert.False(File.Exists(Path.Combine(dir, "en_US.original.json")));
        Assert.Equal(new Dictionary<string, string> { ["Nowhere Pond"] = "Nirgendwo-Teich", ["Test Deer"] = "Testhirsch" }, MissionTexts.ReadFile(Path.Combine(dir, "names", "de.json")));
        Assert.Empty(MissionTextsTool.Check(dir));  // empty .mt.json files
        // a translation is loaded from the folder: the original wins, ours fills the rest
        File.WriteAllText(Path.Combine(dir, "de.mt.json"), """{"m1.title": "Unser Testhirsch bei Nirgendwo-Teich", "m2.title": "Zweite"}""");
        Assert.Empty(MissionTextsTool.Check(dir));
        var texts = MissionTexts.Load(dir);
        Assert.Equal(("Erlege einen Testhirsch bei Nirgendwo-Teich", "Zweite"), (texts.Text("de_DE", "m1.title", "x"), texts.Text("de", "m2.title", "x")));
        var output = new StringWriter();
        Assert.Equal(0, MissionTextsTool.Run([dir, "todo"], output, output));
        Assert.DoesNotContain("m2.title", MissionTexts.ReadFile(Path.Combine(dir, "todo", "de.json")).Keys);
        Assert.Equal(1, MissionTextsTool.Run([dir], output, output));  // no command
    }

    [Fact]
    public void TestAMissingFolderOrFileOrABrokenFileMeansEnglishNotAnError()
    {
        Assert.Equal("English", MissionTexts.Load(Path.Combine(tmp, "nowhere")).Text("de", "m1.title", "English"));
        var dir = Path.Combine(tmp, "partial");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "de.original.json"), """{"m1.title": "Titel"}""");  // no de.mt.json, no other language
        File.WriteAllText(Path.Combine(dir, "fr.original.json"), "{ not json");
        File.WriteAllText(Path.Combine(dir, "es.original.json"), """{"m1.title": 3}""");
        var texts = MissionTexts.Load(dir);
        Assert.Equal(("Titel", "English", "English", "English"), (texts.Text("de", "m1.title", "English"), texts.Text("de", "m1.desc", "English"), texts.Text("fr", "m1.title", "English"), texts.Text("es", "m1.title", "English")));
        Assert.Equal("English", MissionTexts.None.Text("de", "m1.title", "English"));
    }

    [Fact]
    public void TestTheNameCheckFailsOnATranslationThatLosesAName()
    {
        var dir = Path.Combine(tmp, "texts");
        Directory.CreateDirectory(Path.Combine(dir, "names"));
        File.WriteAllText(Path.Combine(dir, MissionTextsTool.English), """{"m1.o0": "Harvest a Test Deer in Nowhere Falls", "m1.o1": "Spot two Test Deer"}""");
        File.WriteAllText(Path.Combine(dir, "names", "de.json"), """{"Test Deer": "Testhirsch", "Nowhere Falls": "Nowhere Falls"}""");
        void Ours(string json) => File.WriteAllText(Path.Combine(dir, "de.mt.json"), json);
        Ours("{}");
        Assert.Empty(MissionTextsTool.Check(dir));
        Ours("""{"m1.o0": "Erlege einen Testhirsch in Nowhere Falls", "m1.o1": "Entdecke zwei Testhirsche"}""");  // the game's word, an inflected one too, and the name as it is
        Assert.Empty(MissionTextsTool.Check(dir));
        Ours("""{"m1.o0": "Erlege einen Test Deer in nowhere-falls"}""");  // the English name unchanged, whatever its case and punctuation
        Assert.Empty(MissionTextsTool.Check(dir));
        Ours("""{"m1.o0": "Erlege einen Hirsch in Nowhere Falls"}""");
        Assert.Equal(["de m1.o0: protected name lost: Test Deer"], MissionTextsTool.Check(dir));
        Ours("""{"m1.o0": "Erlege einen Testhirsch in Nirgendwofälle", "m1.o1": "Entdecke zwei Hirsche", "m9.o0": "x", "m1.title": " "}""");
        Assert.Equal(["de m1.o0: protected name lost: Nowhere Falls", "de m1.o1: protected name lost: Test Deer", "de m9.o0: no such text", "de m1.title: no such text"], MissionTextsTool.Check(dir));
        Ours("""{"m1.o0": " "}""");
        Assert.Equal(["de m1.o0: empty"], MissionTextsTool.Check(dir));
    }
}
