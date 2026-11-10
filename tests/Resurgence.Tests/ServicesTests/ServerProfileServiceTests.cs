using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Services;
using Resurgence.Services.Mocks;

namespace Resurgence.Tests.ServicesTests;

public class ServerProfileServiceTests
{
    sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    /// <summary>Signed in as "fox": the server spells the handle "Fox".</summary>
    sealed class Fox : ISessionService
    {
        public event Action? Changed { add { } remove { } }
        public Task<Session?> CurrentAsync() => Task.FromResult<Session?>(new Session("fox", 0));
        public string? SignInUrl(string next) => null;
        public Task<Result> RequestCodeAsync(string email) => throw new NotSupportedException();
        public Task<Result> SignInAsync(string email, string code, bool register = false) => throw new NotSupportedException();
        public Task<Result> SetHandleAsync(string handle) => throw new NotSupportedException();
        public Task SignOutAsync() => throw new NotSupportedException();
        public Task<StartOutcome> StartAsync() => throw new NotSupportedException();
        public Task<bool> LicenseAcceptedAsync() => throw new NotSupportedException();
        public Task AcceptLicenseAsync() => throw new NotSupportedException();
    }

    static ServerProfileService Make(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://server.test:8080") }, new ServerCookie { Value = "abc", Host = "site.test" }, new Fox(),
            new MockProfileService(), NullLogger<ServerProfileService>.Instance);

