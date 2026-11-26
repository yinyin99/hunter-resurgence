using Microsoft.Extensions.Logging;
using Resurgence.Launcher;
using static Resurgence.Tests.LauncherApp.LogLockTests;

namespace Resurgence.Tests.LauncherApp;

public class GooseCallTests
{
    // a synthetic exe: the five places below, beside the log lock's table at 64; the rest is zero
    internal static readonly GooseCall Goose = new("synthetic", Site: 100, Args: 87, CategoryPush: 50, Category: 200, Register: 300);

    internal static FakeGame Game(uint image = Image)
    {
        var game = LogLockTests.Game(image);
        byte[] call = [.. Convert.FromHexString("E8"), .. Le(300 - 105)];
        call.CopyTo(game.Memory, 100);
        Convert.FromHexString("558D4C244C51568BCF896C247C").CopyTo(game.Memory, 87);
        byte[] push = [.. Convert.FromHexString("6A1568"), .. Le(image + 200)];
        push.CopyTo(game.Memory, 50);
        "animation_track_event\0"u8.ToArray().CopyTo(game.Memory, 200);
        Convert.FromHexString("558BEC83E4F881EC1C040000").CopyTo(game.Memory, 300);
        return game;
    }

    internal sealed class Lines : ILogger
    {
        public List<string> Seen { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Seen.Add(formatter(state, exception));
    }

    [Theory]
    [InlineData(Image)] // the page above the image
    [InlineData(134217728u)] // the page below the image
    public void OnTheExpectedBytesTheCallPointsAtTheFunction(uint image)
    {
        var game = Game(image);
        Assert.Null(Goose.Apply(game));
        Assert.Equal([Page, image + 100], game.Writes.Select(w => w.Address));
        Assert.Equal(Goose.Code(image, Page), game.Writes[0].Bytes);
        Assert.Equal(GooseCall.Size, game.Allocated);
        var call = game.Read(image + 100, 5)!;
        Assert.Equal("E8", Convert.ToHexString(call[..1]));
        Assert.Equal(Page, unchecked(image + 105 + BitConverter.ToUInt32(call, 1)));
    }

    [Theory]
    [InlineData(101, "other values in the registration call")]
    [InlineData(99, "other values in the registration call's arguments")]
    [InlineData(53, "other values in the category's push")]
    [InlineData(220, "other values in the category's text")]
    [InlineData(311, "other values in the registration function")]
    public void AnyOtherValueInTheFivePlacesWritesNothing(int at, string why)
    {
        var game = Game();
        game.Memory[at]++;
        Assert.Equal(why, Goose.Apply(game));
        Assert.Empty(game.Writes);
        Assert.Equal(0, game.Allocated);
    }

    [Fact]
    public void AnUnreadableGameNoMemoryOrAFailedWriteIsReportedAndWritesNothing()
    {
        var noPage = Game();
        noPage.CanAllocate = false;
        var noWrite = Game();
        noWrite.WritesAllowed = 0;
        foreach (var (patch, game, why, allocated) in new[] {
            (Goose, new FakeGame(0, new byte[8192]), "the game's memory could not be read", 0),
            (Goose with { Args = 8190 }, Game(), "the game's memory could not be read", 0),
            (Goose, noPage, "no memory could be allocated in the game", GooseCall.Size),
            (Goose, noWrite, "the function could not be written", GooseCall.Size) })
        {
            Assert.Equal(why, patch.Apply(game));
            Assert.Empty(game.Writes);
            Assert.Equal(allocated, game.Allocated);
        }
    }

    [Fact]
    public void AFailedCallWriteIsReportedAndTheCallKeepsItsBytes()
    {
        var game = Game();
        game.WritesAllowed = 1;
        var call = game.Read(Image + 100, 5);
        Assert.Equal("the registration call could not be written", Goose.Apply(game));
        Assert.Equal([Page], game.Writes.Select(w => w.Address));
        Assert.Equal(Goose.Code(Image, Page), game.Writes[0].Bytes);
        Assert.Equal(call, game.Read(Image + 100, 5));
    }

    [Fact]
    public void EachPatchIsAppliedWhenTheOtherRefusesAndLogsItsOwnLine()
    {
        var gooseRefuses = Game();
        gooseRefuses.Memory[300]++;
        var log = new Lines();
        Goose.ApplyAndLog(gooseRefuses, log);
        Assert.True(Synthetic.ApplyAndResume(gooseRefuses, log));
        Assert.Equal([Page, Image + 68], gooseRefuses.Writes.Select(w => w.Address));
        Assert.True(gooseRefuses.Resumed);
        Assert.Equal(["The game's goose call patch is not applied, the game starts without it: other values in the registration function", "The game's log lock is applied"], log.Seen);

        var lockRefuses = Game();
        lockRefuses.Memory[65]++;
        log = new Lines();
        Goose.ApplyAndLog(lockRefuses, log);
        Assert.True(Synthetic.ApplyAndResume(lockRefuses, log));
        Assert.Equal([Page, Image + 100], lockRefuses.Writes.Select(w => w.Address));
        Assert.True(lockRefuses.Resumed);
        Assert.Equal("The game's goose call patch is applied", log.Seen[0]);
        Assert.StartsWith("The game's log lock is not applied, the game starts without it: ", log.Seen[1]);
    }

    [Fact]
    public void TheFunctionRenamesTheGooseCallEventAndJumpsToTheRegistrationAtAnyBase()
    {
        // mov eax, [esp+4]; cmp [eax+16], 28; jne; mov eax, [eax]; compare the 28 characters; mov [esp+4], the new name; mov eax, Register; jmp eax; filler; the new name, its count and capacity
        Assert.Equal("8B4424048378101C75488B008138736E645F753E817804736E6F7775358178085F676F6F752C81780C73655F637523817810616C6C5F751A817814736571757511817818656E63657508"
            + "C744240460000004B8B0FEA900FFE0CCCCCCCCCCCCCC706C61795F63616C6C5F736F756E64000F0000000F00000000000000", Convert.ToHexString(GooseCall.Known.Code(4194304, 67108864)));
        Assert.Equal(GooseCall.Size, GooseCall.Known.Code(0, 0).Length);
        Assert.Equal(GooseCall.Known.ExeSha256, LogLock.Known.ExeSha256);
    }
}
