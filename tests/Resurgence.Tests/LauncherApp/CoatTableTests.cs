using Resurgence.Launcher;
using static Resurgence.Tests.LauncherApp.LogLockTests;

namespace Resurgence.Tests.LauncherApp;

public class CoatTableTests
{
    // a synthetic exe: the six places below, beside the other patches' places (all below 3000); the first two end with the call; the rest is zero
    static readonly CoatTable Coat = new("synthetic", Lookup: 3000, Layer: 3100, Import: 3400, Checks: [
        (2993, CoatTable.Known.Checks[0].Expected),
        (3093, CoatTable.Known.Checks[1].Expected),
        (3200, CoatTable.Known.Checks[2].Expected),
        (3300, CoatTable.Known.Checks[3].Expected),
        (3400, CoatTable.Known.Checks[4].Expected),
        (3500, CoatTable.Known.Checks[5].Expected),
    ]);

    static FakeGame Game(uint image = Image)
    {
        var game = GooseCallTests.Game(image);
        foreach (var (at, expected) in Coat.Checks) Convert.FromHexString(expected.Replace(" ", "")).CopyTo(game.Memory, (int)at);
        return game;
    }

    static byte[] Call(uint image, uint at) => [.. Convert.FromHexString("E8"), .. Le(unchecked(Page - (image + at + 5)))];

    public static IEnumerable<object[]> CheckNumbers => Enumerable.Range(1, 6).Select(n => new object[] { n });
    public static IEnumerable<object[]> EveryCheckFirstAndLastByte => CheckNumbers.SelectMany(n => new[] { false, true }.Select(last => new[] { n[0], last }));

    [Fact]
    public void TheFunctionIsTheExpectedBytesAtAnyBase()
    {
        // push [esp+4]; push ecx; push edx; call [the import]; add esp, 12; ret. At the image base 4194304 the import's slot is at 14001156; the second base only moves it
        Assert.Equal("FF7424045152FF1504A4D50083C40CC3", Convert.ToHexString(CoatTable.Known.Code(4194304)));
        Assert.Equal("FF7424045152FF1504A46A0183C40CC3", Convert.ToHexString(CoatTable.Known.Code(13959168)));
        Assert.Equal(CoatTable.Size, CoatTable.Known.Code(0).Length);
        Assert.Equal(16, CoatTable.Size);
        Assert.Equal(LogLock.Known.ExeSha256, CoatTable.Known.ExeSha256);
        Assert.Equal(6, CoatTable.Known.Checks.Length);
        // the two checked calls end where the calls are, and the import is checked
        Assert.Equal(CoatTable.Known.Lookup, CoatTable.Known.Checks[0].At + 7);
        Assert.Equal(CoatTable.Known.Layer, CoatTable.Known.Checks[1].At + 7);
        Assert.Equal(CoatTable.Known.Import, CoatTable.Known.Checks[4].At);
    }

    [Fact]
    public void TheRealTableRunsThroughApplyOnAnImageBuiltFromItsOwnChecks()
    {
        var known = CoatTable.Known;
        var memory = new byte[12_000_000]; // past the last checked place
        foreach (var (at, expected) in known.Checks) Convert.FromHexString(expected.Replace(" ", "")).CopyTo(memory, (int)at);
        var game = new FakeGame(Image, memory);
        Assert.Null(known.Apply(game));
        Assert.Equal([Page, Image + known.Layer, Image + known.Lookup], game.Writes.Select(w => w.Address));
        Assert.Equal(known.Code(Image), game.Writes[0].Bytes);
    }

    [Theory]
    [InlineData(Image)] // the page above the image
    [InlineData(134217728u)] // the page below the image
    public void OnTheExpectedBytesBothCallsPointAtTheFunction(uint image)
    {
        var game = Game(image);
        Assert.Null(Coat.Apply(game));
        Assert.Equal([Page, image + 3100, image + 3000], game.Writes.Select(w => w.Address));
        Assert.Equal(Coat.Code(image), game.Writes[0].Bytes);
        Assert.Equal(CoatTable.Size, game.Allocated);
        Assert.Equal(Call(image, 3000), game.Read(image + 3000, 5));
        Assert.Equal(Call(image, 3100), game.Read(image + 3100, 5));
    }

    [Theory]
    [MemberData(nameof(EveryCheckFirstAndLastByte))]
    public void AnyOtherValueInAnyOfTheCheckedPlacesWritesNothing(int n, bool last)
    {
        var game = Game();
        var (at, expected) = Coat.Checks[n - 1];
        game.Memory[at + (last ? Convert.FromHexString(expected.Replace(" ", "")).Length - 1 : 0)]++;
        Assert.Equal($"check {n} holds other values", Coat.Apply(game));
        Assert.Empty(game.Writes);
        Assert.Equal(0, game.Allocated);
    }

