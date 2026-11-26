using System.Text.RegularExpressions;
using Resurgence.Launcher;
using static Resurgence.Tests.LauncherApp.LogLockTests;

namespace Resurgence.Tests.LauncherApp;

public class RabbitSoundTests
{
    // a synthetic exe: the 19 places below, beside the goose call patch's places and the log lock's table (all below 400); the first is the site; the rest is zero
    static readonly RabbitSound Rabbit = new("synthetic", Site: 1000, Profile: 7000, Checks: [
        (1000, "55 8B EC 83 E4 F8 6A FF 68 @4000"),
        (1100, "8B F9 89 7C 24 18"),
        (1200, "8B 0A 85 C9"),
        (1300, "8B 01 8B 49 04 3B C1"),
        (1400, "8B 75 08 3B 30 74 0C"),
        (1500, "8B 45 0C 8B 48 04"),
        (1600, "83 C2 14"),
        (1700, "C2 08 00"),
        (1800, "8B 08 8B 44 24 3C"),
        (1900, "8D 70 10 38 5C 24 20"),
        (2000, "8B 80 6C 12 00 00"),
        (2100, "8B 83 6C 12 00 00 8B 40"),
        (2200, "C7 86 B4 00 00 00 @7000"),
        (2300, "8B 86 1C 12 00 00 53"),
        (2400, "8B 44 24 38 8D 50 0C"),
        (2500, "68 @4100"),
        (2600, "8D 4F 0C E8"),
        (4100, "A0 A5 75 A3"),
        (2700, "83 7E 14 10 72 02 8B 36"),
    ]);

    static byte[] Bytes(string expected, uint image) =>
        Convert.FromHexString(Regex.Replace(expected.Replace(" ", ""), @"@(\d+)", m => Convert.ToHexString(Le(image + uint.Parse(m.Groups[1].Value)))));

    static FakeGame Game(uint image = Image)
    {
        var game = GooseCallTests.Game(image);
        foreach (var (at, expected) in Rabbit.Checks) Bytes(expected, image).CopyTo(game.Memory, (int)at);
        return game;
    }

    public static IEnumerable<object[]> CheckNumbers => Enumerable.Range(1, 19).Select(n => new object[] { n });
    public static IEnumerable<object[]> EveryCheckFirstAndLastByte => CheckNumbers.SelectMany(n => new[] { false, true }.Select(last => new[] { n[0], last }));

    [Fact]
    public void TheFunctionIsTheExpectedBytesAtAnyBase()
    {
        // at the image base 4194304 the return point (site + 6) is 10941574 and the compared value is 15103868; the second base only moves those two values
        Assert.Equal("8B44240481388A6B39B2740D558BEC83E4F8B886F4A600FFE08B44240885C074568B400485C0744F81387C77E60075478B80C003000085C0743D8B801C12000085C074338B400485C0742C8378201072268B400C"
            + "8138616E696D751B817804616C2E77751281780865726577750981780C6F6C665F749553568B511C3B512073298B3285F6741A8B063B460473138B1885DB7408813B3BB5DCC2740A83C004EBE883C214EBD6895C240C5E5BE95BFFFFFF",
            Convert.ToHexString(RabbitSound.Known.Code(4194304)));
        Assert.StartsWith("8B44240481388A6B39B2740D558BEC83E4F8B886F43B01FFE08B44240885C074568B400485C0744F81387C777B0175478B80C003", Convert.ToHexString(RabbitSound.Known.Code(13959168)));
        Assert.Equal(177, RabbitSound.Known.Code(0).Length);
        Assert.Equal(LogLock.Known.ExeSha256, RabbitSound.Known.ExeSha256);
        Assert.Equal(19, RabbitSound.Known.Checks.Length);
        Assert.Equal(RabbitSound.Known.Site, RabbitSound.Known.Checks[0].At);
    }

    [Fact]
    public void TheRealTableRunsThroughApplyOnAnImageBuiltFromItsOwnChecks()
    {
        var known = RabbitSound.Known;
        var memory = new byte[11_000_000]; // past the last checked place
        foreach (var (at, expected) in known.Checks) Bytes(expected, Image).CopyTo(memory, (int)at);
        var game = new FakeGame(Image, memory);
        Assert.Null(known.Apply(game));
        Assert.Equal([Page, Image + known.Site], game.Writes.Select(w => w.Address));
        Assert.Equal(known.Code(Image), game.Writes[0].Bytes);
    }

