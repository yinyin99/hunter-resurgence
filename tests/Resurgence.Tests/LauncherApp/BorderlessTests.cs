using Resurgence.Launcher;

namespace Resurgence.Tests.LauncherApp;

/// <summary>#810: the borderless display mode keeps the game's window borderless while it runs; a fake stands in for the window.</summary>
public class BorderlessTests
{
    sealed class FakeWindow : IGameWindow
    {
        public nint Window; // 0 until the game's window appears
        public bool Frame = true;
        public bool Front = true; // the game is the foreground window
        public int Unframed, Pid, Backgrounded, Fullscreened;
        public nint Find(int pid) { Pid = pid; return Window; }
        public bool Framed(nint window) => window == Window && Frame;
        public void Unframe(nint window) { Unframed++; Frame = false; }
        public bool NoFront; // no foreground window (secure desktop, lock screen)
        public nint Other = 100, Behind; // the window the player went to; the window the game was placed behind
        public bool? Foreground(nint window, out nint front) { front = NoFront ? 0 : Front ? window : Other; return NoFront ? null : window == Window && Front; }
        public void Background(nint window, nint front) { Backgrounded++; Behind = front; }
        public void Fullscreen(nint window) => Fullscreened++;
    }

    [Fact]
    public async Task TheWindowIsMadeBorderlessWhenItAppearsAndAgainWhenItGetsItsFrameBack()
    {
        var w = new FakeWindow();
        var checks = 0;
        // one answer per check: the game runs without a window, its window appears, it stays borderless, the game puts its frame back, then it exits
        await Borderless.KeepAsync(w, 42, () => ++checks switch
        {
            1 => true,
            2 => (w.Window = 7) != 0,
            3 => w.Unframed == 1,
            4 => w.Unframed == 1 && (w.Frame = true),
            5 => w.Unframed == 2,
            _ => false,
        }, TimeSpan.Zero);
        Assert.Equal((2, 42, 6), (w.Unframed, w.Pid, checks));
    }

    [Fact]
    public async Task InTheBackgroundTheTaskbarShowsAndTheLoopDoesNotCoverItAgain()
    {
        var w = new FakeWindow { Window = 7 };
        var checks = 0;
        // in front the window is made borderless, behind another window it is let go once, a frame it gets back there is left alone, and in front again it is made borderless again
        await Borderless.KeepAsync(w, 42, () => ++checks switch
        {
            1 => true,
            2 => w.Unframed == 1 && !(w.Front = false),
            3 => (w.Backgrounded, w.Unframed) == (1, 1) && (w.Frame = true),
            4 => (w.Backgrounded, w.Unframed) == (1, 1) && (w.Front = true),
            5 => (w.Backgrounded, w.Unframed, w.Fullscreened) == (1, 2, 0),
            _ => false,
        }, TimeSpan.Zero);
        Assert.Equal((1, 2, 0), (w.Backgrounded, w.Unframed, w.Fullscreened));
    }

    [Fact]
    public async Task BackInFrontTheWindowIsFullScreenAgainWithoutBeingResized()
    {
        var w = new FakeWindow { Window = 7, Frame = false };
        var checks = 0;
        await Borderless.KeepAsync(w, 42, () => ++checks switch
        {
            1 => !(w.Front = false),
            2 => w.Backgrounded == 1,
            3 => w.Front = true,
            4 => w.Front,
            _ => false,
        }, TimeSpan.Zero);
        Assert.Equal((1, 1, 0), (w.Backgrounded, w.Fullscreened, w.Unframed));
    }

    [Fact]
    public async Task AWindowThatAppearsInTheBackgroundIsMadeBorderlessAndLetGo()
    {
        var w = new FakeWindow { Window = 7, Front = false };
        var checks = 0;
        await Borderless.KeepAsync(w, 42, () => ++checks <= 2, TimeSpan.Zero);
        Assert.Equal((1, 1), (w.Unframed, w.Backgrounded));
    }

    [Fact]
    public async Task InTheBackgroundTheGameIsPlacedBehindTheWindowThePlayerWentTo()
    {
        var w = new FakeWindow { Window = 7, Frame = false, Front = false, Other = 55 };
        var checks = 0;
        await Borderless.KeepAsync(w, 42, () => ++checks <= 2, TimeSpan.Zero);
        Assert.Equal((1, (nint)55), (w.Backgrounded, w.Behind));
    }

    [Fact]
    public async Task WithoutAForegroundWindowNothingChanges()
    {
        var w = new FakeWindow { Window = 7, Frame = false, NoFront = true };
        var checks = 0;
        // the game is in front, a secure desktop takes the foreground (lock screen, UAC), then the game is in front again: no background, no full screen again
        await Borderless.KeepAsync(w, 42, () => ++checks switch
        {
            1 => true,
            2 => !(w.NoFront = false),
            3 => w.NoFront = true,
            4 => !(w.NoFront = false),
            _ => false,
        }, TimeSpan.Zero);
        Assert.Equal((0, 0, 0), (w.Backgrounded, w.Fullscreened, w.Unframed));
    }
}
