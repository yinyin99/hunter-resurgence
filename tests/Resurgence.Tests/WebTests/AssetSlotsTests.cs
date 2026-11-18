using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;
using Resurgence.Ui;

namespace Resurgence.Tests.WebTests;

/// <summary>The manifest of the picture slots (src/Ui/AssetSlots.cs, docs/images.md#missing-pictures) against the pages that draw them.</summary>
public partial class AssetSlotsTests
{
    // <AssetSlot Name="…" followed by the next attribute: the value may hold quotes and '>' of an expression
    [GeneratedRegex(@"<AssetSlot\s+Name=""(?<name>.*?)""\s+(?:Width|Height|Class)=")]
    private static partial Regex Slot();

    // the text of a string literal up to the next quote or brace: "ach-" + x, $"item-{id}", "antler-diagram" : "antler-guide"
    [GeneratedRegex(@"""(?<text>[^""{}]*)")]
    private static partial Regex Literal();

    // the map's own slots, which no <AssetSlot Name="literal"> names: map-bg, map-<NN>, marker-<kind>, reserve-<NN>, page-bg
    [GeneratedRegex(@"""(?<text>(?:(?:map|marker|reserve)-[a-z0-9-]*)|page-bg)")]
    private static partial Regex MapLiteral();

    static IEnumerable<string> Razor() => Directory.EnumerateFiles(Repo.Path("src", "Ui"), "*.razor", SearchOption.AllDirectories)
        .Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\") && Path.GetFileName(f) != "AssetSlot.razor");

    [Fact]
    public void EverySlotOfThePagesIsInTheManifest()
    {
        var unknown = new SortedSet<string>();
        var slots = 0;
        foreach (var file in Razor())
        {
            var text = File.ReadAllText(file);
            var found = Slot().Matches(text);
            Assert.True(found.Count == Regex.Count(text, "<AssetSlot[ \r\n]"), $"{Path.GetFileName(file)}: an <AssetSlot> without Name first, then Width, Height or Class: the scan cannot read it");
            foreach (Match m in found)
            {
                slots++;
                var name = m.Groups["name"].Value;
                // a name without '@' is the literal; with it, every string the expression builds it from that holds a '-' must be a fixed key or the start of a pattern
                var parts = name.StartsWith('@') ? Literal().Matches(name).Select(l => l.Groups["text"].Value).Where(t => t.Contains('-') && Regex.IsMatch(t, "^[a-z][a-z0-9-]*$")) : [name];
                foreach (var part in parts)
                    if (!AssetSlots.Knows(part)) unknown.Add($"{part} ({Path.GetFileName(file)})");
            }
        }
        Assert.True(slots > 100, "the scan found the slots");
        Assert.True(unknown.Count == 0, "slots the manifest (src/Ui/AssetSlots.cs) does not know: " + string.Join(", ", unknown));
    }

    [Fact]
    public void TheMapsAndThePageBackgroundAreInTheManifest()
    {
        var unknown = new SortedSet<string>();
        var found = 0;
        foreach (var name in new[] { "MapPlaceholder.razor", "LaunchMap.razor", "ShellLayout.razor" })
            foreach (var file in Razor().Where(f => Path.GetFileName(f) == name))
                foreach (Match m in MapLiteral().Matches(File.ReadAllText(file)))
                {
                    found++;
                    if (!AssetSlots.Knows(m.Groups["text"].Value)) unknown.Add($"{m.Groups["text"].Value} ({name})");
                }
        Assert.True(found >= 4, "the scan found the map slots");
        Assert.True(unknown.Count == 0, "slots the manifest (src/Ui/AssetSlots.cs) does not know: " + string.Join(", ", unknown));
    }

    static AssetSources Sources(bool live)
    {
        var sp = new ServiceCollection().AddLogging().AddMockServices().BuildServiceProvider();
        return new(new HostMode { Live = live }, sp.GetRequiredService<IStoreService>(), sp.GetRequiredService<ILeaderboardsService>(), sp.GetRequiredService<IHelpService>(),
            sp.GetRequiredService<ICompetitionsService>(), sp.GetRequiredService<IBankService>(), sp.GetRequiredService<IAccountService>(), sp.GetRequiredService<IServersService>());
    }

    [Fact]
    public async Task TheMockSiteExpandsItsDataPatterns()
    {
        var s = Sources(live: false);
        var keys = (await AssetSlots.ExpectedAsync(s)).Select(e => e.Key).ToHashSet();
        var item = (await s.Store.ItemsAsync("all", "", 1)).Rows[0];
        Assert.Contains($"item-{item.Id}", keys);
        Assert.DoesNotContain($"store-item-{item.Id}", keys);
        Assert.Contains("logo-rating-2", keys);
        Assert.Contains("reserve-12", keys);
        Assert.Contains("map-01", keys);
        Assert.Contains("marker-caller", keys);
        Assert.Contains("marker-treestand-active", keys);
        Assert.Contains("avatar-54", keys);
        Assert.Contains("em-pack", keys);
        Assert.Contains("recruit-hero", keys);
        Assert.Contains("competition-board", keys);
        Assert.Contains("marker-bait", keys);
        Assert.Contains(keys, k => k.StartsWith("species-"));
        Assert.Contains(keys, k => k.StartsWith("video-"));
        Assert.Contains(keys, k => k.StartsWith("competition-hero-"));
        Assert.Contains(keys, k => k.StartsWith("bundle-"));
    }

    [Fact]
    public async Task TheLiveSiteUsesTheStoreNamesAndHidesTheMocksOnlyPictures()
    {
        var s = Sources(live: true);
        var expected = await AssetSlots.ExpectedAsync(s);
        var keys = expected.Select(e => e.Key).ToHashSet();
        var item = (await s.Store.ItemsAsync("all", "", 1)).Rows[0];
        Assert.Contains($"store-item-{item.Id}", keys);
        Assert.DoesNotContain($"item-{item.Id}", keys);
        Assert.DoesNotContain("em-pack", keys);
        Assert.DoesNotContain(keys, k => k.StartsWith("mission-giver-"));
        // /competitions and /invite-friends are mock-only pages (LauncherGuard.MockData), and the server never sends the bait and feeder markers
        Assert.DoesNotContain(expected, e => e.Area == "competitions");
        Assert.DoesNotContain("recruit-hero", keys);
        Assert.DoesNotContain(keys, k => k.StartsWith("marker-bait") || k.StartsWith("marker-feeder"));
        Assert.Contains("marker-tripod-active", keys);
    }

    [Fact]
    public async Task TheReportCountsTheSlotsWithoutFileAndKeepsWhatPagesDrewThatTheManifestMissed()
    {
        var expected = await AssetSlots.ExpectedAsync(Sources(live: false));
        using var t = new TempDir("asset-slots-");
        File.WriteAllText(Path.Combine(t.Path, "Logo Bar.png"), "");
        var images = new AssetImages(AssetImages.Index(t.Path));
        images.NoteMissing("logo-rating-2", "/feed");       // a manifest key: listed once, in missing
        images.NoteMissing("new-slot-of-a-page", "/store"); // a key the manifest does not have
        var report = AssetSlots.Report(expected, images);

        Assert.Equal(expected.Count, report.Total);
        Assert.Equal(1, report.Present);
        Assert.Equal(1, report.Areas["decoration"].Present);
        Assert.Equal(expected.Count - 1, report.Missing.Values.Sum(l => l.Count));
        Assert.DoesNotContain(report.Missing["decoration"], m => m.Key == "logo-bar");
        var rating = Assert.Single(report.Missing["decoration"], m => m.Key == "logo-rating-2");
        Assert.Equal(("62x82", "Footer"), (rating.Size, rating.Page));
        Assert.Equal(new Dictionary<string, string> { ["new-slot-of-a-page"] = "/store" }, report.SeenWithoutFile);
    }

    [Fact]
    public async Task TheManifestPartIsKeptButWhatPagesDrewIsFresh()
    {
        var images = new AssetImages(new Dictionary<string, string>());
        var list = new MissingList(images);
        var first = await list.GetAsync(Sources(live: false));
        images.NoteMissing("new-slot-of-a-page", "/store");
        var second = await list.GetAsync(Sources(live: true)); // the live site has no em-pack: a recomputed manifest would have one slot fewer
        Assert.Equal(first.Total, second.Total);
        Assert.Empty(first.SeenWithoutFile);
        Assert.Equal("/store", second.SeenWithoutFile["new-slot-of-a-page"]);
    }
}