    [Theory]
    [InlineData(Image)] // the page above the image
    [InlineData(134217728u)] // the page below the image
    public void OnTheExpectedBytesTheSiteJumpsToTheFunction(uint image)
    {
        var game = Game(image);
        Assert.Null(Rabbit.Apply(game));
        Assert.Equal([Page, image + 1000], game.Writes.Select(w => w.Address));
        Assert.Equal(Rabbit.Code(image), game.Writes[0].Bytes);
        Assert.Equal(177, game.Allocated);
        var site = game.Read(image + 1000, 6)!;
        Assert.Equal("E9", Convert.ToHexString(site[..1]));
        Assert.Equal(Page, unchecked(image + 1005 + BitConverter.ToUInt32(site, 1)));
        Assert.Equal("90", Convert.ToHexString(site[5..]));
    }

    [Theory]
    [MemberData(nameof(EveryCheckFirstAndLastByte))]
    public void AnyOtherValueInAnyOfTheCheckedPlacesWritesNothing(int n, bool last)
    {
        var game = Game();
        var (at, expected) = Rabbit.Checks[n - 1];
        game.Memory[at + (last ? Bytes(expected, Image).Length - 1 : 0)]++;
        Assert.Equal($"check {n} holds other values", Rabbit.Apply(game));
        Assert.Empty(game.Writes);
        Assert.Equal(0, game.Allocated);
    }

    [Theory]
    [MemberData(nameof(CheckNumbers))]
    public void AnUnreadablePlaceIsReportedAndWritesNothing(int n)
    {
        var checks = Rabbit.Checks.ToArray();
        checks[n - 1] = (8190, "00 00 00 00");
        var game = Game();
        Assert.Equal($"check {n} could not be read", (Rabbit with { Checks = checks }).Apply(game));
        Assert.Empty(game.Writes);
        Assert.Equal(0, game.Allocated);
        var unknownImage = new FakeGame(0, new byte[8192]);
        Assert.Equal("the game's memory could not be read", Rabbit.Apply(unknownImage));
        Assert.Empty(unknownImage.Writes);
    }

    [Fact]
    public void NoMemoryAFailedFunctionWriteAndAFailedJumpWriteAreReported()
    {
        var noPage = Game();
        noPage.CanAllocate = false;
        var noWrite = Game();
        noWrite.WritesAllowed = 0;
        foreach (var (game, why) in new[] { (noPage, "no memory could be allocated in the game"), (noWrite, "the sound function could not be written") })
        {
            Assert.Equal(why, Rabbit.Apply(game));
            Assert.Empty(game.Writes);
            Assert.Equal(177, game.Allocated);
        }

        var noJump = Game();
        noJump.WritesAllowed = 1;
        var site = noJump.Read(Image + 1000, 6);
        Assert.Equal("the jump to the sound function could not be written", Rabbit.Apply(noJump));
        Assert.Equal([Page], noJump.Writes.Select(w => w.Address));
        Assert.Equal(Rabbit.Code(Image), noJump.Writes[0].Bytes);
        Assert.Equal(site, noJump.Read(Image + 1000, 6));
    }

    [Fact]
    public void EachOfTheThreePatchesIsAppliedWhenAnotherRefusesAndLogsItsOwnLine()
    {
        uint[][] writes = [[Page, Image + 100], [Page, Image + 1000], [Page, Image + 68]];
        string[] names = ["goose call patch", "rabbit sound patch", "log lock"];
        int[] breaks = [300, 1000, 65]; // the goose call's registration function, the site, the log writer's table
        for (var refused = 0; refused < 3; refused++)
        {
            var game = Game();
            game.Memory[breaks[refused]]++;
            var log = new GooseCallTests.Lines();
            GooseCallTests.Goose.ApplyAndLog(game, log);
            Rabbit.ApplyAndLog(game, log);
            Assert.True(Synthetic.ApplyAndResume(game, log));
            Assert.True(game.Resumed);
            Assert.Equal(writes.Where((_, i) => i != refused).SelectMany(w => w), game.Writes.Select(w => w.Address));
            for (var i = 0; i < 3; i++)
                Assert.StartsWith($"The game's {names[i]} is " + (i == refused ? "not applied, the game starts without it: " : "applied"), log.Seen[i]);
        }
    }
}
