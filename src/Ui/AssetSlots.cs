using Resurgence.Services;

namespace Resurgence.Ui;

/// <summary>A picture slot the website or the launcher can draw: <see cref="Key"/> is the file name without extension (<see cref="AssetImages.Key"/>), <see cref="Size"/> "WxH" of its main place ("" when the page sets it), <see cref="Page"/> where it shows.</summary>
public sealed record ExpectedSlot(string Key, string Area, string Size, string Page);

public sealed record MissingSlot(string Key, string Size, string Page);

public sealed record AreaCount(int Total, int Present);

/// <summary>The reply of /images-missing.json (docs/images.md).</summary>
public sealed record MissingReport(int Total, int Present, IReadOnlyDictionary<string, AreaCount> Areas, IReadOnlyDictionary<string, IReadOnlyList<MissingSlot>> Missing, IReadOnlyDictionary<string, string> SeenWithoutFile);

/// <summary>The services the data patterns of <see cref="AssetSlots"/> are expanded from.</summary>
public sealed record AssetSources(HostMode Mode, IStoreService Store, ILeaderboardsService Boards, IHelpService Help, ICompetitionsService Competitions, IBankService Bank, IAccountService Account, IServersService Servers);

/// <summary>Every picture slot the pages can draw, so the operator sees what is missing without visiting a page (docs/images.md#missing-pictures). The fixed names are listed here; a name built
/// from data is a <see cref="Pattern"/> with the source that expands it. A pattern without a source is listed when a page draws it (<see cref="AssetImages.Missing"/>). AssetSlotsTests fails when an
/// <c>&lt;AssetSlot Name=…&gt;</c> of src/Ui has a literal name or a name prefix this file does not hold.</summary>
public static class AssetSlots
{
    sealed record Literal(ExpectedSlot Slot, bool MockOnly);

    /// <param name="Prefix">The literal start of the name as the .razor files write it.</param>
    /// <param name="Keys">The keys of the pattern; null: no source the website can reach, the slot is listed when drawn.</param>
    sealed record Pattern(string Prefix, string Area, string Size, string Page, Func<AssetSources, Task<IEnumerable<string>>>? Keys = null);

    static Literal L(string key, string area, string size, string page, bool mockOnly = false) => new(new(key, area, size, page), mockOnly);

    static readonly Literal[] Literals =
    [
        L("logo-large", "decoration", "654x164", "BrowserNotice, SignupCard, PublicBar, Eula, SignInLayout"), L("logo-bar", "decoration", "173x44", "StatusBar"),
        L("logo-hero", "decoration", "485x120", "Feed"), L("logo-landing", "decoration", "1000x250", "AutoLogin"), L("logo-splash", "decoration", "483x65", "Splash"),
        L("bg-hero", "decoration", "980x550", "Feed"), L("browser-bg", "decoration", "1280x760", "BrowserNotice"), L("page-bg", "decoration", "", "ShellLayout"),
        L("frame-leather", "decoration", "1280x760", "SignupLayout"), L("gift-frame", "decoration", "561x288", "Signup Page2 to Page5"),
        L("gift-shotgun", "decoration", "467x99", "Signup Page2"), L("gift-licenses", "decoration", "476x193", "Signup Page3"), L("gift-membership", "decoration", "441x149", "Signup Page4"),
        L("paper-notice", "decoration", "", "Paper"), L("icon-store", "decoration", "46x46", "MenuBar"), L("recruit-hero", "decoration", "940x310", "InviteFriends"),
        L("signup-bg-1", "decoration", "1200x674", "SignupCard"), L("signup-bg-2", "decoration", "1200x674", "SignupCard"), L("signup-bg-3", "decoration", "1200x674", "SignupCard"),
        L("signup-bg-4", "decoration", "1200x674", "SignupCard"), L("signup-bg-5", "decoration", "1200x674", "SignupCard"),
        L("avatar-default", "avatars", "", "every place that draws a player"),
        L("polaroid-back", "profile", "239x288", "HunterProfile"), L("polaroid-front", "profile", "239x288", "HunterProfile"), L("profile-banner", "profile", "940x200", "HunterProfile", mockOnly: true),
        L("help-banner", "help", "940x250", "Help"), L("help-hero", "help", "940x250", "Help"), L("video", "help", "900x506", "VideoModal"),
        L("help-dm-interface", "help", "940x850", "DayMissionHelp"), L("help-dm-levels", "help", "484x200", "DayMissionHelp"), L("help-dm-cycle", "help", "910x84", "DayMissionHelp"),
        L("help-dm-achievements", "help", "800x190", "DayMissionHelp"), L("help-dm-rewards", "help", "700x200", "DayMissionHelp"),
        L("help-dm-resume-buy", "help", "700x200", "DayMissionHelp"), L("help-dm-resume-use", "help", "700x200", "DayMissionHelp"),
        .. new[] { 5, 6, 10, 11, 12, 15, 20, 21, 23, 29, 30 }.Select(d => L($"reward-{d}", "rewards", "60x60", "DayMissionHelp")),
        L("em-pack", "bank", "100x80", "BankModal", mockOnly: true),
        L("sheet-logo", "score sheet", "460x64", "SheetTab"), L("sheet-medal", "score sheet", "84x84", "SheetTab"), L("sheet-sponsor", "score sheet", "116x82", "SheetTab"),
        L("map-bg", "reserves and maps", "", "SheetTab"),
    ];