    [Theory]
    [MemberData(nameof(CheckNumbers))]
    public void AnUnreadablePlaceIsReportedAndWritesNothing(int n)
    {
        var checks = Coat.Checks.ToArray();
        checks[n - 1] = (8190, "00 00 00 00");
        var game = Game();
        Assert.Equal($"check {n} could not be read", (Coat with { Checks = checks }).Apply(game));
        Assert.Empty(game.Writes);
        Assert.Equal(0, game.Allocated);
        var unknownImage = new FakeGame(0, new byte[8192]);
        Assert.Equal("the game's memory could not be read", Coat.Apply(unknownImage));
        Assert.Empty(unknownImage.Writes);
    }

    [Fact]
    public void ASecondApplicationChangesNothing()
    {
        var game = Game();
        Assert.Null(Coat.Apply(game));
        var memory = game.Memory.ToArray();
        Assert.Equal("check 1 holds other values", Coat.Apply(game));
        Assert.Equal(3, game.Writes.Count);
        Assert.Equal(CoatTable.Size, game.Allocated);
        Assert.Equal(memory, game.Memory);
    }

    [Fact]
    public void NoMemoryAndAFailedWriteAreReportedAndTheCallsKeepTheirBytes()
    {
        var noPage = Game();
        noPage.CanAllocate = false;
        var noFunction = Game();
        noFunction.WritesAllowed = 0;
        var noCalls = Game();
        noCalls.WritesAllowed = 1; // the function is written, the first call is not
        var noLookup = Game();
        noLookup.WritesAllowed = 2; // the layer match's call is written, the lookup's is not
        foreach (var (game, why) in new[] { (noPage, "no memory could be allocated in the game"), (noFunction, "the compare function could not be written"),
            (noCalls, "the calls could not be written"), (noLookup, "the calls could not be written") })
        {
            var lookup = game.Read(Image + 3000, 5);
            Assert.Equal(why, Coat.Apply(game));
            Assert.Equal(CoatTable.Size, game.Allocated);
            Assert.Equal(lookup, game.Read(Image + 3000, 5)); // the lookup is the last write: until it is made the game finds no table, as before
        }
        Assert.Empty(noPage.Writes);
        Assert.Empty(noFunction.Writes);
        Assert.Equal([Page], noCalls.Writes.Select(w => w.Address));
        Assert.Equal([Page, Image + 3100], noLookup.Writes.Select(w => w.Address));
    }

    [Fact]
    public void ThePatchIsAppliedWhenTheLogLockRefusesAndLogsItsOwnLine()
    {
        var game = Game();
        game.Memory[65]++;
        var log = new GooseCallTests.Lines();
        Coat.ApplyAndLog(game, log);
        Assert.True(Synthetic.ApplyAndResume(game, log));
        Assert.Equal([Page, Image + 3100, Image + 3000], game.Writes.Select(w => w.Address));
        Assert.Equal("The game's coat table patch is applied", log.Seen[0]);
        Assert.StartsWith("The game's log lock is not applied, the game starts without it: ", log.Seen[1]);

        var refused = Game();
        refused.Memory[3200]++;
        log = new GooseCallTests.Lines();
        Coat.ApplyAndLog(refused, log);
        Assert.True(Synthetic.ApplyAndResume(refused, log));
        Assert.Equal([Page, Image + 68], refused.Writes.Select(w => w.Address));
        Assert.Equal(["The game's coat table patch is not applied, the game starts without it: check 3 holds other values", "The game's log lock is applied"], log.Seen);
    }

    // The coat choice. This proves the reasoning of the patch (what the game picks when its two compares ignore letter case, and that the other species keep their choice), not the patch's bytes:
    // it is a port of the game's choice for one model slot (a seed, the uniform index, the weighted pick when the species' table has a group for the slot, the layer match) over a reduced fixture of the shipped tables.
    // The names are invented; what is kept is their letter cases and the weights.
    record Species(string Slot, (string Layer, int Coat)[] Model, (string Texture, (string Layer, int Weight)[] Layers)[] Groups);

    static readonly Species HareAsShipped = new("hare_dif.dds", [("hare_albino_dif", 2), ("hare_event_dif", 73), ("hare_dif", 0)],
        [("hare_DIF.dds", [("hare_DIF", 9999), ("hare_albino_DIF", 1), ("hare_event_dif", 0)])]);

