using Resurgence.Launcher;
using static Resurgence.Tests.LauncherApp.LogLockTests;

namespace Resurgence.Tests.LauncherApp;

public class DogAnswerTests
{
    static byte[] H(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    // a synthetic exe: the three places below, beside the other patches' places (all below 3500); the call at 3000 reaches the function at 3300, whose first instructions are the real ones; the rest is zero
    static readonly DogAnswer Dog = new("synthetic", Site: 3000, Target: 3300, Checks: [
        (2984, "50 51 8B CB E8 B4 64 EC FF 8B D6 83 C4 04 8B F0 E8 27 01 00 00 C7 87 9C 00 00 00 05 00 00 00"),
        (3300, DogAnswer.Known.Checks[1].Expected),
        (3400, DogAnswer.Known.Checks[2].Expected),
    ]);

    static FakeGame Game(uint image = Image)
    {
        var game = GooseCallTests.Game(image);
        foreach (var (at, expected) in Dog.Checks) H(expected).CopyTo(game.Memory, (int)at);
        return game;
    }

    public static IEnumerable<object[]> CheckNumbers => Enumerable.Range(1, 3).Select(n => new object[] { n });
    public static IEnumerable<object[]> EveryCheckFirstAndLastByte => CheckNumbers.SelectMany(n => new[] { false, true }.Select(last => new[] { n[0], last }));

    [Fact]
    public void TheRoutineIsTheExpectedBytesAtAnyBase()
    {
        // mov dword ptr [esi+172], 0; jmp image+2703488 (the rel32 is that minus the end of the routine at the page). Image 4194304 with the page below it, and image 134217728 with the page above it
        Assert.Equal("C786AC00000000000000E9714069FC", Convert.ToHexString(DogAnswer.Known.Code(4194304, 67108864)));
        Assert.Equal("C786AC00000000000000E971402904", Convert.ToHexString(DogAnswer.Known.Code(134217728, 67108864)));
        Assert.Equal(DogAnswer.Size, DogAnswer.Known.Code(0, 0).Length);
        Assert.Equal(15, DogAnswer.Size);
        Assert.Equal(LogLock.Known.ExeSha256, DogAnswer.Known.ExeSha256);
        Assert.Equal(3, DogAnswer.Known.Checks.Length);
    }

    [Fact]
    public void TheRealChecksEndWhereTheCallIsAndItsOriginalTargetIsTheCheckedFunction()
    {
        var known = DogAnswer.Known;
        var (at, expected) = known.Checks[0];
        var bytes = H(expected);
        Assert.Equal(known.Site, at + 16);
        Assert.Equal(232, bytes[16]);
        // the call's own target, from the bytes the first check demands, is the function the second check reads and the routine jumps to
        Assert.Equal(known.Target, unchecked(known.Site + 5 + BitConverter.ToUInt32(bytes, 17)));
        Assert.Equal(known.Target, known.Checks[1].At);
    }

    [Fact]
    public void TheRealPatchRunsThroughApplyOnAnImageBuiltFromItsOwnChecks()
    {
        var known = DogAnswer.Known;
        var memory = new byte[4_000_000]; // past the last checked place
        foreach (var (at, expected) in known.Checks) H(expected).CopyTo(memory, (int)at);
        var game = new FakeGame(Image, memory);
        Assert.Null(known.Apply(game));
        Assert.Equal([Page, Image + known.Site + 1], game.Writes.Select(w => w.Address));
        Assert.Equal(known.Code(Image, Page), game.Writes[0].Bytes);
    }

    [Theory]
    [InlineData(Image)] // the page above the image
    [InlineData(134217728u)] // the page below the image
    public void OnTheExpectedBytesTheCallPointsAtTheRoutineAndOnlyItsTargetChanges(uint image)
    {
        var game = Game(image);
        var before = game.Memory.ToArray();
        Assert.Null(Dog.Apply(game));
        Assert.Equal([Page, image + 3001], game.Writes.Select(w => w.Address));
        Assert.Equal(Dog.Code(image, Page), game.Writes[0].Bytes);
        Assert.Equal(4, game.Writes[1].Bytes.Length);
        Assert.Equal(DogAnswer.Size, game.Allocated);
        var call = game.Read(image + 3000, 5)!;
        Assert.Equal(232, call[0]);
        Assert.Equal(Page, unchecked(image + 3005 + BitConverter.ToUInt32(call, 1)));
        // the routine's own jump lands on the function
        var code = game.Writes[0].Bytes;
        Assert.Equal(image + 3300, unchecked(Page + 15 + BitConverter.ToUInt32(code, 11)));
        // nothing but the call's four bytes changed in the image
        var after = game.Memory;
        for (var i = 0; i < before.Length; i++) if (i is < 3001 or > 3004) Assert.Equal(before[i], after[i]);
    }

    [Theory]
    [MemberData(nameof(EveryCheckFirstAndLastByte))]
    public void AnyOtherValueInAnyOfTheCheckedPlacesWritesNothing(int n, bool last)
    {
        var game = Game();
        var (at, expected) = Dog.Checks[n - 1];
        game.Memory[at + (last ? H(expected).Length - 1 : 0)]++;
        Assert.Equal($"check {n} holds other values", Dog.Apply(game));
        Assert.Empty(game.Writes);
        Assert.Equal(0, game.Allocated);
    }

    [Theory]
    [MemberData(nameof(CheckNumbers))]
    public void AnUnreadablePlaceIsReportedAndWritesNothing(int n)
    {
        var checks = Dog.Checks.ToArray();
        checks[n - 1] = (8190, "00 00 00 00");
        var game = Game();
        Assert.Equal($"check {n} could not be read", (Dog with { Checks = checks }).Apply(game));
        Assert.Empty(game.Writes);
        Assert.Equal(0, game.Allocated);
        var unknownImage = new FakeGame(0, new byte[8192]);
        Assert.Equal("the game's memory could not be read", Dog.Apply(unknownImage));
        Assert.Empty(unknownImage.Writes);
    }

    [Fact]
    public void ASecondApplicationChangesNothing()
    {
        var game = Game();
        Assert.Null(Dog.Apply(game));
        var memory = game.Memory.ToArray();
        Assert.Equal("check 1 holds other values", Dog.Apply(game));
        Assert.Equal(2, game.Writes.Count);
        Assert.Equal(DogAnswer.Size, game.Allocated);
        Assert.Equal(memory, game.Memory);
    }

    [Fact]
    public void NoMemoryAndAFailedWriteAreReportedAndTheCallKeepsItsBytes()
    {
        var noPage = Game();
        noPage.CanAllocate = false;
        var noRoutine = Game();
        noRoutine.WritesAllowed = 0;
        var noCall = Game();
        noCall.WritesAllowed = 1; // the routine is written, the call is not
        foreach (var (game, why) in new[] { (noPage, "no memory could be allocated in the game"), (noRoutine, "the routine could not be written"), (noCall, "the call could not be written") })
        {
            var call = game.Read(Image + 3000, 5);
            Assert.Equal(why, Dog.Apply(game));
            Assert.Equal(call, game.Read(Image + 3000, 5));
        }
        Assert.Empty(noPage.Writes);
        Assert.Empty(noRoutine.Writes);
        Assert.Equal([Page], noCall.Writes.Select(w => w.Address));
    }

    [Fact]
    public void TheOutcomeIsLoggedAndTheLogLockIsIndependent()
    {
        var game = Game();
        var log = new GooseCallTests.Lines();
        Dog.ApplyAndLog(game, log);
        Assert.Equal(["The game's dog answer patch is applied"], log.Seen);

        var refused = Game();
        refused.Memory[3400]++;
        log = new GooseCallTests.Lines();
        Dog.ApplyAndLog(refused, log);
        Assert.True(Synthetic.ApplyAndResume(refused, log));
        Assert.Empty(refused.Writes.Where(w => w.Address == Image + 3001));
        Assert.Equal(["The game's dog answer patch is not applied, the game starts without it: check 3 holds other values", "The game's log lock is applied"], log.Seen);
    }
}