    static readonly Task<IEnumerable<string>> None = Task.FromResult<IEnumerable<string>>([]);

    static Func<AssetSources, Task<IEnumerable<string>>> Values(string prefix, params string[] values) =>
        _ => Task.FromResult<IEnumerable<string>>([.. values.Select(v => prefix + v)]);

    static Func<AssetSources, Task<IEnumerable<string>>> Days(string prefix) => Values(prefix, "1", "2", "3", "4", "5", "6", "7");

    /// <summary>The list of the mock only: the live site draws none of these (HostMode.Live).</summary>
    static Func<AssetSources, Task<IEnumerable<string>>> MockOnly(Func<AssetSources, Task<IEnumerable<string>>> keys) => s => s.Mode.Live ? None : keys(s);

    // ponytail: the store, the boards and the competitions are walked per request (the store is cached 5 minutes); cache the list if the endpoint is fetched often
    static async Task<IEnumerable<string>> ItemKeys(AssetSources s)
    {
        List<string> keys = [];
        for (int p = 1, pages = 1; p <= pages; p++)
        {
            var page = await s.Store.ItemsAsync("all", "", p);
            pages = page.Pages;
            keys.AddRange(page.Rows.Select(i => s.Mode.ItemSlot(i.Id)));
        }
        return keys;
    }

    static async Task<IEnumerable<string>> CompetitionHeroes(AssetSources s)
    {
        List<int> ids = [.. (await s.Competitions.UpcomingAsync(null)).Select(c => c.Id), .. (await s.Competitions.LeagueAsync()).Select(c => c.Id)];
        for (int p = 1, pages = 1; p <= pages; p++)
        {
            var page = await s.Competitions.PreviousAsync(p);
            pages = page.Pages;
            ids.AddRange(page.Rows.Select(c => c.Id));
        }
        return ids.Select(id => $"competition-hero-{id}");
    }