    static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task ARealPlayersHeaderIsTheServers()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """{"handle": "Fox", "hunterscore": 1234, "rank": 2, "membership": "Hunter", "avatar": "https://web.test/media/avatar/256/1000000.png"}"""));
        var p = await Make(fake).ProfileAsync("fox");
        Assert.Equal(("Fox", 1234, 2, true, "Hunter", "avatar-1000000"), (p!.Handle, p.HunterScore, p.Rank, p.Own, p.Membership, p.Avatar));
        Assert.Same(ServerProfileService.Tabs, p.Tabs);
        Assert.Equal("/hunters/fox", Assert.Single(fake.Seen).RequestUri!.AbsolutePath);
    }

    // the live site never shows the mock: a handle the server does not know is no profile, not a fictional hunter
    [Theory]
    [InlineData("Hunter05")]
    [InlineData("Hunter01")]
    public async Task AnUnknownHandleIsNoProfile(string handle) =>
        Assert.Null(await Make(new Fake(_ => Json(HttpStatusCode.NotFound, """{"error": "not found"}"""))).ProfileAsync(handle));

    [Theory]
    [InlineData("https://web.test/media/avatar/256/1000005.png", "avatar-1000005")]
    [InlineData("https://avatars.test/uploads/a.jpg", "avatar-default")]
    [InlineData(null, "avatar-default")]
    public void TheAvatarSlotIsNamedByThePicturesId(string? url, string slot) => Assert.Equal(slot, ServerProfileService.AvatarSlot(url));

    // PL20: a visitor-typed handle goes into the request path
    [Fact]
    public async Task TheHandleIsEscapedIntoOnePathSegment()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.NotFound, "{}"));
        await Make(fake).ProfileAsync("a/b ?c");
        Assert.EndsWith("/hunters/a%2Fb%20%3Fc", Assert.Single(fake.Seen).RequestUri!.OriginalString);
    }

    // PL21: fix b22a8c9
    [Fact]
    public async Task AnotherPlayersProfileIsNotOwnAndNotOnline()
    {
        var p = await Make(new Fake(_ => Json(HttpStatusCode.OK, """{"handle":"Owl","hunterscore":5,"rank":3}"""))).ProfileAsync("Owl");
        Assert.Equal(("Owl", 5, 3, false, false), (p!.Handle, p.HunterScore, p.Rank, p.Own, p.Online));
    }

    [Fact]
    public async Task TheStatisticsAreTheServersTotals()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """
            {"weapons":[{"weapon":"Rifle","ammo":"Bullet","shots":4,"accuracy":75.0,"kills":1,"killsPerHit":33.3,"longest":null,"weaponId":5,"ammoId":10}],
             "animals":[{"name":"Deer","spotted":3,"tracked":2,"harvested":1}],"collectables":[],"distanceKm":5.0,"huntSeconds":7200}
            """));
        var t = await Make(fake).TotalsAsync("a b");
        Assert.EndsWith("/hunters/a%20b/statistics", Assert.Single(fake.Seen).RequestUri!.OriginalString);
        Assert.Equal(new WeaponStat("Rifle", "Bullet", 4, 75.0, 1, 33.3, null, 5, 10), Assert.Single(t.Weapons));
        Assert.Equal(new SpeciesStat("Deer", 3, 2, 1), Assert.Single(t.Animals));
        Assert.Equal((0, 5.0, 7200), (t.Collectables.Count, t.DistanceKm, t.HuntSeconds));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{}")]
    [InlineData(HttpStatusCode.OK, "<html>")]
    [InlineData(HttpStatusCode.OK, """{"handle": "Owl", "hunterscore": 5}""")]  // an answer without the lists
    public async Task AFailedStatisticsAnswerIsAnEmptyTotals(HttpStatusCode code, string body)
    {
        var t = await Make(new Fake(_ => Json(code, body))).TotalsAsync("Owl");
        Assert.Equal((0, 0, 0, 0.0, 0), (t.Weapons.Count, t.Animals.Count, t.Collectables.Count, t.DistanceKm, t.HuntSeconds));
    }

    [Fact]
    public async Task TheHistoryIsThePageOfTheServersHunts()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """{"page":2,"pages":3,"total":90,"rows":[{"id":12,"start":1700000100,"seconds":3600,"reserve":"Reserve","lodge":"#101","collectables":2,"kills":1}]}"""));
        var page = await Make(fake).HistoryAsync("a b", 2);
        Assert.EndsWith("/hunters/a%20b/hunts?page=2", Assert.Single(fake.Seen).RequestUri!.PathAndQuery);
        Assert.Equal((2, 3, 90), (page.Number, page.Pages, page.Total));
        Assert.Equal(new HistoryRow(12, new DateTime(2023, 11, 14, 22, 15, 0, DateTimeKind.Utc), 3600, "Reserve", "#101", 2, 1), Assert.Single(page.Rows));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{}")]
    [InlineData(HttpStatusCode.OK, "<html>")]
    public async Task AFailedHistoryAnswerIsAnEmptyPage(HttpStatusCode code, string body)
    {
        var page = await Make(new Fake(_ => Json(code, body))).HistoryAsync("fox", 1);
        Assert.Equal((0, 1, 1), (page.Rows.Count, page.Number, page.Pages));
    }

    const string Hunt = """
        {"id":12,"reserve":"Reserve","lodge":"#101","start":1700000100,"end":null,"weapons":[{"weapon":"Rifle","ammo":"Bullet","shots":1,"accuracy":100.0,"kills":1,"killsPerHit":100.0,"longest":82.0}],
         "animals":[{"name":"Deer","spotted":0,"tracked":0,"harvested":1}],"collectables":[],"achievements":[],"distanceKm":4.0,"seconds":3600,"antlers":[],
         "kills":[{"id":5,"species":"Deer","weapon":"Rifle","ammo":"Bullet","distanceM":82.0,"score":120.5,"weightKg":64.35,"when":1700000500,"sheet":false}]}
        """;

    [Fact]
    public async Task AHuntIsTheServersHuntWithItsStatisticsAndKills()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, Hunt));
        var service = Make(fake);
        var hunt = await service.ExpeditionAsync("fox", 12);
        Assert.Equal("/hunters/fox/hunts/12", fake.Seen[0].RequestUri!.AbsolutePath);
        Assert.Equal((12L, "Reserve", "#101", null, 4.0, 3600), (hunt!.Id, hunt.Reserve, hunt.Lodge, hunt.End, hunt.DistanceKm, hunt.Seconds));
        Assert.Equal(new WeaponStat("Rifle", "Bullet", 1, 100.0, 1, 100.0, 82.0), Assert.Single(hunt.Weapons));
        Assert.Equal(new SpeciesStat("Deer", 0, 0, 1), Assert.Single(hunt.Animals));
        Assert.Equal(new Kill(5, "Deer", "Rifle", "Bullet", 82.0, 120.5, 64.35, new DateTime(2023, 11, 14, 22, 21, 40, DateTimeKind.Utc), null, false), Assert.Single(hunt.Kills));  // no sheet to open
        await service.ExpeditionAsync("fox", null);
        Assert.Equal("/hunters/fox/hunts/latest", fake.Seen[1].RequestUri!.AbsolutePath);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{}")]
    [InlineData(HttpStatusCode.OK, "<html>")]
    public async Task AnUnknownOrFailedHuntIsNoHunt(HttpStatusCode code, string body) => Assert.Null(await Make(new Fake(_ => Json(code, body))).ExpeditionAsync("fox", 12));

    [Fact]
    public async Task TheScoreSheetIsTheServersSheet()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """
            {"kind":"score","species":"Deer","facts":[{"label":"B. Tip-to-tip spread","value":20.0}],"rows":[{"label":"F. Main beam length","a":22.0,"b":24.0,"c":2.0}],"abnormal":[{"right":1.5,"left":0.0}],
             "sums":[{"label":"Total","value":195.0}],"weapon":"Rifle","scope":true,"ammo":"Bullet","distanceM":60.0,"state":"Idle","bodyPart":"Body","stance":"Prone","platform":"None","weightKg":130.0,
             "type":"Common (M)","woundTime":6.0,"integrity":100,"shotLocation":"Place (1, 2)","shots":1,"when":1700000500,"expeditionId":12,"score":195.0,"harvestValue":90.0}
            """));
        var sheet = await Make(fake).SheetAsync("fox", "antler", 5);  // the server decides the kind, not the address
        Assert.Equal("/hunters/fox/score/5", Assert.Single(fake.Seen).RequestUri!.AbsolutePath);
        Assert.Equal(("score", "Deer", 195.0, 12L, 100), (sheet!.Kind, sheet.Species, sheet.Score, sheet.ExpeditionId, sheet.Integrity));
        Assert.Equal(new SheetRow("F. Main beam length", 22.0, 24.0, 2.0), Assert.Single(sheet.Rows));
        Assert.Equal((1.5, 0.0), Assert.Single(sheet.Abnormal));
        Assert.Equal(new DateTime(2023, 11, 14, 22, 21, 40, DateTimeKind.Utc), sheet.When);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{}")]
    [InlineData(HttpStatusCode.OK, "<html>")]
    public async Task AKillWithoutASheetIsNoSheet(HttpStatusCode code, string body) => Assert.Null(await Make(new Fake(_ => Json(code, body))).SheetAsync("fox", "score", 5));

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "{}")]
    [InlineData(HttpStatusCode.OK, "<html>")]
    public async Task AFailedOrGarbledAnswerIsNoProfile(HttpStatusCode code, string body) => Assert.Null(await Make(new Fake(_ => Json(code, body))).ProfileAsync("Owl"));

    const string Skills = """{"species":[{"name":"Red Fox","image":"red_fox","spotting":2.5,"tracking":1.007}],"weapons":[{"id":7,"name":"Rifle","group":"Rifles","value":3},{"name":"Bow","group":null,"value":1.14}]}""";

    [Fact]
    public async Task SpeciesSkillsAreRingsOfLevelAndPercent()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, Skills));
        var skill = Assert.Single(await Make(fake).SkillsAsync("fox", "species"));
        Assert.Equal("/hunters/fox/skills", Assert.Single(fake.Seen).RequestUri!.AbsolutePath);
        Assert.Equal(("Red Fox", null, "red_fox"), (skill.Name, skill.Group, skill.Image));
        Assert.Equal([("Spotting", 2, 50), ("Tracking", 1, 0)], skill.Rings.Select(r => (r.Label, r.Level, r.Percent)));
    }

    [Fact]
    public async Task WeaponSkillsAreGroupedWithAShootingRing()
    {
        var skills = await Make(new Fake(_ => Json(HttpStatusCode.OK, Skills))).SkillsAsync("fox", "weapons");
        Assert.Equal([("Rifle", "Rifles", 3, 0, 7L), ("Bow", null, 1, 14, null)], skills.Select(s => (s.Name, s.Group, s.Rings[0].Level, s.Rings[0].Percent, s.Id)));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{}")]
    [InlineData(HttpStatusCode.OK, "<html>")]
    public async Task AFailedSkillsAnswerIsNoSkills(HttpStatusCode code, string body) => Assert.Empty(await Make(new Fake(_ => Json(code, body))).SkillsAsync("fox", "species"));

    [Fact]
    public void TheSkillsTabHasAServerSource() => Assert.Contains("skills", ServerProfileService.Tabs);

    [Fact]
    public async Task TrophiesAreTheServersPageOfTheHandle()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """{"page":2,"pages":3,"total":50,"rows":[{"name":"Red Fox","date":"2026-09-13"}]}"""));
        var page = await Make(fake).TrophiesAsync("fox", 2);
        Assert.Equal("/hunters/fox/trophies?page=2", Assert.Single(fake.Seen).RequestUri!.PathAndQuery);
        Assert.Equal((2, 3, 50), (page.Number, page.Pages, page.Total));
        var t = Assert.Single(page.Rows);
        Assert.Equal(("Red Fox", new DateOnly(2026, 9, 13), null, null), (t.Name, t.Date, t.CompetitionId, t.Competition));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{}")]
    [InlineData(HttpStatusCode.OK, "<html>")]
    public async Task AFailedTrophiesAnswerIsAnEmptyPage(HttpStatusCode code, string body)
    {
        var page = await Make(new Fake(_ => Json(code, body))).TrophiesAsync("fox", 1);
        Assert.Equal((0, 1, 1), (page.Rows.Count, page.Number, page.Pages));
    }

    [Fact]
    public void TheTrophiesTabHasAServerSource() => Assert.Contains("trophies", ServerProfileService.Tabs);

    [Fact]
    public async Task PersonalBestsAreTheServersRowsOfTheHandleForAnySeason()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """{"rows":[{"species":"Red Fox","score":50.5,"range":60.25,"weapon":"Test Rifle","ts":1700000500,"kill":9}]}"""));
        var best = await Make(fake).BestAsync("fox", 3);
        Assert.Equal("/hunters/fox/best", Assert.Single(fake.Seen).RequestUri!.AbsolutePath);
        Assert.Equal("done", best.Status);
        Assert.Equal(new BestRow("Red Fox", 50.5, 60.25, "Test Rifle", new DateTime(2023, 11, 14, 22, 21, 40, DateTimeKind.Utc), 9), Assert.Single(best.Rows));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{}")]
    [InlineData(HttpStatusCode.OK, """{"rows":[]}""")]
    [InlineData(HttpStatusCode.OK, "<html>")]
    public async Task NoPersonalBestsOrAFailedAnswerIsEmpty(HttpStatusCode code, string body)
    {
        var best = await Make(new Fake(_ => Json(code, body))).BestAsync("fox", 0);
        Assert.Equal(("empty", 0), (best.Status, best.Rows.Count));
    }

    const string Ranks = """
        {"cap":1000,"ranks":[{"id":1,"category":"animals","name":"Red Fox","value":1500},{"id":2,"category":"animals","name":"Alpine Ibex","value":25},
                             {"id":41,"category":"weapons","name":"Rifles","value":3},{"id":38,"category":"collectables","name":"Mushrooms","value":0}],
         "milestones":[{"category":"animals","title":"Total","steps":[50,250],"value":60},{"category":"animals","title":"Red Fox","steps":[1,25,100],"value":25},
                       {"category":"exploration","title":"Distance","steps":[10,42],"value":12}]}
        """;

    [Fact]
    public async Task RankBarsOfAKindAreTheRowsByNameCountedToTheCap()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, Ranks));
        var bars = await Make(fake).RanksAsync("fox", "animals");
        Assert.Equal("/hunters/fox/ranks", Assert.Single(fake.Seen).RequestUri!.AbsolutePath);
        Assert.Equal([("Alpine Ibex", 25, 25, 1000), ("Red Fox", 1000, 1000, 1000)], bars.Select(b => (b.Name, b.Level, b.Done, b.Total)));
    }

    [Fact]
    public async Task TheRanksOverviewIsTheScoreAndTheThreeCategories()
    {
        var bars = await Make(new Fake(_ => Json(HttpStatusCode.OK, Ranks))).RanksAsync("fox", "overview");
        Assert.Equal([("Hunter Score", 1028, 4000), ("Animals", 1025, 2000), ("Weapons", 3, 1000), ("Collectables", 0, 1000)], bars.Select(b => (b.Name, b.Done, b.Total)));
    }

    [Fact]
    public async Task MilestonesAreTheStepsReachedOfTheKind()
    {
        var animals = await Make(new Fake(_ => Json(HttpStatusCode.OK, Ranks))).MilestonesAsync("fox", "animals");
        Assert.Equal([("Total", 1, 60), ("Red Fox", 2, 25)], animals.Select(m => (m.Title, m.Done, m.Value)));
        Assert.Equal([1, 25, 100], animals[1].Steps);
        Assert.Equal([("Distance", 1)], (await Make(new Fake(_ => Json(HttpStatusCode.OK, Ranks))).MilestonesAsync("fox", "exploration")).Select(m => (m.Title, m.Done)));
        Assert.Empty(await Make(new Fake(_ => Json(HttpStatusCode.OK, Ranks))).MilestonesAsync("fox", "day-mission"));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{}")]
    [InlineData(HttpStatusCode.OK, "<html>")]
    [InlineData(HttpStatusCode.OK, """{"handle": "Owl"}""")]  // an answer without the lists
    public async Task AFailedRanksAnswerIsNoBarsAndNoMilestones(HttpStatusCode code, string body)
    {
        var service = Make(new Fake(_ => Json(code, body)));
        Assert.Empty(await service.RanksAsync("fox", "overview"));
        Assert.Empty(await service.MilestonesAsync("fox", "animals"));
    }

    [Fact]
    public void TheRanksTabHasAServerSource() => Assert.Contains("ranks", ServerProfileService.Tabs);

    const string Dogs = """{"dogs":[{"name":"Rex","type":"Test Hound","level":2,"percent":25,"skills":[{"title":"Loyalty One","level":1}],"trophies":["Ribbon"]}]}""";

    [Fact]
    public async Task DogsCarryTheirLevelSkillsAndTrophies()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, Dogs));
        var dog = Assert.Single(await Make(fake).DogsAsync("fox"));
        Assert.Equal("/hunters/fox/dogs", Assert.Single(fake.Seen).RequestUri!.AbsolutePath);
        Assert.Equal(("Rex", "Test Hound", 2, 25), (dog.Name, dog.Type, dog.Level, dog.Percent));
        Assert.Equal(["Ribbon"], dog.Trophies);
        Assert.Equal([("Loyalty One", 1)], dog.Skills.Select(s => (s.Title, s.Level)));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{}")]
    [InlineData(HttpStatusCode.OK, "<html>")]
    public async Task AFailedDogsAnswerIsNoDogs(HttpStatusCode code, string body) => Assert.Empty(await Make(new Fake(_ => Json(code, body))).DogsAsync("fox"));

    const string Achievements = """{"categories":[{"name":"Animals","level":1,"done":4,"total":5}],"unlocked":4,"recent":[{"title":"25 Mule Deer","description":"25 Mule Deer harvested.","date":"2023-11-15"}]}""";

    [Fact]
    public async Task TheAchievementSummaryIsTheServers()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, Achievements));
        var summary = await Make(fake).AchievementSummaryAsync("fox");
        Assert.Equal("/hunters/fox/achievements", Assert.Single(fake.Seen).RequestUri!.AbsolutePath);
        Assert.Equal(("Animals", 1, 4, 5), summary.Categories.Select(c => (c.Name, c.Level, c.Done, c.Total)).Single());
        Assert.Equal(4, summary.Unlocked);
        Assert.Equal(("25 Mule Deer", "25 Mule Deer harvested.", new DateOnly(2023, 11, 15)), summary.Recent.Select(a => (a.Title, a.Description, a.Date)).Single());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{}")]
    [InlineData(HttpStatusCode.OK, "<html>")]
    public async Task AFailedAchievementsAnswerIsAnEmptySummary(HttpStatusCode code, string body)
    {
        var summary = await Make(new Fake(_ => Json(code, body))).AchievementSummaryAsync("fox");
        Assert.Empty(summary.Categories);
        Assert.Empty(summary.Recent);
        Assert.Equal(0, summary.Unlocked);
    }

    [Fact]
    public void TheAchievementsTabHasAServerSource() => Assert.Contains("achievements", ServerProfileService.Tabs);

    [Fact]
    public void TheDogsTabHasAServerSource() => Assert.Contains("dogs", ServerProfileService.Tabs);

    [Fact]
    public async Task FriendsAreTheServersPageOfTheHandle()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """{"page":2,"pages":3,"total":45,"rows":[{"handle":"Birch","hunterscore":120}]}"""));
        var page = await Make(fake).FriendsAsync("fox", 2);
        Assert.Equal("/hunters/fox/friends", Assert.Single(fake.Seen).RequestUri!.AbsolutePath);
        Assert.Equal("page=2", fake.Seen[0].RequestUri!.Query.TrimStart('?'));
        Assert.Equal((2, 3), (page.Number, page.Pages));
        Assert.Equal(new ProfileFriend("Birch", 120), Assert.Single(page.Rows));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, """{"error": "not found"}""")]
    [InlineData(HttpStatusCode.OK, "<html>")]
    public async Task AFailedFriendsAnswerIsNoFriends(HttpStatusCode code, string body) => Assert.Empty((await Make(new Fake(_ => Json(code, body))).FriendsAsync("fox", 1)).Rows);

    [Fact]
    public void TheFriendsTabHasAServerSource() => Assert.Contains("friends", ServerProfileService.Tabs);

    sealed class Friends(string[] friends, string[] pending) : IFriendsService
    {
        public Task<IReadOnlyList<Friend>> FriendsAsync() => Task.FromResult<IReadOnlyList<Friend>>([.. friends.Select(h => new Friend(h, false))]);
        public Task<IReadOnlyList<Friend>> SearchAsync(string text) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> RequestsAsync() => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> PendingAsync() => Task.FromResult<IReadOnlyList<string>>(pending);
        public Task<Result> SendRequestAsync(string handle) => throw new NotSupportedException();
        public Task<Result> AcceptAsync(string handle) => throw new NotSupportedException();
        public Task<Result> DeclineAsync(string handle) => throw new NotSupportedException();
        public Task<Result> RemoveAsync(string handle) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("Oak", true, false)]
    [InlineData("oak", true, false)]
    [InlineData("Ash", false, true)]
    [InlineData("Elm", false, false)]
    public async Task AHeaderSaysWhetherThePlayerIsAFriendOrHasAsked(string handle, bool friend, bool pending)
    {
        var service = new ServerProfileService(new HttpClient(new Fake(_ => Json(HttpStatusCode.OK, $$"""{"handle": "{{handle}}", "hunterscore": 1, "rank": 2}"""))) { BaseAddress = new Uri("http://server.test") },
            new ServerCookie { Value = "abc" }, new Fox(), new MockProfileService(), NullLogger<ServerProfileService>.Instance, new Friends(["Oak"], ["Ash"]));
        var p = await service.ProfileAsync(handle);
        Assert.Equal((friend, pending), (p!.Friend, p.RequestPending));
    }
}