    static readonly Species HareEvent = HareAsShipped with { Groups = [("hare_dif.dds", [("hare_dif", 0), ("hare_albino_dif", 0), ("hare_event_dif", 10000)])] };

    // equal names: its table is found either way, its first group (filed first) is the one both compares pick
    static readonly Species Cottontail = new("cottontail_dif.dds", [("cottontail_dif", 0), ("cottontail_dark_dif", 1), ("cottontail_event_dif", 54)],
        [("cottontail_dif.dds", [("cottontail_dif", 7500), ("cottontail_dark_dif", 2500), ("cottontail_event_dif", 0)])]);

    // two groups that differ only by case: they stay apart (the grouping at load is exact), and the lookup finds the one filed first with either compare
    static readonly Species MuleDeer = new("deer_dif.dds", [("deer_dif", 0), ("deer_event_dif", 77)],
        [("deer_dif.dds", [("deer_dif", 10000)]), ("DEER_dif.dds", [("deer_event_dif", 0)])]);

    static uint Lcg(uint x) => unchecked(x * 214013 + 2531011);
    static uint R15(uint x) => (x >> 16) & 32767;

    static int CoatOf(Species s, uint seed, bool ignoreCase)
    {
        bool Same(string a, string b) => string.Equals(a, b, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        var index = (int)(R15(Lcg(seed)) % (uint)s.Model.Length); // uniform, every layer of the model with the same chance
        var group = s.Groups.Where(g => Same(g.Texture, s.Slot)).Select(g => g.Layers).FirstOrDefault();
        if (group is { } layers && layers.Sum(l => l.Weight) is var total and > 0)
        {
            var r = (int)(R15(Lcg(seed)) % (uint)total);
            var (picked, acc) = ("", 0);
            foreach (var (layer, weight) in layers)
            {
                picked = layer;
                if (r < (acc += weight)) break;
            }
            index = Array.FindIndex(s.Model, m => Same(m.Layer, picked));
        }
        return index >= 0 ? s.Model[index].Coat : 0; // no layer applied: the game keeps coat 0
    }

    static Dictionary<int, int> Counts(Species s, bool ignoreCase, int seeds = 30000)
    {
        var random = new Random(1);
        var counts = new Dictionary<int, int>();
        for (var i = 0; i < seeds; i++)
        {
            var coat = CoatOf(s, (uint)random.Next(), ignoreCase);
            counts[coat] = counts.GetValueOrDefault(coat) + 1;
        }
        return counts;
    }

    [Fact]
    public void WithTheExactCompareTheHaresThreeSkinsComeOutAboutAThirdEach()
    {
        var counts = Counts(HareAsShipped, ignoreCase: false);
        Assert.Equal([0, 2, 73], counts.Keys.Order());
        Assert.All(counts.Values, n => Assert.InRange(n, 30000 * 0.31, 30000 * 0.36));
    }

    [Fact]
    public void WithTheCaseBlindCompareTheNormalSkinComesOutAbout9999InTenThousandTheAlbinoAboutOneAndTheEventSkinNever()
    {
        var counts = Counts(HareAsShipped, ignoreCase: true);
        Assert.DoesNotContain(73, counts.Keys);
        Assert.InRange(counts[0], 29_990, 30_000);
        Assert.InRange(counts.GetValueOrDefault(2), 0, 10);
        Assert.Equal(30000, counts.Values.Sum());
    }

    [Fact]
    public void TheEventsOwnTableGivesTheEventSkinWithEitherCompare()
    {
        Assert.Equal(new Dictionary<int, int> { [73] = 30000 }, Counts(HareEvent, ignoreCase: false));
        Assert.Equal(new Dictionary<int, int> { [73] = 30000 }, Counts(HareEvent, ignoreCase: true));
    }

    [Fact]
    public void AnotherSpeciesChoiceIsTheSameWithEitherCompareForEverySeed()
    {
        foreach (var (species, eventCoat) in new[] { (Cottontail, 54), (MuleDeer, 77) })
        {
            var random = new Random(2);
            for (var i = 0; i < 30000; i++)
            {
                var seed = (uint)random.Next();
                Assert.Equal(CoatOf(species, seed, ignoreCase: false), CoatOf(species, seed, ignoreCase: true));
            }
            Assert.DoesNotContain(eventCoat, Counts(species, ignoreCase: true).Keys);
        }
        Assert.InRange(Counts(Cottontail, ignoreCase: true)[1], 30000 * 0.23, 30000 * 0.27);
    }
}