    static readonly Pattern[] Patterns =
    [
        // the store: one picture per item and bundle, scaled to each slot that shows it; HostMode says which names the site uses
        new("item-", "store", "150x110", "Store, ItemDetail, BundleDetail, Inventory", s => s.Mode.Live ? None : ItemKeys(s)),
        new("store-item-", "store", "150x110", "Store, ItemDetail, BundleDetail, Inventory, the dogs of DogsTab", s => s.Mode.Live ? ItemKeys(s) : None),
        new("bundle-", "store", "150x110", "Store, BundleDetail", async s => s.Mode.Live ? [] : (await s.Store.BundlesAsync()).Select(b => s.Mode.BundleSlot(b.Id))),
        new("store-bundle-", "store", "150x110", "Store, BundleDetail", async s => s.Mode.Live ? (await s.Store.BundlesAsync()).Select(b => s.Mode.BundleSlot(b.Id)) : []),
        new("logo-rating-", "decoration", "62x82", "Footer", Values("logo-rating-", "1", "2", "3", "4")),
        new("species-", "species", "135x135", "Leaderboards", async s => (await s.Boards.BoardsAsync("score")).Select(b => "species-" + b.Name)),
        // the reserves the server's catalogue holds (the website's mock: 01 to 12); marker kinds are the enum's
        new("reserve-", "reserves and maps", "70x70", "LaunchMap", async s => (await s.Servers.ReservesAsync()).Select(r => $"reserve-{r.Id:00}")),
        new("map-", "reserves and maps", "", "LaunchMap, JoinMapModal", async s => (await s.Servers.ReservesAsync()).Select(r => $"map-{r.Id:00}")),
        new("marker-", "reserves and maps", "24x24", "LaunchMap, JoinMapModal", _ => Task.FromResult<IEnumerable<string>>(
            [.. Enum.GetNames<MarkerKind>().Select(k => "marker-" + k.ToLowerInvariant()).SelectMany(k => new[] { k, k + "-active" })])),  // -active is optional (docs/images.md)
        new("avatar-", "avatars", "60x60", "Settings, everywhere a player is drawn", async s => (await s.Account.AvatarsAsync()).Where(a => a.Id.StartsWith("avatar-", StringComparison.Ordinal)).Select(a => a.Id)),
        new("video-", "help", "185x138", "Help, VideoModal", async s => (await s.Help.TutorialsAsync()).SelectMany(t => t.Videos).Select(v => "video-" + v.Id)),
        new("competition-hero-", "news", "805x380", "Feed", _ => Task.FromResult<IEnumerable<string>>([.. NewsPosts.All.Select(p => p.Picture)])),  // before the competitions: the first listing of a key wins
        new("competition-hero-", "competitions", "980x340", "CompetitionView", CompetitionHeroes),
        new("competition-", "competitions", "45x45", "CompetitionTable", Values("competition-", "board", "weight")),
        new("em-pack-", "bank", "100x80", "BankModal", MockOnly(async s => (await s.Bank.CatalogAsync()).EmPacks.Select(p => "em-pack-" + p.Ems))),
        new("membership-", "bank", "100x70", "BankModal", MockOnly(Values("membership-", "starter", "special", "basic"))),
        new("paw-badge-", "missions", "100x111", "DailyMissions", Values("paw-badge-", "easy", "medium", "hard")),
        new("mission-giver-", "missions", "150x150", "DailyMissions", MockOnly(Days("mission-giver-"))),
        new("mission-picture-", "missions", "250x360", "DailyMissions", MockOnly(Days("mission-picture-"))),
        new("rank-", "profile", "114x114", "RanksTab", Values("rank-", "score", "animals", "weapons", "collectables")),   // rank-<id> of the live site: drawn
        new("ach-", "profile", "100x100", "AchievementsTab", Values("ach-", "animals", "weapons", "exploration", "day-mission")),
        // from a player's profile (the server's tables, not the website's): listed when a page draws them
        new("ach-icon-", "profile", "150x150", "AchievementsTab"),
        new("ach-badge-", "profile", "90x90", "AchievementsTab"),
        new("skill-", "profile", "80x90", "SkillsTab"),
        new("statistics-", "profile", "80x90", "SkillsTab"),
        new("trophy-", "profile", "147x147", "TrophiesTab"),
        new("weapon-", "profile", "150x30", "WeaponTable"),
        new("ammo-", "profile", "36x36", "WeaponTable"),
        new("collectable-", "profile", "24x24", "CollectableTable"),
        new("photo-", "profile", "210x118", "GalleryTab, VideoModal"),
        new("profile-bg-", "profile", "940x200", "HunterProfile"),
        new("dog-skill-", "dogs", "40x40", "DogsTab"),
        new("dog-trophy-ribbon-", "dogs", "64x64", "DogsTab"),
        new("antler-diagram-", "score sheet", "380x220", "SheetTab"),
        new("antler-guide-", "score sheet", "560x460", "SheetTab"),
    ];

    /// <summary>Whether <paramref name="name"/>, a literal of a .razor slot name, is a fixed key or the start of a pattern (a trailing '-' does not matter: <c>$"x-{y}"</c> and <c>"x" + "-" + y</c>).</summary>
    public static bool Knows(string name) =>
        Literals.Any(l => l.Slot.Key == name) || Patterns.Any(p => p.Prefix.TrimEnd('-') == name.TrimEnd('-'));

    /// <summary>Every slot the sources give, by normalised key (<see cref="AssetImages.Key"/>), first listing wins.</summary>
    public static async Task<IReadOnlyList<ExpectedSlot>> ExpectedAsync(AssetSources s)
    {
        Dictionary<string, ExpectedSlot> slots = [];
        void Add(ExpectedSlot e) { var key = AssetImages.Key(e.Key); slots.TryAdd(key, e with { Key = key }); }
        foreach (var l in Literals.Where(l => !(l.MockOnly && s.Mode.Live))) Add(l.Slot);
        foreach (var p in Patterns.Where(p => p.Keys is not null))
            foreach (var key in await p.Keys!(s)) Add(new(key, p.Area, p.Size, p.Page));
        return [.. slots.Values];
    }

    /// <summary>The slots of <paramref name="expected"/> without a file in <paramref name="images"/>, by area, and what pages drew without a file that the manifest does not know.</summary>
    public static MissingReport Report(IReadOnlyList<ExpectedSlot> expected, AssetImages images)
    {
        var missing = expected.Where(e => images.Url(e.Key) is null).ToList();
        var known = expected.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        return new(expected.Count, expected.Count - missing.Count,
            expected.GroupBy(e => e.Area).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => new AreaCount(g.Count(), g.Count(e => images.Url(e.Key) is not null))),
            missing.GroupBy(e => e.Area).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key,
                g => (IReadOnlyList<MissingSlot>)[.. g.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => new MissingSlot(e.Key, e.Size, e.Page))]),
            images.Missing.Where(m => !known.Contains(m.Key)).ToDictionary(m => m.Key, m => m.Value));
    }
}
